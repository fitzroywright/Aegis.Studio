const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('Job Details indicator uses the evaluated route when opened through the workspace',async t=>{
 const {environment}=require('./job-detail-fixture.cjs'),{w}=environment(t),original=w.json;
 w.json=async(url,options)=>url.endsWith('/workflow-graph')?{graph:graph(),evaluation:evaluation()}:original(url,options);
 await w.openWorkspaceJob('job');await new Promise(resolve=>setTimeout(resolve,20));
 const host=w.document.querySelector('.jd-workflow-track');
 assert.ok(host.querySelector('[data-node="edit"]'));
 assert.equal(host.querySelector('[data-node="print"]'),null);
 assert.equal(host.querySelectorAll('.sg-edge').length,2);
});
function setup(t){const dom=new JSDOM('<main></main><ul></ul>',{runScripts:'outside-only',url:'https://studio.test'});t.after(()=>dom.window.close());const w=dom.window;w.StudioWorkflow={nextAction:()=>({label:'Next action',selector:'[data-next]'})};for(const file of ['dagre.min.js','workflow-graph.js','job-details.js'])w.eval(fs.readFileSync(path.join(root,file),'utf8'));return {w,host:w.document.querySelector('main'),summary:w.document.querySelector('ul')};}
test('ordinary jobs do not advertise unused corrections or reshoots',t=>{
 const {w,host,summary}=setup(t);
 w.StudioJobDetails.workflow(host,{stage:'Active Production',sessions:[],history:[]},{jobType:'Photography'},{},null,summary);
 assert.equal(host.querySelectorAll('.jd-flow-exception-lane').length,0);
 assert.equal(host.querySelectorAll('.jd-compact-stage').length,6);
 assert.equal(summary.children.length,6);
 assert.ok(host.querySelector('[data-flow-stage="Completed"]'),'required future delivery remains visible');
});
test('current, evidenced and historical exception routes remain visible and actionable',t=>{
 const {w,host}=setup(t);let action;
 const draw=data=>w.StudioJobDetails.workflow(host,{sessions:[],history:[],...data},{id:'job'},{},selector=>action=selector);
 draw({stage:'Changes Requested',correctionKind:'Source'});
 assert.equal(host.querySelectorAll('.jd-flow-exception-lane').length,1);
 assert.equal(host.querySelector('.jd-flow-lane-return').textContent,'↗ Upload');
 host.querySelector('[data-flow-stage="Changes Requested"]').click();assert.equal(action,'[data-next]');
 draw({stage:'Active Production',history:[{new_stage:'Changes Requested',exited_at_utc:'now'},{new_stage:'Active Production'}]});
 assert.ok(host.querySelector('[data-flow-stage="Changes Requested"]'));
 assert.equal(host.querySelector('.jd-flow-exception-lane').classList.contains('is-active'),false);
 draw({stage:'Awaiting Upload',outstandingRoster:[{status:'Needs Retake'}]});
 assert.ok(host.querySelector('[data-flow-stage="Reshoot Required"]').classList.contains('current'));
 draw({stage:'Completed',history:[{new_stage:'Reshoot Required'}]});
 assert.ok(host.querySelector('[data-flow-stage="Reshoot Required"]'));
});
test('print-only jobs omit the unrequired shoot phase and photography gates',t=>{
 const {w,host,summary}=setup(t);
 w.StudioJobDetails.workflow(host,{stage:'Active Production',sessions:[],history:[]},{jobType:'Print'},{},null,summary);
 assert.equal(summary.querySelector('[data-flow-phase="Shoot"]'),null);
 assert.equal(host.querySelector('[data-flow-stage="Shoot Complete"]'),null);
 assert.ok(host.querySelector('[data-flow-stage="Needs Assignment"]'));
 assert.equal(host.querySelectorAll('.jd-compact-stage').length,5);
});
function graph(){return {nodes:[['start','Start'],['edit','Task'],['print','Task'],['end','End']].map(([id,kind])=>({id,kind,label:id})),edges:[['start','edit'],['edit','end'],['start','print'],['print','end']].map(([from,to],i)=>({id:'e'+i,from,to}))};}
function evaluation(){return {nodes:[{id:'start',status:'Completed'},{id:'edit',status:'Available'},{id:'print',status:'Inactive'},{id:'end',status:'Blocked'}],edges:{e0:'Completed',e1:'Pending',e2:'Inactive',e3:'Inactive'}};}
test('job graph filters inactive service paths before layout and keeps selected future work',t=>{
 const {w,host}=setup(t),g=graph(),before=JSON.stringify(g),ev=evaluation();
 const result=w.StudioGraph.canvas(host,g,{actualPath:true,evaluation:ev});
 assert.equal(host.querySelector('[data-node="print"]'),null);
 assert.equal(result.layout.hasNode('print'),false);
 assert.equal(host.querySelectorAll('.sg-edge').length,2);
 assert.ok(host.querySelector('[data-edge="e1"]').classList.contains('pending'));
 assert.ok(host.querySelector('[data-node="end"]'));
 assert.equal(JSON.stringify(g),before);
 ev.nodes.find(n=>n.id==='print').status='InProgress';
 w.StudioGraph.canvas(host,g,{actualPath:true,evaluation:ev});
 assert.ok(host.querySelector('[data-node="print"]'),'started work survives service changes');
 assert.equal(host.querySelector('[data-edge="e2"]'),null);
});
test('collapsed and expanded subflows omit inactive nested routes',t=>{
 const {w,host}=setup(t),g=graph(),nested=graph();g.nodes.find(n=>n.id==='edit').kind='Subflow';g.nodes.find(n=>n.id==='edit').subflow=nested;
 const ev=evaluation();ev.nodes=ev.nodes.filter(n=>n.id!=='edit').concat(evaluation().nodes.map(n=>({...n,id:'edit/'+n.id})));Object.assign(ev.edges,Object.fromEntries(Object.entries(evaluation().edges).map(([id,state])=>['edit/'+id,state])));
 w.StudioGraph.canvas(host,g,{actualPath:true,evaluation:ev});assert.ok(host.querySelector('[data-node="edit"]'));
 host.querySelector('[data-node="edit"]').ondblclick();
 assert.ok(host.querySelector('[data-node="edit/edit"]'));
 assert.equal(host.querySelector('[data-node="edit/print"]'),null);
 assert.ok(host.querySelector('[data-edge="e0"]'),'expanded subflow retains its incoming connection');
 ev.nodes.filter(n=>n.id.startsWith('edit/')).forEach(n=>n.status='Inactive');
 w.StudioGraph.canvas(host,g,{actualPath:true,evaluation:ev});
 assert.equal(host.querySelector('[data-node="edit"]'),null);
});
test('template canvases and canvases without evaluation retain the full definition',t=>{
 const {w,host}=setup(t);
 for(const options of [{evaluation:evaluation()},{actualPath:true,editable:true,evaluation:evaluation()},{actualPath:true}]){
  w.StudioGraph.canvas(host,graph(),options);
  assert.equal(host.querySelectorAll('.sg-node').length,4);
  assert.equal(host.querySelectorAll('.sg-edge').length,4);
 }
});
