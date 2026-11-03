const {test}=require('node:test'),assert=require('node:assert/strict'),setup=require('./frontdesk-reception-fixture.cjs');
function intake(fixture){fixture.intake=fixture.jobs.map((job,i)=>({jobId:job.id,conversationId:'cv-'+job.id,receivedAtUtc:`2026-11-0${i+1}T15:20:30Z`,source:['Website','Walk-In','Phone','Staff Entry'][i]}));fixture.intake.push({conversationId:'inquiry',jobId:null,receivedAtUtc:'2026-10-28T12:00:00Z',source:'Email'});}
function change(w,selector,value){const input=w.document.querySelector(selector);input.value=value;input.dispatchEvent(new w.Event('change'));}
test('all tabs use the required column order, source labels, received date and full timestamp',async t=>{
 const {w,fixture,render}=setup(t);intake(fixture);fixture.intake[0].receivedAtUtc='2026-11-03T03:20:30Z';await render();
 assert.deepEqual([...w.document.querySelectorAll('thead th')].map(x=>x.textContent),['Reference','Client / enquiry','Date Received','Source','Status','Next required action','Responsible','Due']);
 const row=w.document.querySelector('[data-fd-row-open="booking-expected"]');assert.equal(row.querySelector('time').textContent,'02 Nov 2026');assert.match(row.querySelector('time').title,/22:20:30/);assert.equal(row.querySelector('.fd-source').textContent,'Website');
 const choices=[...w.document.querySelector('[data-fd-source]').options].map(x=>x.textContent);assert.deepEqual(choices,['All Sources','Website','Walk-In','Phone','Email','Staff Entry']);
 w.document.querySelector('[data-fd-tab="enquiries"]').click();assert.equal(w.document.querySelector('.fd-source').textContent,'Email');assert.ok(w.document.querySelector('time'));w.document.querySelector('[data-fd-tab="all"]').click();assert.equal(w.document.querySelectorAll('tbody tr').length,5);
});
test('unknown metadata displays dashes without inventing a received or appointment date',async t=>{
 const {w,fixture,render}=setup(t);intake(fixture);fixture.intake[0].source='Unknown';fixture.intake[0].receivedAtUtc=null;await render();const row=w.document.querySelector('[data-fd-row-open="booking-expected"]');assert.equal(row.querySelector('.fd-received').textContent,'—');assert.equal(row.querySelector('.fd-source').textContent,'—');assert.equal(w.document.querySelector('select option[value="Unknown"]'),null);
});
test('received date sorts globally in both directions and preserves explicit name order',async t=>{
 const {w,fixture,render,settle}=setup(t);intake(fixture);await render();assert.equal(w.document.querySelector('tbody tr').dataset.fdRowOpen,'booking-ready');
 w.document.querySelector('[data-fd-received-sort]').click();assert.equal(w.document.querySelector('tbody tr').dataset.fdRowOpen,'booking-expected');assert.equal(w.document.querySelector('th[aria-sort]').getAttribute('aria-sort'),'ascending');
 change(w,'[data-fd-order]','name');assert.match(w.document.querySelector('tbody tr').textContent,/Alicia/);
 change(w,'[data-fd-source]','Website');await settle();assert.equal(w.document.querySelector('[data-fd-order]').value,'name');assert.equal(w.document.querySelectorAll('[data-fd-row-open]').length,1);
});
test('source and custom received range combine with search, Responsible, Due and tabs',async t=>{
 const {w,fixture,render,settle}=setup(t);intake(fixture);
 w.StudioNextActions={mount:()=>{},load:async()=>{},peek:(kind,id)=>({assignedTo:id==='expected'?'Alex':'Other',dueAtUtc:'2020-01-01T00:00:00Z',action:{title:'Call client',reason:'Confirm details'}}),due:()=>({overdue:true,label:'Overdue'})};
 await render();change(w,'[data-fd-source]','Website');await settle();change(w,'[data-fd-received]','custom');change(w,'[data-fd-received-from]','2026-11-01');change(w,'[data-fd-received-to]','2026-11-01');await settle();
 change(w,'[data-fd-action-owner]','Alex');change(w,'[data-fd-action-due]','overdue');const search=w.document.querySelector('#frontDeskSearch');search.value='Alicia';search.dispatchEvent(new w.Event('input'));
 assert.equal(w.document.querySelectorAll('[data-fd-row-open]').length,1);assert.match(w.document.querySelector('.fd-pagination').textContent,/of 1 records/);assert.ok(fixture.calls.some(c=>c.url.includes('received=custom')&&c.url.includes('source=Website')&&c.url.includes('from=2026-11-01')));
 w.document.querySelector('[data-fd-tab="enquiries"]').click();assert.equal(w.document.querySelectorAll('[data-fd-row-open]').length,0);w.document.querySelector('[data-fd-tab="all"]').click();assert.equal(w.document.querySelectorAll('[data-fd-row-open]').length,1);
});
test('received sorting happens before pagination and filters reset the page and count',async t=>{
 const {w,fixture,render,settle}=setup(t);fixture.jobs=Array.from({length:65},(_,i)=>({id:'job-'+i,clientName:'Client '+i,title:'Job '+i,status:'Scheduled',eventDate:'2026-10-29',workspaceId:'w'}));fixture.requests=[];fixture.enquiries=[];fixture.intake=fixture.jobs.map((j,i)=>({jobId:j.id,conversationId:'c-'+i,source:i===64?'Phone':'Website',receivedAtUtc:new Date(Date.UTC(2020,0,1+i)).toISOString()}));
 await render();assert.equal(w.document.querySelectorAll('[data-fd-row-open]').length,50);assert.equal(w.document.querySelector('tbody tr').dataset.fdRowOpen,'job:job-64');assert.match(w.document.querySelector('.fd-pagination').textContent,/of 65 records/);
 w.document.querySelector('[data-fd-page="2"]').click();assert.equal(w.document.querySelectorAll('[data-fd-row-open]').length,15);assert.equal(w.document.querySelector('tbody tr').dataset.fdRowOpen,'job:job-14');change(w,'[data-fd-source]','Phone');await settle();assert.equal(w.document.querySelectorAll('[data-fd-row-open]').length,1);assert.match(w.document.querySelector('.fd-pagination').textContent,/Page 1 of 1/);
});
test('direct staff booking offers only supported sources and defaults to Staff Entry',async t=>{
 const {w,render}=setup(t);await render();w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.json=async()=>[];await w.openPendingBookingForm(null,null);const select=w.document.querySelector('dialog select[name="source"]');assert.deepEqual([...select.options].map(x=>x.value),['Website','Walk-In','Phone','Email','Staff Entry']);assert.equal(select.value,'Staff Entry');
});
