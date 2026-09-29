const baseShowWorkspacesForGlobalRoles = showWorkspaces;
showWorkspaces = async function() {
    await baseShowWorkspacesForGlobalRoles();
    try {
        const me = await json('/api/auth/me');
        if (me?.user?.isAdministrator !== true) return;
        const roles = await json('/api/security/global-roles/');
        const frontDesk = roles.find(role => role.name === 'Front Desk');
        const operationsManager = roles.find(role => role.name === 'Operations Manager');
        if (!frontDesk && !operationsManager) return;

        const section = document.createElement('section');
        section.className = 'job-section';
        section.innerHTML = `<h2>Global operational roles</h2>
        ${frontDesk ? `<h3>Front Desk</h3><p class="muted">Cross-workspace reception and client service.</p><div id="frontDeskRoleMembers"></div><div class="pos-entry"><label>User name<input id="frontDeskRoleUser" placeholder="domain user name"></label><label>Display name<input id="frontDeskRoleDisplay"></label><button class="primary" onclick="saveFrontDeskRoleMember('${frontDesk.id}')">Add Front Desk user</button></div>` : ''}
        ${operationsManager ? `<h3>Operations Manager</h3><p class="muted">Overall operational escalation role used by Front Desk and cross-workspace escalation.</p><div id="operationsManagerRoleMembers"></div><div class="pos-entry"><label>User name<input id="operationsManagerRoleUser" placeholder="domain user name"></label><label>Display name<input id="operationsManagerRoleDisplay"></label><button class="primary" onclick="saveOperationsManagerRoleMember('${operationsManager.id}')">Assign Operations Manager</button></div>` : ''}`;
        genericView.appendChild(section);
        if(frontDesk) await refreshFrontDeskRoleMembers(frontDesk.id);
        if(operationsManager) await refreshOperationsManagerRoleMembers(operationsManager.id);
    } catch (error) {
        console.warn('Global role administration unavailable', error);
    }
};

async function refreshFrontDeskRoleMembers(roleId) {
    const members = await json(`/api/security/global-roles/${roleId}/members`);
    const target = document.getElementById('frontDeskRoleMembers');
    if (!target) return;
    target.innerHTML = members.length ? members.map(member => `<div class="job-row"><div><b>${esc(studioFirstName(member))}</b><div class="meta">${member.isActive ? 'Active' : 'Inactive'}</div></div><button class="action" onclick="removeFrontDeskRoleMember('${roleId}','${esc(member.userName)}')">Remove</button></div>`).join('') : '<p class="muted">No Front Desk users assigned yet.</p>';
}

async function saveFrontDeskRoleMember(roleId) {
    const userName = document.getElementById('frontDeskRoleUser').value.trim();
    if (!userName) return alert('User name is required.');
    const displayName = document.getElementById('frontDeskRoleDisplay').value.trim() || userName;
    await json(`/api/security/global-roles/${roleId}/members/${encodeURIComponent(userName)}`, {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName,displayName,isActive:true})});
    await refreshFrontDeskRoleMembers(roleId);
}

async function removeFrontDeskRoleMember(roleId, userName) {
    if (!confirm(`Remove ${userName} from Front Desk?`)) return;
    const response = await fetch(`/api/security/global-roles/${roleId}/members/${encodeURIComponent(userName)}`, {method:'DELETE'});
    if (!response.ok) throw new Error(await response.text());
    await refreshFrontDeskRoleMembers(roleId);
}


async function refreshOperationsManagerRoleMembers(roleId) {
    const members = await json(`/api/security/global-roles/${roleId}/members`);
    const target = document.getElementById('operationsManagerRoleMembers');
    if (!target) return;
    target.innerHTML = members.length ? members.map(member => `<div class="job-row"><div><b>${esc(studioFirstName(member))}</b><div class="meta">${member.isActive ? 'Active' : 'Inactive'} · overall escalation</div></div><button class="action" onclick="removeOperationsManagerRoleMember('${roleId}','${esc(member.userName)}')">Remove</button></div>`).join('') : '<p class="muted">No Operations Manager assigned. Front Desk escalation to Operations Manager will be blocked until one is assigned.</p>';
}

async function saveOperationsManagerRoleMember(roleId) {
    const userName = document.getElementById('operationsManagerRoleUser').value.trim();
    if (!userName) return alert('User name is required.');
    const displayName = document.getElementById('operationsManagerRoleDisplay').value.trim() || userName;
    await json(`/api/security/global-roles/${roleId}/members/${encodeURIComponent(userName)}`, {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName,displayName,isActive:true})});
    await refreshOperationsManagerRoleMembers(roleId);
}

async function removeOperationsManagerRoleMember(roleId, userName) {
    if (!confirm(`Remove ${userName} as Operations Manager?`)) return;
    const response = await fetch(`/api/security/global-roles/${roleId}/members/${encodeURIComponent(userName)}`, {method:'DELETE'});
    if (!response.ok) throw new Error(await response.text());
    await refreshOperationsManagerRoleMembers(roleId);
}
