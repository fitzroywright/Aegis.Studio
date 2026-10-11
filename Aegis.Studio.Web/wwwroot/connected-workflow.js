/* Connected workflow adds Job controls while preserving My Work and its layout. */
(function(root){
 'use strict';
 const esc=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const send=(url,options)=>json(url,options);
 const actions={Pending:[['confirm','Confirm booking']], 'Awaiting Upload':[['submit-source','Submit for Production Review']], 'Awaiting Production Review':[['accept-source','Accept source material'],['request-changes','Request source corrections']], 'Active Production':[['submit-finished','Submit finished work']], 'Awaiting Approval':[['approve-finished','Approve finished work'],['request-changes','Request production corrections']], 'Ready for Delivery':[['deliver','Record delivery']], Delivered:[['close','Complete Job']]};
 const elapsed=hours=>hours==null?'Timing unavailable':hours<1?Math.floor(hours*60)+' minutes':hours<24?hours.toFixed(1)+' hours':(hours/24).toFixed(1)+' days';
 function crewPayload(form){
  return [...form.querySelectorAll('[data-crew-row]')].map(row=>({userName:row.querySelector('[name=photographer]').value,role:row.querySelector('[name=role]').value,notes:row.querySelector('[name=notes]').value,start:row.querySelector('[name=attendanceStart]').value?new Date(row.querySelector('[name=attendanceStart]').value).toISOString():null,end:row.querySelector('[name=attendanceEnd]').value?new Date(row.querySelector('[name=attendanceEnd]').value).toISOString():null}));
 }
 function dialog(title,content){
  const d=document.createElement('dialog');d.className='workflow-dialog';d.innerHTML=`<form><header><h2>${esc(title)}</h2><button type="button" data-dismiss aria-label="Close">×</button></header>${content}<p role="alert"></p><div class="dialog-actions"><button type="button" data-dismiss>Cancel</button><button type="submit" class="primary">Save</button></div></form>`;
  document.body.append(d);d.querySelectorAll('[data-dismiss]').forEach(b=>b.onclick=()=>d.close());d.addEventListener('close',()=>d.remove());d.querySelectorAll('select[name=person]').forEach(s=>personPicker(s));d.showModal();return d;
 }
 function options(people,current){return '<option value="">Choose employee</option>'+people.map(p=>`<option value="${esc(p.userName)}" ${String(p.userName).toLowerCase()===String(current||'').toLowerCase()?'selected':''}>${esc(p.displayName||p.userName)} · ${esc(p.userName)}</option>`).join('');}
 function personPicker(select,photographersOnly=false){
  const search=document.createElement('input');search.type='search';search.placeholder=photographersOnly?'Search Studio photographers…':'Search Studio people…';search.setAttribute('aria-label',search.placeholder);select.before(search);
  let request=0,timer;
  search.oninput=()=>{clearTimeout(timer);const version=++request;timer=setTimeout(async()=>{
   try{const rows=await send('/api/studio-employees?assignable=true&limit=25&q='+encodeURIComponent(search.value)+(photographersOnly?'&photographersOnly=true':''));if(!select.isConnected||version!==request)return;const value=select.value;select.innerHTML=options(rows,value);}
   catch(e){const out=select.closest('form')?.querySelector('[role=alert]');if(out)out.textContent=e.message;}
  },250);};
 }
 function localTime(value){if(!value)return '';const d=new Date(value);return new Date(d-d.getTimezoneOffset()*60000).toISOString().slice(0,16);}
 async function command(jobId,fields,holder={},expectedRevision){
  const facts=JSON.stringify(fields);
  if(!holder.__workflowCommand||holder.__workflowCommand.facts!==facts){
   const revision=expectedRevision??(await send(`/api/jobs/${jobId}/connected-workflow`)).data.revision;
   holder.__workflowCommand={facts,body:JSON.stringify({...fields,expectedRevision:revision,operationId:crypto.randomUUID()})};
  }
  const result=await send(`/api/jobs/${jobId}/connected-workflow`,{method:'POST',headers:{'Content-Type':'application/json'},body:holder.__workflowCommand.body});
  delete holder.__workflowCommand;root.dispatchEvent(new CustomEvent('studio-workflow-changed',{detail:{jobId}}));return result;
 }
 const productionTask=kind=>['Cull','Edit','Retouch','Proof','Print','QC','Production','Export','FileIngest','Backup','Archive'].includes(kind);
 const canReportTask=(view,task)=>Boolean(view.canReview||String(task.assignedTo||'').toLowerCase()===String(view.userName||'').toLowerCase()||(view.canManage&&!productionTask(task.activityType)));
 function nextAction(view){
  const d=view.data||{},active=!['Completed','Cancelled'].includes(d.stage);
  if(!active)return {label:'Review workflow',selector:null};
  const a=d.ownerAssignment,own=a&&String(d.owner||'').toLowerCase()===String(a.assigned_to||'').toLowerCase()&&String(a.assigned_to||'').toLowerCase()===String(view.userName||'').toLowerCase();
  if(a?.status==='Assigned')return own?{label:'Accept ownership',selector:'[data-wf-owner-response="accept-owner"]'}:{label:'Awaiting owner acceptance',selector:null};
  if(d.stage==='Pending')return {label:'Confirm booking',selector:view.canFrontDesk?'[data-wf-action="confirm"]':null};
  if(!d.owner||!a||['Declined','Superseded'].includes(a.status))return {label:'Assign owner',selector:view.canAssignOwner?'[data-wf-owner]':null};
  if(d.stage==='Needs Photographer Assignment')return {label:'Assign photographer',selector:view.canManage&&a.status==='Accepted'?'[data-wf-session]':null};
  if(d.stage==='Awaiting Scheduling')return {label:'Schedule session',selector:view.canManage&&a.status==='Accepted'?'[data-wf-session]':null};
  if(['Scheduled','Photography In Progress'].includes(d.stage))return {label:'Record photographer status',selector:view.canManage||(d.sessions||[]).some(s=>(s.crew||[]).some(c=>String(c.user_name).toLowerCase()===String(view.userName).toLowerCase()))?'[data-wf-report]':null};
  if(d.stage==='Changes Requested')return d.correctionKind==='Production'?{label:'Resume production',selector:view.canReview?'[data-wf-action="resume-production"]':null}:{label:'Resubmit corrected source media',selector:view.canManage?'[data-wf-action="submit-source"]':null};
  const step=(actions[d.stage]||[])[0];
  const allowed=step&&(step[0]==='confirm'?view.canFrontDesk:['accept-source','approve-finished','request-changes','submit-finished'].includes(step[0])?view.canReview:view.canManage);
  return {label:step?.[1]||'Review workflow',selector:allowed?'[data-wf-action="'+step[0]+'"]':null};
 }
 async function open(jobId,selector=null){
  const d=studioDialog('Assignments & Workflow','<div data-workflow-host></div>'),host=d.querySelector('[data-workflow-host]');
  d.addEventListener('close',()=>{if(host.__studioWorkflowClock)clearInterval(host.__studioWorkflowClock);},{once:true});
  try{await render(host,jobId,()=>d.isConnected);if(selector)host.querySelector(selector)?.click();}catch(e){if(d.isConnected)host.textContent=e.message;}
  return d;
 }
 async function render(host,jobId,isCurrent=()=>true){
  const view=await send(`/api/jobs/${jobId}/connected-workflow`);if(!isCurrent())return;
  const data=view.data,managed=view.canManage,review=view.canReview;
  // A single active clock per Job panel; clear it before replacing its DOM.
  if(host.__studioWorkflowClock){clearInterval(host.__studioWorkflowClock);host.__studioWorkflowClock=null;}

  let available=actions[data.stage]||[];
  available=available.filter(([key])=>key==='confirm'?view.canFrontDesk:['accept-source','approve-finished','request-changes','submit-finished'].includes(key)?review:managed);
  if(data.stage==='Changes Requested')available=data.correctionKind==='Production'?(review?[['resume-production','Resume production']]:[]):managed?[['submit-source','Resubmit corrected source media']]:[];
  const active=!['Completed','Cancelled'].includes(data.stage);
  const next=nextAction(view),ownerStatus=data.ownerAssignment?.status||'Unassigned';
  const ownAssignment=active&&data.ownerAssignment&&String(data.owner||'').toLowerCase()===String(data.ownerAssignment.assigned_to).toLowerCase()&&String(data.ownerAssignment.assigned_to).toLowerCase()===String(view.userName).toLowerCase()&&['Assigned','Accepted'].includes(ownerStatus);
  const canSchedule=managed&&ownerStatus==='Accepted'&&['Needs Assignment','Needs Photographer Assignment','Awaiting Scheduling','Scheduled'].includes(data.stage);
  const button=(attribute,label)=>`<button type="button" class="action" ${attribute}>${esc(label)}</button>`;
  const explanation=ownerStatus==='Assigned'?`Waiting for ${data.owner||'the assigned owner'} to accept responsibility.`:data.stage==='Pending'?'Confirm after the deposit requirement or an approved override is satisfied.':!data.owner||['Unassigned','Declined','Superseded'].includes(ownerStatus)?'Assign a Job Owner to move this job forward.':data.stage==='Needs Photographer Assignment'?'Choose the photography crew for this job.':data.stage==='Awaiting Scheduling'?'Set the session time and location.':['Scheduled','Photography In Progress'].includes(data.stage)?'Record attendance and progress for the assigned photography crew.':active?'Review the current work before advancing the job.':'This job is '+data.stage.toLowerCase()+'.';
  host.innerHTML=`<section class="workflow-panel">
   <header class="workflow-heading"><span class="studio-status-pill">${esc(data.stage)}</span><small data-wf-clock data-start="${esc(data.enteredAt||'')}">Timing unavailable</small></header>
   ${data.blockedReason?`<div class="workflow-blocker" role="status"><strong>Blocked</strong><span>${esc(data.blockedReason)}</span></div>`:''}
   <div class="workflow-next"><div><small>Next step</small><h3 data-wf-next-title>${esc(next.label)}</h3><p>${esc(explanation)}</p></div><div data-wf-next-action></div></div>
   <article class="workflow-owner"><div><small>Job Owner</small><strong>${esc(data.owner||'Unassigned')}</strong><span>${esc(ownerStatus)}</span></div><div class="workflow-actions">${ownAssignment&&ownerStatus==='Assigned'?button('data-wf-owner-response="accept-owner"','Accept ownership'):''}${view.canAssignOwner&&active?button('data-wf-owner',data.owner?'Reassign owner':'Assign owner'):''}</div></article>
   <div class="workflow-section-heading"><h3>Photography sessions</h3>${canSchedule?button('data-wf-session=""',data.sessions.length?'Add session':'Assign photographer'):''}</div>
   <div class="workflow-sessions">${data.sessions.map(s=>`<article><header><strong>${esc(s.title)}</strong><span>${esc(s.shoot?.status||'Not scheduled')}${s.required?'':' · Waived'}</span></header><p>${s.shoot?esc(new Date(s.shoot.call_time_utc).toLocaleString())+' – '+esc(new Date(s.shoot.planned_end_utc).toLocaleString()):'Date and time to be scheduled'}</p><p class="workflow-location">${esc(s.shoot?.location||'Location to be confirmed')}</p><ul class="workflow-crew">${s.crew.map(c=>`<li><strong>${esc(c.user_name)}</strong><span>${esc(c.role)} · ${c.ready?'Material ready':c.waiver_reason?'Waived':'Media pending'}</span>${c.waiver_reason?`<small>${esc(c.waiver_reason)}</small>`:''}</li>`).join('')||'<li>No photographers assigned</li>'}</ul><div class="workflow-actions">${canSchedule&&!s.shoot?.checked_in_at_utc?button(`data-wf-session="${esc(s.id)}"`,s.shoot?'Edit crew / schedule':'Schedule session'):''}${s.shoot&&active&&(managed||s.crew.some(c=>c.user_name.toLowerCase()===view.userName.toLowerCase()))?button(`data-wf-report="${esc(s.shoot.schedule_entry_id)}"`,'Photographer update'):''}${s.crew.some(c=>c.user_name.toLowerCase()===view.userName.toLowerCase())&&['Awaiting Upload','Changes Requested'].includes(data.stage)?button(`data-wf-ready="${esc(s.id)}"`,'My material is ready'):''}${managed&&['Awaiting Upload','Changes Requested'].includes(data.stage)?button(`data-wf-upload="${esc(s.id)}"`,'Record upload batch'):''}</div>${managed&&['Awaiting Upload','Changes Requested'].includes(data.stage)?`<details><summary>Contribution options</summary>${button(`data-wf-waive="${esc(s.id)}"`,'Waive missing contribution')}</details>`:''}</article>`).join('')||`<p class="workflow-empty">No sessions yet. ${ownerStatus==='Accepted'?'Add a photographer to start planning.':'The Job Owner must accept ownership before crew and scheduling can be added.'}</p>`}</div>
   <div class="workflow-roster" data-roster-counts><div>${Object.entries(data.rosterCounts||{}).map(([state,n])=>`<span><strong>${esc(n)}</strong> ${esc(state)}</span>`).join('')}<span><strong>${(data.outstandingRoster||[]).length}</strong> outstanding</span></div>${managed&&ownerStatus==='Accepted'&&['Scheduled','Photography In Progress','Awaiting Upload'].includes(data.stage)&&(data.outstandingRoster||[]).length?button('data-wf-session="" data-wf-remainder','Reschedule remainder'):''}</div>
   <div class="workflow-secondary">
    <details data-wf-more><summary>More actions</summary><div class="workflow-actions">
     ${ownAssignment?button('data-wf-owner-response="decline-owner"','Decline ownership'):''}
     ${available.map(([key,label])=>button(`data-wf-action="${key}"`,label)).join('')}
     ${managed&&["Active Production","Awaiting Approval","Ready for Delivery","Delivered"].includes(data.stage)?button('data-wf-deliverables','Deliverables'):''}
     ${managed&&active?button('data-wf-task','Create task')+button('data-wf-block',data.blockedReason?'Update / clear block':'Record a blocker'):''}
     ${view.canFrontDesk&&data.stage==='Pending'?button('data-wf-override','Request deposit override')+button('data-wf-claim','Claim booking')+button('data-wf-release','Release claim')+button('data-wf-reassign','Reassign claim'):''}
     ${(managed||view.canFrontDesk)&&active?button('data-wf-transfer','Transfer workspace')+button('data-wf-transfer-requests','Transfer approvals'):''}
     ${view.canFinance?button('data-wf-finance','Finance credit / release'):''}
     ${view.canAssignOwner&&active?button('data-wf-action="cancel"','Cancel job'):''}${view.canReopen&&!active?button('data-wf-reopen','Reopen job'):''}
     ${button('data-wf-request','Request assignment')+button('data-wf-requests','Assignment requests')}
    </div><nav class="workflow-actions" aria-label="Related pages">${button('data-wf-nav="today"','Today')+button('data-wf-nav="jobs"','Jobs')+button('data-wf-workspace','Workspace')+button('data-wf-nav="production"','Production')}</nav></details>
    <details class="workflow-history"><summary>History & reports${(data.fieldConflicts||[]).length?` · ${(data.fieldConflicts||[]).length} sync conflicts`:''}</summary>
   <details><summary>Owner assignment history</summary>${(data.ownerAssignmentHistory||[]).map(a=>`<p>${esc(a.assigned_to)} · ${esc(a.status)} · Assigned by ${esc(a.assigned_by)} · ${esc(a.response_reason||'')}</p>`).join('')||'<p>No ownership responses recorded.</p>'}</details>
   <details><summary>Customer selections</summary><div data-wf-selections></div></details>
   <details><summary>Sync conflict history (${(data.fieldConflicts||[]).length})</summary>${(data.fieldConflicts||[]).map(r=>`<p role="status"><strong>${esc(r.user_name)} · ${esc(r.target_id)}</strong> · ${esc(r.report?.Status||r.report?.status)} · ${esc(r.message)} · ${esc(new Date(r.received_at_utc).toLocaleString())}</p>`).join('')||'<p>No roster sync conflicts recorded.</p>'}<p>Review current Studio facts with the photographer before resolving retained device reports and submitting a new decision.</p></details>
   <details><summary>Photographer reports</summary>${(data.photographerReports||[]).map(r=>`<article><strong>${esc(r.user_name)} · ${esc(r.kind)}</strong><span class="studio-status-pill">${esc(r.state)}</span><p>${esc(new Date(r.occurred_at_utc).toLocaleString())} · ${esc(r.text_value||r.category||'')}</p></article>`).join('')||'<p>No WorkDay reports received for this Job.</p>'}</details>
   <details><summary>Booking override history</summary>${(data.bookingOverrides||[]).map(r=>`<p><strong>${esc(r.status)}</strong> · ${esc(r.requested_by)} · ${esc(r.reason)}${r.decided_by?` · Decision: ${esc(r.decided_by)} · ${esc(r.decision_reason)} · ${esc(new Date(r.decided_at_utc).toLocaleString())}`:""}</p>`).join('')||'<p>No booking overrides.</p>'}</details><details><summary>Workflow and assignment history</summary><ol>${data.history.map(h=>`<li><strong>${esc(h.new_stage)}</strong> · ${h.entered_at_utc?esc(new Date(h.entered_at_utc).toLocaleString()):'Historical timing unavailable'}${h.entered_at_utc?` · ${elapsed((+(h.exited_at_utc?new Date(h.exited_at_utc):new Date())- +new Date(h.entered_at_utc))/3600000)}`:''} · ${esc(h.actor)}${h.reason?' · '+esc(h.reason):''}</li>`).join('')}</ol><p>Review submissions: ${data.submissions.map(s=>esc(s.kind)+' #'+s.revision+' '+esc(s.status)).join('; ')||'None'}</p></details></details></div><p role="alert" data-wf-error></p></section>`;
  // Move the real, permission-checked action so every entry point uses the same handler.
  const primary=next.selector?host.querySelector(next.selector):null;
  if(primary){primary.className='primary';primary.textContent=next.label;host.querySelector('[data-wf-next-action]').append(primary);host.querySelector('[data-wf-next-title]').hidden=true;}
  const customerSelections=host.querySelector('[data-wf-selections]');
  try{const selections=await send('/api/jobs/'+jobId+'/customer-selections');if(!isCurrent())return;customerSelections.innerHTML=selections.map(s=>`<p>${esc(new Date(s.submittedAt).toLocaleString())} · ${(s.versionIds||[]).map(id=>`<a href="/api/jobs/${jobId}/versions/by-id/${esc(id)}/preview" target="_blank" rel="noopener">${esc(id)}</a>`).join(', ')}</p>`).join('')||'<p>No customer selections submitted.</p>';}catch{customerSelections.textContent='Customer selections unavailable. Refresh to retry.';}
  const clock=host.querySelector('[data-wf-clock]');
  if(clock){
   const tick=()=>{
    if(!clock.isConnected||!isCurrent()){clearInterval(host.__studioWorkflowClock);host.__studioWorkflowClock=null;return;}
    const started=Date.parse(clock.dataset.start||'');
    clock.textContent=Number.isFinite(started)?'In stage: '+elapsed(Math.max(0,(Date.now()-started)/3600000)):'Timing unavailable';
   };
   tick();host.__studioWorkflowClock=setInterval(tick,60000);
  }
  const mutate=async(fields)=>{const facts=JSON.stringify(fields);const pending=host.__workflowCommands??=(new Map());if(!pending.has(facts))pending.set(facts,{});await command(jobId,fields,pending.get(facts),data.revision);pending.delete(facts);await render(host,jobId,isCurrent);};
  const fail=error=>{const output=host.querySelector('[data-wf-error]');if(output)output.textContent=error.message+' Your saved decisions are retained. Refresh this Job before retrying.';};
  const bind=(selector,fn)=>host.querySelectorAll(selector).forEach(b=>b.onclick=async()=>{b.disabled=true;try{await fn(b);}catch(e){fail(e);}finally{if(b.isConnected)b.disabled=false;}});
  bind('[data-wf-nav]',b=>root.studioNavigate(b.dataset.wfNav));bind('[data-wf-workspace]',()=>root.selectWorkspace?root.selectWorkspace(data.workspaceId):root.studioNavigate('workspaces'));
  bind('[data-wf-claim]',()=>mutate({action:'claim'}));
  bind('[data-wf-release]',()=>{const reason=prompt('Reason for releasing this claim (required for supervisor override):');return reason===null?undefined:mutate({action:'release-claim',reason});});
  bind('[data-wf-reassign]',async()=>{const people=await send('/api/studio-employees?assignable=true');const d=dialog('Reassign Front Desk claim',`<label>Employee<select name="person" required>${options(people)}</select></label><label>Reason<textarea name="reason" maxlength="4000" required></textarea></label>`);d.querySelector('form').onsubmit=async e=>{e.preventDefault();const f=e.currentTarget;try{await mutate({action:'reassign-claim',userName:f.elements.person.value,reason:f.elements.reason.value});d.close();}catch(err){f.querySelector('[role=alert]').textContent=err.message;}};});
  bind('[data-wf-owner-response]',b=>{const reason=b.dataset.wfOwnerResponse==='decline-owner'?prompt('Reason for declining ownership:'):'';return reason===null?undefined:mutate({action:b.dataset.wfOwnerResponse,reason});});
  bind('[data-wf-owner]',async()=>{
   const people=await send('/api/studio-employees?assignable=true');const d=dialog('Assign Job Owner',`<label>Owner<select name="person">${options(people,data.owner)}</select></label><label>Reason / instructions<textarea name="reason" maxlength="4000"></textarea></label>`);
   d.querySelector('form').onsubmit=async e=>{e.preventDefault();const f=e.currentTarget;try{await mutate({action:'owner',userName:f.elements.person.value||null,reason:f.elements.reason.value});d.close();}catch(err){f.querySelector('[role=alert]').textContent=err.message;}};
  });
  bind('[data-wf-session]',async b=>{
   const [people,resources,entries,photographers]=await Promise.all([send('/api/studio-employees?assignable=true&photographersOnly=true'),send('/api/schedule/resources'),send('/api/schedule/entries?jobId='+jobId),send('/api/photographers')]);const eligible=people.filter(p=>photographers.some(x=>x.isActive&&x.userName.toLowerCase()===p.userName.toLowerCase()));const session=data.sessions.find(s=>s.id===b.dataset.wfSession)||(!b.hasAttribute('data-wf-remainder')?data.sessions.find(s=>s.required&&!s.shoot):null);const selectedResources=entries.find(e=>e.id===session?.shoot?.schedule_entry_id)?.resourceIds||[];const extras=resources.filter(r=>r.isActive&&!photographers.some(p=>p.scheduleResourceId===r.id));
   const remainder=b.hasAttribute('data-wf-remainder');
   const d=dialog(remainder?'Reschedule remainder':'Assign Photographers / Schedule',`<label>Session<input name="title" maxlength="200" required value="${esc(session?.title||(remainder?'Photography remainder':'Photography'))}"></label>${remainder?`<fieldset><legend>Outstanding roster</legend>${(data.outstandingRoster||[]).map(x=>`<label><input type="checkbox" name="remainingSubject" value="${esc(x.id)}" checked> ${esc(x.name)} · ${esc(x.collection)} · ${esc(x.status)}</label>`).join('')}</fieldset>`:''}<div data-crew></div><button class="action" type="button" data-add-crew>Add photographer</button><label>Start (your local time)<input type="datetime-local" name="start" value="${localTime(session?.shoot?.call_time_utc)}"></label><label>End (your local time)<input type="datetime-local" name="end" value="${localTime(session?.shoot?.planned_end_utc)}"></label><label>Location<input name="location" maxlength="500" value="${esc(session?.shoot?.location||'')}"></label><label>Reason / notes<textarea name="reason" maxlength="4000"></textarea></label><label><input name="requiredSession" type="checkbox" ${session?.required!==false?'checked':''}> Required session</label><label>Additional resources<select name="resources" multiple>${extras.map(r=>`<option value="${r.id}" ${selectedResources.includes(r.id)?'selected':''}>${esc(r.name)}</option>`).join('')}</select></label>`);
   const f=d.querySelector('form'),rows=f.querySelector('[data-crew]');
   const add=(person={})=>{const row=document.createElement('div');row.dataset.crewRow='';row.className='workflow-crew-row';row.innerHTML=`<label>Photographer<select name="photographer" required>${options(eligible,person.user_name)}</select></label><label>Role<select name="role"><option value="Lead" ${person.role==='Lead'?'selected':''}>Lead</option><option value="Additional" ${person.role==='Additional'?'selected':''}>Additional</option></select></label><label>Notes<input name="notes" maxlength="1000" value="${esc(person.notes||'')}"></label><label>Attendance start<input type="datetime-local" name="attendanceStart" value="${localTime(person.attendance_start_utc)}"></label><label>Attendance end<input type="datetime-local" name="attendanceEnd" value="${localTime(person.attendance_end_utc)}"></label><button type="button" class="action" data-remove>Remove</button>`;rows.append(row);personPicker(row.querySelector('[name=photographer]'),true);row.querySelector('[data-remove]').onclick=()=>row.remove();};
   (session?.crew.length?session.crew:[{role:'Lead'}]).forEach(add);f.querySelector('[data-add-crew]').onclick=()=>add({role:'Additional'});
   f.onsubmit=async e=>{e.preventDefault();try{await mutate({action:remainder?'reschedule-remainder':'session',subjectIds:remainder?Array.from(f.querySelectorAll('[name=remainingSubject]:checked'),x=>x.value):null,sessionId:session?.id||null,title:f.elements.title.value,crew:crewPayload(f),start:f.elements.start.value?new Date(f.elements.start.value).toISOString():null,end:f.elements.end.value?new Date(f.elements.end.value).toISOString():null,location:f.elements.location.value,reason:f.elements.reason.value,required:f.elements.requiredSession.checked,resourceIds:Array.from(f.elements.resources.selectedOptions).map(o=>o.value)});d.close();}catch(err){f.querySelector('[role=alert]').textContent=err.message;}};
  });
  bind('[data-wf-task]',async()=>{
   const people=await send('/api/studio-employees?assignable=true');const d=dialog('Create / Assign Task',`<label>Title<input name="title" maxlength="200" required></label><label>Type<select name="kind">${(review?['Other','Cull','Edit','Retouch','Proof','Print','QC','Delivery']:['Other','Delivery']).map(x=>`<option>${x}</option>`).join('')}</select></label><label>Assignee<select name="person">${options(people)}</select></label><label>Instructions<textarea name="reason" maxlength="4000"></textarea></label>`);
   d.querySelector('form').onsubmit=async e=>{e.preventDefault();const f=e.currentTarget;try{await mutate({action:'task',title:f.elements.title.value,kind:f.elements.kind.value,userName:f.elements.person.value||null,reason:f.elements.reason.value});d.close();}catch(err){f.querySelector('[role=alert]').textContent=err.message;}};
  });
  bind('[data-wf-transfer]',async()=>{
   const w=await send('/api/frontdesk/workspaces');const d=dialog('Transfer Workspace',`<label>Destination<select name="workspace">${w.map(x=>`<option value="${x.id}">${esc(x.name)}</option>`).join('')}</select></label><label>Reason<textarea name="reason" required maxlength="4000"></textarea></label>`);d.querySelector('form').onsubmit=async e=>{e.preventDefault();const f=e.currentTarget;try{await mutate({action:'transfer',workspaceId:f.elements.workspace.value,reason:f.elements.reason.value});d.close();}catch(err){f.querySelector('[role=alert]').textContent=err.message;}};
  });
  bind('[data-wf-transfer-requests]',async()=>{
   const requests=(await send('/api/workflow/transfer-requests')).filter(r=>r.jobId===jobId);
   const d=dialog('Workspace Transfer Approvals',requests.map(r=>`<article><strong>Requested by ${esc(r.requestedBy)}</strong><p>${esc(r.reason)}</p><button type="button" class="action" data-transfer-decision="${r.id}" data-method="Approved">Approve</button><button type="button" class="action" data-transfer-decision="${r.id}" data-method="Rejected">Reject</button></article>`).join('')||'<p>No transfers await your approval.</p>');
   d.querySelector('[type=submit]').hidden=true;
   d.querySelectorAll('[data-transfer-decision]').forEach(b=>b.onclick=async()=>{
    const reason=prompt('Reason for decision:');if(!reason)return;
    try{await mutate({action:'decide-transfer',assignmentRequestId:b.dataset.transferDecision,method:b.dataset.method,reason});d.close();}
    catch(err){d.querySelector('[role=alert]').textContent=err.message;}
   });
  });
  bind('[data-wf-deliverables]',async()=>{
   const items=await send('/api/jobs/'+jobId+'/delivery-requirements');
   const content=items.map(x=>'<article><strong>'+esc(x.title)+'</strong><p>'+(x.waived?'Waived: '+esc(x.waiver_reason):x.delivered_at_utc?'Delivered: '+esc(x.delivery_method):'Awaiting delivery')+'</p>'+(!x.waived&&!x.delivered_at_utc?'<button type="button" data-deliver="'+esc(x.id)+'">Record delivery</button><button type="button" data-waive="'+esc(x.id)+'">Waive</button>':'')+'</article>').join('');
   const d=dialog('Required Deliverables',content+'<label>Add required deliverable<input name="title" maxlength="200"></label>');
   const f=d.querySelector('form');
   f.onsubmit=async e=>{e.preventDefault();if(!f.elements.title.value.trim())return;try{await mutate({action:'require-delivery',title:f.elements.title.value});d.close();}catch(err){f.querySelector('[role=alert]').textContent=err.message;}};
   d.querySelectorAll('[data-deliver]').forEach(b=>b.onclick=async()=>{const method=prompt('Delivery method:');if(!method)return;const reference=prompt('Receipt / reference:');if(reference===null)return;try{await mutate({action:'complete-delivery',taskId:b.dataset.deliver,method,reference});d.close();}catch(err){f.querySelector('[role=alert]').textContent=err.message;}});
   d.querySelectorAll('[data-waive]').forEach(b=>b.onclick=async()=>{const reason=prompt('Authorized waiver reason:');if(!reason)return;try{await mutate({action:'waive-delivery',taskId:b.dataset.waive,reason});d.close();}catch(err){f.querySelector('[role=alert]').textContent=err.message;}});
  });
  bind('[data-wf-block]',()=>{const reason=prompt('Blocked reason; leave blank to clear the block.');return reason===null?undefined:mutate({action:'blocked',reason});});
  bind('[data-wf-ready]',b=>confirm('Have all intended files finished uploading and passed the required backup checks?')?mutate({action:'contribution-ready',sessionId:b.dataset.wfReady,backupConfirmed:true}):undefined);
  bind('[data-wf-upload]',async b=>{
   const batches=(await json(`/api/jobs/${jobId}/media/upload-batches`)).filter(x=>x.sessionId===b.dataset.wfUpload&&!x.cancelledAtUtc);
   const d=dialog('Verify upload batch',`<label>Stored upload batch<select name="batch" required><option value="">Choose actual batch</option>${batches.map(x=>`<option value="${x.batchId}">${esc(x.createdBy)} · ${x.files.filter(f=>f.state==='Stored').length}/${x.files.length} stored · ${esc(new Date(x.createdAtUtc).toLocaleString())}</option>`).join('')}</select></label><label><input name="backup" type="checkbox" required> Required backup verification completed</label>`);
   const f=d.querySelector('form');
   f.onsubmit=async e=>{e.preventDefault();try{await mutate({action:'record-upload-batch',sessionId:b.dataset.wfUpload,uploadBatchId:f.elements.batch.value,backupConfirmed:f.elements.backup.checked});d.close();}catch(err){f.querySelector('[role=alert]').textContent=err.message;}};
  });
  bind('[data-wf-waive]',b=>{const person=prompt('Photographer username whose contribution is waived:');if(!person)return;const reason=prompt('Reason for waiving this contribution:');return reason?mutate({action:'waive-contribution',sessionId:b.dataset.wfWaive,userName:person,reason}):undefined;});
  bind('[data-wf-report]',b=>openPhotographerReport(jobId,b.dataset.wfReport));
  bind('[data-wf-action]',b=>{const action=b.dataset.wfAction;const reason=['confirm','cancel','request-changes'].includes(action)?prompt(action==='confirm'?'Confirmation note (required for a deposit override):':'Reason / required corrections:'):'';if(reason===null)return;const method=action==='deliver'?prompt('Delivery method (gallery, collection, courier, etc.):'):null;if(action==='deliver'&&!method)return;const reference=action==='deliver'?prompt('Delivery receipt / reference (optional):'):null;return mutate({action,reason,method,reference});});
  bind('[data-wf-finance]',async()=>{
   const release=await send('/api/jobs/'+jobId+'/finance-release');
   const d=dialog('Finance Credit / Final Release',`<p>Outstanding: ${esc(release.decision.balance)} JMD. Authorization preserves this receivable; it does not record payment.</p>${release.history.map(a=>`<article><strong>${esc(a.kind)} · JMD ${esc(a.authorized_amount)}</strong><p>${esc(a.terms)} · Due ${esc(a.payment_due_date)} · ${esc(a.approved_by)} · ${esc(a.contract_reference)} · ${esc(a.reason)}</p>${!a.revoked_at_utc?`<button type="button" data-revoke-finance="${esc(a.id)}">Revoke release</button>`:`<p>Revoked: ${esc(a.revocation_reason)}</p>`}</article>`).join('')}<label>Authorization<select name="kind"><option value="CreditTerms">Contractual credit terms</option><option value="DeliveryAuthorization">Specific final delivery</option></select></label><label>Authorized balance (JMD)<input type="number" name="amount" min="0.01" max="999999999" step="0.01" required value="${esc(release.decision.balance)}"></label><label>Payment due date<input type="date" name="due" required></label><label>Terms<textarea name="terms" maxlength="4000" required></textarea></label><label>Contract / reference<input name="reference" maxlength="4000" required></label><label>Reason<textarea name="reason" maxlength="4000" required></textarea></label>`);
   const f=d.querySelector('form');f.onsubmit=async e=>{e.preventDefault();const save=f.querySelector('[type=submit]');save.disabled=true;try{await mutate({action:'authorize-finance',authorizedAmount:Number(f.elements.amount.value),kind:f.elements.kind.value,paymentDueDate:f.elements.due.value,creditTerms:f.elements.terms.value,reference:f.elements.reference.value,reason:f.elements.reason.value});d.close();}catch(err){f.querySelector('[role=alert]').textContent=err.message;save.disabled=false;}};
   d.querySelectorAll('[data-revoke-finance]').forEach(b=>b.onclick=async()=>{const reason=prompt('Reason for revoking final release:');if(!reason?.trim())return;b.disabled=true;try{await mutate({action:'revoke-finance',taskId:b.dataset.revokeFinance,reason});d.close();}catch(err){f.querySelector('[role=alert]').textContent=err.message;b.disabled=false;}});
  });
  bind('[data-wf-override]',()=>{const reason=prompt('Why is a deposit override required?');return reason?.trim()?mutate({action:'request-deposit-override',reason}):undefined;});
  bind('[data-wf-reopen]',()=>{const reason=prompt('Reason for reopening:');if(!reason)return;const targetStage=prompt('Reopen at Needs Assignment, Needs Photographer Assignment, Awaiting Scheduling or Awaiting Upload:','Needs Assignment');return targetStage?mutate({action:'reopen',reason,targetStage}):undefined;});
  bind('[data-wf-request]',()=>{const kind=prompt('Assignment requested: Owner, Photographer or Task','Photographer');if(!kind)return;const taskId=kind==='Task'?prompt('Task ID:'):null;const reason=prompt('Optional explanation:');return reason===null?undefined:mutate({action:'request-assignment',kind,taskId,reason});});
  bind('[data-wf-requests]',async()=>{
   const rows=(await send('/api/workflow/assignment-requests')).filter(r=>r.job_id===jobId);const d=dialog('Assignment Requests',rows.map(r=>`<article><strong>${esc(r.user_name)} · ${esc(r.kind)}</strong><p>${esc(r.note)} · ${esc(r.status)}</p>${managed&&r.status==='Pending'?`<button class="action" type="button" data-decision="${r.id}" data-method="Approved">Approve</button><button class="action" type="button" data-decision="${r.id}" data-method="Rejected">Reject</button><button class="action" type="button" data-decision="${r.id}" data-method="Redirected">Redirect</button>`:''}</article>`).join('')||'<p>No requests.</p>');d.querySelector('[type=submit]').hidden=true;d.querySelectorAll('[data-decision]').forEach(b=>b.onclick=async()=>{const reason=prompt('Decision reason:');if(!reason)return;const row=rows.find(r=>r.id===b.dataset.decision);const userName=b.dataset.method==='Redirected'?prompt('Responsible manager username:'):null;const sessionId=row.kind==='Photographer'&&b.dataset.method==='Approved'?prompt('Unscheduled session ID:'):null;try{await mutate({action:'decide-request',assignmentRequestId:row.id,method:b.dataset.method,reason,userName,sessionId});d.close();}catch(err){d.querySelector('[role=alert]').textContent=err.message;}});
  });
 }
 root.StudioWorkflow={nextAction,open,render,command,productionTask,canReportTask,elapsed,crewPayload,escape:esc};
 if(typeof module!=='undefined')module.exports=root.StudioWorkflow;
})(typeof window!=='undefined'?window:globalThis);

