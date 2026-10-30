/* One presentation and action router for server-projected job and enquiry work. */
(function(root){
 'use strict';
 const esc=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const rows=new Map(),key=(kind,id)=>kind+':'+id;
 let loading=null,generation=0;
 const dateKey=value=>new Intl.DateTimeFormat('en-CA',{timeZone:'America/Jamaica'}).format(new Date(value));
 function due(row,now=Date.now()){
  if(!row?.dueAtUtc)return {label:'No due date',overdue:false};
  const at=Date.parse(row.dueAtUtc);if(!Number.isFinite(at))return {label:'No due date',overdue:false};
  const today=dateKey(now),date=dateKey(at),tomorrow=dateKey(now+86400000);
  return {label:at<now?'Overdue':date===today?'Today':date===tomorrow?'Tomorrow':new Date(at).toLocaleDateString('en-JM',{timeZone:'America/Jamaica',day:'numeric',month:'short',year:'numeric'}),overdue:at<now};
 }
 function invalidate(){++generation;loading=null;}
 function changed(row){invalidate();if(row)rows.set(key(row.kind,row.entityId),row);root.dispatchEvent(new CustomEvent('studio-next-actions-changed',{detail:row}));}
 async function load(force=false){
  if(force)invalidate();if(loading)return loading;
  const revision=generation;
  const request=json('/api/work/next-actions').then(values=>{if(!Array.isArray(values))throw Error('Unexpected next-action response.');if(revision!==generation)return load();rows.clear();values.forEach(row=>rows.set(key(row.kind,row.entityId),row));return values;});
  loading=request;try{return await request;}finally{if(loading===request)loading=null;}
 }
 async function get(kind,id){
  const values=await json('/api/work/next-actions?kind='+encodeURIComponent(kind)+'&id='+encodeURIComponent(id));
  if(!Array.isArray(values))throw Error('Unexpected next-action response.');
  const row=values[0]||null;if(row)rows.set(key(kind,id),row);else rows.delete(key(kind,id));return row;
 }
 const peek=(kind,id)=>rows.get(key(kind,id));
 function markup(row,{compact=false}={}){
  if(!row)return '<p class="studio-next-empty">No outstanding action.</p>';
  const d=due(row),action=row.action;
  return `<section class="studio-next-action${compact?' studio-next-compact':''}" aria-label="Next required action"><div class="studio-next-main"><small class="studio-next-label">Next required action</small><button type="button" class="studio-next-link" data-next-activate>${esc(action.title)} <span aria-hidden="true">→</span></button><p class="studio-next-reason">${esc(action.reason)}</p></div><div class="studio-next-meta"><span>Responsible: <strong>${esc(row.assignedTo||'Unassigned')}</strong></span><span class="studio-next-due${d.overdue?' is-overdue':''}">Due: ${esc(d.label)}</span>${row.canManage?'<button type="button" class="studio-next-edit" data-next-edit>Assign / set due date</button>':''}</div>${row.waitingReason?`<p class="studio-next-waiting">Waiting: ${esc(row.waitingReason)}</p>`:''}${!compact&&action.after?`<p class="studio-next-after">After this: ${esc(action.after)}</p>`:''}<p class="studio-next-error" role="alert" data-next-error></p></section>`;
 }
 function bind(host,row){
  const fail=error=>{const node=host.querySelector('[data-next-error]');if(node)node.textContent=error.message||String(error);};
  host.querySelector('[data-next-activate]')?.addEventListener('click',event=>{event.stopPropagation();activate(row).catch(fail);});
  host.querySelector('[data-next-edit]')?.addEventListener('click',event=>{event.stopPropagation();edit(row).catch(fail);});
 }
 function mount(host,row,options={}){host.innerHTML=markup(row,options);bind(host,row);}
 async function activate(row){
  if(!row)throw Error('Refresh to load the current action.');
  // Re-read before opening: a queued task may have changed in another user's session.
  const current=await get(row.kind,row.entityId);
  if(!current)throw Error('This record has no outstanding action. Refresh the page.');
  row=current;const a=row.action;
  if(a.route==='association'){
   const conversation=await json('/api/conversations/'+row.conversationId),dialog=studioDialog('Client association','<div data-association-host></div>');
   await StudioInquiryAssociation.mount(dialog.querySelector('[data-association-host]'),row.conversationId,conversation,async()=>{dialog.close();changed();});return dialog;
  }
  if(a.route==='reply')return openEnquiry(row.entityId,{focusReply:true});
  if(a.route==='quotes'){
   if(!row.clientId)throw Error('Assign a client before preparing a quotation.');
   const dialog=await showClientQuotes(row.clientId);dialog?.addEventListener('close',()=>changed(),{once:true});return dialog;
  }
  if(a.route==='deposit'){
   const requests=await json('/api/frontdesk/booking-requests'),request=requests.find(item=>String(item.jobId)===String(row.jobId));
   if(!request)throw Error('This job has no Front Desk deposit request. Open the job to review payment requirements.');
   return openVerifiedDepositForm(request);
  }
  if(a.route==='session-plan')return root.studioNavigate('session-plan',{jobId:row.jobId});
  if(a.route==='media')return root.studioNavigate('workspaces',{jobId:row.jobId});
  if(a.route==='task')return StudioJobGraph.launchTask(row.jobId,a.taskId);
  const dialog=await StudioWorkflow.open(row.jobId,a.selector||null);
  dialog?.addEventListener('close',()=>changed(),{once:true});return dialog;
 }
 async function edit(row){
  const people=await json('/api/studio-employees?assignable=true&limit=100');
  const date=row.dueAtUtc?dateKey(row.dueAtUtc):'';
  const dialog=studioDialog('Next action — responsibility and due date',`<form class="studio-next-form"><p><strong>${esc(row.action.title)}</strong></p><label>Responsible person<select name="person"><option value="">Unassigned</option>${people.map(p=>`<option value="${esc(p.userName)}" ${p.userName?.toLowerCase()===row.assignedTo?.toLowerCase()?'selected':''}>${esc(p.displayName||p.userName)}</option>`).join('')}</select></label><label>Due date<input name="due" type="date" value="${esc(date)}"></label><label>Waiting reason<textarea name="waiting" maxlength="2000" placeholder="Record what is preventing progress">${esc(row.waitingReason||'')}</textarea></label><p>These details apply to this action. Workflow prerequisites still apply.</p><p role="alert"></p><button type="submit" class="primary">Save details</button></form>`);
  const form=dialog.querySelector('form');
  if(row.assignedTo&&!people.some(p=>p.userName?.toLowerCase()===row.assignedTo.toLowerCase())){const option=new Option(row.assignedTo+' (current)',row.assignedTo,true,true);form.elements.person.add(option);}
  form.onsubmit=async event=>{event.preventDefault();const button=form.querySelector('[type=submit]');button.disabled=true;try{
   const updated=await json('/api/work/next-actions/'+row.kind+'/'+encodeURIComponent(row.entityId),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({actionKey:row.action.key,assignedTo:form.elements.person.value||null,dueAtUtc:form.elements.due.value?new Date(form.elements.due.value+'T23:59:59-05:00').toISOString():null,waitingReason:form.elements.waiting.value||null,expectedVersion:row.version})});
   dialog.close();changed(updated);
  }catch(error){form.querySelector('[role=alert]').textContent=error.message;button.disabled=false;}};
  return dialog;
 }
 async function openEnquiry(id,{focusReply=false}={}){
  const dialog=studioDialog('Enquiry','<section class="studio-enquiry"><p role="status">Loading enquiry…</p></section>');dialog.classList.add('studio-enquiry-dialog');
  const host=dialog.querySelector('.studio-enquiry');
  try{
   const [conversation,row,identities]=await Promise.all([json('/api/conversations/'+id),get('enquiry',id),json('/api/conversations/'+id+'/sending-identities')]);
   if(!dialog.isConnected)return dialog;
   if(conversation.jobId){host.innerHTML='<p>This enquiry is now linked to a job.</p><button type="button" data-linked-job>Open job →</button>';host.querySelector('button').onclick=()=>{dialog.close();root.studioNavigate('workspaces',{jobId:conversation.jobId});};return dialog;}
   host.innerHTML=`<h3>${esc(conversation.subject)}</h3><div data-enquiry-next></div><div class="studio-enquiry-columns"><section><h3>Conversation</h3><div class="studio-enquiry-history">${(conversation.messages||[]).map(m=>`<article><header><strong>${esc(m.author)}</strong><time>${esc(new Date(m.createdAt||m.createdAtUtc).toLocaleString('en-JM',{timeZone:'America/Jamaica'}))}</time><small>${esc(m.visibility==='Internal'?'Internal note':'Customer conversation')}</small></header><p>${esc(m.body)}</p></article>`).join('')||'<p>No messages recorded.</p>'}</div>${row?.canManage?`<form class="studio-enquiry-reply"><h3>Reply</h3><p>To: ${esc(conversation.contactName)} · ${esc(conversation.contactAddress)}</p><label>Send as<select name="identity">${(identities.identities||[]).map(identity=>`<option value="${esc(identity.type)}">${esc(identity.label)}</option>`).join('')||'<option value="Workspace">Workspace</option>'}</select></label><label><span class="sr-only">Reply message</span><textarea name="body" required maxlength="10000" placeholder="Write your reply…"></textarea></label>${row.canAttach?'<label>Add attachments<input name="files" type="file" multiple></label>':''}<p role="alert"></p><button type="submit" class="primary">Send reply</button></form>`:'<p>You do not have permission to reply to this enquiry.</p>'}</section><aside><h3>Enquiry details</h3><dl><dt>Contact</dt><dd>${esc(conversation.contactName)}</dd><dt>Address</dt><dd>${esc(conversation.contactAddress)}</dd><dt>Channel</dt><dd>${esc(conversation.channel)}</dd><dt>Responsible</dt><dd>${esc(row?.assignedTo||'Unassigned')}</dd></dl><div data-enquiry-association></div><h3>What follows</h3><p>${esc(row?.action.after||'Review requirements')}</p><ol class="studio-enquiry-follows"><li>Confirm requirements</li><li>Prepare and send quotation</li><li>Record acceptance</li><li>Collect deposit or approve override</li><li>Confirm booking</li></ol><p class="studio-next-after">Later actions become available as requirements are met.</p><button type="button" class="studio-next-link" data-enquiry-prepare>Prepare booking →</button></aside></div>`;
   mount(host.querySelector('[data-enquiry-next]'),row);
   // Focusing the composer here prevents opening a second enquiry popup from its own action.
   host.querySelector('[data-next-activate]')?.addEventListener('click',event=>{if(row?.action.route==='reply'){event.stopImmediatePropagation();host.querySelector('textarea[name=body]')?.focus();}},true);
   host.querySelector('[data-enquiry-prepare]').onclick=async()=>{try{dialog.close();await openPendingBookingForm({...conversation,id},row?.clientId?{id:row.clientId,displayName:conversation.contactName,email:conversation.contactAddress}:undefined);}catch(error){host.querySelector('[data-next-error]').textContent=error.message;}};
   if(root.StudioInquiryAssociation)await StudioInquiryAssociation.mount(host.querySelector('[data-enquiry-association]'),id,conversation,async()=>{dialog.close();changed();await openEnquiry(id);});
   const form=host.querySelector('form.studio-enquiry-reply');
   const uploaded=new Set();
   if(form)form.onsubmit=async event=>{event.preventDefault();const button=form.querySelector('[type=submit]');button.disabled=true;try{
    for(const file of form.elements.files?.files||[]){if(uploaded.has(file))continue;const data=new FormData();data.append('file',file);await json('/api/conversations/'+id+'/attachments?visibility=External',{method:'POST',body:data});uploaded.add(file);}
    await json('/api/conversations/'+id+'/messages',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({body:form.elements.body.value,isInternal:false,sendingIdentity:form.elements.identity.value})});
    form.elements.body.value='';if(form.elements.files)form.elements.files.value='';changed();dialog.close();await openEnquiry(id);
   }catch(error){form.querySelector('[role=alert]').textContent=error.message;button.disabled=false;}};
   if(focusReply)form?.elements.body.focus();
  }catch(error){if(dialog.isConnected){host.replaceChildren();const p=document.createElement('p');p.setAttribute('role','alert');p.textContent='Unable to load enquiry: '+error.message;const retry=document.createElement('button');retry.type='button';retry.textContent='Retry';retry.onclick=()=>{dialog.close();openEnquiry(id,{focusReply});};host.append(p,retry);}}
  return dialog;
 }
 root.StudioNextActions={load,get,peek,markup,mount,bind,activate,edit,openEnquiry,due,changed};
 const workflowEvents=new WeakSet();
 for(const target of [root,document])target.addEventListener('studio-workflow-changed',event=>{if(workflowEvents.has(event))return;workflowEvents.add(event);changed();});
})(window);
