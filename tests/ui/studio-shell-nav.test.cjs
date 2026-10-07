const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');

const code=fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/studio-shell-nav.js'),'utf8');

function setup(){
 const dom=new JSDOM('<body data-studio-view="mywork"><nav id="studioBreadcrumb"></nav><div id="studioPageTools"></div></body>',{url:'https://studio.test/',runScripts:'outside-only'});
 const w=dom.window;
 const calls=[];
 w.studioNavigate=async view=>{calls.push(view);w.document.body.dataset.studioView=view;};
 w.eval(code);
 w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
 return {w,dom,calls};
}

test('Back stays inside Studio and Quick Links persist',async()=>{
 const {w,dom,calls}=setup();
 const tools=w.document.getElementById('studioPageTools');
 assert.ok(tools.querySelector('[data-studio-back]'));
 assert.ok(tools.querySelector('.studio-quick-links'));
 assert.ok(w.document.getElementById('studioBreadcrumb').textContent.includes('My Work'));

 await w.studioNavigate('workspaces');
 tools.querySelector('[data-studio-back]').click();
 await new Promise(resolve=>setTimeout(resolve,0));
 assert.deepEqual(calls,['workspaces','mywork']);
 assert.equal(w.location.href,'https://studio.test/');
 dom.window.close();
});

test('Quick Links and breadcrumbs are present after page tools are repainted',async()=>{
 const {w,dom}=setup();
 const tools=w.document.getElementById('studioPageTools');
 await w.studioNavigate('production');
 tools.innerHTML='<div class="page-specific">Page tool</div>';
 await new Promise(resolve=>setTimeout(resolve,0));
 assert.ok(tools.querySelector('[data-studio-persistent-tools]'));
 assert.ok(tools.querySelector('.page-specific'));
 assert.match(w.document.getElementById('studioBreadcrumb').textContent,/My Work.*Production/);
 dom.window.close();
});
