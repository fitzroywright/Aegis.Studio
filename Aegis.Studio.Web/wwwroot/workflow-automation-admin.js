(() => {
    let definitions = [];
    let selectedId = null;
    const triggers = ['JobStatusChanged', 'ScheduleChanged', 'ProductionChanged', 'DeliveryChanged'];
    const escapeHtml = value => StudioPage.escape(String(value ?? ''));
    const pretty = value => {
        try { return JSON.stringify(JSON.parse(value || '{}'), null, 2); }
        catch { return value || '{}'; }
    };
    const formMarkup = (mode, item = null) => {
        let conditions = {};
        try { conditions = JSON.parse(item?.conditionsJson || '{}'); } catch { conditions = {}; }
        const jobStatus = conditions.jobStatus || '';
        delete conditions.jobStatus;
        const actions = item?.actionsJson || '[{"type":"NotifyJob","subject":"Workflow update","body":"A workflow event occurred."}]';
        return '<form class="workflow-admin-form" data-workflow-form="' + mode + '">' +
            '<div class="workflow-admin-form-heading"><h3>' + (mode === 'edit' ? 'Edit workflow' : 'Create workflow') + '</h3>' +
            (mode === 'edit' ? '<button type="button" class="action" data-workflow-cancel>Cancel</button>' : '') + '</div>' +
            '<label>Name<input id="workflowName' + (mode === 'edit' ? 'Edit' : '') + '" name="name" required maxlength="200" value="' + escapeHtml(item?.name || '') + '"></label>' +
            '<label>Description<textarea name="description" rows="2" maxlength="2000">' + escapeHtml(item?.description || '') + '</textarea></label>' +
            '<div class="workflow-admin-form-row"><label>Trigger<select name="triggerEvent">' + triggers.map(value => '<option value="' + value + '" ' + ((item?.triggerEvent || 'JobStatusChanged') === value ? 'selected' : '') + '>' + value + '</option>').join('') + '</select></label>' +
            '<label>Job status condition<input id="workflowStatusCondition' + (mode === 'edit' ? 'Edit' : '') + '" name="jobStatus" value="' + escapeHtml(jobStatus) + '" placeholder="Optional, e.g. Shoot Complete"></label></div>' +
            '<label>Other conditions (JSON object)<textarea name="otherConditions" rows="4" spellcheck="false">' + escapeHtml(JSON.stringify(conditions, null, 2)) + '</textarea><small>Optional. Supports workspaceId, assignedTo and context conditions.</small></label>' +
            '<label>Actions (JSON array)<textarea name="actionsJson" rows="7" required spellcheck="false">' + escapeHtml(pretty(actions)) + '</textarea><small>Actions must contain a supported action, such as NotifyJob, NotifyRequester, CreateActivity or WaitMinutes.</small></label>' +
            '<label class="workflow-admin-enabled"><input name="isEnabled" type="checkbox" ' + (item?.isEnabled === false ? '' : 'checked') + '> Enabled</label>' +
            '<p class="workflow-admin-error" role="alert"></p><div class="workflow-admin-form-actions"><button class="primary" type="submit">' + (mode === 'edit' ? 'Save changes' : 'Create workflow') + '</button></div></form>';
    };
    window.renderWorkflowSection = function () {
        return '<section class="workflow-admin" aria-label="Workflow automation">' +
            '<p class="workflow-admin-intro">Database-backed event, condition and action workflows. Select an automation to review its details, then choose Edit to change it.</p>' +
            '<div class="workflow-admin-layout"><section class="workflow-admin-list-panel" aria-label="Existing workflows">' +
            '<div class="workflow-admin-list-heading"><h3>Existing workflows</h3><button type="button" class="action" data-workflow-refresh>Refresh</button></div>' +
            '<div id="workflowList" class="workflow-admin-list" aria-live="polite"><p role="status">Loading workflows…</p></div></section>' +
            '<section id="workflowDetails" class="workflow-admin-details" aria-label="Workflow details" hidden></section></div>' +
            '<details class="workflow-admin-create"><summary>Create workflow</summary>' + formMarkup('create') + '</details>' +
            '<section class="workflow-admin-audit"><button type="button" class="action" data-workflow-audit>Run authorization audit</button><p id="authorizationAuditResult" role="status"></p></section></section>';
    };
    function renderList() {
        const list = document.getElementById('workflowList');
        if (!list) return;
        list.innerHTML = definitions.length ? definitions.map(item =>
            '<button type="button" class="workflow-admin-entry" data-workflow-open="' + escapeHtml(item.id) + '" aria-pressed="' + (String(item.id) === String(selectedId)) + '">' +
            '<span class="workflow-admin-entry-copy"><strong>' + escapeHtml(item.name) + '</strong><small>When ' + escapeHtml(item.triggerEvent) + ' · ' + (item.isEnabled ? 'Enabled' : 'Disabled') + '</small></span><span aria-hidden="true">›</span></button>'
        ).join('') : '<p>No workflow automations yet. Create one below.</p>';
        list.querySelectorAll('[data-workflow-open]').forEach(button => button.onclick = () => showDetails(button.dataset.workflowOpen));
    }
    function showDetails(id) {
        const item = definitions.find(value => String(value.id) === String(id));
        if (!item) return;
        selectedId = item.id;
        renderList();
        const panel = document.getElementById('workflowDetails');
        if (!panel) return;
        panel.hidden = false;
        panel.innerHTML = '<div class="workflow-admin-detail-heading"><div><p class="workflow-admin-kicker">Workflow details</p><h3>' + escapeHtml(item.name) + '</h3></div><button type="button" class="primary" data-workflow-edit>Edit</button></div>' +
            '<p>' + escapeHtml(item.description || 'No description provided.') + '</p><dl class="workflow-admin-facts"><div><dt>Trigger</dt><dd>' + escapeHtml(item.triggerEvent) + '</dd></div><div><dt>Status</dt><dd>' + (item.isEnabled ? 'Enabled' : 'Disabled') + '</dd></div></dl>' +
            '<h4>Conditions</h4><pre>' + escapeHtml(pretty(item.conditionsJson)) + '</pre><h4>Actions</h4><pre>' + escapeHtml(pretty(item.actionsJson)) + '</pre>' +
            '<div class="workflow-admin-detail-actions"><button type="button" class="action" data-workflow-delete>Delete</button></div>';
        panel.querySelector('[data-workflow-edit]').onclick = () => showEditor(item);
        panel.querySelector('[data-workflow-delete]').onclick = () => window.deleteWorkflowDefinition(item.id);
    }
    function showEditor(item) {
        const panel = document.getElementById('workflowDetails');
        panel.innerHTML = formMarkup('edit', item);
        bindForm(panel.querySelector('form'), item.id);
    }
    async function saveForm(form, id = null) {
        const error = form.querySelector('[role="alert"]');
        error.textContent = '';
        try {
            const data = new FormData(form);
            const other = JSON.parse(String(data.get('otherConditions') || '{}'));
            if (!other || Array.isArray(other) || typeof other !== 'object') throw new Error('Other conditions must be a JSON object.');
            const conditions = {...other};
            const status = String(data.get('jobStatus') || '').trim();
            if (status) conditions.jobStatus = status;
            else delete conditions.jobStatus;
            const actions = JSON.parse(String(data.get('actionsJson') || ''));
            if (!Array.isArray(actions) || !actions.length) throw new Error('Actions must be a non-empty JSON array.');
            const payload = {
                name: String(data.get('name') || '').trim(),
                description: String(data.get('description') || '').trim() || null,
                triggerEvent: String(data.get('triggerEvent') || ''),
                conditionsJson: JSON.stringify(conditions),
                actionsJson: JSON.stringify(actions),
                isEnabled: form.elements.isEnabled.checked
            };
            if (!payload.name) throw new Error('Workflow name is required.');
            await json(id ? '/api/workflows/' + encodeURIComponent(id) : '/api/workflows/', {
                method: id ? 'PUT' : 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(payload)
            });
            await window.refreshWorkflowList();
            if (id) showDetails(id);
            else form.closest('details')?.removeAttribute('open');
        } catch (failure) {
            error.textContent = failure.message || 'The workflow could not be saved.';
        }
    }
    function bindForm(form, id = null) {
        form.onsubmit = event => { event.preventDefault(); return saveForm(form, id); };
        form.querySelector('[data-workflow-cancel]')?.addEventListener('click', () => showDetails(id));
    }
    window.refreshWorkflowList = async function () {
        definitions = await json('/api/workflows/');
        renderList();
        const panel = document.getElementById('workflowDetails');
        if (selectedId && definitions.some(item => String(item.id) === String(selectedId))) showDetails(selectedId);
        else if (panel) { panel.hidden = true; panel.innerHTML = ''; }
        const host = document.querySelector('[data-template-automation]');
        if (host && window.StudioPage?.setCount) StudioPage.setCount(definitions.length + ' workflows');
        const createForm = document.querySelector('[data-workflow-form="create"]');
        if (createForm) bindForm(createForm);
        const refresh = document.querySelector('[data-workflow-refresh]');
        if (refresh) refresh.onclick = () => window.refreshWorkflowList().catch(error => alert(error.message));
        const audit = document.querySelector('[data-workflow-audit]');
        if (audit) audit.onclick = () => window.runAuthorizationAudit().catch(error => alert(error.message));
    };
    window.createWorkflowDefinition = async function () {
        const form = document.querySelector('[data-workflow-form="create"]');
        if (form) await saveForm(form);
    };
    window.deleteWorkflowDefinition = async function (id) {
        if (!confirm('Delete this workflow automation?')) return;
        const response = await fetch('/api/workflows/' + encodeURIComponent(id), {method:'DELETE', credentials:'same-origin'});
        if (!response.ok) throw new Error('Workflow could not be deleted.');
        selectedId = null;
        await window.refreshWorkflowList();
    };
    window.runAuthorizationAudit = async function () {
        const result = await json('/api/security/authorization-audit');
        const target = document.getElementById('authorizationAuditResult');
        if (target) target.textContent = result.apiEndpointCount + ' API endpoints checked; ' + (result.unapprovedAnonymous.length ? result.unapprovedAnonymous.length + ' need review' : 'none need review') + '.';
    };
})();
