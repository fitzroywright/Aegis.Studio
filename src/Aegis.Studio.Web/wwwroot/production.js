let productionTaskCache = new Map();

showProduction = async function() {
    activateNavigation('production');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    const [tasks, jobs] = await Promise.all([json('/api/production/queue'), json('/api/jobs')]);
    productionTaskCache = new Map(tasks.map(task => [task.id, task]));
    const jobsById = new Map(jobs.map(job => [job.id, job]));
    const openTasks = tasks.filter(task => task.status !== 'Completed');
    genericView.innerHTML = `
        <div class="job-detail-header"><div><h1>Production</h1><p>Track every production step, owner, due date and blocker from capture through delivery.</p></div></div>
        <div class="cards">
            <div class="metric">Open tasks<strong>${openTasks.length}</strong></div>
            <div class="metric">In progress<strong>${openTasks.filter(task => task.status === 'InProgress').length}</strong></div>
            <div class="metric">Blocked<strong>${openTasks.filter(task => task.status === 'Blocked').length}</strong></div>
            <div class="metric">Overdue<strong>${openTasks.filter(task => task.dueAtUtc && new Date(task.dueAtUtc) < new Date()).length}</strong></div>
        </div>
        <section class="job-section"><h2>Production queue</h2>${openTasks.length ? openTasks.map(task => {
            const job = jobsById.get(task.jobId);
            return `<div class="job-row"><div><b>${esc(task.title)}</b><div class="meta">${esc(job?.title || 'Job')} · ${esc(task.assignedTo || 'Unassigned')} · ${esc(task.status)}${task.dueAtUtc ? ' · due '+new Date(task.dueAtUtc).toLocaleString() : ''}</div></div><div><button class="action" data-production-id="${task.id}" data-production-status="InProgress">Start</button><button class="action" data-production-id="${task.id}" data-production-status="Blocked">Block</button><button class="action" data-production-id="${task.id}" data-production-status="Completed">Complete</button></div></div>`;
        }).join('') : '<p class="muted">No production tasks yet. Apply a workflow template to a job below.</p>'}</section>
        <section class="job-section"><h2>Jobs</h2>${jobs.map(job => `<div class="job-row"><div><b>${esc(job.title)}</b><div class="meta">${esc(job.clientName)} · ${esc(job.status)}</div></div><div><button class="action" data-template-job="${job.id}" data-template-name="standard">Standard</button><button class="action" data-template-job="${job.id}" data-template-name="graduation">Graduation</button><button class="action" data-template-job="${job.id}" data-template-name="id">ID</button></div></div>`).join('') || '<p class="muted">No jobs yet.</p>'}</section>`;

    document.querySelectorAll('[data-production-id]').forEach(button => {
        button.onclick = () => setProductionTaskStatus(button.dataset.productionId, button.dataset.productionStatus);
    });
    document.querySelectorAll('[data-template-job]').forEach(button => {
        button.onclick = () => applyProductionTemplate(button.dataset.templateJob, button.dataset.templateName);
    });
};

async function applyProductionTemplate(jobId, templateName) {
    await json(`/api/jobs/${jobId}/production/template/${encodeURIComponent(templateName)}`, { method:'POST' });
    await showProduction();
}

async function setProductionTaskStatus(id, status) {
    const task = productionTaskCache.get(id);
    if (!task) return;
    await json('/api/production/tasks/' + id, {
        method:'PUT', headers:{'Content-Type':'application/json'},
        body:JSON.stringify({ title:task.title, description:task.description, status, assignedTo:task.assignedTo, dueAtUtc:task.dueAtUtc, sortOrder:task.sortOrder })
    });
    await showProduction();
}

const productionNavigationButton = document.querySelector('.brand-button[data-view="production"]');
if (productionNavigationButton) productionNavigationButton.onclick = () => showProduction();
