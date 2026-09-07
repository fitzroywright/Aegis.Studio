let productionTaskCache = new Map();
let operationalActivityCache = new Map();
let shootCache = new Map();
let operationalJobs = new Map();

showProduction = async function() {
    activateNavigation('production');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');

    const [summary, activities, shoots, tasks, jobs] = await Promise.all([
        json('/api/operations/summary'),
        json('/api/operations/activities'),
        json('/api/operations/shoots'),
        json('/api/production/queue'),
        json('/api/jobs')
    ]);

    operationalActivityCache = new Map(activities.map(item => [item.id, item]));
    shootCache = new Map(shoots.map(item => [item.id, item]));
    productionTaskCache = new Map(tasks.map(task => [task.id, task]));
    operationalJobs = new Map(jobs.map(job => [job.id, job]));

    const openActivities = activities.filter(item => item.status !== 'Completed' && item.status !== 'Cancelled');
    const todaysShoots = shoots.filter(item => new Date(item.callTimeUtc).toDateString() === new Date().toDateString());

    genericView.innerHTML = `
        <div class="job-detail-header">
            <div><h1>Production & Photography</h1><p>One operational board for photographers, assistants, shoots, file handoff, editing, quality control, delivery and archive.</p></div>
        </div>
        <div class="cards">
            <div class="metric">Shoots today<strong>${summary.shootsToday}</strong></div>
            <div class="metric">On shoot now<strong>${summary.shootsInProgress}</strong></div>
            <div class="metric">Awaiting backup<strong>${summary.shootsAwaitingBackup}</strong></div>
            <div class="metric">Reshoots required<strong>${summary.reshootsRequired}</strong></div>
            <div class="metric">Open activities<strong>${summary.openActivities}</strong></div>
            <div class="metric">Blocked<strong>${summary.blockedActivities}</strong></div>
            <div class="metric">Overdue<strong>${summary.overdueActivities}</strong></div>
        </div>

        <section class="job-section">
            <div class="job-detail-header"><div><h2>Photographer activity</h2><p class="muted">Call times, check-in/out, image counts, backup, reshoots and production handoff.</p></div><button class="primary" onclick="openShootForm()">+ Schedule shoot</button></div>
            ${shoots.length ? shoots.map(renderShootRow).join('') : '<p class="muted">No photographer sessions scheduled yet.</p>'}
        </section>

        <section class="job-section">
            <div class="job-detail-header"><div><h2>Operational activities</h2><p class="muted">Travel, equipment prep, ingest, backup, cull, edit, retouch, proof, print, QC, delivery, archive and reshoots.</p></div><button class="primary" onclick="openActivityForm()">+ Add activity</button></div>
            ${openActivities.length ? openActivities.map(renderActivityRow).join('') : '<p class="muted">No open operational activities.</p>'}
        </section>

        <section class="job-section">
            <h2>Today</h2>
            ${todaysShoots.length ? todaysShoots.map(renderShootRow).join('') : '<p class="muted">No shoots today.</p>'}
        </section>

        <section class="job-section">
            <h2>Workflow templates</h2>
            <p class="muted">Legacy production task templates remain available while the richer activity model is used for day-to-day operations.</p>
            ${jobs.map(job => `<div class="job-row"><div><b>${esc(job.title)}</b><div class="meta">${esc(job.clientName)} · ${esc(job.status)}</div></div><div><button class="action" data-template-job="${job.id}" data-template-name="standard">Standard</button><button class="action" data-template-job="${job.id}" data-template-name="graduation">Graduation</button><button class="action" data-template-job="${job.id}" data-template-name="id">ID</button><button class="action" onclick="openJobOperations('${job.id}')">Operations</button></div></div>`).join('') || '<p class="muted">No jobs yet.</p>'}
        </section>

        <dialog id="activityDialog"><form id="activityForm" method="dialog" class="request-form">
            <h2>Operational activity</h2>
            <label>Job<select id="activityJob" required>${jobs.map(job => `<option value="${job.id}">${esc(job.title)} · ${esc(job.clientName)}</option>`).join('')}</select></label>
            <label>Type<select id="activityType"><option>Shoot</option><option>Travel</option><option>EquipmentPrep</option><option>FileIngest</option><option>Backup</option><option>Cull</option><option>Edit</option><option>Retouch</option><option>Proof</option><option>Print</option><option>QC</option><option>Delivery</option><option>Archive</option><option>Reshoot</option><option>Other</option></select></label>
            <label>Title<input id="activityTitle" required></label>
            <label>Assigned to<input id="activityAssigned" placeholder="AD user name"></label>
            <div class="row"><label>Scheduled<input id="activityScheduled" type="datetime-local"></label><label>Due<input id="activityDue" type="datetime-local"></label></div>
            <label>Priority<select id="activityPriority"><option>Normal</option><option>Low</option><option>High</option><option>Urgent</option></select></label>
            <label>Location<input id="activityLocation"></label>
            <label>Description<textarea id="activityDescription"></textarea></label>
            <label>Handoff to<input id="activityHandoff" placeholder="next person/team"></label>
            <div class="dialog-actions"><button type="button" onclick="activityDialog.close()">Cancel</button><button type="submit" class="primary">Create</button></div>
        </form></dialog>

        <dialog id="shootDialog"><form id="shootForm" method="dialog" class="request-form">
            <h2>Schedule photographer</h2>
            <label>Job<select id="shootJob" required>${jobs.map(job => `<option value="${job.id}">${esc(job.title)} · ${esc(job.clientName)}</option>`).join('')}</select></label>
            <label>Photographer<input id="shootPhotographer" required placeholder="AD user name"></label>
            <label>Assistant<input id="shootAssistant" placeholder="optional AD user name"></label>
            <div class="row"><label>Call time<input id="shootStart" type="datetime-local" required></label><label>Planned end<input id="shootEnd" type="datetime-local" required></label></div>
            <label>Location<input id="shootLocation"></label>
            <label>Coverage notes<textarea id="shootCoverage"></textarea></label>
            <label>Equipment notes<textarea id="shootEquipment"></textarea></label>
            <div class="dialog-actions"><button type="button" onclick="shootDialog.close()">Cancel</button><button type="submit" class="primary">Schedule</button></div>
        </form></dialog>`;

    activityForm.onsubmit = createOperationalActivity;
    shootForm.onsubmit = createShootSession;
    document.querySelectorAll('[data-template-job]').forEach(button => button.onclick = () => applyProductionTemplate(button.dataset.templateJob, button.dataset.templateName));
};

function renderShootRow(shoot) {
    const job = operationalJobs.get(shoot.jobId);
    const backup = shoot.backupConfirmed ? 'Backup confirmed' : 'BACKUP PENDING';
    const reshoot = shoot.reshootRequired ? ' · RESHOOT REQUIRED' : '';
    return `<div class="job-row"><div><b>${esc(job?.title || 'Job')} — ${esc(shoot.photographerUserName)}</b><div class="meta">${new Date(shoot.callTimeUtc).toLocaleString()} → ${new Date(shoot.plannedEndUtc).toLocaleString()}${shoot.location ? ' · '+esc(shoot.location) : ''}</div><div class="meta">${esc(shoot.status)} · ${shoot.imageCount} images · ${esc(backup)}${reshoot}${shoot.handoffStatus === 'HandedOff' ? ' · handed to '+esc(shoot.handoffTo || 'production') : ''}</div></div><div class="job-actions"><button class="action" onclick="updateShoot('${shoot.id}','checkin')">Check in</button><button class="action" onclick="updateShoot('${shoot.id}','checkout')">Check out</button><button class="action" onclick="recordShootResults('${shoot.id}')">Results</button><button class="action" onclick="handoffShoot('${shoot.id}')">Handoff</button><button class="action" onclick="openJobOperations('${shoot.jobId}')">Job</button></div></div>`;
}

function renderActivityRow(activity) {
    const job = operationalJobs.get(activity.jobId);
    const due = activity.dueAtUtc ? ` · due ${new Date(activity.dueAtUtc).toLocaleString()}` : '';
    const blocked = activity.blockedReason ? ` · ${esc(activity.blockedReason)}` : '';
    return `<div class="job-row"><div><b>${esc(activity.activityType)} · ${esc(activity.title)}</b><div class="meta">${esc(job?.title || 'Job')} · ${esc(activity.assignedTo || 'Unassigned')} · ${esc(activity.priority)} · ${esc(activity.status)}${due}${blocked}</div></div><div class="job-actions"><button class="action" onclick="setOperationalActivityStatus('${activity.id}','InProgress')">Start</button><button class="action" onclick="blockOperationalActivity('${activity.id}')">Block</button><button class="action" onclick="setOperationalActivityStatus('${activity.id}','Completed')">Complete</button><button class="action" onclick="openJobOperations('${activity.jobId}')">Job</button></div></div>`;
}

function openActivityForm(jobId) {
    const dialog = document.getElementById('activityDialog');
    if (jobId) activityJob.value = jobId;
    dialog.showModal();
}

function openShootForm(jobId) {
    const start = new Date(Date.now() + 3600000); start.setMinutes(0,0,0);
    const end = new Date(start.getTime() + 2*3600000);
    const local = date => new Date(date.getTime() - date.getTimezoneOffset()*60000).toISOString().slice(0,16);
    shootStart.value = local(start); shootEnd.value = local(end);
    if (jobId) shootJob.value = jobId;
    shootDialog.showModal();
}

async function createOperationalActivity(event) {
    event.preventDefault();
    await json(`/api/jobs/${activityJob.value}/operations/activities`, { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({
        activityType:activityType.value, title:activityTitle.value.trim(), description:activityDescription.value.trim()||null,
        status:'NotStarted', priority:activityPriority.value, assignedTo:activityAssigned.value.trim()||null,
        scheduledAtUtc:activityScheduled.value ? new Date(activityScheduled.value).toISOString() : null,
        dueAtUtc:activityDue.value ? new Date(activityDue.value).toISOString() : null,
        location:activityLocation.value.trim()||null, blockedReason:null, handoffTo:activityHandoff.value.trim()||null
    }) });
    activityDialog.close(); await showProduction();
}

async function createShootSession(event) {
    event.preventDefault();
    await json(`/api/jobs/${shootJob.value}/operations/shoots`, { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({
        scheduleEntryId:null, photographerUserName:shootPhotographer.value.trim(), assistantUserName:shootAssistant.value.trim()||null,
        callTimeUtc:new Date(shootStart.value).toISOString(), plannedEndUtc:new Date(shootEnd.value).toISOString(),
        location:shootLocation.value.trim()||null, coverageNotes:shootCoverage.value.trim()||null, equipmentNotes:shootEquipment.value.trim()||null
    }) });
    shootDialog.close(); await showProduction();
}

async function setOperationalActivityStatus(id, status, blockedReason) {
    const item = operationalActivityCache.get(id); if (!item) return;
    await json('/api/operations/activities/' + id, { method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify({
        activityType:item.activityType,title:item.title,description:item.description,status,priority:item.priority,assignedTo:item.assignedTo,
        scheduledAtUtc:item.scheduledAtUtc,dueAtUtc:item.dueAtUtc,location:item.location,blockedReason:blockedReason||null,handoffTo:item.handoffTo
    }) });
    await showProduction();
}

async function blockOperationalActivity(id) {
    const reason = prompt('Why is this activity blocked?'); if (!reason) return;
    await setOperationalActivityStatus(id,'Blocked',reason);
}

async function updateShoot(id, action) {
    const item = shootCache.get(id); if (!item) return;
    await json('/api/operations/shoots/' + id, { method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ action, imageCount:null, backupConfirmed:null, reshootRequired:null, coverageNotes:null, equipmentNotes:null, handoffTo:null }) });
    await showProduction();
}

async function recordShootResults(id) {
    const item = shootCache.get(id); if (!item) return;
    const count = Number(prompt('Image count', String(item.imageCount || 0))); if (!Number.isInteger(count) || count < 0) return;
    const backup = confirm('Has the shoot been backed up?');
    const reshoot = confirm('Is a reshoot required?');
    const coverage = prompt('Coverage / field notes', item.coverageNotes || '');
    await json('/api/operations/shoots/' + id, { method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ action:'update', imageCount:count, backupConfirmed:backup, reshootRequired:reshoot, coverageNotes:coverage, equipmentNotes:null, handoffTo:null }) });
    await showProduction();
}

async function handoffShoot(id) {
    const item = shootCache.get(id); if (!item) return;
    if (!item.backupConfirmed && !confirm('Backup has not been confirmed. Hand off anyway?')) return;
    const handoffTo = prompt('Hand off files to which user/team?'); if (!handoffTo) return;
    await json('/api/operations/shoots/' + id, { method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ action:'handoff', imageCount:null, backupConfirmed:null, reshootRequired:null, coverageNotes:null, equipmentNotes:null, handoffTo }) });
    await showProduction();
}

async function openJobOperations(jobId) {
    const [job, activities, shoots, audit] = await Promise.all([
        json('/api/jobs/' + jobId), json('/api/operations/activities?jobId=' + jobId), json('/api/operations/shoots?jobId=' + jobId), json(`/api/jobs/${jobId}/operations/audit`)
    ]);
    genericView.innerHTML = `<div class="job-detail-header"><div><button class="link-button" onclick="showProduction()">← Production</button><h1>${esc(job.title)}</h1><p>${esc(job.clientName)} · ${esc(job.status)}</p></div><div><button class="primary" onclick="showProduction().then(()=>openShootForm('${job.id}'))">+ Shoot</button><button class="action" onclick="showProduction().then(()=>openActivityForm('${job.id}'))">+ Activity</button></div></div>
        <section class="job-section"><h2>Photography</h2>${shoots.length ? shoots.map(renderShootRow).join('') : '<p class="muted">No shoots recorded.</p>'}</section>
        <section class="job-section"><h2>Production activities</h2>${activities.length ? activities.map(renderActivityRow).join('') : '<p class="muted">No activities recorded.</p>'}</section>
        <section class="job-section"><h2>Operational history</h2>${audit.length ? audit.map(item => `<div class="financial-row"><div><b>${esc(item.action)}</b><small>${esc(item.userName)} · ${new Date(item.createdAtUtc).toLocaleString()}</small></div><span>${esc(item.detail || '')}</span></div>`).join('') : '<p class="muted">No operational history yet.</p>'}</section>`;
}

async function applyProductionTemplate(jobId, templateName) {
    await json(`/api/jobs/${jobId}/production/template/${encodeURIComponent(templateName)}`, { method:'POST' });
    await showProduction();
}

const productionNavigationButton = document.querySelector('.brand-button[data-view="production"]');
if (productionNavigationButton) productionNavigationButton.onclick = () => showProduction();
