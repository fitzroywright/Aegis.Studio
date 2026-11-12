const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const read=file=>fs.readFileSync(path.join(root,file),'utf8');
const settle=()=>new Promise(r=>setImmediate(r));
function setup(t,view){
 const dom=new JSDOM('<body data-studio-view="'+view+'"><header class="topbar"></header><div class="shell"><aside id="studioContextRail"></aside><main id="genericView"></main></div><div id="conversationView"></div><span id="studioFooterRate"></span></body>',{runScripts:'outside-only',url:'https://studio.test',pretendToBeVisual:true});
 t.after(()=>dom.window.close());const w=dom.window,calls=[],navigation=[];
 w.genericView=w.document.getElementById('genericView');w.conversationView=w.document.getElementById('conversationView');w.alert=message=>{throw Error(message);};w.money=v=>'JMD '+v;w.studioFirstName=v=>v;
 w.studioNavigate=async(view,options)=>navigation.push({view,options});
 w.studioDialog=(title,html)=>{const d=w.document.createElement('dialog');d.innerHTML='<h2>'+title+'</h2><div class="studio-dialog-body">'+html+'</div>';d.close=()=>d.remove();w.document.body.append(d);return d;};
 const roster={revision:7,canEdit:true,canSchedule:true,rows:[{subjectId:'a',reference:'A1',name:'Alicia',collection:'2026',pickupNotification:'None'}]};
 const subjects=[{id:'a',reference:'A1',name:'Alicia',collection:'2026',status:'Photographed',imageCount:3},{id:'b',reference:'B1',name:'David',collection:'2026',status:'Absent',imageCount:0},{id:'c',reference:'C1',name:'Maya',collection:'2026',status:'Needs Retake',imageCount:1}];
 roster.rows=subjects.map(r=>({subjectId:r.id,reference:r.reference,name:r.name,collection:r.collection,pickupNotification:'None'}));
 const jobs=[{id:'j',conversationId:'cv',title:'Campion Graduation',clientName:'Campion College',clientId:'client',status:'Ready for Delivery'},{id:'j2',title:'Carter Wedding',clientName:'Carter',status:'Published'}];
 w.json=async(url,options)=>{calls.push({url,options});if(options?.method==='PUT')return {...roster,revision:8};if(url==='/api/jobs')return jobs;if(url==='/api/jobs/j')return jobs[0];if(url.endsWith('/session-plan'))return subjects;if(url.endsWith('/session-plan/structure'))return null;if(url.endsWith('/access'))return {permissions:{editJob:true}};if(url.endsWith('/session-plan/roster')||url.includes('/intake/')&&url.endsWith('/roster'))return roster;if(url.endsWith('/finance-release')){if(url.includes('j2'))throw Error('Accounts unavailable');return {decision:{balance:12500}};}if(url==='/api/auth/me')return {user:{isAdministrator:true}};return [];};
 w.fetch=async()=>({ok:true,json:async()=>[]});
 for(const file of ['studio-page.js','management.js','frontdesk.js','session-plan-templates.js','session-plan.js','studio-workflow-views.js','studio-shell-nav.js'])w.eval(read(file)+(file==='frontdesk.js'?'\nwindow.configureFrontDesk=rows=>{frontDeskDate="2026-10-30";frontDeskTab="today";frontDeskRequests=rows;renderFrontDesk();};':''));
 w.eval(fs.readFileSync(path.resolve(root,'../../Aegis.Studio.PublicSite/public/roster-editor.js'),'utf8'));
 w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
 return {w,calls,navigation,roster,subjects,jobs};
}
test('Session Plan reads outcomes and full photo counts separately from roster import',async t=>{
 const {w,calls}=setup(t,'session-plan');await w.StudioWorkflowViews.showSessionPlan('j');
 assert.match(w.genericView.textContent,/Alicia.*Photographed.*3/s);assert.match(w.genericView.textContent,/David.*Absent/s);
 assert.match(w.genericView.textContent,/Maya.*Reshoot.*1/s);
 assert.ok(calls.some(c=>c.url==='/api/jobs/j/session-plan'));
 assert.equal(w.document.querySelector('.studio-right-rail'),null);
 assert.equal(w.document.querySelector('#studioContextRail [data-rail-card="actions"]'),null);
 assert.ok(w.genericView.querySelector('[aria-label="Session Plan actions"] [data-workflow-action="import"]'));
});
test('guided import maps, merges stable references, blocks invalid rows and commits the reviewed revision',async t=>{
 const {w,calls}=setup(t,'session-plan');await w.StudioWorkflowViews.showSessionPlan('j');
 await w.StudioWorkflowViews.run('import');
 w.genericView.querySelector('[data-session-paste]').value='Student ID,Student Name,Class,Parent Email\nA1,Alicia Updated,2026,invalid';
 w.genericView.querySelector('[data-session-pasted]').click();
 assert.equal(w.genericView.querySelector('[data-session-map="reference"]').value,'0');
 w.genericView.querySelector('[data-session-preview]').click();
 assert.equal(w.genericView.querySelector('[data-session-commit]').disabled,true);
 w.genericView.querySelector('[data-session-warnings]').click();assert.match(w.genericView.textContent,/Invalid email/);
 w.genericView.querySelector('[data-session-back]').click();
 const select=w.genericView.querySelector('[data-session-map="contactEmail"]');select.value='';
 w.genericView.querySelector('[data-session-preview]').click();
 assert.equal(w.genericView.querySelector('[data-session-commit]').disabled,false);
 w.genericView.querySelector('[data-session-commit]').click();await settle();await settle();
 const saved=calls.find(c=>c.options?.method==='PUT');assert.ok(saved);
 const payload=JSON.parse(saved.options.body);assert.equal(payload.revision,7);assert.equal(payload.rows.length,3);assert.equal(payload.rows[0].subjectId,'a');assert.equal(payload.rows[0].name,'Alicia Updated');assert.equal(payload.resolveConflict,false);
 assert.equal(calls.filter(c=>/workflow|upload/.test(c.url)).length,0);
});
test('read-only Session Plan cannot open import',async t=>{
 const f=setup(t,'session-plan');f.roster.canEdit=false;await f.w.StudioWorkflowViews.showSessionPlan('j');
 assert.equal(f.w.document.querySelector('[data-workflow-action="import"]').disabled,true);
 await assert.rejects(()=>f.w.StudioWorkflowViews.run('import'),/permission/);
});
test('Delivery shows real balances, leaves failed financial reads unavailable, and routes existing actions',async t=>{
 const {w,navigation}=setup(t,'delivery');const notices=[],payments=[];
 w.StudioClientReminders={open:async id=>notices.push(id)};w.launchFrontDeskPos=async(action,context)=>payments.push({action,context});
 await w.StudioWorkflowViews.showDelivery();
 assert.match(w.genericView.textContent,/JMD|12,500/);assert.match(w.genericView.textContent,/Unavailable/);assert.doesNotMatch(w.genericView.querySelector('[data-delivery-row="j2"]').textContent,/Paid/);
 await w.StudioWorkflowViews.run('notice');await w.StudioWorkflowViews.run('payment');await w.StudioWorkflowViews.run('statement');
 assert.deepEqual(notices,['j']);assert.equal(payments[0].context.jobId,'j');assert.equal(navigation[0].view,'clients');assert.equal(navigation[0].options.tab,'finance');
 w.genericView.querySelector('[data-delivery-row="j2"]').click();assert.match(w.genericView.querySelector('.workflow-detail').textContent,/Accounts unavailable/);
});
test('Front Desk tabs retain request review, safe confirmation and deposits',async t=>{
 const {w,calls}=setup(t,'frontdesk');w.StudioStatusOrder={sort:values=>values};
 w.configureFrontDesk([{id:'r',jobId:'j',status:'AwaitingDeposit',recordedDeposit:5000,request:{clientName:'Miller Family',title:'Family portraits',requiredDeposit:10000,eventDate:'2026-10-30'}}]);
 assert.equal(w.genericView.querySelectorAll('[data-fd-tab]').length,3);
 assert.match(w.genericView.querySelector('.fd-selected-request').textContent,/Awaiting deposit/);assert.ok(w.genericView.querySelector('[data-fd-deposit]'));
 assert.match(w.genericView.querySelector('[data-fd-row-open]').textContent,/Deposit needed/);
 let payment;w.launchFrontDeskPos=async(action,context)=>{payment={action,context};};
 w.genericView.querySelector('[data-fd-deposit]').click();await settle();assert.equal(payment.context.bookingId,'r');
 const original=w.json;w.json=async(url,options)=>url.endsWith('/workflow-graph')?{revision:12}:url.endsWith('/workflow-graph/actions')?(calls.push({url,options}),{}):original(url,options);
 w.eval('showFrontDesk=async()=>{};');w.genericView.querySelector('[data-fd-confirm]').click();await settle();
 const command=calls.find(c=>c.url.endsWith('/workflow-graph/actions'));assert.equal(JSON.parse(command.options.body).expectedRevision,12);
});
test('Production lists jobs by stage and opens Job Details',async t=>{
 const {w,navigation}=setup(t,'production');
 w.eval(read('workflow-graph.js'));w.eval(read('workload-overview.js'));w.eval(read('commercial.js'));
 const data={jobs:[{id:'j',title:'Campion',status:'Active Production'}],activities:[{id:'a',jobId:'j',workOrderId:'o',title:'Retouch portraits',activityType:'Retouch',status:'InProgress',assignedTo:'Tamara'}],orders:[{id:'o',jobId:'j',activityIds:['a']}],eligibility:[{activityId:'a',status:'InProgress'}]};
 const order={id:'o',jobId:'j',jobTitle:'Campion',product:'Portraits',quantity:1,tasks:[{id:'a',status:'InProgress',title:'Retouch portraits',assignedTo:'Tamara',step:{activityType:'Retouch'}}]};
 w.loadWorkspaces=async()=>[];w.json=async url=>url==='/api/production/workload'?data:url.startsWith('/api/production/work-orders?')?[order]:[];
 w.eval('openCommercialWorkOrder=async order=>{window.openedOrder=order;};');
 await w.showCatalogProduction();
 assert.deepEqual([...w.genericView.querySelectorAll('[data-production-lane] h2')].map(e=>e.textContent),['Ready to Start','In Production','Quality Control','Rework','Complete']);
 assert.ok(w.genericView.querySelector('[data-production-lane="progress"] [data-production-job="j"]'));
 assert.equal(w.genericView.querySelector('[data-production-due]'),null);assert.match(w.genericView.querySelector('[data-production-job="j"]').textContent,/Campion/);
 w.genericView.querySelector('[data-production-job="j"]').click();await settle();assert.equal(navigation.at(-1).options.jobId,'j');
 assert.equal(w.document.querySelector('#studioContextRail .st-attention'),null);
});

 test('failed import retains the reviewed subjects and revision for a safe retry',async t=>{
  const {w,calls}=setup(t,'session-plan');await w.StudioWorkflowViews.showSessionPlan('j');await w.StudioWorkflowViews.run('import');
  w.genericView.querySelector('[data-session-paste]').value='name,reference\nNew Person,N1';w.genericView.querySelector('[data-session-pasted]').click();w.genericView.querySelector('[data-session-preview]').click();
  const original=w.json;w.json=async(url,options)=>{if(options?.method==='PUT')throw Error('Roster changed. Reload before saving.');return original(url,options);};
  w.genericView.querySelector('[data-session-commit]').click();await settle();
  assert.match(w.genericView.textContent,/Roster changed/);assert.match(w.genericView.textContent,/New Person/);assert.equal(w.genericView.querySelector('[data-session-commit]').disabled,false);
  assert.equal(calls.filter(c=>c.options?.method==='PUT').length,0);
 });
 test('Session Plan read endpoint checks job permission and reads all subjects without the media page cap',()=>{
  const endpoints=fs.readFileSync(path.resolve(root,'../Media/MediaProductionEndpoints.cs'),'utf8');const source=fs.readFileSync(path.resolve(root,'../Media/MediaProductionStore.cs'),'utf8');
  assert.match(endpoints,/MapGet\("\/api\/jobs\/\{jobId:guid\}\/session-plan"[^\n]+HasPermission\(user,jobId,"ViewJob"\)/);
  const method=source.slice(source.indexOf('public JsonElement SessionPlan'),source.indexOf('public void SubjectStatus'));
  assert.doesNotMatch(method,/limit 500\b/i);assert.match(method,/where s.job_id=\$1 and not s.is_system/);assert.match(method,/studio_workday_roster_progress/);assert.match(method,/cmd.Parameters.AddWithValue\(job\)/);
 });

test('Front Desk retains the scrolled seventh row, radio and double-click target on selection',async t=>{
 const {w,navigation}=setup(t,'frontdesk');w.StudioStatusOrder={sort:v=>v};
 w.configureFrontDesk(Array.from({length:10},(_,i)=>({id:'r'+i,jobId:'j'+i,status:'AwaitingDeposit',request:{clientName:'Client '+i,title:'Portrait '+i,requiredDeposit:100,eventDate:'2026-10-30'}})));
 const list=w.genericView.querySelector('.fd-appointment-scroll'),row=list.querySelector('[data-fd-row-open="r6"]');
 list.scrollTop=300;row.focus();row.click();
 assert.equal(w.genericView.querySelector('.fd-appointment-scroll'),list);
 assert.equal(list.querySelector('[data-fd-row-open="r6"]'),row);assert.equal(list.scrollTop,300);
 assert.equal(w.document.activeElement,row);assert.equal(row.getAttribute('aria-selected'),'true');assert.equal(row.tagName,'TR');assert.equal(row.tabIndex,0);
 assert.match(w.genericView.querySelector('.fd-selected-request').textContent,/Client 6/);
 row.dispatchEvent(new w.MouseEvent('dblclick',{bubbles:true}));await settle();
 assert.equal(navigation.at(-1).options.jobId,'j6');
});
test('Delivery retains the scrolled row through selection and delayed photo responses',async t=>{
 const {w,jobs}=setup(t,'delivery');jobs.splice(0,jobs.length,...Array.from({length:10},(_,i)=>({id:'j'+i,title:'Delivery '+i,status:'Ready for Delivery'})));
 const base=w.json;let release;const pending=new Promise(r=>release=r);
 w.json=async(url,options)=>url.includes('/media?')?(await pending,{assets:[],totalAssets:40}):base(url,options);
 await w.StudioWorkflowViews.showDelivery();
 const list=w.genericView.querySelector('.workflow-table-scroll'),row=list.querySelector('[data-delivery-row="j6"]');
 list.scrollTop=300;row.focus();row.querySelector('input').click();
 assert.equal(list.querySelector('[data-delivery-row="j6"]'),row);assert.equal(row.getAttribute('aria-selected'),'true');
 release();await settle();await settle();
 assert.equal(w.genericView.querySelector('.workflow-table-scroll'),list);assert.equal(list.scrollTop,300);
 assert.match(w.genericView.querySelector('.workflow-detail').textContent,/Delivery 6/);
 assert.equal(list.querySelectorAll('[aria-selected="true"]').length,1);
});

test('Session Plan actions live on the page and retain import and return navigation',async t=>{
 const {w,navigation}=setup(t,'session-plan');await w.StudioWorkflowViews.showSessionPlan('j');
 const toolbar=w.genericView.querySelector('[aria-label="Session Plan actions"]');
 assert.equal(w.document.querySelector('#studioContextRail [data-workflow-action]'),null);
 toolbar.querySelector('[data-workflow-action="import"]').click();await settle();
 assert.deepEqual(JSON.parse(JSON.stringify(navigation.at(-1))),{view:'session-plan-import',options:{jobId:'j'}});
 w.document.body.dataset.studioView='session-plan-import';await w.StudioWorkflowViews.showSessionPlan('j');
 assert.ok(w.genericView.querySelector('.session-import-sidebar [aria-current=step]'));
 assert.ok(w.genericView.querySelector('[data-session-file]'));
 w.genericView.querySelector('[data-workflow-action="job"]').click();await settle();
 assert.deepEqual(JSON.parse(JSON.stringify(navigation.at(-1))),{view:'workspaces',options:{jobId:'j'}});
});


test('Session Plan opens a job without intake and filters subjects by name, group and outcome',async t=>{
 const {w,jobs,roster}=setup(t,'session-plan');jobs[0].conversationId=null;roster.rows[1].collection='Other';
 await w.StudioWorkflowViews.showSessionPlan('j');
 const search=w.genericView.querySelector('[data-session-search]');search.value='David';search.dispatchEvent(new w.Event('input'));
 assert.match(w.genericView.querySelector('.sp-roster tbody').textContent,/David/);assert.doesNotMatch(w.genericView.querySelector('.sp-roster tbody').textContent,/Alicia/);
 const fresh=w.genericView.querySelector('[data-session-search]');fresh.value='';fresh.dispatchEvent(new w.Event('input'));
 const group=w.genericView.querySelector('[data-session-group]');group.value='2026';group.dispatchEvent(new w.Event('change'));
 assert.doesNotMatch(w.genericView.querySelector('.sp-roster tbody').textContent,/David/);
 const status=w.genericView.querySelector('[data-session-outcome]');status.value='Needs Retake';status.dispatchEvent(new w.Event('change'));
 assert.match(w.genericView.querySelector('.sp-roster tbody').textContent,/Maya/);assert.doesNotMatch(w.genericView.querySelector('.sp-roster tbody').textContent,/Alicia/);
});
test('direct subject editing preserves identity and untouched subjects and retains the form after conflict',async t=>{
 const {w,calls}=setup(t,'session-plan');await w.StudioWorkflowViews.showSessionPlan('j');
 w.genericView.querySelector('[data-session-edit="A1"]').click();await settle();
 const form=w.document.querySelector('dialog form');assert.equal(form.elements.reference.readOnly,true);form.elements.name.value='Alicia Corrected';
 const original=w.json;w.json=async(url,options)=>{if(options?.method==='PUT')throw Error('Session Plan changed. Reload before saving.');return original(url,options);};
 form.dispatchEvent(new w.Event('submit',{cancelable:true}));await settle();assert.match(form.textContent,/Session Plan changed/);assert.equal(form.elements.name.value,'Alicia Corrected');
 w.json=original;form.dispatchEvent(new w.Event('submit',{cancelable:true}));await settle();await settle();
 const payload=JSON.parse(calls.find(c=>c.options?.method==='PUT').options.body);
 assert.equal(payload.rows.length,3);assert.equal(payload.rows.find(r=>r.reference==='A1').subjectId,'a');assert.equal(payload.rows.find(r=>r.reference==='A1').name,'Alicia Corrected');assert.equal(w.document.querySelector('dialog'),null);
});
test('manual entry accepts an explicit stable reference and saves through the job endpoint',async t=>{
 const {w,calls}=setup(t,'session-plan');await w.StudioWorkflowViews.showSessionPlan('j');await w.StudioWorkflowViews.run('add-subject');
 const form=w.document.querySelector('dialog form');form.elements.name.value='New Student';form.elements.reference.value='N42';
 form.dispatchEvent(new w.Event('submit',{cancelable:true}));await settle();await settle();
 const save=calls.find(c=>c.options?.method==='PUT');assert.equal(save.url,'/api/jobs/j/session-plan/roster');assert.equal(JSON.parse(save.options.body).rows.at(-1).reference,'N42');
});
test('ambiguous people are blocked until the import explicitly selects the existing identity',async t=>{
 const {w,roster,calls}=setup(t,'session-plan');roster.rows.push({subjectId:'twin',reference:'A2',name:'Alicia',collection:'2026',pickupNotification:'None'});
 await w.StudioWorkflowViews.showSessionPlan('j');await w.StudioWorkflowViews.run('import');
 w.genericView.querySelector('[data-session-paste]').value='name,collection,contactEmail\nAlicia,2026,updated@example.com';w.genericView.querySelector('[data-session-pasted]').click();w.genericView.querySelector('[data-session-preview]').click();
 assert.equal(w.genericView.querySelector('[data-session-commit]').disabled,true);
 const match=w.genericView.querySelector('[data-session-match]');match.value='A2';match.dispatchEvent(new w.Event('change'));
 assert.equal(w.genericView.querySelector('[data-session-commit]').disabled,false);w.genericView.querySelector('[data-session-commit]').click();await settle();await settle();
 const rows=JSON.parse(calls.find(c=>c.options?.method==='PUT').options.body).rows;
 assert.equal(rows.length,4);assert.equal(rows.find(r=>r.reference==='A2').contactEmail,'updated@example.com');assert.equal(rows.find(r=>r.reference==='A1').contactEmail,undefined);
});
test('planned arrivals preview and save only selected people without altering outcomes',async t=>{
 const {w,calls}=setup(t,'session-plan');await w.StudioWorkflowViews.showSessionPlan('j');
 w.genericView.querySelector('[data-session-all]').click();w.genericView.querySelector('[data-session-plan]').click();await settle();
 const form=w.document.querySelector('dialog form');form.elements.start.value='2026-11-03T09:00';form.elements.location.value='Main Hall';form.elements.interval.value='10';
 assert.equal(form.querySelector('[type=submit]').disabled,true);form.querySelector('[data-preview]').click();assert.match(form.querySelector('[data-plan-preview]').textContent,/Alicia.*David.*Maya/s);
 form.dispatchEvent(new w.Event('submit',{cancelable:true}));await settle();await settle();
 const rows=JSON.parse(calls.find(c=>c.options?.method==='PUT').options.body).rows;
 assert.equal(rows.length,3);assert.equal(+new Date(rows[1].scheduledAtUtc)-+new Date(rows[0].scheduledAtUtc),600000);assert.equal(rows[0].scheduleLocation,'Main Hall');assert.ok(rows.every(r=>r.status===undefined));
});
test('paging keeps selections across pages without rendering thousands of subject rows',async t=>{
 const {w,roster}=setup(t,'session-plan');roster.rows=Array.from({length:250},(_,i)=>({subjectId:String(i),reference:'P'+i,name:'Person '+String(i).padStart(3,'0'),collection:'Group'}));
 await w.StudioWorkflowViews.showSessionPlan('j');assert.equal(w.genericView.querySelectorAll('.sp-roster tbody tr').length,100);
 w.genericView.querySelector('[data-session-all]').click();w.genericView.querySelector('[data-session-next]').click();
 assert.equal(w.genericView.querySelector('[data-session-all]').checked,false);assert.match(w.genericView.querySelector('[data-session-plan]').textContent,/100/);
 w.genericView.querySelector('[data-session-all]').click();w.genericView.querySelector('[data-session-prev]').click();assert.equal(w.genericView.querySelector('[data-session-all]').checked,true);assert.match(w.genericView.querySelector('[data-session-plan]').textContent,/200/);
});
test('website review accepts only the selected conflict and includes the reviewed revision',async t=>{
 const {w,roster,calls}=setup(t,'session-plan');roster.incomingRows=[{conversationId:'cv-review',rows:[{reference:'A1',name:'Alicia Website'}]}];
 await w.StudioWorkflowViews.showSessionPlan('j');w.genericView.querySelector('[data-session-conflict]').click();w.genericView.querySelector('[data-session-preview]').click();
 w.genericView.querySelector('[data-session-commit]').click();await settle();await settle();const payload=JSON.parse(calls.find(c=>c.options?.method==='PUT').options.body);
 assert.equal(payload.resolveConflict,true);assert.equal(payload.conflictConversationId,'cv-review');assert.equal(payload.revision,7);assert.equal(payload.rows.length,3);
});
test('assignment and remainder actions use the existing workflow editor and scheduling permissions',async t=>{
 const {w,roster}=setup(t,'session-plan'),opened=[];w.StudioWorkflow={open:async(...args)=>{opened.push(args);return w.document.createElement('dialog');}};
 await w.StudioWorkflowViews.showSessionPlan('j');await w.StudioWorkflowViews.run('assign-session');await w.StudioWorkflowViews.run('remainder');
 assert.deepEqual(opened.map(x=>x[1]),['[data-wf-session]:not([data-wf-remainder])','[data-wf-remainder]']);
 roster.canSchedule=false;await w.StudioWorkflowViews.showSessionPlan('j');assert.equal(w.genericView.querySelector('[data-session-plan]').disabled,true);await assert.rejects(()=>w.StudioWorkflowViews.run('assign-session'),/permission/);
});



test('configurable categories and three hierarchy levels filter without school-specific page logic',async t=>{
 const f=setup(t,'session-plan');
 for(let i=0;i<f.subjects.length;i++)f.subjects[i].plan={revision:2,category:i===2?'Products':'Performers',groups:[{name:'Region',value:'East'},{name:'Venue',value:i?'Harbour':'Town'},{name:'Cast',value:'A'}],role:'Participant',identityConfirmed:false,shots:[{name:'Programme portrait',status:'Pending',assetIds:[],approvedVersionId:null,note:''}]};
 await f.w.StudioWorkflowViews.showSessionPlan('j');
 assert.match(f.w.genericView.textContent,/Performers.*Products/s);assert.match(f.w.genericView.querySelector('[aria-label="Region"]').textContent,/East/);
 const venue=[...f.w.genericView.querySelectorAll('[data-sp-level="1"]')].find(b=>b.dataset.spValue==='Harbour');venue.click();
 assert.match(f.w.genericView.querySelector('.sp-roster tbody').textContent,/David/);assert.doesNotMatch(f.w.genericView.querySelector('.sp-roster tbody').textContent,/Alicia/);
 f.w.genericView.querySelector('[data-sp-category="Products"]').click();assert.match(f.w.genericView.querySelector('.sp-roster tbody').textContent,/Maya/);
});
test('shot coverage requires confirmed identity and a surviving approved version, independently of upload counts',async t=>{
 const f=setup(t,'session-plan');const p={revision:1,category:'Subjects',groups:[],role:'',identityConfirmed:true,shots:[{name:'Custom pose',status:'Approved',assetIds:['asset'],approvedVersionId:'version',note:''}]};
 f.subjects[0].plan=p;f.subjects[0].activeVersionIds=['version'];f.subjects[0].imageCount=18;
 assert.equal(f.w.StudioSessionPlan.coverage(f.subjects[0]),'Approved');
 f.subjects[0].activeVersionIds=[];assert.equal(f.w.StudioSessionPlan.coverage(f.subjects[0]),'Awaiting review');
 p.identityConfirmed=false;f.subjects[0].activeVersionIds=['version'];assert.equal(f.w.StudioSessionPlan.coverage(f.subjects[0]),'Awaiting review');
 p.shots[0].status='Needs retake';assert.equal(f.w.StudioSessionPlan.coverage(f.subjects[0]),'Needs retake');
});
test('subject detail loads every media page using the stable identity rather than the display name',async t=>{
 const f=setup(t,'session-plan'),read=f.w.json;
 f.w.json=async(url,options)=>{if(url.includes('/media?')){f.calls.push({url});const u=new URL(url,'https://studio.test');assert.equal(u.searchParams.get('subjectId'),'a');return u.searchParams.has('after')?{assets:[{id:'last',fileName:'last.jpg'}],hasMore:false}:{assets:Array.from({length:200},(_,i)=>({id:'asset'+i,fileName:'photo'+i+'.jpg'})),hasMore:true};}return read(url,options);};
 await f.w.StudioWorkflowViews.showSessionPlan('j');await f.w.genericView.querySelector('[data-sp-person="A1"]').onclick();
 assert.match(f.w.genericView.querySelector('.sp-identity').textContent,/Alicia.*A1/s);assert.equal(f.w.genericView.querySelectorAll('.sp-photos article').length,201);
});
test('imports carry configurable category and hierarchy columns without changing stable IDs',async t=>{
 const f=setup(t,'session-plan');await f.w.StudioWorkflowViews.showSessionPlan('j');await f.w.StudioWorkflowViews.run('import');
 f.w.genericView.querySelector('[data-session-paste]').value='Name,Reference,Category,Group path\nAlicia,A1,Performers,Region: East / Cast: A';
 f.w.genericView.querySelector('[data-session-pasted]').click();f.w.genericView.querySelector('[data-session-preview]').click();
 f.w.genericView.querySelector('[data-session-commit]').click();await settle();await settle();
 const payload=JSON.parse(f.calls.find(c=>c.options?.method==='PUT').options.body);
 assert.equal(payload.rows[0].subjectId,'a');assert.equal(payload.rows[0].planCategory,'Performers');assert.equal(payload.rows[0].planGroups,'Region: East / Cast: A');
});

test('the requirement editor saves configurable labels with the subject revision and version evidence',async t=>{
 const f=setup(t,'session-plan'),read=f.w.json;
 f.subjects[0].plan={revision:3,category:'Performers',groups:[{name:'Production',value:'Autumn'}],role:'Cast',identityConfirmed:true,shots:[{name:'Programme photograph',status:'Approved',assetIds:['asset-a'],approvedVersionId:'version-a',note:'Checked'}]};f.subjects[0].activeVersionIds=['version-a'];
 f.w.json=async(url,o)=>url.includes('/media?')?{assets:[{id:'asset-a',versionId:'version-a',fileName:'A1.jpg'}],hasMore:false}:read(url,o);
 await f.w.StudioWorkflowViews.showSessionPlan('j');f.w.genericView.querySelector('[data-sp-category="Performers"]').click();await f.w.genericView.querySelector('[data-sp-person="A1"]').onclick();await f.w.genericView.querySelector('[data-sp-configure]').onclick();
 const form=f.w.document.querySelector('dialog form');form.elements.groups.value='Production: Autumn\nCast: A';await form.onsubmit({preventDefault(){}});
 const write=f.calls.find(c=>c.url.endsWith('/session-plan/people/a'));assert.ok(write);const data=JSON.parse(write.options.body);
 assert.equal(data.revision,3);assert.equal(data.category,'Performers');assert.equal(data.groups[1].name,'Cast');assert.equal(data.shots[0].approvedVersionId,'version-a');assert.deepEqual(data.shots[0].assetIds,['asset-a']);
});
test('partial bulk saves retain approvals and retry only subjects that have not saved',async t=>{
 const f=setup(t,'session-plan'),read=f.w.json,writes=[];let fail=true;
 f.w.json=async(url,o)=>{if(o?.method==='PUT'&&url.includes('/session-plan/people/')){writes.push(url);if(url.endsWith('/b')&&fail){fail=false;throw Error('Offline');}return {};}return read(url,o);};
 await f.w.StudioWorkflowViews.showSessionPlan('j');f.w.genericView.querySelector('[data-session-all]').click();await f.w.genericView.querySelector('[data-sp-bulk]').onclick();
 const form=f.w.document.querySelector('dialog form');form.elements.category.value='Campaign';form.elements.path.value='Region: East / Team: A';form.elements.shots.value='Profile\nAction';
 await form.onsubmit({preventDefault(){}});assert.match(form.querySelector('[role=alert]').textContent,/1 of 3 saved/);
 await form.onsubmit({preventDefault(){}});assert.equal(writes.filter(x=>x.endsWith('/a')).length,1);assert.equal(writes.filter(x=>x.endsWith('/b')).length,2);assert.equal(writes.filter(x=>x.endsWith('/c')).length,1);
});

test('optional photographs do not block approved required coverage',t=>{
 const {w}=setup(t,'session-plan');const r={plan:{identityConfirmed:true,shots:[{name:'Main',status:'Approved',assetIds:['a'],approvedVersionId:'v'},{name:'Optional',required:false,status:'Needs retake',assetIds:[]}]},activeVersionIds:['v']};
 assert.equal(w.StudioSessionPlan.coverage(r),'Approved');
});

test('subject editor retains copied identification fields and optional photo instructions',async t=>{
 const {w,subjects,calls}=setup(t,'session-plan');subjects[0].plan={revision:2,category:'Performers',groups:[],role:'',identityConfirmed:false,identificationFields:[{name:'Permit',required:true}],identification:{Permit:'OLD'},shots:[{name:'Creative',required:false,instructions:'Campaign lighting',status:'Pending',assetIds:[],note:''}]};
 await w.StudioWorkflowViews.showSessionPlan('j');
 w.genericView.querySelector('[data-sp-category="Performers"]').click();w.genericView.querySelector('[data-sp-person="A1"]').click();await settle();await settle();w.genericView.querySelector('[data-sp-configure]').click();
 const form=w.document.querySelector('.sp-editor');form.querySelector('[data-identification]').value='P-123';form.querySelector('[type=submit]').click();await settle();await settle();
 const saved=JSON.parse(calls.find(c=>c.options?.method==='PUT').options.body);assert.equal(saved.identification.Permit,'P-123');assert.equal(saved.identificationFields[0].required,true);assert.equal(saved.shots[0].required,false);assert.equal(saved.shots[0].instructions,'Campaign lighting');
});
