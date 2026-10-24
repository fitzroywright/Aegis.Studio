import { mkdirSync, existsSync, openSync, closeSync, writeSync, fsyncSync, ftruncateSync, readSync, unlinkSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { createHash } from 'node:crypto';
import { ensureActivity, recordActivity, activityFor, canonicalJson } from './exchange-activity.mjs';
import { fail } from './intake-store.mjs';

export const uuid = value => typeof value === 'string' && /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/.test(value);
const sha = value => typeof value === 'string' && /^[0-9a-f]{64}$/i.test(value);

export class RemoteMediaStore {
  constructor(store, env = {}, now = () => new Date()) {
    this.store = store; this.db = store.db; this.now = now;
    this.root = resolve(env.REMOTE_MEDIA_PATH || './data/remote-media');
    this.maxBytes = Math.min(100_000_000, Math.max(1, Number(env.REMOTE_MEDIA_MAX_FILE_BYTES) || 100_000_000));
    this.userQuota = Math.max(this.maxBytes, Number(env.REMOTE_MEDIA_USER_QUOTA_BYTES) || 10_000_000_000);
    this.totalQuota = Math.max(this.userQuota, Number(env.REMOTE_MEDIA_TOTAL_QUOTA_BYTES) || 100_000_000_000);
    mkdirSync(this.root, { recursive: true, mode: 0o700 });
    ensureActivity(this.db);
    this.db.exec(`CREATE TABLE IF NOT EXISTS remote_media(
      id TEXT PRIMARY KEY,batch_id TEXT NOT NULL,owner TEXT NOT NULL,destination TEXT NOT NULL,
      file_name TEXT NOT NULL,content_type TEXT NOT NULL,size_bytes INTEGER NOT NULL,sha256 TEXT NOT NULL,
      received_bytes INTEGER NOT NULL DEFAULT 0,state TEXT NOT NULL DEFAULT 'Uploading',
      result TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,expires_at TEXT NOT NULL);
      CREATE INDEX IF NOT EXISTS ix_remote_media_batch ON remote_media(batch_id);
      CREATE INDEX IF NOT EXISTS ix_remote_media_owner ON remote_media(owner,created_at);
      CREATE INDEX IF NOT EXISTS ix_remote_media_ready ON remote_media(state,created_at);
      CREATE TABLE IF NOT EXISTS remote_media_chunks(upload_id TEXT NOT NULL,offset INTEGER NOT NULL,
        size_bytes INTEGER NOT NULL,sha256 TEXT NOT NULL,PRIMARY KEY(upload_id,offset));
      CREATE TABLE IF NOT EXISTS remote_media_retries(id TEXT PRIMARY KEY,upload_id TEXT NOT NULL,payload TEXT NOT NULL);`);
  }
  path(id) { if (!uuid(id)) fail(400, 'invalid_upload', 'A stable upload ID is required.'); return join(this.root, id + '.bin'); }
  get(id, owner = null) {
    const r = this.db.prepare('SELECT * FROM remote_media WHERE id=?').get(id);
    if (!r || owner !== null && r.owner !== owner) fail(404, 'not_found', 'Upload not found.');
    return r;
  }
  view(r) {
    const counts=this.db.prepare("SELECT count(*) AS total,sum(CASE WHEN state IN ('Imported','Purged') THEN 1 ELSE 0 END) AS imported FROM remote_media WHERE batch_id=?").get(r.batch_id);
    return { batchFileCount:counts.total,batchImportedCount:counts.imported||0,uploadId:r.id,batchId:r.batch_id,userName:r.owner,destination:JSON.parse(r.destination),
      fileName:r.file_name,contentType:r.content_type,sizeBytes:r.size_bytes,sha256:r.sha256,
      receivedBytes:r.received_bytes,state:r.state,result:r.result?JSON.parse(r.result):null,
      createdAt:r.created_at,updatedAt:r.updated_at,expiresAt:r.expires_at,chunkBytes:4_000_000 };
  }
  create(owner, p) {
    if (!uuid(p.batchId) || !uuid(p.assignmentId) || !uuid(p.jobId) || !Array.isArray(p.files) || p.files.length < 1 || p.files.length > 1000 ||
        typeof p.collection !== 'string' || !p.collection.trim() || p.collection.length > 100 || p.subjectId != null && !uuid(p.subjectId))
      fail(400, 'invalid_batch', 'Choose an assignment, job and collection, with up to 1,000 files.');
    const destination = JSON.stringify({ assignmentId:p.assignmentId,jobId:p.jobId,sessionId:p.sessionId||null,collection:p.collection,subjectId:p.subjectId||null });
    const ids = new Set();
    return this.store.transaction(() => {
      const existingBatch = this.db.prepare('SELECT id FROM remote_media WHERE batch_id=?').all(p.batchId).map(x=>x.id).sort();
      if (existingBatch.length && JSON.stringify(existingBatch) !== JSON.stringify(p.files.map(x=>x.uploadId).sort()))
        fail(409, 'batch_conflict', 'The batch ID belongs to a different file list.');
      const results = [];
      for (const f of p.files) {
        if (!uuid(f.uploadId) || ids.has(f.uploadId) || typeof f.fileName !== 'string' || !f.fileName || f.fileName.length>255 || /[\\/\x00-\x1f]/.test(f.fileName) ||
            !Number.isSafeInteger(f.sizeBytes) || f.sizeBytes<1 || f.sizeBytes>this.maxBytes || !sha(f.sha256) ||
            !['image/jpeg','image/png','image/webp','image/tiff'].includes(f.contentType))
          fail(400, 'invalid_file', 'Files need unique IDs, a safe filename, supported image type, size and SHA-256 checksum.');
        ids.add(f.uploadId);
        const old = this.db.prepare('SELECT * FROM remote_media WHERE id=?').get(f.uploadId);
        if (old) {
          if (old.owner!==owner || old.batch_id!==p.batchId || old.destination!==destination || old.file_name!==f.fileName ||
              old.size_bytes!==f.sizeBytes || old.sha256!==f.sha256.toLowerCase() || old.content_type!==f.contentType)
            fail(409, 'upload_conflict', 'An upload ID cannot be reused for another file or destination.');
          results.push(this.view(old)); continue;
        }
        const used = this.db.prepare("SELECT COALESCE(sum(size_bytes),0) AS total,COALESCE(sum(CASE WHEN owner=? THEN size_bytes ELSE 0 END),0) AS own FROM remote_media WHERE state NOT IN ('Purged','Expired')").get(owner);
        if (used.own+f.sizeBytes>this.userQuota || used.total+f.sizeBytes>this.totalQuota) fail(413,'quota_exceeded','Temporary media storage quota reached.');
        const now=this.now(),expires=new Date(now.getTime()+7*86400_000).toISOString();
        this.db.prepare('INSERT INTO remote_media(id,batch_id,owner,destination,file_name,content_type,size_bytes,sha256,created_at,updated_at,expires_at) VALUES(?,?,?,?,?,?,?,?,?,?,?)')
          .run(f.uploadId,p.batchId,owner,destination,f.fileName,f.contentType,f.sizeBytes,f.sha256.toLowerCase(),now.toISOString(),now.toISOString(),expires);
        recordActivity(this.db,owner,f.uploadId,'UploadDeclared',owner,{batchId:p.batchId,jobId:p.jobId,sizeBytes:f.sizeBytes});
        results.push(this.view(this.get(f.uploadId,owner)));
      }
      return {batchId:p.batchId,files:results};
    });
  }
  chunk(owner,id,offset,bytes,checksum) {
    const r=this.get(id,owner);
    if (r.state!=='Uploading' || Date.parse(r.expires_at)<=this.now().getTime()) fail(409,'upload_unavailable','Upload is no longer open.');
    if (!Number.isSafeInteger(offset)||offset<0||!bytes.length||bytes.length>4_000_000||offset+bytes.length>r.size_bytes||!sha(checksum))
      fail(400,'invalid_chunk','A valid offset, chunk and checksum are required.');
    const actual=createHash('sha256').update(bytes).digest('hex');
    if(actual!==checksum.toLowerCase())fail(422,'checksum_mismatch','Chunk checksum mismatch.');
    const prior=this.db.prepare('SELECT * FROM remote_media_chunks WHERE upload_id=? AND offset=?').get(id,offset);
    if(prior){if(prior.sha256!==actual||prior.size_bytes!==bytes.length)fail(409,'chunk_conflict','Chunk bytes changed.');return this.view(r);}
    if(offset!==r.received_bytes)fail(409,'offset_conflict','Resume at the recorded receivedBytes offset.');
    const path=this.path(id),fd=openSync(path,existsSync(path)?'r+':'w+',0o600);
    try {
      // Remove an uncommitted tail after a crash; persisted offsets are authoritative.
      ftruncateSync(fd,r.received_bytes);
      let written=0;while(written<bytes.length)written+=writeSync(fd,bytes,written,bytes.length-written,offset+written);
      fsyncSync(fd);
      if(process.platform!=='win32'){const dir=openSync(this.root,'r');try{fsyncSync(dir);}finally{closeSync(dir);}}
    } finally { closeSync(fd); }
    this.store.transaction(()=>{
      this.db.prepare('INSERT INTO remote_media_chunks VALUES(?,?,?,?)').run(id,offset,bytes.length,actual);
      this.db.prepare('UPDATE remote_media SET received_bytes=?,updated_at=? WHERE id=?').run(offset+bytes.length,this.now().toISOString(),id);
      recordActivity(this.db,owner,id,'ChunkReceived',owner,{offset,bytes:bytes.length});
    });
    return this.view(this.get(id,owner));
  }
  async complete(owner,id,scanner) {
    const r=this.get(id,owner);
    if(['ReceivedByApi','Imported','NeedsReview','Purged'].includes(r.state))return this.view(r);
    if(!['Uploading','Quarantined'].includes(r.state)||Date.parse(r.expires_at)<=this.now().getTime()||r.received_bytes!==r.size_bytes)
      fail(409,'upload_incomplete','Upload all bytes before completing.');
    const fd=openSync(this.path(id),'r'),hash=createHash('sha256'),buffer=Buffer.alloc(1024*1024);let head=Buffer.alloc(0);
    try {let n;while((n=readSync(fd,buffer,0,buffer.length,null))>0){if(!head.length)head=Buffer.from(buffer.subarray(0,Math.min(n,16)));hash.update(buffer.subarray(0,n));}}
    finally{closeSync(fd);}
    if(hash.digest('hex')!==r.sha256)fail(422,'checksum_mismatch','File checksum mismatch. Cancel and start a new upload.');
    const jpg=head[0]===255&&head[1]===216&&head[2]===255,png=head.subarray(0,8).equals(Buffer.from([137,80,78,71,13,10,26,10]));
    const webp=head.subarray(0,4).toString()==='RIFF'&&head.subarray(8,12).toString()==='WEBP';
    const tiff=head.subarray(0,4).equals(Buffer.from([73,73,42,0]))||head.subarray(0,4).equals(Buffer.from([77,77,0,42]));
    const ext=r.file_name.split('.').pop().toLowerCase();
    if(!(jpg&&r.content_type==='image/jpeg'&&['jpg','jpeg'].includes(ext)||png&&r.content_type==='image/png'&&ext==='png'||
         webp&&r.content_type==='image/webp'&&ext==='webp'||tiff&&r.content_type==='image/tiff'&&['tif','tiff'].includes(ext)))
      fail(422,'invalid_media','Image signature, extension and declared content type must agree.');
    const clean=await scanner(this.path(id));
    return this.store.transaction(()=>{
      // A cancellation during scanning must not be undone.
      const current=this.get(id,owner);if(!['Uploading','Quarantined'].includes(current.state))return this.view(current);
      const state=clean?'ReceivedByApi':'Quarantined';
      this.db.prepare('UPDATE remote_media SET state=?,updated_at=? WHERE id=?').run(state,this.now().toISOString(),id);
      recordActivity(this.db,owner,id,state,owner,{batchId:r.batch_id,sizeBytes:r.size_bytes});
      console.info(JSON.stringify({event:'RemoteMediaReceipt',uploadId:id,batchId:r.batch_id,state}));
      return this.view(this.get(id,owner));
    });
  }
  cancel(owner,id) {
    return this.store.transaction(()=>{
      const r=this.get(id,owner);if(!['Uploading','Quarantined','Cancelled'].includes(r.state))fail(409,'already_received','A received upload must be resolved by Studio.');
      if(r.state!=='Cancelled'){this.db.prepare("UPDATE remote_media SET state='Cancelled',updated_at=? WHERE id=?").run(this.now().toISOString(),id);recordActivity(this.db,owner,id,'Cancelled',owner);}
      return this.view(this.get(id,owner));
    });
  }
  acknowledge(id,p) {
    return this.store.transaction(()=>{
      const r=this.get(id),state=p.state;
      if(!['Imported','NeedsReview'].includes(state)||state==='Imported'&&(!uuid(p.assetId)||!uuid(p.versionId)))fail(400,'invalid_ack','Import acknowledgment needs resulting Studio asset and version IDs.');
      if(['Imported','NeedsReview','Purged'].includes(r.state)){if(r.result!==canonicalJson(p))fail(409,'ack_conflict','Upload already has a different result.');return this.view(r);}
      if(r.state!=='ReceivedByApi')fail(409,'not_received','Upload is not ready for import.');
      const now=this.now();this.db.prepare('UPDATE remote_media SET state=?,result=?,updated_at=?,expires_at=? WHERE id=?')
        .run(state,canonicalJson(p),now.toISOString(),new Date(now.getTime()+86400_000).toISOString(),id);
      recordActivity(this.db,r.owner,id,state,'Studio',{assetId:p.assetId||null,versionId:p.versionId||null,code:p.code||null},p.appliedAt);
      console.info(JSON.stringify({event:'RemoteMediaImportResult',uploadId:id,state,assetId:p.assetId,versionId:p.versionId}));
      return this.view(this.get(id));
    });
  }
  list(owner) {return this.db.prepare('SELECT * FROM remote_media WHERE owner=? ORDER BY created_at DESC LIMIT 1000').all(owner).map(r=>this.view(r));}
  retry(id,p) {
    if(!uuid(p.retryId)||typeof p.reason!=='string'||!p.reason.trim()||p.reason.length>1000||typeof p.actor!=='string'||p.actor.length>200)
      fail(400,'invalid_retry','A stable retry ID, staff actor and correction reason are required.');
    return this.store.transaction(()=>{
      const r=this.get(id),prior=this.db.prepare('SELECT * FROM remote_media_retries WHERE id=?').get(p.retryId);
      if(prior){if(prior.upload_id!==id||prior.payload!==JSON.stringify(p))fail(409,'retry_conflict','Retry ID belongs to another action.');return this.view(r);}
      if(r.state!=='NeedsReview')fail(409,'review_required','Only a held import may be retried after staff review.');
      this.db.prepare('INSERT INTO remote_media_retries VALUES(?,?,?)').run(p.retryId,id,JSON.stringify(p));
      this.db.prepare("UPDATE remote_media SET state='ReceivedByApi',result=NULL,updated_at=? WHERE id=?").run(this.now().toISOString(),id);
      recordActivity(this.db,r.owner,id,'ImportRetryApproved',p.actor,{retryId:p.retryId,reason:p.reason});
      return this.view(this.get(id));
    });
  }
  ready(){return this.db.prepare("SELECT * FROM remote_media WHERE state='ReceivedByApi' ORDER BY created_at LIMIT 20").all().map(r=>this.view(r));}
  activity(owner,id,after){this.get(id,owner);return activityFor(this.db,owner,id,after);}
  cleanup() {
    for(const r of this.db.prepare("SELECT * FROM remote_media WHERE state='Cancelled' OR (expires_at<? AND state IN ('Imported','Uploading','Quarantined'))").all(this.now().toISOString())){
      const path=this.path(r.id);if(existsSync(path))unlinkSync(path);
      this.store.transaction(()=>{const state=r.state==='Imported'?'Purged':'Expired';this.db.prepare('UPDATE remote_media SET state=?,updated_at=? WHERE id=?').run(state,this.now().toISOString(),r.id);recordActivity(this.db,r.owner,r.id,state,'Retention');});
    }
    // ReceivedByApi and NeedsReview are deliberately retained until a durable import or staff resolution.
  }
}
