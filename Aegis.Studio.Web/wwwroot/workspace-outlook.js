// Workspace -> job -> messages. Workspace discussions and membership stay in context.
(() => {
    const search = document.getElementById('workspaceSearch');
    let revision = 0;
    let selectedJob = null;
    let tab = 'jobs';
    const baseConversation = openConversation;
    function empty(title, message) {
        detailPane.classList.add('empty');
        detailPane.innerHTML = `<div class="empty-state"><h2>${esc(title)}</h2><p>${esc(message)}</p></div>`;
    }
    function filter() {
        const term = search.value.trim().toLocaleLowerCase();
        conversationList.querySelectorAll('[data-search]').forEach(row => row.hidden = !row.textContent.toLocaleLowerCase().includes(term));
    }
    search.oninput = filter;
    const tabs = document.createElement('div');
    tabs.className = 'workspace-tabs';
    tabs.setAttribute('aria-label', 'Workspace views');
    tabs.innerHTML = '<button data-tab="jobs">Jobs</button><button data-tab="team">Workspace messages</button><button data-tab="members">Members</button><button data-tab="enquiries">Unassigned enquiries</button>';
    document.querySelector('.workspace-toolbar').before(tabs);
    const display = () => { activateView('conversations'); };
    const fail = error => empty('Unable to load', error.message || String(error));
    tabs.querySelectorAll('button').forEach(button => button.onclick = () => { tab = button.dataset.tab; loadConversations().catch(fail); });
    selectWorkspace = async function(id, name) {
        const workspace = workspaceCache.find(x => x.id === id);
        if (!workspace?.permissions?.accessWorkspace || !workspace.permissions.view) return alert('You do not have permission to view this workspace.');
        selectedWorkspace = id; selectedJob = null; selectedConversation = null;
        currentWorkspacePermissions = workspace.permissions;
        tab = 'jobs'; search.value = ''; workspaceTitle.textContent = name;
        document.querySelectorAll('.workspace').forEach(button => button.classList.toggle('active', button.dataset.id === id));
        display();
        try { await loadConversations(); } catch (error) { fail(error); }
    };
    loadConversations = async function() {
        const version = ++revision;
        const workspaceId = selectedWorkspace;
        tabs.querySelectorAll('button').forEach(button => button.classList.toggle('active', button.dataset.tab === tab));
        display();
        conversationList.innerHTML = '';
        if (!workspaceId) { empty('Select a workspace', 'Choose a workspace on the left to see its jobs, messages and members.'); return; }
        empty('Select a job', 'Jobs belong to this workspace. Open a job to read its messages.');
        if (tab === 'team') {
            conversationList.innerHTML = '<div class="empty-state"><h3>Workspace messages</h3><p>A shared discussion for the entire workspace.</p></div>';
            await thread('Workspace', workspaceId, version); return;
        }
        if (tab === 'members') {
            const members = await json(`/api/workspaces/${workspaceId}/directory`);
            if (version !== revision) return;
            detailPane.classList.remove('empty');
            detailPane.innerHTML = `<div class="detail-header"><h1>Workspace members</h1></div><div class="messages">${members.map(m => `<article class="message"><strong>${esc(m.displayName)}</strong><p>${esc(m.userName)} · ${esc(m.role)}</p></article>`).join('') || '<p>No active members assigned.</p>'}</div>`;
            conversationList.innerHTML = `<div class="empty-state"><h3>${members.length} members</h3><p>People with access to this workspace.</p></div>`; return;
        }
        if (tab === 'enquiries') {
            const enquiries = await json(`/api/conversations?workspaceId=${workspaceId}`);
            if (version !== revision) return;
            conversationList.innerHTML = enquiries.filter(x => !x.jobId).map(x => `<button class="conversation-card hierarchy-row" data-search data-conversation="${x.id}"><strong>${esc(x.subject)}</strong><small>${esc(x.contactName)}</small></button>`).join('') || '<p class="empty-state">No unassigned enquiries.</p>';
            conversationList.querySelectorAll('[data-conversation]').forEach(button => button.onclick = () => baseConversation(button.dataset.conversation).catch(fail));
            empty('Unassigned enquiries', 'Open an enquiry to create a job when the work is confirmed.'); filter(); return;
        }
        const jobs = await json('/api/jobs');
        if (version !== revision) return;
        conversationList.innerHTML = jobs.filter(x => x.workspaceId === workspaceId).map(job => `<button class="conversation-card hierarchy-row" data-search data-job="${job.id}"><strong>${esc(job.title)}</strong><small>${esc(job.clientName)} · ${esc(job.status)}</small></button>`).join('') || '<p class="empty-state">No jobs in this workspace. Check Unassigned enquiries to create one.</p>';
        conversationList.querySelectorAll('[data-job]').forEach(button => button.onclick = () => showJobMessages(jobs.find(x => x.id === button.dataset.job)).catch(fail));
        filter();
    };
    async function showJobMessages(job) {
        selectedJob = job;
        const version = ++revision;
        conversationList.querySelectorAll('[data-job]').forEach(row => row.classList.toggle('active', row.dataset.job === job.id));
        await thread('Job', job.id, version);
    }
    async function thread(scope, scopeId, version) {
        const value = await json(`/api/messages/threads/${scope}/${scopeId}`, { method:'POST' });
        const messages = await json(`/api/messages/threads/${value.id}`);
        if (version !== revision) return;
        detailPane.classList.remove('empty');
        detailPane.innerHTML = `<div class="detail-header"><div><p>${esc(workspaceTitle.textContent)}${scope === 'Job' ? ' / ' + esc(selectedJob.title) : ''}</p><h1>${scope === 'Job' ? 'Job messages' : 'Workspace messages'}</h1><p>${scope === 'Job' ? 'Internal discussion for this job' : 'Shared with the workspace team'}</p></div></div><div class="messages">${messages.map(m => `<article class="message"><div class="message-head">${esc(m.senderUserName)} · ${esc(new Date(m.createdAtUtc).toLocaleString())}</div><p style="white-space:pre-wrap">${esc(m.body)}</p></article>`).join('') || '<p>No messages yet. Start the discussion below.</p>'}</div><form id="hierarchyCompose" class="composer"><label for="hierarchyBody">${scope === 'Job' ? 'Message the job team' : 'Message the entire workspace'}</label><textarea id="hierarchyBody" required maxlength="10000"></textarea><button class="primary" type="submit">Send message</button><p id="hierarchyError" role="alert"></p></form>`;
        if (scope === 'Job') {
            detailPane.classList.add('job-reading-view');
            const lower = document.createElement('section');
            lower.className = 'job-lower-pane';
            lower.innerHTML = '<nav class="workspace-tabs" aria-label="Job information"><button data-job-tab="gallery">Attached files</button><button data-job-tab="details">Job details</button><button data-job-tab="customer">Customer messages</button></nav><div class="job-tab-content"></div>';
            detailPane.appendChild(lower);
            const job = selectedJob;
            let tabRevision = 0;
            const activate = async name => {
                const token = ++tabRevision;
                const panel = lower.querySelector('.job-tab-content');
                const current = () => version === revision && token === tabRevision;
                lower.querySelectorAll('[data-job-tab]').forEach(button => button.classList.toggle('active', button.dataset.jobTab === name));
                panel.textContent = 'Loading…';
                try { await renderJobTab(name, job, panel, current, () => activate(name)); }
                catch (error) { if (current()) panel.textContent = 'Unable to load: ' + (error.message || error); }
            };
            lower.querySelectorAll('[data-job-tab]').forEach(button => button.onclick = () => activate(button.dataset.jobTab));
            activate('gallery');
        } else detailPane.classList.remove('job-reading-view');
        document.getElementById('hierarchyCompose').onsubmit = async event => {
            event.preventDefault();
            const form = event.currentTarget, body = form.querySelector('textarea').value.trim();
            if (!body) return;
            const button = form.querySelector('button'); button.disabled = true;
            try {
                await json(`/api/messages/threads/${value.id}`, {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({body})});
                if (version === revision) await thread(scope, scopeId, version);
            } catch(error) { form.querySelector('[role="alert"]').textContent = error.message || error; }
            finally { button.disabled = false; }
        };
    }
    async function renderJobTab(name, job, panel, current, refresh) {
        if (name === 'details') {
            const value = await json('/api/jobs/' + job.id);
            if (!current()) return;
            panel.innerHTML = `<h2>${esc(value.title)}</h2><dl class="job-facts"><dt>Client</dt><dd>${esc(value.clientName)}</dd><dt>Status</dt><dd>${esc(value.status)}</dd><dt>Assigned to</dt><dd>${esc(value.assignedTo || 'Unassigned')}</dd><dt>Event date</dt><dd>${esc(value.eventDate || 'Not scheduled')}</dd></dl><button class="action" id="jobFullDetails">Manage job</button>`;
            panel.querySelector('#jobFullDetails').onclick = () => { ++revision; openJob(job.id).catch(fail); };
            return;
        }
        if (name === 'gallery') {
            const files = await json(`/api/conversations/${job.conversationId}/attachments`);
            if (!current()) return;
            panel.innerHTML = `<h2>Attached files</h2><p>Images and documents attached to this job’s conversation.</p><div class="job-file-gallery">${files.map(file => {
                const url = '/api/attachments/' + encodeURIComponent(file.id);
                const image = /\.(jpe?g|png|gif|webp|avif)$/i.test(file.fileName || '');
                return `<a class="job-file-tile" href="${url}" target="_blank" rel="noopener">${image ? `<img loading="lazy" src="${url}" alt="${esc(file.fileName)}">` : '<span class="job-file-document" aria-hidden="true">▤</span>'}<strong>${esc(file.fileName)}</strong><small>${esc(file.visibility)} · ${esc(formatBytes(file.size))}</small><span>Open file ↗</span></a>`;
            }).join('') || '<p>No files attached yet.</p>'}</div><form class="job-file-upload"><label>Add files to this job<input type="file" multiple required></label><label>Visibility<select name="visibility"><option value="Internal">Internal</option><option value="External">External</option></select></label><button class="primary">Upload files</button><p role="status"></p></form>`;
            panel.querySelector('form').onsubmit = async event => {
                event.preventDefault();
                const form = event.currentTarget, button = form.querySelector('button'); button.disabled = true;
                try {
                    for (const file of form.querySelector('input').files) {
                        const data = new FormData(); data.append('file', file);
                        const response = await fetch(`/api/conversations/${job.conversationId}/attachments?visibility=${encodeURIComponent(form.querySelector('select').value)}`, {method:'POST',body:data});
                        if (!response.ok) throw new Error(await response.text());
                    }
                    if (current()) await refresh();
                } catch(error) { form.querySelector('[role="status"]').textContent = 'Upload stopped: ' + (error.message || error); }
                finally { button.disabled = false; }
            };
            return;
        }
        const conversation = await json('/api/conversations/' + job.conversationId);
        const identities = String(conversation.channel).toLowerCase() === 'email'
            ? (await json(`/api/conversations/${job.conversationId}/sending-identities`)).identities : [];
        if (!current()) return;
        panel.innerHTML = `<h2>Customer messages</h2><p>${esc(conversation.contactName)} · ${esc(conversation.contactAddress)}</p>${conversation.messages.map(m => `<article class="message"><div class="message-head">${esc(m.author)} · ${esc(m.visibility)} · ${esc(new Date(m.createdAt).toLocaleString())}</div><p style="white-space:pre-wrap">${esc(m.body)}</p></article>`).join('')}<form class="job-customer-reply"><label>Reply<textarea required maxlength="10000"></textarea></label><label><input type="checkbox"> Internal note</label><div class="send-split"><button class="primary" type="submit">Send reply</button>${identities.length ? `<select aria-label="Send reply as">${identities.map(i => `<option value="${esc(i.type)}" ${i.isDefault ? 'selected' : ''}>${esc(i.label)}</option>`).join('')}</select>` : ''}</div><p role="status"></p></form>`;
        const form = panel.querySelector('form'), identity = form.querySelector('select'), internal = form.querySelector('input'), send = form.querySelector('button');
        const sync = () => { if(identity) identity.disabled = internal.checked; send.textContent = internal.checked ? 'Add internal note' : identity ? (identity.value === 'Self' ? 'Send as Myself' : 'Send as Workspace') : 'Send reply'; };
        internal.onchange = sync; if(identity) identity.onchange = sync; sync();
        form.onsubmit = async event => {
            event.preventDefault(); const body = form.querySelector('textarea').value.trim(); if(!body) return;
            send.disabled = true;
            try {
                await json(`/api/conversations/${job.conversationId}/messages`, {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({body,isInternal:internal.checked,sendingIdentity:identity?.value || 'Workspace'})});
                if(current()) await refresh();
            } catch(error) { form.querySelector('[role="status"]').textContent = error.message || error; }
            finally { send.disabled = false; }
        };
    }
    allButton.textContent = 'Jobs';
    allButton.onclick = () => { tab = 'jobs'; loadConversations().catch(fail); };
    document.getElementById('workspaceRefresh').onclick = () => loadConversations().catch(fail);
    document.querySelector('.workspace-toolbar label').textContent = 'Search jobs or enquiries';
    search.placeholder = 'Search this list';
    document.querySelector('.workspace-list-caption span').textContent = 'Workspace work';
    document.querySelector('.pane-heading p').textContent = 'Jobs, team messages and members';
})();
