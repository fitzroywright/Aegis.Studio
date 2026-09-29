function messageFirstName(author, people, fallback = 'Team member', loginOnly = false) {
    const value = String(author || '').trim();
    const matches = people.filter(p => [p.userName, p.displayName].some(n => String(n || '').toLowerCase() === value.toLowerCase()));
    const person = matches.find(p => p.givenName || p.firstName || (p.displayName && p.displayName.toLowerCase() !== String(p.userName || '').toLowerCase()));
    const name = String(person?.givenName || person?.firstName || person?.displayName || (!loginOnly && /\s/.test(value) ? value : '')).trim();
    if (!name || /[@\\_.0-9]/.test(name)) return fallback;
    return name.split(/\s+/)[0];
}
function mergeJobMessages(teamMessages, customerMessages) {
    return [
        ...teamMessages.map(m => ({ id: 'team-' + m.id, author: m.senderUserName, body: m.body, at: m.createdAtUtc, customer: false, loginOnly: true })),
        ...customerMessages.map(m => ({ id: 'customer-' + m.id, author: m.author, body: m.body, at: m.createdAt, customer: m.visibility.toLowerCase() !== 'internal' }))
    ].sort((a, b) => new Date(a.at) - new Date(b.at) || a.id.localeCompare(b.id));
}
// Workspace -> job -> messages. Workspace discussions and membership stay in context.
(() => {
    const search = document.getElementById('workspaceSearch');
    let revision = 0;
    let selectedJob = null;
    let tab = 'jobs';
    const baseConversation = openConversation;
    function empty(title, message) {
        detailPane.classList.remove('job-reading-view');
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
    tabs.innerHTML = '<button data-tab="jobs">Jobs</button><button data-tab="members">Members</button>';
    document.querySelector('.workspace-toolbar').before(tabs);
    const banners = document.createElement('section');
    banners.className = 'workspace-announcement-banners';
    banners.hidden = true;
    document.getElementById('conversationView').prepend(banners);
    const display = () => { activateView('conversations'); activateNavigation('workspaces'); };
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
        banners.hidden = true;
        banners.innerHTML = '';
        if (workspaceId) renderAnnouncementBanners(banners,workspaceId,()=>selectedWorkspace===workspaceId).catch(error=>{if(selectedWorkspace===workspaceId){banners.hidden=false;banners.textContent='Announcements could not be loaded. Use Refresh to retry.';}});
        tabs.querySelectorAll('button').forEach(button => button.classList.toggle('active', button.dataset.tab === tab));
        display();
        conversationList.innerHTML = '';
        if (!workspaceId) { empty('Select a workspace', 'Choose a workspace on the left to see its jobs, announcements and members.'); return; }
        empty('Select a job', 'Jobs belong to this workspace. Open a job to read its messages.');
        if (tab === 'members') {
            const members = await json(`/api/workspaces/${workspaceId}/directory`);
            if (version !== revision) return;
            detailPane.classList.remove('empty');
            detailPane.innerHTML = `<div class="detail-header"><h1>Workspace members</h1></div><div class="messages">${members.map(m => `<article class="message"><strong>${esc(m.displayName)}</strong><p>${esc(m.userName)} · ${esc(m.role)}</p></article>`).join('') || '<p>No active members assigned.</p>'}</div>`;
            conversationList.innerHTML = `<div class="empty-state"><h3>${members.length} members</h3><p>People with access to this workspace.</p></div>`; return;
        }
        const [jobs, directory, profile] = await Promise.all([
            json('/api/jobs'),
            json(`/api/workspaces/${workspaceId}/directory`).catch(() => []),
            json('/api/auth/me').catch(() => ({}))
        ]);
        const people = [...directory, profile.user || profile];
        if (version !== revision) return;
        conversationList.innerHTML = jobs.filter(x => x.workspaceId === workspaceId).map(job => `<button class="conversation-card hierarchy-row" data-search data-job="${job.id}"><span class="job-row-heading"><strong>${esc(job.title)}</strong></span><small>${esc(job.clientName)}</small><small>Event: ${esc(job.eventDate || "Not scheduled")}</small><small class="job-card-assignment"><span>Assigned to: ${esc(job.assignedTo ? messageFirstName(job.assignedTo, people, "Team member", true) : "Unassigned")}</span><span class="job-status">${esc(job.status)}</span></small></button>`).join('') || '<p class="empty-state">No jobs in this workspace yet.</p>';
        conversationList.querySelectorAll('[data-job]').forEach(button => button.onclick = () => showJobMessages(jobs.find(x => x.id === button.dataset.job)).catch(fail));
        filter();
    };
    window.openWorkspaceJob = async function(id) {
        const job=await json('/api/jobs/'+id);
        const workspace=workspaceCache.find(w=>w.id===job.workspaceId);
        selectedWorkspace=job.workspaceId;currentWorkspacePermissions=workspace?.permissions || {};workspaceTitle.textContent=workspace?.name || 'Workspace';
        selectedJob=job;tab='jobs';await loadConversations();await showJobMessages(job);
    };
    async function showJobMessages(job) {
        selectedJob = job;
        const version = ++revision;
        conversationList.querySelectorAll('[data-job]').forEach(row => row.classList.toggle('active', row.dataset.job === job.id));
        await thread('Job', job.id, version);
    }
    async function thread(scope, scopeId, version) {
        const value = await json(`/api/messages/threads/${scope}/${scopeId}`, { method:'POST' });
        const messages = await json(`/api/messages/threads/${value.id}?count=500`);
        const job = scope === 'Job' ? selectedJob : null;
        let conversation = null, identities = [], customerNotice = '';
        if (job) {
            const response = await fetch('/api/conversations/' + job.conversationId);
            if (response.ok) {
                conversation = await response.json();
                if (String(conversation.channel).toLowerCase() === 'email') {
                    const senderResponse = await fetch(`/api/conversations/${job.conversationId}/sending-identities`);
                    if (senderResponse.ok) identities = (await senderResponse.json()).identities || [];
                    else customerNotice = 'Sending identities unavailable. You can still post internal messages.';
                }
            } else customerNotice = response.status === 403 ? 'Customer messages are restricted by your permissions.' : 'Customer messages could not be loaded. Refresh to retry.';
        }
        if (version !== revision) return;
        const people = [];
        if (job) {
            const responses = await Promise.allSettled([
                json(`/api/workspaces/${job.workspaceId}/directory`), json('/api/auth/me')
            ]);
            if (responses[0].status === 'fulfilled') people.push(...responses[0].value);
            if (responses[1].status === 'fulfilled') people.push(responses[1].value.user || responses[1].value);
        }
        if (version !== revision) return;
        const combined = mergeJobMessages(messages, conversation?.messages || []);
        detailPane.classList.remove('empty');
        detailPane.innerHTML = `<div class="detail-header"><div><p>${esc(workspaceTitle.textContent)}</p><h1>${esc(job?.title || 'Workspace messages')}</h1><p>${job ? 'Job discussion and customer correspondence' : 'Shared with the workspace team'}</p></div></div><div id="combinedJobMessages" class="messages" aria-live="polite"></div><form id="hierarchyCompose" class="composer"><label for="hierarchyBody">${job ? 'Write a message' : 'Message the entire workspace'}</label><textarea id="hierarchyBody" placeholder="Write a message…" required maxlength="10000"></textarea><div class="send-split"><button class="primary" type="submit">${job ? 'Reply' : 'Send message'}</button>${job ? `<select id="jobSendingIdentity" aria-label="Reply identity" title="Choose how to reply"><option value="Workspace" ${conversation && (String(conversation.channel).toLowerCase() !== 'email' || identities.some(i => i.type === 'Workspace')) ? '' : 'disabled'}>Reply</option><option value="Internal">Reply as internal note</option><option value="Self" ${identities.some(i => i.type === 'Self') ? '' : 'disabled'}>Reply as myself${identities.some(i => i.type === 'Self') ? '' : ' (unavailable)'}</option></select>` : ''}</div><p class="muted">${esc(customerNotice)}</p><p id="hierarchyError" role="alert"></p></form>`;
        const renderMessages = () => {
            const visible = combined;
            document.getElementById('combinedJobMessages').innerHTML = visible.map(m => `<article class="message ${m.customer ? 'customer-exchange' : 'internal-discussion'}"><span class="message-avatar" aria-hidden="true">${esc(messageFirstName(m.author, people, m.customer ? "Customer" : "Team member", m.loginOnly).slice(0,1))}</span><div class="message-content"><div class="message-head"><span class="message-kind">${m.customer ? 'CUSTOMER EXCHANGE' : 'INTERNAL'}</span> ${esc(messageFirstName(m.author, people, m.customer ? 'Customer' : 'Team member', m.loginOnly))} · ${esc(new Date(m.at).toLocaleString())}</div><p style="white-space:pre-wrap">${esc(m.body)}</p></div></article>`).join('') || '<p>No messages to display.</p>';
        };
        renderMessages();
        const sender = document.getElementById('jobSendingIdentity');
        const canSend = () => !job || sender?.value === 'Internal' || (!!conversation && (String(conversation.channel).toLowerCase() !== 'email' ? sender?.value === 'Workspace' : identities.some(i => i.type === sender?.value)));
        const syncSend = () => {
            const button = document.getElementById('hierarchyCompose').querySelector('button');
            button.textContent = !job ? 'Send message' : sender?.value === 'Internal' ? 'Reply as internal note' : sender?.value === 'Self' ? 'Reply as myself' : 'Reply';
            button.disabled = !canSend();
        };
        if(sender) { sender.value = 'Workspace'; sender.onchange = syncSend; }
        syncSend();
        if (scope === 'Job') {
            detailPane.classList.add('job-reading-view');
            const lower = document.createElement('section');
            lower.className = 'job-lower-pane';
            lower.innerHTML = '<nav class="workspace-tabs" aria-label="Job information"><button data-job-tab="gallery">Gallery</button><button data-job-tab="subjects">Subjects</button><button data-job-tab="tasks">Tasks / Calendar</button><button data-job-tab="activity">Activity</button></nav><div class="job-tab-content"></div>';
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
            if (!body || !canSend()) return;
            const button = form.querySelector('button'); button.disabled = true;
            try {
                const external = job && sender?.value !== 'Internal';
                const url = external ? `/api/conversations/${job.conversationId}/messages` : `/api/messages/threads/${value.id}`;
                const payload = external ? {body, isInternal:false, sendingIdentity:sender?.value || 'Workspace'} : {body};
                await json(url, {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
                if (version === revision) await thread(scope, scopeId, version);
            } catch(error) { form.querySelector('[role="alert"]').textContent = error.message || error; }
            finally { if (form.isConnected) syncSend(); }
        };
    }
    async function renderJobTab(name, job, panel, current, refresh) {
        if (name === 'subjects') return renderJobSubjects(job,panel,current,refresh);
        if (name === 'tasks') return renderJobTasks(job,panel,current,refresh);
        if (name === 'activity') return renderJobActivity(job,panel,current);
        if (name === 'gallery') {
            const files = await json(`/api/conversations/${job.conversationId}/attachments`);
            if (!current()) return;
            panel.innerHTML = `<div class="versioned-gallery"></div><details class="legacy-attachments"><summary>Conversation attachments</summary><p>Files attached to this job’s conversation.</p><div class="job-file-gallery">${files.map(file => {
                const url = '/api/attachments/' + encodeURIComponent(file.id);
                const image = /\.(jpe?g|png|gif|webp|avif)$/i.test(file.fileName || '');
                return `<a class="job-file-tile" data-file-id="${esc(file.id)}" href="${url}" target="_blank" rel="noopener">${image ? `<img loading="lazy" src="${url}" alt="${esc(file.fileName)}">` : '<span class="job-file-document" aria-hidden="true">▤</span>'}<strong>${esc(file.fileName)}</strong><small>${esc(file.visibility)} · ${esc(formatBytes(file.size))}</small><span>Open file ↗</span></a>`;
            }).join('') || '<p>No files attached yet.</p>'}</div><form class="job-file-upload"><label>Add files to this job<input type="file" multiple required></label><label>Visibility<select name="visibility"><option value="Internal">Internal</option><option value="External">External</option></select></label><button class="primary">Upload files</button><p role="status"></p></form></details>`;
            const versionHost=panel.querySelector('.versioned-gallery');
            renderVersionedGallery(job,versionHost,current).catch(error=>{if(current())versionHost.textContent=error.message;});
            panel.querySelectorAll('.job-file-tile').forEach(tile => tile.onclick = event => {
                const file = files.find(f => f.id === tile.dataset.fileId);
                if (!/\.(jpe?g|png|gif|webp|avif)$/i.test(file.fileName || '')) return;
                event.preventDefault();
                const dialog = document.createElement('dialog'); dialog.className = 'job-preview-dialog';
                const url = '/api/attachments/' + encodeURIComponent(file.id);
                dialog.innerHTML = `<header><strong>${esc(file.fileName)}</strong><button type="button" aria-label="Close preview">Close</button></header><img src="${url}" alt="${esc(file.fileName)}"><a href="${url}" target="_blank" rel="noopener">Open original</a>`;
                document.body.appendChild(dialog);
                dialog.querySelector('button').onclick = () => dialog.close();
                dialog.addEventListener('close', () => dialog.remove(), {once:true}); dialog.showModal();
            });
            panel.querySelector('.job-file-upload').onsubmit = async event => {
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
    }
    allButton.textContent = 'Jobs';
    allButton.onclick = () => { tab = 'jobs'; loadConversations().catch(fail); };
    document.getElementById('workspaceRefresh').onclick = () => loadConversations().catch(fail);
    document.querySelector('.workspace-toolbar label').textContent = 'Search jobs';
    search.placeholder = 'Search this list';
    document.querySelector('.workspace-list-caption span').textContent = 'Workspace work';
    document.querySelector('.pane-heading p').textContent = 'Jobs, announcements and members';
})();
