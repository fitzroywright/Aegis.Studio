const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');

const code=fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/demo-loader.js'),'utf8');

function response(status,body){
 return {
  ok:status>=200&&status<300,
  status,
  json:async()=>body
 };
}

function setup(sequence){
 const dom=new JSDOM('',{runScripts:'outside-only'}),w=dom.window;
 const calls=[];
 w.setTimeout=(fn)=>{fn();return 0;};
 w.fetch=async(url,options={})=>{
  calls.push({url,options});
  if(!sequence.length)throw new Error('Unexpected fetch '+url);
  const next=sequence.shift();
  assert.equal(url,next.url);
  return response(next.status??200,next.body);
 };
 w.eval(code);
 return {w,dom,calls};
}

test('demo load starts a server run and polls until committed completion',async()=>{
 const runId='11111111-1111-4111-8111-111111111111';
 const {w,dom,calls}=setup([
  {url:'/api/admin/demo/seed',status:202,body:{runId,status:'Running',percent:0,stage:'Preparing',message:'Started',completed:false,failed:false}},
  {url:'/api/admin/demo/seed/'+runId,body:{runId,status:'Running',percent:52,stage:'Studio photographs',message:'Carter — photo 17/320',completed:false,failed:false}},
  {url:'/api/admin/demo/seed/'+runId,body:{runId,status:'Completed',percent:100,stage:'Complete',message:'Loaded',completed:true,failed:false}}
 ]);
 const updates=[];
 const result=await w.studioLoadDemoData(p=>updates.push(p));
 assert.deepEqual(updates.map(x=>x.percent),[0,52,100]);
 assert.equal(result.completed,true);
 assert.equal(result.message,'Loaded');
 assert.equal(calls[0].options.method,'POST');
 assert.equal(calls[0].options.headers.Accept,'application/json');
 dom.window.close();
});

test('server-side demo failure is surfaced from persisted run status',async()=>{
 const runId='22222222-2222-4222-8222-222222222222';
 const {w,dom}=setup([
  {url:'/api/admin/demo/seed',status:202,body:{runId,status:'Running',percent:42,stage:'Studio photographs',message:'Working',completed:false,failed:false}},
  {url:'/api/admin/demo/seed/'+runId,body:{runId,status:'Failed',percent:42,stage:'Failed',message:'Rolled back',completed:false,failed:true}}
 ]);
 const updates=[];
 await assert.rejects(w.studioLoadDemoData(p=>updates.push(p)),/Rolled back/);
 assert.deepEqual(updates.map(x=>x.percent),[42,42]);
 dom.window.close();
});

test('current demo run can be recovered after reopening Admin',async()=>{
 const runId='33333333-3333-4333-8333-333333333333';
 const {w,dom}=setup([
  {url:'/api/admin/demo/seed/current',body:{runId,status:'Running',percent:63,stage:'Studio photographs',message:'Continuing',completed:false,failed:false}}
 ]);
 const current=await w.studioGetCurrentDemoDataRun();
 assert.equal(current.runId,runId);
 assert.equal(current.percent,63);
 dom.window.close();
});
