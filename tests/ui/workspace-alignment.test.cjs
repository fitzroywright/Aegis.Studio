
function mediaResponse(data,url){const q=new URL(url,'https://studio.test').searchParams;let assets=data.assets||[];if(q.get('collection'))assets=assets.filter(a=>a.collection===q.get('collection'));if(q.get('subjectId'))assets=assets.filter(a=>(a.subjects||[]).includes(q.get('subjectId')));if(q.get('after'))assets=assets.filter(a=>a.id>q.get('after'));const subjects=(data.subjects||[]).filter(a=>!q.get('subjectSearch')||(a.name+' '+a.reference).toLowerCase().includes(q.get('subjectSearch').toLowerCase()));return {...data,subjects,assets:assets.slice(0,100),totalAssets:assets.length,totalSubjects:(data.subjects||[]).length,hasMore:assets.length>100};}
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('names prefer given or display first name and fall back to login',async()=>{
 const dom=new JSDOM('<span id="signedInUser"></span><span id="userAvatar"></span>',{runScripts:'outside-only',url:'https://studio.test'});
 const w=dom.window;w.fetch=async()=>({ok:true,json:async()=>({user:{userName:'fwright',displayName:'Fitzroy Wright'}})});
 w.eval(fs.readFileSync(path.join(root,'auth.js'),'utf8'));
 await new Promise(resolve=>setTimeout(resolve,0));
 assert.equal(w.document.getElementById('signedInUser').textContent,'Fitzroy');
 assert.equal(w.studioFirstName('fwright'),'Fitzroy');
 w.studioRememberPeople([{userName:'taberfa',firstName:'Taberfa',displayName:'T. Brown'}]);
 assert.equal(w.studioFirstName('taberfa'),'Taberfa');
 assert.equal(w.studioFirstName('unknown.login'),'unknown.login');
});
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
 assert.equal(panel.querySelectorAll('.person-card').length,1);assert.equal(panel.querySelector('script'),null);assert.equal(panel.querySelector('form'),null);
 subjects.push({id:'b',name:'Alex',reference:'002',notes:''});
 await w.renderJobSubjects({id:'job'},panel,()=>true,()=>{});
 assert.equal(panel.querySelectorAll('.person-card').length,2);assert.equal(panel.querySelector('form'),null);
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
test('stale asynchronous subjects response does not overwrite the next tab',async()=>{
 const {w,panel}=environment();panel.textContent='Next tab';w.json=async url=>url.endsWith('/access')?{permissions:{}}:[];
 await w.renderJobSubjects({id:'job'},panel,()=>false,()=>{});assert.equal(panel.textContent,'Next tab');
});
test('workspace job keeps Media dominant with activity beside the handoff and workspace settings',async()=>{
 const html=fs.readFileSync(path.join(root,'index.html'),'utf8');const dom=new JSDOM(html,{runScripts:'outside-only',url:'https://studio.test'});const w=dom.window;
 const job={id:'job',workspaceId:'workspace',conversationId:'conversation',title:'Portraits',clientName:'Client',status:'Scheduled'};
 Object.assign(w,{showWorkspaces:async()=>{},workspaceCache:[{id:'workspace',name:'Portraits',permissions:{accessWorkspace:true,view:true}}],selectedWorkspace:'workspace',currentWorkspacePermissions:{},selectedConversation:null,activateView(){},activateNavigation(){},openConversation:async()=>{},formatBytes:()=>'',openJob:async()=>{},esc:s=>String(s??''),alert(){}});
 for(const id of ['conversationList','detailPane','workspaceTitle','allButton'])w[id]=w.document.getElementById(id);
 w.fetch=async url=>({ok:true,json:async()=>url.endsWith('/sending-identities')?{identities:[{type:'Workspace'},{type:'Self',isDefault:true}]}:{channel:'Email',messages:[]}});
 w.json=async url=>url==='/api/jobs'?[job]:url==='/api/jobs/job'?job:url.endsWith('/media')?{assets:[],subjects:[],unmatched:[],gallery:{default_lens:'Auto'}}:url.includes('/threads/Job/')?{id:'thread'}:url==='/api/auth/me'?{user:{userName:'staff',displayName:'Staff'}}:url.endsWith('/access')?{permissions:{}}:[];
 w.eval(fs.readFileSync(path.join(root,'auth.js'),'utf8'));
 w.money=n=>'JMD '+n;w.eval(fs.readFileSync(path.join(root,'management.js'),'utf8'));
 w.eval(fs.readFileSync(path.join(root,'job-handoff.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'job-workspace.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'media-production.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'workspace-outlook.js'),'utf8'));
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
 assert.equal(w.messageFirstName('fwright',[],'Team member',true),'fwright');
 assert.equal(w.messageFirstName('fwright',[{userName:'fwright',displayName:'fwright'}],'Team member',true),'fwright');
 assert.deepEqual(Array.from(w.document.querySelectorAll('[data-job-scroll]'),b=>b.textContent),['Overview','Messages','Media','Subjects','Tasks','Sessions','Activity']);
 assert.equal(w.document.querySelector('.detail-header .activity-button'),null);
 assert.equal(w.document.querySelector('[data-job-scroll=activity]')?.textContent,'Activity');
 assert.ok(w.document.querySelector('.job-work-board'));
 await new Promise(r=>setTimeout(r,10));assert.ok(w.document.querySelector('.job-context-sidebar .thread-next-tasks'));assert.equal(w.document.querySelector('.media-tasks'),null);assert.equal(w.document.querySelector('.gallery-upload-card'),null);
 assert.ok(w.document.querySelector('.workspace-settings-action'));assert.equal(w.document.querySelector('[data-tab=members]'),null);
 assert.ok(w.document.querySelector('details.job-details-card'));assert.match(w.document.querySelector('[data-job="job"]').textContent,/Assigned to: Unassigned/);assert.match(w.document.querySelector('[data-job="job"]').textContent,/Event: Not scheduled/);assert.equal(w.document.querySelector('[data-tab="enquiries"]'),null);assert.equal(w.document.querySelector('[data-tab="team"]'),null);assert.equal(w.document.querySelector('[data-tab="announcements"]'),null);
});
test('left navigation has the seven operational destinations from the Studio workflow brief',()=>{
 const html=fs.readFileSync(path.join(root,'index.html'),'utf8');
 const shell=new JSDOM(html,{runScripts:'outside-only',url:'https://studio.test'});
 assert.deepEqual(Array.from(shell.window.document.querySelectorAll('.rail [data-view]'),b=>b.textContent.trim()),['My Work','Workspaces','Pipeline','Production','Front Desk','Finance','Admin']);
 assert.equal(shell.window.document.querySelector('[data-view="messages"]'),null);
 assert.equal(shell.window.document.querySelector('[data-view="jobs"]'),null);
 assert.equal(shell.window.document.querySelectorAll('.rail [data-view]').length,7);
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
 w.eval(fs.readFileSync(path.join(root,'frontdesk.js'),'utf8')+"\nfrontDeskJobs=[{id:'job',clientName:'Campbell',title:'Wedding',status:'Needs Scheduling'}];frontDeskRequests=[{id:'request',request:{clientName:'Campbell',title:'Wedding',eventDate:'2026-10-10',requiredDeposit:100,workspaceId:'workspace'},recordedDeposit:40,status:'AwaitingDeposit'}];frontDeskTab='requests';frontDeskWorkspaces=[{id:'workspace',name:'Weddings'}];renderFrontDesk();document.querySelector('[data-frontdesk-tab=\"requests\"]').click();");
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
 await assert.rejects(w.json('/denied'),/permission/);
 w.fetch=async(url,options)=>options?.method==='POST'?new Response(null,{status:204}):new Response(JSON.stringify([{id:'a',title:'Notice',body:'Message'}]),{status:200});
 await w.renderAnnouncementBanners(panel,'workspace',()=>true);
 await panel.querySelector('[data-dismiss]').onclick();
 assert.equal(panel.querySelector('article'),null);
 assert.equal(panel.hidden,true);
});

test('Media shows all images by default and collection and subject filters live in the title bar',async()=>{
 const {w,panel}=environment();w.eval(fs.readFileSync(path.join(root,'media-production.js'),'utf8'));
 const data={subjects:[],unmatched:[],assets:[{id:'a',assetKey:'a',versionId:'v1',fileName:'Ceremony.jpg',contentType:'image/jpeg',version:1,status:'Needs Editing',subjects:[],collection:'Ceremony'},{id:'b',assetKey:'b',versionId:'v2',fileName:'Other.jpg',contentType:'image/jpeg',version:1,status:'Needs Sorting',subjects:[],collection:''}]};
 w.json=async url=>url.split('?')[0].endsWith('/media')?mediaResponse(data,url):url.endsWith('/access')?{permissions:{manageProduction:false}}:[];
 await w.renderProductionMedia({id:'job'},panel,()=>true);
 assert.equal(panel.querySelector('[data-add]'),null);assert.equal(panel.querySelector('.media-tasks'),null);assert.equal(panel.querySelector('input[type=file]'),null);
 assert.equal(panel.querySelectorAll('.job-file-tile').length,2);
 assert.equal(panel.querySelector('[data-lens]'),null);
 const collection=panel.querySelector('[data-collection]');collection.value='Ceremony';await collection.onchange();
 assert.equal(panel.querySelectorAll('.job-file-tile').length,1);assert.match(panel.querySelector('.job-file-tile').textContent,/Ceremony.jpg/);
 const all=panel.querySelector('[data-collection]');all.value='';await all.onchange();assert.equal(panel.querySelectorAll('.job-file-tile').length,2);
});
test('Media collection controls persist names and dropped files target the selected collection',async()=>{
 const {w,panel}=environment();w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;};
 w.FormData=class {entries=[];append(key,value){this.entries.push([key,value]);}get(key){return this.entries.find(x=>x[0]===key)?.[1];}};
 const data={gallery:{default_collection:'General'},collections:['General'],subjects:[],assets:[],unmatched:[]},calls=[];
 w.json=async(url,options)=>{if(url.endsWith('/media'))return data;if(url.endsWith('/access'))return {permissions:{manageProduction:true}};if(url.includes('/activities?'))return [];if(options){calls.push({url,options});if(url.endsWith('/collections'))data.collections.push(JSON.parse(options.body).name);if(url.includes('/collections/General'))data.collections=data.collections.map(x=>x==='General'?JSON.parse(options.body).name:x);return {assetId:'image'};}return [];};
 w.eval(fs.readFileSync(path.join(root,'media-production.js'),'utf8'));
 await w.renderProductionMedia({id:'job'},panel,()=>true);
 panel.querySelector('[data-new-collection]').click();let form=w.document.querySelector('dialog form');form.elements.name.value='Portraits';await form.onsubmit({preventDefault(){},currentTarget:form});
 assert.equal(panel.querySelector('[data-collection]').value,'Portraits');
 await panel.querySelector('.media-drop-surface').ondrop({preventDefault(){},dataTransfer:{files:[{name:'portrait.jpg'}]}});
 let uploadForm=[...w.document.querySelectorAll('dialog form')].find(x=>x.closest('dialog')?.textContent.includes('Add / Upload'));
 assert.ok(uploadForm);await uploadForm.onsubmit({preventDefault(){},currentTarget:uploadForm});
 await new Promise(resolve=>setTimeout(resolve,10));
 assert.equal(calls.find(x=>x.url.endsWith('/uploads')).options.body.get('collection'),'Portraits');
 panel.querySelector('[data-rename-collection]').click();form=[...w.document.querySelectorAll('dialog form')].find(x=>x.closest('dialog')?.textContent.includes('Rename collection'));assert.ok(form);form.elements.name.value='Portraits edited';await form.onsubmit({preventDefault(){},currentTarget:form});
 assert.ok(calls.some(x=>x.url.includes('/collections/Portraits')));
});
test('upcoming tasks keep the Add task action outside the scrolling task list',async()=>{
 const {w,panel}=environment();w.studioFirstName=value=>value;
 w.json=async url=>url.endsWith('/access')?{permissions:{manageProduction:true}}:Array.from({length:8},(_,i)=>({id:'task'+i,title:'Task '+i,status:'NotStarted',assignedTo:'staff'}));
 w.eval(fs.readFileSync(path.join(root,'media-production.js'),'utf8'));
 await w.renderCompactTasks({id:'job'},panel,()=>true);
 assert.equal(panel.querySelector('.thread-task-list .compact-task')?.textContent.includes('Task 0'),true);
 assert.equal(panel.querySelector('.thread-task-list [data-all]'),null);
 assert.equal(panel.querySelector('.thread-task-footer [data-all]')?.textContent,'Add task / View all');
});
test('large Media jobs show a searchable subject list beside overflowing images',async()=>{
 const {w,panel}=environment();
 Object.defineProperty(w.HTMLElement.prototype,'clientWidth',{configurable:true,get(){return this.classList.contains('media-drop-surface')?310:0;}});
 const subjects=Array.from({length:100},(_,i)=>({id:'s'+i,name:'Subject '+i,reference:'R'+i,status:'Photographed',imageCount:1}));
 const assets=Array.from({length:6},(_,i)=>({id:'a'+i,assetKey:'a'+i,versionId:'v'+i,fileName:'Photo'+i+'.jpg',contentType:'image/jpeg',version:1,status:'Ready',subjects:['s'+i],collection:'General'}));
 w.json=async url=>url.split('?')[0].endsWith('/media')?mediaResponse({subjects,assets,collections:['General'],gallery:{},unmatched:[]},url):url.endsWith('/access')?{permissions:{manageProduction:false}}:[];
 w.eval(fs.readFileSync(path.join(root,'media-production.js'),'utf8'));
 await w.renderProductionMedia({id:'job'},panel,()=>true);
 assert.equal(panel.querySelector('[data-subject]'),null);
 const rail=panel.querySelector('.media-subject-rail');assert.equal(rail.hidden,false);assert.equal(rail.querySelectorAll('[data-subject-id]').length,101);
 rail.querySelector('input').value='Subject 42';rail.querySelector('input').oninput();await new Promise(r=>setTimeout(r,350));const filtered=panel.querySelector('.media-subject-rail');assert.equal(filtered.querySelectorAll('[data-subject-id]').length,2);
 await filtered.querySelector('[data-subject-id="s42"]').onclick();assert.equal(panel.querySelectorAll('.job-file-tile').length,0);
 await panel.querySelector('.media-subject-rail [data-subject-id=""]').onclick();assert.equal(panel.querySelectorAll('.job-file-tile').length,6);
});
test('Pipeline shows stage groups with schedule and task on timeline and calendar',async()=>{
 const dom=new JSDOM('<div id="generic"></div><div id="conversation"></div>',{runScripts:'outside-only',url:'https://studio.test'});
 const w=dom.window;w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;};w.genericView=dom.window.document.getElementById('generic');w.conversationView=dom.window.document.getElementById('conversation');w.activateNavigation=()=>{};w.esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 w.studioRememberPeople=()=>{};w.studioFirstName=value=>value;
 const today=new Date();const time=new Date(today.getFullYear(),today.getMonth(),today.getDate()+1,10).toISOString();const end=new Date(today.getFullYear(),today.getMonth(),today.getDate()+1,12).toISOString();
 const requests=[];
 w.json=async(url,options)=>{requests.push(url);if(options)return {id:'new'};if(url==='/api/jobs')return [{id:'job',title:'Wedding',status:'Needs Scheduling',clientName:'Brown'}];if(url.startsWith('/api/schedule/entries?'))return [{id:'shoot',jobId:'job',title:'Wedding shoot',startsAtUtc:time,endsAtUtc:end,resourceIds:['photographer'],status:'Scheduled'},{id:'general',jobId:null,title:'Studio booking',startsAtUtc:time,endsAtUtc:end,resourceIds:[],status:'Tentative'}];if(url==='/api/operations/activities')return [{id:'task',jobId:'job',title:'Confirm location',scheduledAtUtc:time,dueAtUtc:time,status:'NotStarted'}];if(url==='/api/schedule/resources')return [{id:'photographer',name:'Taberfa',resourceType:'Staff',isActive:true}];if(url==='/api/auth/me')return {user:{isAdministrator:true}};if(url==='/api/workspaces')return [{id:'workspace',name:'Weddings'}];throw Error(url);};
 w.eval(fs.readFileSync(path.join(root,'scheduling.js'),'utf8'));
 await w.showPipeline();
 assert.match(w.genericView.textContent,/Needs scheduling/);assert.match(w.genericView.textContent,/Wedding shoot/);assert.match(w.genericView.textContent,/Confirm location/);assert.match(w.genericView.textContent,/Unlinked schedule/);
 assert.equal(w.genericView.querySelectorAll('.pipeline-stage-lane').length,2);
 w.genericView.querySelector('[data-pipeline-view="calendar"]').click();await new Promise(resolve=>setTimeout(resolve,10));
 assert.ok(w.genericView.querySelector('.pipeline-calendar'));assert.match(w.genericView.textContent,/Wedding shoot/);
 w.genericView.querySelector('[data-stage="Needs scheduling"]').click();await new Promise(resolve=>setTimeout(resolve,10));
 assert.equal(w.genericView.querySelector('[data-stage="Needs scheduling"]').getAttribute('aria-pressed'),'true');
 w.genericView.querySelector('#pipelineNewSchedule').click();const form=w.genericView.querySelector('#scheduleEntryForm');
 form.querySelector('#scheduleJob').value='job';form.querySelector('#scheduleTitle').value='Extra shoot';
 await form.onsubmit({preventDefault(){},currentTarget:form});
 assert.ok(requests.some(url=>url==='/api/schedule/entries'));
});



test('large galleries render one bounded page of previews and advance using a server cursor',async()=>{
 const {w,panel}=environment();const requests=[];
 w.json=async url=>{requests.push(url);if(url.endsWith('/access'))return {permissions:{manageProduction:false}};if(url.includes('/media')){const next=new URL(url,'https://studio.test').searchParams.has('after');return {subjects:[],collections:['General'],gallery:{},unmatched:[],totalAssets:100000,totalSubjects:327,hasMore:true,assets:Array.from({length:100},(_,i)=>{const id=String((next?100:0)+i).padStart(6,'0');return {id,assetKey:id,versionId:'v'+id,fileName:id+'.jpg',contentType:'image/jpeg',version:1,status:'Ready',subjects:[],collection:'General'};})};}return [];};
 w.eval(fs.readFileSync(path.join(root,'media-production.js'),'utf8'));await w.renderProductionMedia({id:'job'},panel,()=>true);
 assert.equal(panel.querySelectorAll('.job-file-tile').length,100);assert.ok([...panel.querySelectorAll('img')].every(i=>i.src.endsWith('/preview?size=thumbnail')));assert.equal(panel.querySelector('[data-drag-asset]').dataset.dragAsset,'000000');
 await panel.querySelector('[data-next-page]').onclick();assert.equal(panel.querySelectorAll('.job-file-tile').length,100);assert.equal(panel.querySelector('[data-drag-asset]').dataset.dragAsset,'000100');assert.ok(requests.some(u=>u.includes('after=000099')));w.close();
});
