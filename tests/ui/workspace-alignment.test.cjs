const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
function environment(){
 const dom=new JSDOM('<div id="panel"></div>',{runScripts:'outside-only',url:'https://studio.test'});
 const w=dom.window;w.showWorkspaces=async()=>{};
 w.esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 w.eval(fs.readFileSync(path.join(root,'job-workspace.js'),'utf8'));
 return {w,panel:w.document.getElementById('panel')};
}
test('single subject is a card; multiple subjects form a roster; viewers cannot edit',async()=>{
 const {w,panel}=environment();let subjects=[{id:'a',name:'Morgan <script>',reference:'001',notes:'Portrait'}];
 w.json=async url=>url.endsWith('/access')?{permissions:{editJob:false}}:subjects;
 await w.renderJobSubjects({id:'job'},panel,()=>true,()=>{});
 assert.ok(panel.querySelector('.subject-card'));assert.equal(panel.querySelector('script'),null);assert.equal(panel.querySelector('form'),null);
 subjects.push({id:'b',name:'Alex',reference:'002',notes:''});
 await w.renderJobSubjects({id:'job'},panel,()=>true,()=>{});
 assert.equal(panel.querySelectorAll('tbody tr').length,2);assert.equal(panel.querySelector('.subject-card'),null);
});
test('task list and calendar retain the same scheduled and unscheduled records',async()=>{
 const {w,panel}=environment();let calls=0;const tasks=[{id:'a',title:'Retouch portraits',scheduledAtUtc:new Date().toISOString(),status:'NotStarted'},{id:'b',title:'Await prints',status:'Blocked'}];
 w.json=async url=>{if(url.endsWith('/access'))return {permissions:{manageProduction:false}};calls++;return tasks;};
 await w.renderJobTasks({id:'job'},panel,()=>true,()=>{});
 assert.equal(panel.querySelectorAll('.task-card').length,2);
 panel.querySelector('[data-task-view="calendar"]').click();
 assert.equal(panel.querySelectorAll('.task-card').length,2);assert.match(panel.textContent,/Retouch portraits/);assert.match(panel.textContent,/Await prints/);assert.equal(calls,1);
 panel.querySelector('[data-task-view="list"]').click();assert.equal(panel.querySelectorAll('.task-card').length,2);
});
test('announcement submission uses a dedicated workspace endpoint, never a message thread',async()=>{
 const {w,panel}=environment();const calls=[];w.currentWorkspacePermissions={notifyMembers:true};
 w.json=async(url,options)=>{calls.push({url,options});return options?{id:'a'}:[];};
 await w.renderWorkspaceAnnouncements(panel,'workspace',()=>true);
 const form=panel.querySelector('form');form.elements.title.value='Tomorrow';form.elements.body.value='Team briefing';
 await form.onsubmit({preventDefault(){}});
 const post=calls.find(c=>c.options);assert.equal(post.url,'/api/workspaces/workspace/announcements');assert.deepEqual(JSON.parse(post.options.body),{title:'Tomorrow',body:'Team briefing'});
});
test('gallery collection filtering and provenance use job-scoped versions',async()=>{
 const {w,panel}=environment();w.json=async url=>url.endsWith('/access')?{permissions:{manageProduction:false}}:[{assetKey:'one',id:'v1',versionNumber:2,fileName:'Portrait.jpg',collection:'Portraits'},{assetKey:'two',id:'v2',versionNumber:1,fileName:'Group.jpg',collection:'Groups'}];
 await w.renderVersionedGallery({id:'job'},panel,()=>true);
 assert.equal(panel.querySelectorAll('.job-file-tile').length,2);assert.equal(panel.querySelector('form'),null);
 panel.querySelector('select').value='Groups';panel.querySelector('select').onchange();assert.equal(panel.querySelectorAll('.job-file-tile').length,1);assert.match(panel.querySelector('img').src,/\/api\/jobs\/job\/versions\/by-id\/v2\/content$/);
});
test('stale asynchronous subjects response does not overwrite the next tab',async()=>{
 const {w,panel}=environment();panel.textContent='Next tab';w.json=async url=>url.endsWith('/access')?{permissions:{}}:[];
 await w.renderJobSubjects({id:'job'},panel,()=>false,()=>{});assert.equal(panel.textContent,'Next tab');
});
test('workspace job renders four lower tabs with details in the job list',async()=>{
 const html=fs.readFileSync(path.join(root,'index.html'),'utf8');const dom=new JSDOM(html,{runScripts:'outside-only',url:'https://studio.test'});const w=dom.window;
 const job={id:'job',workspaceId:'workspace',conversationId:'conversation',title:'Portraits',clientName:'Client',status:'Scheduled'};
 Object.assign(w,{showWorkspaces:async()=>{},workspaceCache:[{id:'workspace',name:'Portraits',permissions:{accessWorkspace:true,view:true}}],selectedWorkspace:'workspace',currentWorkspacePermissions:{},selectedConversation:null,activateView(){},activateNavigation(){},openConversation:async()=>{},formatBytes:()=>'',openJob:async()=>{},esc:s=>String(s??''),alert(){}});
 for(const id of ['conversationList','detailPane','workspaceTitle','allButton'])w[id]=w.document.getElementById(id);
 w.fetch=async url=>({ok:true,json:async()=>url.endsWith('/sending-identities')?{identities:[{type:'Workspace'},{type:'Self',isDefault:true}]}:{channel:'Email',messages:[]}});
 w.json=async url=>url==='/api/jobs'?[job]:url==='/api/jobs/job'?job:url.includes('/threads/Job/')?{id:'thread'}:url==='/api/auth/me'?{user:{userName:'staff',displayName:'Staff'}}:url.endsWith('/access')?{permissions:{}}:[];
 w.eval(fs.readFileSync(path.join(root,'job-handoff.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'job-workspace.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'workspace-outlook.js'),'utf8'));
 await w.openWorkspaceJob('job');await new Promise(r=>setTimeout(r,10));
 assert.equal(w.document.querySelector('#jobInternalNote'),null);
 assert.equal(w.document.querySelector('#jobSendingIdentity').value,'Workspace');
 assert.equal(w.document.querySelector('#hierarchyCompose button').textContent,'Reply');
 const requests=[];const originalJson=w.json;w.json=async(url,options)=>{if(options?.body)requests.push({url,body:JSON.parse(options.body)});return originalJson(url,options);};
 for(const mode of ['Workspace','Internal','Self']) {
   const form=w.document.querySelector('#hierarchyCompose');const sender=w.document.querySelector('#jobSendingIdentity');sender.value=mode;sender.onchange();form.querySelector('textarea').value='Test reply';
   await form.onsubmit({preventDefault(){},currentTarget:form});
 }
 assert.equal(requests[0].url,'/api/conversations/conversation/messages');assert.equal(requests[0].body.sendingIdentity,'Workspace');
 assert.equal(requests[1].url,'/api/messages/threads/thread');assert.deepEqual(requests[1].body,{body:'Test reply'});
 assert.equal(requests[2].body.sendingIdentity,'Self');
 assert.equal(w.messageFirstName('fwright',[{userName:'fwright',displayName:'Fitzroy Wright'}],'Team member',true),'Fitzroy');
 assert.equal(w.messageFirstName('fwright',[],'Team member',true),'Team member');
 assert.equal(w.messageFirstName('fwright',[{userName:'fwright',displayName:'fwright'}],'Team member',true),'Team member');
 assert.deepEqual(Array.from(w.document.querySelectorAll('[data-job-tab]'),b=>b.textContent),['Gallery','Subjects','Tasks / Calendar','Activity']);
 assert.equal(w.document.querySelector('details.job-details-card'),null);assert.match(w.document.querySelector('[data-job="job"]').textContent,/Assigned to: Unassigned/);assert.match(w.document.querySelector('[data-job="job"]').textContent,/Event: Not scheduled/);assert.equal(w.document.querySelector('[data-tab="enquiries"]'),null);assert.equal(w.document.querySelector('[data-tab="team"]'),null);assert.equal(w.document.querySelector('[data-tab="announcements"]'),null);
});
test('top navigation keeps personal actions right and removes redundant destinations',async()=>{
 const dom=new JSDOM('<header class="topbar"></header><aside class="rail"></aside><button id="newRequestButton">New Request</button>',{runScripts:'outside-only',url:'https://studio.test'});const w=dom.window;let frontDesk=0;
 Object.assign(w,{loadFrontDeskRole(){},showFrontDesk:async()=>frontDesk++,showProduction(){},showWorkspaces(){},showManagement(){}});
 w.eval(fs.readFileSync(path.join(root,'studio-navigation.js'),'utf8'));
 assert.deepEqual(Array.from(w.document.querySelectorAll('.studio-top-nav button'),b=>b.textContent),['Dashboard','Pipeline','Production','FrontDesk','My Work','New Request','Admin']);
 w.document.querySelector('[data-view="frontdesk"]').click();await Promise.resolve();assert.equal(frontDesk,1);assert.equal(w.document.getElementById('newRequestButton').hidden,false);
});
test('announcement banner disappears only after dismissal succeeds',async()=>{
 const {w,panel}=environment();const calls=[];let fail=true;
 w.json=async(url,options)=>{calls.push(url);if(options){if(fail)throw Error('Offline');return null;}return [{id:'notice',title:'Photo day',body:'Bring equipment'}];};
 await w.renderAnnouncementBanners(panel,'workspace',()=>true);
 assert.equal(panel.hidden,false);let button=panel.querySelector('[data-dismiss]');
 await button.onclick();assert.ok(panel.querySelector('article'));assert.equal(button.disabled,false);
 fail=false;await button.onclick();assert.equal(panel.hidden,true);
 assert.equal(calls.at(-1),'/api/workspaces/workspace/announcements/notice/dismiss');
});
test('job handoff uses shoot completion, never first upload, and shows blocked work',()=>{
 const {w}=environment();w.eval(fs.readFileSync(path.join(root,'job-handoff.js'),'utf8'));
 const job={status:'Photography'};
 let summary=w.jobHandoffSummary(job,[{checkedOutAtUtc:null}],[],[{id:'image'}]);
 assert.equal(summary.finishedCount,0);assert.equal(summary.next,'Complete the scheduled shoot');
 summary=w.jobHandoffSummary(job,[{checkedOutAtUtc:'2026-09-29T17:00:00Z'}],[{status:'Blocked'}],[{id:'image'}]);
 assert.equal(summary.blockedTasks,1);assert.equal(summary.next,'Resolve blocked tasks');
 assert.equal(w.jobHandoffSummary({status:'Completed'},[],[],[]).next,'Completed');
});
test('FrontDesk search preserves focus and separates enquiry and booking queues',()=>{
 const {w,panel}=environment();panel.id='genericView';
 Object.assign(w,{activateView(){},activateNavigation(){},conversationView:{classList:{add(){}}},genericView:panel,money:n=>'JMD '+n,openWorkspaceJob(){},openPos(){},json:async()=>({frontDesk:true,user:{isAdministrator:true}})});
 w.eval(fs.readFileSync(path.join(root,'frontdesk.js'),'utf8')+"\nfrontDeskJobs=[{id:'job',clientName:'Campbell',title:'Wedding',status:'Needs Scheduling'}];frontDeskRequests=[{id:'request',request:{clientName:'Campbell',title:'Wedding',eventDate:'2026-10-10',requiredDeposit:100,workspaceId:'workspace'},recordedDeposit:40,status:'AwaitingDeposit'}];frontDeskWorkspaces=[{id:'workspace',name:'Weddings'}];renderFrontDesk();");
 const input=panel.querySelector('#frontDeskSearch');input.focus();input.value='Campbell';input.oninput({target:input});assert.equal(panel.querySelector('#frontDeskSearch'),input);assert.equal(w.document.activeElement,input);
 assert.match(panel.textContent,/Partial deposit/);assert.match(panel.textContent,/Record verified deposit/);assert.equal(panel.querySelector('[data-frontdesk-job]'),null);
});

test('shared request helper accepts empty successes and dismissal removes the banner',async()=>{
 const {w,panel}=environment();
 const source=fs.readFileSync(path.join(root,'app.js'),'utf8');
 const helper=source.slice(source.indexOf('async function json('),source.indexOf('async function loadWorkspaces('));
 w.eval(helper);
 for(const status of [200,204,205]){
  w.fetch=async()=>new Response(null,{status});
  assert.equal(await w.json('/empty',{method:'POST'}),null);
 }
 w.fetch=async()=>new Response('{"ok":true}',{status:200});
 assert.equal((await w.json('/data')).ok,true);
 w.fetch=async()=>new Response('Denied',{status:403});
 await assert.rejects(w.json('/denied'),/Denied/);
 w.fetch=async(url,options)=>options?.method==='POST'?new Response(null,{status:204}):new Response(JSON.stringify([{id:'a',title:'Notice',body:'Message'}]),{status:200});
 await w.renderAnnouncementBanners(panel,'workspace',()=>true);
 await panel.querySelector('[data-dismiss]').onclick();
 assert.equal(panel.querySelector('article'),null);
 assert.equal(panel.hidden,true);
});
