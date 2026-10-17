const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
test('status dropdown switches delivery and completed job queues and returns to intake',()=>{
 const dom=new JSDOM('<main id="genericView"></main><span id="studioFooterRate"></span>',{runScripts:'outside-only',url:'https://studio.test'});
 const w=dom.window;
 try{
 w.genericView=w.document.querySelector('main');
 w.esc=v=>String(v??'');w.money=String;
 w.StudioStatusOrder={sort:values=>values.sort()};
 w.StudioMessageCard={html:()=>'<section id="frontDeskMessageCard"><button data-messages-all></button><button data-message-compose></button></section>',bind(){}};
 w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/frontdesk.js'),'utf8')+
 "loadFrontDeskCommunications=()=>{};frontDeskJobs=[{id:'ready',title:'Ready job',clientName:'A',status:'Ready for Delivery'},{id:'published',title:'Published job',clientName:'B',status:'Published'},{id:'done',title:'Finished job',clientName:'C',status:'Completed'},{id:'open',title:'Open job',status:'In Progress'}];frontDeskRequests=[{id:'booking',status:'AwaitingDeposit',request:{clientName:'Applicant',title:'Intake',requiredDeposit:100}}];renderFrontDesk();");
 const select=value=>{const el=w.document.querySelector('[data-fd-filter="status"]');el.value=value;el.dispatchEvent(new w.Event('change'));};
 assert.equal(w.document.querySelector('[data-pickups]'),null);
 assert.equal(w.document.querySelector('[data-completed]'),null);
 assert.ok([...w.document.querySelector('[data-fd-filter="status"]').options].some(o=>o.value==='Completed'));
 const statusOptions=()=>[...w.document.querySelector('[data-fd-filter="status"]').options].map(o=>o.value);
 const allOptions=statusOptions();
 assert.ok(w.document.querySelector('.fd-overview.mw-list-panel'));
 assert.equal(w.document.querySelector('.fd-main > .fd-metrics'),null);
 assert.equal(w.document.querySelector('.fd-actions'),null);
 assert.equal(w.document.querySelectorAll('.fd-overview [data-fd-metric]').length,4);
 assert.equal(w.document.querySelectorAll('.fd-overview .workspace-summary-row').length,3);
 assert.deepEqual([...w.document.querySelectorAll('.fd-overview footer[role="toolbar"] button')].map(e=>e.textContent.trim().replace(/^[^A-Za-z]+/,'').replace(/\s+\d+$/,'')),['Unassigned','Create Manual Booking','Find Customer','View Receipt']);
 select('Pickup / Delivery');
 assert.deepEqual(statusOptions(),allOptions);
 assert.deepEqual([...w.document.querySelectorAll('[data-fd-row-open]')].map(e=>e.dataset.fdRowOpen),['ready','published']);
 assert.equal(w.document.querySelectorAll('[data-frontdesk-fulfill]').length,2);
 assert.equal(w.document.querySelector('#studioFooterRate').textContent,'2 Pickup / Delivery Jobs');
 select('Completed');
 assert.deepEqual(statusOptions(),allOptions);
 assert.deepEqual([...w.document.querySelectorAll('[data-fd-row-open]')].map(e=>e.dataset.fdRowOpen),['done']);
 assert.equal(w.document.querySelectorAll('[data-frontdesk-fulfill]').length,0);
 select('');
 assert.deepEqual([...w.document.querySelectorAll('[data-fd-row-open]')].map(e=>e.dataset.fdRowOpen),['booking']);
 }finally{w.close();}
});
