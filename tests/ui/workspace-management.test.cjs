const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot'),read=name=>fs.readFileSync(path.join(root,name),'utf8'),settle=()=>new Promise(resolve=>setImmediate(resolve));
function setup(t,admin=true){
 const dom=new JSDOM(read('index.html'),{url:'https://studio.test',runScripts:'outside-only',pretendToBeVisual:true}),w=dom.window;t.after(()=>w.close());w.document.body.dataset.studioView='workspace-management';
 w.HTMLElement.prototype.scrollIntoView=function(){};const calls=[],navigation=[];
 const workspaces=[{workspace:{id:'w',name:'Portraits',emailAddress:'portraits@studio.test',isEnabled:true},permissions:{accessWorkspace:true,view:true,manageMembers:admin,editWorkspace:admin}},{workspace:{id:'unassigned',name:'Unassigned',isEnabled:true},permissions:{accessWorkspace:true,view:true}}];
 Object.assign(w,{genericView:w.document.getElementById('genericView'),conversationView:w.document.getElementById('conversationView'),studioFirstName:value=>typeof value==='object'?(value.displayName||value.userName):value,loadWorkspaces:async()=>[],renderWorkspaceMenu(){},studioNavigate:async(view,options)=>navigation.push({view,options}),studioDialog:(title,html)=>{const d=w.document.createElement('dialog');d.innerHTML=html;d.close=()=>d.remove();w.document.body.append(d);return d;},openWorkspaceSettings:async id=>calls.push({settings:id})});
 w.json=async(url,options)=>{calls.push({url,options});if(url==='/api/workspace-access')return workspaces;if(url==='/api/auth/me')return {user:{isAdministrator:admin}};if(url==='/api/jobs')return [{id:'j',workspaceId:'w',title:'Portraits',status:'Active Production'},{id:'done',workspaceId:'w',status:'Completed'},{id:'pending',workspaceId:'unassigned',status:'Pending'}];if(url==='/api/security/roles')return [{id:'wr',name:'Workspace Manager',scope:'Workspace',isActive:true},{id:'jr',name:'Job Owner',scope:'Job',isActive:true}];if(url.endsWith('/managers'))return[{userName:'lisa',displayName:'Lisa Brown'}];if(url.endsWith('/members'))return[];if(url==='/api/production/media-summary')return[{jobId:'j',thumbnailVersionIds:['v']}];throw Error(url);};
 w.eval(read('studio-page.js')+'\n'+read('workspaces.js')+'\n'+read('workspace-management.js'));return {w,calls,navigation};
}
test('Workspaces has accountability cards, real managers and counts, photos and centralized administration',async t=>{
 const {w,calls,navigation}=setup(t);await w.StudioWorkspaceManagement.render();await settle();
 const cards=w.document.querySelectorAll('[data-workspace-card]');assert.equal(cards.length,2);const card=cards[0];assert.match(card.textContent,/Portraits.*Lisa Brown.*1 active jobs/s);assert.match(card.querySelector('img').src,/jobs\/j\/versions\/by-id\/v/);
 assert.equal(w.document.querySelector('.studio-right-rail'),null);assert.equal(w.document.querySelector('#workspaceMemberPanel'),null);card.querySelector('[data-workspace-members]').click();await settle();assert.ok(w.document.querySelector('#workspaceMemberPanel'));assert.match(w.document.querySelector('#workspaceMemberPanel').textContent,/Members & permissions/);
 card.querySelector('[data-workspace-settings]').click();assert.ok(calls.some(call=>call.settings==='w'));card.querySelector('[data-workspace-jobs]').click();assert.deepEqual(JSON.parse(JSON.stringify(navigation.at(-1))),{view:'jobs',options:{workspaceId:'w'}});
 assert.equal(w.document.querySelector('#roleScope').value,'Workspace');assert.match([...w.document.querySelectorAll('#securityRoleList [data-role-name]')].map(input=>input.value).join(', '),/Workspace Manager/);assert.doesNotMatch([...w.document.querySelectorAll('#securityRoleList [data-role-name]')].map(input=>input.value).join(', '),/Job Owner/);
 w.document.querySelector('[data-create-workspace]').click();assert.ok(w.document.querySelector('dialog #workspaceManagerUser[required]'));
});
test('view-only Workspace cards expose jobs without administration actions',async t=>{
 const {w}=setup(t,false);await w.StudioWorkspaceManagement.render();assert.equal(w.document.querySelector('[data-create-workspace],[data-workspace-edit],[data-workspace-members],[data-workspace-mail],#roleScope'),null);assert.equal(w.document.querySelectorAll('[data-workspace-jobs]').length,2);
});
test('Admin menu exposes Workspaces and ends with About',async t=>{
 const {w,navigation}=setup(t);w.fetch=async()=>({ok:true,json:async()=>[]});w.eval(read('studio-admin.js'));w.eval(read('studio-shell-nav.js'));w.document.dispatchEvent(new w.Event('DOMContentLoaded'));await settle();
 assert.equal(w.document.querySelector('[data-studio-avatar-workspaces],[data-studio-profile-management]'),null);
 const menu=w.document.querySelector('.studio-admin-menu');assert.ok(menu);assert.equal(menu.previousElementSibling.className,'studio-navigation-scroll');
 const link=menu.querySelector('[data-studio-admin-view="workspace-management"]');assert.equal(menu.querySelector('div').lastElementChild.textContent,'About');assert.equal(link.nextElementSibling.textContent,'About');menu.open=true;link.click();assert.equal(navigation.at(-1).view,'workspace-management');assert.equal(menu.open,false);
 menu.open=true;menu.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));assert.equal(menu.open,false);
});
test('dedicated Session Plan Import preserves the job in its route and browser history',async t=>{
 const dom=new JSDOM('<body><div id="genericView"></div><div id="conversationView"></div></body>',{url:'https://studio.test/#studio=session-plan-import&job=j',runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());const seen=[];
 w.StudioWorkflowViews={showSessionPlan:async id=>seen.push(id)};w.StudioPage={invalidate(){}};w.eval(read('studio-navigation.js'));w.document.dispatchEvent(new w.Event('DOMContentLoaded'));await settle();assert.equal(seen.at(-1),'j');assert.equal(w.history.state.studioJobId,'j');await w.studioNavigate('session-plan-import',{jobId:'next'});assert.equal(seen.at(-1),'next');assert.match(w.location.hash,/job=next/);
});
test('Create Workspace retains manager assignment and returns to Workspace administration',async t=>{
 const {w,calls,navigation}=setup(t),json=w.json;w.json=async(url,options)=>{if(options?.method==='POST'&&url==='/api/admin/workspaces/'){calls.push({url,options});return {id:'new',name:'New Workspace'};}if(options?.method==='PUT'&&url==='/api/workspaces/new/managers/lisa'){calls.push({url,options});return null;}return json(url,options);};
 await w.StudioWorkspaceManagement.render();w.document.querySelector('[data-create-workspace]').click();const form=w.document.querySelector('#workspaceName').closest('form');form.querySelector('#workspaceName').value='New Workspace';form.querySelector('#workspaceManagerUser').value='lisa';form.querySelector('#workspaceEmail').value='new@studio.test';await form.onsubmit({preventDefault(){},currentTarget:form});
 const creation=calls.find(call=>call.url==='/api/admin/workspaces/');assert.equal(JSON.parse(creation.options.body).defaultJobLead,'lisa');assert.ok(calls.some(call=>call.url==='/api/workspaces/new/managers/lisa'));assert.equal(navigation.at(-1).view,'workspace-management');assert.equal(w.document.querySelector('dialog #workspaceName'),null);
});

test('non-administrator Admin menu exposes only permitted workspace access',async t=>{
 const {w}=setup(t,false);w.studioSignedInUserReady=Promise.resolve({isAdministrator:false});w.eval(read('studio-admin.js'));const host=w.document.createElement('div');w.document.body.append(host);w.StudioAdmin.mountMenu(host);await settle();
 const visible=[...host.querySelectorAll('button')].filter(button=>!button.hidden);assert.equal(visible.length,1);assert.equal(visible[0].textContent,'Workspaces');
});

test('job role create and delete return to Staff and Roles, preserving workspace role destinations',async t=>{
 const {w,navigation}=setup(t);w.confirm=()=>true;w.json=async()=>({});w.fetch=async()=>({ok:true});
 const form=w.document.createElement('div');form.innerHTML='<input id="roleName" value="Editor"><select id="roleScope"><option>Job</option></select><input id="roleDescription" value="Editing"><select id="roleTemplate"><option>worker</option></select>';w.document.body.append(form);
 await w.createStudioRole();assert.equal(navigation.at(-1).view,'staff-roles');w.document.body.dataset.studioView='staff-roles';await w.deleteStudioRole('jr');assert.equal(navigation.at(-1).view,'staff-roles');
 w.document.body.dataset.studioView='workspace-management';await w.deleteStudioRole('wr');assert.equal(navigation.at(-1).view,'workspace-management');
});

test('Admin entries use shared restrained icons and speed-bump rows while icon clicks retain navigation',async t=>{
 const {w,navigation}=setup(t);w.studioSignedInUserReady=Promise.resolve({isAdministrator:true});w.eval(read('studio-admin.js'));const host=w.document.createElement('div');w.document.body.append(host);w.StudioAdmin.mountMenu(host);await settle();
 const buttons=[...host.querySelectorAll('[data-studio-admin-view]')];assert.equal(buttons.length,5);
 for(const button of buttons){assert.ok(button.classList.contains('studio-admin-item'));const svg=button.querySelector('svg');assert.equal(svg.getAttribute('aria-hidden'),'true');assert.equal(svg.getAttribute('focusable'),'false');assert.ok(w.document.querySelector(svg.querySelector('use').getAttribute('href')));}
 const workspace=buttons.find(button=>button.dataset.studioAdminView==='workspace-management');workspace.querySelector('svg').dispatchEvent(new w.MouseEvent('click',{bubbles:true}));assert.equal(navigation.at(-1).view,'workspace-management');
 assert.deepEqual(buttons.map(button=>button.textContent),['Products and Services','Templates','Staff and Roles','Workspaces','About']);
 const shared=read('css/components/studio-rows.css');assert.match(shared,/studio-admin-item.*transition:/);assert.match(shared,/@media\(prefers-reduced-motion:reduce\).*studio-admin-item/);
});

