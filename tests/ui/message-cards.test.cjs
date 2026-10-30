const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM,VirtualConsole}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const {api}=require('./shared-controller-fixture.cjs');
const settle=()=>new Promise(resolve=>setTimeout(resolve,25));
async function full(t){
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'https://studio.test/',runScripts:'outside-only',virtualConsole:new VirtualConsole()}),w=dom.window;t.after(()=>w.close());
 w.ResizeObserver=class {observe(){} disconnect(){}};w.requestAnimationFrame=fn=>w.setTimeout(fn,0);
 w.alert=message=>{throw Error(message);};w.confirm=()=>false;w.CSS={escape:v=>String(v)};
 w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;this.dispatchEvent(new w.Event('close'));};w.HTMLElement.prototype.scrollIntoView=function(){};
 w.fetch=async url=>{const u=new URL(url,'https://studio.test'),body=api(u);return {ok:true,status:200,json:async()=>body,text:async()=>JSON.stringify(body),headers:{get:()=>null}};};
 for(const script of w.document.querySelectorAll('script[src]'))require('node:vm').runInContext(require('./studio-script-fixture.cjs')(new URL(script.src).pathname),dom.getInternalVMContext());
 await settle();await w.studioTodayReady;return w;
}
function mountController(w){
 const card=w.StudioMessageCard.create({id:'messageControllerTest',attributes:'data-message-job-id="j0"',footer:'<form class="composer"><textarea id="componentMessageDraft"></textarea></form>'});
 w.document.body.append(card);w.StudioMessageCard.bind(card);return card;
}
test('operational pages omit retired right rails and duplicate message cards',async t=>{
 const w=await full(t);
 for(const view of ['today','jobs','frontdesk','workspaces','finance','messages','about']){
  await w.studioNavigate(view);await settle();assert.equal(w.document.querySelector('.mw-today-right,.sj-right,.fd-secondary,.workspace-right-rail,.st-right,#studioContextRail'),null,view);
  assert.equal(w.document.querySelector('.smc-card'),null,view);
 }
});
test('Notes controller preserves an editor draft through rebinding and mode changes',async t=>{
 const w=await full(t),card=mountController(w);await card.querySelector('[data-smc-notes-toggle]').onclick();
 const form=card.querySelector('.smc-note-form');form.elements.body.value='Keep this draft';
 w.StudioMessageCard.bind(card);await card.querySelector('[data-smc-notes-toggle]').onclick();await card.querySelector('[data-smc-notes-toggle]').onclick();
 assert.equal(card.querySelector('.smc-note-form'),form);assert.equal(form.elements.body.value,'Keep this draft');assert.equal(card.querySelectorAll('.smc-note-form').length,1);
});
test('message controller retains its composer and handlers while Notes is open',async t=>{
 const w=await full(t),card=mountController(w),composer=card.querySelector('form'),draft=card.querySelector('textarea');let submissions=0;
 composer.onsubmit=e=>{e.preventDefault();submissions++;};draft.value='Reply draft';
 await card.querySelector('[data-smc-notes-toggle]').onclick();assert.equal(card.querySelector('[data-smc-messages]').hidden,true);
 await card.querySelector('[data-smc-notes-toggle]').onclick();assert.equal(draft.value,'Reply draft');assert.equal(card.querySelector('form'),composer);
 composer.dispatchEvent(new w.Event('submit',{bubbles:true,cancelable:true}));assert.equal(submissions,1);
});
test('one controller and one stylesheet own message cards without page-specific skins',()=>{
 const html=fs.readFileSync(path.join(root,'index.html'),'utf8');assert.equal((html.match(/src="\/studio-message-card\.js/g)||[]).length,1);assert.equal((html.match(/href="\/css\/components\/studio-message-card\.css/g)||[]).length,1);assert.ok(html.indexOf('/studio-message-card.js')<html.indexOf('/studio-page.js'));
 assert.doesNotMatch(fs.readFileSync(path.join(root,'css/components/studio-message-card.css'),'utf8'),/!important/);
 for(const file of ['studio.css','css/components/studio-page.css','css/pages/jobs-page.css','css/pages/job-details.css'])assert.doesNotMatch(fs.readFileSync(path.join(root,file),'utf8'),/\.(?:job-detail-message-card|st-messages|sj-messages|fd-communications|fd-messages)(?![\w-])/);
});
test('message card and rail styles contain valid declarations and no duplicate property owners',()=>{
 const css=require('css-tree');
 for(const file of ['studio.css','css/components/studio-page.css','css/pages/jobs-page.css','css/pages/job-details.css','css/components/studio-message-card.css']){
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

