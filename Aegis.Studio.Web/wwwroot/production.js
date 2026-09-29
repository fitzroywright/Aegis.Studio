let operationalActivityCache = new Map();
let shootCache = new Map();
let operationalJobs = new Map();
let photographerCache = [];
let scheduleResourceCache = [];
let productionSelectedJob = '';

showProduction = async function() {
    activateNavigation('production');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');

    const [activities, shoots, jobs, photographers, resources] = await Promise.all([
        json('/api/operations/activities'), json('/api/operations/shoots'), json('/api/jobs'), json('/api/photographers'), json('/api/schedule/resources')
    ]);
    operationalActivityCache = new Map(activities.map(item => [item.id,item]));
    shootCache = new Map(shoots.map(item => [item.id,item]));
    operationalJobs = new Map(jobs.map(job => [job.id,job]));
    photographerCache = photographers; studioRememberPeople(photographers);
    scheduleResourceCache = resources;

    const activeJobs=jobs.filter(job=>job.status!=='Completed');
    if(!productionSelectedJob||!jobs.some(job=>job.id===productionSelectedJob))
        productionSelectedJob=activeJobs[0]?.id||jobs[0]?.id||'';
    const selectedJob=jobs.find(job=>job.id===productionSelectedJob)||null;
    const selectedActivities=selectedJob?activities.filter(item=>item.jobId===selectedJob.id):[];
    const selectedShoots=selectedJob?shoots.filter(item=>item.jobId===selectedJob.id):[];
    const completed=selectedActivities.filter(item=>item.status==='Completed').length;
    const progress=selectedActivities.length?Math.round(completed/selectedActivities.length*100):0;
    const activeCount=activities.filter(item=>!['Completed','Cancelled'].includes(item.status)).length;
    const blockedCount=activities.filter(item=>item.status==='Blocked').length;

    genericView.innerHTML = `
      <section class="production-hub">
        <header class="production-hub-header">
          <div><h1>Production</h1><p>Live work, ownership, progress, handoffs and team communication.</p></div>
          <div class="production-header-actions">
            <span class="production-status-summary">${activeCount} active · ${blockedCount} blocked</span>
            <button class="action" onclick="createPhotographerProfile()">+ Photographer</button>
            <button class="action" onclick="openShootForm(productionSelectedJob)">+ Shoot</button>
            <button class="primary" onclick="openActivityForm(productionSelectedJob)">+ Activity</button>
          </div>
        </header>
        <div class="production-hub-grid">
          <aside class="production-queue">
            <div class="production-pane-title"><strong>Activity Hub</strong><span>${activeJobs.length} jobs</span></div>
            <div class="production-queue-scroll">
              ${activeJobs.map(job=>productionJobCard(job,activities,shoots)).join('')||'<p class="muted">No active production jobs.</p>'}
            </div>
          </aside>
          <main class="production-workspace">
            ${selectedJob?productionWorkspaceMarkup(selectedJob,selectedActivities,selectedShoots,progress):'<div class="empty-state"><h2>No production job selected</h2></div>'}
          </main>
          <aside class="production-comms">
            ${selectedJob?productionCommsMarkup(selectedJob):'<div class="production-pane-title"><strong>Communication</strong></div>'}
          </aside>
        </div>
      </section>
      ${buildActivityDialog(jobs)}${buildShootDialog(jobs,photographers,resources)}`;

    activityForm.onsubmit = createOperationalActivity;
    shootForm.onsubmit = createShootSession;
    genericView.querySelectorAll('[data-production-job]').forEach(button=>button.onclick=()=>{productionSelectedJob=button.dataset.productionJob;showProduction().catch(error=>alert(error.message||error));});
    if(selectedJob) await loadProductionComms(selectedJob);
};

function productionJobProgress(job,activities){
    const list=activities.filter(item=>item.jobId===job.id&&!['Cancelled'].includes(item.status));
    if(!list.length)return 0;
    return Math.round(list.filter(item=>item.status==='Completed').length/list.length*100);
}
function productionJobCard(job,activities,shoots){
    const mine=activities.filter(item=>item.jobId===job.id&&!['Completed','Cancelled'].includes(item.status));
    const next=mine.slice().sort((a,b)=>new Date(a.dueAtUtc||a.scheduledAtUtc||'2999-01-01')-new Date(b.dueAtUtc||b.scheduledAtUtc||'2999-01-01'))[0];
    const progress=productionJobProgress(job,activities);
    const active=job.id===productionSelectedJob;
    const owner=next?.assignedTo?studioFirstName(next.assignedTo):job.assignedTo?studioFirstName(job.assignedTo):'Unassigned';
    return `<button class="production-job-card ${active?'active':''}" data-production-job="${job.id}">
      <span class="production-card-top"><strong>${esc(job.title)}</strong><small>${progress}%</small></span>
      <span class="production-card-client">${esc(job.clientName||'Client not linked')}</span>
      <span class="production-card-flow"><i style="width:${progress}%"></i></span>
      <span class="production-card-next">${next?`${esc(next.activityType)} · ${esc(next.title)}`:'No open activity'}</span>
      <span class="production-card-owner">${esc(owner)} · ${esc(job.status)}</span>
    </button>`;
}
function productionActivityPercent(activity){
    if(activity.status==='Completed')return 100;
    if(activity.status==='InProgress')return 55;
    if(activity.status==='Blocked')return 35;
    return 10;
}
function productionWorkspaceMarkup(job,activities,shoots,progress){
    const ordered=activities.slice().sort((a,b)=>new Date(a.scheduledAtUtc||a.dueAtUtc||a.createdAtUtc||0)-new Date(b.scheduledAtUtc||b.dueAtUtc||b.createdAtUtc||0));
    return `<div class="production-workspace-head">
      <div><span class="production-kicker">Selected job</span><h2>${esc(job.title)}</h2><p>${esc(job.clientName)} · ${esc(job.status)}</p></div>
      <div class="production-job-progress"><strong>${progress}%</strong><span><i style="width:${progress}%"></i></span></div>
    </div>
    <div class="production-flow-scroll">
      <section class="production-flow">
        <div class="production-pane-title"><strong>Activity Flow</strong><span>Who is doing what, for this job</span></div>
        ${ordered.length?ordered.map((activity,index)=>productionFlowItem(activity,index,ordered.length)).join(''):'<p class="muted">No production activities yet.</p>'}
      </section>
      <section class="production-shoot-strip">
        <div class="production-pane-title"><strong>Photography / Shoots</strong><span>${shoots.length}</span></div>
        ${shoots.length?shoots.map(renderShootRow).join(''):'<p class="muted">No shoots scheduled.</p>'}
      </section>
    </div>`;
}
function productionFlowItem(activity,index,total){
    const owner=activity.assignedTo?studioFirstName(activity.assignedTo):'Unassigned';
    const percent=productionActivityPercent(activity);
    return `<article class="production-flow-item ${String(activity.status).toLowerCase()}">
      <div class="production-flow-rail"><span>${index+1}</span>${index<total-1?'<i></i>':''}</div>
      <div class="production-flow-body">
        <div class="production-flow-heading"><div><strong>${esc(activity.activityType)} · ${esc(activity.title)}</strong><small>${esc(owner)}${activity.dueAtUtc?' · due '+esc(new Date(activity.dueAtUtc).toLocaleString()):''}</small></div><span class="status-pill">${esc(activity.status)}</span></div>
        <div class="production-mini-progress"><i style="width:${percent}%"></i></div>
        ${activity.blockedReason?`<p class="production-blocked">Blocked: ${esc(activity.blockedReason)}</p>`:activity.description?`<p>${esc(activity.description)}</p>`:''}
        <div class="production-inline-actions"><button class="action" onclick="setOperationalActivityStatus('${activity.id}','InProgress')">Start</button><button class="action" onclick="blockOperationalActivity('${activity.id}')">Block</button><button class="action" onclick="setOperationalActivityStatus('${activity.id}','Completed')">Complete</button></div>
      </div>
    </article>`;
}
function productionCommsMarkup(job){
    return `<div class="production-pane-title"><strong>Communication & Actions</strong><span>Job team</span></div>
      <div class="production-action-bar">
        <button class="action" onclick="openWorkspaceJob('${job.id}')">Open Job</button>
        <button class="action" onclick="openActivityForm('${job.id}')">Add Activity</button>
        <button class="action" onclick="openShootForm('${job.id}')">Schedule Shoot</button>
        <button class="action" onclick="openMediaArchive('${job.id}','')">Media</button>
      </div>
      <div class="production-comms-tabs"><button class="active" data-production-message-scope="job">Job messages</button><button data-production-message-scope="workspace">Workspace</button></div>
      <div id="productionMessageHistory" class="production-message-history"><p class="muted">Loading messages…</p></div>
      <form id="productionMessageComposer" class="production-message-composer"><textarea name="body" placeholder="Message the team…" required></textarea><button class="primary" type="submit">Send</button></form>
      <details class="production-people"><summary>People & resources</summary>
        <div>${photographerCache.filter(p=>p.isActive).map(p=>`<span>${esc(studioFirstName(p))}</span>`).join('')||'<span>No active photographers</span>'}</div>
      </details>`;
}
async function loadProductionComms(job,scope='job'){
    const history=document.getElementById('productionMessageHistory'),form=document.getElementById('productionMessageComposer');
    if(!history||!form)return;
    try{
        const scopeType=scope==='workspace'?'Workspace':'Job';
        const scopeId=scope==='workspace'?job.workspaceId:job.id;
        const thread=await json(`/api/messages/threads/${scopeType}/${scopeId}`,{method:'POST'});
        const messages=await json(`/api/messages/threads/${thread.id}`);
        history.innerHTML=messages.length?messages.map(message=>`<article class="production-message"><strong>${esc(studioFirstName(message.senderUserName))}</strong><small>${esc(new Date(message.createdAtUtc).toLocaleString())}</small><p>${esc(message.body)}</p></article>`).join(''):'<p class="muted">No messages yet.</p>';
        history.scrollTop=history.scrollHeight;
        form.dataset.threadId=thread.id;
    }catch(error){history.innerHTML=`<p class="muted">${esc(error.message||'Messages unavailable.')}</p>`;}
    genericView.querySelectorAll('[data-production-message-scope]').forEach(button=>button.onclick=async()=>{genericView.querySelectorAll('[data-production-message-scope]').forEach(x=>x.classList.toggle('active',x===button));await loadProductionComms(job,button.dataset.productionMessageScope);});
    form.onsubmit=async event=>{event.preventDefault();const body=form.elements.body.value.trim();if(!body||!form.dataset.threadId)return;const button=form.querySelector('button');button.disabled=true;try{await json(`/api/messages/threads/${form.dataset.threadId}`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({body})});form.reset();await loadProductionComms(job,genericView.querySelector('[data-production-message-scope].active')?.dataset.productionMessageScope||'job');}catch(error){alert(error.message||error);}finally{button.disabled=false;}};
}

function renderPhotographer(p) {
    return `<div class="job-row"><div><b>${esc(studioFirstName(p))}</b><div class="meta">${esc(p.employmentType)} · ${p.isActive ? 'Active' : 'Inactive'}</div><div class="meta">${esc((p.skills||[]).join(', ') || 'No skills listed')}</div></div><div><button class="action" onclick="addPhotographerAvailability('${esc(p.userName)}')">Availability</button></div></div>`;
}

function renderShootRow(shoot) {
    const job = operationalJobs.get(shoot.jobId);
    return `<div class="job-row"><div><b>${esc(job?.title || 'Job')} — ${esc(studioFirstName(shoot.photographerUserName))}</b><div class="meta">${new Date(shoot.callTimeUtc).toLocaleString()} → ${new Date(shoot.plannedEndUtc).toLocaleString()}${shoot.location ? ' · '+esc(shoot.location) : ''}</div><div class="meta">${esc(shoot.status)} · ${shoot.imageCount} verified files · ${shoot.backupConfirmed ? 'Backup verified' : 'Backup not verified'}${shoot.reshootRequired ? ' · RESHOOT' : ''}</div></div><div class="job-actions"><button class="action" onclick="updateShoot('${shoot.id}','checkin')">Check in</button><button class="action" onclick="updateShoot('${shoot.id}','checkout')">Check out</button><button class="action" onclick="recordShootResults('${shoot.id}')">Notes</button><button class="action" onclick="openMediaArchive('${shoot.jobId}','${shoot.id}')">Media</button><button class="action" onclick="handoffShoot('${shoot.id}')">Handoff</button><button class="action" onclick="openJobOperations('${shoot.jobId}')">Job</button></div></div>`;
}

function renderActivityRow(activity) {
    const job = operationalJobs.get(activity.jobId);
    return `<div class="job-row"><div><b>${esc(activity.activityType)} · ${esc(activity.title)}</b><div class="meta">${esc(job?.title || 'Job')} · ${esc(activity.assignedTo ? studioFirstName(activity.assignedTo) : 'Unassigned')} · ${esc(activity.priority)} · ${esc(activity.status)}${activity.dueAtUtc ? ' · due '+new Date(activity.dueAtUtc).toLocaleString() : ''}${activity.blockedReason ? ' · '+esc(activity.blockedReason) : ''}</div></div><div class="job-actions"><button class="action" onclick="setOperationalActivityStatus('${activity.id}','InProgress')">Start</button><button class="action" onclick="blockOperationalActivity('${activity.id}')">Block</button><button class="action" onclick="setOperationalActivityStatus('${activity.id}','Completed')">Complete</button><button class="action" onclick="openJobOperations('${activity.jobId}')">Job</button></div></div>`;
}

function buildActivityDialog(jobs) {
    return `<dialog id="activityDialog"><form id="activityForm" method="dialog" class="request-form"><h2>Operational activity</h2><label>Job<select id="activityJob" required>${jobs.map(j=>`<option value="${j.id}">${esc(j.title)}</option>`).join('')}</select></label><label>Type<select id="activityType"><option>Travel</option><option>EquipmentPrep</option><option>FileIngest</option><option>Backup</option><option>Cull</option><option>Edit</option><option>Retouch</option><option>Proof</option><option>Print</option><option>QC</option><option>Delivery</option><option>Archive</option><option>Reshoot</option><option>Other</option></select></label><label>Title<input id="activityTitle" required></label><label>Assigned to<input id="activityAssigned"></label><div class="row"><label>Scheduled<input id="activityScheduled" type="datetime-local"></label><label>Due<input id="activityDue" type="datetime-local"></label></div><label>Priority<select id="activityPriority"><option>Normal</option><option>Low</option><option>High</option><option>Urgent</option></select></label><label>Location<input id="activityLocation"></label><label>Description<textarea id="activityDescription"></textarea></label><label>Handoff to<input id="activityHandoff"></label><div class="dialog-actions"><button type="button" onclick="activityDialog.close()">Cancel</button><button type="submit" class="primary">Create</button></div></form></dialog>`;
}

function buildShootDialog(jobs,photographers,resources) {
    const equipment=resources.filter(r=>['Equipment','Vehicle','Room'].includes(r.resourceType));
    return `<dialog id="shootDialog"><form id="shootForm" method="dialog" class="request-form"><h2>Schedule photographer</h2><label>Job<select id="shootJob" required>${jobs.map(j=>`<option value="${j.id}">${esc(j.title)}</option>`).join('')}</select></label><label>Photographer<select id="shootPhotographer" required><option value="">Select photographer</option>${photographers.filter(p=>p.isActive).map(p=>`<option value="${esc(p.userName)}">${esc(studioFirstName(p))}</option>`).join('')}</select></label><label>Assistant<select id="shootAssistant"><option value="">None</option>${photographers.filter(p=>p.isActive).map(p=>`<option value="${esc(p.userName)}">${esc(studioFirstName(p))}</option>`).join('')}</select></label><div class="row"><label>Call time<input id="shootStart" type="datetime-local" required></label><label>Planned end<input id="shootEnd" type="datetime-local" required></label></div><label>Location<input id="shootLocation"></label><label>Rooms / equipment / vehicles<select id="shootResources" multiple size="6">${equipment.map(r=>`<option value="${r.id}">${esc(r.resourceType)} · ${esc(r.name)}</option>`).join('')}</select></label><label>Coverage notes<textarea id="shootCoverage"></textarea></label><label>Equipment notes<textarea id="shootEquipment"></textarea></label><div class="dialog-actions"><button type="button" onclick="shootDialog.close()">Cancel</button><button type="submit" class="primary">Schedule</button></div></form></dialog>`;
}

function openActivityForm(jobId) { if(jobId) activityJob.value=jobId; activityDialog.showModal(); }
function openShootForm(jobId) { const start=new Date(Date.now()+3600000); start.setMinutes(0,0,0); const end=new Date(start.getTime()+2*3600000); const local=d=>new Date(d.getTime()-d.getTimezoneOffset()*60000).toISOString().slice(0,16); shootStart.value=local(start); shootEnd.value=local(end); if(jobId) shootJob.value=jobId; shootDialog.showModal(); }

async function createOperationalActivity(event) {
    event.preventDefault();
    await json(`/api/jobs/${activityJob.value}/operations/activities`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({activityType:activityType.value,title:activityTitle.value.trim(),description:activityDescription.value.trim()||null,status:'NotStarted',priority:activityPriority.value,assignedTo:activityAssigned.value.trim()||null,scheduledAtUtc:activityScheduled.value?new Date(activityScheduled.value).toISOString():null,dueAtUtc:activityDue.value?new Date(activityDue.value).toISOString():null,location:activityLocation.value.trim()||null,blockedReason:null,handoffTo:activityHandoff.value.trim()||null})});
    activityDialog.close(); await showProduction();
}

async function createShootSession(event) {
    event.preventDefault();
    const resourceIds=Array.from(shootResources.selectedOptions).map(o=>o.value);
    await json(`/api/jobs/${shootJob.value}/operations/shoots`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({photographerUserName:shootPhotographer.value,assistantUserName:shootAssistant.value||null,callTimeUtc:new Date(shootStart.value).toISOString(),plannedEndUtc:new Date(shootEnd.value).toISOString(),location:shootLocation.value.trim()||null,coverageNotes:shootCoverage.value.trim()||null,equipmentNotes:shootEquipment.value.trim()||null,resourceIds})});
    shootDialog.close(); await showProduction();
}

async function setOperationalActivityStatus(id,status,blockedReason) { const item=operationalActivityCache.get(id); if(!item)return; await json('/api/operations/activities/'+id,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({activityType:item.activityType,title:item.title,description:item.description,status,priority:item.priority,assignedTo:item.assignedTo,scheduledAtUtc:item.scheduledAtUtc,dueAtUtc:item.dueAtUtc,location:item.location,blockedReason:blockedReason||null,handoffTo:item.handoffTo})}); await showProduction(); }
async function blockOperationalActivity(id) { const reason=prompt('Why is this activity blocked?'); if(reason) await setOperationalActivityStatus(id,'Blocked',reason); }
async function updateShoot(id,action) { await json('/api/operations/shoots/'+id,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({action,imageCount:null,backupConfirmed:null,reshootRequired:null,coverageNotes:null,equipmentNotes:null,handoffTo:null})}); await showProduction(); }

async function recordShootResults(id) {
    const item=shootCache.get(id); if(!item)return;
    const reshoot=confirm('Is a reshoot required?'); const coverage=prompt('Coverage / field notes',item.coverageNotes||''); const equipment=prompt('Equipment notes',item.equipmentNotes||'');
    await json('/api/operations/shoots/'+id,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'update',imageCount:null,backupConfirmed:null,reshootRequired:reshoot,coverageNotes:coverage,equipmentNotes:equipment,handoffTo:null})}); await showProduction();
}

async function handoffShoot(id) { const handoffTo=prompt('Hand off verified files to which user/team?'); if(!handoffTo)return; try{ await json('/api/operations/shoots/'+id,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'handoff',imageCount:null,backupConfirmed:null,reshootRequired:null,coverageNotes:null,equipmentNotes:null,handoffTo})}); await showProduction(); }catch(error){ alert(error.message||error); } }

async function createPhotographerProfile() {
    const userName=prompt('AD user name'); if(!userName)return; const displayName=prompt('Display name',userName)||userName; const employmentType=prompt('Employment type: Employee, PartTime, Contractor, Freelance','Employee')||'Employee'; const skills=(prompt('Skills, comma separated','Wedding, Portrait')||'').split(',').map(x=>x.trim()).filter(Boolean); const defaultEquipmentNotes=prompt('Default equipment notes','')||null;
    try{ await json('/api/photographers',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({userName,displayName,employmentType,isActive:true,skills,defaultEquipmentNotes,scheduleResourceId:null})}); await showProduction(); }catch(error){ alert(error.message||error); }
}

async function addPhotographerAvailability(userName) {
    const from=prompt('Unavailable from (YYYY-MM-DD HH:MM)'); if(!from)return; const to=prompt('Unavailable until (YYYY-MM-DD HH:MM)'); if(!to)return; const type=prompt('Type: Unavailable, Leave, Preferred, Available','Unavailable')||'Unavailable'; const note=prompt('Note','')||null;
    await json(`/api/photographers/${encodeURIComponent(userName)}/availability`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({startsAtUtc:new Date(from).toISOString(),endsAtUtc:new Date(to).toISOString(),availabilityType:type,note})}); alert('Availability saved.');
}

async function openMediaArchive(jobId,shootId) {
    const records=await json(`/api/jobs/${jobId}/media-archives`);
    const existing=records.filter(r=>r.shootId===shootId);
    const primary=prompt('Primary media path relative to configured MediaArchive root',existing[0]?.relativePath||''); if(primary===null)return;
    const backup=prompt('Backup media path relative to configured MediaArchive root',existing[0]?.backupRelativePath||'');
    let record=existing[0];
    if(!record) record=await json(`/api/jobs/${jobId}/media-archives`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({shootId,relativePath:primary,backupRelativePath:backup||null})});
    try{ const verified=await json(`/api/jobs/${jobId}/media-archives/${record.id}/verify`,{method:'POST'}); alert(`Verified ${verified.fileCount} files (${verified.totalBytes} bytes). Primary: ${verified.primaryVerifiedAtUtc?'yes':'no'}. Backup: ${verified.backupVerifiedAtUtc?'yes':'no'}.`); await showProduction(); }catch(error){ alert(error.message||error); }
}

async function openJobOperations(jobId) {
    const [job,activities,shoots,audit,media]=await Promise.all([json('/api/jobs/'+jobId),json('/api/operations/activities?jobId='+jobId),json('/api/operations/shoots?jobId='+jobId),json(`/api/jobs/${jobId}/operations/audit`),json(`/api/jobs/${jobId}/media-archives`)]);
    genericView.innerHTML=`<div class="job-detail-header"><div><button class="link-button" onclick="showProduction()">← Production</button><h1>${esc(job.title)}</h1><p>${esc(job.clientName)} · ${esc(job.status)}</p></div></div><section class="job-section"><h2>Photography</h2>${shoots.length?shoots.map(renderShootRow).join(''):'<p class="muted">No shoots.</p>'}</section><section class="job-section"><h2>Production activities</h2>${activities.length?activities.map(renderActivityRow).join(''):'<p class="muted">No activities.</p>'}</section><section class="job-section"><h2>Verified media</h2>${media.length?media.map(m=>`<div class="job-row"><div><b>${esc(m.relativePath)}</b><div class="meta">${m.fileCount} files · ${m.totalBytes} bytes · primary ${m.primaryVerifiedAtUtc?'verified':'pending'} · backup ${m.backupVerifiedAtUtc?'verified':'pending'}</div></div></div>`).join(''):'<p class="muted">No media registered.</p>'}</section><section class="job-section"><h2>Operational history</h2>${audit.length?audit.map(i=>`<div class="financial-row"><div><b>${esc(i.action)}</b><small>${esc(studioFirstName(i.userName))} · ${new Date(i.createdAtUtc).toLocaleString()}</small></div><span>${esc(i.detail||'')}</span></div>`).join(''):'<p class="muted">No history.</p>'}</section>`;
}

async function applyProductionTemplate(jobId,templateName) { await json(`/api/jobs/${jobId}/operations/template/${encodeURIComponent(templateName)}`,{method:'POST'}); await showProduction(); }
const productionNavigationButton=document.querySelector('.brand-button[data-view="production"]'); if(productionNavigationButton) productionNavigationButton.onclick=()=>showProduction();
