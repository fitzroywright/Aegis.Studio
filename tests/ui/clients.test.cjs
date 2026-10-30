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
 w.studioMediaIsPreviewable=()=>true;w.StudioJobDetails={openGallery:async(job,options)=>{fixture.galleryJob=job;fixture.galleryOptions=options;return w.studioDialog(job.title,'Gallery for '+job.title);}};w.openWorkspaceJob=async id=>fixture.opened.push(id);
 w.studioNavigate=async(view,options={})=>{fixture.navigation={view,options};if(view==='clients')await w.StudioClients.render(options);};
 w.studioDialog=(title,html)=>{const d=w.document.createElement('dialog');d.innerHTML='<div class="studio-dialog-body">'+html+'</div>';d.close=()=>d.remove();w.document.body.append(d);return d;};
 w.json=async(url,options)=>{fixture.calls.push({url,options});if(fixture.delay&&url.includes('/media?'))await fixture.delay;
 if(url==='/api/production/media-summary')return fixture.mediaSummary||[];if(url==='/api/frontdesk/clients')return fixture.clients;if(url==='/api/jobs')return fixture.jobs;if(url==='/api/workspaces')return[{id:'w1',name:'Portraits'}];
 if(url.endsWith('/workflow'))return{status:'Ready for Delivery',revision:12,actions:fixture.allowed?['publish']:[]};
 if(url.endsWith('/media/publications'))return[];
 if(url.includes('/media?'))return{gallery:{name:'Graduation Selects'},assets:[],collections:['Selects'],totalAssets:42};
 if(url.endsWith('/media/publication')){if(fixture.failPublish)throw Error('Approval changed. Refresh the job.');return{};}
 if(url.endsWith('/invoices')){if(fixture.failFinance)throw Error('Not authorized');return[{id:'i1',invoiceNumber:'INV-1',currency:'USD',totalAmount:20,status:'Issued',dueAtUtc:'2026-10-30'}];}
 if(url.endsWith('/finance-release')){if(fixture.failFinance)throw Error('Not authorized');return{decision:{balance:30}};}
 if(url.endsWith('/operations/audit'))return[{action:'JobCreated',detail:'Pending job prepared',createdAtUtc:'2026-10-17',userName:'staff'}];
 throw Error('Unexpected URL '+url);};
 w.requestAnimationFrame=fn=>w.setTimeout(fn,0);
require('./studio-page-fixture.cjs')(w); w.eval(code);return{dom,w,fixture};
}
function tab(w,key){w.document.querySelector('[data-c-tab="'+key+'"]').click();}
test('client jobs join by stored identity and directory search works',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());await w.StudioClients.render({clientId:'a'});assert.match(w.document.querySelector('[data-c-content]').textContent,/Graduation/);assert.doesNotMatch(w.document.querySelector('[data-c-content]').textContent,/Other client job|Unlinked snapshot/);w.document.querySelector('[data-c-job]').click();assert.deepEqual(fixture.opened,['j1']);const search=w.document.querySelector('[data-c-search]');search.value='other@example';search.dispatchEvent(new w.Event('input'));assert.equal(w.document.querySelectorAll('[data-c-id]').length,1);assert.equal(w.document.querySelector('[data-c-id]').dataset.cId,'b');});
test('gallery preview uses the real renderer and publication requires an explicit confirmation',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());await w.StudioClients.render({clientId:'a'});tab(w,'galleries');await settle();assert.match(w.genericView.textContent,/42 images/);w.document.querySelector('[data-c-media]').click();await settle();assert.match(w.document.querySelector('dialog').textContent,/Gallery for Graduation/);w.document.querySelector('dialog').close();w.document.querySelector('[data-c-publish]').click();await settle();assert.equal(fixture.calls.filter(c=>c.options?.method==='POST').length,0);w.document.querySelector('[data-c-confirm-publish]').click();await settle();const writes=fixture.calls.filter(c=>c.url.endsWith('/media/publication'));assert.equal(writes.length,1);const body=JSON.parse(writes[0].options.body);assert.equal(body.expectedRevision,12);assert.equal(body.action,'publish');assert.ok(body.operationId);});
test('publication permissions and conflict failures remain visible without fabricating success',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());fixture.allowed=false;await w.StudioClients.render({clientId:'a'});tab(w,'galleries');await settle();w.document.querySelector('[data-c-publish]').click();await settle();assert.equal(w.document.querySelector('[data-c-confirm-publish]').disabled,true);w.document.querySelector('dialog').close();fixture.allowed=true;fixture.failPublish=true;w.document.querySelector('[data-c-publish]').click();await settle();w.document.querySelector('[data-c-confirm-publish]').click();await settle();assert.match(w.document.querySelector('dialog [role=alert]').textContent,/Approval changed/);});
test('financial statement preserves signed ledger values and invoice currency',async t=>{const {dom,w}=setup();t.after(()=>dom.window.close());await w.StudioClients.render({clientId:'a'});tab(w,'finance');await settle();const text=w.document.querySelector('[data-c-statement]').textContent;assert.match(text,/INV-1/);assert.match(text,/\$20/);assert.match(text,/-.*10/);assert.match(text,/30/);assert.doesNotMatch(text,/Other client job/);});
test('restricted financial data is shown as unavailable rather than zero',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());fixture.failFinance=true;await w.StudioClients.render({clientId:'a'});tab(w,'finance');await settle();assert.match(w.document.querySelector('[data-c-statement]').textContent,/Unavailable/);assert.match(w.document.querySelector('[data-c-statement] [role=alert]').textContent,/restricted/);});
test('late gallery responses do not replace a newly opened client or route',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());let release;fixture.delay=new Promise(r=>release=r);await w.StudioClients.render({clientId:'a'});tab(w,'galleries');await w.StudioClients.render({clientId:'b'});release();await settle();assert.match(w.document.querySelector('[data-c-content]').textContent,/Other client job/);assert.doesNotMatch(w.document.querySelector('[data-c-content]').textContent,/42 images/);});
test('client and job text is escaped',async t=>{const {dom,w,fixture}=setup();t.after(()=>dom.window.close());fixture.clients[0].displayName='<img src=x onerror=alert(1)>';fixture.jobs[0].title='<script>bad()</script>';await w.StudioClients.render({clientId:'a'});assert.equal(w.genericView.querySelector('script'),null);assert.equal(w.genericView.querySelector('img'),null);assert.match(w.genericView.textContent,/<script>bad/);});
test('Clients has no Messages or Notes card in the directory or selected-client view',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());
 assert.equal(w.StudioMessageCard,undefined);
 for(const options of [{},{clientId:'a'}]){
  await w.StudioClients.render(options);await settle();
  assert.equal(w.document.querySelector('#clientMessageCard,.smc-card,[data-c-send-message],[data-c-messages]'),null);
  assert.equal(fixture.calls.some(call=>call.url.startsWith('/api/notifications')),false);
  assert.ok(w.document.querySelector('.client-directory'));
 }
});
test('client navigation records selection and restores it on browser back',async t=>{const d=new JSDOM('<body data-studio-view="clients"><div><nav id="studioBreadcrumb"></nav><div id="studioPageTools"></div></div><div id="conversationView"></div><div id="genericView"></div></body>',{url:'https://studio.test/#studio=clients&client=a',runScripts:'outside-only'}),w=d.window;t.after(()=>w.close());const seen=[];w.StudioClients={render:async o=>seen.push(o.clientId),invalidate(){}};w.StudioPage={invalidate(){}};w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/studio-navigation.js'),'utf8'));w.document.dispatchEvent(new w.Event('DOMContentLoaded'));await settle();assert.equal(w.history.state.studioClientId,'a');await w.studioNavigate('clients',{clientId:'b'});assert.equal(w.history.state.studioClientId,'b');assert.match(w.location.hash,/client=b/);w.history.back();await new Promise(r=>setTimeout(r,25));assert.equal(w.history.state.studioClientId,'a');assert.equal(seen.at(-1),'a');});


test('client directory uses the canonical route and stored client identity',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());await w.StudioClients.render({clientId:'a'});await settle();
 assert.equal(w.document.querySelector('.clients-page').getAttribute('aria-label'),'Client Management');
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


test('Client Reminders tab passes only jobs associated with the selected client',async()=>{
 const {w,fixture}=setup();try{
 let loaded;w.StudioClientReminders={mountClient:async(host,jobs,current)=>{loaded=jobs;assert.equal(current(),true);host.textContent='Client reminders';}};
 await w.StudioClients.render({clientId:'a'});
 w.document.querySelector('[data-c-tab="reminders"]').click();await settle();
 assert.deepEqual(Array.from(loaded,j=>j.id),['j1']);assert.match(w.genericView.textContent,/Client reminders/);
 }finally{w.close();}
});


test('client directory keeps scroll and focus on selection without replacing either grid',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());
 fixture.clients.push(...Array.from({length:8},(_,i)=>({id:'c'+i,displayName:'Client '+i})));
 await w.StudioClients.render();
 let directory=w.genericView.querySelector('.client-directory-rows');directory.scrollTop=300;
 directory.querySelector('[data-c-id="c4"]').click();await settle();
 directory=w.genericView.querySelector('.client-directory-rows');
 assert.equal(directory.scrollTop,300);assert.equal(directory.querySelector('[aria-selected="true"]').dataset.cId,'c4');
 const row=directory.querySelector('[data-c-id="c4"]'),jobs=w.genericView.querySelector('.client-jobs-scroll');jobs.scrollTop=200;row.focus();
 await settle();await settle();
 assert.equal(w.genericView.querySelector('.client-directory-rows'),directory);assert.equal(directory.scrollTop,300);
 assert.equal(directory.querySelector('[data-c-id="c4"]'),row);assert.equal(w.document.activeElement,row);
 assert.equal(w.genericView.querySelector('.client-jobs-scroll'),jobs);assert.equal(jobs.scrollTop,200);
});

test('financial cards retain exact job balances, invoice currencies and job links',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());
 await w.StudioClients.render({clientId:'a'});tab(w,'finance');await settle();
 const statement=w.document.querySelector('[data-c-statement]'),balances=statement.querySelector('[aria-label="Job balances"]');
 assert.equal(balances.querySelectorAll('.client-finance-card').length,1);
 assert.match(balances.querySelector('[data-client-balance]').textContent,/30/);
 const invoice=statement.querySelector('.client-invoice-card');assert.match(invoice.textContent,/USD/);assert.match(invoice.textContent,/INV-1/);
 assert.equal(statement.querySelectorAll('.client-statement-ledger tbody tr').length,2);
 balances.querySelector('[data-c-job]').click();assert.deepEqual(fixture.opened,['j1']);
});

test('financial cards distinguish zero, credit and unavailable balances without combining currencies',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());
 fixture.jobs.push({id:'zero',clientId:'a',title:'Settled job',status:'Completed'},{id:'credit',clientId:'a',title:'Credit job',status:'Completed'});
 const original=w.json;
 w.json=async(url,options)=>url.endsWith('/finance-release')?{decision:{balance:url.includes('/zero/')?0:url.includes('/credit/')?-25:null}}:original(url,options);
 await w.StudioClients.render({clientId:'a'});tab(w,'finance');await settle();
 const amounts=[...w.document.querySelectorAll('[data-client-balance]')].map(el=>el.textContent);
 assert.equal(amounts[0],'Unavailable');assert.match(amounts[1],/0\.00/);assert.match(amounts[2],/-.*25\.00/);
 assert.match(w.document.querySelector('.client-finance-warning').textContent,/not zero/);
});

test('printing clones the card statement and preserves signed ledger values',async t=>{
 const {dom,w}=setup();t.after(()=>dom.window.close());
 const paper=new JSDOM('<html><head></head><body></body></html>');t.after(()=>paper.window.close());let printed=false;
 w.open=()=>({document:paper.window.document,focus(){},print(){printed=true;},opener:w});
 await w.StudioClients.render({clientId:'a'});tab(w,'finance');await settle();
 w.document.querySelector('[data-c-print]').click();
 assert.equal(printed,true);assert.equal(paper.window.document.querySelectorAll('.client-finance-card').length,2);
 assert.match(paper.window.document.querySelector('.client-statement-ledger').textContent,/-.*10/);
 assert.ok(w.document.querySelector('[data-c-statement]'));
});

test('Client Management omits retired rails and message cards while finance remains usable',async t=>{
 const {dom,w}=setup();t.after(()=>dom.window.close());
 const shell=w.document.createElement('div');shell.className='shell';w.document.body.append(shell);
 w.fetch=async()=>({ok:true,json:async()=>[]});
 w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/studio-shell-nav.js'),'utf8'));
 w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
 await w.StudioClients.render({clientId:'a'});await settle();
 assert.equal(w.document.getElementById('studioContextRail'),null);
 assert.equal(w.document.querySelector('#clientMessageCard,[data-page-messages]'),null);
 tab(w,'finance');await settle();
 assert.equal(w.document.querySelector('.client-statement-heading h3').textContent,'Financial Statement');
 assert.equal(w.document.querySelector('.client-statement-eyebrow'),null);
});

test('Client Jobs has four columns and keeps gallery access in its own tab',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());
 await w.StudioClients.render({clientId:'a'});await settle();
 const grid=w.document.querySelector('.client-jobs-grid');
 assert.equal(grid.querySelectorAll('th').length,4);
 assert.equal(grid.querySelector('[data-c-gallery-job]'),null);
 const row=grid.querySelector('[data-c-job]');assert.equal(row.children.length,4);row.click();assert.deepEqual(fixture.opened,['j1']);
 w.document.querySelector('[data-c-job-search]').value='No match';w.document.querySelector('[data-c-job-search]').dispatchEvent(new w.Event('input'));
 assert.equal(w.document.querySelector('.client-jobs-scroll td').colSpan,4);
 tab(w,'galleries');await settle();assert.ok(w.document.querySelector('[data-c-media]'));
});
test('client job listing chooses a real job photo once and preserves it while filtering',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());w.Math.random=()=>0.99;
 fixture.mediaSummary=[{jobId:'j1',thumbnailVersionIds:['v1','v2']},{jobId:'j1',thumbnailVersionIds:['v2']},{jobId:'j2',thumbnailVersionIds:['other-client']}];
 await w.StudioClients.render({clientId:'a'});await settle();
 const photo=w.document.querySelector('[data-c-photo="j1"] img');assert.ok(photo);assert.match(photo.src,/jobs\/j1\/versions\/by-id\/v2\/preview\?size=thumbnail/);
 assert.equal(w.document.querySelector('[data-c-photo="j2"]'),null);assert.equal(w.document.querySelectorAll('.client-jobs-table th').length,4);
 const search=w.document.querySelector('[data-c-job-search]');search.value='Graduation';search.dispatchEvent(new w.Event('input'));await settle();
 assert.equal(w.document.querySelector('[data-c-photo] img').src,photo.src);assert.equal(fixture.calls.filter(c=>c.url==='/api/production/media-summary').length,1);
 w.document.querySelector('[data-c-photo] img').dispatchEvent(new w.Event('error'));assert.equal(w.document.querySelector('[data-c-photo] img'),null);assert.match(w.document.querySelector('[data-c-photo]').getAttribute('aria-label'),/unavailable/);
 w.document.querySelector('[data-c-job]').click();assert.deepEqual(fixture.opened,['j1']);
});
test('Client Management has a visible New Client action that saves through the existing client service',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());const original=w.json;
 w.json=async(url,options)=>{if(url==='/api/frontdesk/clients'&&options?.method==='POST'){fixture.created=JSON.parse(options.body);fixture.clients.push({id:'new',...fixture.created});return {id:'new'};}return original(url,options);};
 await w.StudioClients.render();w.document.querySelector('[data-c-new]').click();const form=w.document.querySelector('dialog form');assert.ok(form);
 form.elements.displayName.value='New Client';form.elements.email.value='client@example.test';form.elements.phone.value='8765550199';await form.onsubmit({preventDefault(){}});
 assert.equal(fixture.created.displayName,'New Client');assert.equal(fixture.navigation.options.clientId,'new');
 const css=fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/css/pages/clients.css'),'utf8');assert.match(css,/client-directory-table th:nth-child\(3\),\.clients-page \.client-directory-table td:nth-child\(3\)\{width:26%\}/);
});

function galleryLinksFixture(w,fixture){
 const json=w.json,dialog=w.studioDialog;
 w.studioDialog=(title,html)=>{const d=dialog(title,html);d.open=true;return d;};
 fixture.isAdmin=true;fixture.links=[{id:'pa',clientId:'a',status:'Pending',expiresAt:'2026-11-01'},{id:'pb',clientId:'b',status:'Pending',expiresAt:'2026-11-02'}];
 w.json=async(url,options)=>{
  if(url==='/api/auth/me')return {user:{isAdministrator:fixture.isAdmin}};
  if(url.startsWith('/api/admin/gallery-client-links/')){
   fixture.calls.push({url,options});
   if(options?.method==='POST'){if(fixture.linkDelay)await fixture.linkDelay;if(fixture.failLink)throw Error('API Site unavailable');return {code:'one-time-test-code',expiresAt:'2026-11-01'};}
   if(options?.method==='DELETE'){if(fixture.failRevoke)throw Error('Revoke rejected');fixture.links=fixture.links.filter(item=>!url.endsWith('/'+item.id));return{};}
   return {items:fixture.links};
  }
  return json(url,options);
 };
}
test('client gallery account linking uses the selected stored client and scoped link status',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());galleryLinksFixture(w,fixture);
 await w.StudioClients.render({clientId:'a',tab:'galleries'});await settle();w.document.querySelector('[data-c-link-gallery]').click();await settle();
 const dialog=w.document.querySelector('dialog');assert.match(dialog.textContent,/Andrea Campbell.*andrea@example.test/s);assert.equal(dialog.querySelector('select'),null);
 assert.ok(fixture.calls.some(call=>call.url==='/api/admin/gallery-client-links/?clientId=a'));assert.equal(dialog.querySelectorAll('[data-link-revoke]').length,1);assert.equal(dialog.querySelector('[data-link-revoke]').dataset.linkRevoke,'pa');assert.doesNotMatch(dialog.textContent,/2026-11-02/);
 dialog.querySelector('[data-link-create]').click();await settle();const post=fixture.calls.find(call=>call.options?.method==='POST');assert.equal(JSON.parse(post.options.body).clientId,'a');assert.equal(dialog.querySelector('[data-link-code]').value,'one-time-test-code');assert.equal(dialog.querySelector('[data-link-code-wrap]').hidden,false);
 const copied=[];Object.defineProperty(w.navigator,'clipboard',{value:{writeText:async value=>copied.push(value)}});dialog.querySelector('[data-link-copy]').click();await settle();assert.deepEqual(copied,['one-time-test-code']);
 dialog.querySelector('[data-link-revoke]').click();await settle();assert.ok(fixture.calls.some(call=>call.url.endsWith('/pa')&&call.options?.method==='DELETE'));assert.equal(dialog.querySelector('[data-link-code-wrap]').hidden,true);assert.equal(dialog.querySelector('[data-link-code]').value,'');assert.equal(fixture.links.length,1);
 assert.equal(fixture.calls.some(call=>call.url==='/api/admin/gallery-client-links/clients'),false);
});
test('gallery linking retains admin permission and never loads proofs for other staff',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());galleryLinksFixture(w,fixture);fixture.isAdmin=false;
 await w.StudioClients.render({clientId:'a',tab:'galleries'});await settle();assert.equal(w.document.querySelector('[data-c-link-gallery]'),null);assert.equal(fixture.calls.some(call=>call.url.startsWith('/api/admin/gallery-client-links')),false);assert.ok(w.document.querySelector('[data-c-media]'));
});
test('link errors remain visible and retryable; client navigation closes the dialog and discards late codes',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());galleryLinksFixture(w,fixture);fixture.failLink=true;
 await w.StudioClients.render({clientId:'a',tab:'galleries'});await settle();w.document.querySelector('[data-c-link-gallery]').click();await settle();let dialog=w.document.querySelector('dialog');dialog.querySelector('[data-link-create]').click();await settle();assert.match(dialog.querySelector('[data-link-status]').textContent,/API Site unavailable/);assert.equal(dialog.querySelector('[data-link-create]').disabled,false);assert.equal(dialog.querySelector('[data-link-code-wrap]').hidden,true);
 fixture.failRevoke=true;dialog.querySelector('[data-link-revoke]').click();await settle();assert.match(dialog.querySelector('[data-link-status]').textContent,/Revoke rejected/);assert.equal(dialog.querySelector('[data-link-revoke]').disabled,false);
 fixture.failLink=false;let release;fixture.linkDelay=new Promise(resolve=>release=resolve);dialog.querySelector('[data-link-create]').click();dialog.querySelector('[data-link-create]').click();assert.equal(fixture.calls.filter(call=>call.options?.method==='POST').length,2);
 await w.StudioClients.render({clientId:'b',tab:'jobs'});release();await settle();assert.equal(w.document.querySelector('dialog'),null);assert.equal(dialog.querySelector('[data-link-code]').value,'');assert.match(w.document.querySelector('[data-c-content]').textContent,/Other client job/);
});

