(() => {
    const conversationView=document.getElementById('conversationView');
    const genericView=document.getElementById('genericView');

    function setActive(view){
        document.body.dataset.studioView=view;
        document.querySelectorAll('.rail [data-view]').forEach(button=>{
            button.classList.toggle('active',button.dataset.view===view);
        });
        document.querySelectorAll('[data-top-view]').forEach(button=>{
            button.classList.toggle('active',button.dataset.topView===view);
        });
    }

    async function renderMyWork(){
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML='<div class="mywork-loading">Loading your studio…</div>';

        const now=new Date();
        const startOfDay=new Date(now.getFullYear(),now.getMonth(),now.getDate());
        const endOfDay=new Date(now.getFullYear(),now.getMonth(),now.getDate()+1);
        const scheduleFrom=new Date(now.getFullYear(),now.getMonth(),now.getDate()-1).toISOString();
        const scheduleTo=new Date(now.getFullYear(),now.getMonth(),now.getDate()+14).toISOString();

        const [auth,jobs,tasks,schedule,notifications]=await Promise.all([
            json('/api/auth/me').catch(()=>({})),
            json('/api/jobs').catch(()=>[]),
            json('/api/operations/activities').catch(()=>[]),
            json('/api/schedule/entries?from='+encodeURIComponent(scheduleFrom)+'&to='+encodeURIComponent(scheduleTo)).catch(()=>[]),
            json('/api/notifications?count=80').catch(()=>[])
        ]);

        const me=auth.user||auth;
        const user=String(me.userName||me.loginName||'').toLowerCase();
        const myTasks=tasks.filter(t=>!['Completed','Cancelled'].includes(t.status) && (!user || String(t.assignedTo||'').toLowerCase()===user));
        const todaySchedule=schedule
            .filter(s=>{const d=new Date(s.startsAtUtc||s.start||s.startsAt);return d>=startOfDay&&d<endOfDay;})
            .sort((a,b)=>new Date(a.startsAtUtc||a.start||a.startsAt)-new Date(b.startsAtUtc||b.start||b.startsAt));

        const recentJobs=[...jobs]
            .sort((a,b)=>new Date(b.eventDate||b.createdAtUtc||0)-new Date(a.eventDate||a.createdAtUtc||0))
            .slice(0,6);

        const visualJobIds=[...new Set([
            ...recentJobs.map(j=>j.id),
            ...todaySchedule.map(s=>s.jobId).filter(Boolean)
        ])];

        const mediaByJob=new Map();
        await Promise.all(visualJobIds.map(async jobId=>{
            const media=await json('/api/jobs/'+jobId+'/media').catch(()=>({assets:[]}));
            mediaByJob.set(String(jobId),media);
        }));

        const mediaSnapshots=recentJobs.map(job=>({job,media:mediaByJob.get(String(job.id))||{assets:[]}}));
        const totalPhotos=mediaSnapshots.reduce((sum,x)=>sum+(x.media.assets?.length||0),0);
        const messageNotifications=notifications.filter(n=>/message|conversation|reply/i.test(String(n.eventType||n.title||n.type||'')));
        const activityNotifications=notifications.slice(0,8);

        const jobById=id=>jobs.find(j=>String(j.id)===String(id));
        const scheduleJob=entry=>jobById(entry.jobId)||{title:entry.title||'Scheduled session',clientName:entry.title||''};
        const formatTime=value=>new Date(value).toLocaleTimeString([],{hour:'numeric',minute:'2-digit'});
        const formatDay=value=>new Date(value).toLocaleDateString([],{month:'short',day:'numeric'});
        const imageForJob=jobId=>{
            const media=mediaByJob.get(String(jobId))||{assets:[]};
            const asset=(media.assets||[]).find(a=>String(a.contentType||'').startsWith('image/'))||(media.assets||[])[0];
            return asset?.versionId?'/api/jobs/'+jobId+'/versions/by-id/'+asset.versionId+'/content':'';
        };

        const recentCards=mediaSnapshots.slice(0,4).map(({job,media})=>{
            const image=imageForJob(job.id);
            const count=media.assets?.length||0;
            return `<button class="mw-recent-card" data-open-job="${job.id}">
                <div class="mw-recent-image">${image?`<img src="${image}" alt="">`:'<span>📷</span>'}</div>
                <div class="mw-recent-copy">
                    <strong>${esc(job.title)}</strong>
                    <span>${count} photos · ${esc(job.status||'')}</span>
                </div>
            </button>`;
        }).join('');

        const sessionRows=todaySchedule.slice(0,5).map(entry=>{
            const job=scheduleJob(entry);
            const image=entry.jobId?imageForJob(entry.jobId):'';
            return `<button class="mw-session-row" data-open-job="${entry.jobId||job.id||''}">
                <div class="mw-session-time"><strong>${formatTime(entry.startsAtUtc||entry.start||entry.startsAt)}</strong><span>${formatTime(entry.endsAtUtc||entry.end||entry.endsAt)}</span></div>
                <div class="mw-session-thumb">${image?`<img src="${image}" alt="">`:'<span>📷</span>'}</div>
                <div class="mw-session-main"><strong>${esc(job.title||entry.title||'Session')}</strong><span>${esc(job.clientName||'')}</span><span>${esc(entry.location||'Location not set')}</span></div>
                <span class="mw-state-pill">${esc(entry.status||'Upcoming')}</span>
                <span class="mw-kebab">⋮</span>
            </button>`;
        }).join('')||'<div class="mw-empty">No sessions scheduled today.</div>';

        const taskRows=myTasks.slice(0,5).map(task=>`<button class="mw-task-row" data-open-job="${task.jobId}">
            <span class="mw-check"></span>
            <strong>${esc(task.title)}</strong>
            <span>${esc(task.activityType||'Photography')}</span>
            <span class="mw-due">${task.dueAtUtc?formatDay(task.dueAtUtc):'Open'}</span>
            <span class="mw-priority ${String(task.priority||'').toLowerCase()}">${esc(task.priority||'Normal')}</span>
            <span class="mw-kebab">⋮</span>
        </button>`).join('')||'<div class="mw-empty">No open tasks assigned to you.</div>';

        const scheduleRows=todaySchedule.slice(0,6).map((entry,index)=>{
            const job=scheduleJob(entry);
            return `<button class="mw-schedule-row" data-open-job="${entry.jobId||job.id||''}">
                <span class="mw-timeline-dot ${index===0?'now':''}"></span>
                <span class="mw-schedule-time">${formatTime(entry.startsAtUtc||entry.start||entry.startsAt)}</span>
                <span class="mw-schedule-copy"><strong>${esc(job.title||entry.title||'Session')}</strong><small>${esc(entry.location||'Location not set')}</small></span>
                <span class="mw-when">${index===0?'Now':'Upcoming'}</span>
            </button>`;
        }).join('')||'<div class="mw-empty">No schedule items today.</div>';

        const messageRows=messageNotifications.slice(0,3).map(n=>`<button class="mw-message-row">
            <span class="mw-message-avatar">${esc(String(n.title||n.eventType||'M').slice(0,1))}</span>
            <span class="mw-message-copy"><strong>${esc(n.title||'Studio message')}</strong><small>${esc(n.body||n.message||'New activity in Studio')}</small></span>
            <span class="mw-message-time">${n.createdAtUtc?formatTime(n.createdAtUtc):''}</span>
        </button>`).join('')||'<div class="mw-empty">No new messages.</div>';

        const activityRows=activityNotifications.slice(0,4).map(n=>`<div class="mw-activity-row">
            <span class="mw-activity-icon">✓</span>
            <span><strong>${esc(n.title||n.eventType||'Studio activity')}</strong><small>${esc(n.body||n.message||'')}</small></span>
            <time>${n.createdAtUtc?formatTime(n.createdAtUtc):''}</time>
        </div>`).join('')||'<div class="mw-empty">No recent activity.</div>';

        const pipelineCounts={
            capture:jobs.filter(j=>/photo|capture|scheduled/i.test(j.status||'')).length,
            edit:jobs.filter(j=>/edit|process/i.test(j.status||'')).length,
            review:jobs.filter(j=>/review|approval/i.test(j.status||'')).length,
            deliver:jobs.filter(j=>/deliver|ready|complete/i.test(j.status||'')).length
        };

        genericView.innerHTML=`
            <section class="mw-page">
                <header class="mw-page-header">
                    <div><h1>My Work</h1><p>Your day, your studio, everything in one place.</p></div>
                    <div class="mw-date"><strong>${now.toLocaleDateString([],{weekday:'long',month:'long',day:'numeric',year:'numeric'})}</strong><span>Studio overview</span></div>
                </header>

                <div class="mw-layout">
                    <div class="mw-main">
                        <div class="mw-metrics">
                            <article class="mw-metric"><span class="mw-metric-icon blue">📷</span><div><strong>${todaySchedule.length}</strong><b>Today's Sessions</b><small>${todaySchedule.length} scheduled</small></div></article>
                            <article class="mw-metric"><span class="mw-metric-icon green">✓</span><div><strong>${myTasks.length}</strong><b>Pending Tasks</b><small>${myTasks.filter(t=>String(t.priority).toLowerCase()==='high').length} high priority</small></div></article>
                            <article class="mw-metric"><span class="mw-metric-icon purple">▧</span><div><strong>${totalPhotos}</strong><b>New Photos</b><small>Ready for review</small></div></article>
                            <article class="mw-metric"><span class="mw-metric-icon orange">●</span><div><strong>${messageNotifications.length}</strong><b>Client Messages</b><small>${messageNotifications.length?'Needs attention':'All caught up'}</small></div></article>
                        </div>

                        <div class="mw-two-col">
                            <section class="mw-card">
                                <header><h2>Today's Sessions</h2><button data-nav="pipeline">View Calendar →</button></header>
                                <div>${sessionRows}</div>
                            </section>
                            <section class="mw-card">
                                <header><h2>Recent Work</h2><button data-nav="workspaces">View Workspaces →</button></header>
                                <div class="mw-recent-grid">${recentCards||'<div class="mw-empty">No recent work.</div>'}</div>
                            </section>
                        </div>

                        <div class="mw-two-col lower">
                            <section class="mw-card">
                                <header><h2>My Tasks</h2><button data-nav="workspaces">Open Jobs →</button></header>
                                <div class="mw-task-tabs"><button class="active">All (${myTasks.length})</button><button>Editing</button><button>Client</button><button>Production</button></div>
                                <div>${taskRows}</div>
                            </section>
                            <section class="mw-card">
                                <header><h2>Pipeline Overview</h2><button data-nav="pipeline">View Pipeline →</button></header>
                                <div class="mw-pipeline-stats">
                                    <div class="blue"><strong>${pipelineCounts.capture}</strong><span>Capture</span></div>
                                    <div class="purple"><strong>${pipelineCounts.edit}</strong><span>Edit</span></div>
                                    <div class="orange"><strong>${pipelineCounts.review}</strong><span>Review</span></div>
                                    <div class="green"><strong>${pipelineCounts.deliver}</strong><span>Deliver</span></div>
                                </div>
                                <h3>Active Jobs (${jobs.length})</h3>
                                <div class="mw-active-jobs">${jobs.slice(0,5).map(job=>`<button data-open-job="${job.id}"><span>${esc(job.title)}</span><i><em style="width:${20+Math.min(75,Math.abs(String(job.title||'').length*4))}%"></em></i><b>${esc(job.status||'Active')}</b></button>`).join('')}</div>
                            </section>
                        </div>
                    </div>

                    <aside class="mw-side">
                        <section class="mw-card">
                            <header><h2>Today's Schedule</h2><button data-nav="pipeline">View All →</button></header>
                            <div class="mw-schedule-list">${scheduleRows}</div>
                        </section>
                        <section class="mw-card">
                            <header><h2>Messages</h2><button data-nav="workspaces">Open Jobs →</button></header>
                            <div>${messageRows}</div>
                            <button class="mw-link-button" data-nav="workspaces">Open workspace jobs →</button>
                        </section>
                        <section class="mw-card">
                            <header><h2>Studio Activity</h2><button data-nav="reports">View All →</button></header>
                            <div>${activityRows}</div>
                        </section>
                    </aside>
                </div>
            </section>`;

        genericView.querySelectorAll('[data-open-job]').forEach(button=>button.addEventListener('click',()=>{
            if(button.dataset.openJob)openWorkspaceJob(button.dataset.openJob).catch(()=>{});
        }));
        genericView.querySelectorAll('[data-nav]').forEach(button=>button.addEventListener('click',()=>window.studioNavigate(button.dataset.nav)));
    }

    async function renderWorkspaces(){
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML='<div class="mywork-loading">Loading workspaces…</div>';

        const [workspaces,jobs]=await Promise.all([
            json('/api/workspaces').catch(()=>[]),
            json('/api/jobs').catch(()=>[])
        ]);

        const workspaceData=await Promise.all(workspaces.map(async workspace=>{
            const workspaceJobs=jobs.filter(job=>String(job.workspaceId)===String(workspace.id));
            const media=await Promise.all(workspaceJobs.map(async job=>({
                job,
                snapshot:await json('/api/jobs/'+job.id+'/media').catch(()=>({assets:[]}))
            })));
            const announcements=await json('/api/workspaces/'+workspace.id+'/announcements').catch(()=>[]);
            return {workspace,jobs:workspaceJobs,media,announcements};
        }));

        const photoCount=item=>item.media.reduce((sum,x)=>sum+(x.snapshot.assets?.length||0),0);
        const latestJob=item=>[...item.jobs].sort((a,b)=>new Date(b.eventDate||b.createdAtUtc||b.createdAt||0)-new Date(a.eventDate||a.createdAtUtc||a.createdAt||0))[0];
        const thumbFor=(job,snapshot)=>{
            const asset=(snapshot.assets||[]).find(a=>String(a.contentType||'').startsWith('image/'))||(snapshot.assets||[])[0];
            return asset?.versionId?'/api/jobs/'+job.id+'/versions/by-id/'+asset.versionId+'/content':'';
        };
        const heroFor=item=>{
            for(const pair of item.media){
                const url=thumbFor(pair.job,pair.snapshot);
                if(url)return url;
            }
            return '';
        };
        const previewsFor=item=>{
            const values=[];
            for(const pair of item.media){
                for(const asset of (pair.snapshot.assets||[])){
                    if(!asset.versionId)continue;
                    values.push('/api/jobs/'+pair.job.id+'/versions/by-id/'+asset.versionId+'/content');
                    if(values.length===3)return values;
                }
            }
            return values;
        };

        const activeCount=workspaceData.filter(item=>item.jobs.some(job=>String(job.status||'').toLowerCase()!=='completed')).length;
        const completedWorkspaceCount=workspaceData.filter(item=>item.jobs.length&&item.jobs.every(job=>String(job.status||'').toLowerCase()==='completed')).length;
        const announcements=workspaceData
            .flatMap(item=>(item.announcements||[]).map(a=>({...a,workspaceName:item.workspace.name})))
            .sort((a,b)=>new Date(b.createdAtUtc||0)-new Date(a.createdAtUtc||0))
            .slice(0,4);

        const renderRows=items=>items.map(item=>{
            const current=item.jobs.filter(job=>String(job.status||'').toLowerCase()!=='completed');
            const latest=latestJob(item);
            const photos=photoCount(item);
            const hero=heroFor(item);
            const previews=previewsFor(item);
            const status=current.length?'Active':item.jobs.length?'Completed':'Ready';
            const statusClass=status.toLowerCase();
            const subtitle=latest?.clientName||item.workspace.emailAddress||item.workspace.routeKey||'Studio workspace';
            const location=latest?.location||latest?.eventLocation||'';
            const lastActivity=latest?.eventDate||latest?.createdAtUtc||latest?.createdAt;
            return `<article class="workspace-reference-row" data-workspace-row data-status="${statusClass}" data-search="${esc((item.workspace.name+' '+subtitle+' '+status).toLowerCase())}">
                <button type="button" class="workspace-reference-main" data-open-workspace="${item.workspace.id}" data-workspace-name="${esc(item.workspace.name)}">
                    <span class="workspace-reference-hero">${hero?`<img src="${hero}" alt="">`:'<span class="workspace-photo-fallback">📷</span>'}</span>
                    <span class="workspace-reference-copy">
                        <strong><span class="workspace-folder">▰</span>${esc(item.workspace.name)}</strong>
                        <small>${esc(subtitle)}</small>
                        ${location?`<small>⌖ ${esc(location)}</small>`:''}
                    </span>
                    <span class="workspace-reference-stat"><b>${item.jobs.length}</b><small>Jobs</small></span>
                    <span class="workspace-reference-stat"><b>${photos}</b><small>Photos</small></span>
                    <span class="workspace-reference-stat"><b>${current.length}</b><small>Active</small></span>
                    <span class="workspace-reference-previews">
                        ${previews.map(url=>`<img src="${url}" alt="">`).join('')}
                        ${photos>previews.length?`<i>+${photos-previews.length}</i>`:''}
                    </span>
                    <span class="workspace-reference-activity">
                        <em class="workspace-reference-pill ${statusClass}">${status}</em>
                        <small>Last activity</small>
                        <b>${lastActivity?esc(new Date(lastActivity).toLocaleDateString()):'—'}</b>
                    </span>
                    <span class="workspace-reference-more">⋮</span>
                </button>
            </article>`;
        }).join('');

        genericView.innerHTML=`
            <section class="workspace-reference-page">
                <header class="workspace-reference-header">
                    <div><h1>Workspaces</h1><p>Organize jobs, clients, and conversations.</p></div>
                    <button type="button" class="primary workspace-new-button" data-workspace-admin>＋ New Workspace</button>
                </header>

                <div class="workspace-reference-layout">
                    <div class="workspace-reference-center">
                        <div class="workspace-reference-toolbar">
                            <div class="workspace-reference-tabs" role="tablist">
                                <button class="active" data-workspace-filter="all">All Workspaces <b>${workspaceData.length}</b></button>
                                <button data-workspace-filter="active">Active <b>${activeCount}</b></button>
                                <button data-workspace-filter="completed">Completed <b>${completedWorkspaceCount}</b></button>
                            </div>
                            <div class="workspace-reference-tools">
                                <div class="workspace-view-toggle" aria-label="Workspace view">
                                    <button type="button" title="Grid view">▦ Grid</button>
                                    <button type="button" class="active" title="List view">☷ List</button>
                                </div>
                                <select data-workspace-sort aria-label="Sort workspaces">
                                    <option value="name">Name (A–Z)</option>
                                    <option value="jobs">Most jobs</option>
                                    <option value="photos">Most photos</option>
                                </select>
                            </div>
                        </div>
                        <div class="workspace-reference-list" data-workspace-list>${renderRows(workspaceData)}</div>
                    </div>

                    <aside class="workspace-reference-side">
                        <section class="workspace-side-card">
                            <header><h2>📣 Announcements</h2><span>View All →</span></header>
                            <div class="workspace-announcement-list">
                                ${announcements.length?announcements.map(a=>`<article><strong>${esc(a.title)}</strong><p>${esc(a.body)}</p><small>${esc(a.workspaceName)} · ${a.createdAtUtc?new Date(a.createdAtUtc).toLocaleDateString():''}</small></article>`).join(''):'<p class="mw-empty">No workspace announcements.</p>'}
                            </div>
                        </section>

                        <section class="workspace-side-card">
                            <header><h2>⌁ Workspace Filters</h2></header>
                            <label>Search<input type="search" placeholder="Search workspaces…" data-workspace-search-side></label>
                            <label>Status<select data-workspace-status-side><option value="all">All Statuses</option><option value="active">Active</option><option value="completed">Completed</option><option value="ready">Ready</option></select></label>
                            <button type="button" class="action workspace-clear-filters">Clear Filters</button>
                        </section>

                        <section class="workspace-side-card">
                            <header><h2>ϟ Quick Actions</h2></header>
                            <button type="button" class="workspace-quick-action" data-workspace-admin>＋ New Workspace</button>
                            <button type="button" class="workspace-quick-action" data-nav-quick="pipeline">▣ View Calendar</button>
                            <button type="button" class="workspace-quick-action" data-nav-quick="frontdesk">＋ Create Booking</button>
                        </section>
                    </aside>
                </div>
            </section>`;

        const allRows=()=>[...genericView.querySelectorAll('[data-workspace-row]')];
        let filter='all';
        let term='';
        let sort='name';

        const refresh=()=>{
            const list=genericView.querySelector('[data-workspace-list]');
            const rows=allRows();
            rows.forEach(row=>{
                const status=row.dataset.status;
                const matchesFilter=filter==='all'||status===filter;
                const matchesTerm=!term||row.dataset.search.includes(term);
                row.hidden=!(matchesFilter&&matchesTerm);
            });

            rows.sort((a,b)=>{
                const ai=workspaceData.find(x=>String(x.workspace.id)===String(a.querySelector('[data-open-workspace]').dataset.openWorkspace));
                const bi=workspaceData.find(x=>String(x.workspace.id)===String(b.querySelector('[data-open-workspace]').dataset.openWorkspace));
                if(sort==='jobs')return (bi?.jobs.length||0)-(ai?.jobs.length||0);
                if(sort==='photos')return photoCount(bi||{media:[]})-photoCount(ai||{media:[]});
                return String(ai?.workspace.name||'').localeCompare(String(bi?.workspace.name||''));
            }).forEach(row=>list.appendChild(row));
        };

        genericView.querySelectorAll('[data-workspace-filter]').forEach(button=>{
            button.onclick=()=>{
                filter=button.dataset.workspaceFilter;
                genericView.querySelectorAll('[data-workspace-filter]').forEach(x=>x.classList.toggle('active',x===button));
                const side=genericView.querySelector('[data-workspace-status-side]');
                if(side)side.value=filter;
                refresh();
            };
        });
        const search=genericView.querySelector('[data-workspace-search]');
        const searchSide=genericView.querySelector('[data-workspace-search-side]');
        const updateTerm=value=>{
            term=String(value||'').trim().toLowerCase();
            if(search&&search.value!==value)search.value=value;
            if(searchSide&&searchSide.value!==value)searchSide.value=value;
            refresh();
        };
        if(search)search.oninput=e=>updateTerm(e.target.value);
        if(searchSide)searchSide.oninput=e=>updateTerm(e.target.value);
        const sortControl=genericView.querySelector('[data-workspace-sort]');
        if(sortControl)sortControl.onchange=e=>{sort=e.target.value;refresh();};
        const statusControl=genericView.querySelector('[data-workspace-status-side]');
        if(statusControl)statusControl.onchange=e=>{
            filter=e.target.value;
            genericView.querySelectorAll('[data-workspace-filter]').forEach(x=>x.classList.toggle('active',x.dataset.workspaceFilter===filter));
            refresh();
        };
        genericView.querySelector('.workspace-clear-filters').onclick=()=>{
            filter='all';term='';sort='name';
            if(search)search.value='';
            if(searchSide)searchSide.value='';
            if(sortControl)sortControl.value='name';
            if(statusControl)statusControl.value='all';
            genericView.querySelectorAll('[data-workspace-filter]').forEach(x=>x.classList.toggle('active',x.dataset.workspaceFilter==='all'));
            refresh();
        };

        genericView.querySelectorAll('[data-open-workspace]').forEach(button=>{
            button.onclick=async()=>{
                conversationView.classList.remove('hidden');
                genericView.classList.add('hidden');
                await selectWorkspace(button.dataset.openWorkspace,button.dataset.workspaceName);
            };
        });
        genericView.querySelectorAll('[data-workspace-admin]').forEach(button=>{
            button.onclick=()=>window.studioNavigate('admin');
        });
        genericView.querySelectorAll('[data-nav-quick]').forEach(button=>{
            button.onclick=()=>window.studioNavigate(button.dataset.navQuick);
        });
    }

    async function renderAdmin(){
        if(typeof showWorkspaces==='function') await showWorkspaces();
        else {
            conversationView.classList.add('hidden');
            genericView.classList.remove('hidden');
        }

        const me=await json('/api/auth/me').catch(()=>null);
        if(me?.user?.isAdministrator!==true) return;

        let panel=document.getElementById('studioDemoLoader');
        if(panel) return;

        panel=document.createElement('section');
        panel.id='studioDemoLoader';
        panel.className='studio-card studio-demo-loader';
        panel.innerHTML='<div class="studio-card-heading"><div><h2>Demo Data</h2><p>Load Carter Wedding with 50 wedding photos, linked People, messages, tasks, appointments and production data.</p></div><button class="primary" type="button" data-load-demo>Load Demo Data</button></div><p data-demo-status></p>';
        genericView.prepend(panel);

        panel.querySelector('[data-load-demo]').onclick=async()=>{
            const button=panel.querySelector('[data-load-demo]');
            const status=panel.querySelector('[data-demo-status]');
            button.disabled=true;
            status.textContent='Loading demo data…';
            try{
                const result=await json('/api/admin/demo/seed',{method:'POST'});
                status.textContent=result.message||'Demo data loaded.';
                await loadWorkspaces();
            }catch(error){
                status.textContent='Demo data could not be loaded: '+(error.message||error);
            }finally{
                button.disabled=false;
            }
        };
    }

    const routes={
        mywork:renderMyWork,
        workspaces:renderWorkspaces,
        pipeline:()=>showPipeline(),
        production:()=>showProduction(),
        frontdesk:()=>showFrontDesk(),
        reports:()=>showManagement(),
        admin:renderAdmin
    };

    window.studioNavigate=async function(view){
        const route=routes[view];
        if(!route) throw new Error('Unknown Studio navigation view: '+view);
        setActive(view);
        try{
            await route();
            setActive(view);
        }catch(error){
            conversationView.classList.add('hidden');
            genericView.classList.remove('hidden');
            genericView.innerHTML='<div class="studio-page-heading"><h1>Unable to load view</h1></div><div class="studio-card"><p>'+esc(error?.message||error)+'</p></div>';
            throw error;
        }
    };

    document.querySelectorAll('.rail [data-view]').forEach(button=>{
        button.type='button';
        button.onclick=()=>window.studioNavigate(button.dataset.view);
    });
    document.querySelectorAll('[data-top-view]').forEach(button=>{
        button.type='button';
        button.onclick=()=>window.studioNavigate(button.dataset.topView);
    });

    const topNew=document.getElementById('topNewButton');
    if(topNew) topNew.onclick=()=>document.getElementById('newRequestDialog')?.showModal();

    setActive('mywork');
})();