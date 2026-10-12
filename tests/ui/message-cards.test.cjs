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
function checkBottom(w,railSelector){
 const rail=w.document.querySelector(railSelector),card=rail.querySelector('.smc-card');
 assert.equal(rail.children.length,3,railSelector+' on '+w.document.body.dataset.studioView);assert.equal(rail.lastElementChild,card);assert.equal(card.querySelector('.smc-toggle'),null);assert.equal(card.querySelector('.smc-drawer'),null);assert.ok(card.querySelector('[data-smc-notes-toggle]'));return card;
}
test('all operational right rails keep Messages open at the bottom with a Notes toggle',async t=>{
 const w=await full(t);
 for(const [view,rail] of [['today','.mw-today-right'],['jobs','.sj-right'],['frontdesk','.fd-secondary'],['workspaces','.workspace-right-rail'],['finance','.st-right'],['messages','.st-right'],['admin','.st-right']]){
  await w.studioNavigate(view);await settle();const card=checkBottom(w,rail);
  await card.querySelector('[data-smc-notes-toggle]').onclick();assert.equal(card.dataset.smcMode,'notes',view);assert.ok(card.querySelector('[data-notes-job]'),view);
  await card.querySelector('[data-smc-notes-toggle]').onclick();assert.equal(card.querySelector('[data-smc-messages]').hidden,false,view);
 }
});
test('Today and Front Desk filtering preserve the Notes panel without duplicate binding',async t=>{
 const w=await full(t);await w.studioNavigate('today');await w.studioTodayReady;
 let card=w.document.getElementById('todayMessageCard');await card.querySelector('[data-smc-notes-toggle]').onclick();
 let job=card.querySelector('[data-notes-job]');job.value='j0';await job.onchange();card.querySelector('.smc-note-form textarea').value='Today note draft';
 const search=w.document.querySelector('[data-mw-job-search]');search.value='Portrait';search.dispatchEvent(new w.Event('input',{bubbles:true}));await settle();
 assert.equal(w.document.getElementById('todayMessageCard'),card);assert.equal(card.dataset.smcMode,'notes');assert.equal(card.querySelector('.smc-note-form textarea').value,'Today note draft');await card.querySelector('[data-smc-notes-toggle]').onclick();assert.equal(card.dataset.smcMode,'messages');
 await w.studioNavigate('frontdesk');card=w.document.getElementById('frontDeskMessageCard');await card.querySelector('[data-smc-notes-toggle]').onclick();
 job=card.querySelector('[data-notes-job]');job.value='j1';await job.onchange();card.querySelector('.smc-note-form textarea').value='Front Desk note draft';
 const filter=w.document.querySelector('[data-fd-filter]');filter.dispatchEvent(new w.Event('change',{bubbles:true}));await settle();
 assert.equal(w.document.getElementById('frontDeskMessageCard'),card);assert.equal(card.dataset.smcMode,'notes');assert.equal(card.querySelector('.smc-note-form textarea').value,'Front Desk note draft');await card.querySelector('[data-smc-notes-toggle]').onclick();assert.equal(card.dataset.smcMode,'messages');
});
test('Job Details Notes toggle retains the message draft and reply handlers',async t=>{
 const {environment}=require('./job-detail-fixture.cjs'),{w}=environment(t);await w.openWorkspaceJob('job');await settle();
 const card=checkBottom(w,'.job-detail-right'),toggle=card.querySelector('[data-smc-notes-toggle]'),composer=card.querySelector('#hierarchyCompose');
 assert.equal(typeof composer.onsubmit,'function');composer.querySelector('textarea').value='Draft kept while viewing notes';
 await toggle.onclick();assert.equal(card.querySelector('[data-smc-messages]').hidden,true);assert.equal(card.querySelector('[data-notes-job]'),null);
 await toggle.onclick();assert.equal(composer.querySelector('textarea').value,'Draft kept while viewing notes');
 card.querySelector('[data-message-more]').click();assert.equal(card.dataset.smcMode,'messages');assert.equal(w.document.activeElement.id,'hierarchyBody');
 composer.querySelector('textarea').dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));assert.equal(card.querySelector('[data-smc-messages]').hidden,false);
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

