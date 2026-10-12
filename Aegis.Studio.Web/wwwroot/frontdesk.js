let frontDeskJobs = [];
let frontDeskRole = null;
let frontDeskMetricView = '';
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
    const preservedMessageCard=genericView.querySelector('#frontDeskMessageCard');
    const searchNode=genericView.querySelector('#frontDeskSearch');
    const searchFocused=searchNode===document.activeElement;
    const searchSelection=searchNode?[searchNode.selectionStart,searchNode.selectionEnd]:null;
    // Follow the Today page's full-width horizontal shell. No navigation sidebar
    // or additional nested side rail; the secondary cards belong to the content grid.
    const now=new Date(), today=now.toLocaleDateString();
    const sessions=frontDeskSchedule.filter(s=>new Date(s.startsAtUtc).toLocaleDateString()===today&&s.status!=='Cancelled').sort((a,b)=>new Date(a.startsAtUtc)-new Date(b.startsAtUtc));
    const pending=frontDeskRequests.filter(x=>x&&!['Confirmed','Cancelled'].includes(x.status));
    const unassigned=frontDeskJobs.filter(j=>!j.assignedTo&&j.status!=='Completed');
    const enquiries=frontDeskEnquiries.filter(e=>e&&!frontDeskRequests.some(r=>r?.request?.conversationId===e.id));
    const due=frontDeskRequests.filter(x=>x&&(x.status==='AwaitingDeposit'||(x.request?.requiredDeposit>0&&x.recordedDeposit<x.request.requiredDeposit&&!['Confirmed','Cancelled'].includes(x.status))));
    const entries=[
        ...enquiries.map(e=>({kind:'enquiry',id:e.id,created:e.createdAtUtc,name:e.contactName||'New inquiry',title:e.subject||'Inquiry',date:null,status:'New',source:e.channel||'Inquiry',conversationId:e.id})),
        ...frontDeskRequests.filter(Boolean).map(x=>({kind:'request',id:x.id,jobId:x.jobId,created:x.createdAtUtc||x.request?.createdAtUtc,name:x.request?.clientName||'Customer',title:x.request?.title||'Booking',date:x.request?.eventDate,status:frontDeskRequestStage(x),source:'Booking',request:x}))
    ].sort((a,b)=>new Date(b.created||0)-new Date(a.created||0));
    const entryType=e=>e.kind==='enquiry'?'Inquiry':'Booking';
    const valuesFor=key=>{const values=[...new Set(entries.map(e=>key==='type'?entryType(e):String(e[key]||'')))].filter(Boolean);return key==='status'?StudioStatusOrder.sort(values):values.sort((a,b)=>a.localeCompare(b));};
    const metricMatch=e=>{
        if(!frontDeskMetricView)return true;
        if(frontDeskMetricView==='enquiries')return e.kind==='enquiry';
        if(e.kind!=='request')return false;
        const request=frontDeskRequests.find(x=>x?.id===e.id);
        if(frontDeskMetricView==='deposits')return !!request&&(request.status==='AwaitingDeposit'||(request.request?.requiredDeposit>0&&request.recordedDeposit<request.request.requiredDeposit&&!['Confirmed','Cancelled'].includes(request.status)));
        if(frontDeskMetricView==='confirmation')return !!request&&!['Confirmed','Cancelled'].includes(request.status);
        return true;
    };
    const filtered=entries.filter(e=>metricMatch(e)&&
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
    const rows=filtered.map(e=>`<tr class="fd-speedbump-row" tabindex="0" role="link" aria-label="Open ${esc(e.name)}" data-fd-row-open="${esc(e.id)}"><td><small>${esc(e.created?new Date(e.created).toLocaleDateString():'—')}</small></td><td><strong>${esc(e.name)}</strong><small>${esc(e.title)}</small></td><td>${pill(entryType(e),'type')}</td><td>${esc(e.date||'—')}</td><td>${pill(e.status,'status')}</td><td>${pill(e.source,'source')}</td><td class="fd-row-chevron" aria-hidden="true">›</td></tr>`).join('');
    const metric=(key,label,count,note)=>`<button type="button" class="fd-metric fd-metric-link${frontDeskMetricView===key?' is-active':''}" data-fd-metric="${key}" aria-pressed="${frontDeskMetricView===key}"><span>${esc(label)}</span><strong>${count}</strong><small>${esc(note)}</small></button>`;
    genericView.innerHTML=`<div class="fd-page">
      
      <div class="fd-layout"><div class="fd-main">
        
        <section class="op-card fd-queue"><header class="fd-queue-header"><h2>Incoming Bookings &amp; Inquiries</h2><div class="fd-queue-header-tools"><input id="frontDeskSearch" type="search" aria-label="Search bookings and inquiries" placeholder="Search customers or bookings…" value="${esc(frontDeskTerm)}"></div></header>
          <div class="fd-table-scroll" id="frontDeskTableArea">${['today','requests','enquiries'].includes(frontDeskTab)?`<table class="fd-table"><thead><tr><th>Date</th><th>Customer / Request</th>${columnHead('type','Type')}<th>Event Date</th>${columnHead('status','Status')}${columnHead('source','Source')}<th aria-label="Open Record"></th></tr></thead><tbody>${rows||'<tr><td colspan="7">No bookings or inquiries in this queue.</td></tr>'}</tbody></table>`:'<div id="frontDeskJobList"></div>'}</div>
          </section>
        <div class="fd-metrics">${metric('enquiries','New Inquiries',enquiries.length,'Awaiting response')}${metric('deposits','Pending Deposits',due.length,'Awaiting payment')}${metric('confirmation','Needs Confirmation',pending.length,'Review and confirm')}${metric('unassigned','Unassigned',unassigned.length,'Jobs requiring assignment')}</div>
      </div><div class="fd-secondary">
        <section class="op-card fd-actions"><header><h2>Front Desk Services</h2></header><div class="fd-action-grid"><button class="primary" data-new-booking>＋ Create Manual Booking</button><button data-new-client>＋ New Customer</button><button data-find-client>Find Customer</button><button data-take-payment>Take Payment</button><button data-new-sale>New Sale</button><button onclick="pickFrontDeskFinancialAction('RecordDeposit')">Record Deposit</button><button onclick="pickFrontDeskFinancialAction('IssueRefund')">Issue Refund</button><button onclick="pickFrontDeskFinancialAction('ViewReceipt')">View Receipt</button><button onclick="launchFrontDeskPos('CloseRegister')">Close Register</button><button data-pickups>Pickup / Delivery</button><button type="button" data-completed>Completed</button></div></section>
        <section class="op-card fd-schedule"><header><h2>Today's Schedule</h2><button type="button" class="fd-card-link" data-calendar>View Calendar →</button></header><div class="fd-schedule-list">${sessions.map(s=>`<button data-frontdesk-job="${esc(s.jobId||'')}" class="fd-session"><time>${esc(new Date(s.startsAtUtc).toLocaleTimeString([],{hour:'numeric',minute:'2-digit'}))}</time><span><strong>${esc(s.title)}</strong><small>${esc(s.location||s.status||'')}</small></span></button>`).join('')||'<p class="muted">No sessions scheduled today.</p>'}</div></section>
        ${StudioMessageCard.html({id:'frontDeskMessageCard',body:'<div id="fdCommunicationFeed"><p>Loading messages…</p></div>',actions:'<button type="button" data-messages-all>View All →</button>',footer:'<button type="button" data-message-compose>✉ Send Message →</button>',attributes:'data-mw-region="messages" data-fd-communications'})}
      </div></div></div>`;
    if(preservedMessageCard)genericView.querySelector('#frontDeskMessageCard').replaceWith(preservedMessageCard);
    if(searchNode){genericView.querySelector('#frontDeskSearch').replaceWith(searchNode);searchNode.value=frontDeskTerm;if(searchFocused){searchNode.focus({preventScroll:true});searchNode.setSelectionRange(...searchSelection);}}
    // The shared shell footer owns the visible filtered row count.
    const shellCount=document.getElementById('studioFooterRate');
    if(shellCount){
        const jobMatches=frontDeskJobs.filter(j=>JSON.stringify(j).toLowerCase().includes(frontDeskTerm.trim().toLowerCase()));
        const visibleJobs=frontDeskTab==='unassigned'?jobMatches.filter(j=>!j.assignedTo&&j.status!=='Completed'):frontDeskTab==='completed'?jobMatches.filter(j=>j.status==='Completed'):frontDeskTab==='delivery'?jobMatches.filter(j=>['Ready for Client','Ready for Delivery','Published'].includes(j.status)):[];
        const count=['unassigned','completed','delivery'].includes(frontDeskTab)?visibleJobs.length:filtered.length;
        const label=frontDeskTab==='unassigned'?'Unassigned Jobs':frontDeskTab==='completed'?'Completed Jobs':frontDeskTab==='delivery'?'Pickup / Delivery Jobs':frontDeskMetricView==='enquiries'||frontDeskTab==='enquiries'?'New Inquiries':frontDeskMetricView==='deposits'?'Pending Deposits':frontDeskMetricView==='confirmation'?'Needs Confirmation':'Bookings & Inquiries';
        shellCount.textContent=count+' '+label;
    }
    genericView.querySelectorAll('[data-fd-metric]').forEach(button=>button.onclick=()=>{
        const key=button.dataset.fdMetric;
        frontDeskMetricView=frontDeskMetricView===key?'':key;
        frontDeskTab=frontDeskMetricView==='unassigned'?'unassigned':'today';
        frontDeskTerm='';
        frontDeskColumnFilters={type:'',status:'',source:''};
        renderFrontDesk();
    });
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
    genericView.querySelector('[data-take-payment]').onclick=()=>pickFrontDeskFinancialAction('TakePayment');
    genericView.querySelector('[data-new-sale]').onclick=()=>pickFrontDeskFinancialAction('NewSale');
    genericView.querySelector('[data-pickups]').onclick=()=>{frontDeskTab='delivery';renderFrontDesk();};
    genericView.querySelector('[data-completed]').onclick=()=>{frontDeskTab='completed';renderFrontDesk();};
    genericView.querySelector('[data-calendar]').onclick=()=>studioNavigate('pipeline');
    genericView.querySelector('[data-messages-all]').onclick=()=>studioNavigate('messages');
    genericView.querySelector('[data-message-compose]').onclick=()=>openFrontDeskMessagePicker();
    StudioMessageCard.bind(genericView.querySelector('#frontDeskMessageCard'));
    loadFrontDeskCommunications();
    genericView.querySelector('#frontDeskSearch').oninput=e=>{frontDeskTerm=e.target.value;renderFrontDesk();genericView.querySelector('#frontDeskSearch')?.focus();};
    genericView.querySelectorAll('[data-frontdesk-job]').forEach(b=>b.onclick=()=>b.dataset.frontdeskJob&&openWorkspaceJob(b.dataset.frontdeskJob));
    genericView.querySelectorAll('[data-fd-row-open]').forEach(row=>{
        const open=()=>{
            const e=entries.find(item=>String(item.id)===row.dataset.fdRowOpen);
            if(e?.jobId)openWorkspaceJob(e.jobId);
            else if(e?.conversationId)openPendingBookingForm(frontDeskEnquiries.find(item=>item.id===e.conversationId));
        };
        row.addEventListener('click',event=>{
            if(event.target.closest('button,a,input,select'))return;
            open();
        });
        row.addEventListener('keydown',event=>{
            if(event.target!==row||!['Enter',' '].includes(event.key))return;
            event.preventDefault();
            open();
        });
    });
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
    <h3>Jobs → Workspaces</h3><div class="frontdesk-client-jobs">${jobs.map(job=>`<article class="front-desk-row"><div><strong>${esc(job.title)}</strong><small>${esc(frontDeskWorkspaces.find(w=>w.id===job.workspaceId)?.name||'Workspace')} · ${esc(job.status)}${job.eventDate?' · '+esc(job.eventDate):''}</small></div><div class="front-desk-actions"><button class="action" onclick="showFrontDeskFlow('${job.id}')">Next action / Flow</button><button class="action" onclick="messageFrontDeskJob('${job.id}','workspace')">Message Workspace</button><button class="action" onclick="messageFrontDeskJob('${job.id}','escalate')">Escalate</button><button class="action" onclick="messageFrontDeskJob('${job.id}','finance')">Accounts / Finance</button><button class="action" onclick="checkInFrontDeskJob('${job.id}')">Check In</button><button class="primary" data-frontdesk-job="${job.id}">Open</button></div></article>`).join('')||'<p class="muted">No linked jobs yet.</p>'}</div>
    ${upcoming.length?`<h3>Upcoming</h3><div class="frontdesk-client-jobs">${upcoming.slice(0,8).map(entry=>`<article class="front-desk-row"><div><strong>${esc(entry.title)}</strong><small>${esc(new Date(entry.startsAtUtc).toLocaleString())} · ${esc(entry.location||'Location not set')}</small></div>${entry.jobId?`<button type="button" class="action" data-frontdesk-job="${entry.jobId}">Open job</button>`:''}</article>`).join('')}</div>`:''}
    ${quotes.length?`<h3>Quotes</h3><div class="frontdesk-client-jobs">${quotes.slice(0,5).map(q=>`<article class="front-desk-row"><div><strong>${esc(q.request?.title||'Quote')}</strong><small>${esc(q.status)} · ${money(q.total)}</small></div></article>`).join('')}</div>`:''}`;
    host.querySelectorAll('[data-confirm-booking]').forEach(b=>b.onclick=async()=>{b.disabled=true;try{const v=await json('/api/jobs/'+b.dataset.confirmBooking+'/connected-workflow');const reason='';if(reason===null){b.disabled=false;return;}await json('/api/jobs/'+b.dataset.confirmBooking+'/connected-workflow',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'confirm',expectedRevision:v.data.revision,operationId:crypto.randomUUID(),reason})});await showFrontDesk();}catch(error){alert(error.message);b.disabled=false;}});
    host.querySelectorAll('[data-frontdesk-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.frontdeskJob));
}
async function createFrontDeskClientJob(clientId){
    const client=frontDeskClients.find(c=>c.id===clientId);if(!client)return;
    const dialog=frontDeskDialog('New Job',`<p>${esc(client.displayName)}</p><button type="button" data-template-preview disabled>Preview workflow</button><div data-template-services class="sg-service-list"></div><label>Workspace<select name="workspace" required>${frontDeskWorkspaces.filter(w=>String(w.routeKey||'').toLowerCase()!=='accounts-finance').map(w=>`<option value="${w.id}">${esc(w.name)}</option>`).join('')}</select></label><label>Job title<input name="title" required maxlength="200"></label><label>Event date<input name="date" type="date"></label>`);
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
        return `<article class="front-desk-row"><div><strong>${esc(r.clientName)}</strong><div>${esc(r.title)} · ${esc(r.eventDate)}</div><div class="meta">${esc(frontDeskWorkspaces.find(w=>w.id===r.workspaceId)?.name||'Workspace')} · ${frontDeskRequestStage(x)}</div><div class="meta">Deposit recorded: ${money(x.recordedDeposit)}</div></div><div class="front-desk-actions"><button class="primary" data-frontdesk-job="${x.jobId}">Open Job / Review / Route</button>${!['Confirmed','Cancelled'].includes(x.status)?`<button class="action" data-deposit="${x.id}">Record deposit</button><button class="action" data-deposit-requested="${x.id}">Send deposit request</button><button class="action" data-deposit-delivery="${x.id}">Delivery status</button><button class="primary" data-confirm-booking="${x.jobId}">Confirm booking</button>`:`<button class="action" data-frontdesk-pos="${x.jobId}">Billing</button>`}</div></article>`;
    }).join('')||'<p class="empty-state">No Pending Jobs. New requests create a Job immediately for FrontDesk review.</p>';
    else if(frontDeskTab==='enquiries'){
        const linked=new Set(frontDeskRequests.map(r=>r.request.conversationId).filter(Boolean));
        host.innerHTML=frontDeskEnquiries.filter(e=>!linked.has(e.id)&&matches(e)).map(e=>`<article class="front-desk-row"><div><strong>${esc(e.contactName)}</strong><div>${esc(e.subject)}</div><small>${esc(e.channel)} · ${esc(new Date(e.createdAtUtc).toLocaleString())}</small></div><button class="primary" data-convert-enquiry="${e.id}">Prepare booking request</button></article>`).join('')||'<p class="empty-state">No unassigned enquiries awaiting booking preparation.</p>';
    }else{
        let jobs=frontDeskJobs.filter(matches);if(frontDeskTab==='delivery')jobs=jobs.filter(j=>['Ready for Client','Ready for Delivery','Published'].includes(j.status));else if(frontDeskTab==='completed')jobs=jobs.filter(j=>j.status==='Completed');else if(frontDeskTab==='unassigned')jobs=jobs.filter(j=>!j.assignedTo&&j.status!=='Completed');else jobs=jobs.filter(j=>j.status!=='Completed');
        host.innerHTML=jobs.map(renderFrontDeskJob).join('')||'<p class="empty-state">No jobs in this queue.</p>';
    }
    host.querySelectorAll('[data-frontdesk-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.frontdeskJob));
    host.querySelectorAll('[data-frontdesk-fulfill]').forEach(b=>b.onclick=()=>recordFrontDeskFulfillment(b.dataset.frontdeskFulfill));
    host.querySelectorAll('[data-frontdesk-pos]').forEach(b=>b.onclick=()=>openPos(b.dataset.frontdeskPos));
    host.querySelectorAll('[data-convert-enquiry]').forEach(b=>b.onclick=()=>openPendingBookingForm(frontDeskEnquiries.find(e=>e.id===b.dataset.convertEnquiry)));
    host.querySelectorAll('[data-deposit]').forEach(b=>b.onclick=()=>openVerifiedDepositForm(frontDeskRequests.find(r=>r.id===b.dataset.deposit)));
    host.querySelectorAll('[data-deposit-requested]').forEach(b=>b.onclick=async()=>{b.disabled=true;try{const channels=await json('/api/frontdesk/deposit-delivery-channels');if(!Array.isArray(channels)||!channels.length)throw Error('Configure an email or WhatsApp provider before sending a deposit request.');const chosen=channels.length===1?channels[0]:prompt('Delivery channel: '+channels.join(' or '),'Email');if(chosen===null)return;if(!channels.includes(chosen))throw Error('Choose one of the configured delivery channels.');const queued=await json(`/api/frontdesk/booking-requests/${b.dataset.depositRequested}/deposit-requested?channel=${encodeURIComponent(chosen)}`,{method:'POST'});alert(queued.state==='AlreadyQueued'?'A deposit request is already queued. Check its delivery status.':'Deposit request queued for '+(queued.channel||chosen)+' delivery. Check delivery status for provider acceptance or failures.');await showFrontDesk();}catch(e){alert(e.message);}finally{if(b.isConnected)b.disabled=false;}});
    host.querySelectorAll('[data-deposit-delivery]').forEach(b=>b.onclick=async()=>{try{const delivery=await json(`/api/frontdesk/booking-requests/${b.dataset.depositDelivery}/deposit-delivery`);alert((delivery.channel||'Delivery')+': '+delivery.state+'\nAttempts: '+delivery.attempts+(delivery.lastError?'\nError: '+delivery.lastError:'')+(delivery.providerAcceptedAtUtc?'\nProvider accepted: '+new Date(delivery.providerAcceptedAtUtc).toLocaleString():'')+'\nRecipient delivery receipt is unavailable from the configured provider.');}catch(e){alert(e.message);}});
}
function renderFrontDeskJob(job){return `<article class="front-desk-row"><div><strong>${esc(job.clientName)}</strong><div>${esc(job.title)}</div><div class="meta">${esc(job.status)}${job.eventDate?' · '+esc(job.eventDate):''}</div></div><div class="front-desk-actions"><button class="action" onclick="linkFrontDeskClient('${job.id}')">${job.clientId?'Linked client':'Link client'}</button><button class="action" onclick="openFrontDeskClient('${job.id}')">Customer conversation</button><button class="action" onclick="showFrontDeskFlow('${job.id}')">Next action / Flow</button><button class="action" onclick="messageFrontDeskJob('${job.id}','workspace')">Message Workspace</button><button class="action" onclick="messageFrontDeskJob('${job.id}','escalate')">Escalate</button><button class="action" onclick="messageFrontDeskJob('${job.id}','finance')">Message Accounts / Finance</button><button class="action" data-frontdesk-job="${job.id}">Open job</button><button class="primary" data-frontdesk-pos="${job.id}">New Sale</button><button class="action" onclick="launchFrontDeskPos('TakePayment',{jobId:'${job.id}'})">Take Payment</button><button class="action" onclick="pickFrontDeskSale('IssueRefund','${job.id}')">Issue Refund</button><button class="action" onclick="pickFrontDeskSale('ViewReceipt','${job.id}')">View Receipt</button>${['Ready for Client','Ready for Delivery','Published'].includes(job.status)?`<button class="primary" data-frontdesk-fulfill="${job.id}">Record collection / delivery</button>`:''}</div></article>`;}
function frontDeskDialog(title,contents){
    const dialog=document.createElement('dialog');dialog.className='frontdesk-dialog';dialog.innerHTML=`<form class="request-form"><header class="studio-dialog-heading"><h2>${esc(title)}</h2><button class="studio-dialog-close" type="button" data-cancel aria-label="Close">×</button></header>${contents}<p role="alert"></p><div class="dialog-actions"><button type="button" data-cancel>Cancel</button><button class="primary" type="submit">Save</button></div></form>`;document.body.appendChild(dialog);dialog.querySelectorAll('[data-cancel]').forEach(button=>button.onclick=()=>dialog.close());dialog.addEventListener('close',()=>dialog.remove(),{once:true});dialog.showModal();return dialog;
}
async function openPendingBookingForm(enquiry,client){
    const jobTemplates=await json('/api/commercial/templates/Job');
    const dialog=frontDeskDialog('Pending booking request',`<p>A Pending Job is created now. FrontDesk reviews, routes and confirms it separately.</p><label>Job Template<select name="jobTemplate"><option value="">Custom Job</option>${jobTemplates.map(t=>`<option value="${t.id}">${esc(t.definition.name)}</option>`).join('')}</select></label><label>Workspace<select name="workspace" required>${frontDeskWorkspaces.map(w=>`<option value="${w.id}" ${w.id===enquiry?.workspaceId?'selected':''}>${esc(w.name)}</option>`).join('')}</select></label><label>Customer<input name="customer" required maxlength="200" value="${esc(enquiry?.contactName||client?.displayName||'')}"></label><label>Email<input name="email" type="email" required maxlength="320" value="${esc(enquiry?.channel==='Email'?enquiry.contactAddress:(client?.email||''))}"></label><label>Phone (international format, e.g. +1876…)<input name="phone" maxlength="80" value="${esc(client?.phone||'')}"></label><label>Wedding / session title<input name="title" required maxlength="200" value="${esc(enquiry?.subject||'')}"></label><label>Event date<input name="date" type="date" required></label><label>Required deposit (JMD)<input name="deposit" type="number" min="0" max="999999999" step="0.01" value="0"></label><label>Notes<textarea name="notes" maxlength="4000"></textarea></label>`);
    const form=dialog.querySelector('form');if(enquiry)form.elements.workspace.disabled=true;
    let selectedServices=null;const products=await json('/api/pos/catalog');
    form.elements.jobTemplate.onchange=()=>{const t=jobTemplates.find(t=>t.id===form.elements.jobTemplate.value);selectedServices=t?(t.definition.products||[]).map(p=>p.catalogItemId):null;form.querySelector('[data-template-preview]').disabled=!t;form.querySelector('[data-template-services]').innerHTML=t?products.filter(p=>p.isActive).map(p=>`<label><input data-service="${p.id}" type="checkbox" ${selectedServices.includes(p.id)?'checked':''}>${esc(p.name)}</label>`).join(''):'';form.querySelectorAll('[data-service]').forEach(el=>el.onchange=()=>selectedServices=[...form.querySelectorAll('[data-service]:checked')].map(x=>x.dataset.service));};
    form.querySelector('[data-template-preview]').onclick=()=>StudioGraphTemplates.preview(jobTemplates.find(t=>t.id===form.elements.jobTemplate.value).definition,products,selectedServices);
    form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('[type=submit]');button.disabled=true;try{await json('/api/frontdesk/booking-requests',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({jobTemplateId:form.elements.jobTemplate.value||null,selectedServiceIds:selectedServices,workspaceId:form.elements.workspace.value,clientName:form.elements.customer.value,clientEmail:form.elements.email.value,clientPhone:form.elements.phone.value||null,title:form.elements.title.value,eventDate:form.elements.date.value,requiredDeposit:Number(form.elements.deposit.value),currency:'JMD',notes:form.elements.notes.value,conversationId:enquiry?.id||null})});dialog.close();frontDeskTab='requests';await showFrontDesk();}catch(error){form.querySelector('[role=alert]').textContent=error.message;}finally{button.disabled=false;}};
}
function openVerifiedDepositForm(request){return launchFrontDeskPos('RecordDeposit',{bookingId:request.id,jobId:request.jobId});}

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
    const dialog=studioDialog('Job Flow — '+job.title,`<div data-frontdesk-workflow></div><div class="frontdesk-flow">${stageNames.map((stage,index)=>`<button type="button" class="frontdesk-flow-stage ${index<current?'complete':index===current?'current':'future'}" data-flow-stage-name="${esc(stage)}"><small>${index<current?'Completed':index===current?'Current':'Upcoming'}</small><strong>${esc(stage)}</strong></button>`).join('<span>›</span>')}</div><div class="frontdesk-flow-history"><h3 data-flow-history-title>History</h3><div data-flow-history>${eventMarkup(events)}</div></div>`);
    dialog.querySelectorAll('[data-flow-stage-name]').forEach(button=>button.onclick=()=>{
        const stage=button.dataset.flowStageName;
        dialog.querySelector('[data-flow-history-title]').textContent=stage+' history';
        const related=events.filter(event=>[event.title,event.eventType,event.detail,event.description].filter(Boolean).join(' ').toLowerCase().includes(stage.toLowerCase()));
        dialog.querySelector('[data-flow-history]').innerHTML=eventMarkup(related);
    });
    const host=dialog.querySelector('[data-frontdesk-workflow]');
    dialog.addEventListener('close',()=>{if(host.__studioWorkflowClock)clearInterval(host.__studioWorkflowClock);},{once:true});
    try{await window.StudioWorkflow.render(host,jobId,()=>dialog.isConnected);}catch(e){host.textContent=e.message;}
    return dialog;
}

async function loadFrontDeskCommunications(){
    const host=document.getElementById('fdCommunicationFeed');
    if(!host)return;
    try{
        const [notificationResult,...announcementResults]=await Promise.allSettled([
            json('/api/notifications?count=80'),
            ...frontDeskWorkspaces.filter(w=>w.id).map(w=>json('/api/workspaces/'+encodeURIComponent(w.id)+'/announcements').then(items=>({workspace:w,items})))
        ]);
        if(!host.isConnected)return;
        const notifications=notificationResult.status==='fulfilled'&&Array.isArray(notificationResult.value)?notificationResult.value:[];
        const announcements=announcementResults.flatMap(result=>result.status==='fulfilled'&&Array.isArray(result.value.items)?result.value.items.map(a=>({...a,workspaceId:result.value.workspace.id,workspaceName:result.value.workspace.name})) :[]);
        const rows=[
            ...notifications.filter(n=>/message|conversation|reply/i.test(String(n.eventType||n.title||n.type||''))).map(item=>({kind:'message',item,at:item.createdAtUtc||item.createdAt||0})),
            ...announcements.map(item=>({kind:'announcement',item,at:item.createdAtUtc||item.createdAt||0}))
        ].sort((a,b)=>new Date(b.at)-new Date(a.at)).slice(0,6);
        host.innerHTML=rows.map(({kind,item:n})=>`<button type="button" class="smc-row" data-fd-message-job="${esc(kind==='message'&&n.referenceType==='Job'?(n.referenceId||''):'')}" data-fd-announcement-workspace="${esc(kind==='announcement'?(n.workspaceId||''):'')}"><span class="smc-type" aria-hidden="true">${kind==='announcement'?'📣':'✉'}</span><span class="smc-copy"><strong>${esc(n.subject||n.title||(kind==='announcement'?'Announcement':'Message'))}</strong><small>${esc((kind==='announcement'&&n.workspaceName?n.workspaceName+' · ':'')+(n.body||n.eventType||'Studio message'))}</small></span><span class="smc-time">${esc(n.createdAtUtc?new Date(n.createdAtUtc).toLocaleDateString():'')}</span><span class="mw-chevron" aria-hidden="true">›</span></button>`).join('')||'<div class="mw-empty">No recent messages or announcements.</div>';
        host.querySelectorAll('[data-fd-message-job]').forEach(button=>button.onclick=()=>{
            if(button.dataset.fdMessageJob)openWorkspaceJob(button.dataset.fdMessageJob);
            else studioNavigate('messages');
        });
    }catch(error){
        if(host.isConnected)host.innerHTML='<div class="mw-empty">Messages are temporarily unavailable. Use View All to open Messages.</div>';
    }
}
function openFrontDeskMessagePicker(){
    const jobs=frontDeskJobs.filter(job=>job?.id).sort((a,b)=>String(a.clientName||a.title||'').localeCompare(String(b.clientName||b.title||'')));
    const dialog=frontDeskDialog('Send Message',`<p>Select the Job conversation to message. Messages are retained with the Job.</p><label>Job / Customer<select data-job-picker name="jobId" required><option value="">Select a Job…</option>${StudioSubjectPicker.jobOptions(jobs)}</select></label><p class="muted">The message composer will open in a popup after you select a Job.</p>`);
    const form=dialog.querySelector('form');
    form.querySelector('[type=submit]').textContent='Continue';
    if(!jobs.length){form.querySelector('[role=alert]').textContent='No Jobs are available for messaging.';form.querySelector('[type=submit]').disabled=true;}
    form.onsubmit=async event=>{
        event.preventDefault();
        const jobId=form.elements.jobId.value;
        if(!jobs.some(job=>String(job.id)===jobId)){form.querySelector('[role=alert]').textContent='Select a Job.';return;}
        dialog.close();
        try{await messageFrontDeskJob(jobId,'workspace');}
        catch(error){const errorDialog=frontDeskDialog('Unable To Open Message',`<p>${esc(error.message||'Messaging is unavailable.')}</p>`);errorDialog.querySelector('[type=submit]').style.display='none';}
    };
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


async function recordFrontDeskFulfillment(jobId){
 const workflow=await json(`/api/jobs/${jobId}/connected-workflow`);
 const job=frontDeskJobs.find(j=>j.id===jobId),operationId=crypto.randomUUID();
 const dialog=frontDeskDialog('Confirm collection / delivery',`<p>${esc(job?.title||'Job')} · ${esc(job?.clientName||'')}</p><label>Method<select name="method"><option value="Collected">Customer collected</option><option value="Delivered">Delivered to customer</option></select></label><label>Received by<input name="recipient" required maxlength="200"></label>`);
 dialog.querySelector('form').onsubmit=async event=>{event.preventDefault();const form=event.currentTarget,button=form.querySelector('[type=submit]');button.disabled=true;try{await json(`/api/frontdesk/jobs/${jobId}/fulfillment`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({operationId,expectedRevision:workflow.data.revision,method:form.elements.method.value,recipient:form.elements.recipient.value})});dialog.close();await showFrontDesk();}catch(error){form.querySelector('[role=alert]').textContent=error.message;button.disabled=false;}};
}

