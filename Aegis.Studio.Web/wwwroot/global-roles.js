const baseShowWorkspacesForGlobalRoles = showWorkspaces;
showWorkspaces = async function() {
    await baseShowWorkspacesForGlobalRoles();
    try {
        const me = await json('/api/auth/me');
        if (me?.user?.isAdministrator !== true) return;
        const roles = await json('/api/security/global-roles/');
        const frontDesk = roles.find(role => role.name === 'Front Desk');
        if (!frontDesk) return;

        const section = document.createElement('section');
        section.className = 'job-section';
        section.innerHTML = `<h2>Global operational roles</h2><p class="muted">Front Desk is intentionally global: it can see all jobs, reply to clients, notify job/workspace members, view/add client attachments and take payments without gaining workspace or job administration.</p><div id="frontDeskRoleMembers"></div><h3>Add Front Desk user</h3><div class="pos-entry"><label>User name<input id="frontDeskRoleUser" placeholder="domain user name"></label><label>Display name<input id="frontDeskRoleDisplay"></label><button class="primary" onclick="saveFrontDeskRoleMember('${frontDesk.id}')">Add user</button></div>`;
        genericView.appendChild(section);
        await refreshFrontDeskRoleMembers(frontDesk.id);
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
