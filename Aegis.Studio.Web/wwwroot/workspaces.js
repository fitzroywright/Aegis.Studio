let workspaceAccessCache = [];
let studioRolesCache = [];

async function showWorkspaces(){return window.studioNavigate('admin');}

function renderWorkspaceAdminRow(workspace, permissions, isAdministrator) {
    const canEdit = isAdministrator || permissions.editWorkspace || permissions.adminWorkspace;
    const canMembers = isAdministrator || permissions.manageMembers || permissions.adminWorkspace;
    return `<div class="job-row"><div><b>${esc(workspace.name)}</b><div class="meta">${esc(workspace.emailAddress || 'No email')} · ${workspace.isEnabled ? 'Enabled' : 'Disabled'}</div><div class="meta" data-workspace-managers="${workspace.id}">Manager: loading…</div></div><div>${canMembers ? `<button class="action" onclick="manageWorkspaceMembers('${workspace.id}','${esc(workspace.name)}')">Members & permissions</button>` : ''}${canEdit ? `<button class="action" onclick="showMailboxImport('${workspace.id}','${esc(workspace.name)}')">Import old email</button><button class="action" onclick="editWorkspace('${workspace.id}')">Edit</button>` : ''}</div></div>`;
}

async function createWorkspace() {
    const name = document.getElementById('workspaceName').value.trim();
    const managerUser=document.getElementById('workspaceManagerUser').value.trim();
    const managerDisplay=document.getElementById('workspaceManagerDisplay').value.trim()||managerUser;
    if (!name) return alert('Workspace name is required.');
    if (!managerUser) return alert('A Workspace Manager user name is required.');
    const workspace = await json('/api/admin/workspaces/', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ name, emailAddress:document.getElementById('workspaceEmail').value.trim()||null, routeKey:document.getElementById('workspaceRouteKey')?.value.trim()||null, isEnabled:true, sortOrder:0, defaultJobLead:document.getElementById('workspaceDefaultLead')?.value||managerUser }) });
    try{
        await json('/api/workspaces/'+workspace.id+'/managers/'+encodeURIComponent(managerUser),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName:managerUser,displayName:managerDisplay,isActive:true,permissions:null})});
    }catch(error){
        alert('Workspace was created, but its manager could not be assigned: '+error.message+' Assign the manager before using this workspace.');
    }
    await loadWorkspaces(); await showWorkspaces(); await manageWorkspaceMembers(workspace.id, workspace.name);
}

async function hydrateWorkspaceManagers(items){
    await Promise.all(items.map(async item=>{
        const target=document.querySelector('[data-workspace-managers="'+item.workspace.id+'"]');if(!target)return;
        try{
            const managers=await json('/api/workspaces/'+item.workspace.id+'/managers');
            target.textContent=managers.length?'Manager: '+managers.map(m=>studioFirstName(m)).join(', '):'Manager required';
            target.classList.toggle('risk',!managers.length);
        }catch{target.textContent='Manager status unavailable';}
    }));
}

async function editWorkspace(id) {
    const [current,employees] = await Promise.all([json('/api/workspaces/' + id),json('/api/studio-employees?assignable=true')]);
    const leadOptions='<option value="">No default Job Lead</option>'+employees.map(employee=>`<option value="${esc(employee.userName)}" ${String(current.defaultJobLead||'').toLowerCase()===String(employee.userName||'').toLowerCase()?'selected':''}>${esc(employee.displayName)}${employee.roles?.length?' · '+esc(employee.roles.join(', ')):''}</option>`).join('');
    const dialog=studioDialog('Edit Workspace',`<form class="request-form"><label>Name<input name="name" required value="${esc(current.name)}"></label><label>Email address<input name="email" type="email" value="${esc(current.emailAddress||'')}"></label><label>Default Job Lead<select name="defaultJobLead">${leadOptions}</select></label><label>Enabled<select name="enabled"><option value="true" ${current.isEnabled?'selected':''}>Yes</option><option value="false" ${!current.isEnabled?'selected':''}>No</option></select></label><details><summary>Advanced / Integration Settings</summary><label>Route key<input name="routeKey" value="${esc(current.routeKey||'')}" placeholder="Generated automatically"></label><small>Used by website booking and incoming-message integrations. Leaving blank keeps the current key or generates one if missing.</small></details><p role="alert"></p><div class="dialog-actions"><button type="button" data-cancel>Cancel</button><button class="primary" type="submit">Save</button></div></form>`);
    dialog.querySelector('[data-cancel]').onclick=()=>dialog.close();
    const form=dialog.querySelector('form');
    form.onsubmit=async event=>{
        event.preventDefault();const button=form.querySelector('[type=submit]');button.disabled=true;
        try{
            await json('/api/workspaces/'+id,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:form.elements.name.value.trim(),emailAddress:form.elements.email.value.trim()||null,routeKey:form.elements.routeKey.value.trim()||null,isEnabled:form.elements.enabled.value==='true',sortOrder:current.sortOrder||0,defaultJobLead:form.elements.defaultJobLead.value||null})});
            dialog.close();await loadWorkspaces();await showWorkspaces();
        }catch(error){form.querySelector('[role=alert]').textContent=error.message;button.disabled=false;}
    };
}

const permissionLabels = [
    ['accessWorkspace','Access workspace'],['view','View work'],['replyRequester','Reply to requester'],['viewAttachments','View attachments'],['addAttachments','Add attachments'],['createJob','Create jobs'],['editJob','Edit jobs'],['manageSchedule','Manage schedule'],['manageProduction','Manage production'],['notifyMembers','Notify members'],['editWorkspace','Edit workspace'],['manageMembers','Manage members'],['sendAsSelf','Reply as Myself'],['adminWorkspace','Workspace administrator']
];

function permissionsForPreset(preset) {
    const none = Object.fromEntries(permissionLabels.map(([key]) => [key,false]));
    if (preset === 'view') return {...none,accessWorkspace:true,view:true,viewAttachments:true};
    if (preset === 'reply') return {...none,accessWorkspace:true,view:true,replyRequester:true,viewAttachments:true,addAttachments:true,createJob:true};
    if (preset === 'editor') return {...none,accessWorkspace:true,view:true,replyRequester:true,viewAttachments:true,addAttachments:true,createJob:true,editJob:true,manageSchedule:true,manageProduction:true,notifyMembers:true,sendAsSelf:true};
    if (preset === 'manager') return {...none,accessWorkspace:true,view:true,replyRequester:true,viewAttachments:true,addAttachments:true,createJob:true,editJob:true,manageSchedule:true,manageProduction:true,notifyMembers:true,editWorkspace:true,manageMembers:true,sendAsSelf:true};
    return Object.fromEntries(permissionLabels.map(([key]) => [key,true]));
}

async function manageWorkspaceMembers(workspaceId, workspaceName) {
    const panel = document.getElementById('workspaceMemberPanel');
    const members = await json(`/api/workspaces/${workspaceId}/members`);
    const roleOptions = studioRolesCache.filter(x => x.scope === 'Workspace' && x.isActive).map(x => `<option value="${x.id}">${esc(x.name)}</option>`).join('');
    panel.classList.remove('hidden');
    panel.innerHTML = `<h2>${esc(workspaceName)} — Members & permissions</h2><p class="muted">Assign a reusable role or a one-off permission preset.</p><div>${members.length ? members.map(member => renderMember(workspaceId, member)).join('') : '<p class="muted">No members yet.</p>'}</div><h3>Add or update member</h3><div class="pos-entry"><label>User name<input id="memberUserName" placeholder="domain user name"></label><label>Display name<input id="memberDisplayName"></label><label>Preset<select id="memberPreset"><option value="view">View only</option><option value="reply">Request handler</option><option value="editor">Workspace editor</option><option value="manager">Workspace Manager</option><option value="admin">Workspace admin</option></select></label><button class="primary" onclick="saveWorkspaceMember('${workspaceId}','${esc(workspaceName)}')">Save member</button></div>${roleOptions ? `<h3>Assign reusable role</h3><div class="pos-entry"><label>User name<input id="roleMemberUser"></label><label>Role<select id="workspaceRoleSelect">${roleOptions}</select></label><button class="primary" onclick="assignWorkspaceRole('${workspaceId}','${esc(workspaceName)}')">Assign role</button></div>` : ''}`;
    panel.scrollIntoView({behavior:'smooth',block:'start'});
}

function renderMember(workspaceId, member) {
    const permissions = permissionLabels.filter(([key]) => member.permissions[key]).map(([,label]) => label).join(', ');
    return `<div class="job-row"><div><b>${esc(studioFirstName(member))}</b><div class="meta">${member.isActive ? 'Active' : 'Inactive'}</div><div class="meta">${esc(permissions || 'No permissions')}</div></div><div><button class="action" onclick="notifyWorkspaceMember('${workspaceId}','${esc(member.userName)}','${esc(studioFirstName(member))}')">Notify</button><button class="action" onclick="editWorkspaceMember('${workspaceId}','${esc(member.userName)}','${esc(studioFirstName(member))}')">Permissions</button><button class="action" onclick="removeWorkspaceMember('${workspaceId}','${esc(member.userName)}')">Remove</button></div></div>`;
}

async function saveWorkspaceMember(workspaceId, workspaceName) {
    const userName = document.getElementById('memberUserName').value.trim(); if (!userName) return alert('User name is required.');
    const displayName = document.getElementById('memberDisplayName').value.trim() || userName;
    await putWorkspaceMember(workspaceId,userName,displayName,permissionsForPreset(document.getElementById('memberPreset').value));
    await manageWorkspaceMembers(workspaceId,workspaceName);
}

async function assignWorkspaceRole(workspaceId, workspaceName) {
    const userName = document.getElementById('roleMemberUser').value.trim(); if (!userName) return alert('User name is required.');
    const roleId = document.getElementById('workspaceRoleSelect').value;
    await json(`/api/security/workspaces/${workspaceId}/members/${encodeURIComponent(userName)}/role/${roleId}`, {method:'PUT'});
    await manageWorkspaceMembers(workspaceId,workspaceName);
}

async function editWorkspaceMember(workspaceId, userName, displayName) {
    const members = await json(`/api/workspaces/${workspaceId}/members`);
    const member = members.find(x => x.userName.toLowerCase() === userName.toLowerCase()); if (!member) return;
    const selected = permissionLabels.map(([key,label]) => `${label}: ${member.permissions[key] ? 'yes' : 'no'}`).join('\n');
    const preset = prompt(`Current permissions:\n${selected}\n\nSet preset: view, reply, editor, manager, admin`, member.permissions.adminWorkspace ? 'admin' : member.permissions.manageMembers ? 'manager' : member.permissions.editJob ? 'editor' : member.permissions.replyRequester ? 'reply' : 'view');
    if (!preset) return;
    await putWorkspaceMember(workspaceId,userName,displayName,permissionsForPreset(preset.toLowerCase()));
    const item = workspaceAccessCache.find(x => x.workspace.id === workspaceId); await manageWorkspaceMembers(workspaceId,item?.workspace?.name || 'Workspace');
}

async function putWorkspaceMember(workspaceId,userName,displayName,permissions) {
    await json(`/api/workspaces/${workspaceId}/members/${encodeURIComponent(userName)}`, { method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ userName,displayName,isActive:true,permissions }) });
}

async function removeWorkspaceMember(workspaceId,userName) {
    if (!confirm(`Remove ${userName} from this workspace?`)) return;
    const response = await fetch(`/api/workspaces/${workspaceId}/members/${encodeURIComponent(userName)}`, {method:'DELETE'}); if (!response.ok) throw new Error(await response.text());
    const item = workspaceAccessCache.find(x => x.workspace.id === workspaceId); await manageWorkspaceMembers(workspaceId,item?.workspace?.name || 'Workspace');
}

async function notifyWorkspaceMember(workspaceId,userName,displayName) {
    const subject = prompt(`Notification subject for ${displayName}`, 'Workspace notification'); if (!subject) return;
    const body = prompt('Message'); if (!body) return;
    await json(`/api/workspaces/${workspaceId}/notifications`, { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({userName,subject,body}) });
    alert(`Notification queued for ${displayName}. Their messaging preferences determine which enabled channels receive it.`);
}

function showMailboxImport(workspaceId, workspaceName) {
    const panel = document.getElementById('workspaceImportPanel'); panel.classList.remove('hidden');
    panel.innerHTML = `<h2>${esc(workspaceName)} — Import historical mailbox</h2><p class="muted">Imports messages and attachments directly into this workspace. Gmail, Yahoo and Hotmail/Outlook normally require an app password or provider token rather than the user's normal password.</p><div class="pos-entry"><label>Provider<select id="importProvider"><option>Gmail</option><option>Hotmail</option><option>Yahoo</option></select></label><label>Mailbox address<input id="importUser" type="email"></label><label>App password / token<input id="importSecret" type="password"></label><label>Folder<input id="importFolder" value="INBOX"></label><label>From date<input id="importFrom" type="date"></label><label>Maximum messages<input id="importMax" type="number" value="5000" min="1" max="50000"></label><button class="primary" onclick="runMailboxImport('${workspaceId}')">Import mailbox</button></div><div id="importResult" class="meta"></div>`;
    panel.scrollIntoView({behavior:'smooth',block:'start'});
}

async function runMailboxImport(workspaceId) {
    const result = document.getElementById('importResult'); result.textContent = 'Importing… this can take a while for a large mailbox.';
    try {
        const fromValue = document.getElementById('importFrom').value;
        const data = await json(`/api/workspaces/${workspaceId}/email-import`, {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({provider:document.getElementById('importProvider').value,userName:document.getElementById('importUser').value.trim(),secret:document.getElementById('importSecret').value,folder:document.getElementById('importFolder').value.trim()||null,fromUtc:fromValue ? new Date(fromValue).toISOString() : null,toUtc:null,maximumMessages:Number(document.getElementById('importMax').value)||5000})});
        result.textContent = `Imported ${data.importedMessages} messages and ${data.importedAttachments} attachments. ${data.duplicateMessages} duplicates skipped; ${data.failedMessages} failed.`;
    } catch (error) { result.textContent = error.message || error; }
}

function renderEmployeeDirectorySection() {
    return `<section class="job-section"><h2>Employees & Roles</h2><p class="muted">Studio's operational employee directory. Directory providers populate identity; Studio controls whether a person is active and assignable.</p><div id="studioEmployeeList"><p class="muted">Loading…</p></div><h3>Add or maintain employee</h3><div class="pos-entry"><label>User name<input id="employeeUserName"></label><label>Display name<input id="employeeDisplayName"></label><label>Email<input id="employeeEmail" type="email"></label><label>Assignable<select id="employeeAssignable"><option value="true">Yes</option><option value="false">No</option></select></label><button class="primary" onclick="saveStudioEmployee()">Save employee</button></div></section>`;
}
async function refreshStudioEmployees(){
    const holder=document.getElementById('studioEmployeeList');if(!holder)return;
    const employees=await json('/api/admin/studio-employees/');
    holder.innerHTML=employees.map(e=>`<div class="job-row"><div><b>${esc(e.displayName)}</b><div class="meta">${esc(e.userName)} · ${e.isActive?'Active':'Inactive'} · ${e.isAssignable?'Assignable':'Not assignable'}</div><div class="meta">${esc((e.roles||[]).join(', ')||e.source||'Studio')}</div></div><button class="action" data-edit-employee="${esc(e.userName)}">Edit</button></div>`).join('')||'<p class="muted">No employees yet.</p>';
    holder.querySelectorAll('[data-edit-employee]').forEach(button=>button.onclick=()=>{
        const employee=employees.find(e=>String(e.userName).toLowerCase()===String(button.dataset.editEmployee).toLowerCase());if(!employee)return;
        document.getElementById('employeeUserName').value=employee.userName||'';
        document.getElementById('employeeDisplayName').value=employee.displayName||'';
        document.getElementById('employeeEmail').value=employee.emailAddress||'';
        document.getElementById('employeeAssignable').value=String(employee.isAssignable!==false);
    });
}
async function saveStudioEmployee(){
    const userName=document.getElementById('employeeUserName').value.trim();if(!userName)return alert('User name is required.');
    const existing=(await json('/api/admin/studio-employees/')).find(e=>String(e.userName).toLowerCase()===userName.toLowerCase());
    const payload={userName,displayName:document.getElementById('employeeDisplayName').value.trim()||userName,emailAddress:document.getElementById('employeeEmail').value.trim()||null,isActive:existing?.isActive!==false,isAssignable:document.getElementById('employeeAssignable').value==='true',source:existing?.source||'Manual',externalId:existing?.externalId||null,skills:existing?.skills||[],workDayUserName:existing?.workDayUserName||null,lastSyncedAtUtc:existing?.lastSyncedAtUtc||null};
    await json('/api/admin/studio-employees/'+encodeURIComponent(userName),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
    await refreshStudioEmployees();
}

function renderSecurityRolesSection() {
    return `<section class="job-section"><h2>Reusable security roles</h2><p class="muted">Roles can be reused across Workspaces and Jobs. Updating a role propagates its permissions to members assigned that role.</p><div id="securityRoleList">${studioRolesCache.map(x=>`<div class="job-row"><div><b>${esc(x.name)}</b><div class="meta">${esc(x.scope)} · ${esc(x.description||'')}</div></div><button class="action" onclick="deleteStudioRole('${x.id}')">Delete</button></div>`).join('')||'<p class="muted">No reusable roles yet.</p>'}</div><h3>Create role</h3><div class="pos-entry"><label>Name<input id="roleName"></label><label>Scope<select id="roleScope"><option>Workspace</option><option>Job</option></select></label><label>Description<input id="roleDescription"></label><label>Template<select id="roleTemplate"><option value="viewer">Viewer</option><option value="worker">Worker</option><option value="manager">Manager</option><option value="admin">Administrator</option></select></label><button class="primary" onclick="createStudioRole()">Create role</button></div></section>`;
}

function roleTemplate(scope, template) {
    if (scope === 'Workspace') {
        if (template === 'viewer') return permissionsForPreset('view'); if (template === 'worker') return permissionsForPreset('reply'); if (template === 'manager') return permissionsForPreset('editor'); return permissionsForPreset('admin');
    }
    const keys=['accessJob','viewJob','viewConversation','replyRequester','viewAttachments','addAttachments','editJob','changeStatus','assignStaff','manageSchedule','manageProduction','viewFinancials','recordFinancialActivity','usePos','viewPosTransactions','refundVoidPos','notifyJobMembers','manageJobMembers','adminJob'];
    const p=Object.fromEntries(keys.map(k=>[k,false])); p.accessJob=p.viewJob=p.viewConversation=p.viewAttachments=true;
    if(template!=='viewer'){p.addAttachments=p.manageSchedule=p.manageProduction=p.notifyJobMembers=true;}
    if(template==='manager'||template==='admin'){Object.assign(p,{replyRequester:true,editJob:true,changeStatus:true,assignStaff:true,viewFinancials:true,recordFinancialActivity:true,usePos:true,viewPosTransactions:true,manageJobMembers:true});}
    if(template==='admin') Object.keys(p).forEach(k=>p[k]=true); return p;
}

async function createStudioRole() {
    const name=document.getElementById('roleName').value.trim(); if(!name)return alert('Role name is required.'); const scope=document.getElementById('roleScope').value; const permissions=roleTemplate(scope,document.getElementById('roleTemplate').value);
    await json('/api/security/roles',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name,scope,description:document.getElementById('roleDescription').value.trim()||null,permissionsJson:JSON.stringify(permissions),isActive:true})}); await showWorkspaces();
}
async function deleteStudioRole(id){if(!confirm('Delete this reusable role? Existing members keep their copied permissions but lose the role link.'))return;const r=await fetch('/api/security/roles/'+id,{method:'DELETE'});if(!r.ok)throw new Error(await r.text());await showWorkspaces();}

function renderWorkflowSection() {
    return `<section class="job-section"><h2>Workflow automation</h2><p class="muted">Database-backed event/condition/action workflows. Supported triggers include JobStatusChanged, ScheduleChanged, ProductionChanged and DeliveryChanged. Actions: coordination CreateActivity, NotifyJob, NotifyRequester and WaitMinutes. Job status, ownership, production dispatch and scheduling require authorized Job workflow decisions.</p><div id="workflowList"><p class="muted">Loading…</p></div><h3>Create workflow</h3><div class="pos-entry"><label>Name<input id="workflowName"></label><label>Trigger<select id="workflowTrigger"><option>JobStatusChanged</option><option>ScheduleChanged</option><option>ProductionChanged</option><option>DeliveryChanged</option></select></label><label>Job status condition<input id="workflowStatusCondition" placeholder="optional, e.g. Shoot Complete"></label></div><label style="display:block;margin-top:10px">Actions JSON<textarea id="workflowActions" rows="8" style="width:100%">[{"type":"CreateActivity","activityType":"Other","title":"Review backup verification","dueMinutes":120},{"type":"NotifyJob","subject":"Workflow started","body":"Automated production workflow started."}]</textarea></label><button class="primary" onclick="createWorkflowDefinition()">Create workflow</button><h3>Authorization audit</h3><button class="action" onclick="runAuthorizationAudit()">Run endpoint audit</button><div id="authorizationAuditResult" class="meta"></div></section>`;
}

async function refreshWorkflowList(){const holder=document.getElementById('workflowList');if(!holder)return;const workflows=await json('/api/workflows/');holder.innerHTML=workflows.map(x=>`<div class="job-row"><div><b>${esc(x.name)}</b><div class="meta">When ${esc(x.triggerEvent)} · ${x.isEnabled?'Enabled':'Disabled'}</div></div><button class="action" onclick="deleteWorkflowDefinition('${x.id}')">Delete</button></div>`).join('')||'<p class="muted">No workflow definitions yet.</p>';}
async function createWorkflowDefinition(){const name=document.getElementById('workflowName').value.trim();if(!name)return alert('Workflow name is required.');const status=document.getElementById('workflowStatusCondition').value.trim();await json('/api/workflows/',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name,description:null,triggerEvent:document.getElementById('workflowTrigger').value,conditionsJson:JSON.stringify(status?{jobStatus:status}:{}),actionsJson:document.getElementById('workflowActions').value,isEnabled:true})});await refreshWorkflowList();}
async function deleteWorkflowDefinition(id){if(!confirm('Delete workflow?'))return;const r=await fetch('/api/workflows/'+id,{method:'DELETE'});if(!r.ok)throw new Error(await r.text());await refreshWorkflowList();}
async function runAuthorizationAudit(){const report=await json('/api/security/authorization-audit');document.getElementById('authorizationAuditResult').textContent=`Checked ${report.apiEndpointCount} API endpoints. Unapproved anonymous: ${report.unapprovedAnonymous.length ? report.unapprovedAnonymous.join(', ') : 'none'}.`;}

const workspaceNavigationButton = document.querySelector('.brand-button[data-view="workspaces"]');
if (workspaceNavigationButton) workspaceNavigationButton.onclick = () => showWorkspaces();

async function seedStudioDemoData(button) {
    if (!confirm('Load or upgrade the DEMO workspaces with gallery illustrations, versions, subjects, appointments and scheduled tasks? Existing demo data will be kept and duplicates skipped. The initial demo includes fictional deposits in account totals. Use a demo database.')) return;
    button.disabled = true;
    try {
        const result = await json('/api/admin/demo/seed', { method: 'POST' });
        await loadWorkspaces();
        await showWorkspaces();
        alert(result.message);
    } catch (error) {
        alert('Unable to load demo data: ' + (error.message || error));
    } finally {
        button.disabled = false;
    }
}

