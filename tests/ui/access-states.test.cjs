const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const read=name=>fs.readFileSync(path.join(root,name),'utf8');
const settle=()=>new Promise(resolve=>setImmediate(resolve));
function setup(t,hash=''){
 const dom=new JSDOM(read('index.html'),{url:'https://studio.test/'+hash,runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());
 w.ResizeObserver=class{observe(){}disconnect(){}};w.requestAnimationFrame=fn=>w.setTimeout(fn,0);
 for(const name of ['studio-page.js','studio-access.js'])w.eval(read(name));
 Object.assign(w,{conversationView:w.document.getElementById('conversationView'),genericView:w.document.getElementById('genericView'),workspaceCache:[],renderWorkspaceMenu(){}});
 return w;
}
test('page denial has safe navigation, focuses the explanation and escapes the page title',async t=>{
 const w=setup(t),calls=[];w.studioNavigate=view=>calls.push(view);
 w.StudioAccess.page({title:'<img src=x onerror=alert(1)>'});
 assert.equal(w.document.querySelector('#genericView img'),null);
 assert.equal(w.document.activeElement,w.document.querySelector('#genericView h1'));
 w.document.querySelector('[data-access-home]').click();w.document.querySelector('[data-access-about]').click();
 assert.deepEqual(calls,['dashboard','about']);assert.ok(w.conversationView.classList.contains('hidden'));
 assert.equal(w.document.querySelector('[data-access-signout]'),null);
});
test('pending session retains its identity without fetching capabilities and can sign out',async t=>{
 const w=setup(t),calls=[];
 w.fetch=async url=>{calls.push(url);return {ok:true,status:200,json:async()=>({accountState:'PendingAccess',user:{displayName:'Jason Williams',userName:'jason',authenticationSource:'ActiveDirectory'}})};};
 w.eval(read('auth.js'));await w.studioSignedInUserReady;
 assert.deepEqual(calls,['/api/auth/me']);assert.equal(w.StudioAccess.pending(),true);
 w.StudioAccess.page({kind:'pending'});assert.match(w.genericView.textContent,/Jason Williams.*Active Directory/s);
 w.fetch=async url=>{calls.push(url);return {ok:false,status:503};};
 w.document.querySelector('[data-access-signout]').click();await settle();
 assert.equal(calls.at(-1),'/api/auth/logout');assert.equal(w.document.querySelector('[data-access-signout]').disabled,false);
 assert.match(w.document.querySelector('.studio-access-help').textContent,/Sign out could not/);
});
test('pending accounts are routed away from deep links and About never reads business APIs',async t=>{
 const w=setup(t,'#studio=jobs'),calls=[];
 const payload={accountState:'PendingAccess',user:{displayName:'Jason',userName:'jason',isAdministrator:false}};
 w.StudioAccess.setSession(payload);w.studioSignedInUserReady=Promise.resolve(payload.user);
 w.json=async url=>{calls.push(url);if(url==='/api/auth/me')return payload;throw Error('Unexpected business API '+url);};
 w.eval(read('studio-admin.js'));w.eval(read('studio-navigation.js'));w.eval(read('studio-shell-nav.js'));
 await settle();await settle();
 assert.equal(w.document.body.dataset.studioView,'access-pending');assert.match(w.genericView.textContent,/access is pending/);
 assert.ok(w.document.querySelector('.studio-navigation-scroll').classList.contains('hidden'));
 assert.ok(w.document.getElementById('notificationButton').classList.contains('hidden'));
 assert.ok(w.document.getElementById('studioSessionsTrigger').classList.contains('hidden'));
 assert.equal(w.document.getElementById('studioFooterWorkspaces').disabled,true);
 assert.equal(w.document.getElementById('studioFooterWorkspaces').textContent,'No workspace assigned');
 assert.deepEqual([...w.document.querySelectorAll('[data-studio-admin-view]')].filter(b=>!b.hidden).map(b=>b.dataset.studioAdminView),['about']);
 await w.studioNavigate('staff-roles');assert.equal(w.document.body.dataset.studioView,'access-pending');
 await w.studioNavigate('about');assert.ok(w.document.querySelector('.studio-about'));assert.equal(w.document.querySelector('[data-service-status],[data-demo-remove],[data-about-api-key]'),null);
 await w.studioNavigate('jobs',{browserHistory:true});assert.equal(w.document.body.dataset.studioView,'access-pending');
 assert.ok(calls.every(url=>url==='/api/auth/me'));
});
test('403 route reads show access denied while service failures retain the retry diagnostic',async t=>{
 const w=setup(t,'#studio=about');w.json=async()=>({user:{isAdministrator:false}});
 w.eval(read('studio-navigation.js'));await settle();
 w.StudioDashboard={dispose(){},render:async()=>{throw Object.assign(Error('forbidden'),{status:403});}};
 await w.studioNavigate('dashboard');assert.match(w.genericView.textContent,/You don’t have access/);assert.doesNotMatch(w.genericView.textContent,/Unable to load view/);
 w.StudioDashboard.render=async()=>{throw Object.assign(Error('Service unavailable'),{status:503});};
 await assert.rejects(w.studioNavigate('dashboard'),/Service unavailable/);assert.match(w.genericView.textContent,/Unable to load view/);
});
test('restricted media does not fetch assets or expose upload controls',async t=>{
 const w=setup(t),calls=[];w.eval(read('job-details.js'));
 w.json=async url=>{calls.push(url);return {permissions:{viewAttachments:false,manageProduction:false}};};
 const host=w.document.createElement('div');w.genericView.append(host);
 await w.StudioJobDetails.media({id:'job'},host,null,()=>true);
 assert.deepEqual(calls,['/api/jobs/job/access']);assert.match(host.textContent,/don’t have access to job media/);
 assert.equal(host.querySelector('input[type=file],img,[data-add]'),null);assert.ok(host.querySelector('h2'));assert.equal(host.querySelector('h1'),null);
});
test('a media denial returned by the server uses the same section state',async t=>{
 const w=setup(t);w.eval(read('job-details.js'));
 w.json=async url=>{if(url.endsWith('/access'))return {permissions:{viewAttachments:true}};throw Object.assign(Error('Forbidden'),{status:403});};
 const host=w.document.createElement('div');w.genericView.append(host);
 await w.StudioJobDetails.media({id:'job'},host,null,()=>true);
 assert.match(host.textContent,/don’t have access to job media/);assert.equal(host.querySelector('button'),null);
});
test('financial statement checks permission before requesting balances or invoices',async t=>{
 const w=setup(t);w.document.body.dataset.studioView='clients';const calls=[];
 w.json=async url=>{calls.push(url);if(url==='/api/frontdesk/clients')return [{id:'client',displayName:'Example Client'}];if(url==='/api/jobs')return [{id:'job',clientId:'client',title:'Example job',financialEvents:[{amount:999999}]}];if(url.endsWith('/access'))return {permissions:{viewFinancials:false}};return [];};
 w.eval(read('clients.js'));await w.StudioClients.render({clientId:'client',tab:'finance'});await settle();
 assert.match(w.document.querySelector('[data-c-content]').textContent,/don’t have access to this financial statement/);
 assert.doesNotMatch(w.document.querySelector('[data-c-content]').textContent,/999999/);
 assert.equal(calls.some(url=>/invoices|finance-release/.test(url)),false);
});
