let currentWorkspacePermissions = null;

loadWorkspaces = async function() {
    const access = await json('/api/workspace-access');
    workspaceCache = access.map(item => ({
        id: item.workspace.id,
        name: item.workspace.name,
        address: item.workspace.emailAddress || '',
        iconName: item.workspace.iconName || 'folder',
        accentColor: item.workspace.accentColor || '#b99750',
        openCount: 0,
        permissions: item.permissions
    }));
    requestWorkspace.innerHTML = workspaceCache
        .filter(x => x.permissions.accessWorkspace)
        .map(workspace => `<option value="${workspace.id}">${esc(workspace.name)}</option>`)
        .join('');
    renderWorkspaceMenu();
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
    renderWorkspaceMenu();
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


function workspaceMenuGlyph(iconName) {
    return ({
        folder:'▰',
        camera:'◉',
        portrait:'●',
        wedding:'♡',
        id:'▣'
    })[String(iconName || '').toLowerCase()] || '▱';
}

function renderWorkspaceMenu() {
    const details=document.getElementById('workspaceMenu');
    const menu=document.getElementById('workspaceMenuList');
    if(!details||!menu)return;

    const available=workspaceCache.filter(x=>x.permissions?.accessWorkspace&&x.permissions?.view);
    const visible=available.length>6?available.slice(0,5):available.slice(0,6);
    const rows=visible.map(workspace=>{
        const active=String(selectedWorkspace||'')===String(workspace.id);
        const color=/^#[0-9a-fA-F]{6}$/.test(workspace.accentColor||'')?workspace.accentColor:'#7f8b99';
        return `<button type="button" class="workspace-menu-item${active?' active':''}" data-workspace-menu-id="${workspace.id}" data-workspace-menu-name="${esc(workspace.name)}">
            <span class="workspace-menu-icon" style="--workspace-accent:${color}" aria-hidden="true">${workspaceMenuGlyph(workspace.iconName)}</span>
            <span class="workspace-menu-name">${esc(workspace.name)}</span>
            ${active?'<span class="workspace-menu-current">Current</span>':''}
        </button>`;
    }).join('');

    const more=available.length>6
        ? '<button type="button" class="workspace-menu-item workspace-menu-more" data-workspace-menu-more><span class="workspace-menu-icon generic" aria-hidden="true">•••</span><span class="workspace-menu-name">More Workspaces</span></button>'
        : '';

    menu.innerHTML=rows+more;
    menu.querySelectorAll('[data-workspace-menu-id]').forEach(button=>button.onclick=async()=>{
        details.open=false;
        await selectWorkspace(button.dataset.workspaceMenuId,button.dataset.workspaceMenuName);
        renderWorkspaceMenu();
    });
    const moreButton=menu.querySelector('[data-workspace-menu-more]');
    if(moreButton)moreButton.onclick=()=>{
        details.open=false;
        window.studioNavigate?.('workspaces');
    };
}

document.addEventListener('click',event=>{
    const details=document.getElementById('workspaceMenu');
    if(details?.open&&!details.contains(event.target))details.open=false;
});
