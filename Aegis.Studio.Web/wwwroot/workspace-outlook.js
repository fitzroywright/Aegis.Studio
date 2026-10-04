function messageFirstName(author, people, fallback = 'Team member', loginOnly = false) {
    const value = String(author || '').trim();
    if (!value) return fallback;
    const match = people.find(p => [p.userName, p.displayName].some(n => String(n || '').toLowerCase() === value.toLowerCase()));
    return studioFirstName(match || value);
}
function studioWorkspaceIcon(name) {
    const paths={
        messages:'<path d="M4 5h16v11H8l-4 4V5Z"/><path d="M8 9h8M8 12h5"/>',
        media:'<rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="9" cy="10" r="2"/><path d="m4 17 5-5 4 4 3-3 4 4"/>',
        subjects:'<circle cx="9" cy="8" r="3"/><circle cx="17" cy="9" r="2.5"/><path d="M3 20c0-4 2.4-6 6-6s6 2 6 6M14 15c3.5.2 5 1.8 5 5"/>',
        tasks:'<rect x="4" y="3" width="16" height="18" rx="2"/><path d="m8 9 2 2 4-4M8 15h8"/>',
        calendar:'<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M7 3v4M17 3v4M3 10h18"/>',
        activity:'<path d="M3 12h4l2-5 4 10 2-5h6"/>',
        clock:'<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
        camera:'<path d="M4 7h3l1.5-2h7L17 7h3a2 2 0 0 1 2 2v9H2V9a2 2 0 0 1 2-2Z"/><circle cx="12" cy="13" r="4"/>',
        user:'<circle cx="12" cy="8" r="4"/><path d="M4 21c.7-5 3.3-7 8-7s7.3 2 8 7"/>',
        trend:'<path d="M4 18 9 13l4 3 7-9"/><path d="M15 7h5v5"/>',
        files:'<path d="M6 2h8l4 4v16H6z"/><path d="M14 2v5h5"/>',
        search:'<circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/>'
    };
    return '<svg class="studio-ui-icon" aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">'+(paths[name]||paths.activity)+'</svg>';
}

function studioStatusProgress(status) {
    const value=String(status||'').toLowerCase();
    if(value.includes('deliver')||value.includes('publish')||value.includes('complete'))return 100;
    if(value.includes('production')||value.includes('approved'))return 82;
    if(value.includes('review')||value.includes('select'))return 68;
    if(value.includes('upload')||value.includes('shoot'))return 50;
    if(value.includes('schedul'))return 32;
    if(value.includes('confirm')||value.includes('deposit'))return 22;
    return 12;
}

function mergeJobMessages(teamMessages, customerMessages) {
    return [
        ...teamMessages.map(m => ({ id: 'team-' + m.id, sourceId:m.id, sourceKind:'JobMessage', author: m.senderUserName, body: m.body, at: m.createdAtUtc, customer: false, loginOnly: true })),
        ...customerMessages.map(m => ({ id: 'customer-' + m.id, sourceId:m.id, sourceKind:'ConversationMessage', author: m.author, body: m.body, at: m.createdAt, customer: m.visibility.toLowerCase() !== 'internal' }))
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
        detailPane.classList.remove('job-reading-view'); document.getElementById('conversationView').classList.remove('has-open-job');
        detailPane.classList.add('empty');
        detailPane.innerHTML = `<div class="empty-state"><h2>${esc(title)}</h2><p>${esc(message)}</p></div>`;
    }
    function filter() {
        const term = search.value.trim().toLocaleLowerCase();
        const container=tab==='members'?detailPane:conversationList;
        container.querySelectorAll('[data-search]').forEach(row => row.hidden = !row.textContent.toLocaleLowerCase().includes(term));
        const noMatches=container.querySelector('[data-no-member-matches]');
        if(noMatches)noMatches.hidden=!container.querySelector('[data-search]')||!!container.querySelector('[data-search]:not([hidden])');
    }
    search.oninput = filter;
    const tabs = document.createElement('div');
    tabs.className = 'workspace-tabs workspace-list-controls';
    tabs.setAttribute('aria-label', 'Workspace views');
    tabs.innerHTML = '<button data-tab="jobs">Jobs</button><span class="workspace-member-avatars" aria-label="Workspace members"></span><button class="workspace-settings-action" aria-label="Workspace settings" title="Workspace settings">⚙</button>';
    document.querySelector('.workspace-toolbar').before(tabs);
    const searchBox=document.createElement('div');
    searchBox.className='workspace-inline-search';
    search.setAttribute('aria-label','Search jobs');
    search.placeholder='Search jobs…';
    const refreshButton=document.getElementById('workspaceRefresh');
    refreshButton.setAttribute('aria-label','Refresh jobs');
    refreshButton.title='Refresh jobs';
    refreshButton.innerHTML='<svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M20 7v5h-5M4 17v-5h5M6 7a7 7 0 0 1 12-1l2 3M4 15l2 3a7 7 0 0 0 12-1"/></svg>';
    searchBox.append(search,refreshButton);
    tabs.querySelector('.workspace-member-avatars').before(searchBox);
    tabs.querySelector('.workspace-settings-action').onclick=()=>{if(selectedWorkspace)openWorkspaceSettings(selectedWorkspace).catch(fail);};
    document.querySelector('.workspace-toolbar').remove();
    allButton.remove();
    function syncSearchField(){
        const active=tabs.querySelector('[data-tab="'+tab+'"]');
        active.after(searchBox);
        tabs.dataset.searchScope=tab;
        search.placeholder=tab==='members'?'Search members…':'Search jobs…';
        search.setAttribute('aria-label',tab==='members'?'Search workspace members':'Search jobs');
        refreshButton.title=tab==='members'?'Refresh members':'Refresh jobs';
        refreshButton.setAttribute('aria-label',refreshButton.title);
    }
    syncSearchField();
    const banners = document.createElement('section');
    banners.className = 'workspace-announcement-banners';
    banners.hidden = true;
    document.getElementById('conversationView').prepend(banners);
    const display = () => { activateView('conversations'); activateNavigation('workspaces'); };
    const fail = error => empty('Unable to load', error.message || String(error));
    tabs.querySelectorAll('[data-tab]').forEach(button => button.onclick = () => { tab = button.dataset.tab; search.value=''; syncSearchField(); search.focus(); loadConversations().catch(fail); });
    selectWorkspace = async function(id, name) {
        const workspace = workspaceCache.find(x => x.id === id);
        if (!workspace?.permissions?.accessWorkspace || !workspace.permissions.view) return alert('You do not have permission to view this workspace.');
        window.studioRememberView?.({view:'workspaces',workspaceId:id,jobId:null});
        selectedWorkspace = id; selectedJob = null; selectedConversation = null;
        currentWorkspacePermissions = workspace.permissions;
        tab = 'jobs'; search.value = ''; workspaceTitle.textContent = name;
        document.querySelectorAll('.workspace').forEach(button => button.classList.toggle('active', button.dataset.id === id));
        display();
        try { await loadConversations(); } catch (error) { fail(error); }
    };
    loadConversations = async function() {
        syncSearchField();
        const version = ++revision;
        const workspaceId = selectedWorkspace;
        banners.hidden = true;
        banners.innerHTML = '';
        if (workspaceId) renderAnnouncementBanners(banners,workspaceId,()=>selectedWorkspace===workspaceId).catch(error=>{if(selectedWorkspace===workspaceId){banners.hidden=false;banners.textContent='Announcements could not be loaded. Use Refresh to retry.';}});
        tabs.querySelectorAll('[data-tab]').forEach(button => button.classList.toggle('active', button.dataset.tab === tab));
        display();
        conversationList.innerHTML = '';
        if (!workspaceId) { empty('Select a workspace', 'Choose a workspace on the left to see its jobs, announcements and members.'); return; }
        empty('Select a job', 'Jobs belong to this workspace. Open a job to read its messages.');
        const [jobs, directory, profile] = await Promise.all([
            json('/api/jobs'),
            json(`/api/workspaces/${workspaceId}/directory`).catch(() => []),
            json('/api/auth/me').catch(() => ({}))
        ]);
        const people = [...directory, profile.user || profile];
        studioRememberPeople(people);
        tabs.querySelector('.workspace-member-avatars').innerHTML=directory.slice(0,5).map(m=>`<span title="${esc(studioFirstName(m))}">${esc(studioFirstName(m).slice(0,1))}</span>`).join('');
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
        window.studioRememberView?.({view:'workspaces',workspaceId:job.workspaceId,jobId:job.id});
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
            } else {
                customerNotice = response.status === 403
                    ? 'Customer messages are restricted by your permissions.'
                    : 'Customer messages could not be loaded. Refresh to retry.';
            }
        }

        if (version !== revision) return;

        const people = [];
        if (job) {
            const responses = await Promise.allSettled([
                json(`/api/workspaces/${job.workspaceId}/directory`),
                json('/api/auth/me')
            ]);
            if (responses[0].status === 'fulfilled') people.push(...responses[0].value);
            if (responses[1].status === 'fulfilled') people.push(responses[1].value.user || responses[1].value);
            studioRememberPeople(people);
        }

        if (version !== revision) return;

        const combined = mergeJobMessages(messages, conversation?.messages || []);
        const workspaceName = job
            ? (workspaceCache.find(w => w.id === job.workspaceId)?.name || 'Workspace')
            : 'Workspace';

        detailPane.classList.remove('empty');
        document.getElementById('conversationView').classList.toggle('has-open-job', !!job);
        detailPane.classList.toggle('job-reading-view', !!job);

        if (!job) {
            detailPane.innerHTML = `
                <div class="detail-header">
                    <div><h1>Workspace messages</h1><p>Shared with the workspace team</p></div>
                </div>
                <div id="combinedJobMessages" class="messages" aria-live="polite"></div>
                <form id="hierarchyCompose" class="composer">
                    <label for="hierarchyBody">Message the entire workspace</label>
                    <div class="reply-textbox-shell"><textarea id="hierarchyBody" placeholder="Write a message…" required maxlength="10000"></textarea></div>
                    <div class="send-split"><button class="primary" type="submit">Send message</button></div>
                    <p id="hierarchyError" role="alert"></p>
                </form>`;
        } else {
            detailPane.innerHTML = `
                <div class="job-breadcrumb">Workspaces / ${esc(workspaceName)} <span aria-hidden="true">›</span> <strong>${esc(job.title)}</strong></div>

                <header class="studio-job-header">
                    <div class="studio-job-identity">
                        <div class="studio-job-thumb" data-job-cover aria-hidden="true">
                            <span>${esc((job.title || 'J').slice(0,1))}</span>
                        </div>
                        <div class="studio-job-copy">
                            <div class="studio-job-title-line"><h1>${esc(job.title)}</h1></div>
                            <div class="studio-job-client">${esc(job.linkedClientName || job.clientName || 'Client not linked')}</div>
                            <div class="studio-job-meta">
                                <span>${studioWorkspaceIcon('calendar')}<b data-job-date>${esc(job.eventDate || 'Needs scheduling')}</b></span>
                                <span>${studioWorkspaceIcon('activity')}<b data-job-location>Location not set</b></span>
                                <span class="studio-status-pill" data-status="${esc(job.status)}">${esc(job.status)}</span>
                            </div>
                        </div>
                    </div>
                    <div class="job-header-actions">
                        <button class="action" type="button" data-message-action>${studioWorkspaceIcon('messages')}<span>Message</span></button>
                        <button class="action" type="button" data-add-task>Add Task</button>
                        <button class="action" type="button" data-more-action>More <span aria-hidden="true">⌄</span></button>
                    </div>
                </header>

                <nav class="studio-job-tabs job-section-tabs" aria-label="Job information">
                    <button data-job-tab="messages"><span>Messages</span></button>
                    <button data-job-tab="gallery"><span>Media</span></button>
                    <button data-job-tab="subjects"><span>Subjects</span></button>
                    <button data-job-tab="tasks"><span>Tasks</span></button>
                    <button data-job-tab="calendar"><span>Calendar</span></button>
                    <button data-job-tab="activity"><span>Activity</span></button>
                </nav>

                <div class="studio-job-board job-work-board">
                    <section class="studio-job-messages job-message-column" aria-label="Messages">
                        <header class="job-panel-heading">
                            <div><strong>Messages</strong><span class="panel-count">${combined.length}</span></div>
                            <div class="panel-heading-actions">
                                <button type="button" class="icon-button" data-message-search aria-label="Search messages" title="Search messages">${studioWorkspaceIcon('search')}</button>
                                <button type="button" class="icon-button" aria-label="More message options" title="More message options">⋮</button>
                            </div>
                        </header>
                        <div id="combinedJobMessages" class="messages" aria-live="polite"></div>
                        <form id="hierarchyCompose" class="composer compact-composer">
                            <div class="reply-textbox-shell">
                                <textarea id="hierarchyBody" rows="1" placeholder="Type a message…" required maxlength="10000"></textarea>
                                <label class="reply-paperclip" title="Attach document" aria-label="Attach document">
                                    <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M21.4 11.6 12 21a6 6 0 0 1-8.5-8.5l9.9-9.9a4 4 0 0 1 5.7 5.7l-10 10a2 2 0 0 1-2.8-2.8l9.2-9.2"/></svg>
                                    <input type="file" data-message-file hidden>
                                </label>
                            </div>
                            <div class="send-split">
                                <select id="jobSendingIdentity" aria-label="Reply identity" title="Choose how to reply">
                                    <option value="Workspace" ${conversation && (String(conversation.channel).toLowerCase() !== 'email' || identities.some(i => i.type === 'Workspace')) ? '' : 'disabled'}>Reply</option>
                                    <option value="Internal">Internal note</option>
                                    <option value="Self" ${identities.some(i => i.type === 'Self') ? '' : 'disabled'}>As myself</option>
                                </select>
                                <button class="primary send-icon-button" type="submit" aria-label="Send message">➤</button>
                            </div>
                            <p class="muted">${esc(customerNotice)}</p>
                            <p id="hierarchyError" role="alert"></p>
                        </form>
                    </section>

                    <section class="studio-job-main job-lower-pane" aria-label="Job content">
                        <div class="media-toolbar-host" aria-label="Media toolbar"></div>
                        <div class="gallery-tab-filter"></div>
                        <div class="job-tab-content"></div>
                    </section>

                    <aside class="studio-job-context job-context-sidebar">
                        <section class="thread-next-tasks"></section>

                        <section class="job-details-card">
                            <header class="context-card-header"><strong>Job Details</strong><button class="action text-button" type="button" data-edit-details>Edit</button></header>
                            <dl>
                                <dt>Workspace</dt><dd>${esc(workspaceName)}</dd>
                                <dt>Status</dt><dd><span class="studio-status-pill" data-status="${esc(job.status)}">${esc(job.status)}</span></dd>
                                <dt>Event Date</dt><dd>${esc(job.eventDate || 'Needs scheduling')}</dd>
                                <dt>Location</dt><dd data-details-location>Location not set</dd>
                                <dt>Customer</dt><dd>${esc(job.linkedClientName || job.clientName || 'Not linked')}</dd>
                                <dt>Assigned To</dt><dd>${esc(job.assignedTo ? studioFirstName(job.assignedTo) : 'Unassigned')}</dd>
                            </dl>
                        </section>

                        <section class="thread-shared-files"></section>
                    </aside>
                </div>`;
        }

        const messagesHost = document.getElementById('combinedJobMessages');
        messagesHost.innerHTML = combined.map(m => `
            <article class="message ${m.customer ? 'customer-exchange' : 'internal-discussion'}" data-message-id="${esc(m.sourceId || '')}">
                <span class="message-avatar" aria-hidden="true">${esc(messageFirstName(m.author, people, m.customer ? 'Customer' : 'Team member', m.loginOnly).slice(0,1))}</span>
                <div class="message-content">
                    <div class="message-head"><strong>${esc(messageFirstName(m.author, people, m.customer ? 'Customer' : 'Team member', m.loginOnly))}</strong><time>${esc(new Date(m.at).toLocaleString([], {month:'short',day:'numeric',hour:'numeric',minute:'2-digit'}))}</time></div>
                    <p>${esc(m.body)}</p>
                </div>
            </article>`).join('') || '<p>No messages to display.</p>';

        if(job){
            const searchButton=detailPane.querySelector('[data-message-search]');
            if(searchButton) searchButton.onclick=()=>{
                const term=prompt('Search this conversation');
                if(term===null)return;
                const value=term.trim().toLowerCase();
                detailPane.querySelectorAll('#combinedJobMessages .message').forEach(row=>{
                    row.hidden=!!value && !row.textContent.toLowerCase().includes(value);
                });
            };
            const moreButton=detailPane.querySelector('.panel-heading-actions .icon-button:last-child');
            if(moreButton) moreButton.onclick=()=>{
                const dialog=studioDialog('Message options',`
                    <div class="job-actions">
                        <button type="button" class="action" data-focus-compose>Write message</button>
                        <button type="button" class="action" data-show-all>Show all messages</button>
                    </div>`);
                dialog.querySelector('[data-focus-compose]').onclick=()=>{dialog.close();detailPane.querySelector('#hierarchyBody')?.focus();};
                dialog.querySelector('[data-show-all]').onclick=()=>{detailPane.querySelectorAll('#combinedJobMessages .message').forEach(row=>row.hidden=false);dialog.close();};
            };
        }

        const sender = document.getElementById('jobSendingIdentity');
        const canSend = () => !job
            || sender?.value === 'Internal'
            || (!!conversation && (String(conversation.channel).toLowerCase() !== 'email'
                ? sender?.value === 'Workspace'
                : identities.some(i => i.type === sender?.value)));

        const syncSend = () => {
            const button = document.getElementById('hierarchyCompose').querySelector('button');
            if(!job) button.textContent='Send message';
            else if(!button.classList.contains('send-icon-button')) button.textContent = sender?.value === 'Internal' ? 'Reply as internal note' : sender?.value === 'Self' ? 'Reply as myself' : 'Reply';
            button.disabled = !canSend();
        };

        if (sender) {
            sender.value = 'Workspace';
            sender.onchange = syncSend;
        }
        syncSend();

        if (job) {
            const nextPanel = detailPane.querySelector('.thread-next-tasks');
            const filesPanel = detailPane.querySelector('.thread-shared-files');
            const lower = detailPane.querySelector('.job-lower-pane');
            const current = () => version === revision;

            detailPane.querySelectorAll('[data-edit-details]').forEach(button => {
                button.onclick = () => openJobDetailsEditor(job);
            });

            detailPane.querySelector('[data-message-action]').onclick = () => {
                detailPane.querySelector('#hierarchyBody')?.focus();
            };
            detailPane.querySelector('[data-add-task]').onclick = () => {
                const dialog=studioDialog('Job tasks','<div class="all-tasks"></div>');
                const panel=dialog.querySelector('.all-tasks');
                const refresh=()=>renderJobTasks(job,panel,()=>dialog.isConnected,refresh);
                refresh().catch(error=>panel.textContent=error.message);
            };
            detailPane.querySelector('[data-more-action]').onclick = () => {
                const dialog=studioDialog('Job actions',`
                    <div class="job-actions mockup-more-actions">
                        <button type="button" class="action" data-more-edit>Edit Details</button>
                        <button type="button" class="action" data-more-tasks>View Tasks</button>
                        <button type="button" class="action" data-more-activity>View Activity</button>
                    </div>`);
                dialog.querySelector('[data-more-edit]').onclick=()=>{dialog.close();openJobDetailsEditor(job);};
                dialog.querySelector('[data-more-tasks]').onclick=()=>{dialog.close();detailPane.querySelector('[data-job-tab="tasks"]')?.click();};
                dialog.querySelector('[data-more-activity]').onclick=()=>{dialog.close();detailPane.querySelector('[data-job-tab="activity"]')?.click();};
            };

            const dateWindowStart=new Date();
            dateWindowStart.setFullYear(dateWindowStart.getFullYear()-2);
            const dateWindowEnd=new Date();
            dateWindowEnd.setFullYear(dateWindowEnd.getFullYear()+3);

            Promise.allSettled([
                json(`/api/jobs/${job.id}/media`),
                json(`/api/schedule/entries?jobId=${job.id}&from=${encodeURIComponent(dateWindowStart.toISOString())}&to=${encodeURIComponent(dateWindowEnd.toISOString())}`)
            ]).then(results => {
                if(!current()) return;
                const media = results[0].status === 'fulfilled' ? (results[0].value.assets || []) : [];
                const schedules = results[1].status === 'fulfilled' ? results[1].value : [];
                const images = media.filter(a => String(a.contentType || '').startsWith('image/'));
                const cover=detailPane.querySelector('[data-job-cover]');
                const first=images[0];
                if(cover && first?.versionId) cover.innerHTML=`<img src="/api/jobs/${job.id}/versions/by-id/${first.versionId}/content" alt="">`;
                const schedule=schedules.find(x=>x.location)||schedules[0];
                const location=schedule?.location || 'Location not set';
                const locationHeader=detailPane.querySelector('[data-job-location]');
                const locationDetails=detailPane.querySelector('[data-details-location]');
                if(locationHeader) locationHeader.textContent=location;
                if(locationDetails) locationDetails.textContent=location;
            });

            renderCompactTasks(job, nextPanel, current)
                .catch(error => { if (current()) nextPanel.textContent = 'Upcoming work could not be loaded: ' + error.message; });

            const refreshFiles = () => renderConversationFiles(job, filesPanel, current, refreshFiles);
            refreshFiles()
                .catch(error => { if (current()) filesPanel.textContent = 'Conversation files could not be loaded: ' + error.message; });

            let tabRevision = 0;
            const activate = async name => {
                const token = ++tabRevision;
                const panel = lower.querySelector('.job-tab-content');
                const tabCurrent = () => version === revision && token === tabRevision;

                detailPane.querySelectorAll('[data-job-tab]').forEach(button => {
                    button.classList.toggle('active', button.dataset.jobTab === name);
                });

                detailPane.dataset.jobSection = name;
                lower.querySelector('.media-toolbar-host').hidden = name !== 'gallery';
                lower.querySelector('.gallery-tab-filter').replaceChildren();
                panel.textContent = 'Loading…';

                try {
                    if (name === 'gallery') await renderJobMediaOverview(job, panel, tabCurrent);
                    else if (name === 'messages') {
                        panel.innerHTML='<div class="message-tab-placeholder"><strong>Conversation</strong><p>Use the Messages panel to read and reply while keeping the job context visible.</p></div>';
                    } else {
                        await renderJobTab(name, job, panel, tabCurrent, () => activate(name));
                    }
                } catch (error) {
                    if (tabCurrent()) panel.textContent = 'Unable to load: ' + (error.message || error);
                }
            };

            detailPane.querySelectorAll('[data-job-tab]').forEach(button => {
                button.onclick = () => activate(button.dataset.jobTab);
            });

            activate('gallery');
        }

        let pendingSend = null, pendingAttachment = null;
        document.getElementById('hierarchyCompose').onsubmit = async event => {
            event.preventDefault();
            const form = event.currentTarget;
            const body = form.querySelector('textarea').value.trim();
            if ((!body && !pendingSend) || !canSend()) return;

            const button = form.querySelector('button');
            button.disabled = true;

            try {
                const external = job && sender?.value !== 'Internal';
                const url = external
                    ? `/api/conversations/${job.conversationId}/messages`
                    : `/api/messages/threads/${value.id}`;
                const payload = external
                    ? { body, isInternal:false, sendingIdentity:sender?.value || 'Workspace' }
                    : { body };

                const sent = pendingSend || await json(url, {
                    method:'POST',
                    headers:{'Content-Type':'application/json'},
                    body:JSON.stringify(payload)
                });

                pendingSend = sent;
                form.querySelector('textarea').readOnly = true;
                if (sender) sender.disabled = true;

                const file = job ? form.querySelector('[data-message-file]')?.files?.[0] : null;
                if (file) {
                    const messageId = external ? sent.messageId : sent.id;
                    if (!messageId) throw new Error('Message was sent but Studio did not return its message id, so the attachment was not uploaded.');

                    const data = new FormData();
                    data.append('file', file);

                    if (!pendingAttachment) {
                        pendingAttachment = await json(
                            '/api/conversations/' + job.conversationId + '/attachments?visibility=' + encodeURIComponent(external ? 'External' : 'Internal'),
                            { method:'POST', body:data });
                    }

                    const link = await fetch('/api/attachments/' + pendingAttachment.id + '/message-link', {
                        method:'POST',
                        headers:{'Content-Type':'application/json'},
                        body:JSON.stringify({
                            messageId,
                            messageKind:external ? 'ConversationMessage' : 'JobMessage',
                            jobId:job.id
                        })
                    });
                    if (!link.ok) throw new Error(await link.text());
                }

                if (version === revision) await thread(scope, scopeId, version);
            } catch(error) {
                form.querySelector('[role="alert"]').textContent = pendingSend
                    ? 'Message saved. Attachment could not be linked; retry to finish attaching it.'
                    : (error.message || error);
            } finally {
                if (form.isConnected) {
                    syncSend();
                    if (pendingSend) {
                        button.textContent = 'Retry attachment';
                        form.querySelector('textarea').required = false;
                    }
                }
            }
        };
    }

    async function renderConversationFiles(job,panel,current,refresh) {
        const files = (await json(`/api/conversations/${job.conversationId}/files`)).filter(f=>f.messageId);
        if (!current()) return;
        const fileIcon=file=>{
            const name=String(file.fileName||'').toLowerCase();
            if(name.endsWith('.pdf'))return '<span class="file-type-icon pdf">PDF</span>';
            if(name.endsWith('.doc')||name.endsWith('.docx'))return '<span class="file-type-icon doc">D</span>';
            if(/\.(jpe?g|png|gif|webp|avif)$/.test(name))return '<span class="file-type-icon img">▧</span>';
            return '<span class="file-type-icon file">▤</span>';
        };
        const dateLabel=file=>{
            const value=file.createdAtUtc||file.createdAt||file.uploadedAtUtc||file.uploadedAt;
            return value?new Date(value).toLocaleDateString(undefined,{month:'short',day:'numeric'}):'';
        };
        panel.innerHTML = `
            <header class="context-card-header">
                <strong>Conversation Files</strong>
                <button type="button" class="text-button" data-view-all>View All</button>
            </header>
            <div class="conversation-file-list">
                ${files.slice(0,5).map(file=>`
                    <button type="button" class="conversation-file-row" data-file-id="${esc(file.id)}">
                        ${fileIcon(file)}
                        <span class="conversation-file-name">${esc(file.fileName)}</span>
                        <time>${esc(dateLabel(file))}</time>
                    </button>`).join('') || '<p class="context-empty">No files attached yet.</p>'}
            </div>`;

        const openFile=file=>window.open('/api/attachments/'+encodeURIComponent(file.id),'_blank','noopener');
        panel.querySelectorAll('[data-file-id]').forEach(row=>{
            row.onclick=()=>{const file=files.find(f=>f.id===row.dataset.fileId);if(file)openFile(file);};
        });
        const viewAll=panel.querySelector('[data-view-all]');
        if(viewAll)viewAll.onclick=()=>{
            const dialog=studioDialog('Conversation Files','<div class="conversation-file-dialog-list"></div>');
            const host=dialog.querySelector('.conversation-file-dialog-list');
            host.innerHTML=files.map(file=>`<button type="button" class="conversation-file-row" data-file-id="${esc(file.id)}">${fileIcon(file)}<span class="conversation-file-name">${esc(file.fileName)}</span><time>${esc(dateLabel(file))}</time></button>`).join('')||'<p>No files attached yet.</p>';
            host.querySelectorAll('[data-file-id]').forEach(row=>row.onclick=()=>{const file=files.find(f=>f.id===row.dataset.fileId);if(file)openFile(file);});
        };
    }

    async function renderJobMediaOverview(job,panel,current){
        const data=await json(`/api/jobs/${job.id}/media`);
        if(!current())return;

        const assets=(data.assets||[]).filter(a=>String(a.contentType||'').startsWith('image/'));
        const collections=[...new Set(assets.map(a=>a.collection||'Unclassified'))];
        let selected=new Set();
        let selectMode=false;
        let activeCollection='';

        panel.innerHTML=`
            <section class="workspace-media-overview">
                <header class="workspace-media-heading">
                    <div><h2>Media <span class="workspace-media-count">${assets.length}</span></h2></div>
                    <div class="workspace-media-actions">
                        <button type="button" class="primary" data-media-upload>Upload</button>
                        <button type="button" class="action" data-media-select>Select</button>
                        <button type="button" class="action" data-media-download>Download</button>
                        <button type="button" class="action" data-media-roster>Import Roster</button>
                        <button type="button" class="action media-more-button" data-media-more aria-label="More media actions">•••</button>
                    </div>
                </header>
                <div class="workspace-media-collections">
                    <button type="button" class="active" data-collection-filter="">All (${assets.length})</button>
                    ${collections.map(name=>`<button type="button" data-collection-filter="${esc(name)}">${esc(name)} (${assets.filter(a=>(a.collection||'Unclassified')===name).length})</button>`).join('')}
                </div>
                <div class="workspace-media-selection" hidden><strong data-selected-count>0 selected</strong><button type="button" class="text-button" data-clear-selection>Clear</button></div>
                <div class="workspace-media-grid"></div>
            </section>`;

        const grid=panel.querySelector('.workspace-media-grid');
        const selectionBar=panel.querySelector('.workspace-media-selection');
        const selectButton=panel.querySelector('[data-media-select]');

        const syncSelection=()=>{
            selectionBar.hidden=!selectMode && !selected.size;
            selectionBar.querySelector('[data-selected-count]').textContent=`${selected.size} selected`;
            selectButton.classList.toggle('active',selectMode);
            grid.querySelectorAll('[data-preview-media]').forEach(tile=>tile.classList.toggle('selected',selected.has(tile.dataset.previewMedia)));
        };

        const draw=()=>{
            const visible=activeCollection?assets.filter(a=>(a.collection||'Unclassified')===activeCollection):assets;
            grid.innerHTML=visible.slice(0,24).map(a=>`
                <button type="button" class="workspace-media-tile ${selected.has(a.id)?'selected':''}" data-preview-media="${a.id}">
                    <span class="media-select-indicator" aria-hidden="true">✓</span>
                    <img loading="lazy" src="/api/jobs/${job.id}/versions/by-id/${a.versionId}/content" alt="${esc(a.fileName)}">
                    <span class="workspace-media-filename">${esc(a.fileName)}</span>
                </button>`).join('') || '<div class="workspace-media-empty">No images are available in this collection.</div>';

            grid.querySelectorAll('[data-preview-media]').forEach(button=>button.onclick=()=>{
                const asset=assets.find(a=>a.id===button.dataset.previewMedia);
                if(!asset)return;
                if(selectMode){
                    selected.has(asset.id)?selected.delete(asset.id):selected.add(asset.id);
                    syncSelection();
                    return;
                }
                studioDialog(asset.fileName,`<img style="max-width:100%;max-height:72vh;object-fit:contain" src="/api/jobs/${job.id}/versions/by-id/${asset.versionId}/content" alt="${esc(asset.fileName)}">`);
            });
            syncSelection();
        };

        const openFullMedia=async action=>{
            panel.innerHTML='<div class="versioned-gallery"></div>';
            const host=panel.querySelector('.versioned-gallery');
            await renderProductionMedia(job,host,current);
            if(!current())return;
            const selector={
                upload:'[data-add]',
                roster:'[data-roster]'
            }[action];
            if(selector){
                const target=(detailPane.querySelector('.media-toolbar-host')||panel).querySelector(selector) || document.querySelector(selector);
                target?.click();
            }
        };

        panel.querySelectorAll('[data-collection-filter]').forEach(button=>button.onclick=()=>{
            panel.querySelectorAll('[data-collection-filter]').forEach(x=>x.classList.toggle('active',x===button));
            activeCollection=button.dataset.collectionFilter||'';
            draw();
        });

        panel.querySelector('[data-media-upload]').onclick=()=>openFullMedia('upload').catch(error=>alert(error.message));
        panel.querySelector('[data-media-roster]').onclick=()=>openFullMedia('roster').catch(error=>alert(error.message));
        panel.querySelector('[data-media-more]').onclick=()=>openFullMedia('manage').catch(error=>alert(error.message));

        selectButton.onclick=()=>{
            selectMode=!selectMode;
            if(!selectMode)selected.clear();
            syncSelection();
        };
        panel.querySelector('[data-clear-selection]').onclick=()=>{selected.clear();selectMode=false;syncSelection();};

        panel.querySelector('[data-media-download]').onclick=async event=>{
            if(!selected.size){
                selectMode=true;
                syncSelection();
                alert('Select one or more images, then click Download again.');
                return;
            }
            const button=event.currentTarget;
            button.disabled=true;
            try{
                const response=await fetch(`/api/jobs/${job.id}/media/export`,{
                    method:'POST',
                    headers:{'Content-Type':'application/json'},
                    body:JSON.stringify({assets:[...selected]})
                });
                if(!response.ok)throw Error(await response.text());
                const url=URL.createObjectURL(await response.blob());
                const link=document.createElement('a');
                link.href=url;
                link.download='studio-media.zip';
                link.click();
                setTimeout(()=>URL.revokeObjectURL(url),60000);
            }catch(error){alert(error.message);}
            finally{button.disabled=false;}
        };

        draw();
    }

    async function renderJobTab(name, job, panel, current, refresh) {
        if (name === 'subjects') return renderJobSubjects(job,panel,current,refresh);
        if (name === 'tasks') return renderJobTasks(job,panel,current,refresh);
        if (name === 'calendar') { await renderJobCalendar(job,panel,current); return; }
        if (name === 'activity') return renderJobActivity(job,panel,current);
        if (name === 'gallery') {
            panel.innerHTML='<div class="versioned-gallery"></div>';
            await renderProductionMedia(job,panel.querySelector('.versioned-gallery'),current);
            return;
        }
    }
    allButton.textContent = 'Jobs';
    allButton.onclick = () => { tab = 'jobs'; loadConversations().catch(fail); };
    document.getElementById('workspaceRefresh').onclick = () => loadConversations().catch(fail);
    document.querySelector('.pane-heading p').textContent = 'Jobs, announcements and members';
})();

