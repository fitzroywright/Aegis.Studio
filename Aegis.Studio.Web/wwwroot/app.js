let selectedWorkspace = null;
let selectedConversation = null;
let workspaceCache = [];

const esc = value => String(value ?? '').replace(/[&<>'"]/g, character => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    "'": '&#39;',
    '"': '&quot;'
}[character]));

async function json(url, options) {
    const response = await fetch(url, options);
    if (!response.ok) {
        throw new Error(await response.text());
    }

    if (response.status === 204 || response.status === 205) return null;
    const body = await response.text();
    return body.trim() ? JSON.parse(body) : null;
}

function workspaceBandHue(id) {
    let hash=0;
    for(const character of String(id)) hash=(hash*31+character.charCodeAt(0))>>>0;
    return hash%360;
}
async function loadWorkspaces() {
    const data = await json('/api/workspaces');
    workspaceCache = data;
    workspaceList.innerHTML = data.map(workspace => `
        <button class="workspace" data-id="${workspace.id}" style="--workspace-hue:${workspaceBandHue(workspace.id)}">
            <span class="badge">${workspace.openCount}</span>
            <b>${esc(workspace.name)}</b>
            <small>${esc(workspace.address)}</small>
        </button>`).join('');

    await Promise.all(data.map(async workspace=>{
        const button=workspaceList.querySelector(`[data-id="${workspace.id}"]`);
        try{const appearance=await json(`/api/workspaces/${workspace.id}/appearance`);if(!button.isConnected)return;
            if(/^#[0-9a-f]{6}$/i.test(appearance.color)&&appearance.color.toLowerCase()!=='#b99750')button.style.background=`linear-gradient(105deg,${appearance.color}66,${appearance.color}15)`;
            const icon=document.createElement('span');icon.className='workspace-identity-icon';icon.setAttribute('aria-hidden','true');icon.textContent=({folder:'▰',camera:'📷',portrait:'👤',wedding:'💍',id:'▣'})[appearance.icon]||'▰';button.prepend(icon);
        }catch(error){button.title='Workspace appearance could not be loaded: '+error.message;}
    }));

    requestWorkspace.innerHTML = data.map(workspace =>
        `<option value="${workspace.id}">${esc(workspace.name)}</option>`).join('');

    document.querySelectorAll('.workspace').forEach(button => {
        button.onclick = () => selectWorkspace(button.dataset.id, button.querySelector('b').textContent);
    });
}

async function selectWorkspace(id, name) {
    selectedWorkspace = id;
    document.querySelectorAll('.workspace').forEach(button => {
        button.classList.toggle('active', button.dataset.id === id);
    });
    workspaceTitle.textContent = name;
    await loadConversations();
}

async function loadConversations() {
    const query = selectedWorkspace ? `?workspaceId=${selectedWorkspace}` : '';
    const data = await json('/api/conversations' + query);
    conversationList.innerHTML = data.map(conversation => {
        const last = conversation.messages[conversation.messages.length - 1];
        return `
            <div class="conversation-card" data-id="${conversation.id}">
                <h3>${esc(conversation.subject)}</h3>
                <div class="meta">${esc(conversation.contactName)} · ${esc(conversation.channel)} ${conversation.jobId ? '· JOB' : ''}</div>
                <div class="snippet">${esc(last?.body || '')}</div>
            </div>`;
    }).join('') || '<div class="empty-state" style="padding:50px 20px">No open conversations.</div>';

    document.querySelectorAll('.conversation-card').forEach(card => {
        card.onclick = () => openConversation(card.dataset.id);
    });
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
        ${conversation.jobId ? '<div class="job-banner">This conversation is linked to a job. The conversation remains intact as customer history.</div>' : ''}
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
                <label class="toggle"><input id="internalToggle" type="checkbox"> Internal note — customer will not see this</label>
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
}

async function sendMessage() {
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

async function showJobs() {
    activateNavigation('jobs');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    const jobs = await json('/api/jobs');
    genericView.innerHTML = `
        <h1>Jobs</h1>
        <p>Committed work. Conversations remain linked as context; the job drives execution.</p>
        ${renderJobRows(jobs)}`;
    bindJobRows();
}

async function openJob(id) {
    activateNavigation('jobs');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    const job = await json('/api/jobs/' + id);
    genericView.innerHTML = `
        <div class="job-detail-header">
            <div>
                <button class="link-button" onclick="showJobs()">← Jobs</button>
                <h1>${esc(job.title)}</h1>
                <p>${esc(job.clientName)} · ${esc(job.status)}${job.assignedTo ? ' · Assigned to ' + esc(studioFirstName(job.assignedTo)) : ''}</p>
            </div>
            <button class="action" onclick="openConversation('${job.conversationId}')">Open conversation</button>
        </div>
        <div class="cards">
            <div class="metric">Stage<strong>${esc(job.status)}</strong></div>
            <div class="metric">Assigned to<strong class="metric-text">${esc(job.assignedTo ? studioFirstName(job.assignedTo) : 'Unassigned')}</strong></div>
        </div>
        <div class="job-actions">
            <button class="primary" onclick="advanceJob('${job.id}','Scheduled')">Schedule</button>
            <button class="action" onclick="advanceJob('${job.id}','Photography')">Photography</button>
            <button class="action" onclick="advanceJob('${job.id}','Production')">Production</button>
            <button class="action" onclick="advanceJob('${job.id}','Ready for Client')">Ready for Client</button>
            <button class="action" onclick="advanceJob('${job.id}','Completed')">Complete</button>
            <button class="action" onclick="assignJob('${job.id}')">Assign staff</button>
        </div>
        `;
}

async function advanceJob(id, status) {
    await updateJob(id, { status, assignedTo: null, eventDate: null });
    await openJob(id);
}

async function assignJob(id) {
    const assignedTo = prompt('Assign this job to');
    if (!assignedTo) {
        return;
    }

    await updateJob(id, { status: null, assignedTo, eventDate: null });
    await openJob(id);
}

async function updateJob(id, payload) {
    return json('/api/jobs/' + id, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
    });
}

async function recordMoney(id) {
    const kind = prompt('Type', 'Deposit');
    if (!kind) {
        return;
    }

    const amountText = prompt('Amount', '0.00');
    const amount = Number(amountText);
    if (!Number.isFinite(amount) || amount === 0) {
        alert('Enter a non-zero amount. Use a negative value for a refund or reversal.');
        return;
    }

    const note = prompt('Note', '');
    await json('/api/jobs/' + id + '/financial-events', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ kind, amount, note })
    });
    await openJob(id);
}

async function showProduction() {
    activateNavigation('production');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    const jobs = await json('/api/jobs');
    const active = jobs.filter(job => job.status !== 'Completed');
    genericView.innerHTML = `
        <h1>Production</h1>
        <p>The production team sees work, deadlines and ownership — not inbox folders.</p>
        <div class="cards">
            <div class="metric">Active jobs<strong>${active.length}</strong></div>
            <div class="metric">In production<strong>${active.filter(job => job.status === 'Production').length}</strong></div>
            <div class="metric">Unassigned<strong>${active.filter(job => !job.assignedTo).length}</strong></div>
        </div>
        <h2>Work queue</h2>
        ${renderJobRows(active)}`;
    bindJobRows();
}

async function showAccounts() {
    activateNavigation('frontdesk');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    const [summary, jobs] = await Promise.all([
        json('/api/accounts/summary'),
        json('/api/jobs')
    ]);
    genericView.innerHTML = `
        <h1>Accounts</h1>
        <p>Accounting sees financial events attached to the same jobs the rest of Bryan's is already working.</p>
        <div class="cards">
            <div class="metric">Recorded activity<strong>${money(summary.recordedAmount)}</strong></div>
            <div class="metric">Jobs with activity<strong>${summary.jobsWithFinancialActivity}</strong></div>
            <div class="metric">Total jobs<strong>${summary.totalJobs}</strong></div>
        </div>
        <h2>Jobs</h2>
        ${renderJobRows(jobs)}`;
    bindJobRows();
}

async function showManagement() {
    activateNavigation('management');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    const [jobs, conversations] = await Promise.all([
        json('/api/jobs'),
        json('/api/conversations')
    ]);
    const completed = jobs.filter(job => job.status === 'Completed').length;
    genericView.innerHTML = `
        <h1>Management</h1>
        <p>One view across customer demand, committed work, production.</p>
        <div class="cards">
            <div class="metric">Open conversations<strong>${conversations.length}</strong></div>
            <div class="metric">Jobs<strong>${jobs.length}</strong></div>
            <div class="metric">Completed<strong>${completed}</strong></div>
        </div>`;
}

function renderJobRows(jobs) {
    if (!jobs.length) {
        return '<div class="empty-state" style="text-align:left"><h2>No jobs yet</h2><p>Open a conversation and choose Create Job when the customer commits.</p></div>';
    }

    return jobs.map(job => `
        <button class="job-row job-button" data-job-id="${job.id}">
            <div>
                <b>${esc(job.title)}</b>
                <div class="meta">${esc(job.clientName)}${job.assignedTo ? ' · ' + esc(studioFirstName(job.assignedTo)) : ''}</div>
            </div>
            <span class="status-pill">${esc(job.status)}</span>
        </button>`).join('');
}

function bindJobRows() {
    document.querySelectorAll('[data-job-id]').forEach(button => {
        button.onclick = () => openJob(button.dataset.jobId);
    });
}

function money(value) {
    return new Intl.NumberFormat('en-JM', { style: 'currency', currency: 'JMD' }).format(value || 0);
}

function activateNavigation(view) {
    document.querySelectorAll('.brand-button').forEach(button => {
        button.classList.toggle('active', button.dataset.view === view);
    });
}

function activateView(view) {
    if (view === 'conversations') {
        activateNavigation(view);
        conversationView.classList.remove('hidden');
        genericView.classList.add('hidden');
        return;
    }

    if (view === 'jobs') {
        showJobs();
    } else if (view === 'production') {
        showProduction();
    } else if (view === 'accounts') {
        showAccounts();
    } else if (view === 'management') {
        showManagement();
    }
}

allButton.onclick = () => {
    selectedWorkspace = null;
    workspaceTitle.textContent = 'All Work';
    document.querySelectorAll('.workspace').forEach(button => button.classList.remove('active'));
    loadConversations();
};

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

document.querySelectorAll('.brand-button').forEach(button => {
    button.onclick = () => activateView(button.dataset.view);
});

// Wait until permission-aware script overrides have been installed.
document.addEventListener('DOMContentLoaded', () => {
    loadWorkspaces().then(loadConversations).catch(error => {
        conversationList.textContent = 'Unable to load workspaces: ' + (error.message || error);
    });
}, { once: true });
