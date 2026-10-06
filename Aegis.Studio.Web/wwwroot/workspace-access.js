let currentWorkspacePermissions = null;

loadWorkspaces = async function() {
    const access = await json('/api/workspace-access');
    workspaceCache = access.map(item => ({
        id: item.workspace.id,
        name: item.workspace.name,
        address: item.workspace.emailAddress || '',
        openCount: 0,
        permissions: item.permissions
    }));
    requestWorkspace.innerHTML = workspaceCache
        .filter(x => x.permissions.accessWorkspace)
        .map(workspace => `<option value="${workspace.id}">${esc(workspace.name)}</option>`)
        .join('');
};

const originalSelectWorkspaceForAccess = selectWorkspace;
selectWorkspace = async function(id, name) {
    const workspace = workspaceCache.find(x => x.id === id);
    if (!workspace?.permissions?.accessWorkspace || !workspace?.permissions?.view) {
        alert('You do not have permission to view this workspace.');
        return;
    }
    currentWorkspacePermissions = workspace.permissions;
    await originalSelectWorkspaceForAccess(id, name);
};

const originalOpenConversationForAccess = openConversation;
openConversation = async function(id) {
    await originalOpenConversationForAccess(id);
    if (!selectedWorkspace) return;
    const permissions = workspaceCache.find(x => x.id === selectedWorkspace)?.permissions;
    if (!permissions) return;
    currentWorkspacePermissions = permissions;
    const sendButton = detailPane.querySelector('.composer .primary');
    const textarea = detailPane.querySelector('#messageBody');
    const internalToggle = detailPane.querySelector('#internalToggle');
    if (!permissions.replyRequester && sendButton) {
        sendButton.disabled = true;
        sendButton.title = 'You do not have permission to reply to requesters.';
    }
    if (!permissions.replyRequester && textarea) textarea.placeholder = 'Read only — reply permission is required.';
    if (!permissions.editJob) {
        detailPane.querySelectorAll('[onclick^="createJob"]').forEach(button => {
            if (!permissions.createJob) button.disabled = true;
        });
    }
    if (internalToggle && !permissions.editJob && !permissions.manageProduction) {
        internalToggle.disabled = true;
        internalToggle.title = 'Internal notes require edit or production permissions.';
    }
};

const originalSendMessageForAccess = sendMessage;
sendMessage = async function() {
    if (selectedWorkspace && !currentWorkspacePermissions?.replyRequester) {
        alert('You do not have permission to reply to the requester in this workspace.');
        return;
    }
    await originalSendMessageForAccess();
};

const originalCreateJobForAccess = createJob;
createJob = async function() {
    if (selectedWorkspace && !currentWorkspacePermissions?.createJob) {
        alert('You do not have permission to create jobs in this workspace.');
        return;
    }
    await originalCreateJobForAccess();
};
