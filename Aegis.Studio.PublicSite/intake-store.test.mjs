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
 const fixture=JSON.parse(readFileSync(new URL('../docs/public-site-v1/contract-example.json',import.meta.url))),s=new IntakeStore(':memory:');seedCatalog(s);s.create('test-integration','fixture',{...fixture.submission,clientApplication:'Website'});const actual=s.feed().items[0];for(const field of Object.keys(fixture.changeRecord))assert.ok(field in actual,field);assert.equal(actual.source,'API');assert.equal(actual.pricingSnapshot.depositMinor,fixture.submission.pricingSnapshot.depositMinor);assert.equal(actual.roster[0].reference,'student-123');s.close();
});
test('booking slot reservation is atomic and replay does not reserve twice',()=>{
 const s=new IntakeStore(':memory:'),revision=seedCatalog(s),first=booking(revision,'BK','bk-1');const created=s.create('site','same-slot',first);assert.deepEqual(s.create('site','same-slot',first),created);const imported=s.feed().items[0];assert.equal(imported.pricingSnapshot.setupBufferMinutes,15);assert.equal(imported.pricingSnapshot.cleanupBufferMinutes,15);assert.throws(()=>s.create('other','other-key',first),e=>e.code==='slot_unavailable');assert.equal(s.getBookingAvailability({from:'2099-01-01',to:'2099-01-01',locationCode:'BK',sessionTypeId:'portrait'}).slots.some(x=>x.id==='bk-1'),false);s.ack(created.id,1,{bookingStatus:'Cancelled'});assert.equal(s.getBookingAvailability({from:'2099-01-01',to:'2099-01-01',locationCode:'BK',sessionTypeId:'portrait'}).slots.some(x=>x.id==='bk-1'),true);s.close();
});
test('client gallery proofs keep only a hash, expire, record provider, and revoke only while pending',()=>{
 let now=new Date('2026-10-24T12:00:00Z');const s=new IntakeStore(':memory:',()=>now),client='22222222-2222-4222-8222-222222222222';
 const proof=s.createClientLinkChallenge(client);const stored=s.db.prepare('SELECT * FROM client_link_challenges WHERE id=?').get(proof.id);
 assert.notEqual(stored.code_hash,proof.code);assert.equal('code_hash' in s.clientLinkProof(proof.id),false);assert.equal(s.clientLinkProof(proof.id).status,'Pending');
 const linked=s.redeemClientLinkChallenge(proof.code,'Google','stable-provider-subject');assert.equal(linked.clientId,client);assert.equal(s.clientLinkProof(proof.id).status,'Used');assert.equal(s.clientLinkProof(proof.id).provider,'Google');assert.equal(s.revokeClientLinkProof(proof.id),false);
 const expiring=s.createClientLinkChallenge(client);now=new Date(now.getTime()+16*60000);assert.equal(s.clientLinkProof(expiring.id).status,'Expired');assert.equal(s.revokeClientLinkProof(expiring.id),false);s.close();
});

test('gallery staging retries preserve publication, media, and revocation and reject changed metadata',()=>{
 const s=new IntakeStore(':memory:'),g={id:'11111111-1111-4111-8111-111111111111',clientId:'22222222-2222-4222-8222-222222222222',title:'Gallery',finalDeliveryAvailable:true,items:[{versionId:'33333333-3333-4333-8333-333333333333',fileName:'Portrait.jpg'}]};
 s.stageGallery(g);s.db.prepare('INSERT INTO media VALUES(?,?,?,?)').run(g.id,g.items[0].versionId,'image/jpeg',Buffer.from('photo'));
 const state=published=>s.db.prepare('UPDATE galleries SET payload=? WHERE id=?').run(JSON.stringify({...g,published}),g.id);
 state(true);assert.deepEqual(s.stageGallery(g).storedVersionIds,[g.items[0].versionId]);assert.equal(JSON.parse(s.db.prepare('SELECT payload FROM galleries').get().payload).published,true);
 state(false);s.stageGallery(g);assert.equal(JSON.parse(s.db.prepare('SELECT payload FROM galleries').get().payload).published,false);
 assert.throws(()=>s.stageGallery({...g,title:'Changed'}),e=>e.code==='gallery_conflict');assert.throws(()=>s.stageGallery({...g,clientId:'44444444-4444-4444-8444-444444444444'}),e=>e.status===409);s.close();
});

test('immutable selection snapshots survive restart, lost responses, later edits and repeated acknowledgements',()=>{
 const dir=mkdtempSync(join(tmpdir(),'studio-selections-')),path=join(dir,'test.sqlite');let s;
 try{
  s=new IntakeStore(path);const g={id:'11111111-1111-4111-8111-111111111111',clientId:'22222222-2222-4222-8222-222222222222',title:'Gallery',items:[{versionId:'33333333-3333-4333-8333-333333333333',fileName:'Portrait.jpg'}]};s.stageGallery(g);
  s.db.prepare('UPDATE galleries SET payload=?').run(JSON.stringify({...g,published:true}));
  assert.throws(()=>s.submitGallerySelections('identity','other',g.id,'wrong-client'),e=>e.status===404);
  assert.throws(()=>s.submitGallerySelections('identity',g.clientId,g.id,'empty'),e=>e.code==='selection_required');
  s.db.prepare('INSERT INTO selections VALUES(?,?,1)').run(g.id,g.items[0].versionId);
  const receipt=s.submitGallerySelections('identity',g.clientId,g.id,'one');s.close();s=new IntakeStore(path);
  s.db.prepare('UPDATE selections SET selected=0').run();
  assert.deepEqual(s.submitGallerySelections('identity',g.clientId,g.id,'one'),receipt);assert.deepEqual(s.gallerySelectionFeed().items,[receipt]);
  s.acknowledgeGallerySelections(receipt.reference);s.acknowledgeGallerySelections(receipt.reference);assert.deepEqual(s.gallerySelectionFeed().items,[]);
  s.close();s=new IntakeStore(path);assert.deepEqual(s.gallerySelectionFeed().items,[]);assert.deepEqual(s.submitGallerySelections('identity',g.clientId,g.id,'one'),receipt);
  assert.throws(()=>s.acknowledgeGallerySelections('missing'),e=>e.status===404);
 }finally{s?.close();rmSync(dir,{recursive:true,force:true});}
});


test('selection pagination advances past unacknowledged snapshots without losing retry visibility',()=>{
 const s=new IntakeStore(':memory:');try{
  for(let i=1;i<=51;i++)s.db.prepare('INSERT INTO gallery_selection_submissions(id,payload) VALUES(?,?)').run(String(i),JSON.stringify({reference:String(i)}));
  const first=s.gallerySelectionFeed();assert.equal(first.items.length,50);assert.equal(first.hasMore,true);
  const second=s.gallerySelectionFeed(first.nextCursor);assert.deepEqual(second.items,[{reference:'51'}]);assert.equal(second.hasMore,false);
  assert.equal(s.gallerySelectionFeed().items[0].reference,'1');assert.throws(()=>s.gallerySelectionFeed('invalid'),e=>e.status===400);
 }finally{s.close();}
});
