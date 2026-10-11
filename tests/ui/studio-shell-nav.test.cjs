const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');

const code=fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/studio-shell-nav.js'),'utf8');

function setup(){
 const dom=new JSDOM('<body data-studio-view="today"><nav id="studioBreadcrumb"></nav><div id="studioPageTools"></div><div class="shell"><main></main></div><input id="studioSearch"></body>',{url:'https://studio.test/',runScripts:'outside-only'});
 const w=dom.window;
 const calls=[];
 w.studioNavigate=async view=>{calls.push(view);w.document.body.dataset.studioView=view;};
 w.eval(code);
 w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
 return {w,dom,calls};
}

test('breadcrumbs return to Today without leaving Studio',async t=>{
 const {w,dom,calls}=setup();t.after(()=>dom.window.close());
 await w.studioNavigate('workspaces');w.StudioShell.sync('workspaces');
 w.document.querySelector('[data-studio-crumb="today"]').click();
 await new Promise(r=>setTimeout(r,0));
 assert.deepEqual(calls,['workspaces','today']);assert.equal(w.location.href,'https://studio.test/');
});
test('shared rail keeps Navigation, My Workspaces and Actions in order',async t=>{
 const {w,dom}=setup();t.after(()=>dom.window.close());await w.studioNavigate('production');w.StudioShell.sync('production');
 const rail=w.document.getElementById('studioContextRail');
 assert.deepEqual([...rail.querySelectorAll('h2')].map(e=>e.textContent.trim().replace(/^[^A-Za-z]+/,'')),['Navigation','My Workspaces','Actions']);
 for(const view of ['today','jobs','messages'])assert.ok(rail.querySelector('[data-studio-side="'+view+'"]'));
 assert.match(w.document.getElementById('studioBreadcrumb').textContent,/Today.*Production/);
});
