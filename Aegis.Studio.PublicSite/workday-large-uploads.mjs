import {createHash,randomUUID} from 'node:crypto';
import {constants as C} from 'node:fs';
import {mkdir,open,rename,unlink,stat,lstat,rm,mkdtemp} from 'node:fs/promises';
import {createReadStream} from 'node:fs';
import {dirname,resolve,join} from 'node:path';
import {tmpdir} from 'node:os';
import {fileURLToPath} from 'node:url';
import {spawn} from 'node:child_process';
const fail=(status,code,message)=>{throw Object.assign(new Error(message),{status,code});};
const uuid=x=>typeof x==='string'&&/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(x);
const sha=x=>typeof x==='string'&&/^[a-f0-9]{64}$/i.test(x);
const extensions=/\.(jpe?g|png|webp|tiff?|heic|heif|cr2|cr3|nef|nrw|arw|raf|orf|rw2|dng)$/i;
const number=(value,fallback,min,max)=>{const n=Number(value??fallback);if(!Number.isSafeInteger(n)||n<min||n>max)throw Error('Invalid WorkDay upload limit.');return n;};
// Decoder children receive runtime paths only, never application credentials.
const decoderEnvironment=()=>Object.fromEntries(Object.entries({PATH:process.env.PATH,TMPDIR:process.env.TMPDIR,SystemRoot:process.env.SystemRoot,
  LANG:process.env.LANG||'C.UTF-8',OMP_NUM_THREADS:'1',OPENBLAS_NUM_THREADS:'1'}).filter(([,value])=>value!==undefined));
export class WorkDayLargeUploads {
  constructor(env,store,now=()=>Date.now()){
    this.env=env;this.store=store;this.db=store.db;this.now=now;this.locks=new Map();this.active=0;
    this.chunkBytes=4*1024*1024;
    this.maxBytes=number(env.WORKDAY_MAX_UPLOAD_BYTES,512*1024*1024,1,512*1024*1024);
    this.quota=number(env.WORKDAY_UPLOAD_QUOTA_BYTES,10*1024*1024*1024,this.maxBytes,Number.MAX_SAFE_INTEGER);
    this.accountQuota=number(env.WORKDAY_ACCOUNT_QUOTA_BYTES,4*1024*1024*1024,this.maxBytes,this.quota);
    this.concurrency=number(env.WORKDAY_UPLOAD_CONCURRENCY,2,1,8);
    this.grace=number(env.WORKDAY_ACK_GRACE_SECONDS,604800,env.NODE_ENV==='production'?3600:0,31536000)*1000;
    this.temporary=env.INTAKE_DB_PATH===':memory:'&&!env.WORKDAY_UPLOAD_ROOT;
    this.root=resolve(env.WORKDAY_UPLOAD_ROOT||join(dirname(env.WORKDAY_EXCHANGE_DB_PATH||env.INTAKE_DB_PATH||'./data/intake.sqlite'),'workday-originals'));
    const publicRoot=fileURLToPath(new URL('./public/',import.meta.url));
    if(this.root===publicRoot||this.root.startsWith(publicRoot+'/'))throw Error('WorkDay originals must be outside the public asset directory.');
    this.db.exec(`CREATE TABLE IF NOT EXISTS workday_upload_sessions(
      id TEXT PRIMARY KEY,user_key TEXT NOT NULL,assignment_id TEXT NOT NULL,metadata TEXT NOT NULL,
      size_bytes INTEGER NOT NULL,sha256 TEXT NOT NULL,state TEXT NOT NULL,format TEXT,preview_state TEXT NOT NULL DEFAULT 'Pending',
      created_at INTEGER NOT NULL,completed_at INTEGER,acknowledged_at INTEGER,purge_after INTEGER);
      CREATE TABLE IF NOT EXISTS workday_upload_chunks(session_id TEXT NOT NULL,chunk_index INTEGER NOT NULL,size_bytes INTEGER NOT NULL,sha256 TEXT NOT NULL,PRIMARY KEY(session_id,chunk_index));`);
    this.initialized=null;
    this.timer=setInterval(()=>{this.purge().catch(()=>{});},900000);this.timer.unref();
  }
  async init(){
    if(!this.initialized)this.initialized=(async()=>{
      if(this.temporary)this.root=await mkdtemp(join(tmpdir(),'workday-originals-'));
      await mkdir(this.root,{recursive:true,mode:0o700});
      if((await lstat(this.root)).isSymbolicLink())throw Error('Original storage cannot be a symbolic link.');
    })();
    return this.initialized;
  }
  async locked(id,operation){
    const previous=this.locks.get(id)||Promise.resolve();let release;const next=new Promise(r=>release=r);this.locks.set(id,next);
    await previous;try{return await operation();}finally{release();if(this.locks.get(id)===next)this.locks.delete(id);}
  }
  async bounded(operation){
    if(this.active>=this.concurrency)fail(503,'upload_busy','Upload processing is busy. Retry the same operation shortly.');
    this.active++;try{return await operation();}finally{this.active--;}
  }
  folder(id){if(!uuid(id))fail(400,'invalid_upload','Invalid upload identity.');return join(this.root,id.toLowerCase());}
  row(user,assignment,id){
    const r=this.db.prepare('SELECT * FROM workday_upload_sessions WHERE id=? AND user_key=? AND assignment_id=?').get(id.toLowerCase(),user.toLowerCase(),assignment.toLowerCase());
    if(!r)fail(404,'upload_not_found','Upload session not found.');return r;
  }
  status(user,assignment,id){
    const r=this.row(user,assignment,id);const chunks=this.db.prepare('SELECT chunk_index AS chunkIndex,size_bytes AS sizeBytes,sha256 FROM workday_upload_chunks WHERE session_id=? ORDER BY chunk_index').all(r.id);
    return {uploadId:r.id,state:r.state,chunkBytes:this.chunkBytes,sizeBytes:r.size_bytes,sha256:r.sha256,receivedChunks:chunks,
      metadata:{...JSON.parse(r.metadata),uploader:r.user_key,createdAtUtc:new Date(r.created_at).toISOString(),completedAtUtc:r.completed_at?new Date(r.completed_at).toISOString():null,acknowledgedAtUtc:r.acknowledged_at?new Date(r.acknowledged_at).toISOString():null},previewState:r.preview_state,format:r.format?JSON.parse(r.format):null};
  }
  async create(user,assignment,input,context={}){
    if(!uuid(input?.uploadId)||!Number.isSafeInteger(input.sizeBytes)||input.sizeBytes<1||input.sizeBytes>this.maxBytes||!sha(input.sha256)||
       typeof input.fileName!=='string'||input.fileName.length>255||/[\\/\x00-\x1f\x7f]/.test(input.fileName)||!extensions.test(input.fileName))
      fail(400,'invalid_upload','Supply a stable upload ID, supported photograph name, byte size and SHA-256 within the configured limit.');
    if(input.subjectId&&!uuid(input.subjectId))fail(400,'invalid_subject','Invalid subject identity.');
    if(input.subjectId&&!(context.subjectIds||[]).some(x=>x.toLowerCase()===input.subjectId.toLowerCase()))fail(403,'subject_forbidden','This subject is not in the assignment.');
    const id=input.uploadId.toLowerCase();
    const metadata={assignmentId:assignment.toLowerCase(),uploadId:id,fileName:input.fileName,sizeBytes:input.sizeBytes,sha256:input.sha256.toLowerCase(),
      jobId:context.jobId||null,sessionId:context.sessionId||null,subjectId:input.subjectId||null};
    await this.init();
    return this.locked(id,async()=>{
      const old=this.db.prepare('SELECT user_key,metadata,state FROM workday_upload_sessions WHERE id=?').get(id);
      if(old){if(old.user_key!==user.toLowerCase()||old.metadata!==JSON.stringify(metadata))fail(409,'upload_identity_conflict','This upload ID belongs to different data or another account.');if(old.state==='Receiving')await mkdir(this.folder(id),{recursive:true,mode:0o700});return this.status(user,assignment,id);}
      const total=this.db.prepare("SELECT coalesce(sum(size_bytes),0) AS n FROM workday_upload_sessions WHERE state NOT IN ('Cancelled','Purged')").get().n;
      const own=this.db.prepare("SELECT coalesce(sum(size_bytes),0) AS n FROM workday_upload_sessions WHERE user_key=? AND state NOT IN ('Cancelled','Purged')").get(user.toLowerCase()).n;
      if(total+input.sizeBytes>this.quota||own+input.sizeBytes>this.accountQuota)fail(507,'upload_quota','Private upload storage quota exceeded. Unacknowledged originals will not be removed.');
      this.db.prepare('INSERT INTO workday_upload_sessions(id,user_key,assignment_id,metadata,size_bytes,sha256,state,created_at) VALUES(?,?,?,?,?,?,?,?)')
        .run(id,user.toLowerCase(),assignment.toLowerCase(),JSON.stringify(metadata),input.sizeBytes,metadata.sha256,'Receiving',this.now());
      await mkdir(this.folder(id),{recursive:true,mode:0o700});
      return this.status(user,assignment,id);
    });
  }
  async writeChunk(user,assignment,id,index,checksum,source){
    if(!Number.isSafeInteger(index)||index<0||!sha(checksum))fail(400,'invalid_chunk','Chunk index and SHA-256 are required.');
    await this.init();
    return this.bounded(()=>this.locked(id,async()=>{
      const row=this.row(user,assignment,id);
      if(row.state!=='Receiving')fail(409,'upload_not_receiving','This upload is not receiving chunks.');
      const expected=Math.min(this.chunkBytes,row.size_bytes-index*this.chunkBytes);
      if(expected<=0)fail(400,'invalid_chunk','Chunk lies outside the declared file.');
      const folder=this.folder(id),temp=join(folder,'chunk-'+index+'-'+randomUUID()+'.part'),target=join(folder,'chunk-'+index);
      let bytes=0;const hash=createHash('sha256');let handle;
      try{
        handle=await open(temp,C.O_WRONLY|C.O_CREAT|C.O_EXCL|C.O_NOFOLLOW,0o600);
        for await(const chunk of source){
          bytes+=chunk.length;if(bytes>expected)fail(413,'chunk_too_large','Chunk exceeds declared bounds.');
          hash.update(chunk);await handle.writeFile(chunk);
        }
        await handle.sync();await handle.close();handle=null;
        const actual=hash.digest('hex');
        if(bytes!==expected||actual!==checksum.toLowerCase())fail(422,'chunk_checksum','Chunk size or checksum does not match. Retry the same chunk.');
        const old=this.db.prepare('SELECT sha256,size_bytes FROM workday_upload_chunks WHERE session_id=? AND chunk_index=?').get(row.id,index);
        if(old){if(old.sha256!==actual||old.size_bytes!==bytes)fail(409,'chunk_conflict','This chunk index already contains different bytes.');await unlink(temp);}
        else{
          await rename(temp,target);
          const dir=await open(folder,C.O_RDONLY);try{await dir.sync();}finally{await dir.close();}
          this.db.prepare('INSERT INTO workday_upload_chunks VALUES(?,?,?,?)').run(row.id,index,bytes,actual);
        }
        return {chunkIndex:index,sizeBytes:bytes,sha256:actual};
      }finally{if(handle)await handle.close().catch(()=>{});await unlink(temp).catch(()=>{});}
    }));
  }
  async runHelper(helper,args,timeout=90000){
    const script=this.env[helper==='probe'?'WORKDAY_PHOTO_PROBE_PATH':'WORKDAY_RAW_PREVIEW_HELPER_PATH']||
      fileURLToPath(new URL('../Aegis.Studio.Web/Media/'+(helper==='probe'?'photo_probe.py':'raw_preview.py'),import.meta.url));
    return new Promise((resolve,reject)=>{
      const child=spawn(this.env.WORKDAY_RAW_PYTHON||'python3',[script,...args],{shell:false,stdio:['ignore','pipe','ignore'],env:decoderEnvironment()});
      let text='',done=false;const timer=setTimeout(()=>{child.kill('SIGKILL');finish(Error('Photo processing timed out.'));},timeout);
      const finish=(error,value)=>{if(done)return;done=true;clearTimeout(timer);error?reject(error):resolve(value);};
      child.stdout.on('data',chunk=>{text+=chunk;if(text.length>65536){child.kill('SIGKILL');finish(Error('Excessive photo metadata.'));}});
      child.on('error',error=>finish(error));child.on('exit',code=>code===0?finish(null,text):finish(Object.assign(Error('Photo container or decoder unavailable.'),{exitCode:code})));
    });
  }
  async complete(user,assignment,id){
    await this.init();return this.bounded(()=>this.locked(id,async()=>{
      let row=this.row(user,assignment,id);
      if(['Cancelled','Purged'].includes(row.state))fail(409,'upload_unavailable','This upload cannot be completed.');
      if(row.state==='Receiving'){
        const chunks=this.db.prepare('SELECT * FROM workday_upload_chunks WHERE session_id=? ORDER BY chunk_index').all(row.id);
        if(chunks.length!==Math.ceil(row.size_bytes/this.chunkBytes)||chunks.some((c,i)=>c.chunk_index!==i))fail(409,'upload_incomplete','Not all chunks have been durably received.');
        const temp=join(this.folder(id),'original.tmp'),final=join(this.folder(id),'original');
        let output;const hash=createHash('sha256');let size=0;
        try{
          output=await open(temp,C.O_WRONLY|C.O_CREAT|C.O_TRUNC|C.O_NOFOLLOW,0o600);
          for(const chunk of chunks){
            const input=await open(join(this.folder(id),'chunk-'+chunk.chunk_index),C.O_RDONLY|C.O_NOFOLLOW);
            try{for await(const bytes of input.createReadStream({autoClose:false})){size+=bytes.length;hash.update(bytes);await output.writeFile(bytes);}}finally{await input.close();}
          }
          await output.sync();await output.close();output=null;
          if(size!==row.size_bytes||hash.digest('hex')!==row.sha256)fail(422,'file_checksum','Original checksum mismatch. Original has not been queued or acknowledged.');
          let format;
          try{format=JSON.parse(await this.runHelper('probe',[temp,JSON.parse(row.metadata).fileName],15000));}
          catch(error){if(error.exitCode===2)fail(415,'invalid_photo','The photograph contents do not match a supported photo container.');fail(503,'photo_probe_unavailable','Photograph validation runtime is unavailable; received chunks are retained.');}
          await rename(temp,final);
          const directory=await open(this.folder(id),C.O_RDONLY);try{await directory.sync();}finally{await directory.close();}
          this.db.prepare("UPDATE workday_upload_sessions SET state='Ready',completed_at=?,format=? WHERE id=?").run(this.now(),JSON.stringify(format),row.id);
          row=this.row(user,assignment,id);
        }finally{if(output)await output.close().catch(()=>{});await unlink(temp).catch(()=>{});}
      }
      // Also recover a crash after the durable Ready transition but before chunk cleanup/queueing.
      if((await stat(join(this.folder(id),'original'))).size!==row.size_bytes)fail(503,'original_integrity','Private original needs integrity review; acknowledgment withheld.');
      const retainedChunks=this.db.prepare('SELECT chunk_index FROM workday_upload_chunks WHERE session_id=?').all(row.id);
      for(const chunk of retainedChunks)await unlink(join(this.folder(id),'chunk-'+chunk.chunk_index)).catch(error=>{if(error.code!=='ENOENT')throw error;});
      this.db.prepare('DELETE FROM workday_upload_chunks WHERE session_id=?').run(row.id);
      const format=JSON.parse(row.format);
      this.store.enqueue(user,row.id,'AssignmentUpload',{...JSON.parse(row.metadata),contentType:format.contentType,format:format.format,largeUpload:true});
      return {uploadId:row.id,state:row.state,storageStatus:row.state==='Acknowledged'?'Acknowledged':'Pending',sizeBytes:row.size_bytes,sha256:row.sha256,previewState:row.preview_state};
    }));
  }
  async cancel(user,assignment,id){
    return this.locked(id,async()=>{
      const row=this.row(user,assignment,id);if(row.state!=='Receiving'&&row.state!=='Cancelled')fail(409,'original_retained','A completed original is retained until verified Studio acknowledgment and retention expiry.');
      this.db.prepare("UPDATE workday_upload_sessions SET state='Cancelled' WHERE id=?").run(row.id);await rm(this.folder(id),{recursive:true,force:true});return {uploadId:row.id,state:'Cancelled'};
    });
  }
  async original(id){
    await this.init();const row=this.db.prepare('SELECT * FROM workday_upload_sessions WHERE id=?').get(id.toLowerCase());
    if(!row||!['Ready','Acknowledged'].includes(row.state))fail(404,'original_unavailable','Private original is unavailable.');
    const handle=await open(join(this.folder(id),'original'),C.O_RDONLY|C.O_NOFOLLOW);
    if((await handle.stat()).size!==row.size_bytes){await handle.close();fail(503,'original_integrity','Private original size verification failed.');}
    return {stream:handle.createReadStream(),sizeBytes:row.size_bytes,sha256:row.sha256};
  }
  acknowledge(id,result){
    const row=this.db.prepare('SELECT * FROM workday_upload_sessions WHERE id=?').get(id.toLowerCase());if(!row)return;
    if(result?.state==='Conflict')return;
    const receipt=result?.result;
    if(result?.state!=='Acknowledged'||!receipt||receipt.uploadId?.toLowerCase()!==row.id||receipt.assignmentId?.toLowerCase()!==row.assignment_id||
       receipt.sha256?.toLowerCase()!==row.sha256||receipt.sizeBytes!==row.size_bytes||receipt.storageStatus!=='Stored'||!uuid(receipt.attachmentId)||!uuid(receipt.versionId))
      fail(409,'unverified_ack','A durable Studio photo receipt with matching identity, byte size and SHA-256 is required.');
    if(!['Ready','Acknowledged','Purged'].includes(row.state))fail(409,'invalid_ack','Original is not ready for acknowledgment.');
    if(row.state==='Ready')this.db.prepare("UPDATE workday_upload_sessions SET state='Acknowledged',acknowledged_at=?,purge_after=? WHERE id=?").run(this.now(),this.now()+this.grace,row.id);
  }
  async preview(user,assignment,id){
    await this.init();return this.bounded(()=>this.locked(id,async()=>{
      const row=this.row(user,assignment,id),path=join(this.folder(id),'thumbnail.jpg');
      if(row.preview_state==='Ready')return {stream:createReadStream(path),sizeBytes:(await stat(path)).size};
      if(!['Ready','Acknowledged'].includes(row.state))fail(422,'preview_unavailable','Original retained, but its preview is unavailable.');
      try{
        const format=JSON.parse(row.format),original=join(this.folder(id),'original');
        if(format.raw)await this.runHelper('preview',[original,path]);
        else{
          const process=spawn(globalThis.process.execPath,['--max-old-space-size=256',fileURLToPath(new URL('./workday-preview-worker.mjs',import.meta.url)),original,path],{stdio:'ignore',env:decoderEnvironment()});
          await new Promise((resolve,reject)=>{const timer=setTimeout(()=>{process.kill('SIGKILL');reject(Error('Preview timeout'));},30000);process.once('error',e=>{clearTimeout(timer);reject(e);});process.once('exit',c=>{clearTimeout(timer);c===0?resolve():reject(Error('Preview unavailable'));});});
        }
        const size=(await stat(path)).size;if(size<1||size>20_000_000)throw Error('Preview bounds');
        this.db.prepare("UPDATE workday_upload_sessions SET preview_state='Ready' WHERE id=?").run(row.id);
        return {stream:createReadStream(path),sizeBytes:size};
      }catch{
        await unlink(path).catch(()=>{});this.db.prepare("UPDATE workday_upload_sessions SET preview_state='Unavailable' WHERE id=?").run(row.id);
        fail(422,'preview_unavailable','Preview unavailable. The private original remains retained for Studio.');
      }
    }));
  }
  async purge(){
    if(!this.db.isOpen)return {purged:0};await this.init();let purged=0;
    const rows=this.db.prepare("SELECT id FROM workday_upload_sessions WHERE state='Acknowledged' AND purge_after<=?").all(this.now());
    for(const {id} of rows)await this.locked(id,async()=>{
      const row=this.db.prepare('SELECT state,purge_after FROM workday_upload_sessions WHERE id=?').get(id);
      if(row.state!=='Acknowledged'||row.purge_after>this.now())return;
      await unlink(join(this.folder(id),'original')).catch(error=>{if(error.code!=='ENOENT')throw error;});
      this.db.prepare("UPDATE workday_upload_sessions SET state='Purged' WHERE id=?").run(id);purged++;
    });
    return {purged};
  }
  close(){clearInterval(this.timer);}
}
