// Keep the existing FrontDesk, WorkDay and administration screens as destinations.
(() => {
    const rail=document.querySelector('.rail');
    rail.className='studio-top-nav';rail.setAttribute('role','navigation');rail.setAttribute('aria-label','Main navigation');
    document.querySelector('.topbar').after(rail);
    const items=[['mywork','My Work'],['dashboard','Dashboard'],['workspaces','Workspaces'],['pipeline','Pipeline'],['production','Production'],['frontdesk','FrontDesk'],['workday','WorkDay'],['admin','Admin']];
    rail.innerHTML=items.map(([id,label])=>`<button class="brand-button" data-view="${id}">${label}</button>`).join('');
    const showError=e=>{genericView.innerHTML=`<h1>Unable to load view</h1><p>${esc(e.message)}</p>`;};
    const generic=view=>{activateNavigation(view);conversationView.classList.add('hidden');genericView.classList.remove('hidden');genericView.innerHTML='<p>Loading…</p>';};
    const routes={
        mywork:async()=>{generic('mywork');const auth=await json('/api/auth/me');const me=auth.user||auth;const user=String(me.userName||me.loginName||'').toLowerCase();const [tasks,workdayResponse]=await Promise.all([json('/api/operations/activities?assignedTo='+encodeURIComponent(user)),fetch('/api/workday/bootstrap')]);const mine=tasks.filter(t=>user&&String(t.assignedTo||'').toLowerCase()===user&&!['Completed','Cancelled'].includes(t.status));let assignments=[],notice='';if(workdayResponse.ok){const workday=await workdayResponse.json();assignments=[...(workday.today||[]),...(workday.upcoming||[])].filter(a=>a.jobId);}else if(workdayResponse.status!==404)notice='WorkDay assignments could not be loaded.';genericView.innerHTML=`<h1>My Work</h1><h2>My shoots / WorkDay</h2>${assignments.map(a=>`<article class="task-card"><button class="link-button" data-open-job="${a.jobId}">${esc(a.title)}</button><small>${esc(new Date(a.startsAtUtc).toLocaleString())} · ${esc(a.status)}</small></article>`).join('')||`<p>${esc(notice||'No upcoming WorkDay assignments.')}</p>`}<h2>My tasks</h2>${mine.map(t=>`<article class="task-card"><button class="link-button" data-open-job="${t.jobId}">${esc(t.title)}</button><small>${esc(t.status)}${t.dueAtUtc?' · Due '+esc(new Date(t.dueAtUtc).toLocaleString()):''}</small></article>`).join('')||'<p>No open tasks assigned to you.</p>'}`;bindJobs();},
        dashboard:async()=>{await showManagement();activateNavigation('dashboard');const h=genericView.querySelector('h1');if(h)h.textContent='Dashboard';},
        workspaces:async()=>{activateView('conversations');activateNavigation('workspaces');await loadConversations();},
        pipeline:async()=>{generic('pipeline');const jobs=await json('/api/jobs');const stages=[['Needs scheduling',['New','Confirmed','Inquiry','Needs Scheduling']],['Scheduled',['Scheduled']],['Photography',['Photography','Shoot Complete']],['Production',['Production']],['Delivery',['Ready for Client','Completed']]];const known=new Set(stages.flatMap(s=>s[1]));const remaining=jobs.filter(j=>!known.has(j.status));genericView.innerHTML=`<div class="job-detail-header"><div><h1>Pipeline</h1><p>Jobs from scheduling through delivery.</p></div><button class="action" id="pipelineSchedule">Open scheduling</button></div><div class="pipeline-columns">${stages.map(([name,statuses])=>`<section><h2>${name}</h2>${jobs.filter(j=>statuses.includes(j.status)).map(jobCard).join('')||'<p>No jobs</p>'}</section>`).join('')}${remaining.length?`<section><h2>Other stages</h2>${remaining.map(jobCard).join('')}</section>`:''}</div>`;genericView.querySelector('#pipelineSchedule').onclick=()=>showSchedule().catch(showError);bindJobs();},
        production:()=>showProduction(),
        frontdesk:()=>showFrontDesk(),
        workday:async()=>{window.location.assign('/workday-management.html');},
        admin:async()=>{await showWorkspaces();activateNavigation('admin');}
    };
    function jobCard(job){return `<article class="task-card"><button class="link-button" data-open-job="${job.id}">${esc(job.title)}</button><small>${esc(job.clientName)} · ${esc(job.status)}</small></article>`;}
    function bindJobs(){genericView.querySelectorAll('[data-open-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.openJob).catch(showError));}
    rail.querySelectorAll('button').forEach(b=>b.onclick=()=>Promise.resolve(routes[b.dataset.view]()).catch(showError));
    loadFrontDeskRole();
    // Request creation belongs to FrontDesk, not the global workspace header.
    document.getElementById('newRequestButton').hidden=true;
})();
