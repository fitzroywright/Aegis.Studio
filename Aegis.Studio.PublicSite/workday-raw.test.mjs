import {test} from 'node:test';import assert from 'node:assert/strict';
import {randomUUID,createHash} from 'node:crypto';import {mkdtempSync,rmSync,writeFileSync} from 'node:fs';
import {join} from 'node:path';import {tmpdir} from 'node:os';import {Readable} from 'node:stream';
import {createServer} from './server.mjs';import {WorkDayExchangeStore} from './workday-exchange-store.mjs';
import {WorkDayLargeUploads} from './workday-large-uploads.mjs';
const checksum=b=>createHash('sha256').update(b).digest('hex');
const assignment='44444444-4444-4444-8444-444444444444',subject='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
// A structurally valid tiny DNG container. Decoder support is deliberately independent.
function dng(size=256){
 const b=Buffer.alloc(size);b.write('II');b.writeUInt16LE(42,2);b.writeUInt32LE(8,4);
 const tags=[[256,4,1,1],[257,4,1,1],[262,3,1,32803],[273,4,1,200],[279,4,1,1],[50706,1,4,0x00000401]];
 b.writeUInt16LE(tags.length,8);tags.forEach(([tag,type,n,value],i)=>{const p=10+i*12;b.writeUInt16LE(tag,p);b.writeUInt16LE(type,p+2);b.writeUInt32LE(n,p+4);b.writeUInt32LE(value,p+8);});b[200]=99;return b;
}
async function fixture(t,extra={}){
 const dir=mkdtempSync(join(tmpdir(),'workday-raw-'));const env={SESSION_SECRET:'synthetic-raw-test-session-secret-32-characters',INTAKE_DB_PATH:join(dir,'site.sqlite'),WORKDAY_UPLOAD_ROOT:join(dir,'private'),PUBLIC_API_PROVISIONING_TOKEN:'synthetic-provisioning-secret-more-than-32',WORKDAY_ACK_GRACE_SECONDS:'0',...extra};
 let server,base;
 const start=async()=>{server=createServer(env);await new Promise(r=>server.listen(0,'127.0.0.1',r));base='http://127.0.0.1:'+server.address().port+'/api/v1';};
 await start();
 t.after(async()=>{server.closeAllConnections();if(server.listening)await new Promise(r=>server.close(r));rmSync(dir,{recursive:true,force:true});});
 const post=(path,body,headers={})=>fetch(base+path,{method:'POST',headers:{'Content-Type':'application/json',...headers},body:JSON.stringify(body)});
 const key=await(await post('/admin/integration-keys',{name:'RAW test',scopes:['workday:sync']},{'X-Site-Provisioning-Token':env.PUBLIC_API_PROVISIONING_TOKEN})).json();const admin={Authorization:'Bearer '+key.secret};
 async function account(name){const p=await(await post('/admin/workday/users',{userName:name,displayName:name},admin)).json();assert.equal((await post('/workday/enroll',{enrollmentToken:p.enrollmentToken,password:'synthetic-passphrase-12345'})).status,200);const login=await post('/auth/login',{userName:name,password:'synthetic-passphrase-12345'});assert.equal(login.status,200);
  const r=await fetch(base+'/admin/workday/snapshots',{method:'PUT',headers:{...admin,'Content-Type':'application/json'},body:JSON.stringify({userName:name,revision:1,snapshot:{today:[{id:assignment,jobId:'22222222-2222-4222-8222-222222222222',sessionId:'33333333-3333-4333-8333-333333333333'}],upcoming:[],fieldTargets:{[assignment]:[{targetId:'subject:'+subject}]}}})});assert.equal(r.status,200);return {Cookie:login.headers.get('set-cookie').split(';')[0]};}
 const alice=await account('alice'),bob=await account('bob');
 return {dir,env,admin,alice,bob,post,get base(){return base;},restart:async()=>{await new Promise(r=>server.close(r));await start();},path:'/workday/assignments/'+assignment+'/upload-sessions'};
}

test('RAW session authorization, out-of-order idempotent chunks, restart, private original, verified ACK and grace deletion',async t=>{
 const f=await fixture(t);const bytes=dng(5*1024*1024),id=randomUUID(),facts={uploadId:id,fileName:'capture.dng',sizeBytes:bytes.length,sha256:checksum(bytes),subjectId:subject};
 assert.equal((await f.post(f.path,facts)).status,401);
 assert.equal((await f.post(f.path.replace(assignment,'77777777-7777-4777-8777-777777777777'),facts,f.alice)).status,403);
 assert.equal((await f.post(f.path,{...facts,subjectId:randomUUID()},f.alice)).status,403);
 assert.equal((await f.post(f.path,facts,f.alice)).status,201);
 const metadata=(await(await fetch(f.base+f.path+'/'+id,{headers:f.alice})).json()).metadata;
 assert.equal(metadata.uploader,'alice');assert.equal(metadata.subjectId,subject);assert.equal(metadata.sessionId,'33333333-3333-4333-8333-333333333333');assert.equal(metadata.jobId,'22222222-2222-4222-8222-222222222222');assert.ok(metadata.createdAtUtc);
 assert.equal((await f.post(f.path,{...facts,fileName:'changed.dng'},f.alice)).status,409);
 assert.equal((await f.post(f.path,facts,f.bob)).status,409);
 const path=f.path+'/'+id;
 assert.equal((await fetch(f.base+path,{headers:f.bob})).status,404);
 const chunkSize=4*1024*1024;
 async function chunk(index,hash){const body=bytes.subarray(index*chunkSize,Math.min((index+1)*chunkSize,bytes.length));return fetch(f.base+path+'/chunks/'+index,{method:'PUT',headers:{...f.alice,'Content-Type':'application/octet-stream','X-Chunk-SHA256':hash||checksum(body)},body});}
 assert.equal((await chunk(1)).status,200);assert.equal((await chunk(1)).status,200);
 assert.equal((await chunk(0,'0'.repeat(64))).status,422);
 assert.equal((await f.post(path+'/complete',{},f.alice)).status,409);
 await f.restart();assert.equal((await(await fetch(f.base+path,{headers:f.alice})).json()).receivedChunks.length,1);
 assert.equal((await chunk(0)).status,200);
 const complete=await f.post(path+'/complete',{},f.alice);assert.equal(complete.status,202,await complete.clone().text());assert.equal((await complete.json()).sha256,facts.sha256);
 assert.equal((await f.post(path+'/complete',{},f.alice)).status,202);
 assert.equal((await fetch(f.base+'/admin/workday/uploads/'+id+'/original',{headers:f.alice})).status,401);
 let original=await fetch(f.base+'/admin/workday/uploads/'+id+'/original',{headers:f.admin});assert.equal(original.status,200);assert.equal(checksum(Buffer.from(await original.arrayBuffer())),facts.sha256);
 const thumbnail='/workday/assignments/'+assignment+'/uploads/'+id+'/thumbnail';
 assert.equal((await fetch(f.base+thumbnail,{headers:f.bob})).status,404);
 // A RAW decoder need not be installed for durable storage and successful import.
 assert.ok([200,422].includes((await fetch(f.base+thumbnail,{headers:f.alice})).status));
 assert.equal((await f.post('/admin/workday/upload-retention',{},f.admin)).status,200);
 assert.equal((await fetch(f.base+'/admin/workday/uploads/'+id+'/original',{headers:f.admin})).status,200);
 const receipt={uploadId:id,assignmentId:assignment,sha256:facts.sha256,sizeBytes:bytes.length,storageStatus:'Stored',attachmentId:randomUUID(),versionId:randomUUID()};
 assert.equal((await f.post('/admin/workday/updates/'+id+'/result',{state:'Acknowledged',result:{...receipt,sha256:'0'.repeat(64)}},f.admin)).status,409);
 assert.equal((await(await fetch(f.base+'/admin/workday/updates?kind=AssignmentUpload',{headers:f.admin})).json()).items.length,1);
 assert.equal((await f.post('/admin/workday/updates/'+id+'/result',{state:'Acknowledged',result:receipt},f.admin)).status,200);
 assert.equal((await f.post('/admin/workday/updates/'+id+'/result',{state:'Acknowledged',result:receipt},f.admin)).status,200);
 assert.equal((await(await f.post('/admin/workday/upload-retention',{},f.admin)).json()).purged,1);
 assert.equal((await fetch(f.base+'/admin/workday/uploads/'+id+'/original',{headers:f.admin})).status,404);
 const gallery=await(await fetch(f.base+'/workday/assignments/'+assignment+'/uploads',{headers:f.alice})).json();assert.equal(gallery.items.length,1);assert.equal(gallery.items[0].state,'Acknowledged');
});

test('wrong full checksum, unsupported/malicious RAW, cancellation and quota cannot enqueue or purge originals',async t=>{
 const f=await fixture(t,{WORKDAY_MAX_UPLOAD_BYTES:'1024',WORKDAY_ACCOUNT_QUOTA_BYTES:'1024',WORKDAY_UPLOAD_QUOTA_BYTES:'1024'});
 for(const [fileName,bytes,hash,status] of [['bad.nef',Buffer.from('malicious content is not a camera file'),'correct',415],['capture.dng',dng(),'0'.repeat(64),422]]){
  const id=randomUUID(),path=f.path+'/'+id;assert.equal((await f.post(f.path,{uploadId:id,fileName,sizeBytes:bytes.length,sha256:hash==='correct'?checksum(bytes):hash},f.alice)).status,201);
  assert.equal((await fetch(f.base+path+'/chunks/0',{method:'PUT',headers:{...f.alice,'Content-Type':'application/octet-stream','X-Chunk-SHA256':checksum(bytes)},body:bytes})).status,200);
  assert.equal((await f.post(path+'/complete',{},f.alice)).status,status);
  assert.equal((await fetch(f.base+path,{method:'DELETE',headers:f.alice})).status,200);
 }
 const id=randomUUID(),facts={uploadId:id,fileName:'large.dng',sizeBytes:900,sha256:'1'.repeat(64)};
 assert.equal((await f.post(f.path,facts,f.alice)).status,201);
 assert.equal((await f.post(f.path,{...facts,uploadId:randomUUID()},f.alice)).status,507);
 assert.equal((await(await fetch(f.base+'/admin/workday/updates?kind=AssignmentUpload',{headers:f.admin})).json()).items.length,0);
});

test('interrupted chunk leaves no receipt and later retries after process restart preserve bytes',async t=>{
 const dir=mkdtempSync(join(tmpdir(),'raw-interrupt-')),path=join(dir,'db.sqlite'),id=randomUUID(),bytes=dng();let store=new WorkDayExchangeStore(path),uploads=new WorkDayLargeUploads({INTAKE_DB_PATH:path,WORKDAY_UPLOAD_ROOT:join(dir,'private')},store);
 try{
  store.provisionAccount({userName:'alice',displayName:'Alice'});await uploads.create('alice',assignment,{uploadId:id,fileName:'capture.dng',sizeBytes:bytes.length,sha256:checksum(bytes)});
  await assert.rejects(()=>uploads.writeChunk('alice',assignment,id,0,checksum(bytes),Readable.from((async function*(){yield bytes.subarray(0,20);throw Error('network interrupted');})())));
  assert.equal(uploads.status('alice',assignment,id).receivedChunks.length,0);uploads.close();store.close();store=new WorkDayExchangeStore(path);uploads=new WorkDayLargeUploads({INTAKE_DB_PATH:path,WORKDAY_UPLOAD_ROOT:join(dir,'private')},store);
  await uploads.writeChunk('alice',assignment,id,0,checksum(bytes),Readable.from([bytes]));assert.equal((await uploads.complete('alice',assignment,id)).sha256,checksum(bytes));
 }finally{uploads.close();store.close();rmSync(dir,{recursive:true,force:true});}
});

test('250 MiB original transfers with bounded chunks, streaming checksum, private pull and no base64 queue payload',async t=>{
 const f=await fixture(t);const size=250*1024*1024,id=randomUUID(),first=dng(4*1024*1024),zero=Buffer.alloc(4*1024*1024);const hash=createHash('sha256');
 for(let offset=0;offset<size;offset+=zero.length)hash.update(offset===0?first:zero.subarray(0,Math.min(zero.length,size-offset)));
 const digest=hash.digest('hex');assert.equal((await f.post(f.path,{uploadId:id,fileName:'large.dng',sizeBytes:size,sha256:digest},f.alice)).status,201);
 for(let offset=0,i=0;offset<size;offset+=zero.length,i++){
  const body=offset===0?first:zero.subarray(0,Math.min(zero.length,size-offset));
  const response=await fetch(f.base+f.path+'/'+id+'/chunks/'+i,{method:'PUT',headers:{...f.alice,'Content-Type':'application/octet-stream','X-Chunk-SHA256':checksum(body)},body});assert.equal(response.status,200);
 }
 const complete=await f.post(f.path+'/'+id+'/complete',{},f.alice);assert.equal(complete.status,202,await complete.clone().text());
 const original=await fetch(f.base+'/admin/workday/uploads/'+id+'/original',{headers:f.admin});const readHash=createHash('sha256');let length=0;
 for await(const bytes of original.body){length+=bytes.length;readHash.update(bytes);}assert.equal(length,size);assert.equal(readHash.digest('hex'),digest);
 const feed=await(await fetch(f.base+'/admin/workday/updates?kind=AssignmentUpload',{headers:f.admin})).json();assert.equal(feed.items[0].payload.sizeBytes,size);assert.ok(JSON.stringify(feed).length<4096);assert.equal(feed.items[0].payload.contentBase64,undefined);
});

test('positive retention grace starts only after a verified ACK and cannot be extended by ACK replay',async()=>{
 const dir=mkdtempSync(join(tmpdir(),'raw-grace-')),path=join(dir,'db.sqlite'),id=randomUUID(),bytes=dng();
 let now=100000;const store=new WorkDayExchangeStore(path),uploads=new WorkDayLargeUploads({INTAKE_DB_PATH:path,WORKDAY_UPLOAD_ROOT:join(dir,'private'),WORKDAY_ACK_GRACE_SECONDS:'3600'},store,()=>now);
 try{
  store.provisionAccount({userName:'alice',displayName:'Alice'});await uploads.create('alice',assignment,{uploadId:id,fileName:'capture.dng',sizeBytes:bytes.length,sha256:checksum(bytes)});
  await uploads.writeChunk('alice',assignment,id,0,checksum(bytes),Readable.from([bytes]));await uploads.complete('alice',assignment,id);
  now+=86400000;assert.equal((await uploads.purge()).purged,0);
  const result={state:'Acknowledged',result:{uploadId:id,assignmentId:assignment,sizeBytes:bytes.length,sha256:checksum(bytes),storageStatus:'Stored',attachmentId:randomUUID(),versionId:randomUUID()}};
  uploads.acknowledge(id,{state:'Conflict'});assert.equal((await uploads.purge()).purged,0);
  uploads.acknowledge(id,result);const deadline=now+3600000;
  now=deadline-1;uploads.acknowledge(id,result);assert.equal((await uploads.purge()).purged,0);
  now=deadline;assert.equal((await uploads.purge()).purged,1);assert.equal((await uploads.purge()).purged,0);
 }finally{uploads.close();store.close();rmSync(dir,{recursive:true,force:true});}
});


test('processing concurrency is bounded and unrelated originals can resume after saturation',async()=>{
 const dir=mkdtempSync(join(tmpdir(),'raw-concurrency-')),path=join(dir,'db.sqlite'),bytes=dng();
 const store=new WorkDayExchangeStore(path),uploads=new WorkDayLargeUploads({INTAKE_DB_PATH:path,WORKDAY_UPLOAD_ROOT:join(dir,'private'),WORKDAY_UPLOAD_CONCURRENCY:'1'},store);
 let release;const blocked=new Promise(r=>release=r);let entered;const started=new Promise(r=>entered=r);
 try{
  store.provisionAccount({userName:'alice',displayName:'Alice'});const first=randomUUID(),second=randomUUID();
  for(const id of [first,second])await uploads.create('alice',assignment,{uploadId:id,fileName:'capture.dng',sizeBytes:bytes.length,sha256:checksum(bytes)});
  const writing=uploads.writeChunk('alice',assignment,first,0,checksum(bytes),Readable.from((async function*(){entered();await blocked;yield bytes;})()));
  await started;
  await assert.rejects(()=>uploads.writeChunk('alice',assignment,second,0,checksum(bytes),Readable.from([bytes])),error=>error.status===503);
  release();await writing;await uploads.writeChunk('alice',assignment,second,0,checksum(bytes),Readable.from([bytes]));
  assert.equal(uploads.status('alice',assignment,second).receivedChunks.length,1);
 }finally{release();uploads.close();store.close();rmSync(dir,{recursive:true,force:true});}
});


test('a conflicting exchange result cannot be converted to a purgeable ACK by replay',async t=>{
 const f=await fixture(t),bytes=dng(),id=randomUUID(),facts={uploadId:id,fileName:'capture.dng',sizeBytes:bytes.length,sha256:checksum(bytes)};
 await f.post(f.path,facts,f.alice);
 assert.equal((await fetch(f.base+f.path+'/'+id+'/chunks/0',{method:'PUT',headers:{...f.alice,'Content-Type':'application/octet-stream','X-Chunk-SHA256':facts.sha256},body:bytes})).status,200);
 assert.equal((await f.post(f.path+'/'+id+'/complete',{},f.alice)).status,202);
 const endpoint='/admin/workday/updates/'+id+'/result';
 assert.equal((await f.post(endpoint,{state:'Conflict',message:'Synthetic authorization conflict'},f.admin)).status,200);
 const receipt={uploadId:id,assignmentId:assignment,sizeBytes:bytes.length,sha256:facts.sha256,storageStatus:'Stored',attachmentId:randomUUID(),versionId:randomUUID()};
 assert.equal((await(await f.post(endpoint,{state:'Acknowledged',result:receipt},f.admin)).json()).state,'Conflict');
 assert.equal((await(await f.post('/admin/workday/upload-retention',{},f.admin)).json()).purged,0);
 const original=await fetch(f.base+'/admin/workday/uploads/'+id+'/original',{headers:f.admin});assert.equal(original.status,200);await original.arrayBuffer();
});

test('restart after original commit but before queue insertion recovers one import without reupload',async()=>{
 const dir=mkdtempSync(join(tmpdir(),'raw-ready-recovery-')),path=join(dir,'db.sqlite'),id=randomUUID(),bytes=dng(),env={INTAKE_DB_PATH:path,WORKDAY_UPLOAD_ROOT:join(dir,'private')};
 let store=new WorkDayExchangeStore(path),uploads=new WorkDayLargeUploads(env,store);
 try{
  store.provisionAccount({userName:'alice',displayName:'Alice'});await uploads.create('alice',assignment,{uploadId:id,fileName:'capture.dng',sizeBytes:bytes.length,sha256:checksum(bytes)});
  await uploads.writeChunk('alice',assignment,id,0,checksum(bytes),Readable.from([bytes]));
  store.enqueue=()=>{throw Error('Synthetic process exit before outbox insertion');};
  await assert.rejects(()=>uploads.complete('alice',assignment,id));assert.equal(uploads.status('alice',assignment,id).state,'Ready');
  uploads.close();store.close();store=new WorkDayExchangeStore(path);uploads=new WorkDayLargeUploads(env,store);
  assert.equal((await uploads.complete('alice',assignment,id)).sha256,checksum(bytes));await uploads.complete('alice',assignment,id);
  assert.equal(store.pendingUpdates(0,20,'AssignmentUpload').length,1);
 }finally{uploads.close();store.close();rmSync(dir,{recursive:true,force:true});}
});


test('isolated photo helpers receive no service credentials or injected Python search path',async()=>{
 const dir=mkdtempSync(join(tmpdir(),'raw-process-env-')),path=join(dir,'db.sqlite'),helper=join(dir,'check.py');
 const store=new WorkDayExchangeStore(path),uploads=new WorkDayLargeUploads({INTAKE_DB_PATH:path,WORKDAY_UPLOAD_ROOT:join(dir,'private'),WORKDAY_PHOTO_PROBE_PATH:helper},store);
 const old=process.env.PHOTO_PROCESS_TEST_CREDENTIAL;
 try{
  process.env.PHOTO_PROCESS_TEST_CREDENTIAL='synthetic-test-value';
  writeFileSync(helper,`import os,json
assert 'PHOTO_PROCESS_TEST_CREDENTIAL' not in os.environ
assert 'PYTHONPATH' not in os.environ
print(json.dumps({'format':'DNG','raw':True,'contentType':'application/octet-stream'}))
`);
  assert.equal(JSON.parse(await uploads.runHelper('probe',[])).format,'DNG');
 }finally{if(old===undefined)delete process.env.PHOTO_PROCESS_TEST_CREDENTIAL;else process.env.PHOTO_PROCESS_TEST_CREDENTIAL=old;uploads.close();store.close();rmSync(dir,{recursive:true,force:true});}
});
