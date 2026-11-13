const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
function setup(t){
 const dom=new JSDOM('<div id="host"></div>',{runScripts:'outside-only',url:'https://studio.test'}),w=dom.window;
 t.after(()=>w.close());w.eval(fs.readFileSync(path.join(root,'job-workflow-display.js'),'utf8'));return w;
}
function makeView(){
 const subflow={nodes:[{id:'start',label:'Start',kind:'Start'},{id:'edit',label:'Select',kind:'Task'},{id:'review',label:'Review',kind:'Task'},{id:'end',label:'End',kind:'End'}],edges:[]};
 const graph={nodes:[
  {id:'start',label:'Start',kind:'Start'},
  {id:'done',label:'Inquiry',kind:'Task'},
  {id:'current',label:'Shoot',kind:'Task'},
  {id:'available',label:'Production',kind:'Task'},
  {id:'waiting',label:'Delivery',kind:'Task'},
  {id:'blocked',label:'Approval',kind:'Task'},
  {id:'production',label:'Production work',kind:'Subflow',subflow},
  {id:'end',label:'Complete',kind:'End'}
 ],edges:[]};
 const states=[
  {id:'done',status:'Completed'},
  {id:'current',status:'InProgress'},
  {id:'available',status:'Available'},
  {id:'waiting',status:'Blocked',reason:'Waiting for active prerequisites'},
  {id:'blocked',status:'Blocked',reason:'Waiting for active prerequisites'},
  {id:'production/edit',status:'Completed'},
  {id:'production/review',status:'InProgress'},
  {id:'production/end',status:'Completed'}
 ];
 const tasks=[
  {nodeId:'done',status:'Completed'},
  {nodeId:'current',status:'InProgress'},
  {nodeId:'available',status:'NotStarted'},
  {nodeId:'waiting',status:'NotStarted',reason:'Waiting for active prerequisites'},
  {nodeId:'blocked',status:'Blocked',reason:'Client approval required'},
  {nodeId:'production/edit',status:'Completed'},
  {nodeId:'production/review',status:'InProgress'},
  {nodeId:'production/end',status:'Completed'}
 ];
 return {graph,evaluation:{nodes:states},tasks};
}
test('horizontal progress reflects completed stages, with no workflow changes',t=>{
 const w=setup(t),host=w.document.getElementById('host'),view=makeView(),before=JSON.stringify(view);let writes=0;
 w.json=()=>writes++;w.StudioJobGraph={open:()=>writes++};w.StudioJobWorkflowDisplay.render(host,view);
 const bar=host.querySelector('[role=progressbar]');assert.equal(bar.getAttribute('aria-valuenow'),'17');assert.match(bar.getAttribute('aria-valuetext'),/1 of 6 stages completed/);
 assert.equal(host.querySelector('.jd-workflow-progress-fill').style.width,'17%');assert.match(host.querySelector('.jd-workflow-progress-trigger').textContent,/17% complete/);assert.match(host.querySelector('.jd-workflow-progress-footer>small').textContent,/1 of 6/);
 assert.equal(host.querySelector('.jd-workflow-progress-trigger').getAttribute('aria-expanded'),'false');assert.equal(host.querySelector('.jd-workflow-drawer').getAttribute('aria-hidden'),'true');assert.equal(host.querySelector('.jd-workflow-drawer header'),null);assert.doesNotMatch(host.querySelector('.jd-workflow-drawer').textContent,/Job workflow|Hides after/);
 host.querySelector('.jd-workflow-progress-trigger').click();assert.equal(host.querySelector('.jd-workflow-drawer').getAttribute('aria-hidden'),'false');
 assert.ok(host.querySelector('.jd-workflow-stage.completed'));assert.ok(host.querySelector('.jd-workflow-stage.inprogress'));assert.ok(host.querySelector('.jd-workflow-stage.blocked'));assert.ok(host.querySelectorAll('.jd-workflow-connector').length);
 assert.equal(writes,0);assert.equal(JSON.stringify(view),before);
});
test('progress ignores graph endpoints and inactive stages, handles empty and fully completed workflows',t=>{
 const w=setup(t),host=w.document.getElementById('host');
 const view={graph:{nodes:[{id:'s',kind:'Start'},{id:'a',label:'A',kind:'Task'},{id:'b',label:'B',kind:'Task'},{id:'x',label:'Hidden',kind:'Task'},{id:'e',kind:'End'}]},evaluation:{nodes:[{id:'a',status:'Completed'},{id:'b',status:'Completed'},{id:'x',status:'Inactive'}]}};
 w.StudioJobWorkflowDisplay.render(host,view);assert.equal(host.querySelector('[role=progressbar]').getAttribute('aria-valuenow'),'100');assert.match(host.textContent,/2 of 2 stages completed/);
 w.StudioJobWorkflowDisplay.render(host,{graph:{nodes:[]}});assert.equal(host.querySelector('[role=progressbar]').getAttribute('aria-valuenow'),'0');assert.match(host.textContent,/0 of 0/);
 const skipped={graph:{nodes:[{id:'done',label:'Done',kind:'Task'},{id:'skip',label:'Skipped',kind:'Task'}]},tasks:[{nodeId:'done',status:'Completed'},{nodeId:'skip',status:'Skipped'}]};
 w.StudioJobWorkflowDisplay.render(host,skipped);assert.equal(host.querySelector('[role=progressbar]').getAttribute('aria-valuenow'),'50');
});
test('hover opens the drawer, leaving hides it, and it also closes after eight seconds',t=>{
 const w=setup(t),host=w.document.getElementById('host'),timers=new Map();let next=0;
 w.setTimeout=(callback,delay)=>{assert.equal(delay,8000);timers.set(++next,callback);return next;};w.clearTimeout=id=>timers.delete(id);
 w.StudioJobWorkflowDisplay.render(host,makeView());const area=host.querySelector('.jd-workflow-summary'),trigger=host.querySelector('.jd-workflow-progress-trigger'),drawer=host.querySelector('.jd-workflow-drawer');
 area.dispatchEvent(new w.Event('mouseenter'));assert.equal(area.dataset.open,'true');assert.equal(trigger.getAttribute('aria-expanded'),'true');assert.equal(drawer.inert,false);assert.equal(timers.size,1);
 area.dispatchEvent(new w.Event('mouseleave'));assert.equal(area.dataset.open,'false');assert.equal(drawer.inert,true);assert.equal(timers.size,0);
 trigger.click();const callback=[...timers.values()][0];callback();assert.equal(area.dataset.open,'false');assert.equal(trigger.getAttribute('aria-expanded'),'false');
});
test('keyboard and touch access work and dismissal returns focus from inside the drawer',async t=>{
 const w=setup(t),host=w.document.getElementById('host');w.StudioJobWorkflowDisplay.render(host,makeView());const trigger=host.querySelector('.jd-workflow-progress-trigger'),area=host.querySelector('.jd-workflow-summary');
 trigger.dispatchEvent(new w.KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true,cancelable:true}));assert.equal(area.dataset.open,'true');assert.equal(w.document.activeElement,host.querySelector('[data-workflow-close]'));
 w.document.activeElement.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}));assert.equal(area.dataset.open,'false');assert.equal(w.document.activeElement,trigger);
 trigger.click();assert.equal(area.dataset.open,'true');host.querySelector('[data-workflow-close]').click();assert.equal(area.dataset.open,'false');
 trigger.click();const outside=w.document.createElement('button');w.document.body.append(outside);host.querySelector('[data-workflow-close]').focus();outside.focus();await new Promise(r=>setImmediate(r));assert.equal(area.dataset.open,'false');
});
test('the original diagram retains subflow expansion inside the drawer',t=>{
 const w=setup(t),host=w.document.getElementById('host');let edits=0;w.StudioJobGraph={open:()=>edits++};w.StudioJobWorkflowDisplay.render(host,makeView());host.querySelector('.jd-workflow-progress-trigger').click();
 assert.equal(host.querySelector('.jd-subflow-diagram'),null);host.querySelector('[data-subflow-stage="production"]').click();assert.ok(host.querySelector('.jd-subflow-diagram'));assert.match(host.textContent,/Start Production work/);assert.match(host.textContent,/End Production work/);
 host.querySelector('[data-endpoint="start"]').click();assert.equal(host.querySelector('.jd-subflow-diagram'),null);assert.equal(edits,0);
});
test('replacing a job cancels old timers and escapes configured stage names',t=>{
 const w=setup(t),host=w.document.getElementById('host'),timers=new Map();let serial=0;
 w.setTimeout=callback=>{timers.set(++serial,callback);return serial;};w.clearTimeout=id=>timers.delete(id);
 w.StudioJobWorkflowDisplay.render(host,makeView());host.querySelector('.jd-workflow-progress-trigger').click();const oldCallback=[...timers.values()][0];
 w.StudioJobWorkflowDisplay.render(host,{graph:{nodes:[{id:'a',label:'<img src=x>',kind:'Task'}]}});assert.equal(timers.size,0);host.querySelector('.jd-workflow-progress-trigger').click();oldCallback();assert.equal(host.querySelector('.jd-workflow-summary').dataset.open,'true');assert.equal(host.querySelector('img'),null);assert.match(host.querySelector('.jd-workflow-stage').getAttribute('aria-label'),/<img src=x>/);
});

test('stage metadata stays alongside the completion count and retains live values on redraw',t=>{
 const w=setup(t),host=w.document.getElementById('host');host.innerHTML='<dl class="jd-job-stage-meta"><div><dt>Current Job Stage</dt><dd>Editing</dd></div><div><dt>Time in Stage</dt><dd data-stage-age>4h</dd></div></dl>';
 const meta=host.firstElementChild,view=makeView();w.StudioJobWorkflowDisplay.render(host,view);
 assert.equal(host.querySelector('.jd-workflow-progress-footer').lastElementChild,meta);assert.ok(meta.previousElementSibling.matches('small'));
 meta.querySelector('[data-stage-age]').textContent='5h';w.StudioJobWorkflowDisplay.render(host,view);assert.equal(host.querySelectorAll('.jd-job-stage-meta').length,1);assert.equal(host.querySelector('.jd-job-stage-meta'),meta);assert.equal(meta.querySelector('[data-stage-age]').textContent,'5h');assert.equal(host.querySelector('.jd-workflow-progress-trigger .jd-job-stage-meta'),null);
});
