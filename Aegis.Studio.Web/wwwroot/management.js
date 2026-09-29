if (!document.querySelector('link[href="/management.css"]')) {
    const managementStyles = document.createElement('link');
    managementStyles.rel = 'stylesheet';
    managementStyles.href = '/management.css';
    document.head.appendChild(managementStyles);
}

showManagement = async function() {
    activateNavigation('management');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    try {
        const [dashboard, statuses, escalations, workflows] = await Promise.all([
            json('/api/management/dashboard'),
            json('/api/management/jobs-by-status'),
            json('/api/escalations/open'),
            json('/api/workflow-operations/summary')
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
