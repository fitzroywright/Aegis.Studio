const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const {JSDOM}=require('jsdom');
const readScript=require('./studio-script-fixture.cjs');
const {api}=require('./shared-controller-fixture.cjs');

test('the complete application script order starts without a retired workspace renderer',async t=>{
 const html=fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/index.html'),'utf8');
 const dom=new JSDOM(html,{url:'https://studio.test/',runScripts:'outside-only'}),w=dom.window;
 t.after(()=>w.close());
 w.ResizeObserver=class{observe(){}disconnect(){}};
 w.requestAnimationFrame=fn=>w.setTimeout(fn,0);
 w.fetch=async(url)=>{
  const body=api(new URL(url,'https://studio.test'));
  return {ok:true,status:200,json:async()=>body,text:async()=>JSON.stringify(body),headers:{get:()=>null}};
 };
 for(const script of w.document.querySelectorAll('script[src]')){
  const name=new URL(script.src).pathname;
  assert.doesNotThrow(()=>vm.runInContext(readScript(name),dom.getInternalVMContext(),{filename:name}),name);
 }
 await w.studioTodayReady;
 assert.equal(typeof w.studioNavigate,'function');
 assert.equal(typeof w.openWorkspaceGallery,'function');
 assert.equal(w.eval('typeof renderWorkspaceAdminRow'),'undefined');
 await w.studioNavigate('staff-roles');
 assert.ok(w.document.getElementById('employeeAuthSource'),'Staff & Roles remains reachable after all scripts load');
});
