// Small compatibility layer for the established Studio workspace controller.
// It fills two action hooks referenced by workspace-outlook.js without replacing
// navigation, job selection, messaging, media, or other existing handlers.

window.openJobDetailsEditor ??= async function openJobDetailsEditor(job) {
    const [access,productionConfig,jobTypes,recipes] = await Promise.all([json(`/api/jobs/${job.id}/access`),json(`/api/jobs/${job.id}/production-configuration`).catch(()=>({})),json('/api/production/job-types?active=true'),json('/api/production/recipes?active=true')]);
    const permissions = access.permissions || access;
    const jobTypeOptions='<option value="">Unspecified</option>'+jobTypes.map(x=>`<option value="${esc(x.id)}" ${String(productionConfig.jobTypeId||'')===String(x.id)?'selected':''}>${esc(x.name)}</option>`).join('');
    const recipeOptions='<option value="">Use Job Type default</option>'+recipes.map(x=>`<option value="${esc(x.id)}" ${String(productionConfig.productionRecipeId||'')===String(x.id)?'selected':''}>${esc(x.name)}</option>`).join('');
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
                <input name="jobLead" value="${esc(job.jobLead || '')}" ${permissions.assignStaff ? '' : 'disabled'}>
            </label>
            <label>Event Date
                <input name="eventDate" type="date" value="${esc(job.eventDate || '')}">
            </label>
            <label>Job Type
                <select name="jobTypeId">${jobTypeOptions}</select>
            </label>
            <label>Production Recipe
                <select name="productionRecipeId">${recipeOptions}</select>
            </label>
            <div class="dialog-actions">
                <button type="button" class="action" data-cancel>Cancel</button>
                <button type="submit" class="primary">Save</button>
            </div>
            <p role="alert"></p>
        </form>`);

    const form = dialog.querySelector('form');
    dialog.querySelector('[data-cancel]').onclick = () => dialog.close();

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
            if (permissions.manageProduction) {
                await json(`/api/jobs/${job.id}/production-configuration`, {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({jobTypeId:form.elements.jobTypeId.value||null,productionRecipeId:form.elements.productionRecipeId.value||null})});
            }
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
