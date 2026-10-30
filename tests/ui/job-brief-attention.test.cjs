const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot'),read=name=>fs.readFileSync(path.join(root,name),'utf8'),settle=()=>new Promise(resolve=>setImmediate(resolve));
function environment(t){const dom=new JSDOM('<body><section id="brief"></section></body>',{url:'https://studio.test',runScripts:'outside-only'});t.after(()=>dom.window.close());const w=dom.window;w.eval(read('studio-page.js'));w.eval(read('job-brief.js'));w.eval(read('studio-attention.js'));return w;}
test('attention includes other staff work and individual QC, bookings and delivery issues, excluding completed work',t=>{
 const w=environment(t),jobs=[{id:'j',title:'School',status:'Active Production'},{id:'closed',status:'Closed'}],tasks=[{id:'a',jobId:'j',title:'Retouch portraits',assignedTo:'other.staff',status:'Blocked',blockedReason:'Missing originals'},{id:'b',jobId:'j',title:'Finished',status:'Completed'},{id:'c',jobId:'closed',title:'Closed job task',status:'Blocked'}];
 const items=w.StudioAttention.project(jobs,tasks,{activities:[...tasks,{id:'qc',jobId:'j',title:'Check prints',status:'Completed'}],items:[{activityId:'qc',awaitingQc:3}]},{items:[{kind:'Conversation',conversationId:'c',title:'Reply failed',summary:'Delivery rejected'}]});
 assert.deepEqual(Array.from(items,item=>item.id),['task:a','task:qc','delivery:Conversation:c']);assert.match(items[0].context,/other.staff.*Missing originals/);assert.match(items[1].context,/quality control/);assert.equal(items.filter(item=>item.id==='task:a').length,1);
});
test('Job Details displays saved booking instructions, job notes and both attachment sources without duplicates',async t=>{
 const w=environment(t),calls=[];let failSave=true;w.json=async(url,options)=>{calls.push({url,options});if(options){if(failSave){failSave=false;throw Error('Connection lost');}return null;}if(url.endsWith('/booking-instructions'))return [{source:'Booking instructions',body:'Wheelchair access <required>'}];if(url.endsWith('/notes'))return [{body:'Wheelchair access <required>'},{body:'Use blue backdrop'}];if(url.endsWith('/access'))return {permissions:{editJob:true,addAttachments:true}};if(url==='/api/messages/threads')return [{id:'thread',scopeType:'Job',scopeId:'j'},{id:'other',scopeType:'Job',scopeId:'other'}];if(url==='/api/conversations/c/attachments')return [{id:'f',fileName:'Brief.pdf'}];if(url==='/api/messages/threads/thread/attachments')return [{id:'t',fileName:'Shot list.xlsx'}];throw Error(url);};
 const host=w.document.getElementById('brief');await w.StudioJobBrief.mount({id:'j',conversationId:'c'},host,()=>true);
 assert.match(host.textContent,/Wheelchair access <required>.*Use blue backdrop/s);assert.equal(host.querySelector('required'),null);assert.equal(host.querySelectorAll('[data-brief-instructions] article').length,2);
 assert.equal(host.querySelectorAll('.jd-brief-file').length,2);assert.match(host.querySelectorAll('.jd-brief-file')[0].href,/\/api\/attachments\/f$/);assert.match(host.querySelectorAll('.jd-brief-file')[1].href,/\/api\/message-attachments\/t$/);assert.equal(calls.some(call=>call.url.includes('other/attachments')),false);
 const form=host.querySelector('form');form.elements.body.value='Photograph siblings together';await form.onsubmit({preventDefault(){}});assert.match(form.textContent,/Connection lost/);assert.equal(form.elements.body.value,'Photograph siblings together');await form.onsubmit({preventDefault(){}});const posts=calls.filter(call=>call.options?.method==='POST');assert.equal(JSON.parse(posts[0].options.body).id,JSON.parse(posts[1].options.body).id);const save=calls.find(call=>call.options?.method==='POST');assert.equal(save.url,'/api/jobs/j/notes');assert.equal(JSON.parse(save.options.body).body,'Photograph siblings together');
});
test('restricted or failed instructions and files are explicit, and stale reads cannot update another job',async t=>{
 const w=environment(t),host=w.document.getElementById('brief');w.json=async()=>{throw Error('Restricted');};await w.StudioJobBrief.mount({id:'j',conversationId:'c'},host,()=>true);assert.match(host.textContent,/instructions could not be loaded/);assert.match(host.textContent,/files are unavailable or restricted/);assert.equal(host.querySelector('form,input[type=file]'),null);
 let release;const deferred=new Promise(resolve=>release=resolve);w.json=()=>deferred;let live=true;const pending=w.StudioJobBrief.mount({id:'old'},host,()=>live);live=false;host.innerHTML='Next job';release([]);await pending;assert.equal(host.textContent,'Next job');
});
test('shared drawer adds global tasks on pages whose own attention area is empty',async t=>{
 const dom=new JSDOM(read('index.html'),{url:'https://studio.test',runScripts:'outside-only'});t.after(()=>dom.window.close());const w=dom.window;
 w.workspaceCache=[];w.fetch=async()=>({ok:true,json:async()=>[]});w.json=async url=>url==='/api/today/jobs'?[{id:'j',title:'School',status:'Active Production'}]:url.includes('/operations/activities')?[{id:'t',jobId:'j',title:'Retouch portraits',assignedTo:'someone.else',status:'Blocked'}]:url==='/api/production/workload'?{}:url==='/api/messages/attention'?{items:[]}:[];
 w.studioNavigate=async()=>{};w.eval(read('studio-attention.js'));w.eval(read('studio-shell-nav.js'));w.document.dispatchEvent(new w.Event('DOMContentLoaded'));await settle();await settle();
 for(const view of ['production','clients','schedule']){w.document.body.dataset.studioView=view;w.StudioShell.sync(view);assert.match(w.document.getElementById('studioAttentionItems').textContent,/Retouch portraits/);assert.equal(w.document.getElementById('studioAttentionCount').textContent,'1');}
});
test('booking instructions endpoint scopes both stores to the authorized job',()=>{
 const source=fs.readFileSync(path.resolve(root,'../WorkspaceJobs/WorkspaceJobStore.cs'),'utf8');assert.match(source,/booking-instructions[^\n]*HasPermission\(user,jobId,"ViewJob"\)/);assert.match(source,/studio_frontdesk_requests where job_id=\$1/);assert.match(source,/studio_bookings where job_id=\$1/);
 assert.match(read('frontdesk.js'),/Special instructions<textarea name="notes"/);
});
test('exception attention projects gallery requests and overdue invoices separately from canonical next actions',t=>{
 const w=environment(t);const items=w.StudioAttention.project([],[],{},{},Date.now(),[{conversationId:'g',request:{clientName:'Campbell'},status:'ReadyToPublish'}],[{id:'i',invoiceNumber:'INV-9',status:'Overdue'},{id:'paid',status:'Paid'}]);
 assert.deepEqual(Array.from(items,item=>item.id),['gallery-request:g','invoice:i']);assert.match(items[0].title,/Review gallery request/);assert.match(items[1].title,/INV-9/);
 assert.doesNotMatch(read('management.js'),/attention:StudioPage.metrics/);assert.doesNotMatch(read('scheduling.js'),/No pipeline issues require attention/);
});

