const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
function setup(t){const dom=new JSDOM('<div id="host"></div><ol id="summary"></ol>',{runScripts:'outside-only',url:'https://studio.test'}),w=dom.window;t.after(()=>w.close());for(const file of ['workflow-graph.js','job-details.js','workload-overview.js'])w.eval(fs.readFileSync(path.join(root,file),'utf8'));return w;}
const task=(id,status='NotStarted',extra={})=>({id,jobId:'job',activityType:'Print',title:id,status,...extra});
const job={id:'job',status:'Active Production'};
test('partial physical output, QC and refund quantities remain separate from activities and catalog stage counts',t=>{
 const w=setup(t),a=task('print','InProgress'),b=task('qc','ReworkRequired');
 const data={productionJobIds:['job'],activities:[a,b],items:[{activityId:'print',kind:'Physical outputs',quantity:10,remaining:6,awaitingQc:4,rework:0,refundedProduced:2},{activityId:'qc',kind:'Physical outputs',quantity:3,remaining:0,awaitingQc:0,rework:3},{activityId:'print',kind:'Approved images/items',itemRecords:4},{activityId:'print',kind:'Other purchased outputs',quantity:2}],orders:[{id:'order',quantity:20,activityIds:['print','qc']}]};
 const m=w.StudioWorkload.model(data,[job]),s=m.summary(m.activities);assert.equal(s.activities,2);assert.equal(s.orderedUnits,20);assert.equal(s.orders,1);assert.equal(s.physical.progress,6);assert.equal(s.physical.awaitingQc,4);assert.equal(s.physical.rework,3);assert.equal(s.physical.refundedProduced,2);assert.equal(s.images.records,4);assert.equal(s.partialUnknown,1);assert.equal(m.matches(m.activities[0],'qc'),true);assert.equal(m.matches(m.activities[1],'item-rework'),true);
});
test('cancelled outputs, inactive branches and closed jobs never become ready work; missing quantities stay explicit',t=>{
 const w=setup(t),data={activities:[task('cancel','Cancelled'),task('inactive'),task('no-quantity')],eligibility:[{activityId:'inactive',status:'Inactive'}],items:[],orders:[],productionJobIds:[]};
 const m=w.StudioWorkload.model(data,[job]);assert.equal(m.bucket(m.activities[0]),'complete');assert.equal(m.bucket(m.activities[1]),'complete');assert.equal(m.summary(m.activities).missing,1);assert.equal(m.summary(m.activities).restricted,1);assert.match(w.StudioWorkload.cards(m,m.activities,w.StudioGraph.esc),/unavailable item quantities/);
 const closed=w.StudioWorkload.model({activities:[task('closed','InProgress')]},[{...job,status:'Cancelled'}]);assert.equal(closed.active(closed.activities[0]),false);
});
test('workflow prerequisites and approval gates separate waiting from executable work, with independent overdue flags',t=>{
 const w=setup(t),now=Date.now(),data={activities:[task('blocked','NotStarted',{dueAtUtc:new Date(now-1000).toISOString()}),task('ready'),task('client','WaitingOnClient')],eligibility:[{activityId:'blocked',status:'Blocked',reason:'Waiting for active prerequisites'},{activityId:'ready',status:'Available'}]};
 const m=w.StudioWorkload.model(data,[job],now);assert.equal(m.bucket(m.activities[0]),'waiting');assert.equal(m.overdue(m.activities[0]),true);assert.equal(m.bucket(m.activities[1]),'ready');assert.equal(m.bucket(m.activities[2]),'waiting');
 const gate=w.StudioWorkload.model({activities:[task('deposit')]},[{...job,status:'Pending'}]);assert.equal(gate.bucket(gate.activities[0]),'waiting');
});
function workflow(){
 const sub={nodes:[{id:'start',label:'Start',kind:'Start'},{id:'edit',label:'Edit',kind:'Task'},{id:'qc',label:'QC',kind:'Task'},{id:'end',label:'Complete',kind:'End'}],edges:[{id:'a',from:'start',to:'edit'},{id:'b',from:'start',to:'qc'},{id:'c',from:'edit',to:'end'},{id:'d',from:'qc',to:'end'}]};
 return {graph:{nodes:[{id:'start',label:'Start',kind:'Start'},{id:'produce',label:'Production',kind:'Subflow',subflow:sub},{id:'shoot',label:'Photography',kind:'Subflow',subflow:sub},{id:'delivery',label:'Delivery',kind:'Task'},{id:'end',label:'Complete',kind:'End'}],edges:[{id:'one',from:'start',to:'produce'},{id:'two',from:'start',to:'shoot'},{id:'three',from:'produce',to:'delivery'},{id:'four',from:'shoot',to:'delivery'},{id:'five',from:'delivery',to:'end'}]},evaluation:{completed:false,nodes:[{id:'produce/edit',status:'InProgress'},{id:'produce/qc',status:'Blocked',reason:'QC rejected'},{id:'produce/end',status:'Blocked'},{id:'shoot/edit',status:'Completed'},{id:'shoot/qc',status:'Skipped'},{id:'shoot/end',status:'Completed'},{id:'delivery',status:'Blocked'}],edges:{}},tasks:[{id:'qc-task',nodeId:'produce/qc',status:'Blocked',reason:'QC rejected: colour mismatch',assignedTo:null,role:'Production Manager',dependencyLabels:['Edit'],optional:false}]};
}
test('expanding a stage exposes endpoints and parallel dots, retains context and makes no state-changing request',t=>{
 const w=setup(t),view=workflow(),before=JSON.stringify(view),host=w.document.getElementById('host');let requests=0;w.json=async()=>{requests++;throw Error('Unexpected write');};
 w.StudioJobDetails.compact(host,view,job,w.document.getElementById('summary'));assert.equal(host.querySelectorAll('[data-workflow-node]').length,0);assert.equal(w.document.querySelectorAll('#summary [data-workflow-node="produce"]').length,1);
 w.document.querySelector('#summary [data-workflow-node="produce"]').click();assert.equal(host.querySelectorAll('.jd-dot-expansion').length,1);assert.match(w.document.getElementById('summary').textContent,/Start/);assert.match(w.document.getElementById('summary').textContent,/Complete/);assert.equal(host.querySelector('[data-workflow-node="shoot"]'),null);assert.ok(w.document.querySelector('#summary [data-workflow-node="shoot"]'));const edit=host.querySelector('[data-workflow-node="produce/edit"]'),qc=host.querySelector('[data-workflow-node="produce/qc"]');assert.equal(edit.closest('.jd-dot-level'),qc.closest('.jd-dot-level'));
 w.document.querySelector('#summary [data-workflow-node="shoot"]').click();assert.equal(host.querySelectorAll('.jd-dot-expansion').length,1);assert.equal(host.querySelector('[data-workflow-node="produce/edit"]'),null);w.document.querySelector('#summary [data-workflow-node="shoot"]').click();assert.equal(host.querySelectorAll('.jd-dot-expansion').length,0);assert.equal(requests,0);assert.equal(JSON.stringify(view),before);
});
test('step details expose exact blockers, unassigned responsibility and navigate to the existing command screen',async t=>{
 const w=setup(t),host=w.document.getElementById('host');let target;w.StudioJobGraph={open:async(...args)=>target=args};w.StudioJobDetails.compact(host,workflow(),job);host.querySelector('[data-workflow-node="produce"]').click();host.querySelector('[data-workflow-node="produce/qc"]').click();assert.match(host.textContent,/QC rejected: colour mismatch/);assert.match(host.textContent,/Responsible: Unassigned/);assert.match(host.textContent,/Production Manager/);await host.querySelector('[data-existing-task]').onclick();assert.deepEqual(target,['job','qc-task']);host.querySelector('[data-close-step]').click();assert.equal(w.document.activeElement.dataset.workflowNode,'produce/qc');
});
test('completed and optional substages follow evaluated completion; pending required work cannot be ticked by the UI',t=>{
 const w=setup(t),view=workflow(),host=w.document.getElementById('host');w.StudioJobDetails.compact(host,view,job);assert.match(host.querySelector('[data-workflow-node="shoot"]').getAttribute('aria-label'),/2 of 2 steps complete/);assert.ok(host.querySelector('[data-workflow-node="shoot"]').classList.contains('completed'));assert.ok(!host.querySelector('[data-workflow-node="produce"]').classList.contains('completed'));
 view.evaluation.nodes.find(x=>x.id==='shoot/qc').status='Blocked';w.StudioJobDetails.compact(host,view,job);assert.ok(!host.querySelector('[data-workflow-node="shoot"]').classList.contains('completed'));
});
test('single task workflows stay interactive and inactive service branches are excluded',t=>{
 const w=setup(t),host=w.document.getElementById('host'),view={graph:{nodes:[{id:'a',label:'Delivery',kind:'Task'},{id:'b',label:'Unused service',kind:'Task'}],edges:[]},evaluation:{nodes:[{id:'a',status:'Available'},{id:'b',status:'Inactive'}],edges:{}},tasks:[]};w.StudioJobDetails.compact(host,view,job);assert.equal(host.querySelectorAll('[data-workflow-node]').length,1);host.querySelector('button').click();assert.match(host.textContent,/Responsible: Unassigned/);assert.ok(host.querySelector('[data-existing-task]'));
});
test('canonical Production filters the existing work-order queue and preserves drilldown context',async t=>{
 const w=setup(t);w.document.body.innerHTML='<main id="genericView"></main><div id="conversationView"></div><span id="studioFooterRate"></span>';Object.assign(w,{genericView:w.document.getElementById('genericView'),conversationView:w.document.getElementById('conversationView'),esc:w.StudioGraph.esc,studioFirstName:x=>x,alert:message=>{throw Error(message);}});w.requestAnimationFrame=fn=>w.setTimeout(fn,0);w.ResizeObserver=class{observe(){}disconnect(){}};for(const f of ['studio-message-card.js','studio-page.js','commercial.js'])w.eval(fs.readFileSync(path.join(root,f),'utf8'));
 const workload={jobs:[{...job,workspaceId:'w',workspaceName:'Weddings'}],productionJobIds:['job'],activities:[task('print','InProgress',{assignedTo:'staff',productionArea:'Printing',workOrderId:'order'}),task('blocked','Blocked',{workOrderId:'blocked-order',productionArea:'Printing',blockedReason:'Approved source required'})],orders:[{id:'order',jobId:'job',quantity:30,activityIds:['print']},{id:'blocked-order',jobId:'job',quantity:12,activityIds:['blocked']}],items:[]};const requests=[];
 w.json=async u=>{requests.push(u);if(u==='/api/production/workload')return workload;if(u.startsWith('/api/production/work-orders')){const ids=new URL(u,'https://studio.test').searchParams.get('orderIds').split(',');return workload.orders.filter(o=>ids.includes(o.id)).map(o=>({...o,product:'Prints',jobTitle:'Wedding',tasks:workload.activities.filter(a=>o.activityIds.includes(a.id))}));}throw Error(u);};
 await w.showCatalogProduction();assert.equal(w.document.querySelectorAll('[data-order-row]').length,2);assert.match(w.genericView.textContent,/42 catalog ordered units across 2 work orders/);assert.match(w.genericView.textContent,/2 work orders have unknown partial output progress/);
 const selector=w.document.querySelector('[data-workload-filter-select]');selector.value='waiting';await selector.onchange({target:selector});await new Promise(r=>setTimeout(r,20));assert.equal(w.document.querySelectorAll('[data-order-row]').length,1);assert.ok(w.document.querySelector('[data-order-row="blocked-order"]'));assert.equal(w.studioWorkloadSelection.filter,'waiting');assert.ok(requests.every(u=>!u.includes('/media')));assert.ok(requests.filter(u=>u.includes('orderIds=')).every(u=>u.includes('offset=0')));
});
test('Operations separates delivery overdue, explainable concern and unknown dates, preserves filters and links to existing decisions',async t=>{
 const w=setup(t);w.document.body.innerHTML='<main id="genericView"></main><div id="conversationView"></div>';Object.assign(w,{genericView:w.document.getElementById('genericView'),conversationView:w.document.getElementById('conversationView'),esc:w.StudioGraph.esc,studioFirstName:x=>x,requestAnimationFrame:fn=>w.setTimeout(fn,0),ResizeObserver:class{observe(){}disconnect(){}}});for(const f of ['studio-message-card.js','studio-page.js','management.js'])w.eval(fs.readFileSync(path.join(root,f),'utf8'));
 const jobs=['late','risk','unknown'].map(id=>({id,status:'Active Production',title:id,workspaceId:'ws'}));const activities=[{...task('delivery-late'),jobId:'late',activityType:'Delivery',dueAtUtc:new Date(Date.now()-86400000).toISOString()},{...task('delivery-risk'),jobId:'risk',activityType:'Delivery',dueAtUtc:new Date(Date.now()+86400000).toISOString()},{...task('waiting','Blocked'),jobId:'risk',blockedReason:'Client approval required'},{...task('unknown'),jobId:'unknown'}];
 w.json=async u=>u==='/api/operations-management'?{jobs:jobs.map(j=>({jobId:j.id,workspaceId:'ws',stage:j.status,owner:'lead',timing:{light:'green'}})),overrideRequests:[],canDecideOverrides:false}:u==='/api/jobs'?jobs:u==='/api/workspaces'?[{id:'ws',name:'Weddings'}]:u==='/api/production/workload'?{activities,items:[],orders:[]}:[];
 let opened;w.openWorkspaceJob=async id=>opened=id;await w.showOperationsManagement();assert.equal(w.document.querySelector('[data-situation="delivery-overdue"] strong').textContent,'1');assert.equal(w.document.querySelector('[data-situation="delivery-risk"] strong').textContent,'1');assert.equal(w.document.querySelector('[data-situation="delivery-unknown"] strong').textContent,'1');assert.equal(w.document.querySelector('[data-operations-panel="overrides"]'),null);
 w.document.querySelector('[data-situation="delivery-risk"]').click();assert.equal(w.document.querySelectorAll('#operationsJobRows tr').length,1);await w.document.querySelector('#operationsJobRows [data-open-job]').onclick();assert.equal(opened,'risk');await w.showOperationsManagement();assert.equal(w.document.querySelector('#operationsAttentionFilter').value,'delivery-risk');assert.match(w.document.querySelector('#operationsJobRows').textContent,/risk/);assert.match(w.genericView.textContent,/equipment utilization: unknown/);
});
test('a recorded due date, not a blank or invalid timestamp, is required to call work overdue',t=>{
 const w=setup(t),m=w.StudioWorkload.model({activities:[task('none'),task('invalid','NotStarted',{dueAtUtc:'not-a-date'}),task('cancelled','Cancelled',{dueAtUtc:'2000-01-01'})]},[job]);assert.ok(m.activities.every(a=>!m.overdue(a)));
});

test('main icons toggle drawer on second click and subflows place endpoint icons around component path',t=>{
 const w=setup(t),host=w.document.getElementById('host'),summary=w.document.getElementById('summary'),events=[];
 host.addEventListener('studio-flow-details-toggle',e=>events.push(e.detail.open));
 w.StudioJobDetails.compact(host,workflow(),job,summary);
 summary.querySelector('[data-workflow-node="produce"]').click();
 assert.equal(events.at(-1),true);
 assert.equal(summary.querySelectorAll('[data-main-expanded] [data-workflow-node]').length,2);assert.equal(host.querySelectorAll('.jd-subflow-endpoint').length,0);assert.ok(summary.querySelector('.jd-main-dashed'));
 assert.ok(host.querySelector('.jd-dot-subflow>.jd-dot-levels [data-workflow-node="produce/edit"]'));
 summary.querySelector('[data-workflow-node="produce"]').click();
 assert.equal(events.at(-1),false);assert.equal(host.querySelector('.jd-dot-subflow'),null);assert.equal(summary.querySelector('[data-main-expanded]'),null);assert.equal(summary.querySelectorAll('[data-workflow-node="produce"]').length,1);
 summary.querySelector('[data-workflow-node="delivery"]').click();assert.equal(events.at(-1),true);
 summary.querySelector('[data-workflow-node="delivery"]').click();assert.equal(events.at(-1),false);
 assert.equal(host.querySelector('.jd-dot-details'),null);
});

test('curved connectors attach at dot rims and refresh after drawer transitions',t=>{
 const w=setup(t),host=w.document.getElementById('host'),summary=w.document.getElementById('summary');let frame;
 w.requestAnimationFrame=fn=>{frame=fn;return 1;};w.cancelAnimationFrame=()=>{};
 w.HTMLElement.prototype.getBoundingClientRect=function(){
  if(this===w.document.body)return{left:0,top:0,width:600,height:300};
  const button=this.closest?.('[data-workflow-node]'),id=button?.dataset.workflowNode;
  const left=button?.hasAttribute('data-main-start')?20:button?.hasAttribute('data-main-complete')?400:id==='produce/edit'?100:220;
  return{left,top:id?.includes('/')?150:20,width:12,height:12};
 };
 w.StudioJobDetails.compact(host,workflow(),job,summary);
 summary.querySelector('[data-workflow-node="produce"]').click();frame();
 const paths=[...w.document.querySelectorAll('[data-subflow-routes] path')];
 assert.equal(paths.length,4);assert.match(paths[0].getAttribute('d'),/^M 26 32 V /);
 assert.match(paths[0].getAttribute('d'),/ H 100$/);
 assert.ok(paths.every(p=>/ [CQ] /.test(p.getAttribute('d'))));assert.match(paths.at(-1).getAttribute('d'),/ V 32$/);
 w.document.body.dispatchEvent(new w.Event('transitionend'));frame();
 assert.equal(w.document.querySelectorAll('[data-subflow-routes]').length,1);
 summary.querySelector('[data-workflow-node="produce"]').click();
 assert.equal(w.document.querySelector('[data-subflow-routes]'),null);
});

test('workflow labels are short while tooltips preserve full step descriptions',t=>{
 const w=setup(t),host=w.document.getElementById('host'),view={graph:{nodes:[{id:'qc',label:'Quality control and final inspection',description:'Inspect colour and finish before packaging',kind:'Task'}],edges:[]},evaluation:{nodes:[{id:'qc',status:'Available'}],edges:{}},tasks:[]};
 w.StudioJobDetails.compact(host,view,job);
 const button=host.querySelector('[data-workflow-node]');
 assert.equal(button.querySelector('strong').textContent,'QC');
 assert.match(button.title,/Quality control and final inspection/);
 assert.match(button.title,/Inspect colour and finish before packaging/);
});

test('workflow dots use green ticks, amber executable work, red problems and gray future tasks',t=>{
 const w=setup(t),host=w.document.getElementById('host'),statuses=['Completed','Available','InProgress','Partial','ReadyForDelivery','Blocked','Failed','ReworkRequired','ReprintRequired','WaitingOnClient','Pending','Scheduled','Unknown'];
 const nodes=statuses.map((status,i)=>({id:'n'+i,label:status,kind:'Task'}));
 w.StudioJobDetails.compact(host,{graph:{nodes,edges:[]},evaluation:{nodes:nodes.map((n,i)=>({id:n.id,status:statuses[i]})),edges:{}},tasks:[]},job);
 const tones=['green','amber','amber','amber','amber','red','red','red','red','red','gray','gray','gray'];
 host.querySelectorAll('[data-workflow-node]').forEach((b,i)=>assert.equal(b.dataset.flowTone,tones[i]));
 assert.equal(host.querySelector('[data-flow-tone=green] .jd-stage-light').textContent,'✓');
});

test('completed activities retain unresolved physical QC and rework without remaining production',t=>{
 const w=setup(t),m=w.StudioWorkload.model({productionJobIds:['job'],activities:[task('printed','Completed')],items:[{activityId:'printed',kind:'Physical outputs',remaining:0,awaitingQc:5,rework:2}],orders:[]},[job]);
 const summary=m.summary(m.activities);assert.equal(summary.physical.awaitingQc,5);assert.equal(summary.physical.rework,2);assert.equal(summary.physical.progress,0);
});
test('Operations staff bottlenecks count only selected staff activities',async t=>{
 const w=setup(t);w.document.body.innerHTML='<main id="genericView"></main><div id="conversationView"></div>';Object.assign(w,{genericView:w.document.getElementById('genericView'),conversationView:w.document.getElementById('conversationView'),esc:w.StudioGraph.esc,studioFirstName:x=>x,requestAnimationFrame:fn=>w.setTimeout(fn,0),ResizeObserver:class{observe(){}disconnect(){}}});
 for(const f of ['studio-message-card.js','studio-page.js','management.js'])w.eval(fs.readFileSync(path.join(root,f),'utf8'));
 const jobs=[{id:'job',status:'Active Production',title:'Job',workspaceId:'ws'}];
 w.json=async u=>u==='/api/operations-management'?{jobs:[{jobId:'job',workspaceId:'ws',stage:'Active Production',owner:'lead',timing:{light:'green'}}],overrideRequests:[],canDecideOverrides:false}:u==='/api/jobs'?jobs:u==='/api/workspaces'?[{id:'ws',name:'Studio'}]:u==='/api/production/workload'?{activities:[task('alice','Blocked',{assignedTo:'alice',blockedReason:'Review'}),task('bob','Blocked',{assignedTo:'bob',blockedReason:'Review'})],items:[],orders:[] }:[];
 await w.showOperationsManagement();const select=w.document.querySelector('[data-operations-assignee]');select.value='alice';select.dispatchEvent(new w.Event('change'));
 assert.equal(w.document.querySelector('[data-bottleneck="Print"]').textContent,'Print: 1');
 assert.equal(w.document.querySelector('[data-staff-workload="bob"]'),null);
});

test('Production QC filter retains completed activities with unresolved item QC',async t=>{
 const w=setup(t);w.document.body.innerHTML='<main id="genericView"></main><div id="conversationView"></div><span id="studioFooterRate"></span>';Object.assign(w,{genericView:w.document.getElementById('genericView'),conversationView:w.document.getElementById('conversationView'),esc:w.StudioGraph.esc,studioFirstName:x=>x,alert:message=>{throw Error(message);}});w.requestAnimationFrame=fn=>w.setTimeout(fn,0);w.ResizeObserver=class{observe(){}disconnect(){}};for(const f of ['studio-message-card.js','studio-page.js','commercial.js'])w.eval(fs.readFileSync(path.join(root,f),'utf8'));
 const workload={jobs:[{...job,workspaceId:'w',workspaceName:'Weddings'}],productionJobIds:['job'],activities:[task('print','Completed',{assignedTo:'staff',productionArea:'Printing',workOrderId:'order'}),task('blocked','Blocked',{workOrderId:'blocked-order',productionArea:'Printing',blockedReason:'Approved source required'})],orders:[{id:'order',jobId:'job',quantity:30,activityIds:['print']},{id:'blocked-order',jobId:'job',quantity:12,activityIds:['blocked']}],items:[{activityId:'print',kind:'Physical outputs',awaitingQc:3,remaining:0}]};const requests=[];
 w.json=async u=>{requests.push(u);if(u==='/api/production/workload')return workload;if(u.startsWith('/api/production/work-orders')){const ids=new URL(u,'https://studio.test').searchParams.get('orderIds').split(',');return workload.orders.filter(o=>ids.includes(o.id)).map(o=>({...o,product:'Prints',jobTitle:'Wedding',tasks:workload.activities.filter(a=>o.activityIds.includes(a.id))}));}throw Error(u);};
 await w.showCatalogProduction();assert.equal(w.document.querySelectorAll('[data-order-row]').length,2);assert.match(w.genericView.textContent,/42 catalog ordered units across 2 work orders/);assert.match(w.genericView.textContent,/1 work orders have unknown partial output progress/);
 const selector=w.document.querySelector('[data-workload-filter-select]');selector.value='qc';await selector.onchange({target:selector});await new Promise(r=>setTimeout(r,20));assert.equal(w.document.querySelectorAll('[data-order-row]').length,1);assert.ok(w.document.querySelector('[data-order-row="order"]'));assert.equal(w.studioWorkloadSelection.filter,'qc');assert.ok(requests.every(u=>!u.includes('/media')));assert.ok(requests.filter(u=>u.includes('orderIds=')).every(u=>u.includes('offset=0')));
});