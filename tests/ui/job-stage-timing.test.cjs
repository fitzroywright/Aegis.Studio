const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot'),read=file=>fs.readFileSync(path.join(root,file),'utf8');
const context=vm.createContext({window:{}});new vm.Script(read('job-details.js')).runInContext(context);const age=context.window.StudioJobDetails.stageAge;
const now=Date.parse('2026-10-30T18:00:00Z'),settle=async()=>{for(let i=0;i<6;i++)await new Promise(r=>setImmediate(r));};
test('stage age uses the recorded transition even while the next task is unstarted',()=>{
 assert.equal(age({data:{stage:'Awaiting Upload',enteredAt:'2026-10-28T15:00:00Z'},tasks:[{startedAtUtc:null}]},now),'2d 3h');
 assert.equal(age({data:{enteredAt:'2026-10-30T16:45:00Z'},tasks:[{startedAtUtc:'2026-10-20T00:00:00Z'}]},now),'1h 15m');
});
test('short durations remain visible and invalid timestamps never invent an age',()=>{
 assert.equal(age({enteredAt:'2026-10-30T17:15:00Z'},now),'45m');assert.equal(age({enteredAt:'2026-10-30T17:59:45Z'},now),'Less than a minute');assert.equal(age({enteredAt:'2026-10-31T00:00:00Z'},now),'Less than a minute');
 for(const enteredAt of [null,'','invalid'])assert.equal(age({enteredAt,createdAt:'2026-10-01'},now),'Timing unavailable');
});
test('legacy stage timing recovers only from a recorded entry into the current stage',()=>{
 assert.equal(age({stage:'Review',history:[{new_stage:'Review',entered_at_utc:'2026-10-20T18:00:00Z'},{new_stage:'Upload',entered_at_utc:'2026-10-30T17:30:00Z'},{new_stage:'Review',entered_at_utc:'2026-10-29T18:00:00Z'}]},now),'1d 0h');
 assert.equal(age({stage:'Review',history:[{new_stage:'Upload',entered_at_utc:'2026-10-29T18:00:00Z'}]},now),'Timing unavailable');
});
function setup(t){
 const f=require('./job-detail-fixture.cjs').environment(t),w=f.w;for(const script of ['studio-page.js','job-workflow-display.js','job-workflow-graph.js'])w.eval(read(script));
 w.Date.now=()=>now;return f;
}
test('Job Details shows stage time without any task start time and refreshes from window and document events',async t=>{
 const {w}=setup(t),read=w.json;let enteredAt='2026-10-30T17:15:00Z';
 w.json=async(url,options)=>url.endsWith('/connected-workflow')?{data:{stage:'Awaiting Upload',enteredAt}}:read(url,options);
 await w.openWorkspaceJob('job');await settle();assert.equal(w.document.querySelector('[data-stage-age]').textContent,'45m');assert.match(w.document.querySelector('#jobIdentityMenu').textContent,/Time in Stage/);
 enteredAt='2026-10-30T17:59:45Z';w.dispatchEvent(new w.CustomEvent('studio-workflow-changed',{detail:{jobId:'job'}}));await settle();assert.equal(w.document.querySelector('[data-stage-age]').textContent,'Less than a minute');
 enteredAt='2026-10-28T17:00:00Z';w.document.dispatchEvent(new w.CustomEvent('studio-workflow-changed',{detail:{jobId:'job'}}));await settle();assert.equal(w.document.querySelector('[data-stage-age]').textContent,'2d 1h');
});
test('a failed workflow graph cannot hide independently available stage timing',async t=>{
 const {w}=setup(t),read=w.json;w.json=async(url,options)=>{if(url.endsWith('/workflow-graph'))throw Error('Graph unavailable');if(url.endsWith('/connected-workflow'))return {data:{stage:'Pending',enteredAt:'2026-10-30T17:15:00Z'}};return read(url,options);};
 await w.openWorkspaceJob('job');await settle();assert.equal(w.document.querySelector('[data-stage-age]').textContent,'45m');
});
test('timing read failures are explicit and the next workflow event retries successfully',async t=>{
 const {w}=setup(t),read=w.json;let fail=true;w.json=async(url,options)=>{if(url.endsWith('/connected-workflow')){if(fail)throw Error('Offline');return {data:{enteredAt:'2026-10-30T17:15:00Z'}};}return read(url,options);};
 await w.openWorkspaceJob('job');await settle();assert.equal(w.document.querySelector('[data-stage-age]').textContent,'Timing unavailable');assert.ok(w.document.querySelector('[data-next-action]'));
 fail=false;w.dispatchEvent(new w.CustomEvent('studio-workflow-changed',{detail:{jobId:'job'}}));await settle();assert.equal(w.document.querySelector('[data-stage-age]').textContent,'45m');
});
test('late timing responses cannot replace a newer stage snapshot',async t=>{
 const {w}=setup(t),read=w.json;let release,count=0;const held=new Promise(r=>release=r);w.json=async(url,options)=>{if(url.endsWith('/connected-workflow')){const first=++count===1;if(first)await held;return {data:{enteredAt:first?'2026-10-20T00:00:00Z':'2026-10-30T17:15:00Z'}};}return read(url,options);};
 await w.openWorkspaceJob('job');await settle();w.dispatchEvent(new w.CustomEvent('studio-workflow-changed',{detail:{jobId:'job'}}));await settle();release();await settle();assert.equal(w.document.querySelector('[data-stage-age]').textContent,'45m');
});

test('the clock advances each minute and leaving the job stops the displayed clock',async t=>{
 const {w}=setup(t),read=w.json,ticks=[];let clock=now;
 w.Date.now=()=>clock;w.setInterval=fn=>{ticks.push(fn);return ticks.length;};
 w.json=async(url,options)=>url.endsWith('/connected-workflow')?{data:{enteredAt:'2026-10-30T17:15:00Z'}}:read(url,options);
 await w.openWorkspaceJob('job');await settle();clock+=60000;ticks.forEach(fn=>fn());assert.equal(w.document.querySelector('[data-stage-age]').textContent,'46m');
 w.document.body.dataset.studioView='media';await settle();clock+=60000;ticks.forEach(fn=>fn());assert.equal(w.document.querySelector('[data-stage-age]').textContent,'46m');
});
