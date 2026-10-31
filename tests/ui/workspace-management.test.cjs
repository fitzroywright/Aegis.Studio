const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot'),read=name=>fs.readFileSync(path.join(root,name),'utf8'),settle=()=>new Promise(resolve=>setImmediate(resolve));
function setup(t,admin=true){
 const dom=new JSDOM(read('index.html'),{url:'https://studio.test',runScripts:'outside-only',pretendToBeVisual:true}),w=dom.window;t.after(()=>w.close());w.document.body.dataset.studioView='workspace-management';
 w.HTMLElement.prototype.scrollIntoView=function(){};const calls=[],navigation=[];
 const workspaces=[{workspace:{id:'w',name:'Portraits',emailAddress:'portraits@studio.test',isEnabled:true},permissions:{accessWorkspace:true,view:true,manageMembers:admin,editWorkspace:admin}},{workspace:{id:'unassigned',name:'Unassigned',isEnabled:true},permissions:{accessWorkspace:true,view:true}}];
 Object.assign(w,{genericView:w.document.getElementById('genericView'),conversationView:w.document.getElementById('conversationView'),studioFirstName:value=>typeof value==='object'?(value.displayName||value.userName):value,loadWorkspaces:async()=>[],renderWorkspaceMenu(){},studioNavigate:async(view,options)=>navigation.push({view,options}),studioDialog:(title,html)=>{const d=w.document.createElement('dialog');d.innerHTML=html;d.close=()=>d.remove();w.document.body.append(d);return d;},openWorkspaceSettings:async id=>calls.push({settings:id})});
 w.json=async(url,options)=>{calls.push({url,options});if(url==='/api/workspace-access')return workspaces;if(url==='/api/auth/me')return {user:{isAdministrator:admin}};if(url==='/api/jobs')return [{id:'j',workspaceId:'w',title:'Portraits',status:'Active Production'},{id:'done',workspaceId:'w',status:'Completed'},{id:'pending',workspaceId:'unassigned',status:'Pending'}];if(url==='/api/security/roles')return [{id:'wr',name:'Workspace Manager',scope:'Workspace',isActive:true},{id:'jr',name:'Job Owner',scope:'Job',isActive:true}];if(url==='/api/workspaces/w')return {id:'w',name:'Portraits',emailAddress:'portraits@studio.test',isEnabled:true,routeKey:'keep-route',sortOrder:5,defaultJobLead:'lisa'};if(url==='/api/workspaces/w/appearance')return {icon:'camera',color:'#123456'};if(url==='/api/studio-employees?assignable=true')return [{userName:'lisa',displayName:'Lisa Brown'}];if(url.endsWith('/managers'))return[{userName:'lisa',displayName:'Lisa Brown'}];if(url.endsWith('/members'))return[];if(url==='/api/production/media-summary')return[{jobId:'j',thumbnailVersionIds:['v']}];throw Error(url);};
 w.eval(read('studio-page.js')+'\n'+read('workspaces.js')+'\n'+read('workspace-management.js'));return {w,calls,navigation};
}
test('Workspaces cards show values with pencils while permissions and old mail use dialogs',async t=>{
 const {w,navigation}=setup(t);await w.StudioWorkspaceManagement.render();await settle();
 const cards=w.document.querySelectorAll('[data-workspace-card]');assert.equal(cards.length,2);const card=cards[0];assert.equal(card.querySelector('[data-workspace-value="name"]').textContent,'Portraits');assert.match(card.querySelector('[data-workspace-managers]').textContent,/Lisa Brown/);assert.match(card.querySelector('.workspace-active-jobs').textContent,/1 active jobs/);assert.match(card.querySelector('img').src,/jobs\/j\/versions\/by-id\/v/);
 assert.equal(card.querySelector('input:not([type=file]),select,form,[data-workspace-editor]'),null);assert.equal(card.querySelectorAll('[data-workspace-pencil]').length,7);
 card.querySelector('[data-workspace-members]').click();await settle();assert.ok(w.document.querySelector('dialog #workspaceMemberPanel'));w.document.querySelector('#workspaceMemberPanel').closest('dialog').close();
 card.querySelector('[data-workspace-mail]').click();assert.ok(w.document.querySelector('dialog #workspaceImportPanel'));w.document.querySelector('#workspaceImportPanel').closest('dialog').close();
 card.querySelector('[data-workspace-jobs]').click();assert.deepEqual(JSON.parse(JSON.stringify(navigation.at(-1))),{view:'jobs',options:{workspaceId:'w'}});
 assert.equal(w.document.querySelector('#roleScope,#securityRoleList'),null);
 w.document.querySelector('[data-create-workspace]').click();assert.ok(w.document.querySelector('[data-new-workspace] #workspaceManagerUser[required]'));assert.equal(w.document.querySelector('dialog:not([id])'),null);
});
test('view-only Workspace cards expose jobs without administration actions',async t=>{
 const {w}=setup(t,false);await w.StudioWorkspaceManagement.render();assert.equal(w.document.querySelector('[data-create-workspace],[data-workspace-pencil],[data-workspace-members],[data-workspace-mail],#roleScope'),null);assert.equal(w.document.querySelectorAll('[data-workspace-jobs]').length,2);
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

test('non-administrator menu exposes Workspaces and About while hiding privileged pages',async t=>{
 const {w,navigation}=setup(t,false);w.studioSignedInUserReady=Promise.resolve({isAdministrator:false});w.eval(read('studio-admin.js'));const host=w.document.createElement('div');w.document.body.append(host);w.StudioAdmin.mountMenu(host);await settle();
 const visible=[...host.querySelectorAll('button')].filter(button=>!button.hidden);assert.deepEqual(visible.map(button=>button.textContent),['Workspaces','About']);visible[1].click();assert.equal(navigation.at(-1).view,'about');
});

test('job role create and delete return to Staff and Roles, preserving workspace role destinations',async t=>{
 const {w,navigation}=setup(t);w.confirm=()=>true;w.json=async()=>({});w.fetch=async()=>({ok:true});
 const form=w.document.createElement('div');form.innerHTML='<input id="roleName" value="Editor"><select id="roleScope"><option>Job</option></select><input id="roleDescription" value="Editing"><select id="roleTemplate"><option>worker</option></select>';w.document.body.append(form);
 await w.createStudioRole();assert.equal(navigation.at(-1).view,'staff-roles');w.document.body.dataset.studioView='staff-roles';await w.deleteStudioRole('jr');assert.equal(navigation.at(-1).view,'staff-roles');
 w.document.body.dataset.studioView='workspace-management';await w.deleteStudioRole('wr');assert.equal(navigation.at(-1).view,'staff-roles');
});

test('Admin entries use shared restrained icons and speed-bump rows while icon clicks retain navigation',async t=>{
 const {w,navigation}=setup(t);w.studioSignedInUserReady=Promise.resolve({isAdministrator:true});w.eval(read('studio-admin.js'));const host=w.document.createElement('div');w.document.body.append(host);w.StudioAdmin.mountMenu(host);await settle();
 const buttons=[...host.querySelectorAll('[data-studio-admin-view]')];assert.equal(buttons.length,5);
 for(const button of buttons){assert.ok(button.classList.contains('studio-admin-item'));const svg=button.querySelector('svg');assert.equal(svg.getAttribute('aria-hidden'),'true');assert.equal(svg.getAttribute('focusable'),'false');assert.ok(w.document.querySelector(svg.querySelector('use').getAttribute('href')));}
 const workspace=buttons.find(button=>button.dataset.studioAdminView==='workspace-management');workspace.querySelector('svg').dispatchEvent(new w.MouseEvent('click',{bubbles:true}));assert.equal(navigation.at(-1).view,'workspace-management');
 assert.deepEqual(buttons.map(button=>button.textContent),['Products and Services','Templates','Staff and Roles','Workspaces','About']);
 const shared=read('css/components/studio-rows.css');assert.match(shared,/studio-admin-item.*transition:/);assert.match(shared,/@media\(prefers-reduced-motion:reduce\).*studio-admin-item/);
});

function edit(w,card,key,value){const button=card.querySelector('[data-workspace-pencil="'+key+'"]');button.click();const node=card.querySelector('[data-workspace-value="'+key+'"]');if(value!==undefined){const control=node.querySelector('.workspace-appearance-picker');if(control)control.value=value;else node.textContent=value;}return {button,node};}
async function saveField(button){button.click();await settle();await settle();}
test('pencils edit the existing value node and save only that field without a form or textbox',async t=>{
 const {w,calls,navigation}=setup(t),original=w.json;let release;const pending=new Promise(resolve=>release=resolve);
 w.json=async(url,options)=>{if(options?.method==='PUT'){calls.push({url,options});await pending;return {};}return original(url,options);};
 await w.StudioWorkspaceManagement.render();await settle();const card=w.document.querySelector('[data-workspace-card]'),originalNode=card.querySelector('[data-workspace-value="name"]');
 const {node,button}=edit(w,card,'name','New portraits');assert.equal(node,originalNode);assert.equal(node.getAttribute('contenteditable'),'true');assert.equal(card.querySelector('input,textarea,select,form'),null);
 button.click();await settle();button.click();assert.equal(calls.filter(call=>call.options?.method==='PUT').length,1);assert.equal(node.getAttribute('contenteditable'),'false');node.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));assert.equal(node.getAttribute('contenteditable'),'false');release();await settle();await settle();
 const writes=calls.filter(call=>call.options?.method==='PUT'),payload=JSON.parse(writes[0].options.body);assert.equal(writes.length,1);assert.equal(payload.name,'New portraits');assert.equal(payload.routeKey,'keep-route');assert.equal(payload.sortOrder,5);assert.equal(payload.defaultJobLead,'lisa');assert.equal(node.textContent,'New portraits');assert.equal(node.hasAttribute('contenteditable'),false);assert.equal(navigation.length,0);assert.equal(w.document.querySelector('dialog:not([id])'),null);
});
test('field validation and failed saves retain the same value for retry, Escape cancels without writes',async t=>{
 const {w,calls}=setup(t),original=w.json;let fail=true;w.json=async(url,options)=>{if(options?.method==='PUT'){calls.push({url,options});if(fail)throw Error('Save unavailable');return {};}return original(url,options);};
 await w.StudioWorkspaceManagement.render();const card=w.document.querySelector('[data-workspace-card]');const {node,button}=edit(w,card,'emailAddress','invalid');await saveField(button);assert.equal(calls.filter(call=>call.options?.method==='PUT').length,0);assert.match(card.querySelector('[role=status]').textContent,/valid routing email/);
 node.textContent='new@studio.test';await saveField(button);assert.equal(node.textContent,'new@studio.test');assert.equal(node.getAttribute('contenteditable'),'true');assert.match(card.querySelector('[role=status]').textContent,/Save unavailable/);
 fail=false;await saveField(button);assert.equal(node.textContent,'new@studio.test');assert.equal(node.hasAttribute('contenteditable'),false);
 edit(w,card,'emailAddress','discard@studio.test');const count=calls.length;node.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));assert.equal(node.textContent,'new@studio.test');assert.equal(calls.length,count);
});
test('appearance pencils preserve the other appearance setting and independent cards retain drafts',async t=>{
 const {w,calls}=setup(t),original=w.json;w.json=async(url,options)=>{if(options?.method==='PUT'){calls.push({url,options});return {};}return original(url,options);};await w.StudioWorkspaceManagement.render();
 const cards=w.document.querySelectorAll('[data-workspace-card]'),draft=edit(w,cards[1],'name','Unassigned draft');const color=edit(w,cards[0],'color','#654321');await saveField(color.button);
 const write=calls.find(call=>call.options?.method==='PUT');assert.equal(write.url,'/api/workspaces/w/appearance');assert.deepEqual(JSON.parse(write.options.body),{icon:'camera',color:'#654321'});assert.equal(draft.node.textContent,'Unassigned draft');assert.equal(draft.node.getAttribute('contenteditable'),'true');
});
test('manager pencil grants the replacement before changing the previous manager and keeps membership',async t=>{
 const {w,calls}=setup(t),original=w.json;w.json=async(url,options)=>{if(url==='/api/studio-employees?assignable=true')return [{userName:'nicole',displayName:'Nicole'}];if(url.endsWith('/managers')&&!options)return [{userName:'lisa',displayName:'Lisa',permissions:{accessWorkspace:true,view:true,manageSchedule:true,manageProduction:true,manageMembers:true}}];if(options?.method==='PUT'){calls.push({url,options});return {};}return original(url,options);};
 await w.StudioWorkspaceManagement.render();await settle();const card=w.document.querySelector('[data-workspace-card]'),field=edit(w,card,'managers','nicole');await saveField(field.button);const writes=calls.filter(call=>call.options?.method==='PUT');assert.equal(writes[0].url,'/api/workspaces/w/managers/nicole');assert.equal(writes[1].url,'/api/workspaces/w/members/lisa');assert.equal(JSON.parse(writes[1].options.body).isActive,true);assert.equal(JSON.parse(writes[1].options.body).permissions.manageMembers,false);assert.equal(field.node.textContent,'Nicole');
});
test('workspace settings hook focuses the same displayed name with no expanded editor',async t=>{
 const {w}=setup(t);await w.StudioWorkspaceManagement.render();w.eval(read('media-production.js'));await w.openWorkspaceSettings('w');const card=w.document.querySelector('[data-workspace-card="w"]');assert.equal(card.querySelector('[data-workspace-value="name"]').getAttribute('contenteditable'),'true');assert.equal(card.querySelector('form,input,select,[data-workspace-editor]'),null);assert.equal(w.document.querySelector('dialog:not([id])'),null);
});
test('Staff and Roles is the single destination for creating and editing reusable job and workspace roles',async t=>{
 const {w,navigation}=setup(t);await w.StudioWorkspaceManagement.render();assert.equal(w.document.querySelector('#roleScope,#securityRoleList'),null);
 w.genericView.innerHTML=w.renderEmployeeDirectorySection();w.openStaffPermissionTemplates();const scope=w.document.querySelector('#roleScope');assert.deepEqual([...scope.options].map(option=>option.value),['Job','Workspace']);
 const rows=w.document.querySelectorAll('[data-role-row]');assert.equal(rows.length,2);assert.match(w.document.querySelector('#securityRoleList').textContent,/Workspace.*Job/s);
 w.document.querySelector('#roleName').value='Workspace editor';scope.value='Workspace';const original=w.json;w.json=async(url,options)=>options?.method==='POST'?{}:original(url,options);await w.createStudioRole();assert.equal(navigation.at(-1).view,'staff-roles');
});

test('individual member permissions use a popup with the existing permission values',async t=>{
 const {w,calls}=setup(t),original=w.json;
 w.json=async(url,options)=>{if(options?.method==='PUT'){calls.push({url,options});return {};}if(url==='/api/workspaces/w/members')return [{userName:'lisa',displayName:'Lisa',isActive:true,permissions:{accessWorkspace:true,view:true}}];return original(url,options);};
 await w.StudioWorkspaceManagement.render();await w.editWorkspaceMember('w','lisa','Lisa');const form=w.document.querySelector('dialog:not([id]) form');assert.ok(form);assert.equal(form.elements.namedItem('view').checked,true);assert.equal(form.elements.namedItem('editWorkspace').checked,false);form.elements.namedItem('replyRequester').checked=true;await form.onsubmit({preventDefault(){}});const write=calls.find(call=>call.options?.method==='PUT');assert.equal(write.url,'/api/workspaces/w/members/lisa');const payload=JSON.parse(write.options.body);assert.equal(payload.permissions.replyRequester,true);assert.equal(payload.permissions.view,true);assert.equal(payload.permissions.editWorkspace,false);assert.ok(w.document.querySelector('dialog #workspaceMemberPanel'));
});
