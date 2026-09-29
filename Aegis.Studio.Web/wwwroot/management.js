if (!document.querySelector('link[href="/management.css"]')) {
    const managementStyles = document.createElement('link');
    managementStyles.rel = 'stylesheet';
    managementStyles.href = '/management.css?v=20260929-fullops2';
    document.head.appendChild(managementStyles);
}

showManagement = async function() {
    activateNavigation('management');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    genericView.innerHTML = '<p>Loading Dashboard…</p>';
    try {
        const now=new Date(), scheduleFrom=new Date(now.getTime()-14*86400000), scheduleTo=new Date(now.getTime()+90*86400000), capacityTo=new Date(now.getTime()+7*86400000);
        const [jobs, scheduleEntries, activities, workflowStages, capacity, escalations, workflows] = await Promise.all([
            json('/api/jobs'),
            json('/api/schedule/entries?from=' + encodeURIComponent(scheduleFrom.toISOString()) + '&to=' + encodeURIComponent(scheduleTo.toISOString())),
            json('/api/operations/activities'),
            json('/api/workflow/stages'),
            json('/api/schedule/capacity?from=' + encodeURIComponent(now.toISOString()) + '&to=' + encodeURIComponent(capacityTo.toISOString())).catch(()=>[]),
            json('/api/escalations/open').catch(()=>[]),
            json('/api/workflow-operations/summary').catch(()=>null)
        ]);
        const active=jobs.filter(job=>String(job.status).toLowerCase()!=='completed');
        const overdue=activities.filter(item=>!['Completed','Cancelled'].includes(item.status)&&item.dueAtUtc&&new Date(item.dueAtUtc)<now);
        const upcoming=scheduleEntries.filter(item=>new Date(item.startsAtUtc)>=now&&new Date(item.startsAtUtc)<=capacityTo);
        const unassigned=active.filter(job=>!job.assignedTo);
        const scheduledJobs=new Set(scheduleEntries.filter(x=>x.jobId).map(x=>x.jobId));
        const missingDates=active.filter(job=>!job.eventDate&&!scheduledJobs.has(job.id));
        const conflictCount=managementScheduleConflictCount(scheduleEntries);
        const stuckJobs=new Set(overdue.map(x=>x.jobId).filter(Boolean));
        genericView.innerHTML = `
            <div class="job-detail-header"><div><h1>Management Dashboard</h1><p>Work, status, responsibility and exceptions from Studio's live operational data.</p></div><button class="action" onclick="scanEscalations()">Run escalation scan</button></div>
            <div class="management-summary-strip">
                <span><b>${active.length}</b> active jobs</span>
                <span><b>${overdue.length}</b> overdue tasks</span>
                <span><b>${upcoming.length}</b> shoots / schedule items next 7 days</span>
                <span><b>${unassigned.length}</b> unassigned jobs</span>
                <span><b>${missingDates.length}</b> missing dates</span>
                <span class="${conflictCount?'risk':''}"><b>${conflictCount}</b> schedule conflicts</span>
                <span class="${stuckJobs.size?'risk':''}"><b>${stuckJobs.size}</b> jobs with overdue work</span>
            </div>
            <section class="job-section"><h2>Pipeline overview</h2>${renderManagementPipelineFlow(jobs, activities, workflowStages, scheduleEntries)}</section>
            <section class="job-section"><h2>Photographer workload</h2>${renderPhotographerWorkload(jobs, scheduleEntries, activities, capacity)}</section>
            <section class="job-section management-gantt-section"><div class="management-section-heading"><div><h2>Job Gantt</h2><p class="muted">Read-only view of real job, schedule and production dates. Open a job from its row.</p></div></div>${renderManagementGantt(jobs, scheduleEntries, activities)}</section>
            ${escalations.length?'<section class="job-section"><h2>Escalations</h2>'+renderEscalations(escalations)+'</section>':''}
            ${workflows?'<section class="job-section"><h2>Workflow engine</h2><div class="management-summary-strip"><span><b>'+workflows.running+'</b> running</span><span><b>'+workflows.waiting+'</b> waiting</span><span><b>'+workflows.failed+'</b> failed</span><span><b>'+workflows.completed+'</b> completed</span></div></section>':''}`;
    } catch (error) {
        genericView.innerHTML = `<h1>Dashboard</h1><p role="alert">${esc(error.message || error)}</p><button class="action" onclick="showManagement()">Retry</button>`;
    }
};

showAccounts = async function() {
    activateNavigation('frontdesk');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    try {
        const [invoices, jobs] = await Promise.all([json('/api/accounts/invoices'), json('/api/jobs')]);
        const outstanding = invoices.filter(x => ['Issued','Overdue'].includes(x.status)).reduce((sum,x)=>sum+x.totalAmount,0);
        const paid = invoices.filter(x => x.status === 'Paid').reduce((sum,x)=>sum+x.totalAmount,0);
        genericView.innerHTML = `
            <div class="job-detail-header"><div><h1>Front Desk — Accounts</h1><p>Invoices and client portal access remain tied directly to the operational Job.</p></div><button class="primary" onclick="createInvoiceFromAccounts()">New invoice</button></div>
            <div class="cards"><div class="metric">Outstanding<strong>${money(outstanding)}</strong></div><div class="metric">Paid invoices<strong>${money(paid)}</strong></div><div class="metric">Invoices<strong>${invoices.length}</strong></div></div>
            <section class="job-section"><h2>Invoices</h2>${invoices.length ? invoices.map(invoice => `
                <div class="financial-row"><div><b>${esc(invoice.invoiceNumber)}</b><small>${esc(invoice.clientName)} · ${esc(invoice.status)} · due ${new Date(invoice.dueAtUtc).toLocaleDateString()}</small></div><div class="job-actions"><strong>${money(invoice.totalAmount)}</strong>${invoice.status==='Draft'?`<button class="action" onclick="setInvoiceStatus('${invoice.id}','Issued')">Issue</button>`:''}${invoice.status==='Issued'||invoice.status==='Overdue'?`<button class="action" onclick="setInvoiceStatus('${invoice.id}','Paid')">Mark paid</button>`:''}<button class="action" onclick="issueClientPortal('${invoice.jobId}')">Client portal</button></div></div>`).join('') : '<p class="muted">No invoices yet.</p>'}</section>
            <section class="job-section"><h2>Jobs available for billing</h2>${renderJobRows(jobs)}</section>`;
        bindJobRows();
    } catch (error) {
        genericView.innerHTML = `<h1>Accounts</h1><p class="muted">You do not have billing access.</p>`;
    }
};

function renderManagementGantt(jobs, scheduleEntries, activities) {
    const activeJobs=(jobs||[]).filter(job=>job.status!=='Completed');
    if(!activeJobs.length)return '<p class="muted">No active jobs.</p>';

    const DAY=86400000;
    const today=new Date(); today.setHours(0,0,0,0);
    const dated=[];
    for(const job of activeJobs){
        const schedule=(scheduleEntries||[]).filter(item=>item.jobId===job.id);
        const tasks=(activities||[]).filter(item=>item.jobId===job.id);
        const candidates=[];
        if(job.eventDate)candidates.push(new Date(job.eventDate+'T12:00:00'));
        schedule.forEach(item=>{candidates.push(new Date(item.startsAtUtc));candidates.push(new Date(item.endsAtUtc));});
        tasks.forEach(item=>{if(item.scheduledAtUtc)candidates.push(new Date(item.scheduledAtUtc));if(item.dueAtUtc)candidates.push(new Date(item.dueAtUtc));});
        candidates.push(new Date(job.createdAt));
        const valid=candidates.filter(date=>Number.isFinite(+date));
        const start=new Date(Math.min(...valid.map(date=>+date)));
        const end=new Date(Math.max(...valid.map(date=>+date)));
        if(end<start)end.setTime(start.getTime());
        dated.push({job,start,end,tasks});
    }

    const earliest=new Date(Math.min(+today,...dated.map(x=>+x.start)));
    const latestLimit=new Date(today.getTime()+90*DAY);
    const latest=new Date(Math.max(+latestLimit,...dated.map(x=>+x.end)));
    earliest.setHours(0,0,0,0); latest.setHours(0,0,0,0);
    const totalDays=Math.max(1,Math.round((latest-earliest)/DAY)+1);
    const weekCount=Math.ceil(totalDays/7);
    const width=Math.max(840,weekCount*112);
    const weeks=Array.from({length:weekCount},(_,index)=>{
        const date=new Date(earliest.getTime()+index*7*DAY);
        return date.toLocaleDateString(undefined,{month:'short',day:'numeric'});
    });
    const todayPct=Math.max(0,Math.min(100,((today-earliest)/DAY)/totalDays*100));

    const rows=dated.sort((a,b)=>a.start-b.start).map(({job,start,end,tasks})=>{
        const left=Math.max(0,((start-earliest)/DAY)/totalDays*100);
        const span=Math.max(1,((end-start)/DAY+1)/totalDays*100);
        const production=tasks.filter(item=>item.status!=='Cancelled');
        const completed=production.filter(item=>item.status==='Completed').length;
        const progress=production.length?Math.round(completed/production.length*100):0;
        return `<div class="management-gantt-row">
            <button class="management-gantt-job" type="button" onclick="openJob('${job.id}')"><strong>${esc(job.title)}</strong><small>${esc(job.clientName||'Client not linked')} · ${esc(job.status)}</small></button>
            <div class="management-gantt-track" style="--gantt-width:${width}px">
                <div class="management-gantt-today" style="left:${todayPct}%"></div>
                <button class="management-gantt-bar" type="button" onclick="openJob('${job.id}')" style="left:${left}%;width:${span}%">
                    <span style="width:${progress}%"></span><b>${progress}%</b>
                </button>
            </div>
        </div>`;
    }).join('');

    return `<div class="management-gantt-shell">
        <div class="management-gantt-scroll">
            <div class="management-gantt-grid" style="--gantt-width:${width}px">
                <div class="management-gantt-head">
                    <div class="management-gantt-job-head">Job</div>
                    <div class="management-gantt-axis">${weeks.map(label=>`<span>${esc(label)}</span>`).join('')}</div>
                </div>
                ${rows}
            </div>
        </div>
    </div>`;
}

function renderEscalations(values) {
    if (!values.length) return '<p class="muted">No open escalations.</p>';
    return values.map(value => `<div class="financial-row"><div><b>L${value.level} · ${esc(value.subject)}</b><small>${esc(value.detail)} · opened ${new Date(value.openedAtUtc).toLocaleString()}</small></div><button class="action" onclick="ackEscalation('${value.id}')">Acknowledge</button></div>`).join('');
}

function renderBuckets(values, amount) {
    if (!values.length) return '<p class="muted">No data.</p>';
    const max = Math.max(...values.map(x => amount ? Number(x.amount) : Number(x.count)), 1);
    return `<div class="report-bars">${values.map(value => { const numeric = amount ? Number(value.amount) : Number(value.count); return `<div class="report-bar-row"><span>${esc(value.label)}</span><div class="report-track"><div class="report-fill" style="width:${Math.max(2,numeric/max*100)}%"></div></div><strong>${amount ? money(numeric) : numeric}</strong></div>`; }).join('')}</div>`;
}

async function scanEscalations(){ await json('/api/escalations/scan',{method:'POST'}); await showManagement(); }
async function ackEscalation(id){ await json(`/api/escalations/${id}/acknowledge`,{method:'POST'}); await showManagement(); }
async function setInvoiceStatus(id,status){ await json(`/api/accounts/invoices/${id}/status`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({status})}); await showAccounts(); }

async function createInvoiceFromAccounts() {
    const jobs = await json('/api/jobs');
    if (!jobs.length) return alert('There are no jobs to bill.');
    const choices = jobs.map((x,i)=>`${i+1}. ${x.title} — ${x.clientName}`).join('\n');
    const answer = prompt(`Choose job number:\n${choices}`, '1');
    const index = Number(answer)-1;
    if (!Number.isInteger(index)||index<0||index>=jobs.length) return;
    const description = prompt('Invoice item', 'Photography services'); if (!description) return;
    const amount = Number(prompt('Amount (JMD)', '0.00')); if (!Number.isFinite(amount)||amount<0) return alert('Enter a valid amount.');
    const tax = Number(prompt('Tax rate %', '0')) || 0;
    const due = new Date(); due.setDate(due.getDate()+14);
    await json(`/api/jobs/${jobs[index].id}/invoices`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({lines:[{description,quantity:1,unitPrice:amount}],taxRatePercent:tax,dueAtUtc:due.toISOString(),currency:'JMD'})});
    await showAccounts();
}

async function issueClientPortal(jobId) {
    const result = await json(`/api/jobs/${jobId}/client-portal`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({expiresInDays:30})});
    await navigator.clipboard?.writeText(result.publicUrl).catch(()=>{});
    prompt('Client portal URL (copied when browser permission allows)', result.publicUrl);
}


function managementScheduleConflictCount(entries) {
    let count=0;
    const values=(entries||[]).filter(x=>String(x.status).toLowerCase()!=='cancelled');
    for(let i=0;i<values.length;i++)for(let j=i+1;j<values.length;j++){
        const a=values[i],b=values[j];
        if(new Date(a.startsAtUtc)<new Date(b.endsAtUtc)&&new Date(b.startsAtUtc)<new Date(a.endsAtUtc)
            &&(a.resourceIds||[]).some(id=>(b.resourceIds||[]).includes(id))) count++;
    }
    return count;
}

function renderManagementPipelineFlow(jobs, activities, workflowStages, scheduleEntries) {
    const active=(jobs||[]).filter(job=>String(job.status).toLowerCase()!=='completed');
    const ordered=(workflowStages||[]).filter(stage=>String(stage).toLowerCase()!=='completed');
    if(!ordered.length) return '<p class="muted">Workflow stages are not configured.</p>';
    const stageFor=status=>{
        const value=String(status||'').toLowerCase();
        const exact=ordered.find(stage=>stage.toLowerCase()===value);
        if(exact)return exact;
        if(['new','confirmed','inquiry','needs scheduling'].includes(value))return ordered.find(stage=>stage.toLowerCase()==='needs scheduling')||ordered[0];
        return ordered.find(stage=>stage.toLowerCase()==='production'&&value.includes('production'))||null;
    };
    const now=Date.now(), scheduledJobs=new Set((scheduleEntries||[]).filter(x=>x.jobId).map(x=>x.jobId));
    return '<div class="management-flow">'+ordered.map((stage,index)=>{
        const stageJobs=active.filter(job=>stageFor(job.status)===stage);
        const stageIds=new Set(stageJobs.map(x=>x.id));
        const overdue=(activities||[]).filter(a=>stageIds.has(a.jobId)&&!['Completed','Cancelled'].includes(a.status)&&a.dueAtUtc&&new Date(a.dueAtUtc).getTime()<now);
        const stuck=new Set(overdue.map(x=>x.jobId)).size;
        const unassigned=stageJobs.filter(job=>!job.assignedTo).length;
        const missingDate=stageJobs.filter(job=>!job.eventDate&&!scheduledJobs.has(job.id)).length;
        return `<button class="management-flow-stage" type="button" onclick="showJobs()"><span>${index+1}</span><strong>${esc(stage)}</strong><b>${stageJobs.length} jobs</b><small>${stuck} stuck · ${overdue.length} overdue tasks · ${unassigned} unassigned · ${missingDate} missing dates</small></button>`;
    }).join('<span class="management-flow-arrow">›</span>')+'</div>';
}

function renderPhotographerWorkload(jobs, entries, activities, capacity) {
    if(!(capacity||[]).length) return '<p class="muted">Photographer capacity is unavailable for this account or no active photographers are configured.</p>';
    const now=new Date(), weekEnd=new Date(now.getTime()+7*86400000);
    const rows=capacity.map(person=>{
        const scheduled=(entries||[]).filter(e=>(e.resourceIds||[]).includes(person.scheduleResourceId)&&new Date(e.endsAtUtc)>=now);
        const week=scheduled.filter(e=>new Date(e.startsAtUtc)<=weekEnd);
        const linked=new Set(scheduled.map(e=>e.jobId).filter(Boolean));
        const activeJobs=(jobs||[]).filter(j=>linked.has(j.id)&&String(j.status).toLowerCase()!=='completed');
        const overdue=(activities||[]).filter(a=>!['Completed','Cancelled'].includes(a.status)&&a.dueAtUtc&&new Date(a.dueAtUtc)<now&&String(a.assignedTo||'').toLowerCase()===String(person.userName||'').toLowerCase()).length;
        const conflicts=managementScheduleConflictCount(scheduled);
        return {person,active:activeJobs.length,week:week.length,overdue,conflicts};
    }).sort((a,b)=>b.person.utilizationPercent-a.person.utilizationPercent);
    window.managementWorkloadContext={jobs,entries};
    return `<div class="management-workload-table"><div class="management-workload-head"><span>Photographer</span><span>Active jobs</span><span>Next 7 days</span><span>Committed</span><span>Available</span><span>Utilization</span><span>Overdue</span><span>Conflicts</span></div>${rows.map(x=>`<button type="button" class="management-workload-row" onclick="showPhotographerWorkload('${x.person.scheduleResourceId}','${esc(x.person.displayName).replace(/'/g,"&#39;")}')"><strong>${esc(x.person.displayName)}</strong><span>${x.active}</span><span>${x.week}</span><span>${(x.person.scheduledMinutes/60).toFixed(1)} h</span><span>${(x.person.remainingMinutes/60).toFixed(1)} h</span><span class="${x.person.state==='OverCapacity'?'risk':''}">${x.person.utilizationPercent.toFixed(1)}% · ${esc(x.person.state)}</span><span class="${x.overdue?'risk':''}">${x.overdue}</span><span class="${x.conflicts?'risk':''}">${x.conflicts}</span></button>`).join('')}</div><p class="muted">Capacity and utilization come from configured daily capacity, actual schedule duration and recorded Unavailable/Leave time.</p>`;
}

function showPhotographerWorkload(resourceId, displayName) {
    const context=window.managementWorkloadContext||{jobs:[],entries:[]};
    const now=new Date();
    const work=context.entries.filter(e=>(e.resourceIds||[]).includes(resourceId)&&new Date(e.endsAtUtc)>=now).sort((a,b)=>new Date(a.startsAtUtc)-new Date(b.startsAtUtc));
    const dialog=studioDialog(displayName+' — scheduled work',`<div class="management-person-work">${work.map(entry=>{const job=context.jobs.find(j=>j.id===entry.jobId);return `<article class="front-desk-row"><div><strong>${esc(entry.title)}</strong><small>${esc(new Date(entry.startsAtUtc).toLocaleString())} – ${esc(new Date(entry.endsAtUtc).toLocaleString())} · ${esc(entry.location||'Location not set')}</small></div>${job?`<button type="button" class="action" data-management-job="${job.id}">Open job</button>`:''}</article>`;}).join('')||'<p class="muted">No upcoming scheduled work.</p>'}</div>`);
    dialog.querySelectorAll('[data-management-job]').forEach(button=>button.onclick=()=>{dialog.close();openWorkspaceJob(button.dataset.managementJob);});
}
