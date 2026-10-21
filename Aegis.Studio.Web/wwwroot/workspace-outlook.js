function messageFirstName(author, people, fallback = 'Team member', loginOnly = false) {
    const value = String(author || '').trim();
    if (!value) return fallback;
    const match = people.find(p => [p.userName, p.displayName].some(n => String(n || '').toLowerCase() === value.toLowerCase()));
    return studioFirstName(match || value);
}
function mergeJobMessages(teamMessages, clientMessages) {
    return [
        ...teamMessages.map(m => ({ id: 'team-' + m.id, sourceId:m.id, sourceKind:'JobMessage', author: m.senderUserName, body: m.body, at: m.createdAtUtc, client: false, loginOnly: true })),
        ...clientMessages.map(m => ({ id: 'client-' + m.id, sourceId:m.id, sourceKind:'ConversationMessage', author: m.author, body: m.body, at: m.createdAt, client: m.visibility.toLowerCase() !== 'internal' }))
    ].sort((a, b) => new Date(a.at) - new Date(b.at) || a.id.localeCompare(b.id));
}
// Workspace -> job -> messages. Workspace discussions and membership stay in context.
(() => {
    const conversationView = document.getElementById('conversationView');
    let revision = 0;
    let disposeJobView = () => {};
    let selectedJob = null;
    const display = () => { conversationView.classList.remove('hidden'); genericView.classList.add('hidden'); document.body.dataset.studioView='workspaces'; };
    async function selectWorkspaceView(id, name) {
        const workspace = workspaceCache.find(x => String(x.id) === String(id));
        if (!workspace?.permissions?.accessWorkspace || !workspace.permissions.view) return alert('You do not have permission to view this workspace.');
        const version=++revision;
        disposeJobView();disposeJobView=()=>{};
        selectedWorkspace = workspace.id; selectedJob = null; selectedConversation = null;
        currentWorkspacePermissions = workspace.permissions;
        workspaceTitle.textContent = name || workspace.name;
        // Workspace selection uses the same Jobs renderer, scoped to this workspace.
        // Do not render the old split-pane list before navigating.
        await window.studioOpenJobs(workspace.id);
        if(version!==revision || document.body.dataset.studioView!=='jobs')return;
        window.studioRememberView?.({view:'jobs',workspaceId:workspace.id,jobId:null});
        window.studioSetBreadcrumb?.([
            {label:'Today',view:'today'},
            {label:'Workspaces',view:'workspaces'},
            {label:name || workspace.name,view:'jobs',workspaceId:workspace.id}
        ]);
    }
    async function loadWorkspaceConversations() {
        if (selectedWorkspace) {
            const workspace = workspaceCache.find(x => String(x.id) === String(selectedWorkspace));
            return selectWorkspaceView(selectedWorkspace, workspace?.name);
        }
        return window.studioOpenJobs('');
    }
    window.StudioWorkspace={select:selectWorkspaceView,load:loadWorkspaceConversations,invalidate(){revision++;disposeJobView();disposeJobView=()=>{};}};
    window.openWorkspaceJob = async function(id,options={}) {
        if(!options.navigationRecorded)window.studioBeginJobNavigation?.(id,options);
        const version=beginJobLoading();
        try {
            const [job]=await Promise.all([
                json('/api/jobs/'+id),
                workspaceCache.length?Promise.resolve():loadWorkspaces()
            ]);
            if(!currentJobOpening(version))return;
            await showJobMessages(job,version);
        } catch(error) { jobLoadFailed(error,version,()=>window.openWorkspaceJob(id)); }
    };
    function currentJobOpening(version) {
        return version===revision && !conversationView.classList.contains('hidden') && document.body.dataset.studioView==='workspaces';
    }
    function beginJobLoading(job) {
        const version=++revision;
        delete detailPane.dataset.mediaCount;delete detailPane.dataset.subjectCount;delete detailPane.dataset.productionEnabled;delete detailPane.dataset.editEnabled;delete detailPane.dataset.readyEnabled;
        disposeJobView();disposeJobView=()=>{};
        selectedJob=null;selectedConversation=null;
        display();
        detailPane.classList.remove('empty');
        detailPane.classList.add('job-reading-view','job-detail-page');
        detailPane.setAttribute('aria-busy','true');
        conversationView.classList.add('has-open-job');
        detailPane.innerHTML=`<header class="job-detail-heading"><div class="job-detail-title"><h1>${esc(job?.title||'Job Details')}</h1><p role="status">Loading job…</p></div></header><div></div><div class="job-detail-board"><div class="job-detail-center"><section class="job-center-panel"><p role="status">Loading job information and messages…</p></section></div></div>`;
        window.dispatchEvent(new CustomEvent('studio-job-view-changed'));
        return version;
    }
    function jobLoadFailed(error,version,retry) {
        if(!currentJobOpening(version))return;
        detailPane.removeAttribute('aria-busy');
        detailPane.querySelector('.job-detail-title p').textContent='Unable to load job';
        const panel=detailPane.querySelector('.job-detail-center');
        panel.innerHTML=`<section class="job-center-panel"><p role="alert">${esc(error.message||String(error))}</p><button type="button" class="action">Retry</button></section>`;
        panel.querySelector('button').onclick=retry;
    }
    async function showJobMessages(job,version=beginJobLoading(job)) {
        if(!currentJobOpening(version))return;
        selectedWorkspace=job.workspaceId;
        const workspace=workspaceCache.find(item=>String(item.id)===String(job.workspaceId));
        currentWorkspacePermissions=workspace?.permissions||{};
        workspaceTitle.textContent=workspace?.name||'Workspace';
        window.studioRememberView?.({view:'workspaces',workspaceId:job.workspaceId,jobId:job.id});
        window.studioSetBreadcrumb?.([
            {label:'Workspaces',view:'workspaces'},
            {label:workspace?.name||'Workspace',view:'workspaces',workspaceId:job.workspaceId},
            {label:job.title||'Job'}
        ]);
        selectedJob = job;
        detailPane.querySelector('.job-detail-title h1').textContent=job.title||'Job Details';
        conversationList.querySelectorAll('[data-job]').forEach(row => row.classList.toggle('active', row.dataset.job === job.id));
        try { await thread('Job', job.id, version); }
        catch(error) { jobLoadFailed(error,version,()=>window.openWorkspaceJob(job.id)); }
    }
    async function thread(scope, scopeId, version) {
        const job = scope === 'Job' ? selectedJob : null;
        const value = await json(`/api/messages/threads/${scope}/${scopeId}`, { method:'POST' });
        if (version !== revision || (job && !currentJobOpening(version))) return;
        const messages = await json(`/api/messages/threads/${value.id}?count=500`);
        if (version !== revision || (job && !currentJobOpening(version))) return;
        let conversation = null, identities = [], clientNotice = '';
        if (job) {
            const response = await fetch('/api/conversations/' + job.conversationId);
            if (response.ok) {
                conversation = await response.json();
                if (String(conversation.channel).toLowerCase() === 'email') {
                    const senderResponse = await fetch(`/api/conversations/${job.conversationId}/sending-identities`);
                    if (senderResponse.ok) identities = (await senderResponse.json()).identities || [];
                    else clientNotice = 'Sending identities unavailable. You can still post internal messages.';
                }
            } else clientNotice = response.status === 403 ? 'Client messages are restricted by your permissions.' : 'Client messages could not be loaded. Refresh to retry.';
        }
        if (version !== revision || (job && !currentJobOpening(version))) return;
        const people = [];
        if (job) {
            const responses = await Promise.allSettled([
                json(`/api/workspaces/${job.workspaceId}/directory`), json('/api/auth/me')
            ]);
            if (responses[0].status === 'fulfilled') people.push(...responses[0].value);
            if (responses[1].status === 'fulfilled') people.push(responses[1].value.user || responses[1].value);
            studioRememberPeople(people);
        }
        if (version !== revision || (job && !currentJobOpening(version))) return;
        const combined = mergeJobMessages(messages, conversation?.messages || []);
        disposeJobView();disposeJobView=()=>{};
        detailPane.classList.remove('empty');
        detailPane.removeAttribute('aria-busy');
        detailPane.innerHTML = `<div class="detail-header"><div><h1>${esc(job?.title || 'Workspace messages')}</h1><p>${job ? 'Job discussion and client correspondence' : 'Shared with the workspace team'}</p></div></div><div id="combinedJobMessages" class="messages" aria-live="polite"></div><form id="hierarchyCompose" class="composer"><label for="hierarchyBody">${job ? 'Write a message' : 'Message the entire workspace'}</label><div class="reply-textbox-shell"><textarea id="hierarchyBody" placeholder="Write a message…" required maxlength="10000"></textarea>${job ? `<label class="reply-paperclip" title="Attach document" aria-label="Attach document"><svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M21.4 11.6 12 21a6 6 0 0 1-8.5-8.5l9.9-9.9a4 4 0 0 1 5.7 5.7l-10 10a2 2 0 0 1-2.8-2.8l9.2-9.2"/></svg><input type="file" data-message-file hidden></label>` : ``}</div><div class="send-split"><button class="primary" type="submit">${job ? 'Reply' : 'Send message'}</button>${job ? `<select id="jobSendingIdentity" aria-label="Reply identity" title="Choose how to reply"><option value="Workspace" ${conversation && (String(conversation.channel).toLowerCase() !== 'email' || identities.some(i => i.type === 'Workspace')) ? '' : 'disabled'}>Reply</option><option value="Internal">Reply as internal note</option><option value="Self" ${identities.some(i => i.type === 'Self') ? '' : 'disabled'}>Reply as myself${identities.some(i => i.type === 'Self') ? '' : ' (unavailable)'}</option></select>` : ''}</div><p class="muted">${esc(clientNotice)}</p><p id="hierarchyError" role="alert"></p></form>`;
        const renderMessages = () => {
            const visible = combined;
            document.getElementById('combinedJobMessages').innerHTML = visible.map(m => `<article class="message ${m.client ? 'client-exchange' : 'internal-discussion'}" data-message-id="${esc(m.sourceId||'')}"><span class="message-avatar" aria-hidden="true">${esc(messageFirstName(m.author, people, m.client ? "Client" : "Team member", m.loginOnly).slice(0,1))}</span><div class="message-content"><div class="message-head"><strong>${esc(messageFirstName(m.author, people, m.client ? 'Client' : 'Team member', m.loginOnly))}</strong> <time datetime="${esc(m.at)}" title="${esc(new Date(m.at).toLocaleString())}">${esc(new Date(m.at).toLocaleTimeString([], {hour:'numeric',minute:'2-digit'}))}</time>${m.client ? '<span class="message-kind">Client</span>' : ''}</div><p style="white-space:pre-wrap">${esc(m.body)}</p></div></article>`).join('') || '<p>No messages to display.</p>';
        };
        renderMessages();
        const sender = document.getElementById('jobSendingIdentity');
        const canSend = () => !job || sender?.value === 'Internal' || (!!conversation && (String(conversation.channel).toLowerCase() !== 'email' ? sender?.value === 'Workspace' : identities.some(i => i.type === sender?.value)));
        const syncSend = () => {
            const button = document.getElementById('hierarchyCompose').querySelector('button');
            button.textContent = !job ? 'Send message' : sender?.value === 'Internal' ? 'Send internal note' : sender?.value === 'Self' ? 'Send as myself' : 'Send Message';
            button.disabled = !canSend();
        };
        if(sender) { sender.value = 'Workspace'; sender.onchange = syncSend; }
        syncSend();
        if (scope === 'Job') {
            detailPane.classList.add('job-reading-view','job-detail-page');
            conversationView.classList.add('has-open-job');
            let disposed=false,workflowDialog=null,taskDialog=null;
            const current=()=>!disposed && version===revision && selectedJob?.id===job.id && detailPane.classList.contains('job-detail-page') && !conversationView.classList.contains('hidden');
            const header=detailPane.querySelector('.detail-header');
            header.className='job-detail-heading';
            const eventDate=job.eventDate?new Date(job.eventDate.includes('T')?job.eventDate:job.eventDate+'T12:00:00').toLocaleDateString('en-US',{weekday:'short',month:'short',day:'2-digit',year:'numeric'}).replaceAll(',',''):'Needs scheduling';
            const clientName=job.linkedClientName||job.clientName||'Not linked';
            const jobType=job.jobType||job.type||'Photography';
            const attendingRole=job.attendingJobRole||job.assignedRole||'Photographer';
            const workerName=job.assignedTo?studioFirstName(job.assignedTo):'Unassigned';
            header.innerHTML=`<div class="job-detail-title jd-job-identity">
                    <button type="button" class="jd-job-name-toggle" aria-expanded="false" aria-controls="jobIdentityMenu" title="Show job details"><span>${esc(job.title)}</span><span class="jd-job-name-chevron" aria-hidden="true">⌄</span></button>
                    <div id="jobIdentityMenu" class="jd-job-identity-menu" role="region" aria-label="Job details" hidden>
                        <dl>
                            <div><dt>Current Status</dt><dd><span class="jd-status-pill jd-status-label">${esc(job.status)}</span></dd></div>
                            <div><dt>Days in Stage</dt><dd data-stage-age>Loading…</dd></div>
                            <div><dt>Client</dt><dd>${esc(clientName)}</dd></div>
                            <div><dt>Owner</dt><dd data-job-owner>${esc(job.jobLead?studioFirstName(job.jobLead):'Unassigned')}</dd></div>
                            <div><dt>Job Type</dt><dd>${esc(jobType)}</dd></div>
                            <div><dt>Event Date</dt><dd>${esc(eventDate)}</dd></div>
                            <div><dt>Location</dt><dd data-job-location>Loading…</dd></div>
                            <div><dt>Attending Job Role</dt><dd>${esc(attendingRole)}</dd></div>
                            <div><dt>Name of Worker</dt><dd data-job-photographer>${esc(workerName)}</dd></div>
                        </dl>
                    </div>
                    <div class="jd-flow-overview"><ol class="jd-flow-summary" aria-label="Main job flow"></ol></div>
                    <section id="jobFlowDrawer" class="jd-flow-drawer" aria-label="Job flow details" aria-hidden="true" inert><div class="jd-flow-drawer-inner"><div class="jd-workflow-track" aria-live="polite"></div></div></section>
                </div>
                <div class="jd-job-status"><small>${job.jobCode||job.code?'Job Code · '+esc(job.jobCode||job.code):''}</small></div>
                <section class="jd-next-step" aria-label="Next job action" aria-live="polite"><p role="status">Loading next step…</p></section>
                <div class="jd-workflow-caption" hidden><span data-job-blocked></span></div>`;
            const flowDrawer=header.querySelector('.jd-flow-drawer');
            const identityToggle=header.querySelector('.jd-job-name-toggle'),identityMenu=header.querySelector('.jd-job-identity-menu');
            const setIdentityOpen=open=>{
                identityToggle.setAttribute('aria-expanded',String(open));
                identityToggle.title=open?'Hide job details':'Show job details';
                identityMenu.hidden=!open;
            };
            identityToggle.onclick=()=>setIdentityOpen(identityToggle.getAttribute('aria-expanded')!=='true');
            header.addEventListener('click',event=>{
                if(identityToggle.getAttribute('aria-expanded')==='true'&&!event.target.closest('.jd-job-identity'))setIdentityOpen(false);
            });
            header.addEventListener('keydown',event=>{
                if(event.key==='Escape'&&identityToggle.getAttribute('aria-expanded')==='true'){
                    event.preventDefault();setIdentityOpen(false);identityToggle.focus();
                }
            });
            let flowOpen=false,flowTrigger=null;
            const setFlowOpen=(open,restoreFocus=false)=>{
                flowOpen=open;
                flowDrawer.classList.toggle('is-open',open);
                flowDrawer.setAttribute('aria-hidden',String(!open));
                flowDrawer.toggleAttribute('inert',!open);
                if(restoreFocus)flowTrigger?.focus();
            };
            const dismissWorkflow=event=>{
                if(flowOpen&&!event.target.closest('.jd-flow-drawer,.jd-flow-overview'))setFlowOpen(false);
            };
            document.addEventListener('pointerdown',dismissWorkflow);
            header.querySelector('.jd-flow-summary').addEventListener('click',event=>{const icon=event.target.closest('[data-flow-phase]');if(icon){flowTrigger=icon;setFlowOpen(!flowOpen);}},true);
            header.addEventListener('studio-flow-details-toggle',event=>setFlowOpen(event.detail.open));
            header.addEventListener('keydown',event=>{
                if(event.key==='Escape'&&flowOpen){
                    event.preventDefault();event.stopPropagation();setFlowOpen(false,true);
                }
            });
            const board=document.createElement('div');board.className='job-detail-board';
            const center=document.createElement('div');center.className='job-detail-center';
            const panels={},contents={};
            for(const [name,title] of [['media','All Media'],['subjects','Subjects']]){
                const panel=document.createElement('section');panel.className='jd-panel job-'+name+'-section';panel.id='jobDetail-'+name;panel.hidden=name!=='media';
                panel.innerHTML=(name==='media'?'':`<header class="jd-card-heading"><h2>${title}</h2>${name==='subjects'?'<div><button type="button" class="jd-link" data-job-media>← Media</button><button type="button" class="jd-link" data-job-add-subject hidden>＋ Add Subject</button></div>':''}</header>`)+`<div class="job-${name}-content"></div>`;
                panels[name]=panel;contents[name]=panel.querySelector('.job-'+name+'-content');center.append(panel);
            }
            const right=document.createElement('aside');right.className='job-detail-right st-right';right.setAttribute('aria-label','Job tools and messages');
            const selectedFile=document.createElement('section');selectedFile.className='jd-panel job-detail-selected-file';
            selectedFile.innerHTML='<header class="jd-card-heading"><h2>Selected File</h2></header><div class="job-detail-selected-content"><p class="muted">Select a photo in the gallery to inspect it.</p></div>';
            const selectedContent=selectedFile.querySelector('.job-detail-selected-content');
            const messagesPanel=document.getElementById('combinedJobMessages'),composer=document.getElementById('hierarchyCompose');
            const messageCard=StudioMessageCard.create({id:'jobDetailMessages',title:'Messages',attributes:'data-message-job-id="'+esc(job.id)+'"',actions:'<button type="button" data-message-search aria-label="Search messages">⌕</button><button type="button" data-message-more aria-label="Show all messages">⋮</button>'});
            const messageHeader=messageCard.querySelector('.smc-header');
            messageCard.querySelector('.smc-feed').append(messagesPanel);
            messageCard.querySelector('.smc-content').append(composer);
            StudioMessageCard.bind(messageCard);
            const attention=document.createElement('section');attention.className='jd-panel jd-attention';
            attention.innerHTML='<header class="jd-card-heading"><h2>⚠ Needs Attention</h2></header><div class="jd-attention-items"><p role="status">Loading workflow…</p></div><button type="button" class="jd-link" data-view-workflow>View workflow →</button>';
            right.append(attention,selectedFile,messageCard);board.append(center,right);detailPane.append(board);
            const footerTools=document.getElementById('studioFooterJobTools');
            const makeDrawer=(key,title,label)=>{
                const toggle=document.createElement('button');toggle.type='button';toggle.className='jd-footer-link';toggle.dataset['job'+key]='';toggle.textContent=label;toggle.setAttribute('aria-expanded','false');toggle.setAttribute('aria-controls','job'+key+'Drawer');
                const drawer=document.createElement('aside');drawer.id='job'+key+'Drawer';drawer.className='jd-footer-drawer';drawer.setAttribute('aria-labelledby','job'+key+'Title');drawer.setAttribute('aria-hidden','true');drawer.setAttribute('inert','');
                drawer.innerHTML=`<header class="jd-card-heading"><h2 id="job${key}Title">${title}</h2><button type="button" class="jd-link" data-close-drawer aria-label="Close ${title}">×</button></header><div class="jd-footer-drawer-content"></div>`;
                detailPane.append(drawer);footerTools.append(toggle);return {toggle,drawer,host:drawer.querySelector('.jd-footer-drawer-content')};
            };
            const activityView=makeDrawer('Activity','Recent Activity','◷ Activity');
            const drawerViews=[activityView];
            const {toggle:activityToggle,drawer:activityDrawer}=activityView;
            contents.activity=activityView.host;

            const loaded=new Set(),pending=new Map();
            const refreshLocation=async()=>{
                try{
                    const schedules=await json('/api/schedule/entries?jobId='+job.id+'&from='+encodeURIComponent(new Date(Date.now()-31536000000).toISOString())+'&to='+encodeURIComponent(new Date(Date.now()+94608000000).toISOString()));
                    if(current()){const field=header.querySelector('[data-job-location]');field.textContent=schedules.find(s=>s.status!=='Cancelled'&&s.location)?.location||'Not scheduled';field.title=field.textContent;}
                }catch(error){if(current()){const field=header.querySelector('[data-job-location]');field.textContent='Unavailable';field.title=field.textContent;}}
            };
            const loadPanel=(name,force=false)=>{
                if(pending.has(name))return pending.get(name);
                if(loaded.has(name)&&!force)return Promise.resolve(true);
                const host=contents[name],requestDialog=taskDialog;
                const requestCurrent=()=>current()&&(name!=='tasks'||(taskDialog===requestDialog&&requestDialog?.open));
                if(name==='media'&&host.studioJobMedia)return host.studioJobMedia.reload();
                host.innerHTML='<p role="status">Loading…</p>';
                const request=(async()=>{
                    try{
                        if(name==='media')await window.StudioJobDetails.media(job,host,selectedContent,current,(mediaCount,subjectCount,permissions)=>{detailPane.dataset.productionEnabled=String(!!permissions.manageProduction);detailPane.dataset.editEnabled=String(!!permissions.editJob);detailPane.dataset.mediaCount=mediaCount;detailPane.dataset.subjectCount=subjectCount;window.dispatchEvent(new CustomEvent('studio-job-view-changed'));});
                        else await renderJobTab(name,job,host,requestCurrent,()=>loadPanel(name,true));
                        if(!requestCurrent())return false;
                        loaded.add(name);
                        if(name==='subjects')panels.subjects.querySelector('[data-job-add-subject]').hidden=!host.querySelector('#addJobSubject');
                        return true;
                    }catch(error){
                        if(requestCurrent()){
                            host.innerHTML='<p role="alert"></p><button type="button" class="action">Retry</button>';
                            host.querySelector('p').textContent=error.message||String(error);
                            host.querySelector('button').onclick=()=>loadPanel(name,true);
                        }
                        return false;
                    }finally{if(pending.get(name)===request)pending.delete(name);}
                })();pending.set(name,request);return request;
            };
            const openTasks=async event=>{
                if(taskDialog?.open){taskDialog.focus();return;}
                const trigger=event?.currentTarget||document.activeElement;
                const dialog=studioDialog('Tasks','<div class="job-tasks-content"></div>');dialog.classList.add('jd-tasks-dialog');taskDialog=dialog;contents.tasks=dialog.querySelector('.job-tasks-content');
                dialog.addEventListener('close',()=>{if(taskDialog===dialog){taskDialog=null;loaded.delete('tasks');pending.delete('tasks');if(current()){if(trigger?.isConnected&&trigger!==document.body)trigger.focus();else contents.media.querySelector('[data-job-tasks]')?.focus();}}}, {once:true});
                await loadPanel('tasks',true);
            };
            const closeDrawer=(view,restoreFocus=false)=>{view.drawer.classList.remove('is-open');view.drawer.setAttribute('aria-hidden','true');view.drawer.setAttribute('inert','');view.toggle.setAttribute('aria-expanded','false');if(restoreFocus)view.toggle.focus();};
            const openDrawer=async(view,load)=>{
                drawerViews.filter(other=>other!==view).forEach(other=>closeDrawer(other));
                view.drawer.style.setProperty('--jd-drawer-bottom',(document.getElementById('studioShellFooter').getBoundingClientRect().height||32)+'px');
                view.drawer.classList.add('is-open');view.drawer.setAttribute('aria-hidden','false');view.drawer.removeAttribute('inert');view.toggle.setAttribute('aria-expanded','true');view.drawer.querySelector('[data-close-drawer]').focus();await load();
            };
            for(const view of drawerViews){
                const load=()=>loadPanel('activity',true);
                view.toggle.onclick=()=>view.toggle.getAttribute('aria-expanded')==='true'?closeDrawer(view,true):openDrawer(view,load);
                view.drawer.querySelector('[data-close-drawer]').onclick=()=>closeDrawer(view,true);
                view.drawer.addEventListener('keydown',event=>{if(event.key==='Escape'){event.preventDefault();event.stopPropagation();closeDrawer(view,true);}});
            }
            const dismissDrawers=event=>{for(const view of drawerViews)if(view.toggle.getAttribute('aria-expanded')==='true'&&!view.drawer.contains(event.target)&&!view.toggle.contains(event.target))closeDrawer(view);};
            document.addEventListener('pointerdown',dismissDrawers);
            const selectPanel=async name=>{
                if(name==='tasks')return openTasks();
                if(name==='activity')return openDrawer(activityView,()=>loadPanel('activity',true));
                if(name==='messages'){
                    StudioMessageCard.setMode(messageCard,'messages');composer.querySelector('textarea').focus();return;
                }
                for(const [key,panel] of Object.entries(panels))panel.hidden=key!==name;
                await loadPanel(name);
            };
            panels.subjects.querySelector('[data-job-media]').onclick=()=>selectPanel('media');
            contents.media.studioOpenTasks=openTasks;
            contents.media.studioOpenSubjects=()=>selectPanel('subjects');
            panels.media.dataset.jobTab='gallery';
            // Subject photo/roster links use this page-owned callback, never a global DOM lookup.
            contents.subjects.studioOpenMedia=async()=>{await selectPanel('media');return loadPanel('media',true);};
            panels.subjects.querySelector('[data-job-add-subject]').onclick=()=>contents.subjects.querySelector('#addJobSubject')?.click();
            contents.subjects.studioImportRoster=async()=>{await selectPanel('media');return contents.media.studioJobMedia?.importRoster();};
            const handleAction=async event=>{
                if(!current())return;
                try{
                    const action=event.detail?.action;
                    if(action==='upload'){await selectPanel('media');contents.media.studioJobMedia?.upload();}
                    if(action==='subject'){await selectPanel('subjects');contents.subjects.querySelector('#addJobSubject')?.click();}
                    if(action==='task'){await selectPanel('tasks');contents.tasks.querySelector('form input')?.focus();}
                    if(action==='edit')await openJobDetailsEditor(job);
                    if(action==='workflow')await openAssignments();
                    if(action==='template-workflow')await StudioJobGraph.open(job.id);
                    if(action==='reminders')await window.StudioClientReminders.open(job.id);
                }catch(error){alert(error.message||String(error));}
            };
            window.addEventListener('studio-job-action',handleAction);
            attention.querySelector('[data-view-workflow]').onclick=()=>openAssignments();
            messageHeader.querySelector('[data-message-search]').onclick=()=>{StudioMessageCard.setMode(messageCard,'messages');const term=prompt('Search this conversation');if(term===null)return;messagesPanel.querySelectorAll('.message').forEach(row=>row.hidden=!!term&&!row.textContent.toLowerCase().includes(term.toLowerCase()));};
            messageHeader.querySelector('[data-message-more]').onclick=()=>{StudioMessageCard.setMode(messageCard,'messages');messagesPanel.querySelectorAll('.message').forEach(row=>row.hidden=false);composer.querySelector('textarea').focus();};

            let workflow=null,workflowRequest=0;
            const updateAge=()=>{
                if(['Completed','Cancelled'].includes(workflow?.data?.stage)){header.querySelector('[data-stage-age]').textContent=workflow.data.stage;return;}
                const start=Date.parse(workflow?.data?.enteredAt||'');
                header.querySelector('[data-stage-age]').textContent=Number.isFinite(start)?(window.StudioWorkflow?.elapsed?.(Math.max(0,(Date.now()-start)/3600000))||Math.floor(Math.max(0,Date.now()-start)/3600000)+' hours'):'Timing unavailable';
            };
            const refreshWorkflow=async()=>{
                const request=++workflowRequest;
                const advance=header.querySelector('[data-next-step-action]');if(advance)advance.disabled=true;
                try{
                    const view=await json('/api/jobs/'+job.id+'/connected-workflow');if(!current()||request!==workflowRequest)return;
                    workflow=view;const data=view.data;
                    window.StudioJobDetails.nextStep(header.querySelector('.jd-next-step'),view,selector=>openAssignments(selector));
                    window.StudioJobDetails.workflow(header.querySelector('.jd-workflow-track'),data,job,view,selector=>openAssignments(selector),header.querySelector('.jd-flow-summary'));
                    const graphView=await json(`/api/jobs/${job.id}/workflow-graph`).catch(()=>null);
                    if(!current()||request!==workflowRequest)return;
                    if(graphView?.graph)StudioJobDetails.compact(header.querySelector('.jd-workflow-track'),graphView,job,header.querySelector('.jd-flow-summary'),view);
                    header.querySelector('.jd-status-pill').textContent=data.stage;
                    header.querySelector('[data-job-owner]').textContent=data.owner?studioFirstName(data.owner):'Unassigned';
                    const crew=(data.sessions||[]).flatMap(session=>session.crew||[]);
                    const photographers=[...new Set(crew.map(person=>person.user_name).filter(Boolean))];
                    header.querySelector('[data-job-photographer]').textContent=photographers.length?photographers.map(person=>studioFirstName(person)).join(', '):job.assignedTo?studioFirstName(job.assignedTo):'Unassigned';
                    header.querySelector('[data-job-photographer]').title=header.querySelector('[data-job-photographer]').textContent;
                    header.querySelector('[data-job-blocked]').textContent=data.blockedReason?'Blocked: '+data.blockedReason:'';
                    header.querySelector('.jd-workflow-caption').hidden=!data.blockedReason;
                    const next={'Pending':'Review and confirm booking','Needs Assignment':'Assign a job owner','Needs Photographer Assignment':'Assign photographers','Awaiting Scheduling':'Schedule required sessions','Scheduled':'Start photography','Photography In Progress':'Finish required sessions','Awaiting Upload':'Complete and submit source media','Awaiting Production Review':'Review source material','Active Production':'Complete production and submit finished work','Awaiting Approval':'Approve finished work','Changes Requested':'Resolve requested corrections','Ready for Delivery':'Publish gallery and record delivery','Delivered':'Complete job'}[data.stage];
                    attention.querySelector('.jd-attention-items').innerHTML=(data.blockedReason?'<p class="jd-blocked">'+esc(data.blockedReason)+'</p>':'')+(next?'<p>'+esc(next)+'</p>':'<p>No outstanding workflow action.</p>')+((data.fieldConflicts||[]).length?'<p>Sync conflicts recorded: '+data.fieldConflicts.length+' · Review conflict history</p>':'')+Object.entries(data.rosterCounts||{}).map(([state,n])=>'<p>'+esc(state)+': '+esc(n)+'</p>').join('')+((data.outstandingRoster||[]).length?'<p>Outstanding roster: '+data.outstandingRoster.length+'</p>':'');
                    const decision=window.StudioWorkflow.nextAction(view),button=attention.querySelector('[data-view-workflow]');button.textContent=decision.label+' →';button.onclick=()=>openAssignments(decision.selector);updateAge();
                }catch(error){if(current()&&request===workflowRequest){window.StudioJobDetails.nextStep(header.querySelector('.jd-next-step'),null,()=>refreshWorkflow());header.querySelector('[data-stage-age]').textContent='Workflow unavailable';attention.querySelector('.jd-attention-items').textContent='Workflow could not be loaded. Open View workflow to retry.';}}
            };
            const openAssignments=async selector=>{
                setFlowOpen(false);
                if(typeof selector!=='string')selector=null;
                const dialog=studioDialog('Assignments & Workflow','<div class="job-detail-workflow-content"></div>');dialog.classList.add('job-detail-workflow-dialog');
                workflowDialog=dialog;
                const host=dialog.querySelector('.job-detail-workflow-content');
                dialog.addEventListener('close',()=>{
                    if(host.__studioWorkflowClock)clearInterval(host.__studioWorkflowClock);
                    workflowDialog=null;
                    if(current()){
                        refreshWorkflow();refreshLocation();
                        for(const name of ['tasks','activity'])loaded.delete(name);
                        if(taskDialog?.open)loadPanel('tasks',true);
                        if(activityToggle.getAttribute('aria-expanded')==='true')loadPanel('activity',true);
                    }
                }, {once:true});
                try{await window.StudioWorkflow.render(host,job.id,()=>current()&&dialog.isConnected);if(selector)host.querySelector(selector)?.click();}catch(error){host.textContent=error.message||String(error);}
            };
            // No detached panel retains a running clock.
            const timer=setInterval(()=>{if(!current()||!header.isConnected)clearInterval(timer);else updateAge();},60000);
            const navigationObserver=new MutationObserver(()=>{if(document.body.dataset.studioView!=='workspaces'){disposeJobView();disposeJobView=()=>{};}});
            navigationObserver.observe(document.body,{attributes:true,attributeFilter:['data-studio-view']});
            disposeJobView=()=>{disposed=true;navigationObserver.disconnect();clearInterval(timer);workflowDialog?.close();taskDialog?.close();document.removeEventListener('pointerdown',dismissDrawers);document.removeEventListener('pointerdown',dismissWorkflow);drawerViews.forEach(view=>{view.drawer.remove();view.toggle.remove();});contents.media.studioJobMedia?.dispose();window.removeEventListener('studio-job-action',handleAction);};
            const graphChanged=event=>{if(event.detail?.jobId===job.id&&current())refreshWorkflow();};document.addEventListener('studio-workflow-changed',graphChanged);const previousDispose=disposeJobView;disposeJobView=()=>{document.removeEventListener('studio-workflow-changed',graphChanged);previousDispose();};
            detailPane.dataset.readyEnabled='true';refreshWorkflow();refreshLocation();selectPanel('media');
            window.dispatchEvent(new CustomEvent('studio-job-view-changed'));
        } else {
            detailPane.classList.remove('job-reading-view','job-detail-page');
            conversationView.classList.remove('has-open-job');
            window.dispatchEvent(new CustomEvent('studio-job-view-changed'));
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
    async function renderJobTab(name, job, panel, current, refresh) {
        if (name === 'subjects') return renderJobSubjects(job,panel,current,refresh);
        if (name === 'tasks') return renderJobTasks(job,panel,current,refresh);
        if (name === 'activity') return renderJobActivity(job,panel,current);

    }
})();










