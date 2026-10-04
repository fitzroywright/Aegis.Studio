(() => {
    const rail = document.querySelector('.rail');
    const conversationView = document.getElementById('conversationView');
    const genericView = document.getElementById('genericView');
    if (!rail || !conversationView || !genericView) return;

    const icons = {
        mywork:'<path d="M4 10 12 3l8 7v10h-5v-6H9v6H4z"/>',
        workspaces:'<rect x="4" y="7" width="16" height="13" rx="2"/><path d="M8 7V4h8v3"/>',
        jobs:'<rect x="5" y="3" width="14" height="18" rx="2"/><path d="M8 8h8M8 12h8M8 16h5"/>',
        messages:'<path d="M4 5h16v11H8l-4 4z"/>',
        media:'<rect x="4" y="4" width="16" height="16" rx="2"/><circle cx="9" cy="9" r="1.5"/><path d="m6 17 4-4 3 3 2-2 3 3"/>',
        subjects:'<circle cx="12" cy="8" r="3"/><path d="M6 20c0-4 2.7-7 6-7s6 3 6 7"/>',
        tasks:'<rect x="4" y="4" width="16" height="16" rx="4"/><path d="m8 12 2 2 5-5"/>',
        pipeline:'<circle cx="5" cy="6" r="2"/><circle cx="19" cy="12" r="2"/><circle cx="5" cy="18" r="2"/><path d="M7 6h5v6h5M7 18h5v-6"/>',
        production:'<path d="M4 20V9l6 3V8l6 4V4h4v16z"/>',
        frontdesk:'<path d="M4 15a8 8 0 0 1 16 0M2 18h20M12 7V4m-2 0h4"/>',
        reports:'<path d="M5 20V10h4v10M10 20V4h4v16M15 20v-7h4v7"/>',
        admin:'<circle cx="12" cy="12" r="3"/><path d="M19 13.5v-3l-2-.7-.8-1.8.9-1.8-2.1-2.1-1.8.9-1.8-.8-.7-2h-3l-.7 2-1.8.8-1.8-.9L3.3 6.2l.9 1.8-.8 1.8-2 .7v3l2 .7.8 1.8-.9 1.8 2.1 2.1 1.8-.9 1.8.8.7 2h3l.7-2 1.8-.8 1.8.9 2.1-2.1-.9-1.8.8-1.8z"/>'
    };

    const items = [
        ['mywork','My Work'],
        ['workspaces','Workspaces'],
        ['jobs','Jobs'],
        ['messages','Messages'],
        ['media','Media'],
        ['subjects','People'],
        ['tasks','Tasks'],
        ['pipeline','Pipeline'],
        ['production','Production'],
        ['frontdesk','FrontDesk'],
        ['reports','Reports'],
        ['admin','Admin']
    ];

    rail.className='rail';
    rail.setAttribute('role','navigation');
    rail.setAttribute('aria-label','Main navigation');
    rail.innerHTML=items.map(([id,label])=>`
        <button type="button" class="brand-button" data-view="${id}">
            <span class="studio-nav-icon" aria-hidden="true">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">${icons[id]}</svg>
            </span>
            <span>${label}</span>
        </button>`).join('');

    const setActive=view=>{
        rail.querySelectorAll('[data-view]').forEach(button=>button.classList.toggle('active',button.dataset.view===view));
    };

    const showGeneric=(view,title,body='')=>{
        setActive(view);
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML=`<div class="studio-page-heading"><div><h1>${esc(title)}</h1></div></div>${body}`;
    };

    const openJobButtons=()=>{
        genericView.querySelectorAll('[data-open-job]').forEach(button=>{
            button.addEventListener('click',()=>openWorkspaceJob(button.dataset.openJob).catch(showError));
        });
    };

    const showError=error=>{
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML=`<div class="studio-page-heading"><h1>Unable to load view</h1></div><div class="studio-error-card">${esc(error?.message||error||'Unknown error')}</div>`;
    };

    const routes={
        mywork:async()=>{
            setActive('mywork');
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

            const recentJobs=[...jobs].sort((a,b)=>new Date(b.eventDate||b.createdAtUtc||0)-new Date(a.eventDate||a.createdAtUtc||0)).slice(0,6);
            const mediaSnapshots=await Promise.all(recentJobs.map(async job=>({
                job,
                media:await json('/api/jobs/'+job.id+'/media').catch(()=>({assets:[]}))
            })));

            const totalPhotos=mediaSnapshots.reduce((sum,x)=>sum+(x.media.assets?.length||0),0);
            const messageNotifications=notifications.filter(n=>/message|conversation|reply/i.test(String(n.eventType||n.title||n.type||'')));
            const activityNotifications=notifications.slice(0,8);

            const jobById=id=>jobs.find(j=>String(j.id)===String(id));
            const scheduleJob=entry=>jobById(entry.jobId)||{title:entry.title||'Scheduled session',clientName:entry.title||''};
            const formatTime=value=>new Date(value).toLocaleTimeString([],{hour:'numeric',minute:'2-digit'});
            const formatDay=value=>new Date(value).toLocaleDateString([],{month:'short',day:'numeric'});

            const recentCards=mediaSnapshots.slice(0,4).map(({job,media})=>{
                const asset=(media.assets||[]).find(a=>String(a.contentType||'').startsWith('image/'))||(media.assets||[])[0];
                const image=asset?.versionId?'/api/jobs/'+job.id+'/versions/by-id/'+asset.versionId+'/content':'';
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
                return `<button class="mw-session-row" data-open-job="${entry.jobId||job.id||''}">
                    <div class="mw-session-time"><strong>${formatTime(entry.startsAtUtc||entry.start||entry.startsAt)}</strong><span>${formatTime(entry.endsAtUtc||entry.end||entry.endsAt)}</span></div>
                    <div class="mw-session-thumb"><span>📷</span></div>
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
                                    <header><h2>Recent Work</h2><button data-nav="jobs">View All →</button></header>
                                    <div class="mw-recent-grid">${recentCards||'<div class="mw-empty">No recent work.</div>'}</div>
                                </section>
                            </div>

                            <div class="mw-two-col lower">
                                <section class="mw-card">
                                    <header><h2>My Tasks</h2><button data-nav="tasks">View All →</button></header>
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
                                <header><h2>Messages</h2><button data-nav="messages">View All →</button></header>
                                <div>${messageRows}</div>
                                <button class="mw-link-button" data-nav="messages">Show all messages →</button>
                            </section>
                            <section class="mw-card">
                                <header><h2>Studio Activity</h2><button data-nav="reports">View All →</button></header>
                                <div>${activityRows}</div>
                            </section>
                        </aside>
                    </div>
                </section>`;

            genericView.querySelectorAll('[data-open-job]').forEach(button=>button.addEventListener('click',()=>{
                if(button.dataset.openJob)openWorkspaceJob(button.dataset.openJob).catch(showError);
            }));
            genericView.querySelectorAll('[data-nav]').forEach(button=>button.addEventListener('click',()=>navigate(button.dataset.nav)));
        },
        workspaces:async()=>{
            setActive('workspaces');
            const workspaces=await json('/api/workspaces');
            showGeneric('workspaces','Workspaces','<div class="reference-workspace-grid">'+workspaces.map(w=>`<button class="reference-workspace-card" data-open-workspace="${w.id}"><span><strong>${esc(w.name)}</strong><small>Open workspace</small></span><span>›</span></button>`).join('')+'</div>');
            genericView.querySelectorAll('[data-open-workspace]').forEach(button=>button.addEventListener('click',async()=>{
                const workspace=workspaces.find(w=>w.id===button.dataset.openWorkspace);
                conversationView.classList.remove('hidden');
                genericView.classList.add('hidden');
                setActive('workspaces');
                await selectWorkspace(workspace.id,workspace.name);
            }));
        },
        jobs:async()=>{
            const jobs=await json('/api/jobs');
            showGeneric('jobs','Jobs','<div class="studio-card studio-table-list">'+jobs.map(job=>`<button class="studio-list-row" data-open-job="${job.id}"><strong>${esc(job.title)}</strong><span>${esc(job.clientName||'')} · ${esc(job.status||'')}</span></button>`).join('')+'</div>');
            openJobButtons();
        },
        messages:async()=>{
            setActive('messages');
            if(typeof window.showStudioMessages!=='function')
                throw new Error('Studio Messages view is unavailable.');
            await window.showStudioMessages();
            setActive('messages');
        },
        media:async()=>{
            showGeneric('media','Media','<section class="studio-card" data-media-global><p>Loading media requiring attention…</p></section>');
            await renderMediaAttentionQueue(genericView.querySelector('[data-media-global]'));
        },
        subjects:async()=>{
            const jobs=await json('/api/jobs');
            const snapshots=await Promise.all(jobs.slice(0,30).map(async job=>({job,media:await json('/api/jobs/'+job.id+'/media').catch(()=>({subjects:[]}))})));
            const people=snapshots.flatMap(x=>(x.media.subjects||[]).filter(s=>!s.isSystem).map(s=>({job:x.job,subject:s})));
            showGeneric('subjects','People','<div class="studio-card studio-table-list">'+people.map(x=>`<button class="studio-list-row" data-open-job="${x.job.id}"><strong>${esc(x.subject.name)}</strong><span>${esc(x.job.title)} · ${esc(x.subject.imageCount??0)} photos</span></button>`).join('')+'</div>');
            openJobButtons();
        },
        tasks:async()=>{
            const tasks=await json('/api/operations/activities');
            showGeneric('tasks','Tasks','<div class="studio-card studio-table-list">'+tasks.map(t=>`<button class="studio-list-row" data-open-job="${t.jobId}"><strong>${esc(t.title)}</strong><span>${esc(t.status)}${t.assignedTo?' · '+esc(studioFirstName(t.assignedTo)):''}</span></button>`).join('')+'</div>');
            openJobButtons();
        },
        pipeline:async()=>{setActive('pipeline');await showPipeline();setActive('pipeline');},
        production:async()=>{setActive('production');await showProduction();setActive('production');},
        frontdesk:async()=>{setActive('frontdesk');await showFrontDesk();setActive('frontdesk');},
        reports:async()=>{setActive('reports');await showManagement();setActive('reports');},
        admin:async()=>{
            setActive('admin');
            await showWorkspaces();
            setActive('admin');
            conversationView.classList.add('hidden');
            genericView.classList.remove('hidden');
            const me=await json('/api/auth/me').catch(()=>null);
            if(me?.user?.isAdministrator!==true){
                genericView.innerHTML='<div class="studio-page-heading"><h1>Admin</h1></div><div class="studio-card"><p>Administrator access is required.</p></div>';
                return;
            }
            genericView.innerHTML='<div class="studio-page-heading"><h1>Admin</h1></div><section id="studioDemoLoader" class="studio-card studio-demo-loader"><div class="studio-card-heading"><div><h2>Demo Data</h2><p>Load Carter Wedding with 50 wedding photos, linked People, messages, tasks, appointments and production data.</p></div><button class="primary" type="button" data-load-demo>Load Demo Data</button></div><p data-demo-status></p></section>';
            const panel=genericView.querySelector('#studioDemoLoader');
            panel.querySelector('[data-load-demo]').addEventListener('click',async()=>{
                const button=panel.querySelector('[data-load-demo]');
                const status=panel.querySelector('[data-demo-status]');
                button.disabled=true;status.textContent='Loading demo data…';
                try{
                    const result=await json('/api/admin/demo/seed',{method:'POST'});
                    status.textContent=result.message||'Demo data loaded.';
                    await loadWorkspaces();
                }catch(error){
                    status.textContent='Demo data could not be loaded: '+(error.message||error);
                }finally{button.disabled=false;}
            });
        }
    };

    const navigate=async view=>{
        const route=routes[view];
        if(!route)return showError(new Error('Unknown Studio view: '+view));
        try{await route();}catch(error){showError(error);}
    };

    // Bind the actual rendered buttons directly. This is deliberately simple:
    // one visible button = one real navigation handler.
    const bindNavigationButtons=()=>{
        rail.querySelectorAll('[data-view]').forEach(button=>{
            button.onclick=event=>{
                event.preventDefault();
                event.stopPropagation();
                navigate(button.dataset.view);
            };
        });
    };
    bindNavigationButtons();

    // Fallback delegation covers any later nav DOM replacement.
    document.addEventListener('click',event=>{
        const button=event.target.closest('.rail [data-view]');
        if(!button)return;
        event.preventDefault();
        event.stopPropagation();
        navigate(button.dataset.view);
    });

    window.studioNavigate=navigate;

    const topNew=document.getElementById('topNewButton');
    if(topNew)topNew.addEventListener('click',()=>{
        const dialog=document.getElementById('newRequestDialog');
        if(dialog?.showModal)dialog.showModal();
    });

    setActive('workspaces');
})();
