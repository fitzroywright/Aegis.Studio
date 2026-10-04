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
            showGeneric('mywork','My Work','<div class="my-work-grid"><section class="studio-card" data-my-work-tasks><h2>My Tasks</h2><p>Loading…</p></section><section class="studio-card" data-my-work-media><h2>Recent Media</h2><p>Loading…</p></section></div>');
            const auth=await json('/api/auth/me');
            const me=auth.user||auth;
            const user=String(me.userName||me.loginName||'').toLowerCase();
            const tasks=await json('/api/operations/activities?assignedTo='+encodeURIComponent(user)).catch(()=>[]);
            const open=tasks.filter(t=>!['Completed','Cancelled'].includes(t.status));
            const host=genericView.querySelector('[data-my-work-tasks]');
            host.innerHTML='<div class="studio-card-heading"><h2>My Tasks</h2><span>'+open.length+'</span></div>'+open.slice(0,12).map(t=>`<button class="studio-list-row" data-open-job="${t.jobId}"><strong>${esc(t.title)}</strong><span>${esc(t.status)}${t.dueAtUtc?' · '+esc(new Date(t.dueAtUtc).toLocaleDateString()):''}</span></button>`).join('')||'<p>No open tasks assigned to you.</p>';
            openJobButtons();
            const mediaHost=genericView.querySelector('[data-my-work-media]');
            mediaHost.innerHTML='<div class="studio-card-heading"><h2>Recent Media</h2></div>';
            await renderMediaAttentionQueue(mediaHost);
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
            await showMessages();
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

    // Document-level delegation survives all DOM rewrites and prevents dead nav buttons.
    document.addEventListener('click',event=>{
        const button=event.target.closest('.rail [data-view]');
        if(!button)return;
        event.preventDefault();
        event.stopPropagation();
        navigate(button.dataset.view);
    },true);

    window.studioNavigate=navigate;

    const topNew=document.getElementById('topNewButton');
    if(topNew)topNew.addEventListener('click',()=>{
        const dialog=document.getElementById('newRequestDialog');
        if(dialog?.showModal)dialog.showModal();
    });

    setActive('workspaces');
})();
