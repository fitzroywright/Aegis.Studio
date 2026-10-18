/* Finance and Management use the same jobs, transactions, sessions and tasks. */
let studioPeriodDays=90;
const studioColors=['#1677ff','#26c58b','#9454ff','#ff9400','#f34865','#05a7b9'];
function studioFinancials(job){
    const events=job.financialEvents||[];
    const charges=events.filter(e=>/^(charge|adjustment|discount|POS Sale|POS Refund)$/i.test(e.kind)).reduce((n,e)=>n+Number(e.amount),0);
    const received=events.filter(e=>/^(payment|deposit|refund|reversal|POS Sale|POS Refund)$/i.test(e.kind)).reduce((n,e)=>n+Number(e.amount),0);
    return {charges,received,balance:Math.max(0,charges-received)};
}
function studioMetric(value,label,color='blue',detail='',drill=''){
    return `<article class="op-metric" ${drill?`role="button" tabindex="0" data-metric="${drill}"`:""}><span class="op-icon ${color}">${color==='green'?'$':color==='purple'?'▧':color==='orange'?'▣':'✓'}</span><span><b>${esc(value)}</b><span>${esc(label)}</span>${detail?`<small>${esc(detail)}</small>`:''}</span></article>`;
}
function studioCard(title,body,action=''){
    return `<section class="op-card"><header><h2>${esc(title)}</h2>${action}</header><div class="op-body">${body}</div></section>`;
}
function studioPeriod(){return `<select data-period aria-label="Reporting period"><option value="30" ${studioPeriodDays===30?'selected':''}>Last 30 days</option><option value="90" ${studioPeriodDays===90?'selected':''}>Last 90 days</option><option value="365" ${studioPeriodDays===365?'selected':''}>Last year</option></select>`;}
async function studioReadOperations(){
    const now=new Date(),start=new Date(now.getTime()-studioPeriodDays*86400000),end=new Date(now.getTime()+30*86400000);
    const [jobs,tasks,sessions,photographers,invoices]=await Promise.all([
        json('/api/jobs'),json('/api/operations/activities'),json('/api/schedule/entries?from='+encodeURIComponent(start.toISOString())+'&to='+encodeURIComponent(end.toISOString())),json('/api/photographers'),json('/api/accounts/invoices')
    ]);
    const transactions=jobs.flatMap(job=>(job.financialEvents||[]).map(e=>({...e,jobId:job.id,client:job.clientName,title:job.title}))).filter(e=>new Date(e.createdAt||e.createdAtUtc)>=start&&new Date(e.createdAt||e.createdAtUtc)<=now);
    const receipts=transactions.filter(e=>/^(payment|deposit|refund|reversal|POS Sale|POS Refund)$/i.test(e.kind));
    const balances=jobs.map(job=>({job,...studioFinancials(job)}));
    return {jobs,tasks,sessions,photographers,invoices,transactions,receipts,balances,now,start};
}
function studioRevenueChart(receipts){
    const days=Array.from({length:12},(_,i)=>new Date(Date.now()-(12-i)*studioPeriodDays/12*86400000));
    const totals=days.map((day,i)=>receipts.filter(e=>new Date(e.createdAt||e.createdAtUtc)>=day&&(i===11||new Date(e.createdAt||e.createdAtUtc)<days[i+1])).reduce((n,e)=>n+Number(e.amount),0));
    const max=Math.max(1,...totals),min=Math.min(0,...totals),scale=125/(max-min),zero=150+min*scale;
    const y=n=>zero-n*scale,points=totals.map((n,i)=>`${42+i*32},${y(n)}`).join(' ');
    return `<svg class="op-chart" viewBox="0 0 420 185" role="img" aria-label="Net receipts over the selected period"><path d="M40 15V150M40 ${zero}H407" fill="none" stroke="#dce2ea"/><text x="2" y="20">${esc(money(max))}</text><text x="16" y="${zero}">0</text>${totals.map((n,i)=>`<rect x="${42+i*32}" y="${Math.min(zero,y(n))}" width="20" height="${Math.abs(n*scale)}" fill="${n<0?'#f34865':'#1677ff'}"><title>${money(n)}</title></rect>`).join('')}<polyline points="${points}" fill="none" stroke="#9454ff" stroke-width="2"/><text x="42" y="173">${days[0].toLocaleDateString()}</text><text x="340" y="173">Today</text></svg>`;
}
function studioComposition(values,label){
    const total=values.reduce((n,x)=>n+x.value,0);let offset=0;
    const gradient=values.map((x,i)=>{const first=offset;offset+=total?x.value/total*100:0;return `${studioColors[i%studioColors.length]} ${first}% ${offset}%`;}).join(',');
    return `<div class="op-composition"><div class="op-donut" style="background:${total?'conic-gradient('+gradient+')':'#edf0f4'}"><span><b>${esc(total.toLocaleString())}</b><small>${esc(label)}</small></span></div><div class="op-legend">${values.map((x,i)=>`<span><i style="background:${studioColors[i%studioColors.length]}"></i>${esc(x.label)} <b>${total?Math.round(x.value/total*100):0}%</b></span>`).join('')}</div></div>`;
}
function studioTransactionsTable(transactions){
    return `<table class="studio-table"><thead><tr><th>Date</th><th>Client</th><th>Description / Method</th><th>Amount</th></tr></thead><tbody>${transactions.slice().sort((a,b)=>new Date(b.createdAt||b.createdAtUtc)-new Date(a.createdAt||a.createdAtUtc)).slice(0,100).map(e=>`<tr><td>${esc(new Date(e.createdAt||e.createdAtUtc).toLocaleDateString())}</td><td><button data-open-job="${e.jobId}">${esc(e.client)}</button></td><td>${esc(e.kind)}<small>${esc(e.note||'')} · ${esc(e.paymentMethod||'Method unrecorded')} · ${esc(e.recordedBy||'')}</small></td><td>${esc(money(e.amount))}</td></tr>`).join('')}</tbody></table>`;
}
function studioExportTransactions(transactions){
    const quote=value=>'"'+String(value??'').replace(/"/g,'""')+'"';
    const rows=[['Date','Client','Job','Type','Amount','Method','Recorded by','Note'],...transactions.map(e=>[e.createdAt||e.createdAtUtc,e.client,e.title,e.kind,e.amount,e.paymentMethod,e.recordedBy,e.note])];
    const url=URL.createObjectURL(new Blob([rows.map(row=>row.map(quote).join(',')).join('\r\n')],{type:'text/csv;charset=utf-8'}));
    const a=document.createElement('a');a.href=url;a.download='studio-transactions.csv';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
}
function studioBindOperations(data,refresh){
    genericView.querySelectorAll('[data-open-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.openJob).catch(e=>alert(e.message)));
    genericView.querySelector('[data-period]').onchange=e=>{studioPeriodDays=Number(e.target.value);refresh().catch(e=>alert(e.message));};
    genericView.querySelector('[data-export]').onclick=()=>studioExportTransactions(data.transactions);
    genericView.querySelectorAll('[data-metric]').forEach(b=>{b.onclick=()=>{const values=b.dataset.metric==='receipts'?data.receipts:data.tasks.filter(t=>b.dataset.metric==='blocked'?t.status==='Blocked':!['Completed','Cancelled'].includes(t.status)&&t.dueAtUtc&&new Date(t.dueAtUtc)<data.now);const d=studioDialog(b.dataset.metric==='receipts'?'Receipt history':'Work requiring attention',b.dataset.metric==='receipts'?studioTransactionsTable(values):values.slice(0,100).map(t=>`<button data-open-job="${t.jobId}">${esc(t.title)} · ${esc(t.blockedReason||t.status)}</button>`).join('')||'<p>No matching work.</p>');d.querySelectorAll('[data-open-job]').forEach(x=>x.onclick=()=>{d.close();openWorkspaceJob(x.dataset.openJob).catch(e=>alert(e.message));});};b.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();b.click();}};});
}
async function showAccounts(){
    const current=StudioPage.begin('finance');
    conversationView.classList.add('hidden');genericView.classList.remove('hidden');
    const data=await studioReadOperations();data.workspaces=await json('/api/workspaces');
    const receivables=window.studioWorkflowCapabilities?.finance?await json('/api/finance/receivables'):[];
    if(!current())return;
    const net=data.receipts.reduce((n,e)=>n+Number(e.amount),0),charge=data.transactions.filter(e=>e.kind==='Charge').reduce((n,e)=>n+Number(e.amount),0),outstanding=data.balances.reduce((n,x)=>n+x.balance,0);
    const methods=[...new Set(data.receipts.map(e=>e.kind))].map(kind=>({label:kind,value:data.receipts.filter(e=>e.kind===kind).length}));
    genericView.innerHTML=StudioPage.html({title:'Finance',tools:`${studioPeriod()}<button data-export>Export</button><button class="primary" onclick="createInvoiceFromAccounts()">New Invoice</button>`,summary:StudioPage.metrics([['Net receipts',money(net)],['Outstanding',money(outstanding)],['Invoices',data.invoices.length]]),attention:StudioPage.metrics([['Overdue invoices',data.invoices.filter(i=>i.status==='Overdue').length]]),content:`
    <div class="op-metrics">${studioMetric(money(net),'Net receipts','green','','receipts')}${studioMetric(money(charge),'Charges','blue')}${studioMetric(money(outstanding),'Outstanding balance','purple','All jobs')}${studioMetric(data.invoices.filter(i=>i.status==='Overdue').length,'Overdue invoices','orange')}</div>
    ${window.studioWorkflowCapabilities?.finance?studioCard('Receivables and contractual credit',receivables.map(r=>`<button type="button" class="op-link-row" data-open-job="${esc(r.jobId)}"><span><strong>${esc(r.title)} · ${esc(r.customer)}</strong><small>${esc(r.stage)} · ${r.decision.authorized?'Finance release authorized':'Release held'} · ${(r.authorizations||[]).filter(a=>!a.revoked_at_utc).map(a=>'Due '+esc(a.payment_due_date)+(new Date(a.payment_due_date+'T23:59:59-05:00')<new Date()?' · Overdue':'')).join('; ')}</small></span><b>${esc(money(r.decision.balance))}</b></button>`).join('')||'<p>No outstanding receivables.</p>'):''}
    <div class="st-dashboard-grid">${studioCard('Revenue Trend',studioRevenueChart(data.receipts))}${studioCard('Transaction Types',studioComposition(methods,'transactions'))}${studioCard('Recent Transactions','<div data-finance-filter></div><div data-finance-results></div>')}${studioCard('Invoices',data.invoices.map(i=>`<article class="job-row"><span><button class="link-button" data-open-job="${i.jobId}">${esc(i.invoiceNumber)}</button><small>${esc(i.clientName)} · ${esc(i.status)} · ${money(i.totalAmount)}</small></span><span>${i.status==='Draft'?`<button onclick="setInvoiceStatus('${i.id}','Issued')">Issue</button>`:''}<button onclick="issueClientPortal('${i.jobId}')">Client Portal</button></span></article>`).join('')||'<p>No invoices.</p>')}</div>`});
    StudioPage.bind(genericView,current);
    studioBindOperations(data,showAccounts);
    const controls=genericView.querySelector('[data-finance-filter]'),results=genericView.querySelector('[data-finance-results]');
    controls.innerHTML=`<label>Find transaction<input data-finance-search type="search" placeholder="Client, job, reference or cashier"></label><label>From<input type="date" data-finance-from value="${data.start.toISOString().slice(0,10)}"></label><label>Through<input type="date" data-finance-to value="${data.now.toISOString().slice(0,10)}"></label><label>Workspace<select data-finance-workspace><option value="">All workspaces</option>${data.workspaces.map(w=>`<option value="${w.id}">${esc(w.name)}</option>`).join('')}</select></label><label>Type<select data-finance-kind><option value="">All types</option>${[...new Set(data.transactions.map(e=>e.kind))].map(k=>`<option>${esc(k)}</option>`).join('')}</select></label><label>Method<select data-finance-method><option value="">All methods</option>${[...new Set(data.receipts.map(e=>e.paymentMethod||'Unrecorded'))].map(k=>`<option>${esc(k)}</option>`).join('')}</select></label><button data-export-filter>Export matching</button><p data-reconcile></p>`;
    let filtered=[];function render(){const term=controls.querySelector('input').value.toLowerCase(),kind=controls.querySelector('[data-finance-kind]').value,method=controls.querySelector('[data-finance-method]').value,workspace=controls.querySelector('[data-finance-workspace]').value,from=controls.querySelector('[data-finance-from]').value,to=controls.querySelector('[data-finance-to]').value;filtered=data.transactions.filter(e=>(!term||[e.client,e.title,e.note,e.recordedBy].join(' ').toLowerCase().includes(term))&&(!kind||e.kind===kind)&&(!method||(e.paymentMethod||'Unrecorded')===method)&&(!workspace||data.jobs.some(j=>j.id===e.jobId&&(j.workspaceId===workspace||(j.participatingWorkspaceIds||[]).includes(workspace))))&&(!from||new Date(e.createdAt||e.createdAtUtc)>=new Date(from+'T00:00:00'))&&(!to||new Date(e.createdAt||e.createdAtUtc)<=new Date(to+'T23:59:59.999')));results.innerHTML=studioTransactionsTable(filtered)+`<p>${Math.min(filtered.length,100)} of ${filtered.length} transactions shown. Export includes all matches.</p>`;results.querySelectorAll('[data-open-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.openJob).catch(e=>alert(e.message)));const receipts=filtered.filter(e=>/^(payment|deposit|refund|reversal|POS Sale|POS Refund)$/i.test(e.kind));const sum=receipts.reduce((n,e)=>n+Number(e.amount),0);StudioPage.setCount(filtered.length+' transactions');controls.querySelector('[data-reconcile]').textContent=`${receipts.length} receipt/refund records · net ${money(sum)} · ${receipts.filter(e=>!e.paymentMethod).length} missing payment methods`;}controls.querySelectorAll('input').forEach(x=>x.oninput=render);controls.querySelectorAll('select').forEach(x=>x.onchange=render);controls.querySelector('[data-export-filter]').onclick=()=>studioExportTransactions(filtered);render();
}
let operationsContext={workspace:'',attention:'open',search:'',stage:'',assignee:''};
async function showOperationsManagement(){
    const current=StudioPage.begin('operations-management');
    conversationView.classList.add('hidden');genericView.classList.remove('hidden');
    const [data,jobRecords,workspaces,workloadData]=await Promise.all([json('/api/operations-management'),json('/api/jobs'),json('/api/workspaces'),json('/api/production/workload')]);const webhooks=data.canDecideOverrides?await json('/api/operations-management/webhooks'):[];if(!current())return;
    const pending=data.overrideRequests.filter(r=>r.status==='Pending');
    const workload=window.StudioWorkload?StudioWorkload.model(workloadData,jobRecords):null;
    const tasks=workload?.activities||[],openTasks=tasks.filter(workload?.active||(()=>false));
    const deliveryOverdue=new Set(openTasks.filter(a=>a.activityType==='Delivery'&&workload.overdue(a)).map(a=>a.jobId));
    const deliveryRisk=new Set(openTasks.filter(a=>a.activityType==='Delivery'&&Number.isFinite(Date.parse(a.dueAtUtc))&&!workload.overdue(a)&&openTasks.some(t=>t.jobId===a.jobId&&(workload.waiting(t)||!t.assignedTo))).map(a=>a.jobId));
    const deliveryKnown=new Set(tasks.filter(a=>a.activityType==='Delivery'&&Number.isFinite(Date.parse(a.dueAtUtc))).map(a=>a.jobId));
    const overdueJobs=new Set(openTasks.filter(workload?.overdue||(()=>false)).map(a=>a.jobId));
    const workloadJobs=key=>new Set(openTasks.filter(a=>workload.matches(a,key)).map(a=>a.jobId));
    const exceptions=data.jobs.filter(j=>j.workspaceId==='7fe72808-a713-4d99-8b39-481c86061008'||!j.owner||j.blockedReason||j.timing?.light==='red');
    genericView.innerHTML=StudioPage.html({title:'Operations',className:'st-operations-content',summaryTitle:'Workspace Details',tools:'<button type="button" data-operations-refresh>Refresh</button>',
        summary:'<div id="operationsWorkspaceDetails">'+StudioPage.metrics(data.canDecideOverrides?[['Pending overrides',pending.length],['Workflow exceptions',exceptions.length]]:[['Accessible jobs',data.jobs.length],['Workflow exceptions',exceptions.length]])+'</div>',
        attention:data.canDecideOverrides?'<p>Decisions require an Operations Manager. Requesters cannot decide their own override.</p>':'<p>Showing operational jobs available to your account. Deposit overrides and website delivery administration require Operations Manager permission.</p>',
        content:`<section class="operations-situation" aria-label="Management situation"><div class="workload-indicators" data-operations-indicators></div><p>Delivery overdue uses recorded Delivery activity due dates. Delivery concern means a future due date with blocked or unassigned activities; completion timing is unknown. Stage alerts use existing elapsed-time thresholds, not delivery forecasts.</p><div class="operations-overview-links"><label>Workflow stage<select data-operations-stage><option value="">All stages</option>${[...new Set(data.jobs.map(j=>j.stage))].map(x=>`<option>${esc(x)}</option>`).join('')}</select></label><label>Staff workload<select data-operations-assignee><option value="">All staff</option><option value="__unassigned">Unassigned</option>${[...new Set(openTasks.map(t=>t.assignedTo).filter(Boolean))].map(x=>`<option value="${esc(x)}">${esc(studioFirstName(x))}</option>`).join('')}</select></label><button data-operations-production>Open matching Production queue →</button><button data-operations-schedule>Staff schedules →</button></div><div data-operations-bottlenecks></div><p>Available staff capacity and equipment utilization: unknown. Schedule retains recorded availability and assignments; equipment notes are not utilization records.</p></section><section class="operations-workspace-overview"><header><h2>Workspace Operations</h2><input type="search" id="operationsWorkspaceSearch" placeholder="Search workspaces…" aria-label="Search workspaces"></header><div class="operational-grid-scroll"><table class="operational-grid"><thead><tr><th>Workspace</th><th>Messaging Address</th><th>Open Jobs</th><th>Exceptions</th></tr></thead><tbody id="operationsWorkspaceRows"></tbody></table></div></section>
        <nav class="operations-view-switch" aria-label="Operations sections"><button type="button" data-operations-panel="jobs" aria-pressed="true">Job Assignments</button>${data.canDecideOverrides?'<button type="button" data-operations-panel="overrides" aria-pressed="false">Deposit Overrides</button><button type="button" data-operations-panel="delivery" aria-pressed="false">Website Delivery</button>':''}</nav>
        <section class="op-card operations-job-panel" data-operations-section="jobs"><header><h2>Jobs & Responsibilities</h2></header><div class="operations-job-filters"><label>Workspace<select id="operationsWorkspaceFilter"><option value="">All Workspaces</option>${workspaces.map(w=>'<option value="'+esc(w.id)+'">'+esc(w.name)+'</option>').join('')}</select></label><label>Show<select id="operationsAttentionFilter"><option value="open">All Open Jobs</option><option value="attention">Needs Attention</option><option value="delivery-overdue">Delivery overdue</option><option value="delivery-risk">Delivery concern</option><option value="delivery-unknown">Delivery due date unknown</option><option value="stage-alert">Stage time alert</option><option value="overdue">Overdue activities</option><option value="waiting">Blocked / waiting</option><option value="unassigned">Unassigned activities</option><option value="handoffs">Recorded handoffs awaiting completion</option></select></label></div><div class="operational-grid-scroll"><table class="operational-grid"><thead><tr><th>Job / Client</th><th>Stage</th><th>Job Lead</th><th>Next Action</th></tr></thead><tbody id="operationsJobRows"></tbody></table></div></section>
        ${data.canDecideOverrides?`<section class="op-card" data-operations-section="overrides" hidden><header><h2>Booking override requests</h2></header><table class="op-table"><thead><tr><th>Job</th><th>Requested by</th><th>Reason</th><th>Status</th><th>Decision</th></tr></thead><tbody>${data.overrideRequests.map(r=>`<tr data-override-row="${esc(r.job_id)}" data-override-state="${esc(r.status)}"><td><button type="button" class="st-link" data-open-job="${esc(r.job_id)}">${esc(r.title)}</button></td><td>${esc(r.requested_by)}</td><td>${esc(r.reason)}</td><td><span class="studio-status-pill">${esc(r.status)}</span></td><td>${r.status==='Pending'&&r.requested_by.toLowerCase()!==data.userName.toLowerCase()?`<button type="button" data-override="${esc(r.id)}" data-job="${esc(r.job_id)}" data-decision="Approved">Approve</button><button type="button" data-override="${esc(r.id)}" data-job="${esc(r.job_id)}" data-decision="Rejected">Reject</button>`:esc(r.decision_reason||'Awaiting another Operations Manager')}</td></tr>`).join('')||'<tr><td colspan="5">No override requests.</td></tr>'}</tbody></table></section>
        <section class="op-card" data-operations-section="delivery" hidden><header><h2>Website integration delivery</h2></header>${webhooks.map(h=>`<article><strong>${esc(h.event_type)} · ${esc(h.destination_key)}</strong><p>${h.delivered_at_utc?'Accepted by website endpoint':h.last_error?'Retrying: '+esc(h.last_error):'Queued'} · Attempts ${esc(h.attempts)}</p>${h.job_id?`<button type="button" class="st-link" data-open-job="${esc(h.job_id)}">Open Job</button>`:''}</article>`).join('')||'<p>No webhook deliveries recorded.</p>'}</section>` : ''}
        `});
    StudioPage.bind(genericView,current);
    const recordMap=new Map(jobRecords.map(j=>[String(j.id),j]));
    const exceptional=new Set(exceptions.map(j=>String(j.jobId)));
    const isClosed=j=>typeof StudioSubjectPicker!=='undefined'?StudioSubjectPicker.isClosed({status:j.stage}):['Completed','Closed','Cancelled','Archived','Delivered'].includes(j.stage);
    function renderOperationalJobs(){
        const workspaceId=genericView.querySelector('#operationsWorkspaceFilter').value;
        const filter=genericView.querySelector('#operationsAttentionFilter').value,stage=genericView.querySelector('[data-operations-stage]').value,assignee=genericView.querySelector('[data-operations-assignee]').value;
        operationsContext={workspace:workspaceId,attention:filter,stage,assignee,search:genericView.querySelector('#operationsWorkspaceSearch').value};
        const scoped=data.jobs.filter(j=>!isClosed(j)&&(!workspaceId||String(j.workspaceId)===workspaceId)&&(!stage||j.stage===stage)&&(!assignee||openTasks.some(t=>t.jobId===j.jobId&&(assignee==='__unassigned'?!t.assignedTo:t.assignedTo===assignee))));
        const sets={'delivery-overdue':deliveryOverdue,'delivery-risk':deliveryRisk,'delivery-unknown':new Set(data.jobs.filter(j=>!deliveryKnown.has(j.jobId)).map(j=>j.jobId)),'stage-alert':new Set(data.jobs.filter(j=>j.timing?.light==='red'||j.timing?.light==='amber').map(j=>j.jobId)),overdue:overdueJobs,waiting:workloadJobs('waiting'),unassigned:workloadJobs('unassigned'),handoffs:workloadJobs('handoffs'),attention:exceptional};
        const shown=scoped.filter(j=>filter==='open'||sets[filter]?.has(j.jobId));
        const metric=(key,label)=>`<button type="button" class="workload-indicator" data-situation="${key}" aria-pressed="${filter===key}"><strong>${scoped.filter(j=>sets[key]?.has(j.jobId)).length}</strong><span>${label}</span></button>`;
        genericView.querySelector('[data-operations-indicators]').innerHTML=[['delivery-overdue','Jobs past delivery due'],['delivery-risk','Jobs with delivery concern'],['delivery-unknown','Delivery due date unknown'],['stage-alert','Stage time alerts'],['overdue','Jobs with overdue work'],['waiting','Jobs with waiting work'],['unassigned','Jobs with unassigned work'],['handoffs','Jobs with handoffs']].map(([key,label])=>metric(key,label)).join('')+(data.canDecideOverrides?`<button type="button" class="workload-indicator" data-pending-decisions><strong>${pending.filter(r=>scoped.some(j=>j.jobId===r.job_id)).length}</strong><span>Pending override decisions</span></button>`:'');
        genericView.querySelectorAll('[data-situation]').forEach(b=>b.onclick=()=>{genericView.querySelector('#operationsAttentionFilter').value=b.dataset.situation;renderOperationalJobs();});
        genericView.querySelector('[data-pending-decisions]')?.addEventListener('click',()=>{genericView.querySelector('[data-operations-panel="overrides"]').click();const ids=new Set(scoped.map(j=>j.jobId));genericView.querySelectorAll('[data-override-row]').forEach(row=>row.hidden=!ids.has(row.dataset.overrideRow)||row.dataset.overrideState!=='Pending');});
        const scopedIds=new Set(scoped.map(j=>j.jobId)),scopedTasks=openTasks.filter(t=>scopedIds.has(t.jobId));
        genericView.querySelector('[data-operations-bottlenecks]').innerHTML='<p>Waiting activities by production stage: '+[...new Set(scopedTasks.map(t=>t.activityType))].map(type=>`<button type="button" data-bottleneck="${esc(type)}">${esc(type)}: ${scopedTasks.filter(t=>t.activityType===type&&workload.waiting(t)).length}</button>`).join(' ')+'</p><p>Assigned workload (activity counts; capacity unknown): '+[...new Set(scopedTasks.map(t=>t.assignedTo).filter(Boolean))].map(person=>`<button type="button" data-staff-workload="${esc(person)}">${esc(studioFirstName(person))}: ${scopedTasks.filter(t=>t.assignedTo===person).length}</button>`).join(' ')+'</p>';
        genericView.querySelectorAll('[data-bottleneck]').forEach(b=>b.onclick=()=>StudioWorkload.production('waiting',workspaceId,assignee,b.dataset.bottleneck,[...scopedIds]));
        genericView.querySelectorAll('[data-staff-workload]').forEach(b=>b.onclick=()=>StudioWorkload.production('',workspaceId,b.dataset.staffWorkload,'',[...scopedIds]));
        genericView.querySelector('#operationsJobRows').innerHTML=shown.map(j=>{const record=recordMap.get(String(j.jobId));return '<tr><td><button type="button" class="st-link" data-open-job="'+esc(j.jobId)+'">'+esc(record?.title||record?.jobNumber||j.jobId)+'</button><small>'+esc(record?.clientName||'')+'</small></td><td><span class="studio-status-pill">'+esc(j.stage)+'</span></td><td>'+esc(j.owner?studioFirstName(j.owner):'Unassigned')+'</td><td>'+esc(j.blockedReason||j.nextAction||j.timing?.label||'Review workflow')+'</td></tr>';}).join('')||'<tr><td colspan="4">No open jobs match these filters.</td></tr>';
        genericView.querySelectorAll('#operationsJobRows [data-open-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.openJob).catch(e=>alert(e.message)));
        StudioPage.setCount(shown.length+' open jobs · '+exceptions.length+' exceptions');
        const workspace=workspaces.find(w=>String(w.id)===workspaceId);
        const details=genericView.querySelector('#operationsWorkspaceDetails');
        if(details)details.innerHTML=workspace?'<dl class="studio-context-details"><dt>Workspace</dt><dd>'+esc(workspace.name)+'</dd><dt>Messaging address</dt><dd>'+esc(workspace.emailAddress||(workspace.isUnassigned||String(workspace.name).toLowerCase()==='unassigned'?'Not required':'⚠ Email should be set'))+'</dd><dt>Default job lead</dt><dd>'+esc(workspace.defaultJobLead?studioFirstName(workspace.defaultJobLead):'Not set')+'</dd><dt>Open jobs</dt><dd>'+shown.length+'</dd></dl>':StudioPage.metrics([['Accessible jobs',data.jobs.length],['Workflow exceptions',exceptions.length]]);
    }
    function renderOperationalWorkspaces(){
        const term=genericView.querySelector('#operationsWorkspaceSearch').value.toLowerCase();
        genericView.querySelector('#operationsWorkspaceRows').innerHTML=workspaces.filter(w=>String(w.name).toLowerCase().includes(term)).map(w=>{const related=data.jobs.filter(j=>String(j.workspaceId)===String(w.id)&&!isClosed(j));const unassigned=w.isUnassigned||String(w.name).toLowerCase()==='unassigned';return '<tr><td><button type="button" class="st-link" data-operations-workspace="'+esc(w.id)+'">'+esc(w.name)+'</button></td><td>'+esc(w.emailAddress||(unassigned?'Not required':'⚠ Email should be set'))+'</td><td>'+related.length+'</td><td>'+related.filter(j=>exceptional.has(String(j.jobId))).length+'</td></tr>';}).join('')||'<tr><td colspan="4">No accessible workspaces.</td></tr>';
        genericView.querySelectorAll('[data-operations-workspace]').forEach(b=>b.onclick=()=>{genericView.querySelector('#operationsWorkspaceFilter').value=b.dataset.operationsWorkspace;renderOperationalJobs();});
    }
    genericView.querySelector('#operationsWorkspaceSearch').value=operationsContext.search;
    genericView.querySelector('#operationsWorkspaceFilter').value=operationsContext.workspace;
    genericView.querySelector('#operationsAttentionFilter').value=operationsContext.attention;
    genericView.querySelector('[data-operations-stage]').value=operationsContext.stage;
    genericView.querySelector('[data-operations-assignee]').value=operationsContext.assignee;
    genericView.querySelector('[data-operations-stage]').onchange=renderOperationalJobs;
    genericView.querySelector('[data-operations-assignee]').onchange=renderOperationalJobs;
    genericView.querySelector('[data-operations-production]').onclick=()=>StudioWorkload.production(['overdue','waiting','unassigned','handoffs'].includes(operationsContext.attention)?operationsContext.attention:'',operationsContext.workspace,operationsContext.assignee,'',[...genericView.querySelectorAll('#operationsJobRows [data-open-job]')].map(b=>b.dataset.openJob));
    genericView.querySelector('[data-operations-schedule]').onclick=()=>studioNavigate('schedule',{photographer:operationsContext.assignee==='__unassigned'?'':operationsContext.assignee});
    genericView.querySelector('#operationsWorkspaceSearch').oninput=()=>{operationsContext.search=genericView.querySelector('#operationsWorkspaceSearch').value;renderOperationalWorkspaces();};
    genericView.querySelector('#operationsWorkspaceFilter').onchange=renderOperationalJobs;
    genericView.querySelector('#operationsAttentionFilter').onchange=renderOperationalJobs;
    genericView.querySelectorAll('[data-operations-panel]').forEach(b=>b.onclick=()=>{if(b.dataset.operationsPanel==='overrides')genericView.querySelectorAll('[data-override-row]').forEach(row=>row.hidden=false);genericView.querySelectorAll('[data-operations-section]').forEach(section=>section.hidden=section.dataset.operationsSection!==b.dataset.operationsPanel);genericView.querySelectorAll('[data-operations-panel]').forEach(button=>button.setAttribute('aria-pressed',String(button===b)));});
    renderOperationalWorkspaces();renderOperationalJobs();
    genericView.querySelector('[data-operations-refresh]').onclick=showOperationsManagement;
    genericView.querySelectorAll('[data-open-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.openJob).catch(e=>alert(e.message)));
    genericView.querySelectorAll('[data-override]').forEach(b=>b.onclick=async()=>{
        const reason=prompt('Operations decision reason:');if(!reason?.trim())return;b.disabled=true;
        try{const v=await json('/api/jobs/'+b.dataset.job+'/connected-workflow');await json('/api/jobs/'+b.dataset.job+'/connected-workflow',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'decide-deposit-override',expectedRevision:v.data.revision,operationId:crypto.randomUUID(),assignmentRequestId:b.dataset.override,method:b.dataset.decision,reason})});if(current())await showOperationsManagement();}
        catch(e){alert(e.message);if(b.isConnected)b.disabled=false;}
    });
}

async function showManagement(){
    const current=StudioPage.begin('management');
    conversationView.classList.add('hidden');genericView.classList.remove('hidden');
    const data=await studioReadOperations();const {jobs,tasks,sessions,photographers,now}=data;
    const snapshots=await json('/api/production/media-summary');
    if(!current())return;
    const photos=snapshots.reduce((n,s)=>n+Number(s.assetCount),0),overdue=tasks.filter(t=>!['Completed','Cancelled'].includes(t.status)&&t.dueAtUtc&&new Date(t.dueAtUtc)<now),blocked=tasks.filter(t=>t.status==='Blocked');
    const today=sessions.filter(s=>new Date(s.startsAtUtc).toLocaleDateString()===now.toLocaleDateString()&&s.status!=='Cancelled');
    if(!current())return;
    const net=data.receipts.reduce((n,e)=>n+Number(e.amount),0);
    const pipeline=pipelineStages.map(([label,statuses])=>({label,value:jobs.filter(j=>statuses.includes(j.status)).length})).filter(x=>x.value);
    const clientTotals=[...new Set(jobs.map(j=>j.clientId||j.clientName))].map(id=>{const related=jobs.filter(j=>(j.clientId||j.clientName)===id);return {job:related[0],amount:data.receipts.filter(e=>related.some(j=>j.id===e.jobId)).reduce((n,e)=>n+Number(e.amount),0)};}).sort((a,b)=>b.amount-a.amount);
    const upcoming=sessions.filter(s=>new Date(s.startsAtUtc)>=now&&s.status!=='Cancelled').sort((a,b)=>new Date(a.startsAtUtc)-new Date(b.startsAtUtc));
    genericView.innerHTML=StudioPage.html({title:'Management Dashboard',tools:`${studioPeriod()}<button data-export>Export</button>`,summary:StudioPage.metrics([['Jobs',jobs.length],['Sessions today',today.length],['Net receipts',money(net)]]),attention:StudioPage.metrics([['Overdue tasks',overdue.length],['Blocked work',blocked.length]]),content:`<div class="op-metrics management-metrics">${studioMetric(money(net),'Net receipts','green','','receipts')}${studioMetric(sessions.filter(s=>new Date(s.startsAtUtc)<=now&&s.status!=='Cancelled').length,'Sessions','blue')}${studioMetric(clientTotals.length,'Unique clients','purple')}${studioMetric(photos,'Photos catalogued','orange')}${studioMetric(overdue.length,'Overdue tasks','red','','overdue')}${studioMetric(blocked.length,'Blocked work','teal','','blocked')}</div>
    <div class="st-dashboard-grid">${studioCard('Revenue Overview',studioRevenueChart(data.receipts))}${studioCard('Jobs by Stage',studioComposition(pipeline,'jobs'))}${studioCard('Top Clients by Receipts',clientTotals.slice(0,6).map((x,i)=>`<button class="op-link-row" data-open-job="${x.job.id}"><b>${i+1}</b><span>${esc(x.job.clientName)}</span><b>${money(x.amount)}</b></button>`).join(''))}${studioCard('Upcoming Sessions',upcoming.slice(0,8).map(s=>`<button class="op-link-row" data-open-job="${s.jobId}"><span><strong>${esc(s.title)}</strong><small>${esc(new Date(s.startsAtUtc).toLocaleString())} · ${esc(s.location||'')}</small></span><small>${esc(s.status)}</small></button>`).join(''))}
    ${studioCard('Studio Activity',`${studioMetric(today.length,'Sessions today','blue')}${studioMetric(tasks.filter(t=>t.status==='Completed').length,'Completed tasks','green')}${studioMetric(jobs.filter(j=>j.status==='Completed').length,'Completed jobs','purple')}`)}${studioCard('Team Workload',photographers.map(p=>{const assigned=tasks.filter(t=>t.assignedTo===p.userName&&!['Completed','Cancelled'].includes(t.status));const shoots=sessions.filter(s=>(s.resourceIds||[]).includes(p.scheduleResourceId));return `<div class="op-link-row"><span><strong>${esc(p.displayName)}</strong><small>${assigned.length} open tasks · ${shoots.length} sessions</small></span><b>${assigned.length}</b></div>`;}).join(''))}${studioCard('Pipeline Health',pipeline.map(x=>`<button class="op-link-row" data-stage="${esc(x.label)}"><span>${esc(x.label)}</span><b>${x.value}</b></button>`).join(''))}${studioCard('Alerts & Actions',[...overdue,...blocked].filter((t,i,a)=>a.findIndex(x=>x.id===t.id)===i).slice(0,12).map(t=>`<button class="op-alert" data-open-job="${t.jobId}">${esc(t.title)}<small>${esc(t.blockedReason||'Overdue '+new Date(t.dueAtUtc).toLocaleDateString())}</small></button>`).join('')||'<p>No overdue or blocked work.</p>')}</div>`});
    StudioPage.bind(genericView,current);
    studioBindOperations(data,showManagement);
    genericView.querySelectorAll('[data-stage]').forEach(b=>b.onclick=()=>{pipelineSelectedStage=b.dataset.stage;studioNavigate('pipeline');});
}

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
            <button class="management-gantt-job" type="button" onclick="openWorkspaceJob('${job.id}')"><strong>${esc(job.title)}</strong><small>${esc(job.clientName||'Client not linked')} · ${esc(job.status)}</small></button>
            <div class="management-gantt-track" style="--gantt-width:${width}px">
                <div class="management-gantt-today" style="left:${todayPct}%"></div>
                <button class="management-gantt-bar" type="button" onclick="openWorkspaceJob('${job.id}')" style="left:${left}%;width:${span}%">
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
        return `<button class="management-flow-stage" type="button" onclick="studioNavigate('jobs')"><span>${index+1}</span><strong>${esc(stage)}</strong><b>${stageJobs.length} jobs</b><small>${stuck} stuck · ${overdue.length} overdue tasks · ${unassigned} unassigned · ${missingDate} missing dates</small></button>`;
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
    const dialog=studioDialog(displayName+' — scheduled work',`<button type="button" class="primary" data-management-photographer-schedule>View Schedule</button><div class="management-person-work">${work.map(entry=>{const job=context.jobs.find(j=>j.id===entry.jobId);return `<article class="front-desk-row"><div><strong>${esc(entry.title)}</strong><small>${esc(new Date(entry.startsAtUtc).toLocaleString())} – ${esc(new Date(entry.endsAtUtc).toLocaleString())} · ${esc(entry.location||'Location not set')}</small></div>${job?`<button type="button" class="action" data-management-job="${job.id}">Open job</button>`:''}</article>`;}).join('')||'<p class="muted">No upcoming scheduled work.</p>'}</div>`);
    dialog.querySelector('[data-management-photographer-schedule]')?.addEventListener('click',()=>{dialog.close();window.studioNavigate('schedule',{resourceId});});
    dialog.querySelectorAll('[data-management-job]').forEach(button=>button.onclick=()=>{dialog.close();openWorkspaceJob(button.dataset.managementJob);});
}



