import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { IntakeStore } from './intake-store.mjs';
const inquiry={kind:'Inquiry',clientName:'Test Client',clientEmail:'test@example.invalid',notes:'Question'};
function seedCatalog(s){
 const catalog=s.publishBookingCatalog({sessionTypes:[{id:'portrait',name:'Portrait',description:'Studio portrait session',durationMinutes:60,setupBufferMinutes:15,cleanupBufferMinutes:15,priceMinor:12500,depositMinor:5000,currency:'JMD',locations:['BK','BE'],resourceRequirements:{Staff:1},active:true}]});
 const slots=['bk-1','bk-2','bk-3','be-1','be-2','be-3'].map((id,i)=>({id,locationCode:id.startsWith('be')?'BE':'BK',sessionTypeId:'portrait',startsAt:`2099-01-01T${String(14+i).padStart(2,'0')}:00:00Z`,endsAt:`2099-01-01T${String(15+i).padStart(2,'0')}:00:00Z`,catalogRevision:catalog.revision}));
 s.publishBookingAvailability({slots});return catalog.revision;
}
const booking=(revision,locationCode='BK',slotId='bk-1')=>({...inquiry,kind:'Booking',sessionType:'Portrait',sessionTypeId:'portrait',catalogRevision:revision,slotId,locationCode});
test('saved references and replay results survive restart, and conflict is rejected',()=>{
 const dir=mkdtempSync(join(tmpdir(),'studio-intake-'));try{
 let s=new IntakeStore(join(dir,'test.sqlite'));const a=s.create('external','one',inquiry);s.close();s=new IntakeStore(join(dir,'test.sqlite'));
 assert.deepEqual(s.create('external','one',inquiry),a);assert.throws(()=>s.create('external','one',{...inquiry,clientName:'Different'}),e=>e.status===409);assert.equal(s.feed().items.length,1);s.close();
 }finally{rmSync(dir,{recursive:true,force:true});}
});
test('intake source label comes from the server route or provisioned integration credential',()=>{
 const s=new IntakeStore(':memory:');s.create('browser','web-site',inquiry,'Form');const workday=s.createApiCredential('Workday Mobile',['intake:submit'],'Workday Mobile');s.create(workday.id,'workday-1',{...inquiry,clientApplication:'Web site'},'Api',workday.clientApplication);const changes=s.feed().items;assert.equal(changes[0].source,'Web site');assert.equal(changes[1].source,'Workday Mobile');s.close();
});
test('Jamaica year rollover and exhaustion never reuse references',()=>{
 let now=new Date('2027-01-01T04:59:59Z');const s=new IntakeStore(':memory:',()=>now);
 assert.equal(s.create('form','1',inquiry).intakeReference,'2026-W-000001');now=new Date('2027-01-01T05:00:00Z');assert.equal(s.create('api','2',inquiry).intakeReference,'2027-W-000001');
 s.db.prepare("UPDATE counters SET value=999999 WHERE year=2027").run();assert.throws(()=>s.create('api','3',inquiry),e=>e.code==='reference_exhausted');s.close();
});
test('booking references use the yearly site sequence, remain idempotent, and are distinct from inquiry references',()=>{
 let now=new Date('2027-01-01T04:59:59Z');const s=new IntakeStore(':memory:',()=>now),revision=seedCatalog(s);
 const firstBooking=booking(revision,'BK','bk-1'),first=s.create('form','booking-1',firstBooking);assert.equal(first.intakeReference,'2026-BK-000001');assert.equal(s.create('form','booking-1',firstBooking).intakeReference,'2026-BK-000001');
 now=new Date('2027-01-01T05:00:00Z');assert.equal(s.create('api','booking-2',booking(revision,'BK','bk-2')).intakeReference,'2027-BK-000001');assert.equal(s.create('api','booking-3',booking(revision,'BE','be-1')).intakeReference,'2027-BE-000001');assert.equal(s.create('api','booking-4',booking(revision,'BE','be-2')).intakeReference,'2027-BE-000002');assert.equal(s.create('api','inquiry-2',inquiry).intakeReference,'2027-W-000001');assert.throws(()=>s.create('api','bad-location',booking(revision,'XX','bk-3')),e=>e.code==='invalid_booking');s.close();
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
 const s=new IntakeStore(':memory:'),revision=seedCatalog(s),a=s.create('a','1',booking(revision,'BK','bk-1')),b=s.create('a','2',booking(revision,'BK','bk-2')),event={eventId:'e1',provider:'TestAdapter',transactionReference:'capture-1',kind:'Deposit',amountMinor:10000,currency:'JMD'};
 s.payment(a.id,event);s.payment(a.id,event);assert.equal(s.feed().items.at(-1).paymentEvents.length,1);assert.throws(()=>s.payment(b.id,event),e=>e.code==='payment_conflict');assert.throws(()=>s.create('a','3',{...inquiry,paid:true}));s.close();
});
test('published contract example uses the same importer shape as the site change feed',()=>{
 const fixture=JSON.parse(readFileSync(new URL('../docs/public-site-v1/contract-example.json',import.meta.url))),s=new IntakeStore(':memory:');seedCatalog(s);s.create('test-integration','fixture',{...fixture.submission,clientApplication:'Website'});const actual=s.feed().items[0];for(const field of Object.keys(fixture.changeRecord))assert.ok(field in actual,field);assert.equal(actual.source,'API');assert.equal(actual.pricingSnapshot.depositMinor,25000);assert.equal(actual.roster[0].reference,'student-123');s.close();
});
test('booking slot reservation is atomic and replay does not reserve twice',()=>{
 const s=new IntakeStore(':memory:'),revision=seedCatalog(s),first=booking(revision,'BK','bk-1');const created=s.create('site','same-slot',first);assert.deepEqual(s.create('site','same-slot',first),created);assert.equal(created.pricingSnapshot.setupBufferMinutes,15);assert.equal(created.pricingSnapshot.cleanupBufferMinutes,15);assert.throws(()=>s.create('other','other-key',first),e=>e.code==='slot_unavailable');assert.equal(s.getBookingAvailability({from:'2099-01-01',to:'2099-01-01',locationCode:'BK',sessionTypeId:'portrait'}).slots.some(x=>x.id==='bk-1'),false);s.ack(created.id,1,{bookingStatus:'Cancelled'});assert.equal(s.getBookingAvailability({from:'2099-01-01',to:'2099-01-01',locationCode:'BK',sessionTypeId:'portrait'}).slots.some(x=>x.id==='bk-1'),true);s.close();
});

