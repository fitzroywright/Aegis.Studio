let workspaceAccessCache = [];

async function showWorkspaces() {
    activateNavigation('workspaces');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');

    try {
        const me = await json('/api/auth/me');
        const isAdministrator = me?.user?.isAdministrator === true;
        workspaceAccessCache = await json('/api/workspace-access');
        const manageable = workspaceAccessCache.filter(item => isAdministrator || item.permissions.editWorkspace || item.permissions.manageMembers || item.permissions.adminWorkspace);

        genericView.innerHTML = `
            <div class="job-detail-header"><div><h1>Workspaces</h1><p>Workspace access is membership based. Permissions control what each member can see and do.</p></div></div>
            <section class="job-section"><h2>Your workspaces</h2>${manageable.length ? manageable.map(item => renderWorkspaceAdminRow(item.workspace, item.permissions, isAdministrator)).join('') : '<p class="muted">You do not administer any workspaces.</p>'}</section>
            ${isAdministrator ? `<section class="job-section"><h2>Create workspace</h2><p class="muted">Routing uses the exact email address or route key. Membership can be assigned immediately after creation.</p><div class="pos-entry"><label>Name<input id="workspaceName"></label><label>Email address<input id="workspaceEmail" type="email"></label><label>Route key<input id="workspaceRouteKey"></label><button class="primary" onclick="createWorkspace()">Create workspace</button></div></section>` : ''}
            <section id="workspaceMemberPanel" class="job-section hidden"></section>`;
    } catch (error) {
        genericView.innerHTML = `<h1>Workspaces</h1><div class="empty-state" style="text-align:left"><h2>Unable to load workspace administration</h2><p>${esc(error.message || error)}</p></div>`;
    }
}

function renderWorkspaceAdminRow(workspace, permissions, isAdministrator) {
    const canEdit = isAdministrator || permissions.editWorkspace || permissions.adminWorkspace;
    const canMembers = isAdministrator || permissions.manageMembers || permissions.adminWorkspace;
    return `<div class="job-row"><div><b>${esc(workspace.name)}</b><div class="meta">${esc(workspace.emailAddress || 'No email')} · route ${esc(workspace.routeKey || 'none')} · ${workspace.isEnabled ? 'Enabled' : 'Disabled'}</div></div><div>${canMembers ? `<button class="action" onclick="manageWorkspaceMembers('${workspace.id}','${esc(workspace.name)}')">Members & permissions</button>` : ''}${canEdit ? `<button class="action" onclick="editWorkspace('${workspace.id}')">Edit</button>` : ''}</div></div>`;
}

async function createWorkspace() {
    const name = document.getElementById('workspaceName').value.trim();
    if (!name) return alert('Workspace name is required.');
    const workspace = await json('/api/admin/workspaces/', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ name, emailAddress:document.getElementById('workspaceEmail').value.trim()||null, routeKey:document.getElementById('workspaceRouteKey').value.trim()||null, isEnabled:true, sortOrder:0 }) });
    await loadWorkspaces(); await showWorkspaces(); await manageWorkspaceMembers(workspace.id, workspace.name);
}

async function editWorkspace(id) {
    const current = await json('/api/workspaces/' + id);
    const name = prompt('Workspace name', current.name); if (!name) return;
    const emailAddress = prompt('Workspace email address', current.emailAddress || ''); if (emailAddress === null) return;
    const routeKey = prompt('Exact routing key', current.routeKey || ''); if (routeKey === null) return;
    const enabledText = prompt('Enabled? yes/no', current.isEnabled ? 'yes' : 'no'); if (enabledText === null) return;
    await json('/api/workspaces/' + id, { method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ name:name.trim(), emailAddress:emailAddress.trim()||null, routeKey:routeKey.trim()||null, isEnabled:!['no','false','0'].includes(enabledText.trim().toLowerCase()), sortOrder:current.sortOrder||0 }) });
    await loadWorkspaces(); await showWorkspaces();
}

const permissionLabels = [
    ['accessWorkspace','Access workspace'],['view','View work'],['replyRequester','Reply to requester'],['viewAttachments','View attachments'],['addAttachments','Add attachments'],['createJob','Create jobs'],['editJob','Edit jobs'],['manageSchedule','Manage schedule'],['manageProduction','Manage production'],['notifyMembers','Notify members'],['editWorkspace','Edit workspace'],['manageMembers','Manage members'],['adminWorkspace','Workspace administrator']
];

function permissionsForPreset(preset) {
    const none = Object.fromEntries(permissionLabels.map(([key]) => [key,false]));
    if (preset === 'view') return {...none,accessWorkspace:true,view:true,viewAttachments:true};
    if (preset === 'reply') return {...none,accessWorkspace:true,view:true,replyRequester:true,viewAttachments:true,addAttachments:true,createJob:true};
    if (preset === 'editor') return {...none,accessWorkspace:true,view:true,replyRequester:true,viewAttachments:true,addAttachments:true,createJob:true,editJob:true,manageSchedule:true,manageProduction:true,notifyMembers:true};
    return Object.fromEntries(permissionLabels.map(([key]) => [key,true]));
}

async function manageWorkspaceMembers(workspaceId, workspaceName) {
    const panel = document.getElementById('workspaceMemberPanel');
    const members = await json(`/api/workspaces/${workspaceId}/members`);
    panel.classList.remove('hidden');
    panel.innerHTML = `<h2>${esc(workspaceName)} — Members & permissions</h2><p class="muted">Only active members with Access workspace can enter this workspace. Permissions are independent so read-only and specialist roles are possible.</p><div>${members.length ? members.map(member => renderMember(workspaceId, member)).join('') : '<p class="muted">No members yet.</p>'}</div><h3>Add or update member</h3><div class="pos-entry"><label>User name<input id="memberUserName" placeholder="domain user name"></label><label>Display name<input id="memberDisplayName"></label><label>Preset<select id="memberPreset"><option value="view">View only</option><option value="reply">Request handler</option><option value="editor">Workspace editor</option><option value="admin">Workspace admin</option></select></label><button class="primary" onclick="saveWorkspaceMember('${workspaceId}','${esc(workspaceName)}')">Save member</button></div>`;
    panel.scrollIntoView({behavior:'smooth',block:'start'});
}

function renderMember(workspaceId, member) {
    const permissions = permissionLabels.filter(([key]) => member.permissions[key]).map(([,label]) => label).join(', ');
    return `<div class="job-row"><div><b>${esc(member.displayName)}</b><div class="meta">${esc(member.userName)} · ${member.isActive ? 'Active' : 'Inactive'}</div><div class="meta">${esc(permissions || 'No permissions')}</div></div><div><button class="action" onclick="notifyWorkspaceMember('${workspaceId}','${esc(member.userName)}','${esc(member.displayName)}')">Notify</button><button class="action" onclick="editWorkspaceMember('${workspaceId}','${esc(member.userName)}','${esc(member.displayName)}')">Permissions</button><button class="action" onclick="removeWorkspaceMember('${workspaceId}','${esc(member.userName)}')">Remove</button></div></div>`;
}

async function saveWorkspaceMember(workspaceId, workspaceName) {
    const userName = document.getElementById('memberUserName').value.trim(); if (!userName) return alert('User name is required.');
    const displayName = document.getElementById('memberDisplayName').value.trim() || userName;
    await putWorkspaceMember(workspaceId,userName,displayName,permissionsForPreset(document.getElementById('memberPreset').value));
    await manageWorkspaceMembers(workspaceId,workspaceName);
}

async function editWorkspaceMember(workspaceId, userName, displayName) {
    const members = await json(`/api/workspaces/${workspaceId}/members`);
    const member = members.find(x => x.userName.toLowerCase() === userName.toLowerCase()); if (!member) return;
    const selected = permissionLabels.map(([key,label]) => `${label}: ${member.permissions[key] ? 'yes' : 'no'}`).join('\n');
    const preset = prompt(`Current permissions:\n${selected}\n\nSet preset: view, reply, editor, admin`, member.permissions.adminWorkspace ? 'admin' : member.permissions.editJob ? 'editor' : member.permissions.replyRequester ? 'reply' : 'view');
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

const workspaceNavigationButton = document.querySelector('.brand-button[data-view="workspaces"]');
if (workspaceNavigationButton) workspaceNavigationButton.onclick = () => showWorkspaces();
