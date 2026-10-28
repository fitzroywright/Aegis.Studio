// Task-focused pages reuse the shared shell and existing intake, media and finance services.
window.StudioWorkflowViews=(()=>{
 'use strict';
 const esc=value=>StudioPage.escape(value);
 const amount=value=>new Intl.NumberFormat(undefined,{style:'currency',currency:'JMD'}).format(Number(value)||0);
 let session=null,delivery=null;
 const metrics=items=>items.map(([label,value])=>`<div class="workspace-summary-row"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`).join('');
 const button=(key,label,primary=false)=>`<button type="button" class="${primary?'primary':'action'}" data-workflow-action="${key}">${esc(label)}</button>`;
 function actions(view){
  if(view==='frontdesk')return [['booking','New Booking'],['inquiry','New Inquiry'],['quote','Create Quote'],['calendar','View Calendar'],['delivery','Delivery']];
  if(['session-plan','session-plan-import'].includes(view))return [];
  if(view==='delivery')return [['notice','Send Pickup Notice'],['payment','Record Payment'],['statement','View Statement'],['export','Export Report']];
  if(view==='production')return [];
  return null;
 }
 function bind(host=document){host.querySelectorAll('[data-workflow-action]').forEach(b=>{if(['session-plan','session-plan-import'].includes(document.body.dataset.studioView)&&session){const key=b.dataset.workflowAction;b.disabled=session.busy||(['import','add-subject'].includes(key)&&!session.roster.canEdit)||(['assign-session','remainder'].includes(key)&&!session.roster.canSchedule);}b.onclick=()=>{if(b.dataset.workflowAction==='import'&&document.body.dataset.studioView==='session-plan')return studioNavigate('session-plan-import',{jobId:session.job.id});return run(b.dataset.workflowAction,b).catch(e=>alert(e.message));};});}
 async function run(key,control){
  const view=document.body.dataset.studioView;
  if(key==='booking')return openPendingBookingForm(null,null);
  if(key==='inquiry')return openFrontDeskInquiry();
  if(key==='calendar')return studioNavigate('schedule');
  if(key==='delivery')return studioNavigate('delivery');
  if(key==='quote'){
   const clients=await json('/api/frontdesk/clients');
   const d=studioDialog('Create Quote','<form class="subject-form"><label>Client<select name="client" required><option value="">Choose a client…</option>'+clients.map(c=>`<option value="${esc(c.id)}">${esc(c.displayName)}</option>`).join('')+'</select></label><button class="primary">Continue</button></form>');
   d.querySelector('form').onsubmit=async e=>{e.preventDefault();d.close();await showClientQuotes(e.target.elements.client.value);};return;
  }
  if(['session-plan','session-plan-import'].includes(view)&&session){
   if(key==='plan')return studioNavigate('session-plan',{jobId:session.job.id});
   if(key==='job')return studioNavigate('workspaces',{jobId:session.job.id});
   if(key==='template')return download('session-plan-template.csv','name,reference,collection,contactName,contactEmail,contactPhone,scheduleDate,scheduleTime,scheduleLocation,notes,pickupNotification\n');
   if(key==='add-subject')return editSubject();
   if(key==='assign-session')return openPlanAssignments();
   if(key==='remainder')return openPlanAssignments(true);
   if(key==='import'){if(!session.roster.canEdit)throw Error('You do not have permission to import subjects.');session.step=1;session.conflictId=null;session.page=1;paintSession();return;}
  }
  if(view==='delivery'&&delivery){
   if(key==='export')return download('delivery-report.csv','Job,Client,Delivery,Payments,Balance\n'+delivery.rows.map(r=>[r.job.title,r.job.clientName,r.job.status,r.payment,r.balance??'Unavailable'].map(csvCell).join(',')).join('\n'));
   const row=delivery.rows.find(r=>r.job.id===delivery.selected);if(!row)throw Error('Select a job first.');
   if(key==='notice')return StudioClientReminders.open(row.job.id);
   if(key==='payment')return launchFrontDeskPos('TakePayment',{jobId:row.job.id});
   if(key==='statement'){if(!row.job.clientId)throw Error('Link a client to this job before opening a statement.');return studioNavigate('clients',{clientId:row.job.clientId,tab:'finance'});}
   if(key==='fulfill')return recordFrontDeskFulfillment(row.job.id,()=>showDelivery());
   if(key==='open-job')return studioNavigate('workspaces',{jobId:row.job.id});
  }
 }
 const csvCell=v=>'"'+String(v??'').replaceAll('"','""')+'"';
 function download(name,text){const url=URL.createObjectURL(new Blob([text],{type:'text/csv;charset=utf-8'})),a=document.createElement('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}
 function shellPage(view,title,content,overview){
  conversationView.classList.add('hidden');genericView.classList.remove('hidden');
  genericView.innerHTML=StudioPage.html({title,showHeader:false,className:'workflow-view '+view+'-view',content,summary:metrics(overview)});
  StudioPage.bind(genericView);window.StudioShell?.sync(view);bind();
 }
 const planUrl=s=>'/api/jobs/'+s.job.id+'/session-plan';
 const outcome=r=>r.status==='Needs Retake'?'Reshoot':r.status||'Not Photographed';
 const localValue=value=>{if(!value)return '';const d=new Date(value);return Number.isFinite(+d)?new Date(+d-d.getTimezoneOffset()*60000).toISOString().slice(0,16):'';};
 const displayedTime=value=>value?new Date(value).toLocaleString():'';
 const planFields=()=>StudioRosterEditor.sessionFields;
 const planLabels=()=>StudioRosterEditor.sessionLabels;
 function planRows(s){const ids=new Map(s.subjects.map(r=>[r.id,r])),refs=new Map(s.subjects.map(r=>[r.reference,r]));return s.roster.rows.map(r=>({...ids.get(r.subjectId)||refs.get(r.reference),...r})).sort((a,b)=>String(a.collection||'General').localeCompare(String(b.collection||'General'))||String(a.name).localeCompare(String(b.name))||String(a.reference).localeCompare(String(b.reference)));}
 function visibleSubjects(s){return planRows(s).filter(r=>(!s.group||(r.collection||'General')===s.group)&&(!s.outcome||outcome(r)===s.outcome)&&[r.name,r.reference,r.contactName,r.contactEmail,r.contactPhone].join(' ').toLowerCase().includes(s.term.toLowerCase()));}
 async function showSessionPlan(jobId,retained){
  const importPage=document.body.dataset.studioView==='session-plan-import';
  const current=StudioPage.begin(importPage?'session-plan-import':'session-plan');
  if(!jobId)throw Error('Open Session Plan from Job Details.');
  genericView.innerHTML='<p role="status">Loading Session Plan…</p>';
  const [job,subjects,roster]=await Promise.all([json('/api/jobs/'+jobId),json('/api/jobs/'+jobId+'/session-plan'),json('/api/jobs/'+jobId+'/session-plan/roster')]);if(!current())return;
  if(importPage&&!roster.canEdit)throw Error('You do not have permission to import this Session Plan.');
  session={job,subjects:subjects.filter(s=>!s.isSystem),roster,importPage,step:importPage?1:0,cells:[],mapping:{},preview:[],changes:[],choices:{},errors:[],message:'',busy:false,term:retained?.term||'',group:retained?.group||'',outcome:retained?.outcome||'',selected:new Set(),dateOrder:'DMY',conflictId:null};
  paintSession();
 }
 function planPayload(rows){return rows.map(row=>Object.fromEntries(['subjectId',...StudioRosterEditor.fields].filter(k=>row[k]!==undefined).map(k=>[k,row[k]])));}
 async function savePlan(s,rows,message){
  if(!s.roster.canEdit)throw Error('You do not have permission to edit this Session Plan.');
  const errors=StudioRosterEditor.validate(rows);if(errors.length)throw Error(errors.join(' · '));
  if(s.busy)throw Error('A save is already in progress.');s.busy=true;
  try{
   await json(planUrl(s)+'/roster',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({revision:s.roster.revision,rows:planPayload(rows),resolveConflict:Boolean(s.conflictId),conflictConversationId:s.conflictId})});
  }catch(e){s.busy=false;throw e;}
  // A successful write remains successful even if the following refresh fails.
  s.busy=false;s.step=0;s.conflictId=null;s.message=message;
  if(session!==s||!['session-plan','session-plan-import'].includes(document.body.dataset.studioView))return;
  if(s.importPage){await studioNavigate('session-plan',{jobId:s.job.id});return;}
  try{await showSessionPlan(s.job.id,s);session.message=message;paintSession();}
  catch(e){genericView.innerHTML='<p role="status">'+esc(message)+'</p><p role="alert">Refresh failed: '+esc(e.message)+'</p><button type="button" class="action" data-session-reload>Reload Session Plan</button>';genericView.querySelector('button').onclick=()=>showSessionPlan(s.job.id,s).catch(error=>alert(error.message));}
 }
 function exportPlan(rows){const keys=StudioRosterEditor.fields;download('session-plan.csv',[keys.join(','),...rows.map(r=>keys.map(k=>csvCell(typeof r[k]==='string'&&/^[=+@\-\t\r]/.test(r[k])?"'"+r[k]:r[k])).join(','))].join('\n'));}
 async function editSubject(reference){
  const s=session;if(!s.roster.canEdit||s.busy)throw Error('You do not have permission to edit this Session Plan.');
  const existing=reference?s.roster.rows.find(r=>r.reference===reference):null,row=existing||{reference:crypto.randomUUID(),collection:s.group||'General',pickupNotification:'None'};
  const input=(key,label,type='text',required=false)=>`<label>${label}<input name="${key}" type="${type}" ${required?'required':''} value="${esc(row[key]||'')}" maxlength="${key==='contactEmail'?320:200}"></label>`;
  const d=studioDialog(existing?'Edit Subject':'Add Subject',`<form class="workflow-detail"><div class="workflow-column-map">${input('name','Name','text',true)}<label>Reference<input name="reference" value="${esc(row.reference)}" ${existing?'readonly':'required'} maxlength="200"><small>Kept stable to preserve photographs and safe re-import.</small></label>${input('collection','Group / collection')}${input('contactName','Contact name')}${input('contactEmail','Email','email')}${input('contactPhone','Phone','tel')}<label>Planned arrival (${esc(Intl.DateTimeFormat().resolvedOptions().timeZone)})<input name="arrival" type="datetime-local" value="${localValue(row.scheduledAtUtc)}" ${s.roster.canSchedule?'':'disabled'}></label><label>Location<input name="scheduleLocation" maxlength="300" value="${esc(row.scheduleLocation||'')}" ${s.roster.canSchedule?'':'disabled'}></label><label>Pickup notification<select name="pickupNotification">${['None','Email','WhatsApp'].map(v=>`<option ${v===row.pickupNotification?'selected':''}>${v}</option>`).join('')}</select></label></div><label>Notes<textarea name="notes" maxlength="4000">${esc(row.notes||'')}</textarea></label><p role="alert"></p><div class="workflow-detail-actions"><button type="button" class="action" data-cancel>Cancel</button><button type="submit" class="primary">Save Subject</button></div></form>`);
  const form=d.querySelector('form');form.querySelector('[data-cancel]').onclick=()=>d.close();
  form.onsubmit=async e=>{e.preventDefault();const save=form.querySelector('[type=submit]');save.disabled=true;try{const updated={...row};if(!existing)updated.reference=form.elements.reference.value.trim();for(const key of ['name','collection','contactName','contactEmail','contactPhone','notes','pickupNotification'])updated[key]=form.elements[key].value.trim();if(s.roster.canSchedule){updated.scheduledAtUtc=form.elements.arrival.value?new Date(form.elements.arrival.value).toISOString():'';updated.scheduleLocation=form.elements.scheduleLocation.value.trim();}const rows=existing?s.roster.rows.map(r=>r.reference===reference?updated:r):[...s.roster.rows,updated];await savePlan(s,rows,'Subject saved.');d.close();}catch(error){form.querySelector('[role=alert]').textContent=error.message;}finally{save.disabled=false;}};
 }
 async function planSelected(){
  const s=session;if(!s.roster.canEdit||!s.roster.canSchedule)throw Error('Scheduling permission is required.');
  const selected=planRows(s).filter(r=>s.selected.has(r.reference));if(!selected.length)throw Error('Select the subjects to plan.');
  const d=studioDialog('Plan '+selected.length+' subjects',`<form class="workflow-detail"><p>Set planned arrivals in the displayed subject order. Existing outcomes and photographs stay attached.</p><label>First arrival (${esc(Intl.DateTimeFormat().resolvedOptions().timeZone)})<input name="start" type="datetime-local" required></label><label>Minutes between subjects<input name="interval" type="number" min="0" max="1440" value="5" required></label><label>Location<input name="location" maxlength="300" required></label><p>Staff and resource reservations are managed through Assignments & Schedule.</p><div data-plan-preview></div><p role="alert"></p><div class="workflow-detail-actions"><button type="button" class="action" data-preview>Preview Times</button><button type="button" class="action" data-cancel>Cancel</button><button type="submit" class="primary" disabled>Save Plan</button></div></form>`);
  const form=d.querySelector('form');let proposal=null;const save=form.querySelector('[type=submit]');
  const invalidate=()=>{proposal=null;save.disabled=true;form.querySelector('[data-plan-preview]').replaceChildren();};form.oninput=invalidate;
  form.querySelector('[data-preview]').onclick=()=>{if(!form.reportValidity())return;try{const first=new Date(form.elements.start.value),interval=Number(form.elements.interval.value);if(!Number.isFinite(+first)||!Number.isFinite(interval)||interval<0||interval>1440)throw Error('Choose a valid start and interval.');const proposed=new Map(selected.map((r,i)=>[r.reference,{...r,scheduledAtUtc:new Date(+first+i*interval*60000).toISOString(),scheduleLocation:form.elements.location.value.trim()}]));proposal=s.roster.rows.map(r=>proposed.get(r.reference)||r);form.querySelector('[data-plan-preview]').innerHTML='<ol>'+selected.map(r=>`<li>${esc(r.name)} · ${esc(displayedTime(proposed.get(r.reference).scheduledAtUtc))}</li>`).join('')+'</ol>';save.disabled=false;}catch(e){form.querySelector('[role=alert]').textContent=e.message;}};
  form.querySelector('[data-cancel]').onclick=()=>d.close();form.onsubmit=async e=>{e.preventDefault();if(!proposal)return;save.disabled=true;try{await savePlan(s,proposal,'Planned arrivals saved.');d.close();}catch(error){form.querySelector('[role=alert]').textContent=error.message;save.disabled=false;}};
 }
 async function openPlanAssignments(remainder=false){
  const s=session;if(!s.roster.canSchedule)throw Error('Scheduling permission is required.');
  const dialog=await StudioWorkflow.open(s.job.id,remainder?'[data-wf-remainder]':'[data-wf-session]:not([data-wf-remainder])');
  dialog?.addEventListener('close',()=>{if(['session-plan','session-plan-import'].includes(document.body.dataset.studioView)&&session===s)showSessionPlan(s.job.id,s).catch(e=>alert(e.message));},{once:true});
 }
 function buildPlanPreview(s){
  const result=StudioRosterEditor.planImport(s.cells,s.mapping,s.roster.rows,s.choices,s.dateOrder);s.preview=result.rows;s.changes=result.changes;s.errors=result.errors;
  if(!s.roster.canSchedule)for(const row of s.preview){const old=s.roster.rows.find(r=>r.reference===row.reference);if(['scheduledAtUtc','scheduleLocation'].some(k=>(row[k]||'')!==(old?.[k]||'')))s.errors.push('Scheduling permission is required to import planned times or locations.');}
 }
 function paintSession(){
  const s=session,subjects=planRows(s),preview=s.step===3,importing=s.step>0;
  const overview=[['Subjects',subjects.length],['Photographed',subjects.filter(r=>outcome(r)==='Photographed').length],['Absent',subjects.filter(r=>outcome(r)==='Absent').length],['Reshoots',subjects.filter(r=>/reshoot|retake/i.test(outcome(r))).length]];
  const steps=['Choose file','Map columns','Preview & validate','Import'];
  const stepper=importing?'<ol class="workflow-import-steps">'+steps.map((label,i)=>`<li class="${i+1<s.step?'is-complete':i===3&&preview&&!s.errors.length?'is-ready':''}" ${s.step===i+1?'aria-current="step"':''}><span>${i+1}</span><div><strong>${label}</strong><small>${esc(i===0?s.fileName||'CSV or XLSX':i===1?Object.values(s.mapping).filter(v=>v!==''&&v!==undefined).length+' mapped':i===2&&preview?s.changes.length+' rows · '+s.errors.length+' issues':i===3&&preview&&!s.errors.length?'Ready to import':'')}</small></div></li>`).join('')+'</ol>':'';
  const savedByReference=new Map(subjects.map(r=>[r.reference,r])),previewByReference=new Map(s.preview.map(r=>[r.reference,r]));
  const visible=preview?s.changes.map(c=>({...savedByReference.get(c.row.reference),...previewByReference.get(c.row.reference),change:c})):visibleSubjects(s);
  const pageSize=100,pageCount=Math.max(1,Math.ceil(visible.length/pageSize));s.page=Math.min(s.page||1,pageCount);const rows=visible.slice((s.page-1)*pageSize,s.page*pageSize);
  const options=(values,current)=>values.map(v=>`<option value="${esc(v)}" ${v===current?'selected':''}>${esc(v)}</option>`).join('');
  const toolbar=!importing?`<div class="workflow-toolbar session-toolbar"><label>Search<input type="search" data-session-search value="${esc(s.term)}" placeholder="Name, reference or contact"></label><label>Group<select data-session-group><option value="">All groups</option>${options([...new Set(subjects.map(r=>r.collection||'General'))].sort(),s.group)}</select></label><label>Outcome<select data-session-outcome><option value="">All outcomes</option>${options([...new Set(subjects.map(outcome))].sort(),s.outcome)}</select></label><button type="button" class="action" data-session-add ${!s.roster.canEdit||s.busy?'disabled':''}>Add Subject</button><button type="button" class="action" data-session-plan ${!s.roster.canEdit||!s.roster.canSchedule||s.busy?'disabled':''}>Plan Selected (${s.selected.size})</button><button type="button" class="action" data-session-export>Export Plan</button></div>`:'';
  const table=`<div class="workflow-table-scroll"><table class="workflow-table"><thead><tr><th>${preview?'Change':'<input type="checkbox" data-session-all aria-label="Select this page">'}</th><th>Subject</th><th>Group</th><th>Contact</th><th>Planned arrival / location</th><th>Notes / notification</th><th>Outcome</th><th>Photos</th><th></th></tr></thead><tbody>${rows.map(r=>`<tr><td>${preview?esc(r.change.kind):`<input type="checkbox" data-session-select="${esc(r.reference)}" aria-label="Select ${esc(r.name)}" ${s.selected.has(r.reference)?'checked':''}>`}</td><td><strong>${esc(r.name)}</strong><small>${esc(r.reference)}</small></td><td>${esc(r.collection||'General')}</td><td>${esc(r.contactName||'')}<small>${esc(r.contactEmail||'')}</small><small>${esc(r.contactPhone||'')}</small></td><td>${esc(displayedTime(r.scheduledAtUtc))}<small>${esc(r.scheduleLocation||'')}</small></td><td>${esc(r.notes||'')}<small>${esc(r.pickupNotification||'None')}</small></td><td><span class="workflow-pill" data-tone="${/absent|reshoot|retake/i.test(outcome(r))?'danger':outcome(r)==='Photographed'?'success':'neutral'}">${esc(preview&&r.change.kind==='New'?'New subject':outcome(r))}</span></td><td>${r.imageCount===undefined?'—':esc(r.imageCount)}</td><td>${!preview&&s.roster.canEdit?`<button type="button" class="action" data-session-edit="${esc(r.reference)}" ${s.busy?'disabled':''}>Edit</button>`:''}</td></tr>`).join('')||'<tr><td colspan="9">No subjects match. Add a subject or import a list.</td></tr>'}</tbody></table></div>`;
  const paging=`<div class="workflow-toolbar"><span>${visible.length} ${preview?'imported rows':'matching subjects'} · ${s.selected.size} selected · Page ${s.page} of ${pageCount}</span><div><button class="action" data-session-prev ${s.page<=1?'disabled':''}>Previous</button><button class="action" data-session-next ${s.page>=pageCount?'disabled':''}>Next</button><button class="action" data-session-clear>Clear Selection</button></div></div>`;
  let controls='';
  if(s.step===1)controls='<section class="workflow-detail"><h3>Import subjects</h3><p>Upload the first worksheet of an XLSX workbook or a CSV (maximum 5 MB), or paste spreadsheet cells. For a paper list, use Add Subject.</p><label>CSV or XLSX<input type="file" accept=".csv,.xlsx" data-session-file></label><label>Or paste spreadsheet cells<textarea data-session-paste placeholder="Include the column headers"></textarea></label><button type="button" class="primary" data-session-pasted>Continue to column mapping</button></section>';
  if(s.step===2)controls='<section class="workflow-detail"><h3>Map columns</h3><p>Unmapped details are retained. Stable references match existing people; missing or changed references are reviewed before saving.</p><div class="workflow-column-map">'+planFields().map((field,i)=>`<label>${esc(planLabels()[i])}<select data-session-map="${field}"><option value="">Not supplied</option>${s.cells[0].map((h,j)=>`<option value="${j}" ${String(s.mapping[field])===String(j)?'selected':''}>${esc(h||'Column '+(j+1))}</option>`).join('')}</select></label>`).join('')+`<label>Spreadsheet date format<select data-session-date-order><option value="DMY" ${s.dateOrder==='DMY'?'selected':''}>Day / Month / Year</option><option value="MDY" ${s.dateOrder==='MDY'?'selected':''}>Month / Day / Year</option></select></label></div><p>Separate dates and times, Excel date/time numbers, or ISO timestamps are accepted. Times without an offset use ${esc(Intl.DateTimeFormat().resolvedOptions().timeZone)}.</p><button type="button" class="primary" data-session-preview>Preview & validate</button></section>`;
  if(preview)controls=`<section class="workflow-validation" role="status"><strong>${s.changes.filter(c=>c.kind==='New').length} new · ${s.changes.filter(c=>c.kind==='Update').length} updates · ${s.errors.length} issues</strong><span>${s.preview.length} total subjects after import. Existing people and photo associations are retained.</span><button type="button" class="action" data-session-warnings>Review warnings</button></section><div data-session-errors ${s.errors.length?'':'hidden'}>${s.errors.map(e=>'<p>'+esc(e)+'</p>').join('')||'<p>No validation warnings.</p>'}</div>`+s.changes.filter(c=>c.candidates.length).map(c=>`<label class="workflow-match">Row ${c.index+1}: ${esc(c.row.name)} — confirm identity<select data-session-match="${c.index}"><option value="">Choose a match…</option><option value="new" ${s.choices[c.index]==='new'?'selected':''}>Create a different person</option>${c.candidates.map(r=>`<option value="${esc(r.reference)}" ${(s.choices[c.index]??(!c.unresolved?c.row.reference:''))===r.reference?'selected':''}>${esc(r.name)} · ${esc(r.collection)} · ${esc(r.reference)} · ${esc(r.contactEmail||r.contactPhone||'No contact')}</option>`).join('')}</select></label>`).join('');
  const commit=preview?'<footer class="workflow-detail-actions"><button type="button" class="primary" data-session-commit '+(s.busy||s.errors.length?'disabled':'')+'>'+(s.conflictId?'Accept Reviewed Website Changes':'Import Subjects')+'</button><button type="button" class="action" data-session-back>Back to mapping</button><button type="button" class="action" data-session-export>Export Proposed Plan</button><button type="button" class="action" data-session-reload>Reload Saved Plan</button></footer>':'';
  const conflicts=s.roster.incomingRows||[];
  const conflictPanel=!importing&&conflicts.length?'<section class="workflow-validation"><strong>Website changes need review</strong><span>Current Studio details remain in use until you review and accept a change.</span>'+conflicts.map((c,i)=>`<button type="button" class="action" data-session-conflict="${i}" ${!s.roster.canEdit?'disabled':''}>Review change ${i+1}</button>`).join('')+'</section>':'';
  const cancel=importing?'<button type="button" class="action" data-session-cancel '+(s.busy?'disabled':'')+'>Cancel Import</button>':'';
  const mappingSummary=preview?'<section class="session-import-mapping-summary"><h2>Column mapping</h2><dl>'+planFields().filter(field=>s.mapping[field]!==undefined&&s.mapping[field]!=='').map(field=>'<div><dt>'+esc(s.cells[0][Number(s.mapping[field])])+'</dt><dd>'+esc(planLabels()[planFields().indexOf(field)])+'</dd></div>').join('')+'</dl></section>':'';
  const importContent=importing?`<div class="session-import-layout"><aside class="session-import-sidebar"><h2>Import steps</h2>${stepper}<small>CSV or XLSX</small></aside><div class="session-import-content">${mappingSummary}${preview?`<section class="session-import-preview"><h2>Preview · ${s.changes.length} rows</h2>${table}${paging}</section>`:controls}</div></div>${preview?`<section class="session-import-review"><h2>Review before import</h2>${controls}${commit}</section>`:''}${cancel}`:toolbar+conflictPanel+table+paging;
  shellPage(s.importPage?'session-plan-import':'session-plan',s.importPage?'Session Plan Import':'Session Plan',`<div class="${importing?'workflow-session-import':'workflow-session-plan'}"><header class="workflow-page-title">${importing?'<small>Session Plan Import</small>':''}<h1>${esc(s.job.title)}</h1><p>${importing?'Import a class or group once, review the mapping and preserve existing photo associations.':'Subjects, planned arrivals and field outcomes.'}</p></header><div class="workflow-detail-actions" role="toolbar" aria-label="Session Plan actions">${button('import','Import Subjects',true)}${button('template','Download Template')}${s.importPage?button('plan','Back to Session Plan'):button('assign-session','Assignments & Schedule')+button('remainder','Reschedule Remainder')}${button('job','Back to Job')}</div>${importContent}<p role="status" data-session-feedback>${esc(s.message)}</p><p class="workflow-note">Planned arrivals do not reserve staff or resources. Use Assignments & Schedule for bookings. Subject outcomes come from field reporting; uploading photographs does not complete a session.</p></div>`,overview);
  StudioPage.setCount(subjects.length+' subjects');const host=genericView;
  const fail=e=>{s.message=e.message;if(session===s&&['session-plan','session-plan-import'].includes(document.body.dataset.studioView))host.querySelector('[data-session-feedback]')?.replaceChildren(document.createTextNode(e.message));};
  const prepare=cells=>{if(cells.length<2)throw Error('Supply a header and at least one row.');s.cells=cells;s.mapping={};s.choices={};s.page=1;const norm=x=>String(x).replace(/[^a-z]/gi,'').toLowerCase();const aliases={reference:['studentid','subjectid'],name:['studentname','subjectname'],collection:['class','group'],contactEmail:['parentemail'],scheduleDate:['date','sessiondate','eventdate'],scheduleTime:['time','sessiontime','arrivaltime']};planFields().forEach((field,i)=>{const index=cells[0].findIndex(h=>norm(h)===norm(field)||norm(h)===norm(planLabels()[i])||aliases[field]?.includes(norm(h)));if(index>=0)s.mapping[field]=index;});s.step=2;paintSession();};
  host.querySelector('[data-session-file]')?.addEventListener('change',async e=>{const file=e.target.files[0];if(!file)return;s.fileName=file.name;try{if(file.size>5_000_000)throw Error('Choose a file of at most 5 MB.');const form=new FormData();form.append('file',file);const data=await json(planUrl(s)+'/file',{method:'POST',body:form});if(session===s&&['session-plan','session-plan-import'].includes(document.body.dataset.studioView))prepare(data.cells);}catch(e){fail(e);}});
  host.querySelector('[data-session-pasted]')?.addEventListener('click',()=>{try{prepare(StudioRosterEditor.csv(host.querySelector('[data-session-paste]').value));}catch(e){fail(e);}});
  host.querySelector('[data-session-preview]')?.addEventListener('click',()=>{s.mapping=Object.fromEntries([...host.querySelectorAll('[data-session-map]')].map(el=>[el.dataset.sessionMap,el.value]));s.dateOrder=host.querySelector('[data-session-date-order]').value;buildPlanPreview(s);s.step=3;paintSession();});
  host.querySelectorAll('[data-session-match]').forEach(el=>el.onchange=()=>{s.choices[el.dataset.sessionMatch]=el.value;buildPlanPreview(s);paintSession();});
  host.querySelector('[data-session-back]')?.addEventListener('click',()=>{s.step=2;paintSession();});
  host.querySelector('[data-session-warnings]')?.addEventListener('click',()=>{const el=host.querySelector('[data-session-errors]');el.hidden=!el.hidden;});
  host.querySelector('[data-session-cancel]')?.addEventListener('click',()=>{if(s.importPage)return studioNavigate('session-plan',{jobId:s.job.id});s.step=0;s.conflictId=null;s.page=1;paintSession();});
  host.querySelector('[data-session-commit]')?.addEventListener('click',async()=>{if(s.busy||s.errors.length)return;const control=host.querySelector('[data-session-commit]');control.disabled=true;try{await savePlan(s,s.preview,'Subjects saved. Existing photograph associations are retained.');}catch(e){s.message=e.message;if(session===s&&['session-plan','session-plan-import'].includes(document.body.dataset.studioView))paintSession();}});
  host.querySelector('[data-session-search]')?.addEventListener('input',e=>{s.term=e.target.value;s.page=1;const pos=e.target.selectionStart;paintSession();const input=host.querySelector('[data-session-search]');input.focus();input.setSelectionRange(pos,pos);});
  for(const key of ['group','outcome'])host.querySelector('[data-session-'+key+']')?.addEventListener('change',e=>{s[key]=e.target.value;s.page=1;paintSession();});
  host.querySelectorAll('[data-session-select]').forEach(el=>el.onchange=()=>{el.checked?s.selected.add(el.dataset.sessionSelect):s.selected.delete(el.dataset.sessionSelect);const scroll=host.querySelector('.workflow-table-scroll');const top=scroll.scrollTop;paintSession();host.querySelector('.workflow-table-scroll').scrollTop=top;});
  const all=host.querySelector('[data-session-all]');if(all){all.checked=rows.length>0&&rows.every(r=>s.selected.has(r.reference));all.indeterminate=rows.some(r=>s.selected.has(r.reference))&&!all.checked;all.onchange=()=>{rows.forEach(r=>all.checked?s.selected.add(r.reference):s.selected.delete(r.reference));paintSession();};}
  host.querySelector('[data-session-clear]')?.addEventListener('click',()=>{s.selected.clear();paintSession();});
  host.querySelector('[data-session-prev]')?.addEventListener('click',()=>{s.page--;paintSession();});host.querySelector('[data-session-next]')?.addEventListener('click',()=>{s.page++;paintSession();});
  host.querySelector('[data-session-add]')?.addEventListener('click',()=>editSubject().catch(fail));
  host.querySelectorAll('[data-session-edit]').forEach(el=>el.onclick=()=>editSubject(el.dataset.sessionEdit).catch(fail));
  host.querySelector('[data-session-plan]')?.addEventListener('click',()=>planSelected().catch(fail));
  host.querySelector('[data-session-reload]')?.addEventListener('click',()=>{if(confirm('Reload the saved Session Plan and discard this preview? Export Proposed Plan first to keep a copy.'))showSessionPlan(s.job.id,s).catch(fail);});
  host.querySelectorAll('[data-session-export]').forEach(el=>el.onclick=()=>exportPlan(preview?s.preview:visibleSubjects(s)));
  host.querySelectorAll('[data-session-conflict]').forEach(el=>el.onclick=()=>{const c=conflicts[Number(el.dataset.sessionConflict)];s.conflictId=c.conversationId;const keys=StudioRosterEditor.fields.filter(k=>c.rows.some(r=>Object.hasOwn(r,k)));prepare([keys,...c.rows.map(r=>keys.map(k=>r[k]||''))]);});
 }
 async function showDelivery({workspaceId}={}){
  const current=StudioPage.begin('delivery');
  const role=await loadFrontDeskRole();const me=await json('/api/auth/me');
  if(!role?.frontDesk&&!(me.user||me).isAdministrator)throw Error('Front Desk access is required to review delivery and payments.');
  genericView.innerHTML='<p role="status">Loading Delivery…</p>';
  const jobs=await json('/api/jobs');
  const candidates=jobs.filter(j=>(!workspaceId||String(j.workspaceId)===String(workspaceId)||(j.participatingWorkspaceIds||[]).map(String).includes(String(workspaceId)))&&['Ready for Client','Ready for Delivery','Published','Completed','Delivered'].includes(j.status));
  const rows=new Array(candidates.length);let next=0;
  async function worker(){while(next<candidates.length){if(!current())return;const i=next++,job=candidates[i];let balance=null,error='';try{const release=await json('/api/jobs/'+job.id+'/finance-release');balance=release.decision?.balance??null;}catch(e){error=e.message;}rows[i]={job,balance,error,payment:balance===null?'Unavailable':balance>0?'Outstanding':'Paid'};}}
  await Promise.all([worker(),worker(),worker()]);if(!current())return;
  delivery={rows,selected:rows.some(r=>r.job.id===delivery?.selected)?delivery.selected:rows[0]?.job.id||'',term:''};
  paintDelivery();
 }
 function paintDelivery(detailOnly=false){
  const d=delivery,ready=r=>['Ready for Client','Ready for Delivery','Published'].includes(r.job.status),rows=d.rows.filter(r=>[r.job.title,r.job.clientName,r.job.status].join(' ').toLowerCase().includes(d.term.toLowerCase())),selected=d.rows.find(r=>r.job.id===d.selected);
  const next=r=>r.balance===null?'Review payment status':r.balance>0?'Collect balance':ready(r)?'Notify subjects':'View Statement';
  const table='<div class="workflow-table-scroll"><table class="workflow-table"><thead><tr><th></th><th>Job / Client</th><th>Delivery</th><th>Payments</th><th>Balance</th><th>Next Action</th></tr></thead><tbody>'+rows.map(r=>`<tr data-delivery-row="${esc(r.job.id)}" aria-selected="${r.job.id===d.selected}" tabindex="0"><td><input type="radio" name="delivery-job" aria-label="Select ${esc(r.job.title)}" ${r.job.id===d.selected?'checked':''}></td><td><strong>${esc(r.job.title)}</strong><small>${esc(r.job.clientName)}</small></td><td><span class="workflow-pill" data-tone="success">${esc(r.job.status==='Ready for Client'||r.job.status==='Ready for Delivery'?'Ready for collection':r.job.status==='Published'?'Gallery published':r.job.status)}</span></td><td><span class="workflow-pill" data-tone="${r.balance===0?'success':'warning'}">${esc(r.payment)}</span></td><td>${r.balance===null?'Unavailable':esc(amount(r.balance))}</td><td>${esc(next(r))}</td></tr>`).join('')+'</tbody></table></div>';
  const preview=selected?.media?.assets?.find(a=>a.versionId&&String(a.contentType||'').startsWith('image/'));
  const photo=preview?`<img class="workflow-job-photo" src="/api/jobs/${encodeURIComponent(selected.job.id)}/versions/by-id/${encodeURIComponent(preview.versionId)}/preview?size=thumbnail" alt="">`:'';
  const detail=selected?`<section class="workflow-detail"><div class="workflow-job-summary">${photo}<div class="workflow-job-copy"><h2>${esc(selected.job.title)}</h2><p>${esc(selected.job.clientName||'')}</p><dl class="workflow-facts"><div><dt>Delivery status</dt><dd>${esc(selected.job.status)}</dd></div><div><dt>Payment status</dt><dd>${esc(selected.payment)}</dd></div><div><dt>Balance</dt><dd>${selected.balance===null?'Unavailable':esc(amount(selected.balance))}</dd></div></dl><dl class="workflow-facts"><div><dt>Event date</dt><dd>${esc(selected.job.eventDate||'Not recorded')}</dd></div><div><dt>Subjects</dt><dd>${esc(selected.media?.totalSubjects??'—')}</dd></div><div><dt>Images</dt><dd>${esc(selected.media?.totalAssets??'—')}</dd></div></dl></div></div>${selected.error?'<p role="alert">Payment status could not be loaded: '+esc(selected.error)+'</p>':''}<footer class="workflow-detail-actions">${button('notice','Send Pickup Notice',true)}${button('payment','Record Payment')}${button('statement','View Statement')}${ready(selected)?button('fulfill','Record collection / delivery'):''}${button('open-job','Open Job')}</footer></section>`:'<p>No jobs are ready for delivery.</p>';
  if(detailOnly){
   genericView.querySelector('.workflow-detail').outerHTML=detail;
   genericView.querySelectorAll('[data-delivery-row]').forEach(row=>{const active=row.dataset.deliveryRow===d.selected;row.setAttribute('aria-selected',String(active));row.querySelector('input[type=radio]').checked=active;});
   bind(genericView);
  }else shellPage('delivery','Delivery','<div class="workflow-toolbar"><label>Search jobs and clients<input type="search" data-delivery-search value="'+esc(d.term)+'" placeholder="Find a delivery…"></label><button type="button" class="action" data-delivery-refresh>Refresh</button></div>'+table+detail,[['Ready for collection',d.rows.filter(ready).length],['Awaiting payment',d.rows.filter(r=>r.balance>0).length],['Delivered',d.rows.filter(r=>['Completed','Delivered'].includes(r.job.status)).length]]);
  StudioPage.setCount(rows.length+' jobs');
  if(selected&&!selected.mediaRequested){selected.mediaRequested=true;json('/api/jobs/'+selected.job.id+'/media?limit=1').then(media=>{if(delivery===d&&document.body.dataset.studioView==='delivery'){selected.media=media;if(d.selected===selected.job.id)paintDelivery(true);}}).catch(()=>{});}
  genericView.querySelectorAll('[data-delivery-row]').forEach(row=>{const select=()=>{d.selected=row.dataset.deliveryRow;paintDelivery(true);};row.onclick=select;row.onkeydown=e=>{if(['Enter',' '].includes(e.key)){e.preventDefault();select();}};});
  genericView.querySelector('[data-delivery-refresh]').onclick=()=>showDelivery().catch(e=>alert(e.message));
  genericView.querySelector('[data-delivery-search]').oninput=e=>{const input=e.target,start=input.selectionStart;d.term=input.value;paintDelivery();const el=genericView.querySelector('[data-delivery-search]');el.focus();el.setSelectionRange(start,start);};
 }
 return {actions,bind,run,showSessionPlan,showDelivery};
})();

