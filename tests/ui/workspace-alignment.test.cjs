
function mediaResponse(data,url){const q=new URL(url,'https://studio.test').searchParams;let assets=data.assets||[];if(q.get('collection'))assets=assets.filter(a=>a.collection===q.get('collection'));if(q.get('subjectId'))assets=assets.filter(a=>(a.subjects||[]).includes(q.get('subjectId')));if(q.get('after'))assets=assets.filter(a=>a.id>q.get('after'));const subjects=(data.subjects||[]).filter(a=>!q.get('subjectSearch')||(a.name+' '+a.reference).toLowerCase().includes(q.get('subjectSearch').toLowerCase()));return {...data,subjects,assets:assets.slice(0,100),totalAssets:assets.length,totalSubjects:(data.subjects||[]).length,hasMore:assets.length>100};}
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('names prefer given or display first name and fall back to login',async()=>{
 const dom=new JSDOM('<span id="signedInUser"></span><span id="userAvatar"></span>',{runScripts:'outside-only',url:'https://studio.test'});
 const w=dom.window;w.eval(fs.readFileSync(path.join(root,'studio-message-card.js'),'utf8'));w.fetch=async()=>({ok:true,json:async()=>({user:{userName:'fwright',displayName:'Fitzroy Wright'}})});
 w.eval(fs.readFileSync(path.join(root,'auth.js'),'utf8'));
 await new Promise(resolve=>setTimeout(resolve,0));
 assert.equal(w.document.getElementById('signedInUser').textContent,'Fitzroy Wright');
 assert.equal(w.studioFirstName('fwright'),'Fitzroy');
 w.studioRememberPeople([{userName:'taberfa',firstName:'Taberfa',displayName:'T. Brown'}]);
 assert.equal(w.studioFirstName('taberfa'),'Taberfa');
 assert.equal(w.studioFirstName('unknown.login'),'unknown.login');
});
function environment(){
 const dom=new JSDOM('<div id="panel"></div>',{runScripts:'outside-only',url:'https://studio.test'});
 const w=dom.window;w.eval(fs.readFileSync(path.join(root,'studio-message-card.js'),'utf8'));w.showWorkspaces=async()=>{};
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
test('Job task access uses the configured workflow rather than a parallel task list',async()=>{
 const {w,panel}=environment();const opened=[];let reads=0;w.json=async()=>{reads++;return [];};w.StudioJobGraph={launchTask:async id=>opened.push(id)};
 await w.renderJobTasks({id:'job'},panel,()=>true,()=>{});assert.match(panel.textContent,/configured workflow graph/);panel.querySelector('[data-open-job-workflow]').click();assert.deepEqual(opened,['job']);assert.equal(reads,0);w.close();
});

test('stale asynchronous subjects response does not overwrite the next tab',async()=>{
 const {w,panel}=environment();panel.textContent='Next tab';w.json=async url=>url.endsWith('/access')?{permissions:{}}:[];
 await w.renderJobSubjects({id:'job'},panel,()=>false,()=>{});assert.equal(panel.textContent,'Next tab');
});
test('workspace Job Details keeps media dominant without retired reply cards or duplicate panels',async t=>{
 const {w}=require('./job-detail-fixture.cjs').environment(t);await w.openWorkspaceJob('job');await new Promise(r=>setTimeout(r,20));
 assert.ok(w.document.querySelector('.job-detail-board .jd-contact-sheet'));assert.ok(w.document.querySelector('.jd-job-brief'));assert.ok(w.document.querySelector('[aria-label="Filter media by collection"]'));
 assert.equal(w.document.querySelector('.job-detail-right,#jobDetailMessages,#hierarchyCompose,.job-context-sidebar,.gallery-upload-card,.workspace-settings-action'),null);assert.equal(w.document.querySelectorAll('.jd-workflow-display').length,1);
});

test('current shell keeps obsolete left navigation removed',()=>{
 const html=fs.readFileSync(path.join(root,'index.html'),'utf8');
 const shell=new JSDOM(html,{runScripts:'outside-only',url:'https://studio.test'});
 const scripts=Array.from(shell.window.document.querySelectorAll('script[src]'),s=>s.getAttribute('src'));
 assert.equal(shell.window.document.querySelector('.rail'),null);
 assert.ok(scripts.some(src=>src.includes('studio-navigation.js')));
 assert.equal(shell.window.document.querySelector('[data-view="messages"]'),null);
 assert.equal(shell.window.document.querySelector('[data-view="jobs"]'),null);
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
 w.eval(fs.readFileSync(path.join(root,'management.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'frontdesk.js'),'utf8')+"\nfrontDeskJobs=[{id:'job',clientName:'Campbell',title:'Wedding',status:'Needs Scheduling'}];frontDeskRequests=[{id:'request',request:{clientName:'Campbell',title:'Wedding',eventDate:'2026-10-10',requiredDeposit:100,workspaceId:'workspace'},recordedDeposit:40,status:'AwaitingDeposit'}];frontDeskTab='today';frontDeskDate='2026-10-10';frontDeskWorkspaces=[{id:'workspace',name:'Weddings'}];renderFrontDesk();");
 const input=panel.querySelector('#frontDeskSearch');input.focus();input.value='Campbell';input.oninput({target:input});assert.equal(panel.querySelector('#frontDeskSearch'),input);assert.equal(w.document.activeElement,input);
 assert.ok(panel.querySelector('[data-fd-row-open="request"]'));assert.match(panel.textContent,/Awaiting deposit/);assert.equal(panel.querySelector('[data-frontdesk-job]'),null);
});

test('shared request helper accepts empty successful responses',async()=>{
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
 w.json=async(url,options)=>{if(url.endsWith('/connected-workflow'))return {data:{sessions:[]}};if(url.endsWith('/media'))return data;if(url.endsWith('/access'))return {permissions:{manageProduction:true}};if(url.includes('/activities?'))return [];if(options){calls.push({url,options});if(url.endsWith('/collections'))data.collections.push(JSON.parse(options.body).name);if(url.includes('/collections/General'))data.collections=data.collections.map(x=>x==='General'?JSON.parse(options.body).name:x);return {assetId:'image'};}return [];};
 w.eval(fs.readFileSync(path.join(root,'subject-picker.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'media-production.js'),'utf8'));
 await w.renderProductionMedia({id:'job'},panel,()=>true);
 panel.querySelector('[data-new-collection]').click();let form=w.document.querySelector('dialog form');form.elements.name.value='Portraits';await form.onsubmit({preventDefault(){},currentTarget:form});
 assert.equal(panel.querySelector('[data-collection]').value,'Portraits');
 await panel.querySelector('.media-drop-surface').ondrop({preventDefault(){},dataTransfer:{files:[{name:'portrait.jpg'}]}});
 await new Promise(resolve=>setImmediate(resolve));let uploadForm=[...w.document.querySelectorAll('dialog form')].find(x=>x.closest('dialog')?.textContent.includes('Add / Upload'));
 assert.ok(uploadForm);await uploadForm.onsubmit({preventDefault(){},currentTarget:uploadForm});
 await new Promise(resolve=>setTimeout(resolve,10));
 assert.equal(JSON.parse(calls.find(x=>x.url.endsWith('/media/upload-batches')).options.body).collection,'Portraits');assert.ok(calls.find(x=>x.url.endsWith('/uploads')).options.body.get('uploadId'));
 panel.querySelector('[data-rename-collection]').click();form=[...w.document.querySelectorAll('dialog form')].find(x=>x.closest('dialog')?.textContent.includes('Rename collection'));assert.ok(form);form.elements.name.value='Portraits edited';await form.onsubmit({preventDefault(){},currentTarget:form});
 assert.ok(calls.some(x=>x.url.includes('/collections/Portraits')));
});
test('upcoming tasks keep the Add task action outside the scrolling task list',async()=>{
 const {w,panel}=environment();w.studioFirstName=value=>value;w.StudioWorkflow={current:async()=>({canManage:true,canReview:true,userName:'staff',data:{}}),canReportTask:()=>true,open:async()=>{}};
 w.json=async url=>url.endsWith('/access')?{permissions:{manageProduction:true}}:Array.from({length:8},(_,i)=>({id:'task'+i,title:'Task '+i,status:'NotStarted',assignedTo:'staff'}));
 w.eval(fs.readFileSync(path.join(root,'media-production.js'),'utf8'));
 await w.renderCompactTasks({id:'job'},panel,()=>true);
 assert.equal(panel.querySelector('.thread-task-list .compact-task')?.textContent.includes('Task 0'),true);
 assert.equal(panel.querySelector('.thread-task-list [data-all]'),null);
 assert.equal(panel.querySelector('.thread-task-footer [data-all]')?.textContent,'View all');
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
 const w=dom.window;w.eval(fs.readFileSync(path.join(root,'studio-message-card.js'),'utf8'));w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;};w.genericView=dom.window.document.getElementById('generic');w.conversationView=dom.window.document.getElementById('conversation');w.activateNavigation=()=>{};w.esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 w.studioRememberPeople=()=>{};w.studioFirstName=value=>value;
 const today=new Date();const time=new Date(today.getFullYear(),today.getMonth(),today.getDate()+1,10).toISOString();const end=new Date(today.getFullYear(),today.getMonth(),today.getDate()+1,12).toISOString();
 const requests=[];
 w.json=async(url,options)=>{requests.push(url);if(options)return {id:'new'};if(url==='/api/jobs')return [{id:'job',title:'Wedding',status:'Needs Scheduling',clientName:'Brown'}];if(url.startsWith('/api/schedule/entries?'))return [{id:'shoot',jobId:'job',title:'Wedding shoot',startsAtUtc:time,endsAtUtc:end,resourceIds:['photographer'],status:'Scheduled'},{id:'general',jobId:null,title:'Studio booking',startsAtUtc:time,endsAtUtc:end,resourceIds:[],status:'Tentative'}];if(url==='/api/operations/activities')return [{id:'task',jobId:'job',title:'Confirm location',scheduledAtUtc:time,dueAtUtc:time,status:'NotStarted'}];if(url==='/api/schedule/resources')return [{id:'photographer',name:'Taberfa',resourceType:'Staff',isActive:true}];if(url==='/api/auth/me')return {user:{isAdministrator:true}};if(url==='/api/workspaces')return [{id:'workspace',name:'Weddings'}];throw Error(url);};
 for(const file of ['calendar-layouts.js','studio-status-order.js','subject-picker.js','studio-page.js','scheduling.js'])w.eval(fs.readFileSync(path.join(root,file),'utf8'));
 await w.showPipeline();w.genericView.querySelector('[data-pipeline-view="timeline"]').click();await new Promise(r=>setTimeout(r,10));
 assert.match(w.genericView.textContent,/Needs scheduling/);assert.match(w.genericView.textContent,/Wedding shoot/);assert.match(w.genericView.textContent,/Confirm location/);assert.match(w.genericView.textContent,/Unlinked schedule/);
 assert.equal(w.genericView.querySelectorAll('.pipeline-stage-lane').length,2);
 w.genericView.querySelector('[data-pipeline-view="calendar"]').click();await new Promise(resolve=>setTimeout(resolve,10));
 assert.ok(w.genericView.querySelector('.pipeline-calendar'));assert.match(w.genericView.textContent,/Wedding shoot/);
 w.genericView.querySelector('[data-pipeline-view="jobs"]').click();await new Promise(resolve=>setTimeout(resolve,10));const stage=w.genericView.querySelector('#pipelineStatusFilter');stage.value='Needs Scheduling';stage.dispatchEvent(new w.Event('change'));await new Promise(resolve=>setTimeout(resolve,10));assert.equal(stage.value,'Needs Scheduling');
 w.openScheduleEntryForm('job');const form=w.genericView.querySelector('#scheduleEntryForm');
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


test('Workspaces keeps Today breadcrumbs and a single center panel without retired rails',()=>{
 const source=fs.readFileSync(path.join(root,'studio-navigation.js'),'utf8');
 const workspaceSource=fs.readFileSync(path.join(root,'workspace-outlook.js'),'utf8');
 const css=fs.readFileSync(path.join(root,'studio.css'),'utf8');
 assert.match(source,/workspace-list-layout/);
 assert.doesNotMatch(source,/class="workspace-left-rail"/);
 assert.match(source,/workspace-center-panel/);
 assert.doesNotMatch(source,/workspace-right-rail/);
 assert.match(fs.readFileSync(path.join(root,'studio-shell-nav.js'),'utf8'),/Navigation History/);

 assert.match(workspaceSource,/\{label:'Today',view:'today'\}/);
 assert.match(workspaceSource,/\{label:'Workspaces',view:'workspaces'\}/);
 assert.doesNotMatch(css,/body \.shell>\.studio-context-rail/);
});


test('Jobs uses its dedicated renderer and permanent shared navigation',()=>{
 const navigation=fs.readFileSync(path.join(root,'studio-navigation.js'),'utf8');
 const jobs=fs.readFileSync(path.join(root,'jobs-page.js'),'utf8');
 const shell=fs.readFileSync(path.join(root,'studio-shell-nav.js'),'utf8');
 assert.match(navigation,/jobs:renderJobs/);
 assert.match(navigation,/StudioJobsPage.render/);
 assert.match(jobs,/\/api\/workflow\/jobs/);
 assert.doesNotMatch(shell,/sj-rail-card/);assert.match(shell,/studioNavigationTools/);
 assert.match(navigation,/studioOpenJobs/);
});



