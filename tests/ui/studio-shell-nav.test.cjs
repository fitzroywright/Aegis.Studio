const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');

const code=fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/studio-shell-nav.js'),'utf8');

function setup(){
 const dom=new JSDOM('<body data-studio-view="mywork"><nav id="studioBreadcrumb"></nav><div id="studioPageTools"></div><div class="shell"><main></main></div><input id="studioSearch"></body>',{url:'https://studio.test/',runScripts:'outside-only'});
 const w=dom.window;
 const calls=[];
 w.studioNavigate=async view=>{calls.push(view);w.document.body.dataset.studioView=view;};
 w.eval(code);
 w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
 return {w,dom,calls};
}

test('Back stays inside Studio and My Work keeps its own left layout',async()=>{
 const {w,dom,calls}=setup();
 const tools=w.document.getElementById('studioPageTools');
 const rail=w.document.getElementById('studioContextRail');
 assert.ok(tools.querySelector('[data-studio-back]'));
 assert.ok(rail);
 assert.equal(w.document.body.dataset.studioView,'mywork');
 assert.ok(w.document.getElementById('studioBreadcrumb').textContent.includes('My Work'));

 await w.studioNavigate('workspaces');
 assert.equal(w.document.body.dataset.studioView,'workspaces');
 assert.equal(rail.querySelectorAll('.studio-context-card').length,3);
 tools.querySelector('[data-studio-back]').click();
 await new Promise(resolve=>setTimeout(resolve,0));
 assert.deepEqual(calls,['workspaces','mywork']);
 assert.equal(w.location.href,'https://studio.test/');
 dom.window.close();
});

test('Back uses an in-app parent even when the page was opened without Studio history',async()=>{
 const {w,dom,calls}=setup();
 w.document.body.dataset.studioView='jobs';
 // Simulate a direct/deep-linked page where the in-memory Studio history is empty.
 w.eval(code);
 w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
 const back=w.document.querySelector('[data-studio-back]');
 back.click();
 await new Promise(resolve=>setTimeout(resolve,0));
 assert.equal(calls.at(-1),'workspaces');
 assert.equal(w.location.href,'https://studio.test/');
 dom.window.close();
});

test('Every non-My-Work page gets Status Actions and Quick Links cards',async()=>{
 const {w,dom}=setup();
 await w.studioNavigate('production');
 const rail=w.document.getElementById('studioContextRail');
 assert.equal(rail.querySelectorAll('.studio-context-card').length,3);
 assert.match(rail.textContent,/Status/);
 assert.match(rail.textContent,/Actions/);
 assert.match(rail.textContent,/Quick Links/);
 assert.match(w.document.getElementById('studioBreadcrumb').textContent,/My Work.*Production/);

 const tools=w.document.getElementById('studioPageTools');
 tools.innerHTML='<div class="page-specific">Page tool</div>';
 await new Promise(resolve=>setTimeout(resolve,0));
 assert.ok(tools.querySelector('[data-studio-persistent-tools]'));
 assert.ok(tools.querySelector('.page-specific'));
 dom.window.close();
});
