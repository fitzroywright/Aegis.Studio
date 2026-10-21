import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { IntakeStore } from './intake-store.mjs';
const inquiry={kind:'Inquiry',clientName:'Test Client',clientEmail:'test@example.invalid',notes:'Question'};
test('saved references and replay results survive restart, and conflict is rejected',()=>{
 const dir=mkdtempSync(join(tmpdir(),'studio-intake-'));try{
 let s=new IntakeStore(join(dir,'test.sqlite'));const a=s.create('external','one',inquiry);s.close();s=new IntakeStore(join(dir,'test.sqlite'));
 assert.deepEqual(s.create('external','one',inquiry),a);assert.throws(()=>s.create('external','one',{...inquiry,clientName:'Different'}),e=>e.status===409);assert.equal(s.feed().items.length,1);s.close();
 }finally{rmSync(dir,{recursive:true,force:true});}
});
test('Jamaica year rollover and exhaustion never reuse references',()=>{
 let now=new Date('2027-01-01T04:59:59Z');const s=new IntakeStore(':memory:',()=>now);
 assert.equal(s.create('form','1',inquiry).intakeReference,'2026-W-000001');now=new Date('2027-01-01T05:00:00Z');assert.equal(s.create('api','2',inquiry).intakeReference,'2027-W-000001');
 s.db.prepare("UPDATE counters SET value=999999 WHERE year=2027").run();assert.throws(()=>s.create('api','3',inquiry),e=>e.code==='reference_exhausted');s.close();
});
test('lost acknowledgments, changed rosters and old acknowledgments preserve newer revisions',()=>{
 const s=new IntakeStore(':memory:'),r=s.create('a','1',inquiry),first=s.feed(),row={name:'Subject',reference:'one',contactEmail:'p@example.invalid',pickupNotification:'None'};
 s.roster(r.id,'a','',1,[row]);s.ack(r.id,1,{reviewStatus:'AwaitingReview'});assert.equal(s.status(r.id,'a','').pendingChanges,true);
 const next=s.feed(first.nextCursor);assert.equal(next.items.length,1);assert.equal(next.items[0].revision,2);s.ack(r.id,2,{acknowledgmentStatus:'ProviderAccepted'});s.ack(r.id,1,{acknowledgmentStatus:'Queued'});assert.equal(s.status(r.id,'a','').acknowledgmentStatus,'ProviderAccepted');s.close();
});
test('invalid roster commit is atomic and missing contacts do not block an opted-out subject',()=>{
 const s=new IntakeStore(':memory:'),r=s.create('a','1',inquiry);s.roster(r.id,'a','',1,[{name:'Subject',reference:'one',pickupNotification:'None'}]);
 assert.throws(()=>s.roster(r.id,'a','',2,[{name:'Subject',reference:'one',pickupNotification:'Email'}]));assert.equal(s.feed().items.at(-1).revision,2);s.close();
});
test('verified financial events replay once and cannot be attached to a different intake',()=>{
 const booking={...inquiry,kind:'Booking',sessionType:'Portrait',startsAt:'2026-11-01T10:00:00-05:00',endsAt:'2026-11-01T11:00:00-05:00'};const s=new IntakeStore(':memory:'),a=s.create('a','1',booking),b=s.create('a','2',booking),event={eventId:'e1',provider:'TestAdapter',transactionReference:'capture-1',kind:'Deposit',amountMinor:10000,currency:'JMD'};
 s.payment(a.id,event);s.payment(a.id,event);assert.equal(s.feed().items.at(-1).paymentEvents.length,1);assert.throws(()=>s.payment(b.id,event),e=>e.code==='payment_conflict');assert.throws(()=>s.create('a','3',{...inquiry,paid:true}));s.close();
});
test('published contract example uses the same importer shape as the site change feed',()=>{
 const fixture=JSON.parse(readFileSync(new URL('../docs/public-site-v1/contract-example.json',import.meta.url))),s=new IntakeStore(':memory:');s.create('test-integration','fixture',fixture.submission);const actual=s.feed().items[0];for(const field of Object.keys(fixture.changeRecord))assert.ok(field in actual,field);assert.equal(actual.source,'Website');assert.equal(actual.roster[0].reference,'student-123');s.close();
});
