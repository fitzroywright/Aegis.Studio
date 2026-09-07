let frontDeskJobs = [];

const frontDeskBaseActivateView = activateView;
activateView = function(view) {
    if (view === 'frontdesk') {
        showFrontDesk();
        return;
    }

    frontDeskBaseActivateView(view);
};

async function showFrontDesk() {
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
            <button class="primary" onclick="newRequestButton.click()">New Request</button>
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
        members = await json(`/api/jobs/${jobId}/members`);
    } catch (error) {
        alert('You need Job Member management permission to select and notify individual job members from Front Desk.');
        return;
    }

    const active = members.filter(member => member.isActive);
    if (!active.length) {
        alert('This job has no explicit active members to notify.');
        return;
    }

    const choices = active.map((member, index) => `${index + 1}. ${member.displayName || member.userName} (${member.userName})`).join('\n');
    const selected = Number(prompt(`Notify which job member?\n\n${choices}`, '1'));
    if (!Number.isInteger(selected) || selected < 1 || selected > active.length) return;

    const member = active[selected - 1];
    const subject = prompt('Subject', 'Front Desk message');
    if (!subject) return;
    const body = prompt('Message');
    if (!body) return;

    await json(`/api/jobs/${jobId}/notifications`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userName: member.userName, subject, body })
    });
    alert(`Notification sent to ${member.displayName || member.userName}.`);
}
