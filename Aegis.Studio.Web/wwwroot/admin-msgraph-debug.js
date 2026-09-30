const baseShowWorkspacesForGraphDiagnostics = showWorkspaces;

showWorkspaces = async function() {
    await baseShowWorkspacesForGraphDiagnostics();

    try {
        const me = await json('/api/auth/me');
        if (me?.user?.isAdministrator !== true) return;
        await appendGraphDiagnosticsCard();
    } catch (error) {
        console.warn('Microsoft Graph diagnostics unavailable', error);
    }
};

async function appendGraphDiagnosticsCard() {
    const card = document.createElement('section');
    card.className = 'job-section';
    card.innerHTML = '<h2>Microsoft Graph Debug</h2><p class="muted">Checking configuration...</p>';
    genericView.appendChild(card);

    try {
        const status = await json('/api/admin/operations/msgraph');
        card.innerHTML = [
            '<h2>Microsoft Graph Debug</h2>',
            '<p class="muted">Live validation of Studio email ingestion configuration.</p>',
            '<div class="cards">',
            metric('State', status.state),
            metric('Authentication', status.authenticationSucceeded ? 'Valid' : 'Not verified'),
            metric('Mailbox access', status.mailboxAccessible ? 'Accessible' : 'Not verified'),
            metric('Client secret', status.secretPresent ? 'Present' : 'Missing'),
            '</div>',
            row('Tenant ID', status.tenantId || 'Not configured'),
            row('Client ID', status.clientId || 'Not configured'),
            row('Collection mailbox', status.mailbox || 'Not configured'),
            row('Email ingestion enabled', status.enabled ? 'Yes' : 'No'),
            row('Poll interval', String(status.pollSeconds) + ' seconds'),
            row('Graph page size', String(status.pageSize)),
            row('Graph response', status.graphStatusCode == null ? 'No HTTP status returned' : 'HTTP ' + status.graphStatusCode),
            '<div class="job-row"><div><b>Verification result</b><div class="meta">' + esc(status.detail || '') + '</div></div><button class="action" onclick="refreshGraphDiagnostics(this)">Recheck</button></div>'
        ].join('');
    } catch (error) {
        card.innerHTML = '<h2>Microsoft Graph Debug</h2><p class="muted">Unable to verify configuration.</p><div class="job-row"><div><b>Error</b><div class="meta">' + esc(error.message || error) + '</div></div><button class="action" onclick="refreshGraphDiagnostics(this)">Retry</button></div>';
    }
}

function metric(label, value) {
    return '<div class="metric">' + esc(label) + '<strong>' + esc(value) + '</strong></div>';
}

function row(label, value) {
    return '<div class="job-row"><div><b>' + esc(label) + '</b><div class="meta">' + esc(value) + '</div></div></div>';
}

async function refreshGraphDiagnostics(button) {
    const card = button.closest('.job-section');
    if (card) card.remove();
    await appendGraphDiagnosticsCard();
}
