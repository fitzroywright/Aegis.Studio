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
test('Job Details shows one status-colored display and never opens the workflow editor',t=>{
 const w=setup(t),host=w.document.getElementById('host'),view=makeView();let editorOpens=0,canvasCalls=0;
 w.StudioJobGraph={open:()=>{editorOpens++;}};
 w.StudioGraph={canvas:()=>{canvasCalls++;}};
 w.StudioJobWorkflowDisplay.render(host,view);
 const stage=id=>host.querySelector('[aria-label^="'+id+'"]');
 assert.equal(host.querySelector('[data-stage="done"]'),null);
 assert.equal(host.querySelector('.jd-workflow-stage.completed .jd-workflow-stage-icon').textContent,'✓');
 assert.ok(host.querySelector('.jd-workflow-stage.inprogress'));
 assert.ok(host.querySelector('.jd-workflow-stage.available'));
 assert.ok(host.querySelector('.jd-workflow-stage.pending'));
 assert.equal(host.querySelector('.jd-workflow-stage.blocked .jd-workflow-stage-icon').textContent,'!');
 host.querySelectorAll('.jd-workflow-stage[role="listitem"]').forEach(node=>node.click());
 assert.equal(editorOpens,0);assert.equal(canvasCalls,0);
});
test('subflow appears only after its stage is clicked and matches the Start/End curved-route sketch',t=>{
 const w=setup(t),host=w.document.getElementById('host'),view=makeView();let editorOpens=0;
 w.StudioJobGraph={open:()=>{editorOpens++;}};w.StudioJobWorkflowDisplay.render(host,view);
 assert.equal(host.querySelector('.jd-subflow-diagram'),null);
 const stage=host.querySelector('[data-subflow-stage="production"]');assert.ok(stage);
 stage.click();
 const svg=host.querySelector('.jd-subflow-diagram');assert.ok(svg);
 assert.match(host.textContent,/Start Production work/);assert.match(host.textContent,/End Production work/);
 assert.equal(svg.querySelectorAll('.jd-subflow-route').length,3);
 assert.equal(svg.querySelector('.jd-subflow-main-dashes'),null);
 assert.equal(svg.querySelector('rect'),null);
 assert.equal(svg.querySelectorAll('.jd-subflow-label[transform]').length,0);
 assert.equal(editorOpens,0);
 host.querySelector('[data-endpoint="start"]').click();
 assert.equal(host.querySelector('.jd-subflow-diagram'),null);
});
