let frontDeskJobs = [];
let frontDeskRole = null;
let frontDeskColumnFilters = {type:'',status:'',source:''};
let frontDeskSort = {column:'',direction:1};


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
        // Navigation state is owned by studioNavigate() in studio-navigation.js.
        conversationView.classList.add('hidden');
        genericView.classList.remove('hidden');
        genericView.innerHTML = '<h1>Front Desk</h1><p class="muted">You are not assigned to the Front Desk operational role.</p>';
        return;
    }

    // Navigation state is owned by studioNavigate() in studio-navigation.js.
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');

    genericView.innerHTML='<p>Loading Front Desk…</p>';
    const dayStart=new Date();dayStart.setHours(0,0,0,0);
    const sources=[
        ['/api/jobs','jobs'],
        ['/api/frontdesk/booking-requests','bookings'],
        ['/api/frontdesk/enquiries','inquiries'],
        ['/api/frontdesk/workspaces','workspaces'],
        ['/api/frontdesk/clients','clients'],
        ['/api/schedule/entries?from='+encodeURIComponent(dayStart.toISOString())+'&to='+encodeURIComponent(new Date(dayStart.getTime()+90*86400000).toISOString()),'schedule'],
        ['/api/accounts/invoices','invoices']
    ];
    const results=await Promise.allSettled(sources.map(([url])=>json(url)));
    const errors=[];
    const arrays=results.map((result,index)=>{
        if(result.status==='rejected'){
            errors.push(sources[index][1]+': '+(result.reason?.message||'Request failed'));
            return [];
        }
        if(!Array.isArray(result.value)){
            errors.push(sources[index][1]+': Unexpected API response');
            return [];
        }
        return result.value;
    });
    [frontDeskJobs,frontDeskRequests,frontDeskEnquiries,frontDeskWorkspaces,frontDeskClients,frontDeskSchedule,frontDeskInvoices]=arrays;
    try{
        renderFrontDesk();
        if(errors.length){
            const notice=document.createElement('div');
            notice.className='fd-load-warning';
            notice.setAttribute('role','alert');
            notice.innerHTML='<strong>Some Front Desk information could not be loaded.</strong> '+errors.map(esc).join(' · ')+' <button type="button" class="action">Retry</button>';
            notice.querySelector('button').onclick=()=>showFrontDesk();
            genericView.querySelector('.fd-page')?.prepend(notice);
            console.warn('[Studio Front Desk] Partial API failures:',errors);
        }
    }catch(error){
        console.error('[Studio Front Desk] Render failure:',error);
        genericView.innerHTML=`<div class="fd-page"><h1>Front Desk</h1><p role="alert">Unable to render Front Desk: ${esc(error.message)}</p><button class="action" onclick="showFrontDesk()">Retry</button></div>`;
    }
}

let frontDeskRequests=[],frontDeskEnquiries=[],frontDeskWorkspaces=[],frontDeskClients=[],frontDeskSchedule=[],frontDeskInvoices=[];
let frontDeskTab='today',frontDeskTerm='',frontDeskClientTerm='',frontDeskSelectedClientId=null;
function frontDeskRequestStage(item){return item.status==='Confirmed'?'Confirmed':item.status==='Cancelled'?'Cancelled':item.recordedDeposit>0?'Ready for confirmation':item.status==='AwaitingDeposit'?'Awaiting deposit':'Pending Job';}
function renderFrontDesk(){
    // Follow the Today page's full-width horizontal shell. No navigation sidebar
    // or additional nested side rail; the secondary cards belong to the content grid.
    const now=new Date(), today=now.toLocaleDateString();
    const sessions=frontDeskSchedule.filter(s=>new Date(s.startsAtUtc).toLocaleDateString()===today&&s.status!=='Cancelled').sort((a,b)=>new Date(a.startsAtUtc)-new Date(b.startsAtUtc));
    const pending=frontDeskRequests.filter(x=>x&&!['Confirmed','Cancelled'].includes(x.status));
    const unassigned=frontDeskJobs.filter(j=>!j.assignedTo&&j.status!=='Completed');
    const enquiries=frontDeskEnquiries.filter(e=>e&&!frontDeskRequests.some(r=>r?.request?.conversationId===e.id));
    const due=frontDeskRequests.filter(x=>x&&(x.status==='AwaitingDeposit'||(x.request?.requiredDeposit>0&&x.recordedDeposit<x.request.requiredDeposit&&!['Confirmed','Cancelled'].includes(x.status))));
    const tabs=[['today','All'],['enquiries','New']];
    const entries=[
        ...enquiries.map(e=>({kind:'enquiry',id:e.id,created:e.createdAtUtc,name:e.contactName||'New inquiry',title:e.subject||'Inquiry',date:null,status:'New',source:e.channel||'Inquiry',conversationId:e.id})),
        ...frontDeskRequests.filter(Boolean).map(x=>({kind:'request',id:x.id,jobId:x.jobId,created:x.createdAtUtc||x.request?.createdAtUtc,name:x.request?.clientName||'Customer',title:x.request?.title||'Booking',date:x.request?.eventDate,status:frontDeskRequestStage(x),source:'Booking',request:x}))
    ].sort((a,b)=>new Date(b.created||0)-new Date(a.created||0));
    const entryType=e=>e.kind==='enquiry'?'Inquiry':'Booking';
    const valuesFor=key=>[...new Set(entries.map(e=>key==='type'?entryType(e):String(e[key]||'')))].filter(Boolean).sort((a,b)=>a.localeCompare(b));
    const filtered=entries.filter(e=>
        (frontDeskTab==='today'||(frontDeskTab==='enquiries'&&e.kind==='enquiry')||(frontDeskTab==='requests'&&e.kind==='request'))
        &&[e.name,e.title,e.status,e.source].join(' ').toLowerCase().includes(frontDeskTerm.toLowerCase())
        &&(!frontDeskColumnFilters.type||entryType(e)===frontDeskColumnFilters.type)
        &&(!frontDeskColumnFilters.status||e.status===frontDeskColumnFilters.status)
        &&(!frontDeskColumnFilters.source||e.source===frontDeskColumnFilters.source)
    );
    if(frontDeskSort.column){
        const key=frontDeskSort.column;
        filtered.sort((a,b)=>frontDeskSort.direction*String(key==='type'?entryType(a):a[key]||'').localeCompare(String(key==='type'?entryType(b):b[key]||''),undefined,{numeric:true}));
    }
    const pill=(value,category)=>{
        const normalized=String(value||'').toLowerCase();
        const tone=category==='status'
            ?(/confirm|complete|approved|paid/.test(normalized)?'success':/pending|await|deposit/.test(normalized)?'warning':/cancel|reject|overdue/.test(normalized)?'danger':'info')
            :category==='type'?(normalized==='booking'?'blue':'violet')
            :(/web|online|website/.test(normalized)?'blue':/phone|call/.test(normalized)?'violet':/email/.test(normalized)?'teal':'neutral');
        return '<span class="fd-data-pill fd-data-pill--'+tone+'">'+esc(value||'—')+'</span>';
    };
    const columnHead=(key,label)=>{
        const options=valuesFor(key).map(value=>'<option value="'+esc(value)+'"'+(frontDeskColumnFilters[key]===value?' selected':'')+'>'+esc(value)+'</option>').join('');
        const arrow=frontDeskSort.column===key?(frontDeskSort.direction===1?' ↑':' ↓'):' ↕';
        return '<th class="fd-column-head"><div class="fd-column-controls"><button type="button" data-fd-sort="'+key+'" aria-label="Sort by '+label+'">'+label+arrow+'</button><select data-fd-filter="'+key+'" aria-label="Filter '+label+'"><option value="">All</option>'+options+'</select></div></th>';
    };
    const rows=filtered.map(e=>`<tr class="fd-speedbump-row" tabindex="0" role="link" aria-label="Open ${esc(e.name)}" data-fd-row-open="${esc(e.id)}"><td><small>${esc(e.created?new Date(e.created).toLocaleDateString():'—')}</small></td><td><strong>${esc(e.name)}</strong><small>${esc(e.title)}</small></td><td>${pill(entryType(e),'type')}</td><td>${esc(e.date||'—')}</td><td>${pill(e.status,'status')}</td><td>${pill(e.source,'source')}</td><td><button type="button" class="action" data-fd-open="${esc(e.id)}">Open</button></td></tr>`).join('');
    const metric=(label,count,note)=>`<div class="fd-metric"><span>${esc(label)}</span><strong>${count}</strong><small>${esc(note)}</small></div>`;
    genericView.innerHTML=`<div class="fd-page">
      
      <div class="fd-layout"><div class="fd-main">
        
        <section class="op-card fd-queue"><header><h2>Incoming Bookings &amp; Inquiries</h2></header>
          <div class="fd-filters"><div class="fd-tabs">${tabs.map(([key,label])=>`<button class="${frontDeskTab===key?'active':''}" data-frontdesk-tab="${key}">${label}</button>`).join('')}</div><input id="frontDeskSearch" type="search" placeholder="Search customers or bookings…" value="${esc(frontDeskTerm)}"></div>
          <div class="fd-table-scroll" id="frontDeskTableArea">${['today','requests','enquiries'].includes(frontDeskTab)?`<table class="fd-table"><thead><tr><th>Date</th><th>Customer / Request</th>${columnHead('type','Type')}<th>Event Date</th>${columnHead('status','Status')}${columnHead('source','Source')}<th>Actions</th></tr></thead><tbody>${rows||'<tr><td colspan="7">No bookings or inquiries in this queue.</td></tr>'}</tbody></table>`:'<div id="frontDeskJobList"></div>'}</div>
          </section>
        <div class="fd-metrics">${metric('New Inquiries',enquiries.length,'Awaiting response')}${metric('Pending Deposits',due.length,'Awaiting payment')}${metric('Needs Confirmation',pending.length,'Review and confirm')}${metric('Unassigned',unassigned.length,'Jobs requiring assignment')}</div>
      </div><div class="fd-secondary">
        <section class="op-card fd-actions"><header><h2>Front Desk Services</h2></header><div class="fd-action-grid"><button class="primary" data-new-booking>＋ Create Manual Booking</button><button data-new-client>＋ New Customer</button><button data-find-client>Find Customer</button><button data-take-payment>Take Payment</button><button data-new-sale>New Sale</button><button data-pickups>Pickup / Delivery</button><button type="button" data-completed>Completed</button></div></section>
        <section class="op-card fd-schedule"><header><h2>Today's Schedule</h2><button type="button" class="fd-card-link" data-calendar>View Calendar →</button></header><div class="fd-schedule-list">${sessions.map(s=>`<button data-frontdesk-job="${esc(s.jobId||'')}" class="fd-session"><time>${esc(new Date(s.startsAtUtc).toLocaleTimeString([],{hour:'numeric',minute:'2-digit'}))}</time><span><strong>${esc(s.title)}</strong><small>${esc(s.location||s.status||'')}</small></span></button>`).join('')||'<p class="muted">No sessions scheduled today.</p>'}</div></section>
        <section class="op-card fd-messages"><header><h2>Recent Inquiries</h2><button type="button" class="fd-card-link" data-inquiries>View All →</button></header><div class="fd-message-list">${enquiries.slice(0,5).map(e=>`<button data-fd-enquiry="${esc(e.id)}"><strong>${esc(e.contactName||'Customer')}</strong><small>${esc(e.subject||'New inquiry')}</small></button>`).join('')||'<p class="muted">No new inquiries.</p>'}</div></section>
      </div></div></div>`;
    // The shared shell footer owns the visible filtered row count.
    const shellCount=document.getElementById('studioFooterRate');
    if(shellCount) shellCount.textContent=filtered.length+' '+(filtered.length===1?'row':'rows');
    genericView.querySelectorAll('[data-fd-filter]').forEach(select=>select.onchange=()=>{
        frontDeskColumnFilters[select.dataset.fdFilter]=select.value;renderFrontDesk();
    });
    genericView.querySelectorAll('[data-fd-sort]').forEach(button=>button.onclick=()=>{
        const column=button.dataset.fdSort;
        frontDeskSort=frontDeskSort.column===column?{column,direction:-frontDeskSort.direction}:{column,direction:1};
        renderFrontDesk();
    });
    const newBooking=()=>openPendingBookingForm(null,frontDeskClients.find(c=>c.id===frontDeskSelectedClientId));
    genericView.querySelectorAll('[data-new-booking]').forEach(b=>b.onclick=newBooking);
    genericView.querySelectorAll('[data-new-client]').forEach(b=>b.onclick=async()=>{await showFrontDeskClients();document.querySelector('dialog[open] [data-new]')?.click();});
    genericView.querySelector('[data-find-client]').onclick=()=>showFrontDeskClients();
    genericView.querySelector('[data-take-payment]').onclick=()=>frontDeskSelectedClientId?takeFrontDeskClientPayment(frontDeskSelectedClientId):showPos();
    genericView.querySelector('[data-new-sale]').onclick=()=>showPos();
    genericView.querySelector('[data-pickups]').onclick=()=>{frontDeskTab='delivery';renderFrontDesk();};
    genericView.querySelector('[data-completed]').onclick=()=>{frontDeskTab='completed';renderFrontDesk();};
    genericView.querySelector('[data-calendar]').onclick=()=>studioNavigate('pipeline');
    genericView.querySelector('[data-inquiries]').onclick=()=>{frontDeskTab='enquiries';renderFrontDesk();};
    genericView.querySelectorAll('[data-frontdesk-tab]').forEach(b=>b.onclick=()=>{frontDeskTab=b.dataset.frontdeskTab;renderFrontDesk();});
    genericView.querySelector('#frontDeskSearch').oninput=e=>{frontDeskTerm=e.target.value;renderFrontDesk();genericView.querySelector('#frontDeskSearch')?.focus();};
    genericView.querySelectorAll('[data-frontdesk-job]').forEach(b=>b.onclick=()=>b.dataset.frontdeskJob&&openWorkspaceJob(b.dataset.frontdeskJob));
    genericView.querySelectorAll('[data-fd-row-open]').forEach(row=>{
        const open=()=>row.querySelector('[data-fd-open]')?.click();
        row.addEventListener('click',event=>{
            if(event.target.closest('button,a,input,select'))return;
            open();
        });
        row.addEventListener('keydown',event=>{
            if(event.target!==row||!['Enter',' '].includes(event.key))return;
            event.preventDefault();open();
        });
    });
    genericView.querySelectorAll('[data-fd-open]').forEach(b=>b.onclick=()=>{const e=entries.find(x=>x.id===b.dataset.fdOpen);if(e?.jobId)openWorkspaceJob(e.jobId);else if(e?.conversationId)openPendingBookingForm(frontDeskEnquiries.find(x=>x.id===e.conversationId));});
    genericView.querySelectorAll('[data-fd-enquiry]').forEach(b=>b.onclick=()=>openPendingBookingForm(frontDeskEnquiries.find(e=>e.id===b.dataset.fdEnquiry)));
    if(!['today','requests','enquiries'].includes(frontDeskTab))renderFrontDeskRows();
}
function selectFrontDeskClient(clientId){
    frontDeskSelectedClientId=clientId;
    const dialog=studioDialog('Client history','<div id="frontDeskClientContext" class="frontdesk-client-context"></div>');
    renderFrontDeskClientContext().catch(e=>dialog.querySelector('#frontDeskClientContext').textContent=e.message);
}

function renderFrontDeskClientResults(){
    const host=document.getElementById('frontDeskClientResults');if(!host)return;
    const term=frontDeskClientTerm.trim().toLowerCase();
    const clients=frontDeskClients.filter(c=>!term||[c.displayName,c.email,c.phone].join(' ').toLowerCase().includes(term)).slice(0,30);
    host.innerHTML=clients.map(c=>`<button type="button" class="frontdesk-client-row ${frontDeskSelectedClientId===c.id?'active':''}" data-frontdesk-client="${c.id}"><strong>${esc(c.displayName)}</strong><small>${esc(c.email||c.phone||'No contact detail')}</small><span>${frontDeskJobs.filter(j=>j.clientId===c.id&&j.status!=='Completed').length} active</span></button>`).join('')||'<p class="muted">No clients found.</p>';
    host.querySelectorAll('[data-frontdesk-client]').forEach(b=>b.onclick=()=>selectFrontDeskClient(b.dataset.frontdeskClient));

}
async function renderFrontDeskClientContext(){
    const host=document.getElementById('frontDeskClientContext');if(!host)return;
    const client=frontDeskClients.find(c=>c.id===frontDeskSelectedClientId);if(!client){host.innerHTML='<p class="muted">Select a client.</p>';return;}
    const jobs=frontDeskJobs.filter(j=>j.clientId===client.id);
    const jobIds=new Set(jobs.map(j=>j.id));
    const upcoming=frontDeskSchedule.filter(entry=>entry.jobId&&jobIds.has(entry.jobId)&&new Date(entry.startsAtUtc)>=new Date()).sort((a,b)=>new Date(a.startsAtUtc)-new Date(b.startsAtUtc));
    const invoices=frontDeskInvoices.filter(invoice=>jobIds.has(invoice.jobId));
    const outstanding=invoices.filter(invoice=>['Issued','Overdue'].includes(invoice.status));
    const quotes=await json('/api/frontdesk/quotes?clientId='+encodeURIComponent(client.id)).catch(()=>[]);
    const associated=[...new Set(jobs.map(job=>frontDeskWorkspaces.find(w=>w.id===job.workspaceId)?.name).filter(Boolean))];
    host.innerHTML=`<div class="frontdesk-client-heading"><div><h2>${esc(client.displayName)}</h2><p>${esc(client.email||'')}${client.phone?' · '+esc(client.phone):''}</p><small>${associated.length?esc(associated.join(' · ')):'No associated workspaces yet'}</small></div><button class="action" onclick="showFrontDeskClients()">Open Client</button></div>
    <div class="frontdesk-context-actions"><button class="primary" onclick="createFrontDeskClientJob('${client.id}')">New Job</button><button class="action" onclick="openPendingBookingForm(null,frontDeskClients.find(c=>c.id==='${client.id}'))">New Booking</button><button class="action" onclick="showClientQuotes('${client.id}')">Create / Send Quote</button><button class="action" onclick="takeFrontDeskClientPayment('${client.id}')">Take Payment</button></div>
    <div class="frontdesk-client-summary"><span><b>${jobs.filter(j=>j.status!=='Completed').length}</b> current jobs</span><span><b>${upcoming.length}</b> upcoming bookings / shoots</span><span><b>${quotes.filter(q=>!['Converted'].includes(q.status)).length}</b> open quotes</span><span><b>${outstanding.length}</b> outstanding invoices</span><span><b>${money(jobs.reduce((sum,j)=>sum+studioFinancials(j).balance,0))}</b> outstanding</span></div>
    <h3>Jobs → Workspaces</h3><div class="frontdesk-client-jobs">${jobs.map(job=>`<article class="front-desk-row"><div><strong>${esc(job.title)}</strong><small>${esc(frontDeskWorkspaces.find(w=>w.id===job.workspaceId)?.name||'Workspace')} · ${esc(job.status)}${job.eventDate?' · '+esc(job.eventDate):''}</small></div><div class="front-desk-actions"><button class="action" onclick="showFrontDeskFlow('${job.id}')">Flow</button><button class="action" onclick="messageFrontDeskJob('${job.id}','workspace')">Message Workspace</button><button class="action" onclick="messageFrontDeskJob('${job.id}','escalate')">Escalate</button><button class="action" onclick="messageFrontDeskJob('${job.id}','finance')">Accounts / Finance</button><button class="action" onclick="checkInFrontDeskJob('${job.id}')">Check In</button><button class="primary" data-frontdesk-job="${job.id}">Open</button></div></article>`).join('')||'<p class="muted">No linked jobs yet.</p>'}</div>
    ${upcoming.length?`<h3>Upcoming</h3><div class="frontdesk-client-jobs">${upcoming.slice(0,8).map(entry=>`<article class="front-desk-row"><div><strong>${esc(entry.title)}</strong><small>${esc(new Date(entry.startsAtUtc).toLocaleString())} · ${esc(entry.location||'Location not set')}</small></div>${entry.jobId?`<button type="button" class="action" data-frontdesk-job="${entry.jobId}">Open job</button>`:''}</article>`).join('')}</div>`:''}
    ${quotes.length?`<h3>Quotes</h3><div class="frontdesk-client-jobs">${quotes.slice(0,5).map(q=>`<article class="front-desk-row"><div><strong>${esc(q.request?.title||'Quote')}</strong><small>${esc(q.status)} · ${money(q.total)}</small></div></article>`).join('')}</div>`:''}`;
    host.querySelectorAll('[data-confirm-booking]').forEach(b=>b.onclick=async()=>{b.disabled=true;try{const v=await json('/api/jobs/'+b.dataset.confirmBooking+'/connected-workflow');const reason=v.canOverride?prompt('Confirmation note / deposit override reason (if needed):'):'';if(reason===null){b.disabled=false;return;}await json('/api/jobs/'+b.dataset.confirmBooking+'/connected-workflow',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'confirm',expectedRevision:v.data.revision,operationId:crypto.randomUUID(),reason})});await showFrontDesk();}catch(error){alert(error.message);b.disabled=false;}});
    host.querySelectorAll('[data-frontdesk-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.frontdeskJob));
}
async function createFrontDeskClientJob(clientId){
    const client=frontDeskClients.find(c=>c.id===clientId);if(!client)return;
    const dialog=frontDeskDialog('New Job',`<p>${esc(client.displayName)}</p><label>Workspace<select name="workspace" required>${frontDeskWorkspaces.filter(w=>String(w.routeKey||'').toLowerCase()!=='accounts-finance').map(w=>`<option value="${w.id}">${esc(w.name)}</option>`).join('')}</select></label><label>Job title<input name="title" required maxlength="200"></label><label>Event date<input name="date" type="date"></label>`);
    dialog.querySelector('form').onsubmit=async e=>{e.preventDefault();const form=e.currentTarget;try{await json('/api/frontdesk/clients/'+clientId+'/jobs',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({workspaceId:form.elements.workspace.value,title:form.elements.title.value,eventDate:form.elements.date.value||null})});dialog.close();await showFrontDesk();frontDeskSelectedClientId=clientId;renderFrontDeskClientResults();renderFrontDeskClientContext();}catch(error){form.querySelector('[role=alert]').textContent=error.message;}};
}
async function takeFrontDeskClientPayment(clientId){
    const jobs=frontDeskJobs.filter(job=>job.clientId===clientId&&job.status!=='Completed');
    if(!jobs.length)return alert('This client has no active job to receive a payment.');
    if(jobs.length===1)return openPos(jobs[0].id,true);
    const dialog=studioDialog('Take Payment — choose job',jobs.map(job=>`<button type="button" class="job-row" data-payment-job="${job.id}"><div><strong>${esc(job.title)}</strong><small>${esc(frontDeskWorkspaces.find(w=>w.id===job.workspaceId)?.name||'Workspace')} · ${esc(job.status)}</small></div></button>`).join(''));
    dialog.querySelectorAll('[data-payment-job]').forEach(button=>button.onclick=()=>{const id=button.dataset.paymentJob;dialog.close();openPos(id,true);});
}
async function checkInFrontDeskJob(jobId){try{await json('/api/frontdesk/jobs/'+jobId+'/check-in',{method:'POST'});await showFrontDesk();}catch(error){alert(error.message);}}
function renderFrontDeskRows(){
    const host=document.getElementById('frontDeskJobList'),term=frontDeskTerm.trim().toLowerCase();
    const matches=value=>JSON.stringify(value).toLowerCase().includes(term);
    if(frontDeskTab==='requests')host.innerHTML=frontDeskRequests.filter(matches).map(x=>{
        const r=x.request,remaining=Math.max(0,r.requiredDeposit-x.recordedDeposit);
        return `<article class="front-desk-row"><div><strong>${esc(r.clientName)}</strong><div>${esc(r.title)} · ${esc(r.eventDate)}</div><div class="meta">${esc(frontDeskWorkspaces.find(w=>w.id===r.workspaceId)?.name||'Workspace')} · ${frontDeskRequestStage(x)}</div><div class="meta">Deposit recorded: ${money(x.recordedDeposit)}</div></div><div class="front-desk-actions"><button class="primary" data-frontdesk-job="${x.jobId}">Open Job / Review / Route</button>${!['Confirmed','Cancelled'].includes(x.status)?`<button class="action" data-deposit="${x.id}">Record deposit</button><button class="primary" data-confirm-booking="${x.jobId}">Confirm booking</button>`:`<button class="action" data-frontdesk-pos="${x.jobId}">Billing</button>`}</div></article>`;
    }).join('')||'<p class="empty-state">No Pending Jobs. New requests create a Job immediately for FrontDesk review.</p>';
    else if(frontDeskTab==='enquiries'){
        const linked=new Set(frontDeskRequests.map(r=>r.request.conversationId).filter(Boolean));
        host.innerHTML=frontDeskEnquiries.filter(e=>!linked.has(e.id)&&matches(e)).map(e=>`<article class="front-desk-row"><div><strong>${esc(e.contactName)}</strong><div>${esc(e.subject)}</div><small>${esc(e.channel)} · ${esc(new Date(e.createdAtUtc).toLocaleString())}</small></div><button class="primary" data-convert-enquiry="${e.id}">Prepare booking request</button></article>`).join('')||'<p class="empty-state">No unassigned enquiries awaiting booking preparation.</p>';
    }else{
        let jobs=frontDeskJobs.filter(matches);if(frontDeskTab==='delivery')jobs=jobs.filter(j=>['Ready for Client','Ready for Delivery','Published'].includes(j.status));else if(frontDeskTab==='completed')jobs=jobs.filter(j=>j.status==='Completed');else jobs=jobs.filter(j=>j.status!=='Completed');
        host.innerHTML=jobs.map(renderFrontDeskJob).join('')||'<p class="empty-state">No jobs in this queue.</p>';
    }
    host.querySelectorAll('[data-frontdesk-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.frontdeskJob));
    host.querySelectorAll('[data-frontdesk-fulfill]').forEach(b=>b.onclick=()=>recordFrontDeskFulfillment(b.dataset.frontdeskFulfill));
    host.querySelectorAll('[data-frontdesk-pos]').forEach(b=>b.onclick=()=>openPos(b.dataset.frontdeskPos));
    host.querySelectorAll('[data-convert-enquiry]').forEach(b=>b.onclick=()=>openPendingBookingForm(frontDeskEnquiries.find(e=>e.id===b.dataset.convertEnquiry)));
    host.querySelectorAll('[data-deposit]').forEach(b=>b.onclick=()=>openVerifiedDepositForm(frontDeskRequests.find(r=>r.id===b.dataset.deposit)));
    host.querySelectorAll('[data-deposit-requested]').forEach(b=>b.onclick=async()=>{if(!confirm('Confirm that you have already sent the deposit request to the customer. This records the follow-up status; it does not send a message.'))return;b.disabled=true;try{await json(`/api/frontdesk/booking-requests/${b.dataset.depositRequested}/deposit-requested`,{method:'POST'});await showFrontDesk();}catch(e){alert(e.message);b.disabled=false;}});
}
function renderFrontDeskJob(job){return `<article class="front-desk-row"><div><strong>${esc(job.clientName)}</strong><div>${esc(job.title)}</div><div class="meta">${esc(job.status)}${job.eventDate?' · '+esc(job.eventDate):''}</div></div><div class="front-desk-actions"><button class="action" onclick="linkFrontDeskClient('${job.id}')">${job.clientId?'Linked client':'Link client'}</button><button class="action" onclick="openFrontDeskClient('${job.id}')">Customer conversation</button><button class="action" onclick="showFrontDeskFlow('${job.id}')">Flow</button><button class="action" onclick="messageFrontDeskJob('${job.id}','workspace')">Message Workspace</button><button class="action" onclick="messageFrontDeskJob('${job.id}','escalate')">Escalate</button><button class="action" onclick="messageFrontDeskJob('${job.id}','finance')">Message Accounts / Finance</button><button class="action" data-frontdesk-job="${job.id}">Open job</button><button class="primary" data-frontdesk-pos="${job.id}">Balance / Billing</button>${['Ready for Client','Ready for Delivery','Published'].includes(job.status)?`<button class="primary" data-frontdesk-fulfill="${job.id}">Record collection / delivery</button>`:''}</div></article>`;}
function frontDeskDialog(title,contents){
    const dialog=document.createElement('dialog');dialog.className='frontdesk-dialog';dialog.innerHTML=`<form class="request-form"><h2>${esc(title)}</h2>${contents}<p role="alert"></p><div class="dialog-actions"><button type="button" data-cancel>Cancel</button><button class="primary" type="submit">Save</button></div></form>`;document.body.appendChild(dialog);dialog.querySelector('[data-cancel]').onclick=()=>dialog.close();dialog.addEventListener('close',()=>dialog.remove(),{once:true});dialog.showModal();return dialog;
}
function openPendingBookingForm(enquiry,client){
    const dialog=frontDeskDialog('Pending booking request',`<p>A Pending Job is created now. FrontDesk reviews, routes and confirms it separately.</p><label>Workspace<select name="workspace" required>${frontDeskWorkspaces.map(w=>`<option value="${w.id}" ${w.id===enquiry?.workspaceId?'selected':''}>${esc(w.name)}</option>`).join('')}</select></label><label>Customer<input name="customer" required maxlength="200" value="${esc(enquiry?.contactName||client?.displayName||'')}"></label><label>Email<input name="email" type="email" required maxlength="320" value="${esc(enquiry?.channel==='Email'?enquiry.contactAddress:(client?.email||''))}"></label><label>Wedding / session title<input name="title" required maxlength="200" value="${esc(enquiry?.subject||'')}"></label><label>Event date<input name="date" type="date" required></label><label>Required deposit (JMD)<input name="deposit" type="number" min="0" max="999999999" step="0.01" value="0"></label><label>Notes<textarea name="notes" maxlength="4000"></textarea></label>`);
    const form=dialog.querySelector('form');if(enquiry)form.elements.workspace.disabled=true;
    form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('[type=submit]');button.disabled=true;try{await json('/api/frontdesk/booking-requests',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({workspaceId:form.elements.workspace.value,clientName:form.elements.customer.value,clientEmail:form.elements.email.value,title:form.elements.title.value,eventDate:form.elements.date.value,requiredDeposit:Number(form.elements.deposit.value),currency:'JMD',notes:form.elements.notes.value,conversationId:enquiry?.id||null})});dialog.close();frontDeskTab='requests';await showFrontDesk();}catch(error){form.querySelector('[role=alert]').textContent=error.message;}finally{button.disabled=false;}};
}
function openVerifiedDepositForm(request){
    const remaining=Math.max(0,request.request.requiredDeposit-request.recordedDeposit);
    const dialog=frontDeskDialog('Record deposit',`<p>${esc(request.request.clientName)} · ${esc(request.request.title)}</p><p>Record money already received and verified. This does not charge the customer. Any recorded positive deposit enables a separate FrontDesk confirmation action. It does not confirm or create another Job.</p><label>Amount received (JMD)<input name="amount" type="number" step="0.01" min="0.01" max="999999999" value="${remaining}" required></label><label>Payment provider / method<input name="provider" maxlength="100" required placeholder="Bank transfer, cash receipt…"></label><label>Unique receipt / transaction reference<input name="reference" maxlength="200" required></label>`);
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
    const [job,timeline,configuredStages]=await Promise.all([
        json('/api/jobs/'+jobId),
        json('/api/jobs/'+jobId+'/timeline?count=200'),
        json('/api/workflow/stages')
    ]);
    const events=Array.isArray(timeline)?timeline:(timeline.items||timeline.events||[]);
    const stageNames=configuredStages;
    const normalized=String(job.status||'').toLowerCase();
    let current=stageNames.findIndex(stage=>stage.toLowerCase()===normalized);
    if(current<0&&['new','confirmed','inquiry','needs scheduling'].includes(normalized))current=stageNames.findIndex(stage=>stage.toLowerCase()==='needs scheduling');
    if(current<0)current=0;
    const eventMarkup=list=>list.slice(0,30).map(event=>`<p><strong>${esc(event.title||event.eventType||'Activity')}</strong><small>${esc(event.detail||event.description||'')} ${event.createdAtUtc||event.occurredAtUtc?' · '+new Date(event.createdAtUtc||event.occurredAtUtc).toLocaleString():''}</small></p>`).join('')||'<p class="muted">No recorded history for this stage.</p>';
    const dialog=studioDialog('Job Flow — '+job.title,`<div class="frontdesk-flow">${stageNames.map((stage,index)=>`<button type="button" class="frontdesk-flow-stage ${index<current?'complete':index===current?'current':'future'}" data-flow-stage-name="${esc(stage)}"><small>${index<current?'Completed':index===current?'Current':'Upcoming'}</small><strong>${esc(stage)}</strong></button>`).join('<span>›</span>')}</div><div class="frontdesk-flow-history"><h3 data-flow-history-title>History</h3><div data-flow-history>${eventMarkup(events)}</div></div>`);
    dialog.querySelectorAll('[data-flow-stage-name]').forEach(button=>button.onclick=()=>{
        const stage=button.dataset.flowStageName;
        dialog.querySelector('[data-flow-history-title]').textContent=stage+' history';
        const related=events.filter(event=>[event.title,event.eventType,event.detail,event.description].filter(Boolean).join(' ').toLowerCase().includes(stage.toLowerCase()));
        dialog.querySelector('[data-flow-history]').innerHTML=eventMarkup(related);
    });
    return dialog;
}

async function messageFrontDeskJob(jobId, mode) {
    const [job,thread]=await Promise.all([
        json('/api/jobs/'+jobId),
        json('/api/messages/threads/Job/'+jobId,{method:'POST'})
    ]);
    const history=await json('/api/messages/threads/'+thread.id+'?count=80');
    const label=mode==='finance'?'Accounts / Finance':mode==='escalate'?'Escalation':'Workspace';
    const dialog=frontDeskDialog(label+' message',`<p><strong>${esc(job.clientName||'Client')}</strong> · ${esc(job.title)} · ${esc(job.status)}</p><div class="frontdesk-message-history">${history.map(message=>`<article><strong>${esc(studioFirstName(message.senderUserName))}</strong><small>${esc(new Date(message.createdAtUtc).toLocaleString())}</small><p>${esc(message.body)}</p></article>`).join('')||'<p class="muted">No job-team messages yet.</p>'}</div>${mode==='escalate'?'<label>Escalate to<select name="target"><option value="Workspace Manager">Workspace Manager</option><option value="Operations Manager">Operations Manager</option></select></label>':''}<label>Message<textarea name="body" required maxlength="4000" placeholder="${mode==='finance'?'Question about quote, invoice, payment, balance or refund':'What is happening with this job?'}"></textarea></label><label>Attach document<input name="file" type="file"></label><p class="muted">This is the job conversation. Replies remain here and in the job view. Any attached file also appears in Conversation Files.</p>`);
    const form=dialog.querySelector('form');
    form.onsubmit=async e=>{
        e.preventDefault();
        if(!job.conversationId){form.querySelector('[role=alert]').textContent='This job has no linked conversation.';return;}
        try{
            const routed=await json('/api/frontdesk/jobs/'+jobId+'/communications',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({kind:mode,body:form.elements.body.value,escalationTarget:mode==='escalate'?form.elements.target.value:null})});
            const file=form.elements.file.files[0];
            if(file){
                const data=new FormData();data.append('file',file);
                const response=await fetch('/api/conversations/'+job.conversationId+'/attachments?visibility=Internal',{method:'POST',body:data});
                if(!response.ok)throw new Error(await response.text());
                const attachment=await response.json();
                const link=await fetch('/api/attachments/'+attachment.id+'/message-link',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({messageId:routed.messageId,messageKind:'JobMessage',jobId:job.id})});
                if(!link.ok)throw new Error(await link.text());
            }
            dialog.close();
            await messageFrontDeskJob(jobId,mode);
        }catch(error){form.querySelector('[role=alert]').textContent=error.message;}
    };
}


function recordFrontDeskFulfillment(jobId){
 const job=frontDeskJobs.find(j=>j.id===jobId),operationId=crypto.randomUUID();
 const dialog=frontDeskDialog('Confirm collection / delivery',`<p>${esc(job?.title||'Job')} · ${esc(job?.clientName||'')}</p><label>Method<select name="method"><option value="Collected">Customer collected</option><option value="Delivered">Delivered to customer</option></select></label><label>Received by<input name="recipient" required maxlength="200"></label>`);
 dialog.querySelector('form').onsubmit=async event=>{event.preventDefault();const form=event.currentTarget,button=form.querySelector('[type=submit]');button.disabled=true;try{await json(`/api/frontdesk/jobs/${jobId}/fulfillment`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({operationId,method:form.elements.method.value,recipient:form.elements.recipient.value})});dialog.close();await showFrontDesk();}catch(error){form.querySelector('[role=alert]').textContent=error.message;button.disabled=false;}};
}
