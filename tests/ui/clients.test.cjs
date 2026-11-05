const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot'),code=fs.readFileSync(path.join(root,'clients.js'),'utf8');
const settle=()=>new Promise(r=>setTimeout(r,15));
function setup(){
 const dom=new JSDOM('<body data-studio-view="clients"><section id="conversationView"></section><section id="genericView"></section></body>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;
 const fixture={calls:[],opened:[],isAdmin:true,allowed:true,failPublish:false,failFinance:false,delay:null,permissions:['view','create','edit','merge','archive','communications.view','communications.send','tasks.manage','financials.view','export','automations.manage']};
 fixture.clients=[{id:'a',displayName:'Andrea Campbell',email:'andrea@example.test',phone:'876-555-0123'},{id:'b',displayName:'Andrea Campbell',email:'other@example.test'}].map(c=>({...c,clientType:'Individual',relationshipStatus:'Active',clientSince:'2020-01-01',tags:[],metadata:{},primaryWorkspaceId:'w1',totalJobs:1,lifetimeValue:30}));
 fixture.jobs=[{id:'j1',clientId:'a',title:'Graduation',workspaceId:'w1',workspace:'Portraits',status:'Ready for Delivery'},{id:'j2',clientId:'b',title:'Other client job',workspaceId:'w1',status:'Completed'},{id:'j3',clientName:'Andrea Campbell',title:'Unlinked snapshot',status:'Pending'}];
 w.conversationView=w.document.getElementById('conversationView');w.genericView=w.document.getElementById('genericView');w.StudioPage={escape:v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),setCount:c=>fixture.count=c};
 w.eval(fs.readFileSync(path.join(root,'studio-access.js'),'utf8'));
 w.openWorkspaceJob=async id=>fixture.opened.push(id);w.StudioNextActions={openEnquiry:async id=>fixture.enquiry=id};
 w.studioNavigate=async(view,options={})=>{fixture.navigation={view,options};if(view==='clients')await w.StudioClients.render(options);};
 w.StudioClientReminders={mountClient:async(host,jobs)=>{fixture.reminderJobs=jobs;host.textContent='Scheduled client messages';}};
 w.studioDialog=(title,html)=>{const d=w.document.createElement('dialog');d.innerHTML='<div class="studio-dialog-body">'+html+'</div>';d.close=()=>d.remove();w.document.body.append(d);return d;};
 w.json=async(url,options)=>{
  fixture.calls.push({url,options});const u=new URL(url,'https://studio.test');
  if(url==='/api/crm/capabilities')return{isAdministrator:fixture.isAdmin,permissions:fixture.permissions.map(p=>'studio.crm.'+p),types:['Individual','Family / Household','School','Corporate','Organisation'],statuses:['Prospect','Active','Inactive','Archived'],taskTypes:['Call Client','General Follow-up'],leadStatuses:['New','Contacted','Qualified','Quoted','Awaiting Decision','Won','Lost'],sources:['Website','Walk-In','Phone','Email','Staff Entry']};
  if(url==='/api/workspaces')return[{id:'w1',name:'Portraits'}];
  if(url==='/api/crm/views')return fixture.views||[];
  if(u.pathname==='/api/crm/clients'){
   if(options?.method==='POST'){fixture.created=JSON.parse(options.body);fixture.clients.push({id:'new',tags:[],metadata:{},...fixture.created});return{id:'new'};}
   const term=(u.searchParams.get('search')||'').toLowerCase();let rows=fixture.clients.filter(c=>[c.displayName,c.email,c.phone].join(' ').toLowerCase().includes(term));const size=Number(u.searchParams.get('pageSize')||50),page=Number(u.searchParams.get('page')||1);return{items:rows.slice((page-1)*size,page*size),total:rows.length,page,pageSize:size};
  }
  const client=/^\/api\/crm\/clients\/([^/]+)$/.exec(u.pathname);if(client){if(options){fixture.updated=JSON.parse(options.body);return{id:client[1]};}return fixture.clients.find(c=>c.id===client[1]);}
  if(u.pathname==='/api/crm/tasks')return fixture.tasks||[];
  if(url.endsWith('/contacts'))return[];
  if(url.endsWith('/duplicates'))return[];
  if(url.endsWith('/financials')){if(fixture.failFinance)throw Object.assign(Error('Financials restricted'),{status:403});return fixture.financials||{totalInvoiced:0,totalPaid:0,lifetimeValue:30,outstandingBalance:30,averageOrderValue:30,completedJobs:0,rules:'Signed receipts and ledger balance',invoices:[{id:'i1',jobId:'j1',invoiceNumber:'INV-1',currency:'USD',totalAmount:20,status:'Issued'}],ledger:[{jobId:'j1',kind:'Refund',amount:-10,note:'Correction'}]};}
  if(url.includes('/sections/')){
   const match=/clients\/([^/]+)\/sections\/([^/?]+)/.exec(url),id=match[1],tab=match[2];if(fixture.delay&&tab==='galleries')await fixture.delay;
   if(fixture.failSection===tab)throw Error('Section unavailable');
   let rows=tab==='jobs'?fixture.jobs.filter(j=>j.clientId===id):tab==='galleries'?[{id:'p1',jobId:'j1',title:'Graduation',status:'Publication handoff recorded',provider:'Manifest',reference:'approved/publication.json'}]:tab==='enquiries'?[]:tab==='sessions'?[{id:'s1',jobId:'j1',title:'Graduation',photographer:'sam',status:'Scheduled'}]:tab==='communications'?[{id:'m1',enquiryId:'e1',sender:'Staff',recipient:'andrea@example.test',body:'Message',status:'Failed',lastError:'Provider unavailable'}]:tab==='activity'?[{eventType:'JobCreated',description:'Pending job prepared',actor:'staff',jobId:'j1'}]:[];
   return{items:rows,total:rows.length,page:Number(u.searchParams.get('page')||1)};
  }
  if(options)return{};
  throw Error('Unexpected URL '+url);
 };
 w.eval(code);return{dom,w,fixture};
}
function tab(w,key){w.document.querySelector('[data-tab="'+key+'"]').click();}
test('client jobs join by stored identity and directory search works',async t=>{const {w,fixture}=setup();t.after(()=>w.close());await w.StudioClients.render({clientId:'a',tab:'jobs'});assert.match(w.genericView.textContent,/Graduation/);assert.doesNotMatch(w.genericView.textContent,/Other client job|Unlinked snapshot/);w.document.querySelector('[data-job]').click();assert.deepEqual(fixture.opened,['j1']);await w.StudioClients.render();const search=w.document.querySelector('[data-search]');search.value='other@example';search.dispatchEvent(new w.Event('input'));await new Promise(r=>setTimeout(r,300));assert.equal(w.document.querySelectorAll('[data-client]').length,1);assert.equal(w.document.querySelector('[data-client]').dataset.client,'b');assert.equal(fixture.calls.some(c=>c.url==='/api/jobs'),false);});
test('gallery preview delegates to the authoritative job and never publishes implicitly',async t=>{const {w,fixture}=setup();t.after(()=>w.close());await w.StudioClients.render({clientId:'a',tab:'galleries'});assert.match(w.genericView.textContent,/Publication handoff recorded/);w.document.querySelector('[data-job]').click();await settle();assert.deepEqual(fixture.opened,['j1']);assert.equal(fixture.calls.some(c=>c.options?.method==='POST'),false);});
test('publication permissions and load failures remain visible without fabricated success',async t=>{const {w,fixture}=setup();t.after(()=>w.close());fixture.failSection='galleries';await w.StudioClients.render({clientId:'a',tab:'galleries'});assert.match(w.genericView.textContent,/Section unavailable/);assert.ok(w.document.querySelector('[data-retry]'));assert.doesNotMatch(w.genericView.textContent,/Publication handoff recorded/);fixture.failSection=null;w.document.querySelector('[data-retry]').click();await settle();assert.match(w.genericView.textContent,/Publication handoff recorded/);assert.equal(fixture.calls.some(c=>c.options?.method==='POST'),false);});
test('financial statement preserves signed ledger values and invoice currency',async t=>{const {w}=setup();t.after(()=>w.close());await w.StudioClients.render({clientId:'a',tab:'finance'});const text=w.document.querySelector('[data-content]').textContent;assert.match(text,/INV-1/);assert.match(text,/USD/);assert.match(text,/-10/);assert.match(text,/30/);assert.doesNotMatch(text,/Other client job/);});
test('restricted financial data is shown as unavailable rather than zero',async t=>{const {w,fixture}=setup();t.after(()=>w.close());fixture.failFinance=true;await w.StudioClients.render({clientId:'a',tab:'finance'});assert.match(w.document.querySelector('[data-content]').textContent,/don’t have access/);assert.doesNotMatch(w.document.querySelector('[data-content]').textContent,/J\$0/);});
test('late gallery responses do not replace a newly opened client or route',async t=>{const {w,fixture}=setup();t.after(()=>w.close());let release;fixture.delay=new Promise(r=>release=r);const loading=w.StudioClients.render({clientId:'a',tab:'galleries'});await settle();await w.StudioClients.render({clientId:'b',tab:'jobs'});release();await loading;assert.match(w.document.querySelector('[data-content]').textContent,/Other client job/);assert.doesNotMatch(w.document.querySelector('[data-content]').textContent,/approved\/publication/);});
test('client and job text is escaped',async t=>{const {w,fixture}=setup();t.after(()=>w.close());fixture.clients[0].displayName='<img src=x onerror=bad()>';fixture.jobs[0].title='<script>bad()</script>';await w.StudioClients.render({clientId:'a',tab:'jobs'});assert.equal(w.genericView.querySelector('script,img'),null);assert.match(w.genericView.textContent,/<script>bad/);});
test('Clients has no Messages or Notes card in the directory or selected-client view',async t=>{const {w}=setup();t.after(()=>w.close());for(const options of [{},{clientId:'a'}]){await w.StudioClients.render(options);assert.equal(w.document.querySelector('#clientMessageCard,.smc-card,#studioContextRail'),null);assert.ok(w.document.querySelector('.crm-page'));}});
test('client directory uses the canonical route and stored client identity',async t=>{const {w,fixture}=setup();t.after(()=>w.close());await w.StudioClients.render();w.document.querySelector('[data-client="b"]').click();await settle();assert.equal(fixture.navigation.options.clientId,'b');assert.equal(w.document.querySelector('[data-search]'),null);assert.ok(w.document.querySelector('[data-content]'));});
test('client directory search preserves focus while typing and uses server search',async t=>{const {w,fixture}=setup();t.after(()=>w.close());await w.StudioClients.render();let search=w.document.querySelector('[data-search]');search.focus();search.value='Camp';search.dispatchEvent(new w.Event('input'));await new Promise(r=>setTimeout(r,300));search=w.document.querySelector('[data-search]');assert.equal(w.document.activeElement,search);assert.equal(search.value,'Camp');assert.ok(fixture.calls.some(c=>c.url.includes('search=Camp')));});
test('Client Reminders tab passes only jobs associated with the selected client',async t=>{const {w,fixture}=setup();t.after(()=>w.close());await w.StudioClients.render({clientId:'a',tab:'reminders'});assert.deepEqual(Array.from(fixture.reminderJobs,j=>j.id),['j1']);});
test('client directory opens a dedicated profile and restores directory filters',async t=>{const {w}=setup();t.after(()=>w.close());await w.StudioClients.render();const filter=w.document.querySelector('[name=type]');filter.value='School';w.document.querySelector('form').dispatchEvent(new w.Event('submit',{cancelable:true}));await settle();await w.StudioClients.render({clientId:'a'});assert.equal(w.document.querySelector('.crm-directory-scroll'),null);w.document.querySelector('[data-back]').click();await settle();assert.equal(w.document.querySelector('[name=type]').value,'School');});
test('financial table retains exact balances, invoice currencies and job links',async t=>{const {w,fixture}=setup();t.after(()=>w.close());await w.StudioClients.render({clientId:'a',tab:'financials'});assert.match(w.genericView.textContent,/Ledger balance/);assert.match(w.genericView.textContent,/USD/);w.document.querySelector('[data-job]').click();assert.deepEqual(fixture.opened,['j1']);});
test('financial metrics distinguish zero, credit and restricted balances',async t=>{const {w,fixture}=setup();t.after(()=>w.close());fixture.financials={totalInvoiced:0,totalPaid:0,lifetimeValue:0,outstandingBalance:-20,averageOrderValue:0,completedJobs:0,rules:'Signed ledger',invoices:[],ledger:[]};await w.StudioClients.render({clientId:'a',tab:'finance'});assert.match(w.genericView.textContent,/-.*20/);fixture.permissions=fixture.permissions.filter(p=>p!=='financials.view');await w.StudioClients.render({clientId:'a',tab:'finance'});assert.match(w.document.querySelector('[data-content]').textContent,/don’t have access/);});
test('printing clones the statement and preserves signed ledger values',async t=>{const {w}=setup();t.after(()=>w.close());const printDom=new JSDOM('<html><head></head><body></body></html>');t.after(()=>printDom.window.close());let printed=false;w.open=()=>({document:printDom.window.document,focus(){},print(){printed=true;}});await w.StudioClients.render({clientId:'a',tab:'finance'});w.document.querySelector('[data-print]').click();assert.equal(printed,true);assert.match(printDom.window.document.body.textContent,/-10/);assert.match(printDom.window.document.body.textContent,/USD/);});
test('Client Management omits retired rails and message cards while finance remains usable',async t=>{const {w}=setup();t.after(()=>w.close());await w.StudioClients.render({clientId:'a',tab:'finance'});assert.equal(w.document.querySelector('#studioContextRail,.client-directory,.smc-card'),null);assert.ok(w.document.querySelector('[data-print]'));});
test('Client Jobs uses a compact table and keeps gallery access in its own tab',async t=>{const {w,fixture}=setup();t.after(()=>w.close());await w.StudioClients.render({clientId:'a',tab:'jobs'});assert.equal(w.document.querySelectorAll('[data-content] th').length,5);assert.equal(w.document.querySelector('[data-gallery]'),null);w.document.querySelector('[data-job]').click();assert.deepEqual(fixture.opened,['j1']);tab(w,'galleries');await settle();assert.match(w.genericView.textContent,/Publication handoff recorded/);});
test('client job listing delegates photos to the permission checked job gallery',async t=>{const {w,fixture}=setup();t.after(()=>w.close());await w.StudioClients.render({clientId:'a',tab:'jobs'});assert.equal(w.document.querySelector('[data-job="j2"]'),null);assert.equal(fixture.calls.some(c=>c.url.includes('production/media-summary')),false);tab(w,'galleries');await settle();w.document.querySelector('[data-job]').click();assert.deepEqual(fixture.opened,['j1']);});
test('Client Management has a visible Create Client action saving through CRM service',async t=>{const {w,fixture}=setup();t.after(()=>w.close());await w.StudioClients.render();w.document.querySelector('[data-new]').click();const form=w.document.querySelector('dialog form');form.elements.displayName.value='New Client';form.elements.email.value='client@example.test';form.elements.phone.value='8765550199';form.elements.primaryWorkspaceId.value='w1';await form.onsubmit({preventDefault(){}});assert.equal(fixture.created.displayName,'New Client');assert.equal(fixture.navigation.options.clientId,'new');assert.equal(fixture.created.primaryWorkspaceId,'w1');});
test('client navigation records selection and restores it on browser back',async t=>{const d=new JSDOM('<body data-studio-view="clients"><div><nav id="studioBreadcrumb"></nav><div id="studioPageTools"></div></div><div id="conversationView"></div><div id="genericView"></div></body>',{url:'https://studio.test/#studio=clients&client=a',runScripts:'outside-only'}),w=d.window;t.after(()=>w.close());const seen=[];w.StudioClients={render:async o=>seen.push(o.clientId),invalidate(){}};w.StudioPage={invalidate(){}};w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/studio-navigation.js'),'utf8'));w.document.dispatchEvent(new w.Event('DOMContentLoaded'));await settle();assert.equal(w.history.state.studioClientId,'a');await w.studioNavigate('clients',{clientId:'b'});assert.equal(w.history.state.studioClientId,'b');assert.match(w.location.hash,/client=b/);w.history.back();await new Promise(r=>setTimeout(r,25));assert.equal(w.history.state.studioClientId,'a');assert.equal(seen.at(-1),'a');});



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
 await w.StudioClients.render({clientId:'a',tab:'galleries'});await settle();w.document.querySelector('[data-gallery-link]').click();await settle();
 const dialog=w.document.querySelector('dialog');assert.match(dialog.textContent,/Andrea Campbell.*andrea@example.test/s);assert.equal(dialog.querySelector('select'),null);
 assert.ok(fixture.calls.some(call=>call.url==='/api/admin/gallery-client-links/?clientId=a'));assert.equal(dialog.querySelectorAll('[data-link-revoke]').length,1);assert.equal(dialog.querySelector('[data-link-revoke]').dataset.linkRevoke,'pa');assert.doesNotMatch(dialog.textContent,/2026-11-02/);
 dialog.querySelector('[data-link-create]').click();await settle();const post=fixture.calls.find(call=>call.options?.method==='POST');assert.equal(JSON.parse(post.options.body).clientId,'a');assert.equal(dialog.querySelector('[data-link-code]').value,'one-time-test-code');assert.equal(dialog.querySelector('[data-link-code-wrap]').hidden,false);
 const copied=[];Object.defineProperty(w.navigator,'clipboard',{value:{writeText:async value=>copied.push(value)}});dialog.querySelector('[data-link-copy]').click();await settle();assert.deepEqual(copied,['one-time-test-code']);
 dialog.querySelector('[data-link-revoke]').click();await settle();assert.ok(fixture.calls.some(call=>call.url.endsWith('/pa')&&call.options?.method==='DELETE'));assert.equal(dialog.querySelector('[data-link-code-wrap]').hidden,true);assert.equal(dialog.querySelector('[data-link-code]').value,'');assert.equal(fixture.links.length,1);
 assert.equal(fixture.calls.some(call=>call.url==='/api/admin/gallery-client-links/clients'),false);
});
test('gallery linking retains admin permission and never loads proofs for other staff',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());galleryLinksFixture(w,fixture);fixture.isAdmin=false;
 await w.StudioClients.render({clientId:'a',tab:'galleries'});await settle();assert.equal(w.document.querySelector('[data-gallery-link]'),null);assert.equal(fixture.calls.some(call=>call.url.startsWith('/api/admin/gallery-client-links')),false);assert.ok(w.document.querySelector('[data-job]'));
});
test('link errors remain visible and retryable; client navigation closes the dialog and discards late codes',async t=>{
 const {dom,w,fixture}=setup();t.after(()=>dom.window.close());galleryLinksFixture(w,fixture);fixture.failLink=true;
 await w.StudioClients.render({clientId:'a',tab:'galleries'});await settle();w.document.querySelector('[data-gallery-link]').click();await settle();let dialog=w.document.querySelector('dialog');dialog.querySelector('[data-link-create]').click();await settle();assert.match(dialog.querySelector('[data-link-status]').textContent,/API Site unavailable/);assert.equal(dialog.querySelector('[data-link-create]').disabled,false);assert.equal(dialog.querySelector('[data-link-code-wrap]').hidden,true);
 fixture.failRevoke=true;dialog.querySelector('[data-link-revoke]').click();await settle();assert.match(dialog.querySelector('[data-link-status]').textContent,/Revoke rejected/);assert.equal(dialog.querySelector('[data-link-revoke]').disabled,false);
 fixture.failLink=false;let release;fixture.linkDelay=new Promise(resolve=>release=resolve);dialog.querySelector('[data-link-create]').click();dialog.querySelector('[data-link-create]').click();assert.equal(fixture.calls.filter(call=>call.options?.method==='POST').length,2);
 await w.StudioClients.render({clientId:'b',tab:'jobs'});release();await settle();assert.equal(w.document.querySelector('dialog'),null);assert.equal(dialog.querySelector('[data-link-code]').value,'');assert.match(w.document.querySelector('[data-content]').textContent,/Other client job/);
});

test('Clients directory uses Jobs-style board, inline filters and scrolling rows',async t=>{
 const {w}=setup();t.after(()=>w.close());await w.StudioClients.render();
 assert.ok(w.document.querySelector('.crm-jobs-board'));
 assert.ok(w.document.querySelector('.crm-jobs-head [data-search]'));
 assert.ok(w.document.querySelector('.crm-jobs-head [data-header-status]'));
 assert.ok(w.document.querySelector('.crm-jobs-body [data-client-row]'));
 assert.ok(w.document.querySelector('.crm-pagination'));
 assert.equal(w.document.querySelectorAll('.crm-jobs-head th').length,6);
 assert.equal(w.document.querySelector('[data-import]'),null);
 assert.equal(w.document.querySelector('[data-export]'),null);
 w.document.querySelector('[data-client-view=active]').click();
 await settle();
 assert.equal(w.document.querySelector('[data-client-view=active]').getAttribute('aria-pressed'),'true');
});
