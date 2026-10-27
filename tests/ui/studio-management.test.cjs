const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
function setup(t,administrator=true){
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{runScripts:'outside-only',url:'https://studio.test/#studio=studio-management'}),w=dom.window;t.after(()=>w.close());
 Object.assign(w,{esc:String,money:String,workspaceCache:[],workspaceAccessCache:[],studioRolesCache:[],selectedWorkspace:null,renderWorkspaceMenu(){},renderWorkspaceAdminRow:()=>'',renderEmployeeDirectorySection:()=>'<section>Staff directory</section>',renderSecurityRolesSection:()=>'<section>Roles</section>',renderWorkflowSection:()=>'<section>Workflows</section>',hydrateWorkspaceManagers:async()=>{},refreshStudioEmployees:async()=>{},refreshWorkflowList:async()=>{},renderGlobalRoleAdministration:async()=>{},renderAnnouncementAdministration:async()=>{},loadWorkspaces:async()=>{},studioGetCurrentDemoDataRun:async()=>null});
 w.json=async url=>url==='/api/auth/me'?{user:{isAdministrator:administrator}}:url==='/api/admin/studio-status'?{database:'Ready',storage:{provider:'Local',available:true,writable:true},previews:{},messaging:{}}:[];
 w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;};
 w.ResizeObserver=class{observe(){} disconnect(){}};w.requestAnimationFrame=fn=>w.setTimeout(fn,0);
 for(const file of ['studio-message-card.js','studio-page.js','studio-admin.js','booking-sessions.js','commercial.js','studio-navigation.js'])w.eval(fs.readFileSync(path.join(root,file),'utf8'));
 return {w,dom};
}
test('Admin routes mount their own content without retaining unrelated cards',async t=>{
 const {w}=setup(t);await new Promise(resolve=>w.setTimeout(resolve,0));await w.studioNavigate('studio-management');
 assert.equal(w.document.querySelector('.st-toolbar h1').textContent,'Studio Management');
 assert.equal(w.StudioAdmin.entries.some(entry=>entry.view==='gallery-access-admin'),false);
 assert.ok(w.document.querySelector('[data-service-status]'));
 assert.equal(w.document.querySelector('#studioDemoLoader,#publicBookingCatalog,#galleryClientLinkAdmin'),null);
 for(const [view,text] of [['demo-data','Demo Data'],['staff-admin','Staff directory'],['roles-admin','Roles'],['workflows-admin','Workflows'],['booking-sessions','Session Types'],['api-site-key','API Site key']]){
   await w.studioNavigate(view);assert.ok(w.document.getElementById('genericView').textContent.includes(text),view);
   assert.equal(w.document.querySelector('[data-service-status]'),null);
   assert.equal(w.location.hash.includes('studio='+view),true);
 }
 await w.studioNavigate('demo-data');assert.ok(w.document.querySelector('[data-demo-reset]'));assert.ok(w.document.querySelector('[data-load-demo]'));
 await w.studioNavigate('admin');assert.equal(w.document.body.dataset.studioView,'studio-management');
});
test('the consolidated management page retains its administrator access check',async t=>{
 const {w}=setup(t,false);await w.studioNavigate('studio-management');
 assert.match(w.document.getElementById('genericView').textContent,/Administrator access required/);assert.equal(w.document.querySelector('[data-demo-reset]'),null);
});
test('the avatar menu contains one Preferences action and no separate Administration entry',t=>{
 const {w}=setup(t);const labels=[...w.document.querySelectorAll('.user-menu-popover button')].map(b=>b.textContent);
 assert.deepEqual(labels,['Preferences','Notifications','Activity','Sign Out']);assert.equal(w.document.querySelector('#profileDialog h2').textContent,'Preferences');
});


test('every extracted admin route denies non-administrators before rendering controls',async t=>{
 const {w}=setup(t,false);for(const view of ['booking-sessions','demo-data','staff-admin','roles-admin','workflows-admin','api-site-key']){
  await w.studioNavigate(view);assert.match(w.document.getElementById('genericView').textContent,/Administrator access required/);assert.equal(w.document.querySelector('[data-api-key-generate],[data-demo-reset],#publicBookingCatalog'),null);
 }
});
test('key generation produces independent scoped credentials without network or persistence',async t=>{
 const {w}=setup(t);await new Promise(resolve=>w.setTimeout(resolve,0));await w.studioNavigate('api-site-key');const button=w.document.querySelector('[data-api-key-generate]');button.click();
 const credential=JSON.parse(w.document.querySelector('[data-api-key-credential]').value);assert.equal(credential.id,'studio');assert.match(credential.secret,/^[a-f0-9]{64}$/);assert.ok(credential.scopes.includes('gallery:publish'));assert.ok(!credential.scopes.includes('payment:verified'));assert.equal(w.document.querySelector('[data-api-key-setting]').value,'PublicSite__IntegrationToken='+credential.secret);
 button.click();assert.notEqual(JSON.parse(w.document.querySelector('[data-api-key-credential]').value).secret,credential.secret);assert.equal(w.localStorage.length,0);
 await w.studioNavigate('studio-management');assert.equal(w.document.querySelector('[data-api-key-credential]'),null);
});
