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
    const legacyAllButton=document.getElementById('allButton');
    legacyAllButton?.remove();
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
        conversationList.innerHTML = jobs.filter(x => x.workspaceId === workspaceId||(x.participatingWorkspaceIds||[]).includes(workspaceId)).map(job => `<button class="conversation-card hierarchy-row compact-job-row" data-search data-job="${job.id}">
            <span class="job-row-heading"><strong>${esc(job.title)}</strong></span>
            <span class="job-row-client">${esc(job.clientName || job.linkedClientName || 'Client not linked')}</span>
            <span class="job-row-meta">
                <span>Event: ${esc(job.eventDate || 'Not scheduled')}</span>
                <span>Assigned to: ${esc(job.assignedTo ? messageFirstName(job.assignedTo, people, 'Team member', true) : 'Unassigned')}</span>
                <span class="job-status">${esc(job.status)}</span>
            </span>
        </button>`).join('') || '<p class="empty-state">No jobs in this workspace yet.</p>';
        conversationList.querySelectorAll('[data-job]').forEach(button => button.onclick = () => showJobMessages(jobs.find(x => x.id === button.dataset.job)).catch(fail));
        filter();
    };
    window.openWorkspaceJob = async function(id) {
        if(!workspaceCache.length)await loadWorkspaces();
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
            const header=detailPane.querySelector('.detail-header');
            header.classList.add('studio-job-header');
            const copy=header.querySelector(':scope > div');
            copy.classList.add('studio-job-copy');
            copy.querySelector('p').innerHTML=[
                '<span class="studio-job-client">'+esc(job.linkedClientName||job.clientName||'Client not linked')+'</span>',
                '<span class="studio-job-date">'+esc(job.eventDate||'Needs scheduling')+'</span>',
                '<span class="studio-job-location">Location not set</span>',
                '<span class="studio-status-pill">'+esc(job.status)+'</span>'
            ].join('');

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
            detailPane.classList.add('job-reading-view');
            document.getElementById('conversationView').classList.add('has-open-job');

            const job = selectedJob;
            const workspaceName=workspaceCache.find(w=>w.id===job.workspaceId)?.name||'Workspace';
            const current=()=>version===revision;

            const lower=document.createElement('section');
            lower.className='job-lower-pane';
            lower.innerHTML=`
                <section class="job-center-panel job-media-section">
                    <header class="job-center-heading">
                        <h2>Media</h2>
                        <div class="media-toolbar-host" aria-label="Media toolbar"></div>
                    </header>
                    <div class="gallery-tab-filter"></div>
                    <div class="job-media-content"></div>
                </section>
                <section class="job-center-panel job-subjects-section">
                    <header class="job-center-heading"><h2>Subjects</h2><button type="button" class="text-button" data-job-add-subject>＋ Add Subject</button></header>
                    <div class="job-subjects-content"></div>
                </section>
                <section class="job-center-panel job-tasks-section">
                    <header class="job-center-heading"><h2>Tasks</h2><button type="button" class="text-button" data-job-add-task>＋ Add Task</button></header>
                    <div class="job-tasks-content"></div>
                </section>`;
            detailPane.appendChild(lower);

            const messagesPanel=document.getElementById('combinedJobMessages');
            const composer=document.getElementById('hierarchyCompose');
            const filesPanel=document.createElement('section');filesPanel.className='thread-shared-files';
            const nextPanel=document.createElement('section');nextPanel.className='thread-next-tasks';
            const refreshFiles=()=>renderConversationFiles(job,filesPanel,current,refreshFiles);
            refreshFiles().catch(error=>{if(current())filesPanel.textContent=error.message;});

            const messageColumn=document.createElement('section');
            messageColumn.className='job-message-column';
            const messageHeader=document.createElement('header');
            messageHeader.className='job-panel-heading';
            messageHeader.innerHTML='<div><strong>Messages</strong></div><div class="panel-heading-actions"><button type="button" class="icon-button" data-message-search aria-label="Search messages">⌕</button><button type="button" class="icon-button" data-message-more aria-label="Message options">⋮</button></div>';
            messageColumn.append(messageHeader,messagesPanel,composer);

            const side=document.createElement('aside');
            side.className='job-context-sidebar';
            side.append(nextPanel);

            const details=document.createElement('details');
            details.className='job-details-card';
            details.open=true;
            const detailRows=[
                ['Status','<span class="studio-status-pill">'+esc(job.status||'')+'</span>'],
                ['Priority',esc(job.priority||'Normal')],
                ['Workspace',esc(workspaceName)],
                ['Location','<span class="job-details-location">Location not set</span>'],
                ['Date & Time',esc(job.eventDate||'Needs scheduling')],
                ['Client',esc(job.linkedClientName||job.clientName||'Not linked')],
                ['Type',esc(job.jobType||job.type||'Photography')],
                ['Tags',esc(Array.isArray(job.tags)?job.tags.join(', '):(job.tags||'—'))],
                ['Package charge',esc(money(studioFinancials(job).charges))],
                ['Amount Paid',esc(money(studioFinancials(job).received))],
                ['Remaining',esc(money(studioFinancials(job).balance))],
                ['Job Code',esc(job.jobCode||job.code||String(job.id).slice(0,12))]
            ];
            details.innerHTML='<summary class="context-card-header"><strong>Job Details</strong><button class="text-button" type="button" data-edit-details>✎ Edit</button></summary><dl>'+detailRows.map(([label,value])=>'<dt>'+label+'</dt><dd>'+value+'</dd>').join('')+'</dl>';
            details.querySelector('[data-edit-details]').onclick=()=>openJobDetailsEditor(job);
            side.append(details,filesPanel);

            const board=document.createElement('div');
            board.className='job-work-board';

            const header=detailPane.querySelector('.detail-header');
            const existingThumb=header.querySelector('.studio-job-thumb');
            if(existingThumb) existingThumb.remove();

            const breadcrumb=document.createElement('button');
            breadcrumb.type='button';
            breadcrumb.className='job-breadcrumb job-back-link';
            breadcrumb.textContent='←  Back to Workspaces';
            breadcrumb.onclick=()=>window.studioNavigate?.('workspaces');
            header.before(breadcrumb);

            const meta=document.createElement('section');
            meta.className='job-meta-strip';
            const metaItems=[
                ['Client',job.linkedClientName||job.clientName||'Not linked'],
                ['Type',job.jobType||job.type||'Photography'],
                ['Photographer',job.assignedTo?studioFirstName(job.assignedTo):'Unassigned'],
                ['Workspace',workspaceName],
                ['Job Code',job.jobCode||job.code||String(job.id).slice(0,12)]
            ];
            meta.innerHTML=metaItems.map(([label,value],index)=>'<div class="job-meta-item"><span class="job-meta-icon">'+['♙','♙','●','▥','▧'][index]+'</span><span><small>'+esc(label)+'</small><strong>'+esc(String(value))+'</strong></span></div>').join('');

            const actions=document.createElement('div');
            actions.className='job-header-actions';
            actions.innerHTML='<button class="job-status-button" type="button" data-status-action><span></span>'+esc(job.status||'Status')+'⌄</button><button class="job-complete-button" type="button" data-complete-action>✓&nbsp;&nbsp; Mark Complete</button><button class="action more-action" data-more-action aria-label="More job actions">⋮</button>';
            actions.querySelector('[data-status-action]').onclick=()=>openJobDetailsEditor(job);
            actions.querySelector('[data-complete-action]').onclick=async()=>{
                const button=actions.querySelector('[data-complete-action]');
                button.disabled=true;
                try{
                    await json('/api/jobs/'+job.id,{
                        method:'PUT',
                        headers:{'Content-Type':'application/json'},
                        body:JSON.stringify({status:'Completed',assignedTo:job.assignedTo||null,eventDate:job.eventDate||null})
                    });
                    await openWorkspaceJob(job.id);
                }catch(error){
                    alert(error.message||String(error));
                    button.disabled=false;
                }
            };
            actions.querySelector('[data-more-action]').onclick=()=>{
                const dialog=studioDialog('Job actions','<div class="job-actions"><button type="button" class="action" data-edit>Edit Details</button><button type="button" class="action" data-calendar>Calendar</button><button type="button" class="action" data-activity>Activity</button></div>');
                dialog.querySelector('[data-edit]').onclick=()=>{dialog.close();openJobDetailsEditor(job);};
                dialog.querySelector('[data-calendar]').onclick=async()=>{dialog.close();await renderJobTab('calendar',job,lower.querySelector('.job-tasks-content'),current,()=>{});};
                dialog.querySelector('[data-activity]').onclick=async()=>{dialog.close();await renderJobTab('activity',job,lower.querySelector('.job-tasks-content'),current,()=>{});};
            };
            header.append(actions);
            header.after(meta);

            const nav=document.createElement('nav');
            nav.className='job-section-tabs';
            nav.setAttribute('aria-label','Job information');
            nav.innerHTML='<button data-job-scroll="overview" class="active">Overview</button><button data-job-scroll="messages">Messages</button><button data-job-scroll="media">Media</button><button data-job-scroll="subjects">Subjects</button><button data-job-scroll="tasks">Tasks</button><button data-job-scroll="sessions">Sessions</button><button data-job-scroll="activity">Activity</button>';
            meta.after(nav,board);

            board.append(messageColumn,lower,side);
            messageHeader.querySelector('[data-message-search]').onclick=()=>{
                const term=prompt('Search this conversation');if(term===null)return;
                messagesPanel.querySelectorAll('.message').forEach(row=>row.hidden=!!term&&!row.textContent.toLowerCase().includes(term.toLowerCase()));
            };
            messageHeader.querySelector('[data-message-more]').onclick=()=>{
                messagesPanel.querySelectorAll('.message').forEach(row=>row.hidden=false);composer.querySelector('textarea').focus();
            };

            const mediaPanel=lower.querySelector('.job-media-content');
            const subjectsPanel=lower.querySelector('.job-subjects-content');
            const tasksPanel=lower.querySelector('.job-tasks-content');
            const refreshSubjects=()=>renderJobTab('subjects',job,subjectsPanel,current,refreshSubjects);
            const refreshTasks=()=>renderJobTab('tasks',job,tasksPanel,current,refreshTasks);

            Promise.allSettled([
                renderJobTab('gallery',job,mediaPanel,current,()=>{}),
                refreshSubjects(),
                refreshTasks(),
                renderCompactTasks(job,nextPanel,current)
            ]).then(()=>{});

            lower.querySelector('[data-job-add-subject]').onclick=()=>{
                const add=subjectsPanel.querySelector('#addJobSubject');
                if(add)add.click();
            };

            lower.querySelector('[data-job-add-task]').onclick=()=>{
                const dialog=studioDialog('Job tasks','<div class="all-tasks"></div>');
                const panel=dialog.querySelector('.all-tasks');
                const refresh=()=>renderJobTasks(job,panel,()=>dialog.isConnected,refresh);
                refresh().catch(error=>panel.textContent=error.message);
            };

            nav.querySelectorAll('[data-job-scroll]').forEach(button=>{
                button.onclick=async()=>{
                    nav.querySelectorAll('button').forEach(x=>x.classList.toggle('active',x===button));
                    const name=button.dataset.jobScroll;
                    if(name==='overview'){messagesPanel.querySelectorAll('.message').forEach(row=>row.hidden=false);await refreshTasks();return;}
                    if(name==='sessions'){const dialog=studioDialog('Job Sessions','<div data-job-sessions></div>');const sessions=await json('/api/operations/shoots?jobId='+job.id);dialog.querySelector('[data-job-sessions]').innerHTML=sessions.map(s=>'<article class="task-card"><strong>'+esc(s.location||'Session')+'</strong><small>'+esc(new Date(s.callTimeUtc).toLocaleString())+' · '+esc(s.status)+' · '+esc(studioFirstName(s.photographerUserName))+'</small></article>').join('')||'<p>No sessions scheduled.</p>';return;}
                    if(name==='messages')messageColumn.scrollIntoView({block:'nearest'});
                    else if(name==='media')lower.querySelector('.job-media-section').scrollIntoView({block:'nearest'});
                    else if(name==='subjects')lower.querySelector('.job-subjects-section').scrollIntoView({block:'nearest'});
                    else if(name==='tasks')lower.querySelector('.job-tasks-section').scrollIntoView({block:'nearest'});
                    else if(name==='calendar')await renderJobTab('calendar',job,tasksPanel,current,refreshTasks);
                    else if(name==='activity')await renderJobTab('activity',job,tasksPanel,current,refreshTasks);
                };
            });

            Promise.resolve().then(async()=>{
                try{
                    const schedules=await json('/api/schedule/entries?jobId='+job.id+'&from='+encodeURIComponent(new Date(Date.now()-31536000000).toISOString())+'&to='+encodeURIComponent(new Date(Date.now()+94608000000).toISOString()));
                    if(version!==revision)return;
                    const location=schedules.find(x=>x.location)?.location;
                    if(location){
                        detailPane.querySelectorAll('.job-details-location,.studio-job-location').forEach(node=>node.textContent=location);
                    }
                }catch{}
            });
        } else {
            detailPane.classList.remove('job-reading-view');
            document.getElementById('conversationView').classList.remove('has-open-job');
        }
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
            const files = (await json(`/api/conversations/${job.conversationId}/files`)).filter(f=>f.messageId);
            if (!current()) return;
            panel.innerHTML = `<section class="legacy-attachments"><header class="conversation-files-title">Conversation Files</header><div class="conversation-file-actions"><button class="action" data-preview-file disabled>Preview</button><a class="action" data-download-file hidden>Download</a></div><div class="job-file-gallery">${files.map(file => {
                const url = '/api/attachments/' + encodeURIComponent(file.id);
                const image = /\.(jpe?g|png|gif|webp|avif)$/i.test(file.fileName || '');
                return `<a class="job-file-tile" data-file-id="${esc(file.id)}" href="${url}" target="_blank" rel="noopener">${image ? `<img loading="lazy" src="${url}" alt="${esc(file.fileName)}">` : '<span class="job-file-document" aria-hidden="true">▤</span>'}<strong>${esc(file.fileName)}</strong><small>${esc(file.visibility)} · ${esc(formatBytes(file.size))}${file.messageId?' · linked to '+esc(file.messageKind||'message'):''}</small><span>${file.messageId?'<span class="conversation-file-source" data-source-message="'+esc(file.messageId)+'">View source message</span>':'Open file'}</span></a>`;
            }).join('') || '<p>No files attached yet.</p>'}</div></section>`;
            panel.querySelectorAll('[data-source-message]').forEach(source=>source.onclick=event=>{
                event.preventDefault();event.stopPropagation();
                const message=document.querySelector('[data-message-id="'+source.dataset.sourceMessage+'"]');
                if(!message){alert('The source message is outside the currently loaded message history.');return;}
                message.scrollIntoView({behavior:'smooth',block:'center'});
                message.classList.add('source-message-highlight');setTimeout(()=>message.classList.remove('source-message-highlight'),1800);
            });

            panel.querySelectorAll('.job-file-tile').forEach(tile => tile.ondblclick = event => {
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

            panel.querySelectorAll('.job-file-tile').forEach(tile=>{
                tile.onclick=event=>{event.preventDefault();panel.querySelectorAll('.job-file-tile').forEach(x=>x.classList.toggle('selected',x===tile));const preview=panel.querySelector('[data-preview-file]'),download=panel.querySelector('[data-download-file]');preview.disabled=false;preview.onclick=()=>{const file=files.find(x=>x.id===tile.dataset.fileId);if(/\.(jpe?g|png|gif|webp|avif)$/i.test(file.fileName||''))tile.ondblclick({preventDefault(){}});else window.open(tile.href,'_blank','noopener');};download.hidden=false;download.href=tile.href;download.download=files.find(x=>x.id===tile.dataset.fileId).fileName;};
                tile.onkeydown=event=>{if(event.key==='Enter'){event.preventDefault();tile.click();panel.querySelector('[data-preview-file]').click();}};
            });
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
    document.getElementById('workspaceRefresh').onclick = () => loadConversations().catch(fail);
    document.querySelector('.pane-heading p').textContent = 'Jobs, announcements and members';
})();


