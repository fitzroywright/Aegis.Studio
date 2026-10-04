function messageFirstName(author, people, fallback = 'Team member', loginOnly = false) {
    const value = String(author || '').trim();
    if (!value) return fallback;
    const match = people.find(p => [p.userName, p.displayName].some(n => String(n || '').toLowerCase() === value.toLowerCase()));
    return studioFirstName(match || value);
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
    let jobFilter = 'all';
    const baseConversation = openConversation;
    function empty(title, message) {
        detailPane.classList.remove('job-reading-view'); document.getElementById('conversationView').classList.remove('has-open-job');
        detailPane.classList.add('empty');
        detailPane.innerHTML = `<div class="empty-state"><h2>${esc(title)}</h2><p>${esc(message)}</p></div>`;
    }
    function filter() {
        if(tab==='jobs' && typeof applyJobFilter==='function'){ applyJobFilter(); return; }
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

    const jobFilterBar=document.createElement('div');
    jobFilterBar.className='job-filter-bar';
    jobFilterBar.innerHTML='<button type="button" data-job-filter="all" class="active">All <span>0</span></button><button type="button" data-job-filter="active">Active <span>0</span></button><button type="button" data-job-filter="planning">Planning <span>0</span></button><button type="button" data-job-filter="completed">Completed <span>0</span></button>';
    tabs.after(jobFilterBar);

    const statusGroup=status=>{
        const value=String(status||'').toLowerCase();
        if(value.includes('complete')||value.includes('deliver')||value.includes('publish'))return 'completed';
        if(value.includes('plan')||value.includes('pending')||value.includes('schedule')||value.includes('confirm')||value.includes('deposit'))return 'planning';
        return 'active';
    };

    function applyJobFilter(){
        const term=search.value.trim().toLocaleLowerCase();
        conversationList.querySelectorAll('[data-search][data-job]').forEach(row=>{
            const textMatch=!term||row.textContent.toLocaleLowerCase().includes(term);
            const groupMatch=jobFilter==='all'||row.dataset.statusGroup===jobFilter;
            row.hidden=!(textMatch&&groupMatch);
        });
        jobFilterBar.querySelectorAll('[data-job-filter]').forEach(button=>button.classList.toggle('active',button.dataset.jobFilter===jobFilter));
    }

    jobFilterBar.querySelectorAll('[data-job-filter]').forEach(button=>button.onclick=()=>{
        jobFilter=button.dataset.jobFilter;
        applyJobFilter();
    });

    workspaceTitle.setAttribute('role','button');
    workspaceTitle.tabIndex=0;
    workspaceTitle.classList.add('workspace-title-picker');
    const openWorkspacePicker=()=>{
        const visible=workspaceCache.filter(x=>x.permissions?.accessWorkspace&&x.permissions?.view);
        const dialog=studioDialog('Choose Workspace','<div class="workspace-picker-list">'+visible.map(w=>'<button type="button" class="workspace-picker-row" data-workspace-id="'+esc(w.id)+'"><strong>'+esc(w.name)+'</strong><span>'+esc(w.address||'')+'</span></button>').join('')+'</div>');
        dialog.querySelectorAll('[data-workspace-id]').forEach(button=>button.onclick=()=>{
            const workspace=visible.find(w=>w.id===button.dataset.workspaceId);
            dialog.close();
            selectWorkspace(workspace.id,workspace.name).catch(fail);
        });
    };
    workspaceTitle.onclick=openWorkspacePicker;
    workspaceTitle.onkeydown=event=>{if(event.key==='Enter'||event.key===' '){event.preventDefault();openWorkspacePicker();}};

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
        tab = 'jobs'; jobFilter='all'; search.value = ''; workspaceTitle.textContent = name;
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
        const workspaceJobs=jobs.filter(x => x.workspaceId === workspaceId);
        const counts={
            all:workspaceJobs.length,
            active:workspaceJobs.filter(x=>statusGroup(x.status)==='active').length,
            planning:workspaceJobs.filter(x=>statusGroup(x.status)==='planning').length,
            completed:workspaceJobs.filter(x=>statusGroup(x.status)==='completed').length
        };
        jobFilterBar.querySelectorAll('[data-job-filter]').forEach(button=>{
            const count=button.querySelector('span');
            if(count)count.textContent=counts[button.dataset.jobFilter]??0;
        });
        conversationList.innerHTML = workspaceJobs.map(job => `<button class="conversation-card hierarchy-row" data-search data-job="${job.id}" data-status-group="${statusGroup(job.status)}" data-job-status="${esc(String(job.status||'').toLowerCase())}">
            <span class="job-list-thumb" data-job-thumb="${job.id}"><span>${esc((job.title||'J').slice(0,1))}</span></span>
            <span class="job-list-copy">
                <span class="job-row-heading"><strong>${esc(job.title)}</strong></span>
                <small>${esc(job.clientName||job.linkedClientName||'Client not linked')}</small>
                <small class="job-date-line">▣ ${esc(job.eventDate || "Not scheduled")}</small>
            </span>
            <span class="job-status">${esc(job.status)}</span>
        </button>`).join('') || '<p class="empty-state">No jobs in this workspace yet.</p>';
        conversationList.querySelectorAll('[data-job]').forEach(button => button.onclick = () => showJobMessages(workspaceJobs.find(x => x.id === button.dataset.job)).catch(fail));
        Promise.allSettled(workspaceJobs.map(async job=>{
            const media=await json(`/api/jobs/${job.id}/media`);
            if(version!==revision)return;
            const image=(media.assets||[]).find(a=>String(a.contentType||'').startsWith('image/'));
            const host=conversationList.querySelector(`[data-job-thumb="${job.id}"]`);
            if(host&&image?.versionId)host.innerHTML=`<img src="/api/jobs/${job.id}/versions/by-id/${image.versionId}/content" alt="">`;
        }));
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
    async function openJobDetailsEditor(job) {
        const access=await json(`/api/jobs/${job.id}/access`);
        const permissions=access.permissions||access;
        if(!permissions.editJob){
            alert('You do not have permission to edit this job.');
            return;
        }
        const dialog=studioDialog('Edit Job Details',`
            <form class="subject-form">
                <label>Status<input name="status" value="${esc(job.status||'')}" ${permissions.changeStatus?'':'disabled'}></label>
                <label>Assigned To<input name="assignedTo" value="${esc(job.assignedTo||'')}" ${permissions.assignStaff?'':'disabled'}></label>
                <label>Event Date<input name="eventDate" type="date" value="${esc(job.eventDate||'')}"></label>
                <div class="dialog-actions">
                    <button type="button" class="action" data-cancel>Cancel</button>
                    <button type="submit" class="primary">Save</button>
                </div>
                <p role="alert"></p>
            </form>`);
        const form=dialog.querySelector('form');
        dialog.querySelector('[data-cancel]').onclick=()=>dialog.close();
        form.onsubmit=async event=>{
            event.preventDefault();
            const button=form.querySelector('[type=submit]');
            button.disabled=true;
            try{
                const updated=await json(`/api/jobs/${job.id}`,{
                    method:'PUT',
                    headers:{'Content-Type':'application/json'},
                    body:JSON.stringify({
                        status:permissions.changeStatus?form.elements.status.value:null,
                        assignedTo:permissions.assignStaff?(form.elements.assignedTo.value||null):null,
                        eventDate:form.elements.eventDate.value||null
                    })
                });
                dialog.close();
                selectedJob=updated;
                await thread('Job',job.id,revision);
            }catch(error){
                form.querySelector('[role=alert]').textContent=error.message;
                button.disabled=false;
            }
        };
    }

    async function renderJobCalendar(job,panel,current) {
        await renderJobTasks(job,panel,current,()=>renderJobCalendar(job,panel,current));
        if(!current())return;
        panel.querySelector('[data-task-view="calendar"]')?.click();
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
            studioRememberPeople(people);
        }
        if (version !== revision) return;
        const combined = mergeJobMessages(messages, conversation?.messages || []);
        detailPane.classList.remove('empty');
        detailPane.innerHTML = `<div class="detail-header"><div><h1>${esc(job?.title || 'Workspace messages')}</h1><p>${job ? 'Job discussion and customer correspondence' : 'Shared with the workspace team'}</p></div></div><div id="combinedJobMessages" class="messages" aria-live="polite"></div><form id="hierarchyCompose" class="composer"><label for="hierarchyBody">${job ? 'Write a message' : 'Message the entire workspace'}</label><div class="reply-textbox-shell"><textarea id="hierarchyBody" placeholder="Write a message…" required maxlength="10000"></textarea>${job ? `<label class="reply-paperclip" title="Attach document" aria-label="Attach document"><svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M21.4 11.6 12 21a6 6 0 0 1-8.5-8.5l9.9-9.9a4 4 0 0 1 5.7 5.7l-10 10a2 2 0 0 1-2.8-2.8l9.2-9.2"/></svg><input type="file" data-message-file hidden></label>` : ``}</div><div class="send-split"><button class="primary" type="submit">${job ? 'Reply' : 'Send message'}</button>${job ? `<select id="jobSendingIdentity" aria-label="Reply identity" title="Choose how to reply"><option value="Workspace" ${conversation && (String(conversation.channel).toLowerCase() !== 'email' || identities.some(i => i.type === 'Workspace')) ? '' : 'disabled'}>Reply</option><option value="Internal">Reply as internal note</option><option value="Self" ${identities.some(i => i.type === 'Self') ? '' : 'disabled'}>Reply as myself${identities.some(i => i.type === 'Self') ? '' : ' (unavailable)'}</option></select>` : ''}</div><p class="muted">${esc(customerNotice)}</p><p id="hierarchyError" role="alert"></p></form>`;
        const renderMessages = () => {
            const visible = combined;
            document.getElementById('combinedJobMessages').innerHTML = visible.map(m => `<article class="message ${m.customer ? 'customer-exchange' : 'internal-discussion'}" data-message-id="${esc(m.sourceId||'')}"><span class="message-avatar" aria-hidden="true">${esc(messageFirstName(m.author, people, m.customer ? "Customer" : "Team member", m.loginOnly).slice(0,1))}</span><div class="message-content"><div class="message-head"><span class="message-kind">${m.customer ? 'CUSTOMER EXCHANGE' : 'INTERNAL'}</span> ${esc(messageFirstName(m.author, people, m.customer ? 'Customer' : 'Team member', m.loginOnly))} · ${esc(new Date(m.at).toLocaleString())}</div><p style="white-space:pre-wrap">${esc(m.body)}</p></div></article>`).join('') || '<p>No messages to display.</p>';
        };
        renderMessages();
        if(job){
            const searchButton=detailPane.querySelector('[data-message-search]');
            if(searchButton)searchButton.onclick=()=>{
                const term=prompt('Search this conversation');
                if(term===null)return;
                const value=term.trim().toLowerCase();
                detailPane.querySelectorAll('#combinedJobMessages .message').forEach(row=>row.hidden=!!value&&!row.textContent.toLowerCase().includes(value));
            };
            const moreButton=detailPane.querySelector('[data-message-more]');
            if(moreButton)moreButton.onclick=()=>{
                detailPane.querySelectorAll('#combinedJobMessages .message').forEach(row=>row.hidden=false);
                document.getElementById('hierarchyBody')?.focus();
            };
        }
        if(job){
            const header=detailPane.querySelector('.detail-header');
            header.classList.add('studio-job-header');
            const copy=header.firstElementChild;
            copy.classList.add('studio-job-copy');
            const thumb=document.createElement('div');
            thumb.className='studio-job-thumb';
            thumb.innerHTML='<span>'+esc((job.title||'J').slice(0,1))+'</span>';
            header.prepend(thumb);
            copy.querySelector('p').innerHTML=[
                '<span class="studio-job-client">'+esc(job.linkedClientName||job.clientName||'Client not linked')+'</span>',
                '<span class="studio-job-date">'+esc(job.eventDate||'Needs scheduling')+'</span>',
                '<span class="studio-job-location">Location not set</span>',
                '<span class="studio-status-pill">'+esc(job.status)+'</span>'
            ].join('');
            Promise.allSettled([
                json(`/api/jobs/${job.id}/media`),
                json(`/api/schedule/entries?jobId=${job.id}&from=${encodeURIComponent(new Date(Date.now()-31536000000).toISOString())}&to=${encodeURIComponent(new Date(Date.now()+94608000000).toISOString())}`)
            ]).then(results=>{
                if(version!==revision)return;
                const media=results[0].status==='fulfilled'?(results[0].value.assets||[]):[];
                const image=media.find(a=>String(a.contentType||'').startsWith('image/'));
                if(image?.versionId)thumb.innerHTML=`<img src="/api/jobs/${job.id}/versions/by-id/${image.versionId}/content" alt="">`;
                const schedules=results[1].status==='fulfilled'?results[1].value:[];
                const location=schedules.find(x=>x.location)?.location;
                if(location){
                    copy.querySelector('.studio-job-location').textContent=location;
                    const detailsLocation=detailPane.querySelector('[data-details-location]');
                    if(detailsLocation)detailsLocation.textContent=location;
                }
            });
        }
        if(job){
            const filesPanel=document.createElement('section');
            filesPanel.className='thread-shared-files';
            const messagesPanel=document.getElementById('combinedJobMessages');
            const composer=document.getElementById('hierarchyCompose');
            const conversationRow=document.createElement('div');
            conversationRow.className='job-conversation-row';
            messagesPanel.before(conversationRow);
            const messageColumn=document.createElement('div');
            messageColumn.className='job-message-column';
            const messageHeader=document.createElement('header');
            messageHeader.className='job-panel-heading';
            messageHeader.innerHTML='<div><strong>Messages</strong><span class="panel-count">'+combined.length+'</span></div><div class="panel-heading-actions"><button type="button" class="icon-button" data-message-search aria-label="Search messages">⌕</button><button type="button" class="icon-button" data-message-more aria-label="More message options">⋮</button></div>';
            const nextPanel=document.createElement('section');nextPanel.className='thread-next-tasks';
            messageColumn.append(messageHeader,messagesPanel,composer);
            conversationRow.append(messageColumn,filesPanel,nextPanel);
            const current=()=>version===revision;
            renderCompactTasks(job,nextPanel,current).catch(error=>{if(current())nextPanel.textContent='Upcoming work could not be loaded: '+error.message;});
            const refreshFiles=()=>renderConversationFiles(job,filesPanel,current,refreshFiles);
            refreshFiles().catch(error=>{if(current())filesPanel.textContent='Conversation files could not be loaded: '+error.message;});
        }
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
            detailPane.classList.add('job-reading-view'); document.getElementById('conversationView').classList.add('has-open-job');
            const lower = document.createElement('section');
            lower.className = 'job-lower-pane';
            lower.innerHTML = '<nav class="workspace-tabs job-section-tabs" aria-label="Job information"><button data-job-tab="messages">▣ <span>Messages</span></button><button data-job-tab="gallery">▧ <span>Media</span></button><button data-job-tab="subjects">♙ <span>Subjects</span></button><button data-job-tab="tasks">☑ <span>Tasks</span></button><button data-job-tab="calendar">▦ <span>Calendar</span></button><button data-job-tab="activity">⌁ <span>Activity</span></button></nav><div class="media-toolbar-host" aria-label="Media toolbar"></div><div class="gallery-tab-filter"></div><div class="job-tab-content"></div>';
            detailPane.appendChild(lower);
            const job = selectedJob;
            let tabRevision = 0;
            const activate = async name => {
                const token = ++tabRevision;
                const panel = lower.querySelector('.job-tab-content');
                const current = () => version === revision && token === tabRevision;
                lower.querySelectorAll('[data-job-tab]').forEach(button => button.classList.toggle('active', button.dataset.jobTab === name));
                lower.querySelector('.gallery-tab-filter').replaceChildren();
                detailPane.dataset.jobSection=name; lower.querySelector('.media-toolbar-host').hidden=name!=='gallery';
                panel.textContent = 'Loading…';
                try { await renderJobTab(name==='messages'?'gallery':name, job, panel, current, () => activate(name)); }
                catch (error) { if (current()) panel.textContent = 'Unable to load: ' + (error.message || error); }
            };
            lower.querySelectorAll('[data-job-tab]').forEach(button => button.onclick = () => activate(button.dataset.jobTab));
            const conversationRow=detailPane.querySelector('.job-conversation-row');
            const side=document.createElement('aside'); side.className='job-context-sidebar';
            side.append(conversationRow.querySelector('.thread-next-tasks'));
            const details=document.createElement('section');details.className='job-details-card';
            details.innerHTML=`<header class="context-card-header"><strong>Job Details</strong><button class="action text-button" type="button" data-edit-details>Edit</button></header><dl><dt>Workspace</dt><dd>${esc(workspaceCache.find(w=>w.id===job.workspaceId)?.name||'Workspace')}</dd><dt>Status</dt><dd><span class="studio-status-pill">${esc(job.status)}</span></dd><dt>Event Date</dt><dd>${esc(job.eventDate||'Needs scheduling')}</dd><dt>Location</dt><dd data-details-location>Location not set</dd><dt>Customer</dt><dd>${esc(job.linkedClientName||job.clientName||'Not linked')}</dd><dt>Assigned To</dt><dd>${esc(job.assignedTo?studioFirstName(job.assignedTo):'Unassigned')}</dd></dl>`;
            details.querySelector('[data-edit-details]').onclick=()=>openJobDetailsEditor(job);
            side.append(details,conversationRow.querySelector('.thread-shared-files'));
            const board=document.createElement('div');board.className='job-work-board';
            const header=detailPane.querySelector('.detail-header');
            const breadcrumb=document.createElement('p');breadcrumb.className='job-breadcrumb';breadcrumb.innerHTML='Workspaces <span aria-hidden="true">/</span> '+esc(workspaceCache.find(w=>w.id===job.workspaceId)?.name||'Workspace')+' <span aria-hidden="true">/</span> <strong>'+esc(job.title)+'</strong>';
            header.before(breadcrumb);
            const actions=document.createElement('div');
            actions.className='job-header-actions';
            actions.innerHTML='<button class="action" data-message-action>▰ Message</button><button class="primary" data-add-task>＋ Add Task</button><button class="action" data-more-action>More⌄</button>';
            actions.querySelector('[data-message-action]').onclick=()=>document.getElementById('hierarchyBody')?.focus();
            actions.querySelector('[data-add-task]').onclick=()=>{
                const dialog=studioDialog('Job tasks','<div class="all-tasks"></div>');
                const panel=dialog.querySelector('.all-tasks');
                const refresh=()=>renderJobTasks(job,panel,()=>dialog.isConnected,refresh);
                refresh().catch(error=>panel.textContent=error.message);
            };
            actions.querySelector('[data-more-action]').onclick=()=>{
                const dialog=studioDialog('Job actions','<div class="job-actions"><button type="button" class="action" data-edit> Edit Details </button><button type="button" class="action" data-tasks>View Tasks</button><button type="button" class="action" data-activity>View Activity</button></div>');
                dialog.querySelector('[data-edit]').onclick=()=>{dialog.close();openJobDetailsEditor(job);};
                dialog.querySelector('[data-tasks]').onclick=()=>{dialog.close();lower.querySelector('[data-job-tab="tasks"]')?.click();};
                dialog.querySelector('[data-activity]').onclick=()=>{dialog.close();lower.querySelector('[data-job-tab="activity"]')?.click();};
            };
            header.append(actions);
            const nav=lower.querySelector('.job-section-tabs');header.after(nav,board);
            board.append(conversationRow.querySelector('.job-message-column'),lower,side);conversationRow.remove();
            activate('gallery');
        } else detailPane.classList.remove('job-reading-view'); document.getElementById('conversationView').classList.remove('has-open-job');
        let pendingSend=null,pendingAttachment=null;
        document.getElementById('hierarchyCompose').onsubmit = async event => {
            event.preventDefault();
            const form = event.currentTarget, body = form.querySelector('textarea').value.trim();
            if ((!body&&!pendingSend) || !canSend()) return;
            const button = form.querySelector('button'); button.disabled = true;
            try {
                const external = job && sender?.value !== 'Internal';
                const url = external ? `/api/conversations/${job.conversationId}/messages` : `/api/messages/threads/${value.id}`;
                const payload = external ? {body, isInternal:false, sendingIdentity:sender?.value || 'Workspace'} : {body};
                const sent = pendingSend || await json(url, {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)}); pendingSend=sent; form.querySelector('textarea').readOnly=true; if(sender)sender.disabled=true;
                const file = job ? form.querySelector('[data-message-file]')?.files?.[0] : null;
                if (file) {
                    const messageId = external ? sent.messageId : sent.id;
                    if (!messageId) throw new Error('Message was sent but Studio did not return its message id, so the attachment was not uploaded.');
                    const data = new FormData(); data.append('file', file);
                    if(!pendingAttachment)pendingAttachment=await json('/api/conversations/' + job.conversationId + '/attachments?visibility=' + encodeURIComponent(external ? 'External' : 'Internal'),{method:'POST',body:data});
                    const attachment=pendingAttachment;
                    const link = await fetch('/api/attachments/' + attachment.id + '/message-link', {
                        method:'POST',
                        headers:{'Content-Type':'application/json'},
                        body:JSON.stringify({messageId, messageKind:external?'ConversationMessage':'JobMessage', jobId:job.id})
                    });
                    if (!link.ok) throw new Error(await link.text());
                }
                if (version === revision) await thread(scope, scopeId, version);
            } catch(error) { form.querySelector('[role="alert"]').textContent = pendingSend ? 'Message saved. Attachment could not be linked; retry to finish attaching it.' : (error.message || error); }
            finally { if (form.isConnected) { syncSend(); if(pendingSend) {button.textContent='Retry attachment';form.querySelector('textarea').required=false;} } }
        };
    }
    async function renderConversationFiles(job,panel,current,refresh) {
        const files=(await json(`/api/conversations/${job.conversationId}/files`)).filter(f=>f.messageId);
        if(!current())return;

        const icon=file=>{
            const name=String(file.fileName||'').toLowerCase();
            if(name.endsWith('.pdf'))return '<span class="file-type-icon pdf">PDF</span>';
            if(name.endsWith('.doc')||name.endsWith('.docx'))return '<span class="file-type-icon doc">W</span>';
            if(/\.(jpe?g|png|gif|webp|avif)$/.test(name))return '<span class="file-type-icon img">▧</span>';
            return '<span class="file-type-icon file">▤</span>';
        };
        const when=file=>{
            const value=file.createdAtUtc||file.createdAt||file.uploadedAtUtc||file.uploadedAt;
            return value?new Date(value).toLocaleDateString(undefined,{month:'short',day:'numeric'}):'';
        };
        const open=file=>window.open('/api/attachments/'+encodeURIComponent(file.id),'_blank','noopener');

        panel.innerHTML=`
            <header class="context-card-header">
                <strong>Conversation Files</strong>
                <button type="button" class="text-button" data-view-all>View All</button>
            </header>
            <div class="conversation-file-list">
                ${files.slice(0,5).map(file=>`
                    <button type="button" class="conversation-file-row" data-file-id="${esc(file.id)}">
                        ${icon(file)}
                        <span class="conversation-file-name">${esc(file.fileName)}</span>
                        <time>${esc(when(file))}</time>
                    </button>`).join('')||'<p class="context-empty">No files attached yet.</p>'}
            </div>`;

        panel.querySelectorAll('[data-file-id]').forEach(row=>row.onclick=()=>{
            const file=files.find(x=>x.id===row.dataset.fileId);
            if(file)open(file);
        });

        const viewAll=panel.querySelector('[data-view-all]');
        viewAll.onclick=()=>{
            const dialog=studioDialog('Conversation Files','<div class="conversation-file-dialog-list"></div>');
            const host=dialog.querySelector('.conversation-file-dialog-list');
            host.innerHTML=files.map(file=>`
                <button type="button" class="conversation-file-row" data-file-id="${esc(file.id)}">
                    ${icon(file)}
                    <span class="conversation-file-name">${esc(file.fileName)}</span>
                    <time>${esc(when(file))}</time>
                </button>`).join('')||'<p>No files attached yet.</p>';
            host.querySelectorAll('[data-file-id]').forEach(row=>row.onclick=()=>{
                const file=files.find(x=>x.id===row.dataset.fileId);
                if(file)open(file);
            });
        };
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

