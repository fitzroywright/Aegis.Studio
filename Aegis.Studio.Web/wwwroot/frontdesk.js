let frontDeskJobs = [];
let frontDeskActionOwner='',frontDeskActionDue='',frontDeskActionError='';
const frontDeskNextRow=entry=>frontDeskActionError?null:window.StudioNextActions?.peek(entry.kind==='enquiry'?'enquiry':'job',entry.kind==='enquiry'?entry.id:entry.jobId);
let frontDeskDashboardWorkspace='';
let frontDeskRole = null;
let frontDeskMetricView = '';
let frontDeskColumnFilters = {type:'',status:'',source:''};
let frontDeskSelectedEntry='';
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
    return frontDeskRole;
}

async function showFrontDesk(options={}) {
    const loadRevision=++frontDeskLoadRevision;
    if(options.queue){frontDeskMetricView=({inquiries:'enquiries',deposits:'deposits',confirmation:'confirmation'})[options.queue]||'';frontDeskTab='today';frontDeskColumnFilters={type:'',status:'',source:''};}
    frontDeskDashboardWorkspace=String(options.workspaceId||'');
    if (!frontDeskRole) await loadFrontDeskRole();
    const me = await json('/api/auth/me');
    if(loadRevision!==frontDeskLoadRevision||document.body.dataset.studioView!=='frontdesk')return;
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
    const dayStart=new Date(frontDeskDate+'T00:00:00-05:00');
    const sources=[
        ['/api/jobs','jobs'],
        ['/api/frontdesk/booking-requests','bookings'],
        ['/api/frontdesk/enquiries','inquiries'],
        ['/api/frontdesk/workspaces','workspaces'],
        ['/api/frontdesk/clients','clients'],
        ['/api/schedule/entries?from='+encodeURIComponent(dayStart.toISOString())+'&to='+encodeURIComponent(new Date(dayStart.getTime()+86400000).toISOString()),'schedule'],
        ['/api/accounts/invoices','invoices'],
        ['/api/frontdesk/reception?from='+encodeURIComponent(dayStart.toISOString())+'&to='+encodeURIComponent(new Date(dayStart.getTime()+86400000).toISOString()),'reception'],
        ['/api/frontdesk/gallery-access-requests/unresolved','galleryRequests']
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
    if(loadRevision!==frontDeskLoadRevision||document.body.dataset.studioView!=='frontdesk')return;
    frontDeskReceptionAvailable=results[7].status==='fulfilled'&&Array.isArray(results[7].value);
    [frontDeskJobs,frontDeskRequests,frontDeskEnquiries,frontDeskWorkspaces,frontDeskClients,frontDeskSchedule,frontDeskInvoices,frontDeskReception,frontDeskGalleryRequests]=arrays;
    frontDeskDetails.clear();
    frontDeskActionError='';
    if(window.StudioNextActions){try{await StudioNextActions.load();}catch(error){frontDeskActionError='Next actions could not be loaded: '+error.message;}}
    if(loadRevision!==frontDeskLoadRevision||document.body.dataset.studioView!=='frontdesk')return;
    if(frontDeskDashboardWorkspace){
        const id=frontDeskDashboardWorkspace;
        frontDeskJobs=frontDeskJobs.filter(j=>String(j.workspaceId)===id||(j.participatingWorkspaceIds||[]).map(String).includes(id));
        const ids=new Set(frontDeskJobs.map(j=>String(j.id)));
        frontDeskRequests=frontDeskRequests.filter(r=>String(r.request?.workspaceId)===id||ids.has(String(r.jobId)));
        frontDeskEnquiries=frontDeskEnquiries.filter(e=>String(e.workspaceId)===id);
    }
    try{
        renderFrontDesk();

        if(errors.length){
            const notice=document.createElement('div');
            notice.className='fd-load-warning';
            notice.setAttribute('role','alert');
            notice.innerHTML='<strong>Some Front Desk information could not be loaded.</strong> '+errors.map(esc).join(' · ')+' <button type="button" class="action">Retry</button>';
            notice.querySelector('button').onclick=()=>showFrontDesk({workspaceId:frontDeskDashboardWorkspace});
            genericView.querySelector('.fd-reception')?.prepend(notice);
            console.warn('[Studio Front Desk] Partial API failures:',errors);
        }
    }catch(error){
        console.error('[Studio Front Desk] Render failure:',error);
        genericView.innerHTML=`<div class="fd-page"><h1>Front Desk</h1><p role="alert">Unable to render Front Desk: ${esc(error.message)}</p><button class="action" onclick="showFrontDesk()">Retry</button></div>`;
    }
}

let frontDeskRequests=[],frontDeskEnquiries=[],frontDeskWorkspaces=[],frontDeskClients=[],frontDeskSchedule=[],frontDeskInvoices=[],frontDeskGalleryRequests=[];
let frontDeskTab='all',frontDeskTerm='',frontDeskClientTerm='',frontDeskSelectedClientId=null;
function frontDeskRequestStage(item){return item.status==='Confirmed'?'Confirmed':item.status==='Cancelled'?'Cancelled':Number(item.recordedDeposit||0)>=Number(item.request?.requiredDeposit||0)?'Ready for confirmation':'Awaiting deposit';}
let frontDeskJobClickDelegationBound=false;
function bindFrontDeskJobClickDelegation(){
    if(frontDeskJobClickDelegationBound)return;
    frontDeskJobClickDelegationBound=true;
    document.addEventListener('click',event=>{
        const target=event.target.closest?.('[data-frontdesk-job]');
        if(!target||!genericView.contains(target))return;
        const jobId=target.dataset.frontdeskJob;
        if(!jobId)return;
        event.preventDefault();
        event.stopPropagation();
        openFrontDeskJob(jobId).catch(error=>alert(error.message||String(error)));
    },true);
}
function openFrontDeskJob(jobId){
    if(!jobId)return Promise.resolve();
    const id=String(jobId);
    // Switch to the Workspaces surface before opening the job. The job loader
    // ignores results unless that surface is active.
    if(typeof window.studioNavigate==='function')
        return Promise.resolve(window.studioNavigate('workspaces',{jobId:id}));
    if(typeof window.openWorkspaceJob==='function'){
        window.studioBeginJobNavigation?.(id,{history:true});
        return Promise.resolve(window.openWorkspaceJob(id,{navigationRecorded:true,history:false}));
    }
    return Promise.reject(new Error('Job navigation is unavailable.'));
}

let frontDeskDate=new Intl.DateTimeFormat('en-CA',{timeZone:'America/Jamaica'}).format(new Date());
let frontDeskLocation='',frontDeskArrivalFilter='',frontDeskAppointmentSort='time';
let frontDeskReception=[],frontDeskReceptionAvailable=false,frontDeskLoadRevision=0;
const frontDeskDetails=new Map();
const frontDeskArrivalStages=[['expected','Expected','i-people'],['arrived','Arrived','i-people'],['photographer','With photographer','i-camera'],['ready','Ready to leave','i-work']];
function frontDeskDateKey(value){if(!value)return '';const date=new Date(value);return Number.isNaN(+date)?'':new Intl.DateTimeFormat('en-CA',{timeZone:'America/Jamaica'}).format(date);}
function frontDeskLocationName(value){return ({BK:'Kings Plaza',BE:'East Queen Street'})[value]||value||'';}
function frontDeskArrival(entry){
 const state=frontDeskReception.find(row=>String(row.jobId)===String(entry.jobId));
 if(!frontDeskReceptionAvailable||!entry.jobId)return {key:'unknown',label:'Arrival not available'};
 if(state?.sessionCount>0&&state.completedSessions>=state.sessionCount)return {key:'ready',label:'Ready to leave'};
 if(state?.startedAtUtc)return {key:'photographer',label:'With photographer'};
 if(state?.arrivedAtUtc)return {key:'arrived',label:'Arrived',at:state.arrivedAtUtc};
 return {key:'expected',label:'Expected'};
}
function frontDeskEntries(all=false){
 const knownJobs=new Map(frontDeskJobs.map(job=>[String(job.id),job]));
 const daySchedule=frontDeskSchedule.filter(s=>s.status!=='Cancelled'&&frontDeskDateKey(s.startsAtUtc)===frontDeskDate);
 const bookingJobs=new Set(frontDeskRequests.filter(b=>b.jobId).map(b=>String(b.jobId)));
 const requests=frontDeskRequests.filter(b=>b&&b.status!=='Cancelled'&&!['Completed','Closed','Cancelled','Archived'].includes(knownJobs.get(String(b.jobId))?.status)).flatMap(booking=>{
  const r=booking.request||{},job=knownJobs.get(String(booking.jobId)),schedule=daySchedule.filter(s=>String(s.jobId)===String(booking.jobId));
  if(!all&&!frontDeskMetricView&&!schedule.length&&(r.eventDate||frontDeskDateKey(r.startsAtUtc))!==frontDeskDate)return [];
  return (schedule.length?schedule:[null]).map(slot=>({kind:'request',id:booking.id+(slot?':'+slot.id:''),jobId:booking.jobId,job,request:booking,name:r.clientName||job?.clientName||'Client',title:r.title||job?.title||'Booking',date:r.eventDate,startsAtUtc:slot?.startsAtUtc||r.startsAtUtc,endsAtUtc:slot?.endsAtUtc||r.endsAtUtc,location:frontDeskLocationName(r.location||slot?.location),reference:booking.intakeReference||r.intakeReference||'',conversationId:r.conversationId}));
 });
 const jobs=frontDeskJobs.filter(job=>!bookingJobs.has(String(job.id))&&!['Cancelled','Archived','Completed','Closed'].includes(job.status)).flatMap(job=>{
  const schedule=daySchedule.filter(s=>String(s.jobId)===String(job.id));
  return (schedule.length?schedule:all||job.eventDate===frontDeskDate?[null]:[]).map(slot=>({kind:'job',id:'job:'+job.id+(slot?':'+slot.id:''),jobId:job.id,job,name:job.clientName||'Client',title:job.title,date:job.eventDate,startsAtUtc:slot?.startsAtUtc,endsAtUtc:slot?.endsAtUtc,location:frontDeskLocationName(slot?.location),reference:job.intakeReference||'',conversationId:job.conversationId}));
 });
 return [...requests,...jobs];
}
function frontDeskWaiting(entry){const at=frontDeskArrival(entry).at;return at?'Waiting '+Math.max(0,Math.floor((Date.now()-new Date(at))/60000))+' min':'';}
function renderFrontDesk(selectionOnly=false){
 if(!['all','today','enquiries','sales'].includes(frontDeskTab))frontDeskTab='today';
 const search=genericView.querySelector('#frontDeskSearch'),focused=search===document.activeElement,range=search?[search.selectionStart,search.selectionEnd]:null;
 const enquiries=frontDeskEnquiries.filter(e=>e&&!frontDeskRequests.some(b=>b.request?.conversationId===e.id)).map(e=>({kind:'enquiry',id:e.id,name:e.contactName||'New inquiry',title:e.subject||'Inquiry',conversationId:e.id,reference:e.intakeReference||'',contact:e.contactAddress,created:e.createdAtUtc,source:e.channel,enquiry:e}));
 const appointments=frontDeskEntries(frontDeskTab==='all');
 const locations=[...new Set([...frontDeskRequests.map(b=>frontDeskLocationName(b.request?.location)),...frontDeskSchedule.map(s=>frontDeskLocationName(s.location))].filter(Boolean))].sort();
 const locationEntries=appointments.filter(e=>!frontDeskLocation||e.location===frontDeskLocation);
 const counts=Object.fromEntries(frontDeskArrivalStages.map(([key])=>[key,locationEntries.filter(e=>frontDeskArrival(e).key===key).length]));
 let entries=frontDeskTab==='all'?[...locationEntries,...(!frontDeskLocation?enquiries:[])]:frontDeskTab==='enquiries'?enquiries:frontDeskTab==='sales'?frontDeskJobs.map(job=>({kind:'job',id:'job:'+job.id,jobId:job.id,job,name:job.clientName||'Client',title:job.title,reference:job.intakeReference||'',date:job.eventDate})):locationEntries;
 if(frontDeskMetricView==='enquiries'){frontDeskTab='enquiries';entries=enquiries;}
 if(frontDeskMetricView==='deposits')entries=entries.filter(e=>e.request&&!['Confirmed','Cancelled'].includes(e.request.status)&&Number(e.request.recordedDeposit||0)<Number(e.request.request?.requiredDeposit||0));
 if(frontDeskMetricView==='confirmation')entries=entries.filter(e=>e.request&&!['Confirmed','Cancelled'].includes(e.request.status));
 entries=entries.filter(e=>(!frontDeskArrivalFilter||frontDeskTab!=='today'||frontDeskArrival(e).key===frontDeskArrivalFilter)&&[e.name,e.title,e.reference,e.location,e.contact,e.request?.request?.clientPhone,e.request?.request?.clientEmail,frontDeskClients.find(c=>c.id===(e.job?.clientId||e.request?.request?.clientId))?.phone].join(' ').toLowerCase().includes(frontDeskTerm.toLowerCase()));
 entries=entries.filter(entry=>{const action=frontDeskNextRow(entry),due=window.StudioNextActions?.due(action);return (!frontDeskActionOwner||(frontDeskActionOwner==='unassigned'?!action?.assignedTo:action?.assignedTo===frontDeskActionOwner))&&(!frontDeskActionDue||(frontDeskActionDue==='overdue'?due?.overdue:frontDeskActionDue==='today'?action?.dueAtUtc&&frontDeskDateKey(action.dueAtUtc)===frontDeskDateKey(new Date()):!action?.dueAtUtc));});
 entries.sort((a,b)=>frontDeskAppointmentSort==='name'?a.name.localeCompare(b.name):String(a.startsAtUtc||a.created||a.date||'9999').localeCompare(String(b.startsAtUtc||b.created||b.date||'9999')));
 if(!entries.some(e=>String(e.id)===String(frontDeskSelectedEntry)))frontDeskSelectedEntry=(entries.find(e=>String(e.request?.id)===String(frontDeskSelectedEntry))||entries[0])?.id||'';
 const selected=entries.find(e=>String(e.id)===String(frontDeskSelectedEntry));
 const detail=selected?frontDeskSelectedMarkup(selected):'<section class="fd-selected-request fd-empty"><h2>'+(frontDeskTab==='enquiries'?'No inquiries':'No appointments')+'</h2><p>Choose another date, location or filter.</p></section>';
 if(selectionOnly&&genericView.querySelector('.fd-selected-request')){
  genericView.querySelectorAll('[data-fd-row-open]').forEach(row=>row.setAttribute('aria-selected',String(row.dataset.fdRowOpen===String(frontDeskSelectedEntry))));
  genericView.querySelector('.fd-selected-request').outerHTML=detail;bindFrontDeskSelection(selected);return;
 }
 const time=value=>value?new Date(value).toLocaleTimeString([],{timeZone:'America/Jamaica',hour:'2-digit',minute:'2-digit'}):'—';
 const rows=entries.map(e=>{const stage=frontDeskArrival(e),next=frontDeskNextRow(e),due=window.StudioNextActions?.due(next),deposit=e.request&&!['Confirmed','Cancelled'].includes(e.request.status)&&Number(e.request.recordedDeposit||0)<Number(e.request.request?.requiredDeposit||0);return `<tr class="fd-appointment-row studio-speedbump-row" data-fd-row-open="${esc(e.id)}" tabindex="0" aria-selected="${e.id===frontDeskSelectedEntry}"><td>${esc(e.reference||'—')}</td><td class="fd-appointment-copy"><strong>${esc(e.name)}</strong><span>${esc(e.title)}</span>${e.startsAtUtc?'<small>'+esc(time(e.startsAtUtc))+'</small>':''}</td><td><span class="fd-arrival-pill">${esc(e.kind==='enquiry'?'Enquiry':e.job?.status||(e.request?frontDeskRequestStage(e.request):'Job'))}</span>${frontDeskTab==='today'?'<small>'+esc(stage.label)+'</small>':''}${deposit?'<small class="fd-deposit-alert">! Deposit needed</small>':''}</td><td data-fd-next="${esc(e.id)}">${next?'<button type="button" class="studio-next-link" data-fd-next-open>'+esc(next.action.title)+' →</button><p class="studio-next-reason">'+esc(next.action.reason)+'</p>':'<span>'+esc(frontDeskActionError?'Next action unavailable':'No outstanding action')+'</span>'}</td><td>${esc(next?.assignedTo||'Unassigned')}</td><td class="${due?.overdue?'fd-due-overdue':''}">${esc(due?.label||'No due date')}</td></tr>`;}).join('');
 const actionOwners=[...new Set([...frontDeskJobs.map(j=>window.StudioNextActions?.peek('job',j.id)?.assignedTo),...frontDeskEnquiries.map(e=>window.StudioNextActions?.peek('enquiry',e.id)?.assignedTo)].filter(Boolean))].sort();
 const heading=frontDeskTab==='all'?'Jobs and enquiries':frontDeskTab==='enquiries'?'Open inquiries':frontDeskTab==='sales'?'Sales & payments':frontDeskMetricView==='deposits'?'Deposits needed':frontDeskMetricView==='confirmation'?'Needs confirmation':frontDeskDate===frontDeskDateKey(new Date())?"Today's appointments":'Appointments';
 genericView.innerHTML=`<section class="fd-reception${frontDeskTab==='all'?' fd-all-work':''}" aria-label="Front Desk"><div class="fd-topline"><label class="fd-location"><span class="sr-only">Studio location</span><select data-fd-location aria-label="Studio location"><option value="">All locations</option>${locations.map(location=>`<option ${location===frontDeskLocation?'selected':''}>${esc(location)}</option>`).join('')}</select></label><label class="fd-date" ${frontDeskTab==='all'?'hidden':''}><span class="sr-only">Appointment date</span><span aria-hidden="true">◷ ${esc(new Date(frontDeskDate+'T12:00:00-05:00').toLocaleDateString([],{timeZone:'America/Jamaica',weekday:'long',month:'long',day:'numeric'}))}</span><input class="fd-calendar-native" type="date" data-fd-date aria-label="Appointment date" value="${esc(frontDeskDate)}"></label><input id="frontDeskSearch" type="search" aria-label="Find client, phone or booking" placeholder="Find client, phone or booking" value="${esc(frontDeskTerm)}"><div class="fd-new-actions"><button type="button" class="action" data-fd-new-enquiry>＋ Enquiry</button><button type="button" class="primary" data-fd-new-booking>＋ New booking</button></div></div><nav class="fd-work-tabs" aria-label="Front Desk tasks">${[['all','All'],['today','Appointments'],['enquiries','Enquiries '+enquiries.length]].map(([key,label])=>`<button type="button" data-fd-tab="${key}" aria-current="${frontDeskTab===key?'page':'false'}">${label}</button>`).join('')}</nav><div class="fd-arrival-strip" ${frontDeskTab==='all'?'hidden':''} role="group" aria-label="Arrival stages">${frontDeskArrivalStages.map(([key,label,icon])=>`<button type="button" data-fd-arrival="${key}" class="fd-tone-${key}" aria-pressed="${frontDeskArrivalFilter===key}"><svg class="fd-stage-icon" aria-hidden="true"><use href="#${icon}"></use></svg><span class="fd-stage-dot">●</span><span>${label}</span><strong>${counts[key]}</strong></button>`).join('')}<button type="button" data-fd-arrival="" aria-pressed="${!frontDeskArrivalFilter}">All <strong>${locationEntries.length}</strong></button></div>${!frontDeskReceptionAvailable?'<p class="fd-warning" role="status">Arrival status is unavailable. Booking and customer details remain accessible.</p>':''}<div class="fd-action-filters"><label>Responsible <select data-fd-action-owner><option value="">All staff</option><option value="unassigned" ${frontDeskActionOwner==='unassigned'?'selected':''}>Unassigned</option>${actionOwners.map(owner=>`<option value="${esc(owner)}" ${owner===frontDeskActionOwner?'selected':''}>${esc(owner)}</option>`).join('')}</select></label><label>Due <select data-fd-action-due><option value="">Any time</option>${[['overdue','Overdue'],['today','Today'],['none','No due date']].map(([key,label])=>`<option value="${key}" ${frontDeskActionDue===key?'selected':''}>${label}</option>`).join('')}</select></label>${frontDeskActionError?`<p role="alert">${esc(frontDeskActionError)} <button type="button" data-fd-next-retry>Retry</button></p>`:''}</div><div class="fd-service-desk"><section class="fd-appointment-list"><header><h2>${heading}</h2><select data-fd-order aria-label="Appointment order"><option value="time" ${frontDeskAppointmentSort==='time'?'selected':''}>Time order</option><option value="name" ${frontDeskAppointmentSort==='name'?'selected':''}>Client name</option></select></header><div class="fd-appointment-scroll"><table class="fd-next-table"><thead><tr><th>Reference</th><th>Client / enquiry</th><th>Status</th><th>Next required action</th><th>Responsible</th><th>Due</th></tr></thead><tbody>${rows||'<tr><td colspan="6">No matching records.</td></tr>'}</tbody></table></div></section>${frontDeskTab==='all'?'':detail}</div><footer class="fd-grid-actions" role="toolbar" aria-label="Front Desk actions"><button type="button" data-fd-new-client>♧ New customer</button><button type="button" data-fd-financial-action="NewSale">＋ New sale</button><button type="button" data-fd-financial-action="RecordDeposit">▱ Record deposit</button><button type="button" data-fd-financial-action="IssueRefund">↶ Issue refund</button><button type="button" data-fd-financial-action="ViewReceipt">☷ Completed transactions</button><details class="fd-more-actions"><summary>More</summary><button type="button" data-fd-financial-action="TakePayment">Take payment</button><button type="button" data-fd-close-register>Close register</button></details></footer></section>`;
 if(search){genericView.querySelector('#frontDeskSearch').replaceWith(search);if(focused){search.focus({preventScroll:true});if(range[0]!==null)search.setSelectionRange(...range);}}
 genericView.querySelector('#frontDeskSearch').oninput=e=>{frontDeskTerm=e.target.value;renderFrontDesk();};
 genericView.querySelector('[data-fd-action-owner]').onchange=e=>{frontDeskActionOwner=e.target.value;renderFrontDesk();};
 genericView.querySelector('[data-fd-action-due]').onchange=e=>{frontDeskActionDue=e.target.value;renderFrontDesk();};
 genericView.querySelector('[data-fd-next-retry]')?.addEventListener('click',()=>showFrontDesk({workspaceId:frontDeskDashboardWorkspace}));
 genericView.querySelector('[data-fd-location]').onchange=e=>{frontDeskLocation=e.target.value;renderFrontDesk();};
 genericView.querySelector('[data-fd-date]').onchange=e=>{if(!e.target.value)return;frontDeskDate=e.target.value;frontDeskSelectedEntry='';frontDeskDetails.clear();showFrontDesk({workspaceId:frontDeskDashboardWorkspace}).catch(error=>alert(error.message));};
 genericView.querySelector('[data-fd-order]').onchange=e=>{frontDeskAppointmentSort=e.target.value;renderFrontDesk();};
 genericView.querySelector('[data-fd-new-enquiry]').onclick=()=>document.getElementById('newRequestButton')?.click();
 genericView.querySelector('[data-fd-new-booking]').onclick=()=>window.studioLaunchNewBooking();
 genericView.querySelector('[data-fd-new-client]').onclick=()=>showFrontDeskClients(true).catch(error=>alert(error.message));
 genericView.querySelector('[data-fd-close-register]').onclick=()=>launchFrontDeskPos('CloseRegister').catch(error=>alert(error.message));
 genericView.querySelectorAll('[data-fd-financial-action]').forEach(b=>b.onclick=()=>pickFrontDeskFinancialAction(b.dataset.fdFinancialAction).catch(error=>alert(error.message)));
 genericView.querySelectorAll('[data-fd-arrival]').forEach(b=>b.onclick=()=>{frontDeskArrivalFilter=b.dataset.fdArrival;frontDeskMetricView='';frontDeskTab='today';renderFrontDesk();});
 genericView.querySelectorAll('[data-fd-tab]').forEach(b=>b.onclick=()=>{frontDeskTab=b.dataset.fdTab;frontDeskMetricView='';frontDeskArrivalFilter='';renderFrontDesk();});
 genericView.querySelectorAll('[data-fd-row-open]').forEach(row=>{const entry=entries.find(e=>String(e.id)===row.dataset.fdRowOpen);const open=()=>{frontDeskSelectedEntry=row.dataset.fdRowOpen;if(entry?.kind==='enquiry'&&window.StudioNextActions)StudioNextActions.openEnquiry(entry.id).catch(error=>alert(error.message));else if(frontDeskTab==='all'&&entry?.jobId)openFrontDeskJob(entry.jobId).catch(error=>alert(error.message));else renderFrontDesk(true);};row.onclick=event=>{if(!event.target.closest('button'))open();};row.onkeydown=event=>{if(event.target===row&&['Enter',' '].includes(event.key)){event.preventDefault();open();}};row.ondblclick=()=>{if(entry?.jobId&&frontDeskTab!=='all')openFrontDeskJob(entry.jobId).catch(error=>alert(error.message));};const button=row.querySelector('[data-fd-next-open]');if(button)button.onclick=event=>{event.stopPropagation();StudioNextActions.activate(frontDeskNextRow(entry)).catch(error=>alert(error.message));};});
 const count=document.getElementById('studioFooterRate');if(count)count.textContent=entries.length+' '+heading;
 bindFrontDeskJobClickDelegation();bindFrontDeskSelection(selected);
}
function frontDeskSelectedMarkup(entry){
 const detail=frontDeskDetails.get(String(entry.jobId)),job=detail?.job||entry.job||{},request=entry.request?.request||{};
 const client=frontDeskClients.find(c=>String(c.id)===String(job.clientId||request.clientId));
 const arrival=frontDeskArrival(entry),stageIndex=frontDeskArrivalStages.findIndex(([key])=>key===arrival.key);
 const events=job.financialEvents||[],financial=studioFinancials(job),quoted=request.priceSnapshot?.priceMinor;
 const ledgerPriced=events.some(event=>/^(charge|adjustment|discount|POS Sale|POS Refund)$/i.test(event.kind));
 const totalKnown=ledgerPriced||typeof quoted==='number';
 const total=Math.max(0,ledgerPriced?financial.charges:Number(quoted||0)/100),paid=events.length?financial.received:Number(entry.request?.recordedDeposit||0),balance=Math.max(0,total-paid);
 const snapshot=detail?.view?.data||{},sessions=snapshot.sessions||[],crew=[...new Set(sessions.flatMap(s=>(s.crew||[]).map(c=>c.display_name||c.user_name)).filter(Boolean))];
 const initials=entry.name.split(/\s+/).slice(0,2).map(part=>part[0]).join('');
 const jobCount=client?frontDeskJobs.filter(j=>j.clientId===client.id).length:0;
 const notes=request.notes||snapshot.notes||'';
 const subjects=Array.isArray(detail?.subjects)?detail.subjects.filter(person=>!person.isSystem):null;
 const canReport=!!detail?.view&&(detail.view.canManage||sessions.some(session=>(session.crew||[]).some(person=>String(person.user_name).toLowerCase()===String(detail.view.userName).toLowerCase())));
 const audit=detail?.audit||[];
 const start=entry.startsAtUtc?new Date(entry.startsAtUtc).toLocaleTimeString([],{timeZone:'America/Jamaica',hour:'2-digit',minute:'2-digit'}):'Time not supplied';
 const pending=entry.request&&!['Confirmed','Cancelled'].includes(entry.request.status);
 const mainAction=entry.kind==='enquiry'?'<button type="button" class="primary" data-fd-prepare>Prepare booking</button>':pending?'<button type="button" class="primary" data-fd-confirm>Confirm booking</button>':arrival.key==='expected'&&job.status==='Scheduled'?'<button type="button" class="primary" data-fd-check-in>✓ Check in</button>':arrival.key==='arrived'?'<button type="button" class="primary" data-fd-start data-fd-can-report="'+canReport+'">'+(canReport?'▶ Start session':'Session status')+'</button>':arrival.key==='photographer'?'<button type="button" class="primary" data-fd-start>Session status</button>':arrival.key==='ready'?'<button type="button" class="primary" data-fd-payment>Take payment</button>':'';
 return `<section class="fd-selected-request" aria-label="Selected customer"><header class="fd-customer-heading"><span class="fd-initials">${esc(initials)}</span><div><h2>${esc(client?.displayName||entry.name)} ${jobCount>1?'<small class="fd-returning">Returning client</small>':''}</h2><p>${esc(client?.phone||request.clientPhone||'')}${(client?.phone||request.clientPhone)&&(client?.email||request.clientEmail)?' · ':''}${esc(client?.email||request.clientEmail||entry.contact||'')}</p></div>${client?'<button type="button" class="fd-text-action" data-fd-edit-client>✎ Edit customer</button>':''}</header>${entry.kind==='enquiry'?'':`<ol class="fd-arrival-progress" aria-label="Customer arrival progress">${frontDeskArrivalStages.map(([key,label],i)=>`<li class="fd-tone-${key}" ${i===stageIndex?'aria-current="step"':''} data-complete="${i<stageIndex}"><span>${i<stageIndex?'✓':'●'}</span>${label}${i===stageIndex&&arrival.key==='arrived'?'<small>'+esc(frontDeskWaiting(entry))+'</small>':''}</li>`).join('')}</ol>`}<div data-fd-selected-next></div><section class="fd-booking-card"><div class="fd-session-heading"><div><h3>${esc(entry.title)}</h3><span class="fd-arrival-pill">${esc(entry.kind==='enquiry'?'New inquiry':entry.request?frontDeskRequestStage(entry.request):job.status||'Booking')}</span><p>${esc(entry.reference||'Reference not supplied')}</p></div><div><p>◷ ${esc(entry.date||'Requested date not supplied')}</p><p>${esc(start)}</p><p>⌖ ${esc(entry.location||'Location not supplied')}</p></div></div>${entry.kind==='enquiry'?'':`<dl class="fd-money"><div><dt>Total</dt><dd>${totalKnown?esc(money(total)):'Not priced'}</dd></div><div><dt>Paid</dt><dd>${esc(money(paid))}</dd></div><div><dt>Balance</dt><dd>${totalKnown?esc(money(balance)):'Not priced'}</dd></div></dl><progress class="fd-payment-progress" max="${total||1}" value="${Math.min(paid,total||0)}" aria-label="Payment progress"></progress>`}<div class="fd-customer-actions">${mainAction}${entry.jobId&&arrival.key!=='ready'?'<button type="button" class="action" data-fd-payment>▱ Take payment</button>':''}${entry.jobId?'<button type="button" class="action" data-fd-open-selected>◉ View job</button>':''}${pending?'<button type="button" class="action" data-fd-deposit>Record deposit</button><button type="button" class="fd-text-action" data-fd-deposit-request>Send deposit request</button><button type="button" class="fd-text-action" data-fd-deposit-delivery>Delivery status</button>':''}</div>${entry.jobId?`<dl class="fd-session-facts"><div><dt>Photographer</dt><dd>${esc(crew.join(', ')||'Not assigned')}</dd></div><div><dt>Job owner</dt><dd>${esc(snapshot.owner||job.jobOwner||job.jobLead||'Not assigned')}</dd></div><div><dt>Subjects</dt><dd>${subjects?esc((subjects.slice(0,2).map(person=>person.name).join(', ')||'None recorded')+(subjects.length>2?' +'+(subjects.length-2):'')):'Open session plan'}</dd></div><div><dt>Session plan</dt><dd><button type="button" class="fd-text-action" data-fd-session-plan>${subjects?subjects.length+' subject'+(subjects.length===1?'':'s'):'View session plan'} →</button></dd></div></dl>`:''}</section><div class="fd-customer-bottom"><section><h3>Recent activity</h3>${detail?.error?'<p role="alert">'+esc(detail.error)+'</p><button type="button" class="fd-text-action" data-fd-detail-retry>Retry details</button>':detail?.loading?'<p role="status">Loading job details…</p>':audit.length?'<ol class="fd-activity">'+audit.slice(0,4).map(a=>'<li><time>'+esc(new Date(a.createdAtUtc).toLocaleString([],{timeZone:'America/Jamaica',month:'short',day:'numeric',hour:'2-digit',minute:'2-digit'}))+'</time><span>'+esc(a.detail||a.action)+'</span></li>').join('')+'</ol>':'<p>No recorded activity.</p>'}</section><section><h3>Notes</h3><p class="fd-customer-note">${esc(notes||'No notes recorded.')}</p>${entry.conversationId||request.conversationId?'<button type="button" class="fd-text-action" data-fd-intake>Intake / email status</button>':''}</section></div><p role="alert" data-fd-selection-error></p></section>`;
}
function bindFrontDeskSelection(entry){
 if(!entry)return;const host=genericView.querySelector('.fd-selected-request');if(!host)return;
 if(window.StudioNextActions)StudioNextActions.mount(host.querySelector('[data-fd-selected-next]'),frontDeskNextRow(entry),{compact:true});
 const fail=error=>{if(host.isConnected)host.querySelector('[data-fd-selection-error]').textContent=error.message||String(error);};
 host.querySelector('[data-fd-open-selected]')?.addEventListener('click',()=>openFrontDeskJob(entry.jobId).catch(fail));
 host.querySelector('[data-fd-prepare]')?.addEventListener('click',()=>openPendingBookingForm(frontDeskEnquiries.find(e=>e.id===entry.id)).catch(fail));
 host.querySelector('[data-fd-payment]')?.addEventListener('click',()=>launchFrontDeskPos('TakePayment',{jobId:entry.jobId}).catch(fail));
 host.querySelector('[data-fd-deposit]')?.addEventListener('click',()=>openVerifiedDepositForm(entry.request).catch(fail));
 host.querySelector('[data-fd-session-plan]')?.addEventListener('click',()=>studioNavigate('session-plan',{jobId:entry.jobId}).catch(fail));
 host.querySelector('[data-fd-edit-client]')?.addEventListener('click',()=>showFrontDeskClients(false,(entry.job||frontDeskDetails.get(String(entry.jobId))?.job)?.clientId||entry.request?.request?.clientId).catch(fail));
 host.querySelector('[data-fd-start]')?.addEventListener('click',async event=>{try{const dialog=await StudioWorkflow.open(entry.jobId,event.currentTarget.dataset.fdCanReport==='true'?'[data-wf-report]':null);dialog?.addEventListener('close',()=>{frontDeskDetails.delete(String(entry.jobId));showFrontDesk({workspaceId:frontDeskDashboardWorkspace}).catch(fail);},{once:true});}catch(error){fail(error);}});
 host.querySelector('[data-fd-intake]')?.addEventListener('click',()=>openIntakeDeliveryStatus(entry.conversationId||entry.request?.request?.conversationId).catch(fail));
 host.querySelector('[data-fd-check-in]')?.addEventListener('click',async event=>{const button=event.currentTarget;button.disabled=true;try{await json('/api/frontdesk/jobs/'+encodeURIComponent(entry.jobId)+'/check-in',{method:'POST'});frontDeskDetails.delete(String(entry.jobId));await showFrontDesk({workspaceId:frontDeskDashboardWorkspace});}catch(error){fail(error);button.disabled=false;}});
 host.querySelector('[data-fd-confirm]')?.addEventListener('click',async event=>{const button=event.currentTarget;button.disabled=true;try{if(!entry.jobId)throw Error('This request has no linked job.');const workflow=await json('/api/jobs/'+entry.jobId+'/workflow-graph');await json('/api/jobs/'+entry.jobId+'/workflow-graph/actions',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'confirm-booking',expectedRevision:workflow.revision,operationId:crypto.randomUUID(),reason:''})});frontDeskDetails.delete(String(entry.jobId));await showFrontDesk({workspaceId:frontDeskDashboardWorkspace});}catch(error){fail(error);button.disabled=false;}});
 host.querySelector('[data-fd-deposit-request]')?.addEventListener('click',async event=>{const button=event.currentTarget;button.disabled=true;try{const channels=await json('/api/frontdesk/deposit-delivery-channels');if(!channels.length)throw Error('No deposit delivery channel is configured.');const channel=channels.length===1?channels[0]:prompt('Delivery channel: '+channels.join(' or '),channels[0]);if(!channel)return;if(!channels.includes(channel))throw Error('Choose a configured channel.');await json('/api/frontdesk/booking-requests/'+entry.request.id+'/deposit-requested?channel='+encodeURIComponent(channel),{method:'POST'});fail(Error('Deposit request queued. Check delivery status for acceptance.'));}catch(error){fail(error);}finally{button.disabled=false;}});
 host.querySelector('[data-fd-deposit-delivery]')?.addEventListener('click',async()=>{try{const value=await json('/api/frontdesk/booking-requests/'+entry.request.id+'/deposit-delivery');fail(Error((value.channel||'Delivery')+': '+value.state+(value.lastError?' · '+value.lastError:'')));}catch(error){fail(error);}});
 host.querySelector('[data-fd-detail-retry]')?.addEventListener('click',()=>{frontDeskDetails.delete(String(entry.jobId));loadFrontDeskSelectedDetails(entry);});
 if(entry.jobId&&!frontDeskDetails.has(String(entry.jobId)))loadFrontDeskSelectedDetails(entry);
}
async function loadFrontDeskSelectedDetails(entry){
 const key=String(entry.jobId),state={loading:true};frontDeskDetails.set(key,state);
 if(document.body.dataset.studioView==='frontdesk'&&String(frontDeskSelectedEntry)===String(entry.id)&&genericView.querySelector('.fd-selected-request')){genericView.querySelector('.fd-selected-request').outerHTML=frontDeskSelectedMarkup(entry);bindFrontDeskSelection(entry);}
 const results=await Promise.allSettled([json('/api/jobs/'+key),json('/api/jobs/'+key+'/connected-workflow'),json('/api/jobs/'+key+'/operations/audit'),json('/api/jobs/'+key+'/session-plan')]);
 if(frontDeskDetails.get(key)!==state)return;
 state.loading=false;const names=['job','view','audit','subjects'];results.forEach((result,i)=>{if(result.status==='fulfilled')state[names[i]]=result.value;else state.error='Some job details could not be loaded.';});
 if(!Array.isArray(state.audit))state.audit=[];
 if(document.body.dataset.studioView==='frontdesk'&&String(frontDeskSelectedEntry)===String(entry.id)&&genericView.querySelector('.fd-selected-request')){genericView.querySelector('.fd-selected-request').outerHTML=frontDeskSelectedMarkup(entry);bindFrontDeskSelection(entry);}
}

async function openFrontDeskInquiry(){
    const d=frontDeskDialog('New Inquiry',`<label>Workspace<select name="workspace" required>${frontDeskWorkspaces.map(w=>`<option value="${esc(w.id)}">${esc(w.name)}</option>`).join('')}</select></label><label>Client name<input name="name" required maxlength="200"></label><label>Contact email<input name="email" type="email" required maxlength="320"></label><label>Subject<input name="subject" required maxlength="200"></label><label>Inquiry<textarea name="body" required maxlength="10000"></textarea></label>`);
    d.querySelector('form').onsubmit=async e=>{e.preventDefault();const f=e.currentTarget,b=f.querySelector('[type=submit]');b.disabled=true;try{await json('/api/interactions',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({workspaceId:f.elements.workspace.value,destination:null,contactName:f.elements.name.value,contactAddress:f.elements.email.value,subject:f.elements.subject.value,body:f.elements.body.value,channel:'WalkIn'})});d.close();await showFrontDesk();}catch(e){f.querySelector('[role=alert]').textContent=e.message;b.disabled=false;}};
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
    host.querySelectorAll('[data-confirm-booking]').forEach(b=>b.onclick=async()=>{b.disabled=true;try{const v=await json('/api/jobs/'+b.dataset.confirmBooking+'/workflow-graph');const reason='';if(reason===null){b.disabled=false;return;}await json('/api/jobs/'+b.dataset.confirmBooking+'/workflow-graph/actions',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'confirm-booking',expectedRevision:v.revision,operationId:crypto.randomUUID(),reason})});await showFrontDesk();}catch(error){alert(error.message);b.disabled=false;}});
    host.querySelectorAll('[data-frontdesk-job]').forEach(b=>b.onclick=()=>openFrontDeskJob(b.dataset.frontdeskJob));
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
async function reviewFrontDeskGalleryRequest(conversationId){
    const request=frontDeskGalleryRequests.find(x=>x.conversationId===conversationId);
    if(!request){alert('Gallery request was not found in the unresolved queue. Refresh Front Desk.');return;}
    const details=request.request||{},matches=new Set((request.candidateClients||[]).map(c=>c.id));
    const ordered=[...frontDeskClients].sort((a,b)=>Number(matches.has(b.id))-Number(matches.has(a.id))||String(a.displayName).localeCompare(String(b.displayName)));
    const dialog=frontDeskDialog('Identify Client / Gallery Request',`
      <p><strong>${esc(details.clientName||'Client')}</strong> · ${esc(details.clientEmail||'')} · ${esc(details.telephone||'')}</p>
      <p>Booking Reference: ${esc(details.bookingReference||'Not supplied')}</p>
      <p>${esc(details.notes||'')}</p>
      <label>Studio Client (verify identity independently)<select name="clientId" required>
        <option value="">Choose verified client</option>
        ${ordered.map(c=>`<option value="${c.id}" ${c.id===request.clientId?'selected':''}>${matches.has(c.id)?'Possible match · ':''}${esc(c.displayName)} · ${esc(c.email||c.phone||'No contact')}</option>`).join('')}
      </select></label>
      <label>Available publication<select name="publicationId"><option value="">Not ready — Awaiting Production</option></select></label>
      <label>Verification note<textarea name="note" maxlength="1000" placeholder="How was the client identity verified?"></textarea></label>
      <label><input name="verified" type="checkbox" required> I have independently verified this client identity.</label>
      ${request.status==='ReadyToPublish'?'<button type="button" class="action" data-gallery-publish>Publish linked gallery</button>':''}
    `);
    const form=dialog.querySelector('form'),client=form.elements.clientId,publication=form.elements.publicationId;
    async function loadPublications(){
      publication.replaceChildren(new Option('Not ready — Awaiting Production',''));
      if(!client.value)return;
      try{
        const available=await json('/api/frontdesk/gallery-publications?clientId='+encodeURIComponent(client.value));
        for(const p of available)publication.add(new Option(p.title+' · '+p.state,p.id));
        publication.value=request.clientId===client.value?(request.publicationId||''):'';
      }catch(error){form.querySelector('[role=alert]').textContent=error.message;}
    }
    client.onchange=loadPublications;await loadPublications();
    form.querySelector('[data-gallery-publish]')?.addEventListener('click',async event=>{
      event.currentTarget.disabled=true;
      try{
        await json('/api/frontdesk/gallery-access-requests/'+encodeURIComponent(conversationId)+'/publish',{method:'POST'});
        dialog.close();await showFrontDesk();
      }catch(error){form.querySelector('[role=alert]').textContent=error.message;event.currentTarget.disabled=false;}
    });
    form.onsubmit=async event=>{
      event.preventDefault();
      const button=form.querySelector('[type=submit]');button.disabled=true;
      try{
        await json('/api/frontdesk/gallery-access-requests/'+encodeURIComponent(conversationId)+'/resolve',{
          method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({
            clientId:client.value,publicationId:publication.value||null,note:form.elements.note.value||null
          })
        });
        dialog.close();await showFrontDesk();
      }catch(error){form.querySelector('[role=alert]').textContent=error.message;button.disabled=false;}
    };
}
function frontDeskDialog(title,contents){
    const dialog=document.createElement('dialog');dialog.className='frontdesk-dialog';dialog.innerHTML=`<form class="request-form"><header class="studio-dialog-heading"><h2>${esc(title)}</h2><button class="studio-dialog-close" type="button" data-cancel aria-label="Close">×</button></header>${contents}<p role="alert"></p><div class="dialog-actions"><button type="button" data-cancel>Cancel</button><button class="primary" type="submit">Save</button></div></form>`;document.body.appendChild(dialog);dialog.querySelectorAll('[data-cancel]').forEach(button=>button.onclick=()=>dialog.close());dialog.addEventListener('close',()=>dialog.remove(),{once:true});dialog.showModal();return dialog;
}
async function launchStudioNewBookingAction(){
    try{
        const me=await json('/api/auth/me');
        const role=await json('/api/security/global-role-me');
        if(!role?.frontDesk&&!(me.user||me).isAdministrator){
            alert('You are not assigned to the Front Desk operational role.');
            return;
        }
        const [workspaces,clients]=await Promise.all([
            json('/api/frontdesk/workspaces'),
            json('/api/frontdesk/clients')
        ]);
        frontDeskWorkspaces=Array.isArray(workspaces)?workspaces:[];
        frontDeskClients=Array.isArray(clients)?clients:[];
        await openPendingBookingForm(null,null);
    }catch(error){
        console.error('[Studio New Booking]',error);
        alert('Unable to open New Booking: '+(error?.message||'Request failed'));
    }
}
window.studioLaunchNewBooking=launchStudioNewBookingAction;
async function openPendingBookingForm(enquiry,client){


    const jobTemplates=await json('/api/commercial/templates/Job');
    const dialog=frontDeskDialog('Pending booking request',`<p>A Pending Job is created now. FrontDesk reviews, routes and confirms it separately.</p><label>Job Template<select name="jobTemplate"><option value="">Custom Job</option>${jobTemplates.map(t=>`<option value="${t.id}">${esc(t.definition.name)}</option>`).join('')}</select></label><div class="frontdesk-template-tools"><button type="button" class="action" data-template-preview disabled>Preview job template</button><div data-template-services aria-live="polite"></div></div><label>Workspace<select name="workspace" required>${frontDeskWorkspaces.map(w=>`<option value="${w.id}" ${w.id===enquiry?.workspaceId?'selected':''}>${esc(w.name)}</option>`).join('')}</select></label><label>Client<input name="client" required maxlength="200" value="${esc(enquiry?.contactName||client?.displayName||'')}"></label><label>Email<input name="email" type="email" required maxlength="320" value="${esc(['Email','Website'].includes(enquiry?.channel)?enquiry.contactAddress:(client?.email||''))}"></label><label>Phone (international format, e.g. +1876…)<input name="phone" maxlength="80" value="${esc(client?.phone||'')}"></label><label>Wedding / session title<input name="title" required maxlength="200" value="${esc(enquiry?.subject||'')}"></label><label>Event date<input name="date" type="date" required></label><label>Required deposit (JMD)<input name="deposit" type="number" min="0" max="999999999" step="0.01" value="0"></label><label>Special instructions<textarea name="notes" maxlength="4000" placeholder="Photography requirements, accessibility needs, preferences or other instructions for this job"></textarea></label>`);
    const form=dialog.querySelector('form');if(enquiry)form.elements.workspace.disabled=true;
    if(enquiry?.id&&typeof window.openIntakeRoster==='function'){const tools=document.createElement('div');for(const [text,fn] of [['Subjects / roster',openIntakeRoster],['Intake / email status',openIntakeDeliveryStatus]]){const b=document.createElement('button');b.type='button';b.textContent=text;b.onclick=()=>fn(enquiry.id);tools.append(b);}form.append(tools);}

    let selectedServices=null;const products=await json('/api/pos/catalog');
    form.elements.jobTemplate.onchange=()=>{const t=jobTemplates.find(t=>t.id===form.elements.jobTemplate.value);selectedServices=t?(t.definition.products||[]).map(p=>p.catalogItemId):null;form.querySelector('[data-template-preview]').disabled=!t;form.querySelector('[data-template-services]').innerHTML=t?products.filter(p=>p.isActive).map(p=>`<label><input data-service="${p.id}" type="checkbox" ${selectedServices.includes(p.id)?'checked':''}>${esc(p.name)}</label>`).join(''):'';form.querySelectorAll('[data-service]').forEach(el=>el.onchange=()=>selectedServices=[...form.querySelectorAll('[data-service]:checked')].map(x=>x.dataset.service));};
    form.querySelector('[data-template-preview]').onclick=()=>StudioGraphTemplates.preview(jobTemplates.find(t=>t.id===form.elements.jobTemplate.value).definition,products,selectedServices);
    form.onsubmit=async e=>{
        e.preventDefault();
        const button=form.querySelector('[type=submit]');
        const feedback=form.querySelector('[role=alert]');
        const payload={clientId:client?.id||null,jobTemplateId:form.elements.jobTemplate.value||null,selectedServiceIds:selectedServices,workspaceId:form.elements.workspace.value,clientName:form.elements.client.value,clientEmail:form.elements.email.value,clientPhone:form.elements.phone.value||null,title:form.elements.title.value,eventDate:form.elements.date.value,requiredDeposit:Number(form.elements.deposit.value),currency:'JMD',notes:form.elements.notes.value,conversationId:enquiry?.id||null};
        const startedAt=performance.now();
        const saveContext={workspaceId:payload.workspaceId,conversationId:payload.conversationId,eventDate:payload.eventDate,hasEventDate:!!payload.eventDate};
        button.disabled=true;
        feedback.setAttribute('role','status');
        feedback.textContent='Saving Pending Job…';
        console.info('[Studio Front Desk Save] Started',saveContext);
        try{
            const saved=await json('/api/frontdesk/booking-requests',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});
            const result={requestId:saved?.id||null,jobId:saved?.jobId||null,eventDate:saved?.request?.eventDate||payload.eventDate,elapsedMs:Math.round(performance.now()-startedAt)};
            console.info('[Studio Front Desk Save] Succeeded',result);
            feedback.textContent='Saved successfully. '+(result.jobId?'Pending Job ID: '+result.jobId+'. ':'')+'Booking Request ID: '+(result.requestId||'not returned')+'. Event date: '+(result.eventDate||'not returned')+'. Close this dialog to view the updated Front Desk list.';
            button.textContent='Saved';
            form.querySelectorAll('[data-cancel]').forEach(close=>close.textContent='Close');
            frontDeskTab='requests';frontDeskDate=result.eventDate||frontDeskDate;frontDeskSelectedEntry=result.requestId||'';frontDeskMetricView='';
            try{await showFrontDesk();}catch(refreshError){console.warn('[Studio Front Desk Save] Save succeeded, but Front Desk refresh failed',refreshError);}
        }catch(error){
            const elapsedMs=Math.round(performance.now()-startedAt);
            console.error('[Studio Front Desk Save] Failed',{...saveContext,elapsedMs,error});
            feedback.setAttribute('role','alert');
            feedback.textContent='Save failed after '+elapsedMs+' ms: '+(error?.message||String(error));
            button.disabled=false;
        }
    };
}
function openVerifiedDepositForm(request){return launchFrontDeskPos('RecordDeposit',{bookingId:request.id,jobId:request.jobId});}

async function openFrontDeskClient(jobId) {
    const job = await json('/api/jobs/' + jobId);
    if (!job.conversationId) {
        alert('This job has no linked client conversation.');
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

async function showFrontDeskClients(openNew=false,selectedClientId=null){
 const clients=await json('/api/frontdesk/clients');
 const dialog=studioDialog('Clients','<div class="front-desk-tools"><input type="search" aria-label="Find client" placeholder="Find client"><button class="action" data-new>New client</button></div><div data-clients></div>');
 const render=()=>{const term=dialog.querySelector('input').value.toLowerCase();dialog.querySelector('[data-clients]').innerHTML=clients.filter(c=>[c.displayName,c.email,c.phone].join(' ').toLowerCase().includes(term)).map(c=>`<article class="task-card"><strong>${esc(c.displayName)}</strong><small>${esc(c.email||'')} · ${esc(c.phone||'')}</small><button class="action" data-client="${c.id}">Details / history</button></article>`).join('')||'<p>No clients found.</p>';dialog.querySelectorAll('[data-client]').forEach(b=>b.onclick=()=>editClient(clients.find(c=>c.id===b.dataset.client)));};
 async function editClient(client){
  const jobs=frontDeskJobs.filter(j=>j.clientId===client?.id);
  const editor=studioDialog(client?'Client details':'New client',`<form class="subject-form"><label>Name<input name="displayName" required maxlength="200" value="${esc(client?.displayName||'')}"></label><label>Email<input name="email" type="email" value="${esc(client?.email||'')}"></label><label>Phone<input name="phone" maxlength="80" value="${esc(client?.phone||'')}"></label><button class="primary">Save client</button><p role="alert"></p></form>${client?`<button class="action" onclick="showClientQuotes('${client.id}')">Quotes / estimates</button><button class="action" onclick="invoiceClientJobs('${client.id}')">Invoice client jobs</button><h3>Job history across workspaces</h3>`+jobs.map(j=>`<p>${esc(j.title)} · ${esc(j.status)}</p>`).join('')+'<p>Link jobs using the Client action in Active jobs.</p>':''}`);
  editor.querySelector('form').onsubmit=async e=>{e.preventDefault();const f=e.currentTarget;try{await json('/api/frontdesk/clients'+(client?'/'+client.id:''),{method:client?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({displayName:f.elements.displayName.value,email:f.elements.email.value,phone:f.elements.phone.value})});editor.close();dialog.close();if(selectedClientId){frontDeskDetails.clear();await showFrontDesk({workspaceId:frontDeskDashboardWorkspace});}else await showFrontDeskClients();}catch(error){f.querySelector('[role=alert]').textContent=error.message;}};
 }
 dialog.querySelector('input').oninput=render;dialog.querySelector('[data-new]').onclick=()=>editClient(null);render();
 if(openNew)await editClient(null);else if(selectedClientId)await editClient(clients.find(c=>String(c.id)===String(selectedClientId)));
}
async function linkFrontDeskClient(jobId){
 const clients=await json('/api/frontdesk/clients'),job=frontDeskJobs.find(j=>j.id===jobId);
 const dialog=frontDeskDialog('Link client',`<label>Client<select name="client"><option value="">Not linked</option>${clients.map(c=>`<option value="${c.id}" ${job.clientId===c.id?'selected':''}>${esc(c.displayName)}</option>`).join('')}</select></label><p>Clients can have jobs in any workspace. Existing client correspondence is preserved.</p>`);
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
 dialog.querySelectorAll('[data-accept]').forEach(b=>b.onclick=async()=>{const reference=prompt('Client acceptance reference (email, phone call or signed document):');if(!reference)return;try{await json(`/api/frontdesk/quotes/${b.dataset.accept}/accept`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({reference})});await refresh();}catch(e){dialog.querySelector('[role=alert]').textContent=e.message;}});
 dialog.querySelectorAll('[data-convert]').forEach(b=>b.onclick=async()=>{try{await json(`/api/frontdesk/quotes/${b.dataset.convert}/convert`,{method:'POST'});dialog.close();await showFrontDesk();}catch(e){dialog.querySelector('[role=alert]').textContent=e.message;}});
 dialog.querySelector('[data-create]').onclick=()=>{const editor=frontDeskDialog('New client quote',`<label>Workspace<select name="workspace">${frontDeskWorkspaces.map(w=>`<option value="${w.id}">${esc(w.name)}</option>`).join('')}</select></label><label>Work / service<input name="title" required maxlength="200"></label><label>Event date<input name="date" type="date" required></label><label>Estimated cost before tax (JMD)<input name="amount" type="number" min="0.01" max="999999999" step="0.01" required></label><label>Tax %<input name="tax" type="number" min="0" max="100" step="0.01" value="0"></label><label>Required deposit (JMD)<input name="deposit" type="number" min="0.01" step="0.01" required></label><label>Instructions<textarea name="notes" maxlength="4000"></textarea></label>`);editor.querySelector('form').onsubmit=async e=>{e.preventDefault();const f=e.currentTarget;try{await json('/api/frontdesk/quotes',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({clientId,workspaceId:f.elements.workspace.value,title:f.elements.title.value,eventDate:f.elements.date.value,instructions:f.elements.notes.value,requiredDeposit:Number(f.elements.deposit.value),estimate:{lines:[{description:f.elements.title.value,quantity:1,unitPrice:Number(f.elements.amount.value)}],taxRatePercent:Number(f.elements.tax.value),dueAtUtc:new Date(f.elements.date.value+'T12:00:00').toISOString(),currency:'JMD'}})});editor.close();await refresh();}catch(error){f.querySelector('[role=alert]').textContent=error.message;}};};
 return dialog;
}


async function showFrontDeskFlow(jobId){return openFrontDeskJob(jobId);}

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
        host.innerHTML=rows.map(({kind,item:n})=>`<button type="button" class="smc-row" data-fd-message-job="${esc(kind==='message'&&n.referenceType==='Job'?(n.referenceId||''):'')}" data-fd-announcement-workspace="${esc(kind==='announcement'?(n.workspaceId||''):'')}"><span class="smc-type" aria-hidden="true">${kind==='announcement'?'📣':'✉'}</span><span class="smc-copy"><strong>${esc(n.subject||n.title||(kind==='announcement'?'Announcement':'Message'))}</strong><small>${esc((kind==='announcement'&&n.workspaceName?n.workspaceName+' · ':'')+(n.body||n.eventType||'Studio message'))}</small></span><span class="smc-time">${esc(n.createdAtUtc?new Date(n.createdAtUtc).toLocaleDateString():'')}</span></button>`).join('')||'<div class="mw-empty">No recent messages or announcements.</div>';
        host.querySelectorAll('[data-fd-message-job]').forEach(button=>button.onclick=()=>{
            if(button.dataset.fdMessageJob)openFrontDeskJob(button.dataset.fdMessageJob);
            else studioNavigate('messages');
        });
    }catch(error){
        if(host.isConnected)host.innerHTML='<div class="mw-empty">Messages are temporarily unavailable. Use View All to open Messages.</div>';
    }
}
function openFrontDeskMessagePicker(){
    const jobs=frontDeskJobs.filter(job=>job?.id).sort((a,b)=>String(a.clientName||a.title||'').localeCompare(String(b.clientName||b.title||'')));
    const dialog=frontDeskDialog('Send Message',`<p>Select the Job conversation to message. Messages are retained with the Job.</p><label>Job / Client<select data-job-picker name="jobId" required><option value="">Select a Job…</option>${StudioSubjectPicker.jobOptions(jobs)}</select></label><p class="muted">The message composer will open in a popup after you select a Job.</p>`);
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


async function recordFrontDeskFulfillment(jobId,refresh=showFrontDesk){
 const workflow=await json(`/api/jobs/${jobId}/workflow-graph`);
 const job=frontDeskJobs.find(j=>j.id===jobId),operationId=crypto.randomUUID();
 const dialog=frontDeskDialog('Confirm collection / delivery',`<p>${esc(job?.title||'Job')} · ${esc(job?.clientName||'')}</p><label>Method<select name="method"><option value="Collected">Client collected</option><option value="Delivered">Delivered to client</option></select></label><label>Received by<input name="recipient" required maxlength="200"></label>`);
 dialog.querySelector('form').onsubmit=async event=>{event.preventDefault();const form=event.currentTarget,button=form.querySelector('[type=submit]');button.disabled=true;try{await json(`/api/frontdesk/jobs/${jobId}/fulfillment`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({operationId,expectedRevision:workflow.revision,method:form.elements.method.value,recipient:form.elements.recipient.value})});dialog.close();await refresh();}catch(error){form.querySelector('[role=alert]').textContent=error.message;button.disabled=false;}};
}













window.addEventListener('studio-next-actions-changed',()=>{if(document.body.dataset.studioView==='frontdesk')showFrontDesk({workspaceId:frontDeskDashboardWorkspace}).catch(error=>console.error(error));});
