let productionSearch='',productionPriority='';
let operationalActivityCache = new Map();
let shootCache = new Map();
let operationalJobs = new Map();
let photographerCache = [];
let scheduleResourceCache = [];
let productionSelectedJob = '';

let productionView='queue';
let productionQueueScope='mine',productionStageFilter='',productionWorkspaceFilter='',productionAssigneeFilter='',productionDueFilter='';
async function showProduction(){
    conversationView.classList.add('hidden');genericView.classList.remove('hidden');
    const [activities,shoots,jobs,photographers,resources,auth,workspaces]=await Promise.all([json('/api/operations/activities'),json('/api/operations/shoots'),json('/api/jobs'),json('/api/photographers'),json('/api/schedule/resources'),json('/api/auth/me'),json('/api/workspaces')]);
    operationalActivityCache=new Map(activities.map(a=>[a.id,a]));shootCache=new Map(shoots.map(a=>[a.id,a]));operationalJobs=new Map(jobs.map(j=>[j.id,j]));photographerCache=photographers;scheduleResourceCache=resources;studioRememberPeople(photographers);
    const me=auth.user||auth,user=String(me.userName||me.loginName||'').toLowerCase(),manager=me.isAdministrator===true;
    if(manager&&productionQueueScope==='mine'&&!activities.some(a=>String(a.assignedTo||'').toLowerCase()===user))productionQueueScope='all';
    const [mediaSummary,permissionMap]=await Promise.all([json('/api/production/media-summary'),json('/api/jobs/access')]);
    const access=new Map(Object.entries(permissionMap));
    const productionTypes=['FileIngest','Cull','Edit','Retouch','Proof','QC','Print','Delivery','Archive'];
    const unitSummaries=await json('/api/production/items-summary');
    const queue=activities.filter(a=>productionTypes.includes(a.activityType)).filter(a=>productionQueueScope==='all'||String(a.assignedTo||'').toLowerCase()===user).filter(a=>!productionPriority||a.priority===productionPriority).filter(a=>!productionSearch||[a.title,a.description,a.collectionName,operationalJobs.get(a.jobId)?.clientName,operationalJobs.get(a.jobId)?.title].join(' ').toLowerCase().includes(productionSearch.toLowerCase())).filter(a=>!productionStageFilter||a.activityType===productionStageFilter).filter(a=>!productionWorkspaceFilter||(operationalJobs.get(a.jobId)?.workspaceId===productionWorkspaceFilter||(operationalJobs.get(a.jobId)?.participatingWorkspaceIds||[]).includes(productionWorkspaceFilter))).filter(a=>!productionAssigneeFilter||a.assignedTo===productionAssigneeFilter).filter(a=>!productionDueFilter||(productionDueFilter==='blocked'?a.status==='Blocked':productionDueFilter==='overdue'?a.dueAtUtc&&new Date(a.dueAtUtc)<new Date()&&!['Completed','Cancelled'].includes(a.status):a.dueAtUtc&&new Date(a.dueAtUtc).toLocaleDateString()===new Date().toLocaleDateString()));
    const options=(values,current)=>'<option value="">All</option>'+values.map(([id,label])=>`<option value="${esc(id)}" ${current===id?'selected':''}>${esc(label)}</option>`).join('');
    genericView.innerHTML=`<header class="studio-page-heading"><div><h1>Production</h1><p>Collections, assignments, handoffs and photographic work.</p></div><div class="job-actions"><button data-new-shoot>Schedule Shoot</button><button class="primary" data-new-activity>＋ Activity</button></div></header>
    <div class="op-metrics">${studioMetric(queue.filter(a=>!['Completed','Cancelled'].includes(a.status)).length,'Open work units','blue')}${studioMetric(queue.filter(a=>a.status==='InProgress').length,'In progress','purple')}${studioMetric(queue.filter(a=>a.status==='Blocked').length,'Blocked','orange')}${studioMetric(queue.filter(a=>a.status==='Completed').length,'Completed','green')}</div>
    <div class="workspace-tabs"><button data-production-view="queue" class="${productionView==='queue'?'active':''}">Photo Production</button><button data-production-view="photography" class="${productionView==='photography'?'active':''}">Photography / Shoots</button></div><div class="production-filters"><button data-scope="mine" class="${productionQueueScope==='mine'?'active':''}">My Queue</button><button data-scope="all" class="${productionQueueScope==='all'?'active':''}">All Production</button><label>Find job, client or product<input data-production-search type="search" value="${esc(productionSearch)}"></label><label>Priority<select data-production-priority>${options(['Low','Normal','High','Urgent'].map(p=>[p,p]),productionPriority)}</select></label><label>Stage<select data-filter="stage">${options(productionTypes.map(x=>[x,x]),productionStageFilter)}</select></label><label>Workspace<select data-filter="workspace">${options(workspaces.map(w=>[w.id,w.name]),productionWorkspaceFilter)}</select></label><label>Assignee<select data-filter="assignee">${options([...new Set(activities.map(a=>a.assignedTo).filter(Boolean))].map(x=>[x,studioFirstName(x)]),productionAssigneeFilter)}</select></label><label>Attention<select data-filter="due">${options([['today','Due today'],['overdue','Overdue'],['blocked','Blocked / waiting']],productionDueFilter)}</select></label></div>
    <section class="production-queue-card" ${productionView==='queue'?'':'hidden'}><table class="studio-table"><thead><tr><th>Job / Collection</th><th>Workspace</th><th>Stage / Status</th><th>Assets</th><th>Assignee</th><th>Age</th><th>Due / Priority</th><th>Blocker / Next action</th><th>Actions</th></tr></thead><tbody>${queue.map(a=>{const job=operationalJobs.get(a.jobId),summary=mediaSummary.filter(x=>x.jobId===a.jobId&&(!a.collectionName||x.collection===a.collectionName)),assetCount=summary.reduce((n,x)=>n+Number(x.assetCount),0),approvedCount=summary.reduce((n,x)=>n+Number(x.approvedCount),0),completedCount=summary.reduce((n,x)=>n+Number(x.completedCount),0);const canEdit=access.get(a.jobId)?.manageProduction;return `<tr><td><button data-open-job="${a.jobId}">${esc(job?.title||'Job')}</button><small>${esc(a.collectionName||a.title)}</small></td><td>${esc(workspaces.find(w=>w.id===job?.workspaceId)?.name||'')}</td><td>${esc(a.activityType)}<small>${esc(a.status)}</small></td><td>${unitSummaries.find(s=>s.activityId===a.id)?.completed??completedCount}/${unitSummaries.find(s=>s.activityId===a.id)?.quantity??assetCount}<small>${approvedCount} approved</small></td><td>${esc(studioFirstName(a.assignedTo||'Unassigned'))}</td><td>${Math.max(0,Math.floor((Date.now()-new Date(a.startedAtUtc||a.createdAtUtc))/3600000))}h</td><td>${a.dueAtUtc?esc(new Date(a.dueAtUtc).toLocaleDateString()):'Unscheduled'}<small>${esc(a.priority)}</small></td><td>${esc(a.blockedReason||a.handoffTo||(a.status==='NotStarted'?'Start '+a.activityType:a.status==='Completed'?'Hand off':'Continue '+a.activityType))}</td><td>${canEdit?`<button data-production-items="${a.id}">Items / QC</button>`:''}${canEdit&&!['Completed','Cancelled'].includes(a.status)?`${a.status!=='InProgress'?`<button data-work-start="${a.id}">Start</button>`:''}<button data-work-block="${a.id}">Block</button><button data-work-complete="${a.id}">Complete</button>`:'View'}</td></tr>`;}).join('')||'<tr><td colspan="9">No work matches these filters.</td></tr>'}</tbody></table></section>
    <section class="production-queue-card" ${productionView==='photography'?'':'hidden'}>${shoots.filter(s=>productionQueueScope==='all'||String(s.photographerUserName).toLowerCase()===user).map(s=>renderShootRow(s,access.get(s.jobId)?.manageSchedule||access.get(s.jobId)?.manageProduction)).join('')||'<p class="empty-state">No shoots match this queue.</p>'}</section><div class="production-resources">${resources.filter(r=>r.resourceType==='Equipment').map(r=>`<span><strong>${esc(r.name)}</strong><small>${esc(r.notes||'No resource status recorded')}</small></span>`).join('')}</div>${buildActivityDialog(jobs.filter(j=>access.get(j.id)?.manageProduction))}${buildShootDialog(jobs.filter(j=>access.get(j.id)?.manageSchedule),photographers,resources)}`;
    document.getElementById('activityForm').onsubmit=createOperationalActivity;document.getElementById('shootForm').onsubmit=createShootSession;bindEmbeddedShootAvailability();
    genericView.querySelector('[data-new-activity]').disabled=!jobs.some(j=>access.get(j.id)?.manageProduction);
    genericView.querySelector('[data-new-activity]').onclick=()=>openActivityForm();
    genericView.querySelector('[data-new-shoot]').disabled=!jobs.some(j=>access.get(j.id)?.manageSchedule);
    genericView.querySelector('[data-new-shoot]').onclick=()=>openShootForm();
    genericView.querySelectorAll('[data-production-view]').forEach(b=>b.onclick=()=>{productionView=b.dataset.productionView;showProduction().catch(e=>alert(e.message));});
    genericView.querySelectorAll('[data-open-job]').forEach(b=>b.onclick=()=>openWorkspaceJob(b.dataset.openJob).catch(e=>alert(e.message)));
    genericView.querySelectorAll('[data-scope]').forEach(b=>b.onclick=()=>{productionQueueScope=b.dataset.scope;showProduction().catch(e=>alert(e.message));});
    let searchTimer;genericView.querySelector('[data-production-search]').oninput=e=>{productionSearch=e.target.value;clearTimeout(searchTimer);searchTimer=setTimeout(()=>showProduction().catch(e=>alert(e.message)),300);};genericView.querySelector('[data-production-priority]').onchange=e=>{productionPriority=e.target.value;showProduction().catch(e=>alert(e.message));};
    genericView.querySelectorAll('[data-filter]').forEach(s=>s.onchange=()=>{if(s.dataset.filter==='stage')productionStageFilter=s.value;else if(s.dataset.filter==='workspace')productionWorkspaceFilter=s.value;else if(s.dataset.filter==='assignee')productionAssigneeFilter=s.value;else productionDueFilter=s.value;showProduction().catch(e=>alert(e.message));});
    for(const [selector,status] of [['workStart','InProgress'],['workComplete','Completed']])genericView.querySelectorAll('[data-'+selector.replace(/[A-Z]/g,x=>'-'+x.toLowerCase())+']').forEach(b=>b.onclick=()=>setOperationalActivityStatus(b.dataset[selector],status).catch(e=>alert(e.message)));
    genericView.querySelectorAll('[data-production-items]').forEach(b=>b.onclick=()=>openProductionItems(operationalActivityCache.get(b.dataset.productionItems)).catch(e=>alert(e.message)));
    genericView.querySelectorAll('[data-work-block]').forEach(b=>b.onclick=()=>blockOperationalActivity(b.dataset.workBlock).catch(e=>alert(e.message)));
}

function renderPhotographer(p) {
    return `<div class="job-row"><div><b>${esc(studioFirstName(p))}</b><div class="meta">${esc(p.employmentType)} · ${p.isActive ? 'Active' : 'Inactive'}</div><div class="meta">${esc((p.skills||[]).join(', ') || 'No skills listed')}</div></div><div><button class="action" onclick="addPhotographerAvailability('${esc(p.userName)}')">Availability</button></div></div>`;
}

function renderShootRow(shoot,canManage=true) {
    const job = operationalJobs.get(shoot.jobId);
    return `<div class="job-row"><div><b>${esc(job?.title || 'Job')} — ${esc(studioFirstName(shoot.photographerUserName))}</b><div class="meta">${new Date(shoot.callTimeUtc).toLocaleString()} → ${new Date(shoot.plannedEndUtc).toLocaleString()}${shoot.location ? ' · '+esc(shoot.location) : ''}</div><div class="meta">${esc(shoot.status)} · ${shoot.imageCount} verified files · ${shoot.backupConfirmed ? 'Backup verified' : 'Backup not verified'}${shoot.reshootRequired ? ' · RESHOOT' : ''}</div></div><div class="job-actions">${canManage?`${!shoot.checkedInAtUtc&&shoot.status!=='Cancelled'?`<button class="action" onclick="openEmbeddedShootSchedule('${shoot.jobId}','${shoot.id}')">Reschedule / Change photographer</button>`:''}<button class="action" onclick="updateShoot('${shoot.id}','checkin')">Check in</button><button class="action" onclick="updateShoot('${shoot.id}','checkout')">Check out</button><button class="action" onclick="recordShootResults('${shoot.id}')">Notes</button>`:''}<button class="action" onclick="openMediaArchive('${shoot.jobId}','${shoot.id}')">Media</button>${canManage?`<button class="action" onclick="handoffShoot('${shoot.id}')">Handoff</button>`:''}<button class="action" onclick="openJobOperations('${shoot.jobId}')">Job</button></div></div>`;
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
    return `<dialog id="shootDialog"><form id="shootForm" method="dialog" class="request-form"><h2>Schedule photographer</h2><label>Job<select id="shootJob" required>${jobs.map(j=>`<option value="${j.id}">${esc(j.title)}</option>`).join('')}</select></label><label>Photographer<select id="shootPhotographer" required><option value="">Select photographer</option>${photographers.filter(p=>p.isActive).map(p=>`<option value="${esc(p.userName)}">${esc(studioFirstName(p))}</option>`).join('')}</select></label><label>Assistant<select id="shootAssistant"><option value="">None</option>${photographers.filter(p=>p.isActive).map(p=>`<option value="${esc(p.userName)}">${esc(studioFirstName(p))}</option>`).join('')}</select></label><div class="row"><label>Call time<input id="shootStart" type="datetime-local" required></label><label>Planned end<input id="shootEnd" type="datetime-local" required></label></div><button type="button" class="action" data-check-workday>Check photographer availability</button><p data-workday-availability role="status"></p><label>Location<input id="shootLocation"></label><label>Rooms / equipment / vehicles<select id="shootResources" multiple size="6">${equipment.map(r=>`<option value="${r.id}">${esc(r.resourceType)} · ${esc(r.name)}</option>`).join('')}</select></label><label>Coverage notes<textarea id="shootCoverage"></textarea></label><label>Equipment notes<textarea id="shootEquipment"></textarea></label><div class="dialog-actions"><button type="button" onclick="shootDialog.close()">Cancel</button><button type="submit" class="primary">Schedule</button></div></form></dialog>`;
}

function openActivityForm(jobId) { if(jobId) activityJob.value=jobId; activityDialog.showModal(); }
let editingShootId=null;
function openShootForm(jobId) { editingShootId=null;shootJob.disabled=false; const start=new Date(Date.now()+3600000); start.setMinutes(0,0,0); const end=new Date(start.getTime()+2*3600000); const local=d=>new Date(d.getTime()-d.getTimezoneOffset()*60000).toISOString().slice(0,16); shootStart.value=local(start); shootEnd.value=local(end); if(jobId) shootJob.value=jobId; shootDialog.showModal(); }

async function createOperationalActivity(event) {
    event.preventDefault();
    await json(`/api/jobs/${activityJob.value}/operations/activities`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({activityType:activityType.value,title:activityTitle.value.trim(),description:activityDescription.value.trim()||null,status:'NotStarted',priority:activityPriority.value,assignedTo:activityAssigned.value.trim()||null,scheduledAtUtc:activityScheduled.value?new Date(activityScheduled.value).toISOString():null,dueAtUtc:activityDue.value?new Date(activityDue.value).toISOString():null,location:activityLocation.value.trim()||null,blockedReason:null,handoffTo:activityHandoff.value.trim()||null})});
    activityDialog.close(); await showProduction();
}

async function createShootSession(event) {
    event.preventDefault();
    const resourceIds=Array.from(shootResources.selectedOptions).map(o=>o.value);
    await json(`/api/jobs/${shootJob.value}/operations/shoots`+(editingShootId?`/${editingShootId}/schedule`:''),{method:editingShootId?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({photographerUserName:shootPhotographer.value,assistantUserName:shootAssistant.value||null,callTimeUtc:new Date(shootStart.value).toISOString(),plannedEndUtc:new Date(shootEnd.value).toISOString(),location:shootLocation.value.trim()||null,coverageNotes:shootCoverage.value.trim()||null,equipmentNotes:shootEquipment.value.trim()||null,resourceIds})});
    shootDialog.close();if(document.body.dataset.studioView==='production')await showProduction();else await openWorkspaceJob(shootJob.value);
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



async function openEmbeddedShootSchedule(jobId,shootId=null){
 const [job,photographers,resources,shoots,entries,access]=await Promise.all([json(`/api/jobs/${jobId}`),json('/api/photographers'),json('/api/schedule/resources'),json(`/api/operations/shoots?jobId=${jobId}`),json(`/api/schedule/entries?jobId=${jobId}`),json(`/api/jobs/${jobId}/access`)]);
 const p=access.permissions||access;if(!p.manageSchedule&&!p.assignStaff)throw Error('Scheduling permission is required.');
 document.getElementById('shootDialog')?.remove();
 const holder=document.createElement('div');holder.innerHTML=buildShootDialog([job],photographers,resources);document.body.appendChild(holder.firstElementChild);
 const dialog=document.getElementById('shootDialog');bindEmbeddedShootAvailability();document.getElementById('shootForm').onsubmit=async event=>{try{await createShootSession(event);}catch(error){let alertBox=dialog.querySelector('[role=alert]');if(!alertBox){alertBox=document.createElement('p');alertBox.setAttribute('role','alert');dialog.querySelector('form').appendChild(alertBox);}alertBox.textContent=error.message;}};
 dialog.addEventListener('close',()=>dialog.remove(),{once:true});openShootForm(jobId);
 if(shootId){
  const shoot=shoots.find(s=>s.id===shootId);if(!shoot){dialog.close();throw Error('Shoot no longer exists.');}
  editingShootId=shootId;shootJob.disabled=true;shootPhotographer.value=shoot.photographerUserName;shootAssistant.value=shoot.assistantUserName||'';
  const local=value=>{const d=new Date(value);return new Date(d.getTime()-d.getTimezoneOffset()*60000).toISOString().slice(0,16);};
  shootStart.value=local(shoot.callTimeUtc);shootEnd.value=local(shoot.plannedEndUtc);shootLocation.value=shoot.location||'';shootCoverage.value=shoot.coverageNotes||'';shootEquipment.value=shoot.equipmentNotes||'';
  const ids=entries.find(e=>e.id===shoot.scheduleEntryId)?.resourceIds||[];Array.from(shootResources.options).forEach(o=>o.selected=ids.includes(o.value));
  dialog.querySelector('h2').textContent='Reschedule / Change photographer';dialog.querySelector('[type=submit]').textContent='Save schedule';
 }
}

async function openProductionItems(activity){
 const dialog=studioDialog(activity.title+' — Production / QC','<p>Loading approved production items…</p>');
 const host=dialog.querySelector('.studio-dialog-body');
 async function load(after=''){
  const [data,orders]=await Promise.all([json(`/api/jobs/${activity.jobId}/production/${activity.id}/items`+(after?'?after='+after:'')),json(`/api/jobs/${activity.jobId}/production/${activity.id}/order-lines`)]);if(!dialog.isConnected)return;
  host.innerHTML=`${orders.filter(l=>!l.sourceVersionId).map(l=>`<article class="task-card"><strong>${esc(l.description)} · ${l.quantity} purchased</strong><p>Approved source required</p><button data-order-source="${l.id}">Choose approved image</button></article>`).join('')}<p>${data.completed}/${data.quantity} units produced · ${data.passed}/${data.total} items passed QC · ${data.rejected} rejected</p><progress max="${Math.max(1,data.quantity)}" value="${data.completed}"></progress>${data.items.map(i=>`<article class="task-card"><a href="/api/jobs/${activity.jobId}/versions/by-id/${i.sourceVersionId}/content">${esc(i.fileName)} · Approved v${i.versionNumber}</a><small>${esc(i.productCode)} · ${i.completedQuantity}/${i.quantity} · ${esc(i.qcState)}</small>${i.refundedProducedQuantity?`<p>${i.refundedProducedQuantity} produced units refunded — review stock / waste.</p>`:''}${i.qcReason?`<p>${esc(i.qcReason)}</p>`:''}<button ${i.qcState==='Cancelled'?'disabled':''} data-qc-pass="${i.id}">Pass QC</button><button ${i.qcState==='Cancelled'?'disabled':''} data-qc-reject="${i.id}">Reject / Rework</button></article>`).join('')||'<p>No approved items yet. Approve the job media to create its production items.</p>'}${data.items.length===100?'<button data-next-items>Next items</button>':''}${after?'<button data-first-items>First items</button>':''}<p role="alert"></p>`;
  host.querySelectorAll('[data-order-source]').forEach(b=>b.onclick=async()=>{
   const picker=studioDialog('Choose approved source','<p>Loading approved images…</p>'),body=picker.querySelector('.studio-dialog-body');
   async function page(cursor=''){
    const media=await json(`/api/jobs/${activity.jobId}/media?limit=100`+(cursor?'&after='+cursor:''));
    body.innerHTML=media.assets.filter(a=>a.approvedVersionId).map(a=>`<button data-order-asset="${a.id}">${esc(a.approvedFileName||a.fileName)} · approved v${a.approvedVersionNumber||'?'}</button>`).join('')+'<p role="alert"></p>'+(media.hasMore?'<button data-more-source>Next images</button>':'');
    body.querySelectorAll('[data-order-asset]').forEach(button=>button.onclick=async()=>{try{await json(`/api/jobs/${activity.jobId}/production/${activity.id}/order-lines/${b.dataset.orderSource}/source`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({assetId:button.dataset.orderAsset})});picker.close();await load(after);}catch(e){body.querySelector('[role=alert]').textContent=e.message;}});
    const next=body.querySelector('[data-more-source]');if(next)next.onclick=()=>page(media.assets.at(-1).id).catch(e=>body.querySelector('[role=alert]').textContent=e.message);
   }try{await page();}catch(e){body.textContent=e.message;}
  });
  async function save(id,state){const item=data.items.find(i=>i.id===id);const reason=state==='Rejected'?prompt('Rejection / rework reason'):null;if(state==='Rejected'&&!reason)return;try{await json(`/api/jobs/${activity.jobId}/production/${activity.id}/items/${id}`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({completedQuantity:state==='Passed'?item.quantity:item.completedQuantity,state,reason})});await load(after);}catch(error){host.querySelector('[role=alert]').textContent=error.message;}}
  host.querySelectorAll('[data-qc-pass]').forEach(b=>b.onclick=()=>save(b.dataset.qcPass,'Passed'));host.querySelectorAll('[data-qc-reject]').forEach(b=>b.onclick=()=>save(b.dataset.qcReject,'Rejected'));
  const next=host.querySelector('[data-next-items]');if(next)next.onclick=()=>load(data.items.map(i=>i.id).sort().at(-1)).catch(e=>host.querySelector('[role=alert]').textContent=e.message);
  const first=host.querySelector('[data-first-items]');if(first)first.onclick=()=>load().catch(e=>host.querySelector('[role=alert]').textContent=e.message);
 }
 try{await load();}catch(error){host.textContent=error.message;}
 dialog.addEventListener('close',()=>showProduction().catch(e=>alert(e.message)),{once:true});
}

function bindEmbeddedShootAvailability(){
 const dialog=document.getElementById('shootDialog'),button=dialog?.querySelector('[data-check-workday]');if(!button)return;
 button.onclick=async()=>{const target=dialog.querySelector('[data-workday-availability]');button.disabled=true;try{
  const start=new Date(shootStart.value),end=new Date(shootEnd.value);if(!Number.isFinite(+start)||!Number.isFinite(+end)||end<=start)throw Error('Choose a start and end time first.');
  const states=await json(`/api/jobs/${shootJob.value}/scheduling/availability?from=${encodeURIComponent(start.toISOString())}&to=${encodeURIComponent(end.toISOString())}`+(editingShootId?'&shootId='+editingShootId:''));
  const selected=shootPhotographer.value;Array.from(shootPhotographer.options).forEach(option=>{const person=states.find(p=>p.userName===option.value);if(person)option.textContent=studioFirstName(person.displayName)+' · '+(person.available?'Available':'Unavailable');});shootPhotographer.value=selected;
  const person=states.find(p=>p.userName===selected);target.textContent=person?(person.available?'Photographer available for this slot.':'Photographer has leave or an overlapping assignment. Choose another person or time.'):'Choose an available photographer. Availability is checked again when saving.';
 }catch(error){target.textContent=error.message;}finally{button.disabled=false;}};
}
