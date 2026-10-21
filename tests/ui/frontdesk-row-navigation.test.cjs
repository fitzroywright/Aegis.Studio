const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
function setup(){
 const dom=new JSDOM('<body data-studio-view="frontdesk"><main id="genericView"></main><span id="studioFooterRate"></span></body>',{url:'https://studio.test',runScripts:'outside-only',pretendToBeVisual:true});
 const w=dom.window,opened=[],navigations=[],errors=[];
 w.addEventListener('error',event=>{errors.push(event.error);event.preventDefault();});
 w.genericView=w.document.querySelector('main');w.esc=v=>String(v??'');w.money=String;
 w.openWorkspaceJob=async id=>opened.push(id);
 w.studioNavigate=async(view,options)=>{navigations.push({view,jobId:options.jobId});await w.openWorkspaceJob(options.jobId,{navigationRecorded:true,history:false});};
 w.json=async()=>[];
 for(const name of ['studio-status-order.js','studio-message-card.js'])w.eval(fs.readFileSync(path.join(root,name),'utf8'));
 w.eval(fs.readFileSync(path.join(root,'frontdesk.js'),'utf8')+"loadFrontDeskCommunications=()=>{};frontDeskJobs=[{id:'job-one',clientName:'A',title:'First job',status:'In Progress'},{id:'job-two',clientName:'B',title:'Second job',status:'Completed'}];frontDeskRequests=[{id:'booking-one',jobId:'job-one',status:'AwaitingDeposit',request:{clientName:'A',title:'Booking',requiredDeposit:10}}];window.setFrontDeskTestState=()=>{recordFrontDeskFulfillment=async()=>{};frontDeskJobs=[{id:'ready',title:'Ready',clientName:'A',status:'Ready for Delivery'}];frontDeskColumnFilters.status='Pickup / Delivery';renderFrontDesk();};renderFrontDesk();");
 return {w,opened,navigations,errors};
}
test('booking and completed queue rows open the correct job by mouse and keyboard',async()=>{
 const {w,opened,navigations,errors}=setup();try{
 w.document.querySelector('[data-fd-row-open="booking-one"] strong').click();
 const status=w.document.querySelector('[data-fd-filter="status"]');status.value='Completed';status.dispatchEvent(new w.Event('change'));
 w.document.querySelector('[data-fd-row-open="job-two"]').dispatchEvent(new w.KeyboardEvent('keydown',{key:'Enter',bubbles:true}));
 await Promise.resolve();await Promise.resolve();
 assert.deepEqual(opened,['job-one','job-two']);assert.deepEqual(navigations,[{view:'workspaces',jobId:'job-one'},{view:'workspaces',jobId:'job-two'}]);assert.deepEqual(errors,[]);
 }finally{w.close();}
});
test('Unassigned queue uses the same grid and row navigation without breaking return to intake',async()=>{
 const {w,opened,navigations,errors}=setup();try{
 const input=w.document.querySelector('#frontDeskSearch');input.focus();input.value='A';input.dispatchEvent(new w.Event('input'));
 w.document.querySelector('[data-fd-metric="unassigned"]').click();
 assert.deepEqual(errors,[]);
 assert.ok(w.document.querySelector('[data-fd-row-open="job-one"]'));assert.equal(w.document.querySelectorAll('.fd-table').length,2);assert.equal(w.document.querySelector('#frontDeskJobList'),null);
 w.document.querySelector('[data-fd-row-open="job-one"] strong').click();
 await Promise.resolve();await Promise.resolve();assert.deepEqual(opened,['job-one']);assert.deepEqual(navigations,[{view:'workspaces',jobId:'job-one'}]);
 w.document.querySelector('[data-fd-metric="unassigned"]').click();
 assert.deepEqual(errors,[]);
 assert.ok(w.document.querySelector('#frontDeskSearch'));assert.deepEqual(errors,[]);
 }finally{w.close();}
});
test('row actions do not also open the job and Front Desk keeps the shared rail/message card',async()=>{
 const {w,opened,errors}=setup();try{
 w.setFrontDeskTestState();
 w.document.querySelector('[data-frontdesk-fulfill]').click();await Promise.resolve();
 assert.deepEqual(opened,[]);assert.deepEqual(errors,[]);
 assert.ok(w.document.querySelector('.fd-secondary.mw-list-right [data-mw-region="schedule"].mw-list-panel'));
 assert.equal(w.document.querySelectorAll('#frontDeskMessageCard.smc-card').length,1);
 assert.equal(w.document.querySelectorAll('#frontDeskMessageCard [data-smc-notes-toggle]').length,1);
 }finally{w.close();}
});
