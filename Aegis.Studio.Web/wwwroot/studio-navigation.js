// Keep the existing FrontDesk, WorkDay and administration screens as destinations.
(() => {
    const rail=document.querySelector('.rail');
    rail.className='studio-top-nav';rail.setAttribute('role','navigation');rail.setAttribute('aria-label','Main navigation');
    document.querySelector('.topbar').after(rail);
    const items=[['dashboard','Dashboard'],['pipeline','Pipeline'],['production','Production'],['frontdesk','FrontDesk'],['mywork','My Work'],['admin','Admin']];
    const icons={
        mywork:'<rect x="5" y="5" width="14" height="16" rx="2"/><path d="M9 5V3h6v2M8 12l2 2 5-5M8 18h8"/>',
        dashboard:'<path d="M4 20V11h4v9M10 20V5h4v15M16 20V8h4v12"/>',
        workspaces:'<path d="M3 7V5h7l2 2h9v13H3z"/><path d="M3 10h18"/>',
        pipeline:'<circle cx="5" cy="6" r="2"/><circle cx="19" cy="12" r="2"/><circle cx="5" cy="18" r="2"/><path d="M7 6h5v6h5M7 18h5v-6"/>',
        production:'<path d="M3 20V9l6 3V8l6 4V4h4l2 16zM7 16h1m4 0h1m4 0h1"/>',
        frontdesk:'<path d="M4 15a8 8 0 0 1 16 0M2 18h20M12 7V4m-2 0h4"/>',
        workday:'<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M7 3v4m10-4v4M3 10h18M8 14h3m3 0h3m-9 4h3"/>',
        admin:'<path d="M12 3l8 3v6c0 5-8 9-8 9s-8-4-8-9V6z"/><path d="M8 12l3 3 5-6"/>'
    };
    rail.innerHTML=items.map(([id,label])=>`<button class="brand-button" data-view="${id}"><span class="studio-nav-icon icon-${id}" aria-hidden="true"><svg viewBox="0 0 24 24" focusable="false" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">${icons[id]}</svg></span><span>${label}</span></button>`).join('');
    const showError=e=>{genericView.innerHTML=`<h1>Unable to load view</h1><p>${esc(e.message)}</p>`;};
    const generic=view=>{activateNavigation(view);conversationView.classList.add('hidden');genericView.classList.remove('hidden');genericView.innerHTML='<p>Loading…</p>';};
    const routes={
        mywork:async()=>{generic('mywork');const auth=await json('/api/auth/me');const me=auth.user||auth;const user=String(me.userName||me.loginName||'').toLowerCase();const [tasks,workdayResponse]=await Promise.all([json('/api/operations/activities?assignedTo='+encodeURIComponent(user)),fetch('/api/workday/bootstrap')]);const mine=tasks.filter(t=>user&&String(t.assignedTo||'').toLowerCase()===user&&!['Completed','Cancelled'].includes(t.status));let assignments=[],notice='';if(workdayResponse.ok){const workday=await workdayResponse.json();assignments=[...(workday.today||[]),...(workday.upcoming||[])].filter(a=>a.jobId);}else if(workdayResponse.status!==404)notice='WorkDay assignments could not be loaded.';genericView.innerHTML=`<h1>My Work</h1><h2>My shoots / WorkDay</h2>${assignments.map(a=>`<article class="task-card"><button class="link-button" data-open-job="${a.jobId}">${esc(a.title)}</button><small>${esc(new Date(a.startsAtUtc).toLocaleString())} · ${esc(a.status)}</small><small>${esc(a.location||'Location not set')}</small><p>${esc(a.instructions||'')}</p></article>`).join('')||`<p>${esc(notice||'No upcoming WorkDay assignments.')}</p>`}<h2>My tasks</h2>${mine.map(t=>`<article class="task-card"><button class="link-button" data-open-job="${t.jobId}">${esc(t.title)}</button><small>${esc(t.status)}${t.dueAtUtc?' · Due '+esc(new Date(t.dueAtUtc).toLocaleString()):''}</small></article>`).join('')||'<p>No open tasks assigned to you.</p>'}`;bindJobs();await renderMediaAttentionQueue(genericView);},
        dashboard:async()=>{await showManagement();activateNavigation('dashboard');const h=genericView.querySelector('h1');if(h)h.textContent='Dashboard';},
        workspaces:async()=>{activateView('conversations');activateNavigation('workspaces');await loadConversations();},
        pipeline:()=>showPipeline(),
        production:()=>showProduction(),
        frontdesk:()=>showFrontDesk(),
        workday:async()=>{window.location.assign('/workday-management.html');},
        admin:async()=>{await showWorkspaces();activateNavigation('admin');}
    };
    function jobCard(job){return `<article class="task-card"><button class="link-button" data-open-job="${job.id}">${esc(job.title)}</button><small>${esc(job.clientName)} · ${esc(job.status)}</small><small>${esc(job.eventDate||'Date not set')} · ${esc(job.assignedTo||'Unassigned')}</small><button class="action" data-plan-job="${job.id}">Schedule / availability</button></article>`;}
    function bindJobs(){genericView.querySelectorAll('[data-open-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.openJob).catch(showError));}
    rail.querySelectorAll('button').forEach(b=>b.onclick=()=>Promise.resolve(routes[b.dataset.view]()).catch(showError));
    loadFrontDeskRole();
    const newRequest=document.getElementById('newRequestButton');
    newRequest.hidden=false;
    rail.querySelector('[data-view="mywork"]').after(newRequest);
})();
