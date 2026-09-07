async function showWorkspaces() {
    activateNavigation('workspaces');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');

    try {
        const me = await json('/api/auth/me');
        const isAdministrator = me?.user?.isAdministrator === true;

        if (!isAdministrator) {
            genericView.innerHTML = `
                <h1>Workspaces</h1>
                <div class="empty-state" style="text-align:left">
                    <h2>Administrator access required</h2>
                    <p>Workspace configuration controls routing and is restricted to Studio administrators.</p>
                </div>`;
            return;
        }

        const workspaces = await json('/api/admin/workspaces/');
        genericView.innerHTML = `
            <div class="job-detail-header">
                <div>
                    <h1>Workspaces</h1>
                    <p>Create and manage Studio workspaces and their exact email/booking routing identities.</p>
                </div>
            </div>
            <section class="job-section">
                <h2>Configured workspaces</h2>
                <div id="workspaceAdminRows">${renderWorkspaceAdminRows(workspaces)}</div>
            </section>
            <section class="job-section">
                <h2>Create workspace</h2>
                <p class="muted">Routing uses the exact email address or route key. No keyword guessing is performed.</p>
                <div class="pos-entry">
                    <label>Name<input id="workspaceName" placeholder="e.g. Weddings"></label>
                    <label>Email address<input id="workspaceEmail" type="email" placeholder="e.g. weddings@example.com"></label>
                    <label>Route key<input id="workspaceRouteKey" placeholder="e.g. weddings"></label>
                    <button class="primary" onclick="createWorkspace()">Create workspace</button>
                </div>
            </section>`;
    } catch (error) {
        genericView.innerHTML = `
            <h1>Workspaces</h1>
            <div class="empty-state" style="text-align:left">
                <h2>Unable to load workspace administration</h2>
                <p>${esc(error.message || error)}</p>
            </div>`;
    }
}

function renderWorkspaceAdminRows(workspaces) {
    if (!workspaces.length) {
        return '<p class="muted">No workspaces configured yet. Create the first workspace below.</p>';
    }

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
    const emailAddress = document.getElementById('workspaceEmail').value.trim();
    const routeKey = document.getElementById('workspaceRouteKey').value.trim();

    if (!name) {
        alert('Workspace name is required.');
        return;
    }

    await json('/api/admin/workspaces/', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            name,
            emailAddress: emailAddress || null,
            routeKey: routeKey || null,
            isEnabled: true,
            sortOrder: 0
        })
    });

    await loadWorkspaces();
    await showWorkspaces();
}

async function editWorkspace(id) {
    const workspaces = await json('/api/admin/workspaces/');
    const current = workspaces.find(workspace => workspace.id === id);
    if (!current) {
        return;
    }

    const name = prompt('Workspace name', current.name);
    if (!name) {
        return;
    }

    const emailAddress = prompt('Workspace email address', current.emailAddress || '');
    if (emailAddress === null) {
        return;
    }

    const routeKey = prompt('Exact routing key', current.routeKey || '');
    if (routeKey === null) {
        return;
    }

    const enabledText = prompt('Enabled? yes/no', current.isEnabled ? 'yes' : 'no');
    if (enabledText === null) {
        return;
    }

    const isEnabled = !['no', 'false', '0'].includes(enabledText.trim().toLowerCase());
    await json('/api/admin/workspaces/' + id, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            name: name.trim(),
            emailAddress: emailAddress.trim() || null,
            routeKey: routeKey.trim() || null,
            isEnabled,
            sortOrder: current.sortOrder || 0
        })
    });

    await loadWorkspaces();
    await showWorkspaces();
}

const workspaceNavigationButton = document.querySelector('.brand-button[data-view="workspaces"]');
if (workspaceNavigationButton) {
    workspaceNavigationButton.onclick = () => showWorkspaces();
}
