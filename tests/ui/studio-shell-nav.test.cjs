const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');

const code=fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/studio-shell-nav.js'),'utf8');

function setup(){
 const dom=new JSDOM('<body data-studio-view="today"><header class="topbar"></header><nav id="studioBreadcrumb"></nav><div id="studioPageTools"></div><div class="shell"><main></main></div><input id="studioSearch"></body>',{url:'https://studio.test/',runScripts:'outside-only'});
 const w=dom.window;
 const calls=[];
 w.studioNavigate=async view=>{calls.push(view);w.document.body.dataset.studioView=view;};
 w.fetch=async()=>({ok:true,json:async()=>[]});
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
test('shared shell keeps contextual cards and one navigation toolstrip',async t=>{
 const {w,dom}=setup();t.after(()=>dom.window.close());await w.studioNavigate('production');w.StudioShell.sync('production');
 const rail=w.document.getElementById('studioContextRail');
 assert.deepEqual([...rail.querySelectorAll('h2')].map(e=>e.textContent.trim().replace(/^[^A-Za-z]+/,'')),['Overview','Actions']);
 for(const view of ['today','jobs','frontdesk'])assert.ok(w.document.querySelector('#studioNavigationTools [data-studio-side="'+view+'"]'));
 assert.equal(rail.querySelector('[data-rail-card="navigation"]'),null);
 assert.match(w.document.getElementById('studioBreadcrumb').textContent,/Today.*Production/);
});

test('navigation stays available without a reveal control or pointer interaction',t=>{
 const {w,dom}=setup();t.after(()=>dom.window.close());
 const host=w.document.getElementById('studioNavigationToolstrip');
 const tools=w.document.getElementById('studioNavigationTools');
 assert.equal(host.parentElement,w.document.querySelector('.topbar'));
 assert.equal(host.querySelector('.studio-navigation-reveal'),null);
 for(const type of ['pointerenter','pointerleave','focusout'])host.dispatchEvent(new w.Event(type));
 w.document.body.dispatchEvent(new w.Event('pointerdown',{bubbles:true}));
 assert.notEqual(tools.inert,true);
 assert.notEqual(tools.getAttribute('aria-hidden'),'true');
 assert.equal(tools.querySelectorAll('button').length,7);
});

test('toolstrip routes once and remains the single navigation after page changes',async t=>{
 const {w,dom,calls}=setup();t.after(()=>dom.window.close());
 const tools=w.document.getElementById('studioNavigationTools');
 for(const view of ['frontdesk','jobs','schedule','media']){
  tools.querySelector(`[data-studio-side="${view}"]`).click();
  w.StudioShell.sync(view);
  assert.equal(tools.querySelector('[aria-current="page"]').dataset.studioSide,view);
  assert.notEqual(tools.inert,true);
 }
 assert.deepEqual(calls,['frontdesk','jobs','schedule','media']);
 assert.equal(w.document.querySelectorAll('#studioNavigationTools').length,1);
 assert.equal(w.document.querySelectorAll('#studioContextRail [data-studio-side]').length,0);
 assert.equal(w.document.querySelector('[data-studio-side="operations-management"]'),null);
});

test('navigation buttons accept keyboard focus and direct activation',t=>{
 const {w,dom,calls}=setup();t.after(()=>dom.window.close());
 const button=w.document.querySelector('#studioNavigationTools [data-studio-side="jobs"]');
 button.focus();assert.equal(w.document.activeElement,button);
 button.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));
 assert.equal(w.document.activeElement,button);
 button.click();assert.deepEqual(calls,['jobs']);
 assert.equal(w.document.activeElement,button);
});

test('collapsed side rail persists while navigation stays accessible',async t=>{
 const {w,dom}=setup();t.after(()=>dom.window.close());
 const toggle=w.document.querySelector('[data-rail-toggle="left"]');
 toggle.click();
 assert.equal(w.document.getElementById('studioContextRail').classList.contains('studio-rail-collapsed-left'),true);
 assert.equal(JSON.parse(w.localStorage.getItem('aegis.studio.rail-collapse.v1')).left,true);
 await w.studioNavigate('frontdesk');w.StudioShell.sync('frontdesk');
 assert.equal(toggle.getAttribute('aria-expanded'),'false');
 assert.equal(toggle.getAttribute('aria-label'),'Expand left rail');
 assert.notEqual(w.document.getElementById('studioNavigationTools').inert,true);
 toggle.click();assert.equal(toggle.getAttribute('aria-expanded'),'true');
});

test('toolstrip ends with the existing bell and avatar while their menus remain usable',async t=>{
 const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'https://studio.test/',runScripts:'outside-only'});
 t.after(()=>dom.window.close());
 const w=dom.window,calls=[];
 w.fetch=async()=>({ok:true,json:async()=>[]});
 w.studioNavigate=view=>calls.push(view);
 const bell=w.document.getElementById('notificationButton');
 const avatar=w.document.querySelector('.user-menu');
 w.eval(fs.readFileSync(path.join(root,'notifications.js'),'utf8'));
 w.eval(code);w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
 const host=w.document.getElementById('studioNavigationToolstrip');
 const account=host.lastElementChild;
 assert.ok(account.classList.contains('studio-navigation-account'));
 assert.equal(account.firstElementChild,bell);
 assert.equal(account.lastElementChild,avatar);
 assert.equal(w.document.querySelectorAll('#notificationButton').length,1);
 assert.equal(w.document.querySelectorAll('.user-menu').length,1);
 await bell.onclick();
 const panel=w.document.getElementById('studioMessageBellPanel');
 assert.equal(panel.parentElement,account);
 assert.equal(panel.hidden,false);
 panel.querySelector('[data-bell-close]').click();assert.equal(panel.hidden,true);
 avatar.open=true;
 avatar.querySelector('[data-studio-avatar-notifications]').click();
 assert.deepEqual(calls,['notifications']);assert.equal(avatar.open,false);
 w.StudioShell.sync('jobs');
 assert.equal(account.firstElementChild,bell);assert.equal(account.lastElementChild,avatar);
});

test('shared footer restores count and keeps history and workspace selection across pages',async t=>{
 const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'https://studio.test/',runScripts:'outside-only'});
 t.after(()=>dom.window.close());
 const w=dom.window,selected=[];
 w.document.body.dataset.studioView='frontdesk';
 w.workspaceCache=[{id:'studio-a',name:'Studio A'},{id:'studio-b',name:'Studio B'}];
 w.selectWorkspace=(id,name)=>selected.push({id,name});
 w.fetch=async()=>({ok:true,json:async()=>[]});
 w.eval(fs.readFileSync(path.join(root,'studio-page.js'),'utf8'));
 w.eval(code);w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
 const footer=w.document.getElementById('studioShellFooter');
 assert.equal(footer.querySelector('#studioBreadcrumb').parentElement,footer);
 const count=footer.querySelector('#studioFooterRate');
 assert.ok(count);
 w.StudioPage.setCount('12 Jobs');assert.equal(count.textContent,'12 Jobs');
 const workspace=footer.querySelector('#studioFooterWorkspaces');
 assert.ok(workspace);assert.equal(count.nextElementSibling,workspace);
 workspace.value='studio-b';workspace.dispatchEvent(new w.Event('change'));
 assert.deepEqual(selected,[{id:'studio-b',name:'Studio B'}]);
 for(const view of ['jobs','frontdesk','today']){
  w.document.body.dataset.studioView=view;w.StudioShell.sync(view);
  assert.equal(w.document.getElementById('studioShellFooter'),footer);
  assert.equal(footer.querySelector('#studioFooterWorkspaces').value,'studio-b');
  assert.equal(footer.querySelectorAll('#studioFooterRate').length,1);
 }
});


test('Today through Media use restrained line icons without changing routes or labels',t=>{
 const {w,dom,calls}=setup();t.after(()=>dom.window.close());
 const expected=[['today','Today'],['frontdesk','Front Desk'],['clients','Clients'],['jobs','Jobs'],['production','Production'],['schedule','Schedule'],['media','Media']];
 const buttons=[...w.document.querySelectorAll('#studioNavigationTools [data-studio-side]')];
 assert.deepEqual(buttons.map(b=>[b.dataset.studioSide,b.textContent]),expected);
 for(const button of buttons){const icon=button.querySelector('svg.studio-navigation-icon');assert.ok(icon);assert.equal(icon.getAttribute('viewBox'),'0 0 24 24');assert.equal(icon.getAttribute('aria-hidden'),'true');assert.equal(icon.getAttribute('fill'),'none');assert.equal(icon.getAttribute('stroke'),'currentColor');assert.ok(icon.querySelector('.studio-navigation-accent'));assert.equal(icon.querySelector('[fill]'),null);button.click();}
 assert.deepEqual(calls,expected.map(([key])=>key));
});
