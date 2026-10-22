const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');

function setup(t){
 const dom=new JSDOM('<body><section id="next"></section></body>',{runScripts:'outside-only',url:'https://studio.test'});
 const w=dom.window;t.after(()=>w.close());w.StudioGraph={esc:String};
 w.json=async()=>{throw Error('Unexpected request');};
 w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/job-details.js'),'utf8'));
 return{w,host:w.document.querySelector('#next')};
}
function graph(statuses){
 const ids=['booking','shoot','production'];
 const labels=['Inquiry / Booking','Shoot','Production'];
 const nodes=[{id:'start',label:'Start',kind:'Start'}];
 const tasks=[],states=[];
 ids.forEach((id,index)=>{
  nodes.push({id,label:labels[index],kind:'Task'});
  const reason=statuses[index]==='Blocked'?'Waiting for prerequisite <script>':null;
  states.push({id,status:statuses[index],dependencies:index?[ids[index-1]]:[],reason});
  tasks.push({id:'task-'+id,nodeId:id,title:labels[index],status:statuses[index],eligibility:statuses[index],assignedTo:index===1?'Photographer':null,role:index===1?'Photographer':null,reason,dependencyLabels:index?[labels[index-1]]:[]});
 });
 nodes.push({id:'end',label:'Complete',kind:'End'});
 return{graph:{nodes,edges:[]},evaluation:{nodes:states,edges:{}},tasks};
}
test('next action comes from the graph task and opens that node',t=>{
 const{w,host}=setup(t),view=graph(['Completed','Available','Blocked']);let selected;
 w.StudioJobDetails.nextStep(host,view,id=>selected=id);
 assert.match(host.querySelector('.jd-next-step-action-link').textContent,/Shoot/);
 host.querySelector('button').click();assert.equal(selected,'shoot');assert.equal(host.dataset.state,'ready');
});
test('blocked graph task shows its reason as escaped text',t=>{
 const{w,host}=setup(t),view=graph(['Completed','Blocked','NotStarted']);
 w.StudioJobDetails.nextStep(host,view,()=>{});
 assert.match(host.textContent,/Blocked: Waiting for prerequisite/);assert.equal(host.querySelector('script'),null);assert.equal(host.dataset.state,'blocked');
});
test('completed graph has no next action and workflow failure offers one retry',t=>{
 const{w,host}=setup(t);
 w.StudioJobDetails.nextStep(host,graph(['Completed','Completed','Completed']),()=>{});
 assert.match(host.textContent,/Workflow Complete/);assert.equal(host.querySelector('[data-next-step-action]'),null);
 let retried=false;w.StudioJobDetails.nextStep(host,null,()=>retried=true);host.querySelector('button').click();assert.equal(retried,true);
});
test('Job Details loads and expands one workflow graph without a legacy workflow request or dialog',async t=>{
 const{environment}=require('./job-detail-fixture.cjs'),{w,fixture}=environment(t);
 await w.openWorkspaceJob('job');await new Promise(r=>setTimeout(r,40));
 const next=w.document.querySelector('.job-detail-heading .jd-next-step');assert.ok(next);assert.match(next.textContent,/Next Required Action/);
 assert.ok(fixture.calls.some(c=>c.url.endsWith('/workflow-graph')));
 assert.equal(fixture.calls.some(c=>c.url.endsWith('/connected-workflow')),false);
 next.querySelector('[data-next-step-action]').click();await new Promise(r=>setTimeout(r,20));
 assert.ok(w.document.querySelector('.jd-flow-drawer.is-open'));
 assert.equal(w.document.querySelectorAll('.job-detail-workflow-dialog').length,0);
});