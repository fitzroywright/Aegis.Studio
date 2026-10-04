let operationalActivityCache = new Map();
let shootCache = new Map();
let operationalJobs = new Map();
let photographerCache = [];
let scheduleResourceCache = [];
let productionSelectedJob = '';

let productionView='queue';
let productionQueueScope='mine',productionStageFilter='',productionWorkspaceFilter='',productionAssigneeFilter='',productionDueFilter='';
async function showProduction(){
    activateNavigation('production');conversationView.classList.add('hidden');genericView.classList.remove('hidden');
    const [activities,shoots,jobs,photographers,resources,auth,workspaces]=await Promise.all([json('/api/operations/activities'),json('/api/operations/shoots'),json('/api/jobs'),json('/api/photographers'),json('/api/schedule/resources'),json('/api/auth/me'),json('/api/workspaces')]);
    operationalActivityCache=new Map(activities.map(a=>[a.id,a]));shootCache=new Map(shoots.map(a=>[a.id,a]));operationalJobs=new Map(jobs.map(j=>[j.id,j]));photographerCache=photographers;scheduleResourceCache=resources;studioRememberPeople(photographers);
    const me=auth.user||auth,user=String(me.userName||me.loginName||'').toLowerCase(),manager=me.isAdministrator===true;
    if(manager&&productionQueueScope==='mine'&&!activities.some(a=>String(a.assignedTo||'').toLowerCase()===user))productionQueueScope='all';
    const jobIds=jobs.map(j=>j.id),snapshots=new Map(await Promise.all(jobIds.map(async id=>[id,await json('/api/jobs/'+id+'/media')])));
    const access=new Map(await Promise.all(jobIds.map(async id=>[id,(await json('/api/jobs/'+id+'/access')).permissions])));
    const productionTypes=['FileIngest','Cull','Edit','Retouch','Proof','QC','Print','Delivery','Archive'];
    const queue=activities.filter(a=>productionTypes.includes(a.activityType)).filter(a=>productionQueueScope==='all'||String(a.assignedTo||'').toLowerCase()===user).filter(a=>!productionStageFilter||a.activityType===productionStageFilter).filter(a=>!productionWorkspaceFilter||operationalJobs.get(a.jobId)?.workspaceId===productionWorkspaceFilter).filter(a=>!productionAssigneeFilter||a.assignedTo===productionAssigneeFilter).filter(a=>!productionDueFilter||(productionDueFilter==='blocked'?a.status==='Blocked':productionDueFilter==='overdue'?a.dueAtUtc&&new Date(a.dueAtUtc)<new Date()&&!['Completed','Cancelled'].includes(a.status):a.dueAtUtc&&new Date(a.dueAtUtc).toLocaleDateString()===new Date().toLocaleDateString()));
    const options=(values,current)=>'<option value="">All</option>'+values.map(([id,label])=>`<option value="${esc(id)}" ${current===id?'selected':''}>${esc(label)}</option>`).join('');
    genericView.innerHTML=`<header class="studio-page-heading"><div><h1>Production</h1><p>Collections, assignments, handoffs and photographic work.</p></div><div class="job-actions"><button data-new-shoot>Schedule Shoot</button><button class="primary" data-new-activity>＋ Activity</button></div></header>
    <div class="op-metrics">${studioMetric(queue.filter(a=>!['Completed','Cancelled'].includes(a.status)).length,'Open work units','blue')}${studioMetric(queue.filter(a=>a.status==='InProgress').length,'In progress','purple')}${studioMetric(queue.filter(a=>a.status==='Blocked').length,'Blocked','orange')}${studioMetric(queue.filter(a=>a.status==='Completed').length,'Completed','green')}</div>
    <div class="workspace-tabs"><button data-production-view="queue" class="${productionView==='queue'?'active':''}">Photo Production</button><button data-production-view="photography" class="${productionView==='photography'?'active':''}">Photography / Shoots</button></div><div class="production-filters"><button data-scope="mine" class="${productionQueueScope==='mine'?'active':''}">My Queue</button><button data-scope="all" class="${productionQueueScope==='all'?'active':''}">All Production</button><label>Stage<select data-filter="stage">${options(productionTypes.map(x=>[x,x]),productionStageFilter)}</select></label><label>Workspace<select data-filter="workspace">${options(workspaces.map(w=>[w.id,w.name]),productionWorkspaceFilter)}</select></label><label>Assignee<select data-filter="assignee">${options([...new Set(activities.map(a=>a.assignedTo).filter(Boolean))].map(x=>[x,studioFirstName(x)]),productionAssigneeFilter)}</select></label><label>Attention<select data-filter="due">${options([['today','Due today'],['overdue','Overdue'],['blocked','Blocked / waiting']],productionDueFilter)}</select></label></div>
    <section class="production-queue-card" ${productionView==='queue'?'':'hidden'}><table class="studio-table"><thead><tr><th>Job / Collection</th><th>Workspace</th><th>Stage / Status</th><th>Assets</th><th>Assignee</th><th>Age</th><th>Due / Priority</th><th>Blocker / Next action</th><th>Actions</th></tr></thead><tbody>${queue.map(a=>{const job=operationalJobs.get(a.jobId),snapshot=snapshots.get(a.jobId),assets=(snapshot?.assets||[]).filter(x=>!a.collectionName||(x.collection||x.collectionName)===a.collectionName);const canEdit=access.get(a.jobId)?.manageProduction;return `<tr><td><button data-open-job="${a.jobId}">${esc(job?.title||'Job')}</button><small>${esc(a.collectionName||a.title)}</small></td><td>${esc(workspaces.find(w=>w.id===job?.workspaceId)?.name||'')}</td><td>${esc(a.activityType)}<small>${esc(a.status)}</small></td><td>${assets.length}</td><td>${esc(studioFirstName(a.assignedTo||'Unassigned'))}</td><td>${Math.max(0,Math.floor((Date.now()-new Date(a.startedAtUtc||a.createdAtUtc))/3600000))}h</td><td>${a.dueAtUtc?esc(new Date(a.dueAtUtc).toLocaleDateString()):'Unscheduled'}<small>${esc(a.priority)}</small></td><td>${esc(a.blockedReason||a.handoffTo||(a.status==='NotStarted'?'Start '+a.activityType:a.status==='Completed'?'Hand off':'Continue '+a.activityType))}</td><td>${canEdit&&!['Completed','Cancelled'].includes(a.status)?`${a.status!=='InProgress'?`<button data-work-start="${a.id}">Start</button>`:''}<button data-work-block="${a.id}">Block</button><button data-work-complete="${a.id}">Complete</button>`:'View'}</td></tr>`;}).join('')||'<tr><td colspan="9">No work matches these filters.</td></tr>'}</tbody></table></section>
    <section class="production-queue-card" ${productionView==='photography'?'':'hidden'}>${shoots.filter(s=>productionQueueScope==='all'||String(s.photographerUserName).toLowerCase()===user).map(s=>renderShootRow(s,access.get(s.jobId)?.manageSchedule||access.get(s.jobId)?.manageProduction)).join('')||'<p class="empty-state">No shoots match this queue.</p>'}</section><div class="production-resources">${resources.filter(r=>r.resourceType==='Equipment').map(r=>`<span><strong>${esc(r.name)}</strong><small>${esc(r.notes||'No resource status recorded')}</small></span>`).join('')}</div>${buildActivityDialog(jobs.filter(j=>access.get(j.id)?.manageProduction))}${buildShootDialog(jobs.filter(j=>access.get(j.id)?.manageSchedule),photographers,resources)}`;
    document.getElementById('activityForm').onsubmit=createOperationalActivity;document.getElementById('shootForm').onsubmit=createShootSession;
    genericView.querySelector('[data-new-activity]').disabled=!jobs.some(j=>access.get(j.id)?.manageProduction);
    genericView.querySelector('[data-new-activity]').onclick=()=>openActivityForm();
    genericView.querySelector('[data-new-shoot]').disabled=!jobs.some(j=>access.get(j.id)?.manageSchedule);
    genericView.querySelector('[data-new-shoot]').onclick=()=>openShootForm();
    genericView.querySelectorAll('[data-production-view]').forEach(b=>b.onclick=()=>{productionView=b.dataset.productionView;showProduction().catch(e=>alert(e.message));});
    genericView.querySelectorAll('[data-open-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.openJob).catch(e=>alert(e.message)));
    genericView.querySelectorAll('[data-scope]').forEach(b=>b.onclick=()=>{productionQueueScope=b.dataset.scope;showProduction().catch(e=>alert(e.message));});
    genericView.querySelectorAll('[data-filter]').forEach(s=>s.onchange=()=>{if(s.dataset.filter==='stage')productionStageFilter=s.value;else if(s.dataset.filter==='workspace')productionWorkspaceFilter=s.value;else if(s.dataset.filter==='assignee')productionAssigneeFilter=s.value;else productionDueFilter=s.value;showProduction().catch(e=>alert(e.message));});
    for(const [selector,status] of [['workStart','InProgress'],['workComplete','Completed']])genericView.querySelectorAll('[data-'+selector.replace(/[A-Z]/g,x=>'-'+x.toLowerCase())+']').forEach(b=>b.onclick=()=>setOperationalActivityStatus(b.dataset[selector],status).catch(e=>alert(e.message)));
    genericView.querySelectorAll('[data-work-block]').forEach(b=>b.onclick=()=>blockOperationalActivity(b.dataset.workBlock).catch(e=>alert(e.message)));
}

function renderPhotographer(p) {
    return `<div class="job-row"><div><b>${esc(studioFirstName(p))}</b><div class="meta">${esc(p.employmentType)} · ${p.isActive ? 'Active' : 'Inactive'}</div><div class="meta">${esc((p.skills||[]).join(', ') || 'No skills listed')}</div></div><div><button class="action" onclick="addPhotographerAvailability('${esc(p.userName)}')">Availability</button></div></div>`;
}

function renderShootRow(shoot,canManage=true) {
    const job = operationalJobs.get(shoot.jobId);
    return `<div class="job-row"><div><b>${esc(job?.title || 'Job')} — ${esc(studioFirstName(shoot.photographerUserName))}</b><div class="meta">${new Date(shoot.callTimeUtc).toLocaleString()} → ${new Date(shoot.plannedEndUtc).toLocaleString()}${shoot.location ? ' · '+esc(shoot.location) : ''}</div><div class="meta">${esc(shoot.status)} · ${shoot.imageCount} verified files · ${shoot.backupConfirmed ? 'Backup verified' : 'Backup not verified'}${shoot.reshootRequired ? ' · RESHOOT' : ''}</div></div><div class="job-actions">${canManage?`<button class="action" onclick="updateShoot('${shoot.id}','checkin')">Check in</button><button class="action" onclick="updateShoot('${shoot.id}','checkout')">Check out</button><button class="action" onclick="recordShootResults('${shoot.id}')">Notes</button>`:''}<button class="action" onclick="openMediaArchive('${shoot.jobId}','${shoot.id}')">Media</button>${canManage?`<button class="action" onclick="handoffShoot('${shoot.id}')">Handoff</button>`:''}<button class="action" onclick="openJobOperations('${shoot.jobId}')">Job</button></div></div>`;
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


