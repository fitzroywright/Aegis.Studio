const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
function environment(){
 const dom=new JSDOM('<div id="panel"></div>',{runScripts:'outside-only',url:'https://studio.test'});
 const w=dom.window;
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
test('workspace job renders exactly four lower tabs and a collapsible details card',async()=>{
 const html=fs.readFileSync(path.join(root,'index.html'),'utf8');const dom=new JSDOM(html,{runScripts:'outside-only',url:'https://studio.test'});const w=dom.window;
 const job={id:'job',workspaceId:'workspace',conversationId:'conversation',title:'Portraits',clientName:'Client',status:'Scheduled'};
 Object.assign(w,{workspaceCache:[{id:'workspace',name:'Portraits',permissions:{accessWorkspace:true,view:true}}],selectedWorkspace:'workspace',currentWorkspacePermissions:{},selectedConversation:null,activateView(){},activateNavigation(){},openConversation:async()=>{},formatBytes:()=>'',openJob:async()=>{},esc:s=>String(s??''),alert(){}});
 for(const id of ['conversationList','detailPane','workspaceTitle','allButton'])w[id]=w.document.getElementById(id);
 w.fetch=async()=>({ok:true,json:async()=>({channel:'Phone',messages:[]})});
 w.json=async url=>url==='/api/jobs'?[job]:url==='/api/jobs/job'?job:url.includes('/threads/Job/')?{id:'thread'}:url==='/api/auth/me'?{user:{userName:'staff',displayName:'Staff'}}:url.endsWith('/access')?{permissions:{}}:[];
 w.eval(fs.readFileSync(path.join(root,'job-workspace.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'workspace-outlook.js'),'utf8'));
 await w.openWorkspaceJob('job');await new Promise(r=>setTimeout(r,10));
 assert.deepEqual(Array.from(w.document.querySelectorAll('[data-job-tab]'),b=>b.textContent),['Gallery','Subjects','Tasks / Calendar','Activity']);
 assert.ok(w.document.querySelector('details.job-details-card'));assert.equal(w.document.querySelector('[data-tab="enquiries"]'),null);assert.equal(w.document.querySelector('[data-tab="team"]'),null);assert.ok(w.document.querySelector('[data-tab="announcements"]'));
});
test('top navigation has the requested order and preserves FrontDesk and WorkDay destinations',async()=>{
 const dom=new JSDOM('<header class="topbar"></header><aside class="rail"></aside><button id="newRequestButton"></button>',{runScripts:'outside-only',url:'https://studio.test'});const w=dom.window;let frontDesk=0;
 Object.assign(w,{loadFrontDeskRole(){},showFrontDesk:async()=>frontDesk++,showProduction(){},showWorkspaces(){},showManagement(){}});
 w.eval(fs.readFileSync(path.join(root,'studio-navigation.js'),'utf8'));
 assert.deepEqual(Array.from(w.document.querySelectorAll('.studio-top-nav button'),b=>b.textContent),['My Work','Dashboard','Workspaces','Pipeline','Production','FrontDesk','WorkDay','Admin']);
 w.document.querySelector('[data-view="frontdesk"]').click();await Promise.resolve();assert.equal(frontDesk,1);assert.equal(w.document.getElementById('newRequestButton').hidden,true);
});
