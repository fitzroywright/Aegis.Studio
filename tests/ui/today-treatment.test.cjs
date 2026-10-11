const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),{JSDOM,VirtualConsole}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');const {api,jobs,permissions}=require('./shared-controller-fixture.cjs');
async function setup(t){
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'https://studio.test/',runScripts:'outside-only',virtualConsole:new VirtualConsole()}),w=dom.window;t.after(()=>w.close());const errors=[];
 w.alert=e=>errors.push(String(e));w.confirm=()=>false;w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;};w.HTMLElement.prototype.scrollIntoView=function(){};w.CSS={escape:v=>String(v)};
 let read=u=>api(u);
 w.fetch=async(url,options)=>{const u=new URL(url,'https://studio.test/');let body=await read(u,options);
 if(u.pathname==='/api/jobs/access')body=Object.fromEntries(jobs.map(j=>[j.id,permissions]));
 if(u.pathname==='/api/admin/studio-status')body={database:'Healthy',storage:{provider:'Local',available:true,writable:true},previews:{},messaging:{}};
 return {ok:true,status:200,json:async()=>body,text:async()=>JSON.stringify(body),headers:{get:()=>null}};};
 for(const script of w.document.querySelectorAll('script[src]'))require('vm').runInContext(fs.readFileSync(path.join(root,new URL(script.src).pathname),'utf8'),dom.getInternalVMContext());
 await new Promise(r=>setTimeout(r,0));await w.studioTodayReady;return {w,errors,read(fn){read=fn;}};
}
test('converted routes keep a single Today board, three information cards and real controls',async t=>{
 const {w,errors}=await setup(t);
 for(const view of ['messages','catalog','job-templates','work-order-templates','production','pipeline','schedule','finance','management','reports','studio-management','admin']){
  await w.studioNavigate(view);assert.equal(w.document.body.dataset.studioView,view);assert.equal(w.document.querySelectorAll('#genericView .st-page').length,1,view);assert.equal(w.document.querySelectorAll('.st-right>.st-card').length,3,view);assert.equal(w.document.querySelector('.production-side'),null);
 }
 assert.ok(w.document.querySelector('#workspaceName'));assert.ok(w.document.querySelector('[data-load-demo]'));
 await w.studioNavigate('schedule');assert.ok(w.document.querySelector('#scheduleEntryForm'));assert.ok(w.document.querySelector('#pipelineJobFilter'));
 await w.studioNavigate('finance');assert.ok(w.document.querySelector('[data-finance-method]'));assert.ok(w.document.querySelector('[data-export]'));assert.deepEqual(errors,[]);
});
test('Messages opens the inbox, filters real messages, and reads the selected conversation',async t=>{
 const e=await setup(t),{w}=e;e.read(u=>u.pathname==='/api/messages/threads'?[{id:'thread',scopeType:'Job',scopeId:'j0',title:'Portrait session',unreadCount:0}]:u.pathname==='/api/messages/threads/thread'?[{id:'m',body:'Session confirmed.\nThank you.',senderUserName:'sam',createdAtUtc:new Date().toISOString()}]:api(u));
 await w.studioNavigate('messages');assert.equal(w.document.querySelector('.sj-page'),null);assert.equal(w.document.querySelectorAll('[data-inbox-message]').length,1);assert.equal(w.document.getElementById('studioFooterRate').textContent,'1 messages');assert.equal(w.document.querySelector('.studio-inbox-message-subject').textContent,'Session confirmed.');
 const input=w.document.querySelector('#studioInboxSearch');input.value='does not exist';input.oninput({target:input});assert.equal(w.document.querySelectorAll('[data-inbox-message]').length,0);input.value='confirmed';input.oninput({target:input});await w.document.querySelector('[data-inbox-message]').onclick();assert.match(w.document.querySelector('#studioMessageHistory').textContent,/Thank you/);
});
test('a slow Finance response cannot replace a newer Jobs navigation',async t=>{
 const e=await setup(t),{w}=e;let release;const held=new Promise(r=>release=r);e.read(async u=>{if(u.pathname==='/api/accounts/invoices')await held;return api(u);});const finance=w.studioNavigate('finance');await w.studioNavigate('jobs');release();await finance;assert.equal(w.document.body.dataset.studioView,'jobs');assert.ok(w.document.querySelector('tr[data-sj-job]'));assert.equal(w.document.querySelector('.st-page'),null);
});
test('a slow Messages response cannot replace a newer Jobs navigation',async t=>{
 const e=await setup(t),{w}=e;let release;const held=new Promise(r=>release=r);e.read(async u=>{if(u.pathname==='/api/messages/threads')await held;return api(u);});const inbox=w.studioNavigate('messages');await w.studioNavigate('jobs');release();await inbox;assert.equal(w.document.body.dataset.studioView,'jobs');assert.equal(w.document.querySelector('#studioInboxRows'),null);
});
test('standalone staff pages retain their API content regions and replace inline window styling',()=>{
 for(const [file,ids] of [['workday-management.html',['fleet','sessions','timeline','map']],['workday-workspace-management.html',['summary','people','performance']]]){const d=new JSDOM(fs.readFileSync(path.join(root,file),'utf8'));for(const id of ids)assert.ok(d.window.document.getElementById(id));assert.ok(d.window.document.querySelector('.wd-masthead'));assert.ok(d.window.document.querySelector('.wd-footer'));d.window.close();}
 const html=fs.readFileSync(path.join(root,'pos-window.html'),'utf8');assert.doesNotMatch(html,/<style>/);assert.match(html,/pos-window\.css/);assert.match(html,/studio-rows\.css/);
});
test('Workspace WorkDay script renders the performance sections without a syntax failure',async t=>{
 const dom=new JSDOM(fs.readFileSync(path.join(root,'workday-workspace-management.html'),'utf8'),{url:'https://studio.test/workspaces/11111111-1111-1111-1111-111111111111/workday-management',runScripts:'outside-only'});t.after(()=>dom.window.close());const w=dom.window;
 w.fetch=async url=>({ok:true,json:async()=>url.includes('/activity')?{photographers:[],generatedAtUtc:new Date().toISOString()}:{photographers:[],editors:[],production:[]}});
 w.eval(fs.readFileSync(path.join(root,'js/workday-workspace-management.js'),'utf8'));await new Promise(r=>setTimeout(r,0));assert.match(w.document.getElementById('performance').textContent,/Photographers.*Editors.*Other Production/);assert.doesNotMatch(w.document.getElementById('summary').textContent,/unavailable/);
});
