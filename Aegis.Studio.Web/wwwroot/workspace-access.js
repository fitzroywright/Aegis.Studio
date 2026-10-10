let currentWorkspacePermissions = null;

// Permission presentation is called by the conversation renderer, never by a wrapper.
function applyConversationPermissions() {
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
}

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

    const moreLabel=available.length>6?'More Workspaces':'All Workspaces';
    const moreIcon=available.length>6?'•••':'▱';
    const more='<button type="button" class="workspace-menu-item workspace-menu-more" data-workspace-menu-more><span class="workspace-menu-icon generic" aria-hidden="true">'+moreIcon+'</span><span class="workspace-menu-name">'+moreLabel+'</span></button>';

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

