let frontDeskJobs = [];
let frontDeskRole = null;

const frontDeskBaseActivateView = activateView;
activateView = function(view) {
    if (view === 'frontdesk') {
        showFrontDesk();
        return;
    }

    frontDeskBaseActivateView(view);
};

async function loadFrontDeskRole() {
    try {
        frontDeskRole = await json('/api/security/global-role-me');
        const button = document.querySelector('.brand-button[data-view="frontdesk"]');
        if (button) {
            const me = await json('/api/auth/me');
            button.classList.toggle('hidden', !frontDeskRole.frontDesk && !(me.user || me).isAdministrator);
        }
    } catch {
        frontDeskRole = { frontDesk: false, permissions: {} };
    }
}

async function showFrontDesk() {
    if (!frontDeskRole) await loadFrontDeskRole();
    const me = await json('/api/auth/me');
    if (!frontDeskRole?.frontDesk && !(me.user || me).isAdministrator) {
        activateNavigation('frontdesk');
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML = '<h1>Front Desk</h1><p class="muted">You are not assigned to the Front Desk operational role.</p>';
        return;
    }

    activateNavigation('frontdesk');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');

    frontDeskJobs = await json('/api/jobs');
    renderFrontDesk();
}

function renderFrontDesk() {
    const term = (document.getElementById('frontDeskSearch')?.value || '').trim().toLowerCase();
    const jobs = frontDeskJobs.filter(job => !term || `${job.title} ${job.clientName} ${job.status} ${job.assignedTo || ''}`.toLowerCase().includes(term));
    const active = frontDeskJobs.filter(job => job.status !== 'Completed');
    const unassigned = active.filter(job => !job.assignedTo);
    const ready = active.filter(job => job.status === 'Ready for Client');

    genericView.innerHTML = `
        <div class="job-detail-header">
            <div>
                <h1>Front Desk</h1>
                <p>One operational workspace for reception: find any job, communicate with the customer or team, and take payment without switching systems.</p>
            </div>
            <div class="job-actions"><button class="action" onclick="showAccounts()">Accounts / Invoices</button><button class="action" onclick="showPos()">Sales / Payments</button><button class="primary" onclick="newRequestButton.click()">New Request</button></div>
        </div>
        <div class="cards">
            <div class="metric">Active jobs<strong>${active.length}</strong></div>
            <div class="metric">Ready for client<strong>${ready.length}</strong></div>
            <div class="metric">Unassigned<strong>${unassigned.length}</strong></div>
            <div class="metric">All jobs<strong>${frontDeskJobs.length}</strong></div>
        </div>
        <div class="front-desk-tools">
            <input id="frontDeskSearch" value="${esc(term)}" placeholder="Search client, job, status or staff" oninput="renderFrontDesk()">
            <button class="action" onclick="showFrontDesk()">Refresh</button>
        </div>
        <h2>Jobs</h2>
        <div id="frontDeskJobList">
            ${jobs.length ? jobs.map(renderFrontDeskJob).join('') : '<p class="muted">No matching jobs.</p>'}
        </div>`;
}

function renderFrontDeskJob(job) {
    return `
        <div class="front-desk-row">
            <div>
                <b>${esc(job.clientName)}</b>
                <div>${esc(job.title)}</div>
                <div class="meta">${esc(job.status)}${job.assignedTo ? ' · ' + esc(job.assignedTo) : ' · Unassigned'}${job.eventDate ? ' · ' + esc(job.eventDate) : ''}</div>
            </div>
            <div class="front-desk-actions">
                <button class="action" onclick="openFrontDeskClient('${job.id}')">Client</button>
                <button class="action" onclick="notifyFrontDeskTeam('${job.id}')">Notify Team</button>
                <button class="action" onclick="openJob('${job.id}')">Job</button>
                <button class="primary" onclick="openPos('${job.id}')">Billing / P O S</button>
            </div>
        </div>`;
}

async function openFrontDeskClient(jobId) {
    const job = await json('/api/jobs/' + jobId);
    if (!job.conversationId) {
        alert('This job has no linked customer conversation.');
        return;
    }

    await openConversation(job.conversationId);
}

async function notifyFrontDeskTeam(jobId) {
    let members;
    try {
        members = await json(`/api/jobs/${jobId}/notifiable-members`);
    } catch (error) {
        alert('You do not have permission to notify members for this job.');
        return;
    }

    if (!members.length) {
        alert('This job and its workspace have no active members to notify.');
        return;
    }

    const choices = members.map((member, index) => `${index + 1}. ${member.displayName || member.userName} (${member.userName}) · ${member.source}`).join('\n');
    const selected = Number(prompt(`Notify which member?\n\n${choices}`, '1'));
    if (!Number.isInteger(selected) || selected < 1 || selected > members.length) return;

    const member = members[selected - 1];
    const subject = prompt('Subject', 'Front Desk message');
    if (!subject) return;
    const body = prompt('Message');
    if (!body) return;

    const result = await json(`/api/jobs/${jobId}/notifications`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userName: member.userName, subject, body })
    });
    alert(result.message || `Notification request accepted for ${member.displayName || member.userName}. Delivery is not yet confirmed.`);
}

loadFrontDeskRole();
