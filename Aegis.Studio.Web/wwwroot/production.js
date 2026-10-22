let productionSearch='',productionPriority='',productionWorkloadFilter='',productionWorkloadJobIds=null;
let operationalActivityCache = new Map();
let shootCache = new Map();
let operationalJobs = new Map();
let photographerCache = [];
let scheduleResourceCache = [];
let productionSelectedJob = '';

let productionView='queue';
let productionQueueScope='mine',productionStageFilter='',productionWorkspaceFilter='',productionAssigneeFilter='',productionDueFilter='';
async function createOperationalActivity(event) {
    event.preventDefault();
    await StudioWorkflow.command(activityJob.value,{action:'task',kind:activityType.value,title:activityTitle.value.trim(),description:activityDescription.value.trim()||null,priority:activityPriority.value,userName:activityAssigned.value.trim()||null,scheduledAtUtc:activityScheduled.value?new Date(activityScheduled.value).toISOString():null,dueAtUtc:activityDue.value?new Date(activityDue.value).toISOString():null,location:activityLocation.value.trim()||null,handoffTo:activityHandoff.value.trim()||null},event.currentTarget);
    activityDialog.close(); await showProduction();
}

async function createShootSession(event) {
    event.preventDefault();
    const resourceIds=Array.from(shootResources.selectedOptions).map(o=>o.value);
    await json(`/api/jobs/${shootJob.value}/operations/shoots`+(editingShootId?`/${editingShootId}/schedule`:''),{method:editingShootId?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({photographerUserName:shootPhotographer.value,assistantUserName:shootAssistant.value||null,callTimeUtc:new Date(shootStart.value).toISOString(),plannedEndUtc:new Date(shootEnd.value).toISOString(),location:shootLocation.value.trim()||null,coverageNotes:shootCoverage.value.trim()||null,equipmentNotes:shootEquipment.value.trim()||null,resourceIds})});
    shootDialog.close();if(document.body.dataset.studioView==='production')await showProduction();else await openWorkspaceJob(shootJob.value);
}

async function setOperationalActivityStatus(id,status,blockedReason) { const item=operationalActivityCache.get(id); if(!item)return; await StudioWorkflow.command(item.jobId,{action:'task-progress',taskId:id,status,blockedReason:blockedReason||null},item); await showProduction(); }
async function blockOperationalActivity(id) { const reason=prompt('Why is this activity blocked?'); if(reason) await setOperationalActivityStatus(id,'Blocked',reason); }
async function updateShoot(id,action) { const shoot=shootCache.get(id);if(shoot)await openPhotographerReport(shoot.jobId,shoot.scheduleEntryId); }

async function recordShootResults(id) {
    const item=shootCache.get(id); if(!item)return;
    const reshoot=confirm('Is a reshoot required?'); const coverage=prompt('Coverage / field notes',item.coverageNotes||''); const equipment=prompt('Equipment notes',item.equipmentNotes||'');
    await json('/api/operations/shoots/'+id,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'update',imageCount:null,backupConfirmed:null,reshootRequired:reshoot,coverageNotes:coverage,equipmentNotes:equipment,handoffTo:null})}); await showProduction();
}

async function handoffShoot(id) { const handoffTo=prompt('Hand off verified files to which user/team?'); if(!handoffTo)return; try{ await json('/api/operations/shoots/'+id,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'handoff',imageCount:null,backupConfirmed:null,reshootRequired:null,coverageNotes:null,equipmentNotes:null,handoffTo})}); await showProduction(); }catch(error){ alert(error.message||error); } }

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
const productionNavigationButton=document.querySelector('.brand-button[data-view="production"]'); if(productionNavigationButton) productionNavigationButton.onclick=()=>window.studioNavigate('production');
// The legacy activity queue is retired as a page; all Production navigation uses the canonical board.
window.showProduction=()=>window.studioNavigate('production');



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
  const [data,orders,access]=await Promise.all([json(`/api/jobs/${activity.jobId}/production/${activity.id}/items`+(after?'?after='+after:'')),json(`/api/jobs/${activity.jobId}/production/${activity.id}/order-lines`),json(`/api/jobs/${activity.jobId}/access`)]);if(!dialog.isConnected)return;const permissions=access.permissions||access;const canManageProduction=!!permissions.manageProduction;
  host.innerHTML=`${orders.filter(l=>canManageProduction&&!l.sourceVersionId).map(l=>`<article class="task-card"><strong>${esc(l.description)} · ${l.quantity} purchased</strong><p>Approved source required</p><button data-order-source="${l.id}">Choose approved image</button></article>`).join('')}<p>${data.completed}/${data.quantity} units produced · ${data.passed}/${data.total} items passed QC · ${data.rejected} rejected</p><progress max="${Math.max(1,data.quantity)}" value="${data.completed}"></progress>${data.items.map(i=>`<article class="task-card"><a href="/api/jobs/${activity.jobId}/versions/by-id/${i.sourceVersionId}/content">${esc(i.fileName)} · Approved v${i.versionNumber}</a><small>${esc(i.productCode)} · ${i.completedQuantity}/${i.quantity} · ${esc(i.qcState)}</small>${i.refundedProducedQuantity?`<p>${i.refundedProducedQuantity} produced units refunded — review stock / waste.</p>`:''}${i.qcReason?`<p>${esc(i.qcReason)}</p>`:''}${canManageProduction?`<button ${i.qcState==='Cancelled'?'disabled':''} data-qc-pass="${i.id}">Pass QC</button><button ${i.qcState==='Cancelled'?'disabled':''} data-qc-reject="${i.id}">Reject / Rework</button>`:i.qcState==='Pending'&&canManageProduction?`<button data-produced="${i.id}">Record produced quantity</button>`:''}</article>`).join('')||'<p>No approved items yet. Approve the job media to create its production items.</p>'}${data.items.length===100?'<button data-next-items>Next items</button>':''}${after?'<button data-first-items>First items</button>':''}<p role="alert"></p>`;
  host.querySelectorAll('[data-order-source]').forEach(b=>b.onclick=async()=>{
   const picker=studioDialog('Choose approved source','<p>Loading approved images…</p>'),body=picker.querySelector('.studio-dialog-body');
   async function page(cursor=''){
    const media=await json(`/api/jobs/${activity.jobId}/media?limit=100`+(cursor?'&after='+cursor:''));
    body.innerHTML=media.assets.filter(a=>a.approvedVersionId).map(a=>`<button data-order-asset="${a.id}">${esc(a.approvedFileName||a.fileName)} · approved v${a.approvedVersionNumber||'?'}</button>`).join('')+'<p role="alert"></p>'+(media.hasMore?'<button data-more-source>Next images</button>':'');
    body.querySelectorAll('[data-order-asset]').forEach(button=>button.onclick=async()=>{try{await json(`/api/jobs/${activity.jobId}/production/${activity.id}/order-lines/${b.dataset.orderSource}/source`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({assetId:button.dataset.orderAsset})});picker.close();await load(after);}catch(e){body.querySelector('[role=alert]').textContent=e.message;}});
    const next=body.querySelector('[data-more-source]');if(next)next.onclick=()=>page(media.assets.at(-1).id).catch(e=>body.querySelector('[role=alert]').textContent=e.message);
   }try{await page();}catch(e){body.textContent=e.message;}
  });
  async function save(id,state){const item=data.items.find(i=>i.id===id);const entered=state==='Pending'?prompt('Completed quantity',String(item.completedQuantity)):null;if(state==='Pending'&&entered===null)return;const amount=state==='Pending'?Number(entered):state==='Passed'?item.quantity:item.completedQuantity;if(!Number.isInteger(amount)||amount<0||amount>item.quantity)throw Error('Choose a quantity within the ordered amount.');const reason=state==='Rejected'?prompt('Rejection / rework reason'):null;if(state==='Rejected'&&!reason)return;try{await json(`/api/jobs/${activity.jobId}/production/${activity.id}/items/${id}`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({completedQuantity:amount,state,reason})});await load(after);}catch(error){host.querySelector('[role=alert]').textContent=error.message;}}
  host.querySelectorAll('[data-produced]').forEach(b=>b.onclick=()=>save(b.dataset.produced,'Pending').catch(e=>host.querySelector('[role=alert]').textContent=e.message));
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


async function openPhotographerReport(jobId,assignmentId=null){
 const rows=(await json(`/api/jobs/${jobId}/photographer-reports`)).filter(x=>!assignmentId||x.assignmentId===assignmentId);
 if(!rows.length)throw Error('No current photographer assignments are available for reporting.');
 const d=studioDialog('Record photographer update',`<form class="subject-form"><label>Assignment / photographer<select name="person">${rows.map((x,i)=>`<option value="${i}">${esc(x.title)} · ${esc(x.displayName)} · ${esc(x.status)}</option>`).join('')}</select></label><label>Update<select name="kind"><option value="0">Accepted</option><option value="10">Declined</option><option value="1">Arrived</option><option value="2">Started</option><option value="3">Completed</option><option value="5">Note</option><option value="6">Problem</option></select></label><label>Actual event time<input name="occurred" type="datetime-local" required></label><label>Photographer note<textarea name="note" maxlength="4000"></textarea></label><label>Why this is recorded manually / source<textarea name="reason" maxlength="4000" required></textarea></label><button class="primary">Record update</button><p role="alert"></p></form>`);
 const f=d.querySelector('form');const now=new Date();f.elements.occurred.value=new Date(now.getTime()-now.getTimezoneOffset()*60000).toISOString().slice(0,16);
 let original=null;
 f.onsubmit=async e=>{e.preventDefault();const button=f.querySelector('button.primary');button.disabled=true;try{if(!original){const row=rows[Number(f.elements.person.value)];original={photographer:row.userName,reason:f.elements.reason.value,operation:{operationId:crypto.randomUUID(),kind:Number(f.elements.kind.value),assignmentId:row.assignmentId,occurredAtUtc:new Date(f.elements.occurred.value).toISOString(),deviceId:'StudioManual',text:f.elements.note.value||null,category:null,location:null,expectedVersion:row.version}};f.querySelectorAll('input,textarea,select').forEach(x=>x.disabled=true);}await json(`/api/jobs/${jobId}/photographer-reports`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(original)});d.close();await openWorkspaceJob(jobId);}catch(error){f.querySelector('[role=alert]').textContent=error.message+' Refresh assignment facts before recording a new decision if this report conflicts.';button.disabled=false;button.textContent='Retry original report';}};
}

