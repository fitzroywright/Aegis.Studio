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
 const roster={revision:7,canEdit:true,rows:[{subjectId:'a',reference:'A1',name:'Alicia',collection:'2026',pickupNotification:'None'}]};
 const subjects=[{id:'a',reference:'A1',name:'Alicia',collection:'2026',status:'Photographed',imageCount:3},{id:'b',reference:'B1',name:'David',collection:'2026',status:'Absent',imageCount:0},{id:'c',reference:'C1',name:'Maya',collection:'2026',status:'Needs Retake',imageCount:1}];
 const jobs=[{id:'j',conversationId:'cv',title:'Campion Graduation',clientName:'Campion College',clientId:'client',status:'Ready for Delivery'},{id:'j2',title:'Carter Wedding',clientName:'Carter',status:'Published'}];
 w.json=async(url,options)=>{calls.push({url,options});if(options?.method==='PUT')return {...roster,revision:8};if(url==='/api/jobs')return jobs;if(url==='/api/jobs/j')return jobs[0];if(url.endsWith('/session-plan'))return subjects;if(url.endsWith('/access'))return {permissions:{editJob:true}};if(url.includes('/intake/')&&url.endsWith('/roster'))return roster;if(url.endsWith('/finance-release')){if(url.includes('j2'))throw Error('Accounts unavailable');return {decision:{balance:12500}};}if(url==='/api/auth/me')return {user:{isAdministrator:true}};return [];};
 w.fetch=async()=>({ok:true,json:async()=>[]});
 for(const file of ['studio-page.js','frontdesk.js','studio-workflow-views.js','studio-shell-nav.js'])w.eval(read(file)+(file==='frontdesk.js'?'\nwindow.configureFrontDesk=rows=>{frontDeskRequests=rows;renderFrontDesk();};':''));
 w.eval(fs.readFileSync(path.resolve(root,'../../Aegis.Studio.PublicSite/public/roster-editor.js'),'utf8'));
 w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
 return {w,calls,navigation,roster,subjects,jobs};
}
test('Session Plan reads outcomes and full photo counts separately from roster import',async t=>{
 const {w,calls}=setup(t,'session-plan');await w.StudioWorkflowViews.showSessionPlan('j');
 assert.match(w.genericView.textContent,/Alicia.*Photographed.*3/s);assert.match(w.genericView.textContent,/David.*Absent/s);
 assert.match(w.document.getElementById('studioContextRail').textContent,/Reshoots1/);
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
 const payload=JSON.parse(saved.options.body);assert.equal(payload.revision,7);assert.equal(payload.rows.length,1);assert.equal(payload.rows[0].subjectId,'a');assert.equal(payload.rows[0].name,'Alicia Updated');assert.equal(payload.resolveConflict,false);
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
 assert.equal(w.genericView.querySelectorAll('[data-fd-tab]').length,4);
 assert.match(w.genericView.querySelector('.fd-selected-request').textContent,/Deposit remaining.*5000/s);
 assert.match(w.genericView.querySelector('[data-fd-row-open]').textContent,/Awaiting deposit/);
 let payment;w.launchFrontDeskPos=async(action,context)=>{payment={action,context};};
 w.genericView.querySelector('[data-fd-deposit]').click();await settle();assert.equal(payment.context.bookingId,'r');
 const original=w.json;w.json=async(url,options)=>url.endsWith('/workflow-graph')?{revision:12}:url.endsWith('/workflow-graph/actions')?(calls.push({url,options}),{}):original(url,options);
 w.eval('showFrontDesk=async()=>{};');w.genericView.querySelector('[data-fd-confirm]').click();await settle();
 const command=calls.find(c=>c.url.endsWith('/workflow-graph/actions'));assert.equal(JSON.parse(command.options.body).expectedRevision,12);
});
test('production keeps the four approved lanes and opens existing work-order editors',async t=>{
 const {w}=setup(t,'production');
 w.eval(read('workflow-graph.js'));w.eval(read('workload-overview.js'));w.eval(read('commercial.js'));
 const data={jobs:[{id:'j',title:'Campion',status:'Active Production'}],activities:[{id:'a',jobId:'j',workOrderId:'o',title:'Retouch portraits',activityType:'Retouch',status:'InProgress',assignedTo:'Tamara'}],orders:[{id:'o',jobId:'j',activityIds:['a']}],eligibility:[{activityId:'a',status:'InProgress'}]};
 const order={id:'o',jobId:'j',jobTitle:'Campion',product:'Portraits',quantity:1,tasks:[{id:'a',status:'InProgress',title:'Retouch portraits',assignedTo:'Tamara',step:{activityType:'Retouch'}}]};
 w.loadWorkspaces=async()=>[];w.json=async url=>url==='/api/production/workload'?data:url.startsWith('/api/production/work-orders?')?[order]:[];
 w.eval('openCommercialWorkOrder=async order=>{window.openedOrder=order;};');
 await w.showCatalogProduction();
 assert.deepEqual([...w.genericView.querySelectorAll('[data-production-lane] h2')].map(e=>e.textContent),['Ready to Start','In Production','Quality Control','Rework']);
 assert.ok(w.genericView.querySelector('[data-production-lane="progress"] [data-order-row="o"]'));
 assert.ok(w.genericView.querySelector('[data-production-due]'));
 w.genericView.querySelector('[data-order]').click();assert.equal(w.openedOrder.id,'o');
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
 const list=w.genericView.querySelector('.fd-table-scroll'),row=list.querySelector('[data-fd-row-open="r6"]');
 list.scrollTop=300;row.focus();row.click();
 assert.equal(w.genericView.querySelector('.fd-table-scroll'),list);
 assert.equal(list.querySelector('[data-fd-row-open="r6"]'),row);assert.equal(list.scrollTop,300);
 assert.equal(w.document.activeElement,row);assert.equal(row.getAttribute('aria-selected'),'true');assert.equal(row.querySelector('input').checked,true);
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
 toolbar.querySelector('[data-workflow-action="import"]').click();
 assert.ok(w.genericView.querySelector('[data-session-file]'));
 w.genericView.querySelector('[data-workflow-action="job"]').click();await settle();
 assert.deepEqual(JSON.parse(JSON.stringify(navigation.at(-1))),{view:'workspaces',options:{jobId:'j'}});
});
