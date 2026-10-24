import { createHash } from 'node:crypto';
import { uuid } from './remote-media-store.mjs';
import { ensureActivity, recordActivity, activityFor, canonicalJson } from './exchange-activity.mjs';
import { fail } from './intake-store.mjs';

const eventTime=s=>typeof s==='string'&&/^\d{4}-\d{2}-\d{2}T/.test(s)&&Number.isFinite(Date.parse(s))&&/^\d{4}-/.test(new Date(s).toISOString())&&!new Date(s).toISOString().startsWith('0000');
const text=(s,max=200)=>typeof s==='string'&&s.trim().length>0&&s.length<=max;
export const stableJson=canonicalJson;
export const manifestDigest=p=>createHash('sha256').update(stableJson(p)).digest('hex');

export class GalleryExchangeStore {
  constructor(store,env={}){this.quota=Math.max(100000000,Number(env.GALLERY_TRANSFER_QUOTA_BYTES)||100000000000);this.store=store;this.db=store.db;ensureActivity(this.db);
    this.db.exec(`CREATE TABLE IF NOT EXISTS gallery_requests(
      sequence INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT NOT NULL UNIQUE,owner TEXT NOT NULL,external_reference TEXT NOT NULL,
      requester_reference TEXT NOT NULL,hints TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'Pending',decision TEXT,
      decision_version INTEGER NOT NULL DEFAULT 0,created_at TEXT NOT NULL,UNIQUE(owner,external_reference));
      CREATE TABLE IF NOT EXISTS gallery_transfers(id TEXT PRIMARY KEY,request_id TEXT NOT NULL,gallery_id TEXT NOT NULL,
        owner TEXT NOT NULL,version INTEGER NOT NULL,manifest TEXT NOT NULL,digest TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'Staging',
        acknowledgment TEXT,created_at TEXT NOT NULL,completed_at TEXT,UNIQUE(gallery_id,version));
      CREATE TABLE IF NOT EXISTS gallery_transfer_files(transfer_id TEXT NOT NULL,version_id TEXT NOT NULL,content BLOB NOT NULL,PRIMARY KEY(transfer_id,version_id));
      CREATE TABLE IF NOT EXISTS gallery_revocations(id TEXT PRIMARY KEY,gallery_id TEXT NOT NULL,owner TEXT NOT NULL,request_id TEXT NOT NULL,
        payload TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'Requested',acknowledgment TEXT,created_at TEXT NOT NULL,completed_at TEXT);
      CREATE TABLE IF NOT EXISTS gallery_exchange_events(sequence INTEGER PRIMARY KEY AUTOINCREMENT,owner TEXT NOT NULL,aggregate_id TEXT NOT NULL,payload TEXT NOT NULL);`);
  }
  request(id,owner=null){const r=this.db.prepare('SELECT * FROM gallery_requests WHERE id=?').get(id);if(!r||owner!==null&&r.owner!==owner)fail(404,'not_found','Gallery request not found.');return r;}
  requestView(r,internal=false){return {id:r.id,integrationId:r.owner,externalReference:r.external_reference,requesterReference:r.requester_reference,
    ...(internal?{hints:JSON.parse(r.hints)}:{}),state:r.state,decisionVersion:r.decision_version,
    decision:r.decision?JSON.parse(r.decision):null,createdAt:r.created_at};}
  createRequest(owner,p){
    if(!uuid(p.id)||!text(p.externalReference)||!text(p.requesterReference)||!p.hints||typeof p.hints!=='object'||Array.isArray(p.hints)||JSON.stringify(p.hints).length>4000)
      fail(400,'invalid_request','Stable request ID, external reference, requester reference and lookup hints are required.');
    const hints=stableJson(p.hints);
    return this.store.transaction(()=>{
      const old=this.db.prepare('SELECT * FROM gallery_requests WHERE id=? OR (owner=? AND external_reference=?)').get(p.id,owner,p.externalReference);
      if(old){if(old.id!==p.id||old.owner!==owner||old.external_reference!==p.externalReference||old.requester_reference!==p.requesterReference||old.hints!==hints)fail(409,'request_conflict','The request identity was used for different data.');return this.requestView(old);}
      this.db.prepare('INSERT INTO gallery_requests(id,owner,external_reference,requester_reference,hints,created_at) VALUES(?,?,?,?,?,?)')
        .run(p.id,owner,p.externalReference,p.requesterReference,hints,new Date().toISOString());
      recordActivity(this.db,owner,p.id,'GalleryRequested',owner);
      return this.requestView(this.request(p.id));
    });
  }
  decide(id,p){
    return this.store.transaction(()=>{
      const r=this.request(id);
      if(!['Approved','Rejected','Held'].includes(p.state)||!Number.isSafeInteger(p.version)||p.version<1||p.state==='Approved'&&(!uuid(p.clientId)||!uuid(p.jobId)||p.entitlementVerified!==true)||typeof p.reason!=='string'||p.reason.length>1000)
        fail(400,'invalid_decision','A versioned staff decision and verified client/job association are required.');
      if(p.state!=='Approved'&&!p.reason.trim())fail(400,'reason_required','Supply a client-facing review reason.');
      const oldDecision=r.decision?JSON.parse(r.decision):null;
      if(oldDecision&&this.db.prepare('SELECT 1 FROM gallery_transfers WHERE request_id=?').get(id)&&
          (oldDecision.clientId!==p.clientId||oldDecision.jobId!==p.jobId))fail(409,'request_scope_locked','A released request cannot change client or job.');
      const encoded=stableJson(p);
      if(p.version===r.decision_version){if(r.decision!==encoded)fail(409,'decision_conflict','Decision version has different data.');return this.requestView(r);}
      if(p.version!==r.decision_version+1)fail(409,'decision_conflict','Apply staff decisions in order.');
      this.db.prepare('UPDATE gallery_requests SET state=?,decision=?,decision_version=? WHERE id=?').run(p.state,encoded,p.version,id);
      recordActivity(this.db,r.owner,id,'GalleryDecision','Studio',{state:p.state,version:p.version,jobId:p.jobId||null});
      return this.requestView(this.request(id));
    });
  }
  transfer(id,owner=null){const r=this.db.prepare('SELECT * FROM gallery_transfers WHERE id=?').get(id);if(!r||owner!==null&&r.owner!==owner)fail(404,'not_found','Transfer not found.');return r;}
  transferView(r){return {...JSON.parse(r.manifest),digest:r.digest,state:r.state,acknowledgment:r.acknowledgment?JSON.parse(r.acknowledgment):null,createdAt:r.created_at};}
  stage(p){
    if(!uuid(p.id)||!uuid(p.requestId)||p.galleryId!==p.requestId||!Number.isSafeInteger(p.version)||p.version<1||!text(p.title)||!Array.isArray(p.items)||p.items.length<1||p.items.length>10000)
      fail(400,'invalid_manifest','A versioned gallery manifest with approved files is required.');
    const ids=new Set(),versions=new Set();
    for(const f of p.items){
      if(!uuid(f.itemId)||!uuid(f.versionId)||ids.has(f.itemId)||versions.has(f.versionId)||!text(f.fileName,255)||/[\\/\x00-\x1f]/.test(f.fileName)||!Number.isSafeInteger(f.sizeBytes)||f.sizeBytes<1||f.sizeBytes>100_000_000||
        !/^[a-f0-9]{64}$/i.test(f.sha256)||!['image/jpeg','image/png','image/webp','image/tiff'].includes(f.contentType))
        fail(400,'invalid_manifest','Manifest file IDs, checksums, types and sizes must be valid and unique.');
      ids.add(f.itemId);versions.add(f.versionId);
    }
    return this.store.transaction(()=>{
      const request=this.request(p.requestId);if(request.state!=='Approved')fail(409,'not_approved','Staff must approve the request before release.');
      if(p.decisionVersion!==request.decision_version)fail(409,'stale_decision','Transfer must match the current verified staff decision.');
      const manifest=stableJson(p),digest=manifestDigest(p),prior=this.db.prepare('SELECT * FROM gallery_transfers WHERE id=?').get(p.id);
      if(prior){if(prior.manifest!==manifest||prior.owner!==request.owner)fail(409,'transfer_conflict','Transfer identities are immutable.');return {...this.transferView(prior),storedVersionIds:this.storedFiles(p.id)};}
      const previous=this.db.prepare('SELECT * FROM gallery_transfers WHERE gallery_id=? ORDER BY version DESC LIMIT 1').get(p.galleryId);
      if(previous&&(previous.request_id!==p.requestId||previous.owner!==request.owner))fail(409,'gallery_scope','Gallery belongs to another approved request.');
      if(previous&&p.version<=previous.version)fail(409,'version_conflict','Publish a newer gallery version. Cancelled local versions may leave gaps.');
      const withdrawal=this.db.prepare('SELECT created_at FROM gallery_revocations WHERE gallery_id=? ORDER BY created_at DESC LIMIT 1').get(p.galleryId);
      if(withdrawal&&(!p.republishApproval||p.decisionVersion!==request.decision_version))fail(409,'republish_approval_required','A withdrawn gallery needs an explicit new staff approval.');
      this.db.prepare('INSERT INTO gallery_transfers(id,request_id,gallery_id,owner,version,manifest,digest,created_at) VALUES(?,?,?,?,?,?,?,?)')
        .run(p.id,p.requestId,p.galleryId,request.owner,p.version,manifest,digest,new Date().toISOString());
      const oldItems=previous?JSON.parse(previous.manifest).items:[],oldById=new Map(oldItems.map(x=>[x.itemId,x]));
      const counts={added:p.items.filter(x=>!oldById.has(x.itemId)).length,replaced:p.items.filter(x=>oldById.has(x.itemId)&&oldById.get(x.itemId).versionId!==x.versionId).length,removed:oldItems.filter(x=>!ids.has(x.itemId)).length};
      recordActivity(this.db,request.owner,p.requestId,'TransferStaged','Studio',{transferId:p.id,galleryId:p.galleryId,version:p.version,...counts});
      return {...this.transferView(this.transfer(p.id)),storedVersionIds:[]};
    });
  }
  storedFiles(id){return this.db.prepare('SELECT version_id FROM gallery_transfer_files WHERE transfer_id=?').all(id).map(x=>x.version_id);}
  putFile(id,versionId,bytes){
    const r=this.transfer(id);if(r.state!=='Staging')fail(409,'transfer_not_staging','Only a staged transfer accepts files.');
    const f=JSON.parse(r.manifest).items.find(x=>x.versionId===versionId);
    if(!f||bytes.length!==f.sizeBytes||createHash('sha256').update(bytes).digest('hex')!==f.sha256.toLowerCase())fail(422,'checksum_mismatch','File bytes must match the approved manifest.');
    const old=this.db.prepare('SELECT content FROM gallery_transfer_files WHERE transfer_id=? AND version_id=?').get(id,versionId);
    if(old){if(!Buffer.from(old.content).equals(bytes))fail(409,'file_conflict','Immutable file changed.');return;}
    const used=this.db.prepare('SELECT coalesce(sum(length(content)),0) AS bytes FROM gallery_transfer_files').get().bytes;
    if(used+bytes.length>this.quota)fail(413,'gallery_quota_exceeded','Gallery transfer storage quota reached.');
    this.store.transaction(()=>{this.db.prepare('INSERT INTO gallery_transfer_files VALUES(?,?,?)').run(id,versionId,bytes);
      recordActivity(this.db,r.owner,r.request_id,'TransferFileStored','Studio',{transferId:id,versionId,sizeBytes:bytes.length});});
  }
  event(owner,id,p){this.db.prepare('INSERT INTO gallery_exchange_events(owner,aggregate_id,payload) VALUES(?,?,?)').run(owner,id,stableJson(p));}
  activate(id){
    return this.store.transaction(()=>{
      const r=this.transfer(id);if(['Available','Completed'].includes(r.state))return this.transferView(r);
      if(r.state!=='Staging')fail(409,'transfer_unavailable','Transfer is no longer publishable.');
      const p=JSON.parse(r.manifest),stored=this.storedFiles(id);
      if(p.items.some(x=>!stored.includes(x.versionId)))fail(409,'files_pending','Every approved file must be present.');
      const approval=this.request(r.request_id);if(approval.state!=='Approved'||JSON.parse(r.manifest).decisionVersion!==approval.decision_version)fail(409,'not_approved','Gallery request approval changed before activation.');
      const newest=this.db.prepare("SELECT max(version) AS version FROM gallery_transfers WHERE gallery_id=? AND state IN ('Available','Completed')").get(r.gallery_id).version||0;
      if(newest>=r.version)fail(409,'stale_transfer','A later gallery version is already available.');
      this.db.prepare("UPDATE gallery_transfers SET state='Superseded' WHERE gallery_id=? AND version<? AND state IN ('Staging','Available','Completed')").run(r.gallery_id,r.version);
      this.db.prepare("UPDATE gallery_transfers SET state='Available' WHERE id=?").run(id);
      this.event(r.owner,r.request_id,{type:'TransferAvailable',transferId:id,requestId:r.request_id,galleryId:r.gallery_id,version:r.version});
      recordActivity(this.db,r.owner,r.request_id,'TransferAvailable','Studio',{transferId:id,version:r.version});
      console.info(JSON.stringify({event:'GalleryTransferAvailable',requestId:r.request_id,transferId:id,version:r.version}));
      return this.transferView(this.transfer(id));
    });
  }
  file(id,versionId,owner){
    const r=this.transfer(id,owner);if(this.request(r.request_id).state!=='Approved'||!['Available','Completed'].includes(r.state))fail(410,'transfer_unavailable','Transfer was withdrawn or superseded.');
    const item=JSON.parse(r.manifest).items.find(x=>x.versionId===versionId),data=this.db.prepare('SELECT content FROM gallery_transfer_files WHERE transfer_id=? AND version_id=?').get(id,versionId);
    if(!item||!data)fail(410,'file_unavailable','Transfer file is unavailable.');return {item,bytes:Buffer.from(data.content)};
  }
  acknowledge(id,owner,p){
    return this.store.transaction(()=>{
      const r=this.transfer(id,owner);
      if(r.state==='Completed'){if(r.acknowledgment!==stableJson(p))fail(409,'ack_conflict','Transfer already has a different acknowledgment.');return this.transferView(r);}
      if(r.state!=='Available'||p.version!==r.version||p.digest!==r.digest||p.state!=='Imported'||!text(p.occurredAt,60)||!eventTime(p.occurredAt))
        fail(409,'invalid_ack','A complete import acknowledgment must match the current transfer version and digest.');
      this.db.prepare("UPDATE gallery_transfers SET state='Completed',acknowledgment=?,completed_at=? WHERE id=?").run(stableJson(p),new Date().toISOString(),id);
      recordActivity(this.db,owner,r.request_id,'ExternalImportConfirmed',owner,{transferId:id,version:r.version,digest:r.digest},p.occurredAt);
      console.info(JSON.stringify({event:'GalleryExternalImportConfirmed',transferId:id,requestId:r.request_id,version:r.version}));
      return this.transferView(this.transfer(id));
    });
  }
  revoke(p){
    if(!uuid(p.id)||!uuid(p.requestId)||p.galleryId!==p.requestId||!Number.isSafeInteger(p.throughVersion)||p.throughVersion<0||!text(p.reason,1000))fail(400,'invalid_revocation','Stable revocation and gallery/request IDs and reason are required.');
    return this.store.transaction(()=>{
      const request=this.request(p.requestId),previous=this.db.prepare('SELECT * FROM gallery_revocations WHERE id=?').get(p.id);
      if(previous){if(previous.payload!==stableJson(p))fail(409,'revocation_conflict','Revocation ID was used for different data.');return previous;}
      this.db.prepare('INSERT INTO gallery_revocations(id,gallery_id,owner,request_id,payload,created_at) VALUES(?,?,?,?,?,?)')
        .run(p.id,p.galleryId,request.owner,p.requestId,stableJson(p),new Date().toISOString());
      this.db.prepare("UPDATE gallery_transfers SET state='Revoked' WHERE gallery_id=? AND version<=?").run(p.galleryId,p.throughVersion);
      // Re-publication requires another approved decision as well as a new transfer version.
      if(!this.db.prepare("SELECT 1 FROM gallery_transfers WHERE gallery_id=? AND version>? AND state IN ('Staging','Available','Completed')").get(p.galleryId,p.throughVersion))this.db.prepare("UPDATE gallery_requests SET state='Held' WHERE id=?").run(p.requestId);
      this.event(request.owner,p.requestId,{type:'RevocationRequested',revocationId:p.id,requestId:p.requestId,galleryId:p.galleryId,throughVersion:p.throughVersion});
      recordActivity(this.db,request.owner,p.requestId,'RevocationRequested','Studio',{revocationId:p.id,galleryId:p.galleryId});
      console.info(JSON.stringify({event:'GalleryRevocationRequested',revocationId:p.id,requestId:p.requestId,galleryId:p.galleryId,throughVersion:p.throughVersion}));
      return this.db.prepare('SELECT * FROM gallery_revocations WHERE id=?').get(p.id);
    });
  }
  acknowledgeRevocation(id,owner,p){
    return this.store.transaction(()=>{
      const r=this.db.prepare('SELECT * FROM gallery_revocations WHERE id=? AND owner=?').get(id,owner);
      if(!r)fail(404,'not_found','Revocation not found.');
      if(!['Received','Completed','Failed'].includes(p.state)||p.galleryId!==r.gallery_id||p.requestId!==r.request_id||p.throughVersion!==JSON.parse(r.payload).throughVersion||!text(p.occurredAt,60)||!eventTime(p.occurredAt)||
        typeof p.limitations!=='string'||p.limitations.length>1000)fail(400,'invalid_ack','Revocation status, scope, event time and deletion limitations are required.');
      if(r.acknowledgment===stableJson(p))return r;
      if(r.state==='Completed')fail(409,'ack_conflict','Completed revocation cannot change.');
      this.db.prepare('UPDATE gallery_revocations SET state=?,acknowledgment=?,completed_at=? WHERE id=?')
        .run(p.state,stableJson(p),p.state==='Completed'?new Date().toISOString():null,id);
      recordActivity(this.db,owner,r.request_id,'ExternalRevocation'+p.state,owner,{revocationId:id,limitations:p.limitations},p.occurredAt);
      console.info(JSON.stringify({event:'GalleryRevocationResult',revocationId:id,requestId:r.request_id,state:p.state}));
      return this.db.prepare('SELECT * FROM gallery_revocations WHERE id=?').get(id);
    });
  }
  cleanup(){
    const cutoff=new Date(Date.now()-86400000).toISOString();
    for(const r of this.db.prepare("SELECT id,owner,request_id FROM gallery_transfers t WHERE (state='Completed' AND completed_at<?) OR (state='Superseded' AND EXISTS(SELECT 1 FROM gallery_transfers n WHERE n.gallery_id=t.gallery_id AND n.version>t.version AND n.state='Completed' AND n.completed_at<?)) OR (state='Revoked' AND EXISTS(SELECT 1 FROM gallery_revocations r WHERE r.gallery_id=t.gallery_id AND r.state='Completed' AND json_extract(r.payload,'$.throughVersion')>=t.version AND r.completed_at<?))").all(cutoff,cutoff,cutoff)){
      if(!this.db.prepare('SELECT 1 FROM gallery_transfer_files WHERE transfer_id=?').get(r.id))continue;
      this.store.transaction(()=>{this.db.prepare('DELETE FROM gallery_transfer_files WHERE transfer_id=?').run(r.id);recordActivity(this.db,r.owner,r.request_id,'TemporaryTransferFilesPurged','Retention',{transferId:r.id});});
    }
  }
  events(owner,after=0){return this.db.prepare('SELECT sequence,payload FROM gallery_exchange_events WHERE owner=? AND sequence>? ORDER BY sequence LIMIT 100').all(owner,after).map(x=>({sequence:x.sequence,...JSON.parse(x.payload)}));}
  activity(owner,id,after=0){this.request(id,owner);return activityFor(this.db,owner,id,after);}
}
