const baseProductionView = typeof showProduction === 'function' ? showProduction : null;

showProduction = async function() {
    activateNavigation('production');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    const [tasks, jobs] = await Promise.all([json('/api/production/queue'), json('/api/jobs')]);
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
            return `<div class="job-row"><div><b>${esc(task.title)}</b><div class="meta">${esc(job?.title || 'Job')} · ${esc(task.assignedTo || 'Unassigned')} · ${esc(task.status)}${task.dueAtUtc ? ' · due '+new Date(task.dueAtUtc).toLocaleString() : ''}</div></div><div><button class="action" onclick="setProductionTaskStatus('${task.id}','InProgress','${esc(task.title)}','${esc(task.assignedTo || '')}',${task.dueAtUtc ? `'${task.dueAtUtc}'` : 'null'},${task.sortOrder})">Start</button><button class="action" onclick="setProductionTaskStatus('${task.id}','Completed','${esc(task.title)}','${esc(task.assignedTo || '')}',${task.dueAtUtc ? `'${task.dueAtUtc}'` : 'null'},${task.sortOrder})">Complete</button></div></div>`;
        }).join('') : '<p class="muted">No production tasks yet. Open a job and apply a workflow template.</p>'}</section>
        <section class="job-section"><h2>Jobs needing a workflow</h2>${jobs.map(job => `<div class="job-row"><div><b>${esc(job.title)}</b><div class="meta">${esc(job.clientName)} · ${esc(job.status)}</div></div><div><button class="action" onclick="applyProductionTemplate('${job.id}','standard')">Standard</button><button class="action" onclick="applyProductionTemplate('${job.id}','graduation')">Graduation</button><button class="action" onclick="applyProductionTemplate('${job.id}','id')">ID</button></div></div>`).join('') || '<p class="muted">No jobs yet.</p>'}</section>`;
};

async function applyProductionTemplate(jobId, templateName) {
    await json(`/api/jobs/${jobId}/production/template/${encodeURIComponent(templateName)}`, { method:'POST' });
    await showProduction();
}

async function setProductionTaskStatus(id, status, title, assignedTo, dueAtUtc, sortOrder) {
    await json('/api/production/tasks/' + id, {
        method:'PUT', headers:{'Content-Type':'application/json'},
        body:JSON.stringify({ title, description:null, status, assignedTo:assignedTo || null, dueAtUtc, sortOrder })
    });
    await showProduction();
}

const productionNavigationButton = document.querySelector('.brand-button[data-view="production"]');
if (productionNavigationButton) productionNavigationButton.onclick = () => showProduction();
