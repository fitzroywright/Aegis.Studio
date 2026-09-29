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
    genericView.innerHTML=`<div class="job-detail-header"><div><h1>FrontDesk</h1><p>Customer requests, deposits and delivery handoffs.</p></div><div class="job-actions"><button class="action" onclick="showFrontDeskClients()">Clients</button><button class="action" onclick="showAccounts()">Accounts / Invoices</button><button class="action" onclick="showPos()">Sales / Payments</button><button class="primary" id="newBookingRequest">New booking request</button></div></div>
    <div class="cards"><div class="metric">Pending requests<strong>${pending.length}</strong></div><div class="metric">Awaiting deposit<strong>${pending.filter(r=>r.status==='AwaitingDeposit').length}</strong></div><div class="metric">Needs scheduling<strong>${frontDeskJobs.filter(j=>['New','Needs Scheduling'].includes(j.status)).length}</strong></div><div class="metric">Ready for delivery<strong>${ready.length}</strong></div></div>
    <nav class="workspace-tabs" aria-label="FrontDesk queues">${[['requests','Booking requests'],['enquiries','Enquiries'],['jobs','Active jobs'],['delivery','Delivery / balance'],['completed','Completed']].map(([key,label])=>`<button data-frontdesk-tab="${key}" class="${key===frontDeskTab?'active':''}">${label}</button>`).join('')}</nav>
    <div class="front-desk-tools"><input id="frontDeskSearch" type="search" placeholder="Search customer, request or job" value="${esc(frontDeskTerm)}"><button class="action" onclick="showFrontDesk()">Refresh</button></div><div id="frontDeskJobList"></div>`;
    const newRequestAction=document.getElementById('newRequestButton');
    const frontDeskActions=genericView.querySelector('.job-actions');
    if(newRequestAction&&frontDeskActions){
        newRequestAction.hidden=false;
        frontDeskActions.insertBefore(newRequestAction,genericView.querySelector('#newBookingRequest'));
    }
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
function renderFrontDeskJob(job){return `<article class="front-desk-row"><div><strong>${esc(job.clientName)}</strong><div>${esc(job.title)}</div><div class="meta">${esc(job.status)}${job.eventDate?' · '+esc(job.eventDate):''}</div></div><div class="front-desk-actions"><button class="action" onclick="linkFrontDeskClient('${job.id}')">${job.clientId?'Linked client':'Link client'}</button><button class="action" onclick="openFrontDeskClient('${job.id}')">Customer conversation</button><button class="action" onclick="showFrontDeskFlow('${job.id}')">Flow</button><button class="action" onclick="messageFrontDeskJob('${job.id}','workspace')">Message Workspace</button><button class="action" onclick="messageFrontDeskJob('${job.id}','escalate')">Escalate</button><button class="action" onclick="messageFrontDeskJob('${job.id}','finance')">Message Accounts / Finance</button><button class="action" data-frontdesk-job="${job.id}">Open job</button><button class="primary" data-frontdesk-pos="${job.id}">Balance / Billing</button></div></article>`;}
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

    const choices = members.map((member, index) => `${index + 1}. ${studioFirstName(member)} · ${member.source}`).join('\n');
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
    alert(result.message || `Notification request accepted for ${studioFirstName(member)}. Delivery is not yet confirmed.`);
}

loadFrontDeskRole();

async function showFrontDeskClients(){
 const clients=await json('/api/frontdesk/clients');
 const dialog=studioDialog('Clients','<div class="front-desk-tools"><input type="search" aria-label="Find client" placeholder="Find client"><button class="action" data-new>New client</button></div><div data-clients></div>');
 const render=()=>{const term=dialog.querySelector('input').value.toLowerCase();dialog.querySelector('[data-clients]').innerHTML=clients.filter(c=>[c.displayName,c.email,c.phone].join(' ').toLowerCase().includes(term)).map(c=>`<article class="task-card"><strong>${esc(c.displayName)}</strong><small>${esc(c.email||'')} · ${esc(c.phone||'')}</small><button class="action" data-client="${c.id}">Details / history</button></article>`).join('')||'<p>No clients found.</p>';dialog.querySelectorAll('[data-client]').forEach(b=>b.onclick=()=>editClient(clients.find(c=>c.id===b.dataset.client)));};
 async function editClient(client){
  const jobs=frontDeskJobs.filter(j=>j.clientId===client?.id);
  const editor=studioDialog(client?'Client details':'New client',`<form class="subject-form"><label>Name<input name="displayName" required maxlength="200" value="${esc(client?.displayName||'')}"></label><label>Email<input name="email" type="email" value="${esc(client?.email||'')}"></label><label>Phone<input name="phone" maxlength="80" value="${esc(client?.phone||'')}"></label><button class="primary">Save client</button><p role="alert"></p></form>${client?`<button class="action" onclick="showClientQuotes('${client.id}')">Quotes / estimates</button><button class="action" onclick="invoiceClientJobs('${client.id}')">Invoice client jobs</button><h3>Job history across workspaces</h3>`+jobs.map(j=>`<p>${esc(j.title)} · ${esc(j.status)}</p>`).join('')+'<p>Link jobs using the Client action in Active jobs.</p>':''}`);
  editor.querySelector('form').onsubmit=async e=>{e.preventDefault();const f=e.currentTarget;try{await json('/api/frontdesk/clients'+(client?'/'+client.id:''),{method:client?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({displayName:f.elements.displayName.value,email:f.elements.email.value,phone:f.elements.phone.value})});editor.close();dialog.close();await showFrontDeskClients();}catch(error){f.querySelector('[role=alert]').textContent=error.message;}};
 }
 dialog.querySelector('input').oninput=render;dialog.querySelector('[data-new]').onclick=()=>editClient(null);render();
}
async function linkFrontDeskClient(jobId){
 const clients=await json('/api/frontdesk/clients'),job=frontDeskJobs.find(j=>j.id===jobId);
 const dialog=frontDeskDialog('Link client',`<label>Client<select name="client"><option value="">Not linked</option>${clients.map(c=>`<option value="${c.id}" ${job.clientId===c.id?'selected':''}>${esc(c.displayName)}</option>`).join('')}</select></label><p>Clients can have jobs in any workspace. Existing customer correspondence is preserved.</p>`);
 dialog.querySelector('form').onsubmit=async e=>{e.preventDefault();try{await json(`/api/frontdesk/clients/job/${jobId}`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({clientId:e.currentTarget.elements.client.value||null})});dialog.close();await showFrontDesk();}catch(error){e.currentTarget.querySelector('[role=alert]').textContent=error.message;}};
}
async function invoiceClientJobs(clientId){
 const jobs=frontDeskJobs.filter(j=>j.clientId===clientId);
 const dialog=frontDeskDialog('Client invoice',`<p>Select jobs for this client. Enter the agreed charge for each selected job; this creates a draft invoice.</p>${jobs.map(j=>`<label><input type="checkbox" name="jobs" value="${j.id}">${esc(j.title)}<input type="number" data-charge="${j.id}" min="0" max="999999999" step="0.01" value="0" aria-label="Charge for ${esc(j.title)}"></label>`).join('')}<label>Tax %<input name="tax" type="number" min="0" max="100" step="0.01" value="0"></label><label>Due date<input name="due" type="date" required></label>`);
 dialog.querySelector('form').onsubmit=async e=>{e.preventDefault();const form=e.currentTarget;const ids=Array.from(form.querySelectorAll('[name=jobs]:checked'),x=>x.value);if(!ids.length){form.querySelector('[role=alert]').textContent='Select at least one job.';return;}try{const invoice=await json('/api/frontdesk/clients/invoices',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({jobIds:ids,invoice:{lines:ids.map(id=>({description:jobs.find(j=>j.id===id).title,quantity:1,unitPrice:Number(form.querySelector(`[data-charge="${id}"]`).value)})),taxRatePercent:Number(form.elements.tax.value),dueAtUtc:new Date(form.elements.due.value+'T12:00:00').toISOString(),currency:'JMD'}})});dialog.close();alert('Draft invoice '+invoice.invoiceNumber+' created.');}catch(error){form.querySelector('[role=alert]').textContent=error.message;}};
}
async function showClientQuotes(clientId){
 const quotes=await json('/api/frontdesk/quotes?clientId='+clientId);
 const dialog=studioDialog('Client quotes',`<button class="primary" data-create>New quote</button><div>${quotes.map(q=>`<article class="task-card"><strong>${esc(q.request.title)} · ${money(q.total)}</strong><small>${esc(q.status)}${q.deliveryStatus?' · Email: '+esc(q.deliveryStatus):''}</small>${q.deliveryError?`<p role="alert">${esc(q.deliveryError)}</p>`:''}<p>${esc(q.request.instructions||'')}</p>${q.status==='Draft'?`<button class="action" data-send="${q.id}">Send quote by email</button>`:''}${['Draft','Queued'].includes(q.status)?`<button class="action" data-accept="${q.id}">Record acceptance</button>`:''}${q.status==='Accepted'?`<button class="primary" data-convert="${q.id}">Create pending booking</button>`:''}</article>`).join('')||'<p>No quotes yet.</p>'}</div><p role="alert"></p>`);
 const refresh=async()=>{dialog.close();await showClientQuotes(clientId);};
 dialog.querySelectorAll('[data-send]').forEach(b=>b.onclick=async()=>{if(!confirm('Queue this quote for delivery to the client’s saved email address?'))return;try{await json(`/api/frontdesk/quotes/${b.dataset.send}/send`,{method:'POST'});await refresh();}catch(e){dialog.querySelector('[role=alert]').textContent=e.message;}});
 dialog.querySelectorAll('[data-accept]').forEach(b=>b.onclick=async()=>{const reference=prompt('Customer acceptance reference (email, phone call or signed document):');if(!reference)return;try{await json(`/api/frontdesk/quotes/${b.dataset.accept}/accept`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({reference})});await refresh();}catch(e){dialog.querySelector('[role=alert]').textContent=e.message;}});
 dialog.querySelectorAll('[data-convert]').forEach(b=>b.onclick=async()=>{try{await json(`/api/frontdesk/quotes/${b.dataset.convert}/convert`,{method:'POST'});dialog.close();await showFrontDesk();}catch(e){dialog.querySelector('[role=alert]').textContent=e.message;}});
 dialog.querySelector('[data-create]').onclick=()=>{const editor=frontDeskDialog('New client quote',`<label>Workspace<select name="workspace">${frontDeskWorkspaces.map(w=>`<option value="${w.id}">${esc(w.name)}</option>`).join('')}</select></label><label>Work / service<input name="title" required maxlength="200"></label><label>Event date<input name="date" type="date" required></label><label>Estimated cost before tax (JMD)<input name="amount" type="number" min="0.01" max="999999999" step="0.01" required></label><label>Tax %<input name="tax" type="number" min="0" max="100" step="0.01" value="0"></label><label>Required deposit (JMD)<input name="deposit" type="number" min="0.01" step="0.01" required></label><label>Instructions<textarea name="notes" maxlength="4000"></textarea></label>`);editor.querySelector('form').onsubmit=async e=>{e.preventDefault();const f=e.currentTarget;try{await json('/api/frontdesk/quotes',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({clientId,workspaceId:f.elements.workspace.value,title:f.elements.title.value,eventDate:f.elements.date.value,instructions:f.elements.notes.value,requiredDeposit:Number(f.elements.deposit.value),estimate:{lines:[{description:f.elements.title.value,quantity:1,unitPrice:Number(f.elements.amount.value)}],taxRatePercent:Number(f.elements.tax.value),dueAtUtc:new Date(f.elements.date.value+'T12:00:00').toISOString(),currency:'JMD'}})});editor.close();await refresh();}catch(error){f.querySelector('[role=alert]').textContent=error.message;}};};
}


async function showFrontDeskFlow(jobId) {
    const job=await json('/api/jobs/'+jobId);
    const timeline=await json('/api/jobs/'+jobId+'/timeline?count=200');
    const events=Array.isArray(timeline)?timeline:(timeline.items||timeline.events||[]);
    const stageNames=[];
    for(const event of events){
        const text=String(event.detail||event.description||event.title||'');
        const match=text.match(/(?:→|to )\s*([^→]+)$/i);
        if(match&&!stageNames.includes(match[1].trim()))stageNames.push(match[1].trim());
    }
    if(!stageNames.includes(job.status))stageNames.push(job.status);
    const current=Math.max(0,stageNames.indexOf(job.status));
    const dialog=studioDialog('Job Flow — '+job.title,`<div class="frontdesk-flow">${stageNames.map((stage,index)=>`<button type="button" class="frontdesk-flow-stage ${index<current?'complete':index===current?'current':'future'}" data-flow-stage="${index}"><small>${index<current?'Completed':index===current?'Current':'Upcoming'}</small><strong>${esc(stage)}</strong></button>`).join('<span>›</span>')}</div><div class="frontdesk-flow-history"><h3>History</h3>${events.slice(0,30).map(event=>`<p><strong>${esc(event.title||event.eventType||'Activity')}</strong><small>${esc(event.detail||event.description||'')} ${event.createdAtUtc||event.occurredAtUtc?' · '+new Date(event.createdAtUtc||event.occurredAtUtc).toLocaleString():''}</small></p>`).join('')||'<p class="muted">No recorded stage history yet.</p>'}</div>`);
    return dialog;
}

async function messageFrontDeskJob(jobId, mode) {
    const job=await json('/api/jobs/'+jobId);
    const label=mode==='finance'?'Accounts / Finance':mode==='escalate'?'Escalation':'Workspace';
    const dialog=frontDeskDialog(label+' message',`<p><strong>${esc(job.clientName||'Client')}</strong> · ${esc(job.title)} · ${esc(job.status)}</p>${mode==='escalate'?'<label>Escalate to<select name="target"><option value="Workspace Manager">Workspace Manager</option><option value="Operations Manager">Operations Manager</option></select></label>':''}<label>Message<textarea name="body" required maxlength="4000" placeholder="${mode==='finance'?'Question about quote, invoice, payment, balance or refund':'What is happening with this job?'}"></textarea></label><label>Attach document<input name="file" type="file"></label><p class="muted">The message stays in the job conversation. Any attached file also appears in Conversation Files.</p>`);
    const form=dialog.querySelector('form');
    form.onsubmit=async e=>{
        e.preventDefault();
        if(!job.conversationId){form.querySelector('[role=alert]').textContent='This job has no linked conversation.';return;}
        const prefix=mode==='finance'?'[Accounts / Finance] ':mode==='escalate'?`[Escalation — ${form.elements.target.value}] `:'[Workspace query] ';
        try{
            await json('/api/conversations/'+job.conversationId+'/messages',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({body:prefix+form.elements.body.value,isInternal:true,sendingIdentity:'Workspace'})});
            const file=form.elements.file.files[0];
            if(file){
                const data=new FormData();data.append('file',file);
                const response=await fetch('/api/conversations/'+job.conversationId+'/attachments?visibility=Internal',{method:'POST',body:data});
                if(!response.ok)throw new Error(await response.text());
            }
            dialog.close();
            alert(label+' message added to the job conversation.');
        }catch(error){form.querySelector('[role=alert]').textContent=error.message;}
    };
}
