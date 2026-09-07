const baseShowManagement = showManagement;
showManagement = async function() {
    await baseShowManagement();
    try {
        const me = await json('/api/auth/me');
        if (!me.isAdministrator) return;
        const workspaces = await json('/api/admin/workspaces/');
        const section = document.createElement('section');
        section.className = 'job-section';
        section.innerHTML = `
            <h2>Workspace administration</h2>
            <p class="muted">Workspace names, email identities and route keys are stored in PostgreSQL. Email and booking routing use these exact values; no keyword guessing is performed.</p>
            <div id="workspaceAdminRows">${renderWorkspaceAdminRows(workspaces)}</div>
            <h3 style="margin-top:20px">Add workspace</h3>
            <div class="pos-entry">
                <label>Name<input id="workspaceName"></label>
                <label>Email<input id="workspaceEmail"></label>
                <label>Route key<input id="workspaceRouteKey"></label>
                <button class="primary" onclick="createWorkspace()">Add</button>
            </div>`;
        genericView.appendChild(section);
    } catch (error) {
        // Non-administrators or temporarily unavailable administration data do not block management metrics.
    }
};

function renderWorkspaceAdminRows(workspaces) {
    if (!workspaces.length) return '<p class="muted">No workspaces configured.</p>';
    return workspaces.map(workspace => `
        <div class="job-row">
            <div>
                <b>${esc(workspace.name)}</b>
                <div class="meta">${esc(workspace.emailAddress || 'No email')} · route ${esc(workspace.routeKey || 'none')} · ${workspace.isEnabled ? 'Enabled' : 'Disabled'}</div>
            </div>
            <button class="action" onclick="editWorkspace('${workspace.id}')">Edit</button>
        </div>`).join('');
}

async function createWorkspace() {
    const name = document.getElementById('workspaceName').value.trim();
    if (!name) return alert('Workspace name is required.');
    await json('/api/admin/workspaces/', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            name,
            emailAddress: document.getElementById('workspaceEmail').value.trim() || null,
            routeKey: document.getElementById('workspaceRouteKey').value.trim() || null,
            isEnabled: true,
            sortOrder: 0
        })
    });
    await loadWorkspaces();
    await showManagement();
}

async function editWorkspace(id) {
    const workspaces = await json('/api/admin/workspaces/');
    const current = workspaces.find(workspace => workspace.id === id);
    if (!current) return;
    const name = prompt('Workspace name', current.name);
    if (!name) return;
    const emailAddress = prompt('Workspace email address', current.emailAddress || '') ?? current.emailAddress;
    const routeKey = prompt('Exact routing key', current.routeKey || '') ?? current.routeKey;
    const enabledText = prompt('Enabled? yes/no', current.isEnabled ? 'yes' : 'no');
    const isEnabled = !enabledText || !['no','false','0'].includes(enabledText.trim().toLowerCase());
    await json('/api/admin/workspaces/' + id, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name, emailAddress: emailAddress || null, routeKey: routeKey || null, isEnabled, sortOrder: current.sortOrder || 0 })
    });
    await loadWorkspaces();
    await showManagement();
}
