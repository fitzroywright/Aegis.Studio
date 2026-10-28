import { test } from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import sharp from 'sharp';
import { createServer } from './server.mjs';
import { WorkDayExchangeStore } from './workday-exchange-store.mjs';

const assignment='44444444-4444-4444-8444-444444444444';
const otherAssignment='66666666-6666-4666-8666-666666666666';
test('uploaded-photo gallery survives restart and restricts lists and thumbnails to owner and current assignment',async t=>{
  const dir=mkdtempSync(join(tmpdir(),'workday-gallery-'));
  const provision='gallery-test-provisioning-secret-at-least-32-chars';
  const env={SESSION_SECRET:'gallery-test-session-secret-at-least-32-chars',INTAKE_DB_PATH:join(dir,'site.sqlite'),PUBLIC_API_PROVISIONING_TOKEN:provision};
  let server,base;
  async function start(){
    server=createServer(env);await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
    base='http://127.0.0.1:'+server.address().port+'/api/v1';
  }
  t.after(async()=>{if(server?.listening)await new Promise(resolve=>server.close(resolve));rmSync(dir,{recursive:true,force:true});});
  await start();
  const post=async(path,body,headers={})=>fetch(base+path,{method:'POST',headers:{'Content-Type':'application/json',...headers},body:JSON.stringify(body)});
  const created=await post('/admin/integration-keys',{name:'WorkDay gallery test',scopes:['workday:sync']},{'X-Site-Provisioning-Token':provision});
  assert.equal(created.status,201);
  const studio={Authorization:'Bearer '+(await created.json()).secret,'Content-Type':'application/json'};
  async function snapshot(userName,revision,ids){
    const response=await fetch(base+'/admin/workday/snapshots',{method:'PUT',headers:studio,body:JSON.stringify({
      userName,revision,snapshot:{userName,displayName:userName,today:ids.map(id=>({id,title:'Test assignment'})),upcoming:[]}})});
    assert.equal(response.status,200);
  }
  async function account(userName){
    const response=await post('/admin/workday/users',{userName,displayName:userName},studio);
    const enrolled=await post('/workday/enroll',{enrollmentToken:(await response.json()).enrollmentToken,password:'test-only-passphrase-12345'});
    assert.equal(enrolled.status,200);
    const login=await post('/auth/login',{userName,password:'test-only-passphrase-12345'});assert.equal(login.status,200);
    await snapshot(userName,1,[assignment,otherAssignment]);
    return {Cookie:login.headers.get('set-cookie').split(';')[0]};
  }
  const alice=await account('alice'),bob=await account('bob');
  const photo=await sharp({create:{width:720,height:480,channels:3,background:'#42617b'}}).png().toBuffer();
  const uploadId=randomUUID(),path='/workday/assignments/'+assignment+'/uploads';
  async function upload(id,bytes=photo,name='portrait.png',headers=alice){
    const form=new FormData();form.append('uploadId',id);form.append('file',new Blob([bytes],{type:'image/png'}),name);
    return fetch(base+path,{method:'POST',headers,body:form});
  }
  assert.equal((await upload(uploadId)).status,202);
  assert.equal((await upload(uploadId)).status,202); // Same operation is safe to retry.
  const response=await fetch(base+path,{headers:alice});assert.equal(response.status,200);
  assert.match(response.headers.get('cache-control'),/private.*no-store/);
  const page=await response.json();assert.equal(page.items.length,1);assert.equal(page.items[0].uploadId,uploadId);
  assert.equal(page.items[0].state,'Pending');assert.equal(page.items[0].fileName,'portrait.png');
  assert.doesNotMatch(JSON.stringify(page),/contentBase64|password|user_key/);
  const thumbnail=path+'/'+uploadId+'/thumbnail';
  const image=await fetch(base+thumbnail,{headers:alice});assert.equal(image.status,200);
  assert.equal(image.headers.get('content-type'),'image/jpeg');assert.match(image.headers.get('cache-control'),/private.*no-store/);
  const details=await sharp(Buffer.from(await image.arrayBuffer())).metadata();
  assert.equal(details.width,360);assert.equal(details.height,240);assert.equal(details.exif,undefined);
  assert.equal((await fetch(base+path)).status,401);assert.equal((await fetch(base+thumbnail)).status,401);
  assert.deepEqual((await(await fetch(base+path,{headers:bob})).json()).items,[]);
  assert.equal((await fetch(base+thumbnail,{headers:bob})).status,404);
  const otherPath='/workday/assignments/'+otherAssignment+'/uploads';
  assert.deepEqual((await(await fetch(base+otherPath,{headers:alice})).json()).items,[]);
  assert.equal((await fetch(base+otherPath+'/'+uploadId+'/thumbnail',{headers:alice})).status,404);
  assert.equal((await fetch(base+'/workday/assignments/77777777-7777-4777-8777-777777777777/uploads',{headers:alice})).status,403);
  assert.equal((await fetch(base+path+'?before=-1',{headers:alice})).status,400);
  assert.equal((await fetch(base+path+'?before=NaN',{headers:alice})).status,400);
  const acknowledged=await post('/admin/workday/updates/'+uploadId+'/result',{state:'Acknowledged'},studio);
  assert.equal(acknowledged.status,200);
  assert.equal((await(await fetch(base+path,{headers:alice})).json()).items[0].state,'Acknowledged');
  await snapshot('alice',2,[]);
  assert.equal((await fetch(base+path,{headers:alice})).status,403);
  assert.equal((await fetch(base+thumbnail,{headers:alice})).status,403);
  await snapshot('alice',3,[assignment]);
  await new Promise(resolve=>server.close(resolve));await start();
  assert.equal((await(await fetch(base+path,{headers:alice})).json()).items[0].uploadId,uploadId);
  assert.equal((await fetch(base+thumbnail,{headers:alice})).status,200);
  const badId=randomUUID();assert.equal((await upload(badId,Buffer.from([137,80,78,71,13,10,26,10]),'damaged.png')).status,202);
  assert.equal((await fetch(base+path+'/'+badId+'/thumbnail',{headers:alice})).status,422);
  assert.equal((await(await fetch(base+path,{headers:alice})).json()).items.length,2);
});

test('upload history pages without photo bytes, duplicate rows or cross-account records',()=>{
  const store=new WorkDayExchangeStore(':memory:');
  try{
    store.provisionAccount({userName:'alice',displayName:'Alice'});store.provisionAccount({userName:'bob',displayName:'Bob'});
    const ids=[];
    for(let i=0;i<83;i++){
      const uploadId=randomUUID();ids.push(uploadId);
      store.enqueue('alice',uploadId,'AssignmentUpload',{assignmentId:assignment,uploadId,fileName:i+'.jpg',contentBase64:'private-bytes'});
    }
    store.enqueue('bob',randomUUID(),'AssignmentUpload',{assignmentId:assignment,fileName:'private.jpg'});
    const first=store.assignmentUploads('alice',assignment),second=store.assignmentUploads('alice',assignment,first.nextCursor),
      third=store.assignmentUploads('alice',assignment,second.nextCursor);
    assert.equal(first.items.length,40);assert.equal(second.items.length,40);assert.equal(third.items.length,3);assert.equal(third.nextCursor,null);
    assert.equal(new Set([...first.items,...second.items,...third.items].map(x=>x.uploadId)).size,83);
    assert.equal(first.items[0].uploadId,ids.at(-1));assert.doesNotMatch(JSON.stringify(first),/contentBase64|private-bytes/);
    assert.equal(store.assignmentUpload('bob',assignment,ids[0]),null);
    store.completeUpdate(ids[0],{state:'Conflict'});
    assert.equal(store.assignmentUploads('alice',assignment,Number.MAX_SAFE_INTEGER,100).items.at(-1).state,'Conflict');
  }finally{store.close();}
});
