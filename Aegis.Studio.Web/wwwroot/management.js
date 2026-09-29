if (!document.querySelector('link[href="/management.css"]')) {
    const managementStyles = document.createElement('link');
    managementStyles.rel = 'stylesheet';
    managementStyles.href = '/management.css?v=20260929-formpass';
    document.head.appendChild(managementStyles);
}

showManagement = async function() {
    activateNavigation('management');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    try {
        const [dashboard, statuses, escalations, workflows, jobs, scheduleEntries, activities, resources] = await Promise.all([
            json('/api/management/dashboard'),
            json('/api/management/jobs-by-status'),
            json('/api/escalations/open'),
            json('/api/workflow-operations/summary'),
            json('/api/jobs'),
            json('/api/schedule/entries?from=' + encodeURIComponent(new Date(Date.now()-14*86400000).toISOString()) + '&to=' + encodeURIComponent(new Date(Date.now()+90*86400000).toISOString())),
            json('/api/operations/activities'),
            json('/api/schedule/resources')
        ]);
        genericView.innerHTML = `
            <div class="job-detail-header"><div><h1>Management Dashboard</h1><p>Live operations, customer demand, production risk, workflow health.</p></div><button class="action" onclick="scanEscalations()">Run escalation scan</button></div>
            <div class="cards">
                <div class="metric">Open conversations<strong>${dashboard.openConversations}</strong></div>
                <div class="metric">Active jobs<strong>${dashboard.activeJobs}</strong></div>
                <div class="metric">Overdue production<strong>${dashboard.overdueActivities}</strong></div>
                <div class="metric">Blocked work<strong>${dashboard.blockedActivities}</strong></div>
                <div class="metric">Failed workflows<strong>${dashboard.failedWorkflows}</strong></div>
                <div class="metric">Open escalations<strong>${escalations.length}</strong></div>
            </div>
            <section class="job-section"><h2>Pipeline overview</h2>${renderManagementPipelineFlow(jobs, activities)}</section>
            <section class="job-section"><h2>Photographer workload</h2>${renderPhotographerWorkload(jobs, scheduleEntries, resources)}</section>
            <section class="job-section management-gantt-section"><div class="management-section-heading"><div><h2>Job Gantt</h2><p class="muted">Active jobs, schedule commitments and production activity across the next 90 days.</p></div></div>${renderManagementGantt(jobs, scheduleEntries, activities)}</section>
            <section class="job-section"><h2>Escalations</h2>${renderEscalations(escalations)}</section>
            <section class="job-section"><h2>Jobs by status</h2>${renderBuckets(statuses, false)}</section>
            <section class="job-section"><h2>Workflow engine</h2><div class="cards"><div class="metric">Running<strong>${workflows.running}</strong></div><div class="metric">Waiting<strong>${workflows.waiting}</strong></div><div class="metric">Failed<strong>${workflows.failed}</strong></div><div class="metric">Completed<strong>${workflows.completed}</strong></div></div></section>`;
    } catch (error) {
        genericView.innerHTML = `<h1>Management</h1><p class="muted">Management dashboard requires Administrator access.</p><pre>${esc(error.message || error)}</pre>`;
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
        return \`<div class="management-gantt-row">
            <button class="management-gantt-job" type="button" onclick="openJob('\${job.id}')"><strong>\${esc(job.title)}</strong><small>\${esc(job.clientName||'Client not linked')} · \${esc(job.status)}</small></button>
            <div class="management-gantt-track" style="--gantt-width:\${width}px">
                <div class="management-gantt-today" style="left:\${todayPct}%"></div>
                <button class="management-gantt-bar" type="button" onclick="openJob('\${job.id}')" style="left:\${left}%;width:\${span}%">
                    <span style="width:\${progress}%"></span><b>\${progress}%</b>
                </button>
            </div>
        </div>\`;
    }).join('');

    return \`<div class="management-gantt-shell">
        <div class="management-gantt-scroll">
            <div class="management-gantt-grid" style="--gantt-width:\${width}px">
                <div class="management-gantt-head">
                    <div class="management-gantt-job-head">Job</div>
                    <div class="management-gantt-axis">\${weeks.map(label=>\`<span>\${esc(label)}</span>\`).join('')}</div>
                </div>
                \${rows}
            </div>
        </div>
    </div>\`;
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


function renderManagementPipelineFlow(jobs, activities) {
    const active=(jobs||[]).filter(job=>job.status!=='Completed');
    const ordered=[];
    for(const job of active) if(!ordered.includes(job.status)) ordered.push(job.status);
    if(!ordered.length) return '<p class="muted">No active jobs.</p>';
    const now=Date.now();
    return '<div class="management-flow">'+ordered.map((stage,index)=>{
        const stageJobs=active.filter(job=>job.status===stage);
        const overdue=(activities||[]).filter(a=>a.status!=='Completed'&&a.status!=='Cancelled'&&a.dueAtUtc&&new Date(a.dueAtUtc).getTime()<now&&stageJobs.some(j=>j.id===a.jobId)).length;
        const unassigned=stageJobs.filter(job=>!job.assignedTo).length;
        return `<button class="management-flow-stage" type="button" onclick="showJobs()"><span>${index+1}</span><strong>${esc(stage)}</strong><b>${stageJobs.length} jobs</b><small>${overdue} overdue · ${unassigned} unassigned</small></button>`;
    }).join('<span class="management-flow-arrow">›</span>')+'</div>';
}

function renderPhotographerWorkload(jobs, entries, resources) {
    const staff=(resources||[]).filter(r=>r.isActive&&String(r.resourceType).toLowerCase()==='staff');
    if(!staff.length) return '<p class="muted">No active Staff scheduling resources are configured.</p>';
    const now=new Date(), weekEnd=new Date(now.getTime()+7*86400000);
    const rows=staff.map(person=>{
        const scheduled=(entries||[]).filter(e=>(e.resourceIds||[]).includes(person.id)&&new Date(e.endsAtUtc)>=now);
        const week=scheduled.filter(e=>new Date(e.startsAtUtc)<=weekEnd);
        const hours=week.reduce((sum,e)=>sum+Math.max(0,(new Date(e.endsAtUtc)-new Date(e.startsAtUtc))/3600000),0);
        const conflicts=scheduled.reduce((count,e,i)=>count+scheduled.slice(i+1).filter(o=>new Date(e.startsAtUtc)<new Date(o.endsAtUtc)&&new Date(o.startsAtUtc)<new Date(e.endsAtUtc)).length,0);
        const linked=new Set(scheduled.map(e=>e.jobId).filter(Boolean));
        const active=(jobs||[]).filter(j=>linked.has(j.id)&&j.status!=='Completed').length;
        return {person,active,week:week.length,hours,conflicts};
    }).sort((a,b)=>b.hours-a.hours);
    return `<div class="management-workload-table"><div class="management-workload-head"><span>Photographer / staff</span><span>Active jobs</span><span>Next 7 days</span><span>Committed</span><span>Conflicts</span></div>${rows.map(x=>`<button type="button" class="management-workload-row" onclick="activateView('schedule')"><strong>${esc(x.person.name)}</strong><span>${x.active}</span><span>${x.week}</span><span>${x.hours.toFixed(1)} h</span><span class="${x.conflicts?'risk':''}">${x.conflicts}</span></button>`).join('')}</div><p class="muted">Committed time is calculated from actual schedule duration; Studio does not infer utilization from job count.</p>`;
}
