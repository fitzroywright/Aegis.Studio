const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const code=fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/clients.js'),'utf8');
const settle=()=>new Promise(r=>setImmediate(r));
function setup(){
 const dom=new JSDOM('<body data-studio-view="clients"><section id="conversationView"></section><section id="genericView"></section></body>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;
 const fixture={calls:[],opened:[],allowed:true,failPublish:false,failFinance:false,delay:null};
 const escape=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 w.StudioPage={escape,setCount:c=>fixture.count=c};w.conversationView=w.document.querySelector('#conversationView');w.genericView=w.document.querySelector('#genericView');w.alert=message=>fixture.alert=message;
 fixture.clients=[{id:'a',displayName:'Andrea Campbell',email:'andrea@example.test',phone:'876-555-0123'},{id:'b',displayName:'Andrea Campbell',email:'other@example.test'}];
 fixture.jobs=[{id:'j1',clientId:'a',title:'Graduation',workspaceId:'w1',status:'Ready for Delivery',financialEvents:[{kind:'Charge',amount:100,createdAt:'2026-10-17'},{kind:'Refund',amount:-10,createdAt:'2026-10-17'}]},{id:'j2',clientId:'b',title:'Other client job',workspaceId:'w1',status:'Completed'},{id:'j3',clientName:'Andrea Campbell',title:'Unlinked snapshot',status:'Pending'}];
 w.studioMediaIsPreviewable=()=>true;w.renderProductionMedia=async(job,host)=>{host.textContent='Gallery for '+job.title;};w.openWorkspaceJob=async id=>fixture.opened.push(id);
 w.studioNavigate=async(view,options={})=>{fixture.navigation={view,options};if(view==='clients')await w.StudioClients.render(options);};
 w.studioDialog=(title,html)=>{const d=w.document.createElement('dialog');d.innerHTML='<div class="studio-dialog-body">'+html+'</div>';d.close=()=>d.remove();w.document.body.append(d);return d;};
 w.json=async(url,options)=>{fixture.calls.push({url,options});if(fixture.delay&&url.includes('/media?'))await fixture.delay;
 if(url==='/api/frontdesk/clients')return fixture.clients;if(url==='/api/jobs')return fixture.jobs;if(url==='/api/workspaces')return[{id:'w1',name:'Portraits'}];
 if(url.endsWith('/workflow'))return{status:'Ready for Delivery',revision:12,actions:fixture.allowed?['publish']:[]};
 if(url.endsWith('/media/publications'))return[];
 if(url.includes('/media?'))return{gallery:{name:'Graduation Selects'},assets:[],collections:['Selects'],totalAssets:42};
 if(url.endsWith('/media/publication')){if(fixture.failPublish)throw Error('Approval changed. Refresh the job.');return{};}
 if(url.endsWith('/invoices')){if(fixture.failFinance)throw Error('Not authorized');return[{id:'i1',invoiceNumber:'INV-1',currency:'USD',totalAmount:20,status:'Issued',dueAtUtc:'2026-10-30'}];}
 if(url.endsWith('/finance-release')){if(fixture.failFinance)throw Error('Not authorized');return{decision:{balance:30}};}
 if(url.endsWith('/operations/audit'))return[{action:'JobCreated',detail:'Pending job prepared',createdAtUtc:'2026-10-17',userName:'staff'}];
 throw Error('Unexpected URL '+url);};
 w.requestAnimationFrame=fn=>w.setTimeout(fn,0);
 w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/studio-message-card.js'),'utf8'));
 w.eval(code);return{dom,w,fixture};
}
function tab(w,key){w.document.querySelector('[data-c-tab="'+key+'"]').click();}
test('client jobs join by stored identity and directory search works',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());await w.StudioClients.render({clientId:'a'});assert.match(w.document.querySelector('[data-c-content]').textContent,/Graduation/);assert.doesNotMatch(w.document.querySelector('[data-c-content]').textContent,/Other client job|Unlinked snapshot/);w.document.querySelector('[data-c-job]').click();assert.deepEqual(fixture.opened,['j1']);const search=w.document.querySelector('[data-c-search]');search.value='other@example';search.dispatchEvent(new w.Event('input'));assert.equal(w.document.querySelectorAll('[data-c-id]').length,1);assert.equal(w.document.querySelector('[data-c-id]').dataset.cId,'b');});
test('gallery preview uses the real renderer and publication requires an explicit confirmation',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());await w.StudioClients.render({clientId:'a'});tab(w,'galleries');await settle();assert.match(w.genericView.textContent,/42 images/);w.document.querySelector('[data-c-media]').click();await settle();assert.match(w.document.querySelector('dialog').textContent,/Gallery for Graduation/);w.document.querySelector('dialog').close();w.document.querySelector('[data-c-publish]').click();await settle();assert.equal(fixture.calls.filter(c=>c.options?.method==='POST').length,0);w.document.querySelector('[data-c-confirm-publish]').click();await settle();const writes=fixture.calls.filter(c=>c.url.endsWith('/media/publication'));assert.equal(writes.length,1);const body=JSON.parse(writes[0].options.body);assert.equal(body.expectedRevision,12);assert.equal(body.action,'publish');assert.ok(body.operationId);});
test('publication permissions and conflict failures remain visible without fabricating success',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());fixture.allowed=false;await w.StudioClients.render({clientId:'a'});tab(w,'galleries');await settle();w.document.querySelector('[data-c-publish]').click();await settle();assert.equal(w.document.querySelector('[data-c-confirm-publish]').disabled,true);w.document.querySelector('dialog').close();fixture.allowed=true;fixture.failPublish=true;w.document.querySelector('[data-c-publish]').click();await settle();w.document.querySelector('[data-c-confirm-publish]').click();await settle();assert.match(w.document.querySelector('dialog [role=alert]').textContent,/Approval changed/);});
test('financial statement preserves signed ledger values and invoice currency',async t=>{const {dom,w}=setup();t.after(()=>dom.window.close());await w.StudioClients.render({clientId:'a'});tab(w,'finance');await settle();const text=w.document.querySelector('[data-c-statement]').textContent;assert.match(text,/INV-1/);assert.match(text,/\$20/);assert.match(text,/-.*10/);assert.match(text,/30/);assert.doesNotMatch(text,/Other client job/);});
test('restricted financial data is shown as unavailable rather than zero',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());fixture.failFinance=true;await w.StudioClients.render({clientId:'a'});tab(w,'finance');await settle();assert.match(w.document.querySelector('[data-c-statement]').textContent,/Unavailable/);assert.match(w.document.querySelector('[data-c-statement] [role=alert]').textContent,/restricted/);});
test('late gallery responses do not replace a newly opened client or route',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());let release;fixture.delay=new Promise(r=>release=r);await w.StudioClients.render({clientId:'a'});tab(w,'galleries');await w.StudioClients.render({clientId:'b'});release();await settle();assert.match(w.document.querySelector('[data-c-content]').textContent,/Other client job/);assert.doesNotMatch(w.document.querySelector('[data-c-content]').textContent,/42 images/);});
test('client and job text is escaped',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());fixture.clients[0].displayName='<img src=x onerror=alert(1)>';fixture.jobs[0].title='<script>bad()</script>';await w.StudioClients.render({clientId:'a'});assert.equal(w.genericView.querySelector('script'),null);assert.equal(w.genericView.querySelector('img'),null);assert.match(w.genericView.textContent,/<script>bad/);});
test('client message alerts stay scoped to linked jobs',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());const base=w.json;w.json=async(url,options)=>url==='/api/notifications?count=200'?[{channel:'InApp',referenceType:'Job',referenceId:'j1',eventType:'WorkspaceMessage',subject:'Client reply',body:'Thank you'},{channel:'InApp',referenceType:'Job',referenceId:'j2',eventType:'WorkspaceMessage',subject:'Other client private reply'}]:base(url,options);await w.StudioClients.render({clientId:'a'});await settle();assert.match(w.document.querySelector('#clientMessageCard').textContent,/Client reply/);assert.doesNotMatch(w.document.querySelector('#clientMessageCard').textContent,/Other client private/);});
test('client navigation records selection and restores it on browser back',async t=>{const d=new JSDOM('<body data-studio-view="clients"><div><nav id="studioBreadcrumb"></nav><div id="studioPageTools"></div></div><div id="conversationView"></div><div id="genericView"></div></body>',{url:'https://studio.test/#studio=clients&client=a',runScripts:'outside-only'}),w=d.window;t.after(()=>w.close());const seen=[];w.StudioClients={render:async o=>seen.push(o.clientId),invalidate(){}};w.StudioPage={invalidate(){}};w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/studio-navigation.js'),'utf8'));w.document.dispatchEvent(new w.Event('DOMContentLoaded'));await settle();assert.equal(w.history.state.studioClientId,'a');await w.studioNavigate('clients',{clientId:'b'});assert.equal(w.history.state.studioClientId,'b');assert.match(w.location.hash,/client=b/);w.history.back();await new Promise(r=>setTimeout(r,25));assert.equal(w.history.state.studioClientId,'a');assert.equal(seen.at(-1),'a');});


test('client directory uses the canonical route and stored client identity',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());await w.StudioClients.render({clientId:'a'});await settle();
 assert.equal(w.document.querySelector('.clients-page').getAttribute('aria-label'),'Clients');
 assert.equal(w.document.querySelector('.client-directory-table th').textContent.trim(),'Client');
 assert.match(w.document.querySelector('[data-c-search]').getAttribute('aria-label'),/clients/);
 w.document.querySelector('[data-c-id="b"]').click();await settle();
 assert.equal(fixture.navigation.view,'clients');
 assert.equal(fixture.navigation.options.clientId,'b');
 assert.ok(fixture.calls.some(c=>c.url==='/api/frontdesk/clients'));
});


test('client and linked-job searches preserve caret and selection while typing',async t=>{
 const {dom,w}=setup();t.after(()=>dom.window.close());await w.StudioClients.render({clientId:'a'});await settle();
 for(const selector of ['[data-c-search]','[data-c-job-search]']){
  let input=w.document.querySelector(selector);input.focus();input.value='andrea';input.setSelectionRange(3,3);input.dispatchEvent(new w.Event('input',{bubbles:true}));
  input=w.document.querySelector(selector);assert.equal(w.document.activeElement,input);assert.equal(input.selectionStart,3);assert.equal(input.selectionEnd,3);
  input.value='andXrea';input.setSelectionRange(4,4);input.dispatchEvent(new w.Event('input',{bubbles:true}));input=w.document.querySelector(selector);assert.equal(input.value,'andXrea');assert.equal(input.selectionStart,4);assert.equal(input.selectionEnd,4);
  input.value='';input.setSelectionRange(0,0);input.dispatchEvent(new w.Event('input',{bubbles:true}));
 }
});
