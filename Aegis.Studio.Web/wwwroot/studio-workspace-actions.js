// Small compatibility layer for the established Studio workspace controller.
// It fills two action hooks referenced by workspace-outlook.js without replacing
// navigation, job selection, messaging, media, or other existing handlers.

window.openJobDetailsEditor ??= async function openJobDetailsEditor(job) {
    const access = await json(`/api/jobs/${job.id}/access`);
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
            <label>Assigned To
                <input name="assignedTo" value="${esc(job.assignedTo || '')}" ${permissions.assignStaff ? '' : 'disabled'}>
            </label>
            <label>Event Date
                <input name="eventDate" type="date" value="${esc(job.eventDate || '')}">
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
                    assignedTo: permissions.assignStaff ? (form.elements.assignedTo.value || null) : null,
                    eventDate: form.elements.eventDate.value || null
                })
            });

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
