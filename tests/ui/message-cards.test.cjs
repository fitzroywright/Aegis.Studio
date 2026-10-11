const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM,VirtualConsole}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const {api}=require('./shared-controller-fixture.cjs');
const settle=()=>new Promise(resolve=>setTimeout(resolve,25));
async function full(t){
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'https://studio.test/',runScripts:'outside-only',virtualConsole:new VirtualConsole()}),w=dom.window;t.after(()=>w.close());
 w.alert=message=>{throw Error(message);};w.confirm=()=>false;w.CSS={escape:v=>String(v)};
 w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;this.dispatchEvent(new w.Event('close'));};w.HTMLElement.prototype.scrollIntoView=function(){};
 w.fetch=async url=>{const u=new URL(url,'https://studio.test'),body=api(u);return {ok:true,status:200,json:async()=>body,text:async()=>JSON.stringify(body),headers:{get:()=>null}};};
 for(const script of w.document.querySelectorAll('script[src]'))require('node:vm').runInContext(fs.readFileSync(path.join(root,new URL(script.src).pathname),'utf8'),dom.getInternalVMContext());
 await settle();await w.studioTodayReady;return w;
}
function checkMiddle(w,railSelector){
 const rail=w.document.querySelector(railSelector),card=rail.querySelector('.smc-card');
 assert.equal(rail.children.length,3);assert.equal(rail.children[1],card);assert.equal(card.querySelector('.smc-toggle').getAttribute('aria-expanded'),'false');assert.equal(card.querySelector('.smc-drawer').hasAttribute('inert'),true);return card;
}
test('Today, Jobs, Front Desk, Workspaces and the shared shell render the same middle card',async t=>{
 const w=await full(t);
 for(const [view,rail] of [['today','.mw-today-right'],['jobs','.sj-right'],['frontdesk','.fd-secondary'],['workspaces','.workspace-right-rail'],['finance','.st-right'],['messages','.st-right'],['admin','.st-right']]){
  await w.studioNavigate(view);await settle();const card=checkMiddle(w,rail),toggle=card.querySelector('.smc-toggle');
  toggle.click();assert.equal(toggle.getAttribute('aria-expanded'),'true',view);assert.equal(card.querySelector('.smc-drawer').hasAttribute('inert'),false,view);
  toggle.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));assert.equal(toggle.getAttribute('aria-expanded'),'false',view);assert.equal(w.document.activeElement,toggle,view);
 }
});
test('Today partial renders and Front Desk filters preserve the expanded card and rebind once',async t=>{
 const w=await full(t);await w.studioNavigate('today');await w.studioTodayReady;
 w.document.querySelector('#todayMessageCard .smc-toggle').click();
 const search=w.document.querySelector('[data-mw-job-search]');search.value='Portrait';search.dispatchEvent(new w.Event('input',{bubbles:true}));await settle();
 let card=w.document.getElementById('todayMessageCard');assert.equal(w.StudioMessageCard.isOpen(card),true);card.querySelector('.smc-toggle').click();assert.equal(w.StudioMessageCard.isOpen(card),false);
 await w.studioNavigate('frontdesk');w.document.querySelector('#frontDeskMessageCard .smc-toggle').click();
 const filter=w.document.querySelector('[data-fd-filter]');filter.dispatchEvent(new w.Event('change',{bubbles:true}));await settle();
 card=w.document.getElementById('frontDeskMessageCard');assert.equal(w.StudioMessageCard.isOpen(card),true);card.querySelector('.smc-toggle').click();assert.equal(w.StudioMessageCard.isOpen(card),false);
});
test('Job Details uses the middle card and retains drafts, message actions and reply handlers',async t=>{
 const {environment}=require('./job-detail-fixture.cjs'),{w}=environment(t);await w.openWorkspaceJob('job');await settle();
 const card=checkMiddle(w,'.job-detail-right'),toggle=card.querySelector('.smc-toggle'),composer=card.querySelector('#hierarchyCompose');
 assert.equal(typeof composer.onsubmit,'function');toggle.click();composer.querySelector('textarea').value='Draft kept during collapse';
 toggle.click();toggle.click();assert.equal(composer.querySelector('textarea').value,'Draft kept during collapse');
 toggle.click();card.querySelector('[data-message-more]').click();assert.equal(w.StudioMessageCard.isOpen(card),true);assert.equal(w.document.activeElement.id,'hierarchyBody');
 composer.querySelector('textarea').dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));assert.equal(w.StudioMessageCard.isOpen(card),false);assert.equal(w.document.activeElement,toggle);
});
test('one controller and one stylesheet own message cards without page-specific skins',()=>{
 const html=fs.readFileSync(path.join(root,'index.html'),'utf8');assert.equal((html.match(/src="\/studio-message-card\.js/g)||[]).length,1);assert.equal((html.match(/href="\/studio-message-card\.css/g)||[]).length,1);assert.ok(html.indexOf('/studio-message-card.js')<html.indexOf('/studio-page.js'));
 assert.doesNotMatch(fs.readFileSync(path.join(root,'studio-message-card.css'),'utf8'),/!important/);
 for(const file of ['studio.css','studio-page.css','jobs-page.css','job-details.css'])assert.doesNotMatch(fs.readFileSync(path.join(root,file),'utf8'),/\.job-detail-message-card|\.st-messages|\.sj-messages|\.fd-communications|\.fd-messages/);
});
test('message card and rail styles contain valid declarations and no duplicate property owners',()=>{
 const css=require('css-tree');
 for(const file of ['studio.css','studio-page.css','jobs-page.css','job-details.css','studio-message-card.css']){
  const ast=css.parse(fs.readFileSync(path.join(root,file),'utf8')),seen=new Set();
  function visit(list,scope=[]){list.forEach(node=>{
   if(node.type==='Atrule'&&node.block&&!/keyframes|font-face|property/.test(node.name))visit(node.block.children,[...scope,node.name+':'+(node.prelude?css.generate(node.prelude):'')]);
   if(node.type!=='Rule'||node.prelude.type!=='SelectorList')return;
   node.block.children.forEach(d=>{if(d.type!=='Declaration')return;const text=css.generate(d);if(!d.property.startsWith('--')&&!text.includes('var('))assert.equal(css.lexer.matchDeclaration(d).error,null,file+': '+text);});
   node.prelude.children.forEach(selector=>{if(!/smc-|st-right|sj-right|job-detail-right|mw-today-right|workspace-right-rail|fd-secondary/.test(css.generate(selector)))return;node.block.children.forEach(d=>{if(d.type!=='Declaration')return;const key=scope.join('|')+'\0'+css.generate(selector)+'\0'+d.property;assert.equal(seen.has(key),false,file+': duplicate '+css.generate(selector)+' / '+d.property);seen.add(key);});});
  });}
  visit(ast.children);
 }
});
