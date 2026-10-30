const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const read=file=>fs.readFileSync(path.join(root,file),'utf8');

function router(t,url='https://studio.test/') {
 const dom=new JSDOM(read('index.html'),{url,runScripts:'outside-only'});
 t.after(()=>dom.window.close());
 const w=dom.window;
 // Keep startup explicit so these checks do not launch unrelated Today requests.
 const listen=w.document.addEventListener.bind(w.document);
 let start;
 w.document.addEventListener=(event,handler,options)=>event==='DOMContentLoaded'?start=handler:listen(event,handler,options);
 w.eval(read('studio-navigation.js'));
 w.document.addEventListener=listen;
 return {w,start};
}

test('removed Operations destination cannot change the active page or request data',async t=>{
 const {w}=router(t);
 w.document.body.dataset.studioView='frontdesk';
 w.document.getElementById('genericView').textContent='Existing Front Desk';
 const calls=[];w.json=async url=>{calls.push(url);return [];};
 w.eval(read('management.js'));
 assert.equal(w.showOperationsManagement,undefined);
 await assert.rejects(w.studioNavigate('operations-management'),/Unknown Studio navigation view/);
 assert.equal(w.document.body.dataset.studioView,'frontdesk');
 assert.equal(w.document.getElementById('genericView').textContent,'Existing Front Desk');
 assert.deepEqual(calls,[]);
 assert.equal(typeof w.showAccounts,'function');
 assert.equal(typeof w.showManagement,'function');
});

test('saved Operations bookmark and browser history return to Today',async t=>{
 const {w,start}=router(t,'https://studio.test/#studio=operations-management');
 const destinations=[];
 w.studioNavigate=async view=>destinations.push(view);
 await start();
 w.dispatchEvent(new w.PopStateEvent('popstate',{state:{studioView:'operations-management'}}));
 assert.deepEqual(destinations,['dashboard','dashboard']);
});

test('shared navigation retains functional destinations without Operations',async t=>{
 const {w}=router(t);
 w.json=async()=>[];
 w.fetch=async()=>({ok:true,json:async()=>[]});
 w.eval(read('studio-shell-nav.js'));
 w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
 for(const view of ['today','frontdesk','jobs','production','schedule','media']) {
  w.document.body.dataset.studioView=view;
  w.StudioShell.sync(view);
  const rail=w.document.getElementById('studioNavigationTools');
  assert.equal(rail.querySelector('[data-studio-side="operations-management"]'),null);
  for(const destination of ['frontdesk','clients','jobs','production','schedule','media'])
   assert.ok(rail.querySelector(`[data-studio-side="${destination}"]`),destination);
 }
});

