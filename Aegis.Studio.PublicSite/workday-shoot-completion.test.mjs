import {test} from 'node:test';
import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import {WorkDayExchangeStore} from './workday-exchange-store.mjs';

test('shoot declaration requires all own assignment photos, preserves retries and survives new uploads',t=>{
  const store=new WorkDayExchangeStore(':memory:');t.after(()=>store.close());
  store.provisionAccount({userName:'alice',displayName:'Alice'});
  store.provisionAccount({userName:'bob',displayName:'Bob'});
  const assignment=randomUUID(),other=randomUUID(),first=randomUUID(),second=randomUUID();
  const request=()=>({operationId:randomUUID(),expectedVersion:0});
  const upload=(user,id,job=assignment)=>store.enqueue(user,id,'AssignmentUpload',{assignmentId:job,uploadId:id,fileName:'images.jpg'});
  const acknowledge=(id,version=true)=>store.completeUpdate(id,{state:'Acknowledged',result:version?{
    versionId:randomUUID(),assetId:randomUUID(),messageId:randomUUID(),storageStatus:'Stored'
  }:{attachmentId:randomUUID()}});
  assert.throws(()=>store.confirmShoot('alice',assignment,request()),/every photograph/);
  upload('alice',first);assert.throws(()=>store.confirmShoot('alice',assignment,request()),/every photograph/);
  acknowledge(first);upload('alice',second);acknowledge(second,false);
  assert.throws(()=>store.confirmShoot('alice',assignment,request()),/every photograph/);
  // An old attachment-only receipt never qualifies as a gallery import.
  store.db.prepare("UPDATE workday_updates SET state='Pending' WHERE operation_id=?").run(second);acknowledge(second);
  upload('bob',randomUUID());upload('alice',randomUUID(),other); // Other users/assignments do not block.
  const receipt=store.confirmShoot('ALICE',assignment,request());assert.equal(receipt.state,'Pending');
  assert.equal(store.confirmShoot('alice',assignment,request()).operationId,receipt.operationId);
  const queued=store.pendingUpdates(0,100,'Operation').find(x=>x.operationId===receipt.operationId);
  assert.equal(queued.payload.kind,'ConfirmShootComplete');assert.deepEqual(queued.payload.uploadIds,[first,second]);
  assert.equal(queued.payload.assignmentId,assignment);assert.equal(queued.payload.expectedVersion,0);
  assert.throws(()=>upload('alice',randomUUID()),/confirmation is syncing/);
  assert.equal(upload('alice',first).replayed,true); // Lost upload responses remain retryable.
  assert.equal(store.shootCompletion('bob',assignment),null);
  store.completeUpdate(receipt.operationId,{state:'Conflict',message:'Assignment changed.'});
  const retry=store.confirmShoot('alice',assignment,request());assert.notEqual(retry.operationId,receipt.operationId);
  store.completeUpdate(retry.operationId,{state:'Acknowledged',message:'Acknowledged'});
  assert.equal(store.shootCompletion('alice',assignment).state,'Acknowledged');
  assert.equal(store.confirmShoot('alice',assignment,request()).operationId,retry.operationId);
  const later=randomUUID();upload('alice',later);assert.equal(store.shootCompletion('alice',assignment),null);
  assert.throws(()=>store.confirmShoot('alice',assignment,request()),/every photograph/);
  acknowledge(later);assert.equal(store.confirmShoot('alice',assignment,request()).state,'Pending');
});
