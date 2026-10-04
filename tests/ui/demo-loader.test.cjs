const {test}=require('node:test');const assert=require('node:assert/strict');const fs=require('node:fs');const path=require('node:path');const {JSDOM}=require('jsdom');
const code=fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/demo-loader.js'),'utf8');
function setup(parts){
 const dom=new JSDOM('',{runScripts:'outside-only'}),w=dom.window;let cancelled=false;
 Object.assign(w,{TextDecoder,fetch:async(url,options)=>{assert.equal(url,'/api/admin/demo/seed');assert.equal(options.method,'POST');assert.equal(options.headers.Accept,'application/x-ndjson');return {ok:true,headers:{get:()=> 'application/x-ndjson'},body:{getReader:()=>({read:async()=>parts.length?{value:parts.shift(),done:false}:{done:true},cancel:async()=>{cancelled=true;},releaseLock(){}})}};}});
 w.eval(code);return {w,dom,cancelled:()=>cancelled};
}
test('demo progress decodes split JSON and UTF-8 before accepting committed completion',async()=>{
 const bytes=new TextEncoder().encode(JSON.stringify({percent:52,stage:'Studio photographs',message:'Carter — photo 17/320'})+'\n'+JSON.stringify({percent:100,stage:'Complete',message:'Loaded',completed:true})+'\n');
 const dash=bytes.indexOf(226),{w,dom,cancelled}=setup([bytes.slice(0,dash+1),bytes.slice(dash+1,dash+2),bytes.slice(dash+2)]);const updates=[];
 const result=await w.studioLoadDemoData(p=>updates.push(p));assert.equal(updates.length,2);assert.equal(updates[0].message,'Carter — photo 17/320');assert.equal(result.percent,100);assert.equal(result.completed,true);assert.equal(cancelled(),true);dom.window.close();
});
test('server failure is surfaced without reporting 100 percent',async()=>{
 const {w,dom,cancelled}=setup([new TextEncoder().encode('{"percent":42,"stage":"Studio photographs"}\n{"failed":true,"message":"Rolled back"}\n')]);const updates=[];
 await assert.rejects(w.studioLoadDemoData(p=>updates.push(p)),/Rolled back/);assert.equal(updates.length,1);assert.equal(updates[0].percent,42);assert.equal(cancelled(),true);dom.window.close();
});
test('a disconnected progress stream cannot claim the seed completed',async()=>{
 const {w,dom}=setup([new TextEncoder().encode('{"percent":99,"stage":"Committing"}\n')]);
 await assert.rejects(w.studioLoadDemoData(()=>{}),/before demo loading completed/);dom.window.close();
});
