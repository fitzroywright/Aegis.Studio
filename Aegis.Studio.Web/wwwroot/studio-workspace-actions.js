// Small compatibility layer for the established Studio workspace controller.
// It fills two action hooks referenced by workspace-outlook.js without replacing
// navigation, job selection, messaging, media, or other existing handlers.

window.openJobDetailsEditor ??= async function openJobDetailsEditor(job) {
    const [access,jobTypes,productionConfig,employees] = await Promise.all([
        json(`/api/jobs/${job.id}/access`),
        json('/api/production-config/job-types').catch(()=>[]),
        json(`/api/jobs/${job.id}/production-config`).catch(()=>({})),
        json('/api/studio-employees?assignable=true').catch(()=>[])
    ]);
    const permissions = access.permissions || access;
    if (!permissions.editJob) {
        alert('You do not have permission to edit this job.');
        return;
    }

    const dialog = studioDialog('Edit Job Details', `
        <form class="subject-form">
            <label>Status
                <input name="status" value="${esc(job.status || '')}" ${permissions.changeStatus ? '' : 'disabled'}>
            </label>
            <label>Job Lead
                <select name="jobLead" ${permissions.assignStaff ? '' : 'disabled'}><option value="">Unassigned</option>${employees.map(e=>`<option value="${esc(e.userName)}" ${String(job.jobLead||'').toLowerCase()===String(e.userName||'').toLowerCase()?'selected':''}>${esc(e.displayName)}${e.roles?.length?' · '+esc(e.roles.join(', ')):''}</option>`).join('')}</select>
            </label>
            <label>Job Type
                <select name="jobType"><option value="">Not set</option>${jobTypes.map(t=>`<option value="${t.id}" ${productionConfig.jobTypeId===t.id?'selected':''}>${esc(t.name)}</option>`).join('')}</select>
            </label>
            <label>Event Date
                <input name="eventDate" type="date" value="${esc(job.eventDate || '')}">
            </label>
            <div class="dialog-actions">
                ${permissions.manageProduction?'<button type="button" class="action" data-generate-production>Generate Production Tasks</button>':''}
                <button type="button" class="action" data-cancel>Cancel</button>
                <button type="submit" class="primary">Save</button>
            </div>
            <p role="alert"></p>
        </form>`);

    const form = dialog.querySelector('form');
    dialog.querySelector('[data-cancel]').onclick = () => dialog.close();

    dialog.querySelector('[data-generate-production]')?.addEventListener('click',async event=>{
        const button=event.currentTarget;button.disabled=true;
        const error=form.querySelector('[role="alert"]');error.textContent='';
        try{
            await json(`/api/jobs/${job.id}/job-type`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({jobTypeId:form.elements.jobType.value||null})});
            const created=await json(`/api/jobs/${job.id}/production/generate`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({recipeId:null})});
            error.textContent=created.length?`Generated ${created.length} production task${created.length===1?'':'s'}.`:'Production tasks already exist for this recipe.';
        }catch(cause){error.textContent=cause.message||String(cause);}
        finally{button.disabled=false;}
    });

    form.onsubmit = async event => {
        event.preventDefault();
        const button = form.querySelector('[type="submit"]');
        const error = form.querySelector('[role="alert"]');
        button.disabled = true;
        error.textContent = '';

        try {
            await json(`/api/jobs/${job.id}`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    status: permissions.changeStatus ? form.elements.status.value : null,
                    assignedTo: null,
                    eventDate: form.elements.eventDate.value || null
                })
            });

            if (permissions.assignStaff) {
                await json(`/api/jobs/${job.id}/lead`, {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName:form.elements.jobLead.value||null})});
            }
            await json(`/api/jobs/${job.id}/job-type`, {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({jobTypeId:form.elements.jobType.value||null})});
            dialog.close();
            await openWorkspaceJob(job.id);
        } catch (cause) {
            error.textContent = cause.message || String(cause);
            button.disabled = false;
        }
    };
};

window.renderJobCalendar ??= async function renderJobCalendar(job, panel, current) {
    const refresh = () => window.renderJobCalendar(job, panel, current);
    await renderJobTasks(job, panel, current, refresh);
    if (!current()) return;
    panel.querySelector('[data-task-view="calendar"]')?.click();
};
