const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');

function setup(t){
 const dom=new JSDOM('<body><section id="next"></section></body>',{runScripts:'outside-only',url:'https://studio.test'});
 const w=dom.window;t.after(()=>w.close());
 w.json=async()=>{throw Error('Unexpected request');};
 for(const file of ['workflow-graph.js','job-details.js','job-workflow-graph.js'])w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/'+file),'utf8'));
 w.studioDialog=(title,html)=>{const d=w.document.createElement('dialog');d.innerHTML=html;d.close=()=>d.remove();w.document.body.append(d);return d;};
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
test('next action uses the graph task long description and opens its node',t=>{
 const{w,host}=setup(t),view=graph(['Completed','Available','Blocked']);let selected;
 view.tasks[1].description='Photograph the scheduled subjects';
 w.StudioJobDetails.nextActionLink(host,view,id=>selected=id);
 assert.match(host.querySelector('[data-next-action]').textContent,/Photograph the scheduled subjects/);
 host.querySelector('button').click();assert.equal(selected,'shoot');assert.equal(w.StudioJobDetails.workflowAction(view).status,'Available');
});
test('blocked graph task dialog escapes its reason and offers no transition',async t=>{
 const{w,host}=setup(t),view=graph(['Completed','Blocked','NotStarted']);
 w.json=async url=>url.endsWith('/workflow-graph')?view:url.endsWith('/access')?{permissions:{editJob:true}}:url==='/api/auth/me'?{user:{userName:'manager'}}:{};
 const d=await w.StudioJobGraph.launchTask('job','task-shoot');
 assert.match(d.textContent,/Waiting for prerequisite <script>/);assert.equal(d.querySelector('script'),null);
 assert.equal(d.querySelector('[data-start],[data-complete]'),null);
});
test('a completed graph has no outstanding action and leaves no stale action link',t=>{
 const{w,host}=setup(t);w.StudioJobDetails.nextActionLink(host,graph(['Completed','Available','Blocked']),()=>{});
 w.StudioJobDetails.nextActionLink(host,graph(['Completed','Completed','Completed']),()=>{});
 assert.equal(w.StudioJobDetails.workflowAction(graph(['Completed','Completed','Completed'])),null);assert.equal(host.querySelector('button'),null);
});
test('Job Details uses one display workflow and its next action opens the assigned task dialog',async t=>{
 const{environment}=require('./job-detail-fixture.cjs'),{w,fixture}=environment(t);
 w.studioDialog=(title,html)=>{const d=w.document.createElement('dialog');d.innerHTML='<div class="studio-dialog-body">'+html+'</div>';d.close=()=>d.remove();w.document.body.append(d);return d;};
 await w.openWorkspaceJob('job');await new Promise(r=>setTimeout(r,40));
 const next=w.document.querySelector('.job-detail-heading [data-next-action]');assert.ok(next);assert.match(next.textContent,/Production/);
 assert.ok(fixture.calls.some(c=>c.url.endsWith('/workflow-graph')));assert.equal(fixture.calls.some(c=>c.url.endsWith('/connected-workflow')),true);
 next.click();await new Promise(r=>setTimeout(r,20));assert.match(w.document.querySelector('dialog:not([id])').textContent,/Production/);
 assert.equal(w.document.querySelectorAll('.job-detail-workflow-dialog').length,0);
});
test('workflow load failure remains visible in the shared attention drawer and Retry reloads it',async t=>{
 const{environment}=require('./job-detail-fixture.cjs'),{w,fixture}=environment(t),original=w.json;let failed=true;
 w.json=async(url,options)=>{if(url.endsWith('/workflow-graph')&&failed)throw Error('Workflow offline');return original(url,options);};
 await w.openWorkspaceJob('job');await new Promise(r=>setTimeout(r,20));
 const drawer=w.document.getElementById('studioAttentionDrawer');assert.match(drawer.textContent,/Workflow unavailable: Workflow offline/);
 const retry=[...drawer.querySelectorAll('button')].find(b=>b.textContent==='Retry workflow →');assert.ok(retry);failed=false;await retry.onclick();
 assert.ok(w.document.querySelector('.jd-workflow-track [role=listitem]'));assert.doesNotMatch(drawer.textContent,/Workflow unavailable/);
});
