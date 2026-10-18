let selectedWorkspace = null;
let selectedConversation = null;
let workspaceCache = [];

async function json(url, options) {
    const {onResponse,...requestOptions}=options||{};
    const response = await fetch(url, requestOptions);
    onResponse?.(response);
    if (!response.ok) {
        let error=null;try{error=await response.json();}catch{}
        const fallback=response.status===403?'You do not have permission for this action.':response.status===401?'Your session has expired. Sign in again.':response.status===404?'This item is no longer available.':'This action could not be completed. Please retry.';
        throw new Error(response.status>=500?fallback:(error?.error||error?.detail||fallback));
    }

    if (response.status === 204 || response.status === 205) return null;
    const body = await response.text();
    return body.trim() ? JSON.parse(body) : null;
}

async function loadWorkspaces() {
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
}

// Public workspace entry points delegate to the sole workspace controller.
async function selectWorkspace(id, name) {
    return window.StudioWorkspace.select(id, name);
}

async function loadConversations() {
    return window.StudioWorkspace.load();
}

async function openConversation(id) {
    selectedConversation = id;
    document.querySelectorAll('.conversation-card').forEach(card => {
        card.classList.toggle('active', card.dataset.id === id);
    });

    const conversation = await json('/api/conversations/' + id);
    let sendingIdentity = null;
    if (String(conversation.channel || '').toLowerCase() === 'email') {
        try {
            sendingIdentity = await json('/api/conversations/' + id + '/sending-identities');
        } catch {
            sendingIdentity = { defaultIdentity: 'Workspace', identities: [] };
        }
    }
    const identityOptions = sendingIdentity?.identities || [];
    const identityPicker = identityOptions.length
        ? `<div class="send-split"><button id="sendReplyButton" class="primary" onclick="sendMessage()">Send as Workspace</button><select id="sendingIdentity" aria-label="Send reply as" title="Choose Workspace or Myself">
                ${identityOptions.map(identity => `<option value="${esc(identity.type)}" ${identity.isDefault ? 'selected' : ''}>${esc(identity.label)}</option>`).join('')}
            </select></div>`
        : '<button class="primary" onclick="sendMessage()">Send</button>';
    detailPane.classList.remove('empty');
    detailPane.innerHTML = `
        <div class="detail-header">
            <div>
                <h1>${esc(conversation.subject)}</h1>
                <p>${esc(conversation.contactName)} · ${esc(conversation.contactAddress)} · via ${esc(conversation.channel)}</p>
            </div>
            <div>${conversation.jobId
                ? `<button class="action" onclick="openJob('${conversation.jobId}')">Open Job</button>`
                : '<button class="primary" onclick="createJob()">Create Job</button>'}</div>
        </div>
        ${conversation.jobId ? '<div class="job-banner">This conversation is linked to a job. The conversation remains intact as client history.</div>' : ''}
        <div class="messages">
            ${conversation.messages.map(message => `
                <article class="message ${message.visibility.toLowerCase()}">
                    <div class="message-head">${esc(message.visibility.toUpperCase())} · ${esc(message.author)} · ${new Date(message.createdAt).toLocaleString()}</div>
                    <p>${esc(message.body)}</p>
                </article>`).join('')}
        </div>
        <div class="composer">
            <textarea id="messageBody" placeholder="Write a reply or internal note..."></textarea>
            <div class="composer-row">
                <label class="toggle"><input id="internalToggle" type="checkbox"> Internal note — client will not see this</label>
                ${identityPicker}
            </div>
        </div>`;
    const internalToggle = document.getElementById('internalToggle');
    const identitySelect = document.getElementById('sendingIdentity');
    if (internalToggle && identitySelect) {
        const syncIdentityState = () => {
            identitySelect.disabled = internalToggle.checked;
            const button = document.getElementById('sendReplyButton');
            button.textContent = internalToggle.checked ? 'Add internal note' : identitySelect.value === 'Self' ? 'Send as Myself' : 'Send as Workspace';
            button.title = internalToggle.checked ? 'Visible to staff only' : identitySelect.selectedOptions[0]?.textContent || '';

        };
        internalToggle.addEventListener('change', syncIdentityState);
        identitySelect.addEventListener('change', syncIdentityState);
        syncIdentityState();
    }
    applyConversationPermissions();
    await tightenConversationExperience(id);
    await renderAttachments(id);
}

async function sendMessage() {
    if (selectedWorkspace && !currentWorkspacePermissions?.replyRequester) {
        alert('You do not have permission to reply to the requester in this workspace.');
        return;
    }
    const body = document.getElementById('messageBody').value.trim();
    if (!body) {
        return;
    }

    await json('/api/conversations/' + selectedConversation + '/messages', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            body,
            isInternal: document.getElementById('internalToggle').checked,
            sendingIdentity: document.getElementById('sendingIdentity')?.value || 'Workspace'
        })
    });

    await openConversation(selectedConversation);
    await loadConversations();
    await loadWorkspaces();
}

async function createJob() {
    if (selectedWorkspace && !currentWorkspacePermissions?.createJob) {
        alert('You do not have permission to create jobs in this workspace.');
        return;
    }
    const conversation = await json('/api/conversations/' + selectedConversation);
    const title = prompt('Job title', conversation.subject);
    if (!title) {
        return;
    }

    const job = await json('/api/conversations/' + selectedConversation + '/jobs', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ title, eventDate: null })
    });

    await openConversation(selectedConversation);
    await loadConversations();
    await openJob(job.id);
}

async function openJob(id) {
    await openWorkspaceJob(id);
    await window.renderJobAccess?.(id);
}

function money(value) {
    return new Intl.NumberFormat('en-JM', { style: 'currency', currency: 'JMD' }).format(value || 0);
}

// Site navigation is owned exclusively by studio-navigation.js.

newRequestButton.onclick = () => {
    if (selectedWorkspace) {
        requestWorkspace.value = selectedWorkspace;
    }

    newRequestDialog.showModal();
};

cancelRequest.onclick = () => newRequestDialog.close();

newRequestForm.onsubmit = async event => {
    event.preventDefault();
    const request = {
        workspaceId: requestWorkspace.value,
        destination: null,
        contactName: requestContactName.value,
        contactAddress: requestContactAddress.value,
        subject: requestSubject.value,
        body: requestBody.value,
        channel: requestChannel.value
    };

    const created = await json('/api/interactions', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request)
    });

    newRequestForm.reset();
    newRequestDialog.close();
    selectedWorkspace = created.workspaceId;
    await loadWorkspaces();
    const workspace = workspaceCache.find(item => item.id === selectedWorkspace);
    workspaceTitle.textContent = workspace?.name || 'Work';
    await loadConversations();
    await openConversation(created.id);
};




