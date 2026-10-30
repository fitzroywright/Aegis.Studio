const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
function setup(t,administrator=true){
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{runScripts:'outside-only',url:'https://studio.test/#studio=studio-management'}),w=dom.window;t.after(()=>w.close());
 Object.assign(w,{esc:String,money:String,workspaceCache:[],workspaceAccessCache:[],studioRolesCache:[],selectedWorkspace:null,renderWorkspaceMenu(){},renderWorkspaceAdminRow:()=>'',renderEmployeeDirectorySection:()=>'<section>Staff directory and Roles</section>',renderSecurityRolesSection:()=>'<section>Roles</section>',renderWorkflowSection:()=>'<section>Workflows</section>',hydrateWorkspaceManagers:async()=>{},refreshStudioEmployees:async()=>{},refreshWorkflowList:async()=>{},renderGlobalRoleAdministration:async()=>{},renderAnnouncementAdministration:async()=>{},loadWorkspaces:async()=>{},studioGetCurrentDemoDataRun:async()=>null});
 w.json=async url=>url==='/api/auth/me'?{user:{isAdministrator:administrator}}:url==='/api/admin/studio-status'?{database:'Ready',storage:{provider:'Local',available:true,writable:true},previews:{queued:2,failed:0,unsupported:0},messaging:{queuedEmails:1,failedNotifications:0},checkedAtUtc:'2026-10-27T22:00:00Z'}:[];
 w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;};
 w.ResizeObserver=class{observe(){} disconnect(){}};w.requestAnimationFrame=fn=>w.setTimeout(fn,0);
 for(const file of ['studio-message-card.js','studio-page.js','studio-admin.js','booking-sessions.js','commercial.js','studio-navigation.js'])w.eval(fs.readFileSync(path.join(root,file),'utf8'));
 return {w,dom};
}
test('About contains product, support, service health and demo tools; Staff and Roles share one page',async t=>{
 const {w}=setup(t);await new Promise(resolve=>w.setTimeout(resolve,0));await w.studioNavigate('about');
 assert.equal(w.document.querySelector('.st-page').getAttribute('aria-label'),'About');assert.equal(w.document.querySelector('.st-toolbar h1'),null);
 assert.ok(w.document.querySelector('[data-service-status]'));assert.ok(w.document.querySelector('[data-demo-remove]'));assert.ok(w.document.querySelector('[data-load-demo]'));
 assert.equal(w.document.querySelector('.studio-about-logo-player .studio-about-logo').getAttribute('src'),'/thinking-machines-about-once.gif');
 assert.equal(w.document.querySelectorAll('.studio-about-logo-player').length,1);
 assert.match(w.document.querySelector('.studio-about').textContent,/it@thinkingmachines.ltd.*America\/Jamaica/s);
 assert.equal(w.document.querySelector('.studio-about h2'),null);
 assert.equal(w.document.querySelectorAll('.studio-about-grid > .studio-about-card').length,3);
 assert.equal(w.document.querySelector('.st-content.st-card-scroll'),null);
 assert.ok(w.document.querySelector('[data-service-status] .studio-health-details'));
 assert.equal(w.document.querySelector('[data-service-status]').closest('.studio-about'),null);
 assert.equal(w.StudioAdmin.entries.map(entry=>entry.view).join(','),'catalog,templates,staff-roles,workspace-management,about');assert.equal(w.StudioAdmin.entries.some(entry=>['studio-management','demo-data','staff-admin','roles-admin','api-site-key','booking-sessions'].includes(entry.view)),false);
 for(const [view,text] of [['staff-roles','Staff directory']]){
  await w.studioNavigate(view);assert.ok(w.document.getElementById('genericView').textContent.includes(text),view);assert.equal(w.document.querySelector('[data-service-status],#studioDemoLoader'),null);assert.equal(w.location.hash.includes('studio='+view),true);
  if(view==='staff-roles')assert.match(w.document.getElementById('genericView').textContent,/Staff directory.*Roles/s);
 }
 for(const [old,canonical] of [['admin','about'],['studio-management','about'],['demo-data','about'],['staff-admin','staff-roles'],['roles-admin','staff-roles']]){await w.studioNavigate(old);assert.equal(w.document.body.dataset.studioView,canonical);assert.match(w.location.hash,new RegExp('studio='+canonical));}
});
test('the consolidated management page retains its administrator access check',async t=>{
 const {w}=setup(t,false);await w.studioNavigate('studio-management');
 assert.match(w.document.getElementById('genericView').textContent,/Administrator access required/);assert.equal(w.document.querySelector('[data-demo-remove]'),null);
});
test('the avatar menu contains one Preferences action and no separate Administration entry',t=>{
 const {w}=setup(t);const labels=[...w.document.querySelectorAll('.user-menu-popover button')].map(b=>b.textContent);
 assert.deepEqual(labels,['Preferences','Notifications','Sign Out']);assert.equal(w.document.querySelector('#profileDialog h2').textContent,'Preferences');
});


test('every extracted admin route denies non-administrators before rendering controls',async t=>{
 const {w}=setup(t,false);for(const view of ['demo-data','staff-admin','roles-admin','workflows-admin','api-site-key']){
  await w.studioNavigate(view);assert.match(w.document.getElementById('genericView').textContent,/Administrator access required/);assert.equal(w.document.querySelector('[data-about-api-key],[data-api-key-generate],[data-demo-remove],#publicBookingCatalog'),null);
 }
});
test('About support card opens the existing key generator in a dialog without changing the page',async t=>{
 const {w}=setup(t);await new Promise(resolve=>w.setTimeout(resolve,0));
 w.studioDialog=(title,body)=>{const dialog=w.document.createElement('dialog');dialog.innerHTML=body;dialog.close=()=>{dialog.open=false;dialog.remove();dialog.dispatchEvent(new w.Event('close'));};w.document.body.append(dialog);dialog.showModal();return dialog;};
 await w.studioNavigate('about');assert.ok(w.document.querySelector('.studio-about [data-about-api-key]'));assert.equal(w.document.querySelector('[data-api-key-generate]'),null);
 w.document.querySelector('[data-about-api-key]').click();const button=w.document.querySelector('[data-api-key-generate]');const dialog=button.closest('dialog');button.click();
 const credential=JSON.parse(dialog.querySelector('[data-api-key-credential]').value);assert.equal(credential.id,'studio');assert.match(credential.secret,/^[a-f0-9]{64}$/);assert.ok(credential.scopes.includes('gallery:publish'));assert.ok(!credential.scopes.includes('payment:verified'));assert.equal(dialog.querySelector('[data-api-key-setting]').value,'PublicSite__IntegrationToken='+credential.secret);
 button.click();assert.notEqual(JSON.parse(dialog.querySelector('[data-api-key-credential]').value).secret,credential.secret);assert.equal(w.localStorage.length,0);assert.equal(w.document.body.dataset.studioView,'about');
 await w.studioNavigate('staff-roles');await new Promise(resolve=>w.setTimeout(resolve,0));assert.equal(w.document.querySelector('dialog [data-api-key-credential]'),null);
 await w.studioNavigate('api-site-key');assert.equal(w.document.body.dataset.studioView,'about');assert.ok(w.document.querySelector('.studio-about [data-about-api-key]'));
});
test('About retains demo removal confirmation and does not mutate data on cancellation',async t=>{
 const {w}=setup(t);await new Promise(resolve=>w.setTimeout(resolve,0));await w.studioNavigate('about');const read=w.json,writes=[];
 w.json=async(url,options)=>{if(options?.method==='POST'){writes.push({url,options});return{message:'Demo removed.'};}return read(url,options);};
 w.prompt=()=>null;const button=w.document.querySelector('[data-demo-remove]');await button.onclick();assert.equal(writes.length,0);
 w.prompt=()=>'REMOVE STUDIO DEMO DATA';await button.onclick();assert.equal(writes.length,1);assert.equal(writes[0].url,'/api/admin/connected-workflow/demo-data/remove');assert.equal(JSON.parse(writes[0].options.body).confirmation,'REMOVE STUDIO DEMO DATA');assert.equal(button.disabled,false);assert.match(w.document.querySelector('[data-demo-status]').textContent,/Demo removed/);
});

