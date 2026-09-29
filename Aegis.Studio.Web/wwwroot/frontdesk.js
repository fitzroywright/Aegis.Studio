let frontDeskJobs = [];
let frontDeskRole = null;

const frontDeskBaseActivateView = activateView;
activateView = function(view) {
    if (view === 'frontdesk') {
        showFrontDesk();
        return;
    }

    frontDeskBaseActivateView(view);
};

async function loadFrontDeskRole() {
    try {
        frontDeskRole = await json('/api/security/global-role-me');
        const button = document.querySelector('.brand-button[data-view="frontdesk"]');
        if (button) {
            const me = await json('/api/auth/me');
            button.classList.toggle('hidden', !frontDeskRole.frontDesk && !(me.user || me).isAdministrator);
        }
    } catch {
        frontDeskRole = { frontDesk: false, permissions: {} };
    }
}

async function showFrontDesk() {
    if (!frontDeskRole) await loadFrontDeskRole();
    const me = await json('/api/auth/me');
    if (!frontDeskRole?.frontDesk && !(me.user || me).isAdministrator) {
        activateNavigation('frontdesk');
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML = '<h1>Front Desk</h1><p class="muted">You are not assigned to the Front Desk operational role.</p>';
        return;
    }

    activateNavigation('frontdesk');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');

    genericView.innerHTML='<p>Loading FrontDesk…</p>';
    try {
        const results=await Promise.all([json('/api/jobs'),json('/api/frontdesk/booking-requests'),json('/api/frontdesk/enquiries'),json('/api/frontdesk/workspaces')]);
        [frontDeskJobs,frontDeskRequests,frontDeskEnquiries,frontDeskWorkspaces]=results;
        renderFrontDesk();
    } catch(error){genericView.innerHTML=`<h1>FrontDesk</h1><p role="alert">${esc(error.message)}</p><button class="action" onclick="showFrontDesk()">Retry</button>`;}
}

let frontDeskRequests=[],frontDeskEnquiries=[],frontDeskWorkspaces=[];
let frontDeskTab='requests',frontDeskTerm='';
function frontDeskRequestStage(item){return item.jobId?'Confirmed':item.recordedDeposit>0?'Partial deposit':item.status==='AwaitingDeposit'?'Awaiting deposit':'Pending request';}
function renderFrontDesk() {
    const pending=frontDeskRequests.filter(r=>!r.jobId),ready=frontDeskJobs.filter(j=>j.status==='Ready for Client');
    genericView.innerHTML=`<div class="job-detail-header"><div><h1>FrontDesk</h1><p>Customer requests, deposits and delivery handoffs.</p></div><div class="job-actions"><button class="action" onclick="showAccounts()">Accounts / Invoices</button><button class="action" onclick="showPos()">Sales / Payments</button><button class="primary" id="newBookingRequest">New booking request</button></div></div>
    <div class="cards"><div class="metric">Pending requests<strong>${pending.length}</strong></div><div class="metric">Awaiting deposit<strong>${pending.filter(r=>r.status==='AwaitingDeposit').length}</strong></div><div class="metric">Needs scheduling<strong>${frontDeskJobs.filter(j=>['New','Needs Scheduling'].includes(j.status)).length}</strong></div><div class="metric">Ready for delivery<strong>${ready.length}</strong></div></div>
    <nav class="workspace-tabs" aria-label="FrontDesk queues">${[['requests','Booking requests'],['enquiries','Enquiries'],['jobs','Active jobs'],['delivery','Delivery / balance'],['completed','Completed']].map(([key,label])=>`<button data-frontdesk-tab="${key}" class="${key===frontDeskTab?'active':''}">${label}</button>`).join('')}</nav>
    <div class="front-desk-tools"><input id="frontDeskSearch" type="search" placeholder="Search customer, request or job" value="${esc(frontDeskTerm)}"><button class="action" onclick="showFrontDesk()">Refresh</button></div><div id="frontDeskJobList"></div>`;
    genericView.querySelector('#newBookingRequest').onclick=()=>openPendingBookingForm();
    genericView.querySelectorAll('[data-frontdesk-tab]').forEach(b=>b.onclick=()=>{frontDeskTab=b.dataset.frontdeskTab;renderFrontDesk();});
    genericView.querySelector('#frontDeskSearch').oninput=e=>{frontDeskTerm=e.target.value;renderFrontDeskRows();};
    renderFrontDeskRows();
}
function renderFrontDeskRows(){
    const host=document.getElementById('frontDeskJobList'),term=frontDeskTerm.trim().toLowerCase();
    const matches=value=>JSON.stringify(value).toLowerCase().includes(term);
    if(frontDeskTab==='requests')host.innerHTML=frontDeskRequests.filter(matches).map(x=>{
        const r=x.request,remaining=Math.max(0,r.requiredDeposit-x.recordedDeposit);
        return `<article class="front-desk-row"><div><strong>${esc(r.clientName)}</strong><div>${esc(r.title)} · ${esc(r.eventDate)}</div><div class="meta">${esc(frontDeskWorkspaces.find(w=>w.id===r.workspaceId)?.name||'Workspace')} · ${frontDeskRequestStage(x)}</div><div class="meta">Required deposit: ${money(r.requiredDeposit)} · Recorded: ${money(x.recordedDeposit)} · Remaining deposit: ${money(remaining)}</div></div><div class="front-desk-actions">${x.jobId?`<button class="primary" data-frontdesk-job="${x.jobId}">Open job</button><button class="action" data-frontdesk-pos="${x.jobId}">Balance / Billing</button>`:`${x.status==='Pending'?`<button class="action" data-deposit-requested="${x.id}">Mark deposit requested</button>`:''}<button class="primary" data-deposit="${x.id}">Record verified deposit</button>`}</div></article>`;
    }).join('')||'<p class="empty-state">No booking requests. Create a request or convert an enquiry; no job is created until the required deposit is recorded.</p>';
    else if(frontDeskTab==='enquiries'){
        const linked=new Set(frontDeskRequests.map(r=>r.request.conversationId).filter(Boolean));
        host.innerHTML=frontDeskEnquiries.filter(e=>!linked.has(e.id)&&matches(e)).map(e=>`<article class="front-desk-row"><div><strong>${esc(e.contactName)}</strong><div>${esc(e.subject)}</div><small>${esc(e.channel)} · ${esc(new Date(e.createdAtUtc).toLocaleString())}</small></div><button class="primary" data-convert-enquiry="${e.id}">Prepare booking request</button></article>`).join('')||'<p class="empty-state">No unassigned enquiries awaiting booking preparation.</p>';
    }else{
        let jobs=frontDeskJobs.filter(matches);if(frontDeskTab==='delivery')jobs=jobs.filter(j=>j.status==='Ready for Client');else if(frontDeskTab==='completed')jobs=jobs.filter(j=>j.status==='Completed');else jobs=jobs.filter(j=>j.status!=='Completed');
        host.innerHTML=jobs.map(renderFrontDeskJob).join('')||'<p class="empty-state">No jobs in this queue.</p>';
    }
    host.querySelectorAll('[data-frontdesk-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.frontdeskJob));
    host.querySelectorAll('[data-frontdesk-pos]').forEach(b=>b.onclick=()=>openPos(b.dataset.frontdeskPos));
    host.querySelectorAll('[data-convert-enquiry]').forEach(b=>b.onclick=()=>openPendingBookingForm(frontDeskEnquiries.find(e=>e.id===b.dataset.convertEnquiry)));
    host.querySelectorAll('[data-deposit]').forEach(b=>b.onclick=()=>openVerifiedDepositForm(frontDeskRequests.find(r=>r.id===b.dataset.deposit)));
    host.querySelectorAll('[data-deposit-requested]').forEach(b=>b.onclick=async()=>{if(!confirm('Confirm that you have already sent the deposit request to the customer. This records the follow-up status; it does not send a message.'))return;b.disabled=true;try{await json(`/api/frontdesk/booking-requests/${b.dataset.depositRequested}/deposit-requested`,{method:'POST'});await showFrontDesk();}catch(e){alert(e.message);b.disabled=false;}});
}
function renderFrontDeskJob(job){return `<article class="front-desk-row"><div><strong>${esc(job.clientName)}</strong><div>${esc(job.title)}</div><div class="meta">${esc(job.status)}${job.eventDate?' · '+esc(job.eventDate):''}</div></div><div class="front-desk-actions"><button class="action" onclick="openFrontDeskClient('${job.id}')">Customer conversation</button><button class="action" onclick="notifyFrontDeskTeam('${job.id}')">Notify team</button><button class="action" data-frontdesk-job="${job.id}">Open job</button><button class="primary" data-frontdesk-pos="${job.id}">Balance / Billing</button></div></article>`;}
function frontDeskDialog(title,contents){
    const dialog=document.createElement('dialog');dialog.className='frontdesk-dialog';dialog.innerHTML=`<form class="request-form"><h2>${esc(title)}</h2>${contents}<p role="alert"></p><div class="dialog-actions"><button type="button" data-cancel>Cancel</button><button class="primary" type="submit">Save</button></div></form>`;document.body.appendChild(dialog);dialog.querySelector('[data-cancel]').onclick=()=>dialog.close();dialog.addEventListener('close',()=>dialog.remove(),{once:true});dialog.showModal();return dialog;
}
function openPendingBookingForm(enquiry){
    const dialog=frontDeskDialog('Pending booking request',`<p>The job will be created after the required deposit is recorded.</p><label>Workspace<select name="workspace" required>${frontDeskWorkspaces.map(w=>`<option value="${w.id}" ${w.id===enquiry?.workspaceId?'selected':''}>${esc(w.name)}</option>`).join('')}</select></label><label>Customer<input name="customer" required maxlength="200" value="${esc(enquiry?.contactName||'')}"></label><label>Email<input name="email" type="email" required maxlength="320" value="${esc(enquiry?.channel==='Email'?enquiry.contactAddress:'')}"></label><label>Wedding / session title<input name="title" required maxlength="200" value="${esc(enquiry?.subject||'')}"></label><label>Event date<input name="date" type="date" required></label><label>Required deposit (JMD)<input name="deposit" type="number" min="0.01" max="999999999" step="0.01" required></label><label>Notes<textarea name="notes" maxlength="4000"></textarea></label>`);
    const form=dialog.querySelector('form');if(enquiry)form.elements.workspace.disabled=true;
    form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('[type=submit]');button.disabled=true;try{await json('/api/frontdesk/booking-requests',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({workspaceId:form.elements.workspace.value,clientName:form.elements.customer.value,clientEmail:form.elements.email.value,title:form.elements.title.value,eventDate:form.elements.date.value,requiredDeposit:Number(form.elements.deposit.value),currency:'JMD',notes:form.elements.notes.value,conversationId:enquiry?.id||null})});dialog.close();frontDeskTab='requests';await showFrontDesk();}catch(error){form.querySelector('[role=alert]').textContent=error.message;}finally{button.disabled=false;}};
}
function openVerifiedDepositForm(request){
    const remaining=Math.max(0,request.request.requiredDeposit-request.recordedDeposit);
    const dialog=frontDeskDialog('Record verified deposit',`<p>${esc(request.request.clientName)} · ${esc(request.request.title)}</p><p>Record money already received and verified. This does not charge the customer. Meeting the required deposit confirms this booking and creates one job for scheduling.</p><label>Amount received (JMD)<input name="amount" type="number" step="0.01" min="0.01" max="999999999" value="${remaining}" required></label><label>Payment provider / method<input name="provider" maxlength="100" required placeholder="Bank transfer, cash receipt…"></label><label>Unique receipt / transaction reference<input name="reference" maxlength="200" required></label>`);
    const form=dialog.querySelector('form');form.querySelector('[type=submit]').textContent='Record deposit';form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('[type=submit]');button.disabled=true;try{await json(`/api/frontdesk/booking-requests/${request.id}/deposits`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({amount:Number(form.elements.amount.value),currency:'JMD',provider:form.elements.provider.value,reference:form.elements.reference.value})});dialog.close();await showFrontDesk();}catch(error){form.querySelector('[role=alert]').textContent=error.message;}finally{button.disabled=false;}};
}

async function openFrontDeskClient(jobId) {
    const job = await json('/api/jobs/' + jobId);
    if (!job.conversationId) {
        alert('This job has no linked customer conversation.');
        return;
    }

    await openWorkspaceJob(jobId);
}

async function notifyFrontDeskTeam(jobId) {
    let members;
    try {
        members = await json(`/api/jobs/${jobId}/notifiable-members`);
    } catch (error) {
        alert('You do not have permission to notify members for this job.');
        return;
    }

    if (!members.length) {
        alert('This job and its workspace have no active members to notify.');
        return;
    }

    const choices = members.map((member, index) => `${index + 1}. ${member.displayName || member.userName} (${member.userName}) · ${member.source}`).join('\n');
    const selected = Number(prompt(`Notify which member?\n\n${choices}`, '1'));
    if (!Number.isInteger(selected) || selected < 1 || selected > members.length) return;

    const member = members[selected - 1];
    const subject = prompt('Subject', 'Front Desk message');
    if (!subject) return;
    const body = prompt('Message');
    if (!body) return;

    const result = await json(`/api/jobs/${jobId}/notifications`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userName: member.userName, subject, body })
    });
    alert(result.message || `Notification request accepted for ${member.displayName || member.userName}. Delivery is not yet confirmed.`);
}

loadFrontDeskRole();
