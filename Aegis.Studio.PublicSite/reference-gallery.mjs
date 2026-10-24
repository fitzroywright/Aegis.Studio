// Reference website adapter: its own identity store, gallery database and files.
// All exchange with the API site uses authenticated HTTP, never API database/file access.
import { DatabaseSync } from 'node:sqlite';
import { createHash, randomBytes, randomUUID, scryptSync, timingSafeEqual } from 'node:crypto';
import { mkdirSync, existsSync, statSync, rmSync, openSync, closeSync, fsyncSync } from 'node:fs';
import { createReadStream, createWriteStream } from 'node:fs';
import { pipeline } from 'node:stream/promises';
import { dirname, resolve, join } from 'node:path';
import { uuid } from './remote-media-store.mjs';
import { manifestDigest } from './gallery-exchange-store.mjs';
import { ensureActivity, recordActivity } from './exchange-activity.mjs';
import { fail } from './intake-store.mjs';

const digest=x=>createHash('sha256').update(x).digest('hex');
export function createReferenceGallery(env,readBody,json){
  const database=resolve(env.REFERENCE_GALLERY_DB_PATH||join(dirname(env.INTAKE_DB_PATH||'./data/intake.sqlite'),'reference-gallery.sqlite'));
  if(database===resolve(env.INTAKE_DB_PATH||'./data/intake.sqlite'))throw new Error('Reference gallery storage must be separate from API storage.');
  mkdirSync(dirname(database),{recursive:true,mode:0o700});
  const db=new DatabaseSync(database),root=resolve(env.REFERENCE_GALLERY_MEDIA_PATH||join(dirname(database),'reference-gallery-media'));
  mkdirSync(root,{recursive:true,mode:0o700});ensureActivity(db);
  db.exec(`PRAGMA journal_mode=WAL;PRAGMA synchronous=FULL;PRAGMA busy_timeout=5000;
    CREATE TABLE IF NOT EXISTS clients(id TEXT PRIMARY KEY,email TEXT NOT NULL UNIQUE,name TEXT NOT NULL,salt TEXT NOT NULL,password_hash TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS client_sessions(token_hash TEXT PRIMARY KEY,client_id TEXT NOT NULL,expires_at TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS requests(id TEXT PRIMARY KEY,client_id TEXT NOT NULL,payload TEXT NOT NULL,submitted INTEGER NOT NULL DEFAULT 0,status TEXT NOT NULL DEFAULT 'Pending synchronization');
    CREATE TABLE IF NOT EXISTS imported_galleries(gallery_id TEXT PRIMARY KEY,request_id TEXT NOT NULL,version INTEGER NOT NULL,transfer_id TEXT NOT NULL,manifest TEXT NOT NULL,ack TEXT NOT NULL,state TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS imported_versions(transfer_id TEXT PRIMARY KEY,gallery_id TEXT NOT NULL,manifest TEXT NOT NULL,ack TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS received_transfers(id TEXT PRIMARY KEY,gallery_id TEXT NOT NULL,version INTEGER NOT NULL);
    CREATE TABLE IF NOT EXISTS received_revocations(id TEXT PRIMARY KEY,payload TEXT NOT NULL,state TEXT NOT NULL,ack TEXT);
    CREATE TABLE IF NOT EXISTS exchange_cursor(id INTEGER PRIMARY KEY CHECK(id=1),value INTEGER NOT NULL);
    INSERT INTO exchange_cursor VALUES(1,0) ON CONFLICT DO NOTHING;`);
  if(!db.prepare('PRAGMA table_info(requests)').all().some(x=>x.name==='status_reason'))db.exec("ALTER TABLE requests ADD COLUMN status_reason TEXT NOT NULL DEFAULT ''");
  if(!db.prepare('PRAGMA table_info(received_transfers)').all().some(x=>x.name==='version'))db.exec("ALTER TABLE received_transfers ADD COLUMN version INTEGER NOT NULL DEFAULT 0");
  let origin=null;
  if(env.REFERENCE_GALLERY_API_URL){origin=new URL(env.REFERENCE_GALLERY_API_URL);if(origin.pathname!=='/'||origin.username||origin.password||origin.protocol!=='https:'&&!(env.NODE_ENV!=='production'&&['localhost','127.0.0.1'].includes(origin.hostname)))throw new Error('Reference gallery API URL must be an HTTPS origin.');}
  const configured=()=>origin&&env.REFERENCE_GALLERY_API_KEY?.length>=32;
  async function remote(path,options={}){
    if(!configured())fail(503,'exchange_unconfigured','Gallery exchange is not configured.');
    const r=await fetch(new URL('/api/v1/'+path,origin),{redirect:'error',signal:AbortSignal.timeout(120000),...options,headers:{Authorization:'Bearer '+env.REFERENCE_GALLERY_API_KEY,...options.headers}});
    if(!r.ok){const e=new Error('Gallery API request failed.');e.status=r.status;throw e;}return r;
  }
  async function post(path,p){return (await remote(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(p)})).json();}
  function transaction(action){db.exec('BEGIN IMMEDIATE');try{const result=action();db.exec('COMMIT');return result;}catch(e){db.exec('ROLLBACK');throw e;}}
  function client(req){
    const token=String(req.headers.cookie||'').split(';').map(x=>x.trim()).find(x=>x.startsWith('bryans_client_session='))?.slice(22)||'';
    const row=db.prepare('SELECT c.id,c.name,c.email FROM client_sessions s JOIN clients c ON c.id=s.client_id WHERE s.token_hash=? AND s.expires_at>?').get(digest(token),new Date().toISOString());
    if(!row)fail(401,'client_signin_required','Sign in to request or view your galleries.');return row;
  }
  function csrf(req){
    let same=false;try{same=new URL(req.headers.origin).host===req.headers.host;}catch{}
    const token=String(req.headers.cookie||'').split(';').map(x=>x.trim()).find(x=>x.startsWith((env.REFERENCE_SITE_CSRF_COOKIE||'intake_csrf')+'='))?.slice((env.REFERENCE_SITE_CSRF_COOKIE||'intake_csrf').length+1)||'';
    if(!same||!token||token!==req.headers['x-csrf-token'])fail(403,'origin_rejected','Reload the page before submitting.');
  }
  async function input(req){let p;try{p=JSON.parse((await readBody(req,8192)).toString());}catch{fail(400,'invalid_json','Supply valid JSON.');}if(!p||Array.isArray(p)||typeof p!=='object')fail(400,'invalid_json','Supply a JSON object.');return p;}
  const rates=new Map();
  function throttle(req){const minute=Math.floor(Date.now()/60000),key=(req.socket.remoteAddress||'unknown')+':'+minute,n=(rates.get(key)||0)+1;rates.set(key,n);if(rates.size>1000)for(const k of rates.keys())if(!k.endsWith(':'+minute))rates.delete(k);if(n>10)fail(429,'rate_limited','Wait before trying again.');}
  const cookie=(token,age=28800)=>`bryans_client_session=${token}; Path=/; HttpOnly; SameSite=Lax; Max-Age=${age}${env.NODE_ENV==='production'?'; Secure':''}`;

  function cleanupOld(galleryId,currentVersion){
    for(const r of db.prepare('SELECT id FROM received_transfers WHERE gallery_id=? AND version<?').all(galleryId,currentVersion)){
      if(!uuid(r.id))throw new Error('Invalid retained transfer identity.');
      rmSync(join(root,r.id),{recursive:true,force:true});
    }
  }
  async function importTransfer(id){
    let manifest;
    try{manifest=await (await remote('gallery-transfers/'+id)).json();}catch(e){if(e.status===410)return;throw e;}
    if(!uuid(id)||manifest.id!==id||!uuid(manifest.galleryId)||!uuid(manifest.requestId)||!Array.isArray(manifest.items))throw new Error('Invalid transfer manifest.');
    const facts={...manifest};delete facts.digest;delete facts.state;delete facts.acknowledgment;delete facts.createdAt;
    if(manifestDigest(facts)!==manifest.digest)throw new Error('Transfer manifest digest mismatch.');
    const localRequest=db.prepare('SELECT client_id FROM requests WHERE id=?').get(manifest.requestId);
    if(!localRequest)throw new Error('Transfer has no originating website request.');
    const old=db.prepare('SELECT * FROM imported_galleries WHERE gallery_id=?').get(manifest.galleryId);
    if(old&&old.version>manifest.version)return;
    db.prepare('INSERT INTO received_transfers VALUES(?,?,?) ON CONFLICT DO NOTHING').run(id,manifest.galleryId,manifest.version);
    const prior=db.prepare('SELECT * FROM imported_versions WHERE transfer_id=?').get(id);
    if(prior){cleanupOld(manifest.galleryId,manifest.version);await post('gallery-transfers/'+id+'/ack',JSON.parse(prior.ack));return;}
    const dir=join(root,id);mkdirSync(dir,{recursive:true,mode:0o700});
    for(const file of manifest.items){
      if(!uuid(file.versionId)||!Number.isSafeInteger(file.sizeBytes)||file.sizeBytes<1||file.sizeBytes>100000000)throw new Error('Invalid approved file.');
      const path=join(dir,file.versionId);
      let offset=existsSync(path)?statSync(path).size:0;
      if(offset>file.sizeBytes){rmSync(path);offset=0;}
      if(offset<file.sizeBytes){
        const r=await remote('gallery-transfers/'+id+'/files/'+file.versionId,{headers:offset?{Range:'bytes='+offset+'-'}:{}});
        if(offset&&r.status!==206)throw new Error('API did not honor the resume range.');
        let count=offset;
        async function* bounded(source){for await(const chunk of source){count+=chunk.length;if(count>file.sizeBytes)throw new Error('Transfer exceeds manifest.');yield chunk;}}
        await pipeline(r.body,bounded,createWriteStream(path,{flags:offset?'a':'w',mode:0o600}));
      }
      const hash=createHash('sha256');for await(const chunk of createReadStream(path))hash.update(chunk);
      if(statSync(path).size!==file.sizeBytes||hash.digest('hex')!==file.sha256.toLowerCase()){rmSync(path);throw new Error('Gallery checksum mismatch; file will be retried.');}
      const fd=openSync(path,'r');try{fsyncSync(fd);}finally{closeSync(fd);}
    }
    if(process.platform!=='win32')for(const path of [dir,root]){const fd=openSync(path,'r');try{fsyncSync(fd);}finally{closeSync(fd);}}
    // The pointer changes only after all files are durable. Keep the exact acknowledgment for retries.
    const ack={state:'Imported',version:manifest.version,digest:manifest.digest,occurredAt:new Date().toISOString()};
    transaction(()=>{
      db.prepare('INSERT INTO imported_versions VALUES(?,?,?,?)').run(id,manifest.galleryId,JSON.stringify(manifest),JSON.stringify(ack));
      db.prepare("INSERT INTO imported_galleries VALUES(?,?,?,?,?,?,'Active') ON CONFLICT(gallery_id) DO UPDATE SET request_id=excluded.request_id,version=excluded.version,transfer_id=excluded.transfer_id,manifest=excluded.manifest,ack=excluded.ack,state='Active'")
        .run(manifest.galleryId,manifest.requestId,manifest.version,id,JSON.stringify(manifest),JSON.stringify(ack));
      recordActivity(db,localRequest.client_id,manifest.galleryId,'GalleryImported','Website',{transferId:id,version:manifest.version});
    });
    cleanupOld(manifest.galleryId,manifest.version);
    await post('gallery-transfers/'+id+'/ack',ack);
  }
  async function revoke(event){
    const previous=db.prepare('SELECT * FROM received_revocations WHERE id=?').get(event.revocationId);
    if(previous?.state==='Completed'){await post('gallery-revocations/'+event.revocationId+'/ack',JSON.parse(previous.ack));return;}
    transaction(()=>{
      db.prepare("INSERT INTO received_revocations VALUES(?,?,'Received',NULL) ON CONFLICT(id) DO NOTHING").run(event.revocationId,JSON.stringify(event));
      db.prepare("UPDATE imported_galleries SET state='Revoked' WHERE gallery_id=? AND version<=?").run(event.galleryId,event.throughVersion);
    });
    const scope={galleryId:event.galleryId,requestId:event.requestId,throughVersion:event.throughVersion,limitations:''};
    await post('gallery-revocations/'+event.revocationId+'/ack',{...scope,state:'Received',occurredAt:new Date().toISOString()});
    try{
      for(const version of db.prepare('SELECT id AS transfer_id FROM received_transfers WHERE gallery_id=? AND version<=?').all(event.galleryId,event.throughVersion)){
        if(!uuid(version.transfer_id))throw new Error('Invalid local transfer identity.');
        rmSync(join(root,version.transfer_id),{recursive:true,force:true});
      }
      const ack={...scope,state:'Completed',occurredAt:new Date().toISOString(),limitations:'Website active files removed. Previously downloaded client copies and any operator-managed backups are outside this deletion.'};
      transaction(()=>{db.prepare("UPDATE received_revocations SET state='Completed',ack=? WHERE id=?").run(JSON.stringify(ack),event.revocationId);
        recordActivity(db,'Website',event.galleryId,'GalleryRevocationCompleted','Website',{revocationId:event.revocationId});});
      await post('gallery-revocations/'+event.revocationId+'/ack',ack);
    }catch(error){
      await post('gallery-revocations/'+event.revocationId+'/ack',{...scope,state:'Failed',occurredAt:new Date().toISOString(),limitations:'Website cleanup or acknowledgment failed; follow-up required.'}).catch(()=>{});
      throw error;
    }
  }
  let busy=false,closed=false,timer=null;
  async function synchronize(){
    if(busy||closed||!configured())return;busy=true;
    try{
      for(const r of db.prepare('SELECT * FROM requests WHERE submitted=0 ORDER BY rowid LIMIT 20').all()){
        await post('gallery-requests',JSON.parse(r.payload));db.prepare('UPDATE requests SET submitted=1,status=? WHERE id=?').run('Pending staff review',r.id);
      }
      const after=db.prepare('SELECT value FROM exchange_cursor WHERE id=1').get().value;
      const feed=await (await remote('gallery-events?after='+after)).json();
      for(const event of feed.items){
        if(event.type==='TransferAvailable')await importTransfer(event.transferId);
        else if(event.type==='RevocationRequested')await revoke(event);
        else throw new Error('Unknown gallery event.');
        db.prepare('UPDATE exchange_cursor SET value=? WHERE id=1').run(event.sequence);
      }
      for(const r of db.prepare('SELECT id FROM requests WHERE submitted=1 ORDER BY rowid DESC LIMIT 100').all()){
        const p=await (await remote('gallery-requests/'+r.id)).json();db.prepare('UPDATE requests SET status=?,status_reason=? WHERE id=?').run(p.state,p.decision?.reason||'',r.id);
      }
    }catch(error){console.warn(JSON.stringify({event:'ReferenceGalleryExchangeRetry',failureType:error.name,status:error.status||null}));}
    finally{busy=false;}
  }
  return {
    start(){timer=setInterval(synchronize,15000);timer.unref();},
    async close(){closed=true;if(timer)clearInterval(timer);while(busy)await new Promise(r=>setTimeout(r,20));db.close();},
    async handle(req,res,url){
      const path=url.pathname,method=req.method;if(!path.startsWith('/api/client-access/'))return false;
      if(['POST','PUT','DELETE'].includes(method))csrf(req);
      if(['register','login'].some(x=>path==='/api/client-access/'+x)&&method==='POST'){
        throttle(req);const p=await input(req),email=String(p.email||'').trim().toLowerCase(),password=p.password;
        if(!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)||email.length>320||typeof password!=='string'||password.length<12||password.length>200)fail(400,'invalid_account','Supply an email and a password of 12–200 characters.');
        if(path.endsWith('/register')){
          if(typeof p.name!=='string'||!p.name.trim()||p.name.length>200)fail(400,'invalid_account','Supply your name.');
          const salt=randomBytes(16),hash=scryptSync(password,salt,64);
          if(db.prepare('SELECT 1 FROM clients WHERE email=?').get(email))fail(409,'account_exists','Account is unavailable. Sign in or contact the studio.');
          db.prepare('INSERT INTO clients VALUES(?,?,?,?,?)').run(randomUUID(),email,p.name.trim(),salt.toString('base64'),hash.toString('base64'));
        }
        const c=db.prepare('SELECT * FROM clients WHERE email=?').get(email),salt=c?Buffer.from(c.salt,'base64'):Buffer.alloc(16);
        const supplied=scryptSync(password,salt,64);if(!c||!timingSafeEqual(supplied,Buffer.from(c.password_hash,'base64')))fail(401,'unauthorized','Sign-in failed.');
        const token=randomBytes(32).toString('base64url');db.prepare('INSERT INTO client_sessions VALUES(?,?,?)').run(digest(token),c.id,new Date(Date.now()+8*3600000).toISOString());
        json(res,200,{name:c.name,email:c.email},{'Set-Cookie':cookie(token)});return true;
      }
      const c=client(req);
      if(path==='/api/client-access/session'&&method==='GET'){json(res,200,c);return true;}
      if(path==='/api/client-access/logout'&&method==='POST'){
        db.prepare('DELETE FROM client_sessions WHERE client_id=?').run(c.id);json(res,200,{signedOut:true},{'Set-Cookie':cookie('',0)});return true;
      }
      if(path==='/api/client-access/requests'&&method==='POST'){
        if(!configured())fail(503,'exchange_unconfigured','Gallery request exchange is not configured.');
        const p=await input(req);if(!uuid(p.id)||typeof p.bookingReference!=='string'||p.bookingReference.length>100||typeof p.sessionDate!=='string'||p.sessionDate.length>40)fail(400,'invalid_request','Supply a request ID, booking reference and session date.');
        const payload={id:p.id,externalReference:p.id,requesterReference:c.id,hints:{name:c.name,email:c.email,bookingReference:p.bookingReference,sessionDate:p.sessionDate}};
        transaction(()=>{const old=db.prepare('SELECT * FROM requests WHERE id=?').get(p.id);if(old){if(old.client_id!==c.id||old.payload!==JSON.stringify(payload))fail(409,'request_conflict','Request ID belongs to different data.');return;}
          db.prepare('INSERT INTO requests(id,client_id,payload) VALUES(?,?,?)').run(p.id,c.id,JSON.stringify(payload));recordActivity(db,c.id,p.id,'GalleryRequested',c.id);});
        json(res,202,{id:p.id,state:'Pending synchronization'});return true;
      }
      if(path==='/api/client-access/requests'&&method==='GET'){json(res,200,{items:db.prepare('SELECT id,status,status_reason AS reason FROM requests WHERE client_id=? ORDER BY rowid DESC').all(c.id)});return true;}
      if(path==='/api/client-access/galleries'&&method==='GET'){
        json(res,200,{items:db.prepare("SELECT g.* FROM imported_galleries g JOIN requests r ON r.id=g.request_id WHERE r.client_id=? AND g.state='Active'").all(c.id).map(r=>JSON.parse(r.manifest))});return true;
      }
      const file=path.match(/^\/api\/client-access\/galleries\/([0-9a-f-]{36})\/files\/([0-9a-f-]{36})$/i);
      if(file&&method==='GET'){
        const g=db.prepare("SELECT g.* FROM imported_galleries g JOIN requests r ON r.id=g.request_id WHERE g.gallery_id=? AND r.client_id=? AND g.state='Active'").get(file[1],c.id);
        const item=g&&JSON.parse(g.manifest).items.find(x=>x.versionId===file[2]);if(!item)fail(404,'not_found','Gallery file not found.');
        if(!existsSync(join(root,g.transfer_id,item.versionId)))fail(410,'file_unavailable','Gallery file is unavailable; contact the studio.');
        res.writeHead(200,{'Content-Type':item.contentType,'Cache-Control':'private, no-store','Content-Disposition':'inline','X-Content-Type-Options':'nosniff'});
        await pipeline(createReadStream(join(root,g.transfer_id,item.versionId)),res);return true;
      }
      fail(404,'not_found','Client gallery operation not found.');
    }
  };
}
