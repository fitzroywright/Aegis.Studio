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

test('reset and regeneration starts a full server run and waits through reset and photo stages',async()=>{
 const runId='44444444-4444-4444-8444-444444444444';
 const {w,dom,calls}=setup([
  {url:'/api/admin/connected-workflow/demo-data/reset-and-generate',status:202,body:{runId,status:'Running',percent:0,stage:'Resetting'}},
  {url:'/api/admin/demo/seed/'+runId,body:{runId,status:'Running',percent:60,stage:'Studio photographs'}},
  {url:'/api/admin/demo/seed/'+runId,body:{runId,status:'Completed',percent:100,completed:true,message:'Full demo rebuilt'}}
 ]);
 const updates=[];const result=await w.studioLoadDemoData(p=>updates.push(p.stage),{reset:true});
 assert.deepEqual(updates,['Resetting','Studio photographs',undefined]);assert.equal(result.completed,true);
 assert.deepEqual(JSON.parse(calls[0].options.body),{confirmation:'REMOVE STUDIO DEMO DATA'});dom.window.close();
});

test('demo load starts a server run and polls until committed completion',async()=>{
 const runId='11111111-1111-4111-8111-111111111111';
 const {w,dom,calls}=setup([
  {url:'/api/admin/connected-workflow/demo-data/reset-and-generate',status:202,body:{runId,status:'Running',percent:0,stage:'Preparing',message:'Started',completed:false,failed:false}},
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
  {url:'/api/admin/connected-workflow/demo-data/reset-and-generate',status:202,body:{runId,status:'Running',percent:42,stage:'Studio photographs',message:'Working',completed:false,failed:false}},
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


test('reset rejection shows the server reason without starting polling',async()=>{
 const reason='Factory reset requires Storage:FactoryResetExclusiveRoot=true after verifying the configured storage root is dedicated to Studio.';
 const {w,dom,calls}=setup([
  {url:'/api/admin/connected-workflow/demo-data/reset-and-generate',status:409,body:{error:reason}},
  {url:'/api/admin/demo/seed/current',status:204,body:null}
 ]);
 await assert.rejects(w.studioLoadDemoData(()=>assert.fail('No run was started'),{reset:true}),{message:reason});
 assert.equal(calls.length,2);dom.window.close();
});

test('active-run conflict retains the wait instruction',async()=>{
 const reason='A demo load is already running. Wait for it to finish before resetting.';
 const {w,dom}=setup([{url:'/api/admin/connected-workflow/demo-data/reset-and-generate',status:409,body:{error:reason}},{url:'/api/admin/demo/seed/current',status:204,body:null}]);
 await assert.rejects(w.studioLoadDemoData(()=>{},{reset:true}),{message:reason});dom.window.close();
});

test('unstructured rejection retains an HTTP fallback',async()=>{
 const {w,dom}=setup([{url:'/api/admin/connected-workflow/demo-data/reset-and-generate',status:502,body:null}]);
 await assert.rejects(w.studioLoadDemoData(()=>{}),/Demo loader returned HTTP 502/);dom.window.close();
});

test('status failures also show the server reason',async()=>{
 const {w,dom}=setup([{url:'/api/admin/demo/seed/current',status:409,body:{error:'Run status is unavailable.'}}]);
 await assert.rejects(w.studioGetCurrentDemoDataRun(),/Run status is unavailable/);dom.window.close();
});


 test('a second browser follows the active recreation after a conflict',async()=>{
 const runId='55555555-5555-4555-8555-555555555555';
 const {w,dom,calls}=setup([
  {url:'/api/admin/connected-workflow/demo-data/reset-and-generate',status:409,body:{error:'Already running'}},
  {url:'/api/admin/demo/seed/current',body:{runId,status:'Running',percent:60}},
  {url:'/api/admin/demo/seed/'+runId,body:{runId,status:'Completed',completed:true,percent:100}}
 ]);
 const result=await w.studioLoadDemoData(()=>{});
 assert.equal(result.completed,true);assert.equal(calls.filter(x=>x.options.method==='POST').length,1);dom.window.close();
});
