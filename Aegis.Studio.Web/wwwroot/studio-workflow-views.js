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
  if(view==='session-plan')return [];
  if(view==='delivery')return [['notice','Send Pickup Notice'],['payment','Record Payment'],['statement','View Statement'],['export','Export Report']];
  if(view==='production')return [['work-order','Open Work Order'],['assign-work','Assign Work']];
  return null;
 }
 function bind(host=document){host.querySelectorAll('[data-workflow-action]').forEach(b=>{if(b.dataset.workflowAction==='import'&&session)b.disabled=!session.roster.canEdit||session.busy;b.onclick=()=>run(b.dataset.workflowAction,b).catch(e=>alert(e.message));});}
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
  if(view==='session-plan'&&session){
   if(key==='job')return studioNavigate('workspaces',{jobId:session.job.id});
   if(key==='template')return download('session-plan-template.csv','name,reference,collection,contactName,contactEmail,contactPhone,notes,pickupNotification\n');
   if(key==='import'){if(!session.roster.canEdit)throw Error('You do not have permission to import subjects.');session.step=1;paintSession();return;}
  }
  if(view==='production'&&(key==='work-order'||key==='assign-work')){
   const cards=[...document.querySelectorAll('[data-order]')];
   if(!cards.length)throw Error('No work orders match the current filters.');
   if(cards.length===1)return cards[0].click();
   const d=studioDialog(key==='assign-work'?'Assign Work':'Open Work Order',cards.map((c,i)=>`<button type="button" class="action" data-pick-order="${i}">${esc(c.closest('article').textContent)}</button>`).join(''));
   d.querySelectorAll('[data-pick-order]').forEach(b=>b.onclick=()=>{d.close();cards[Number(b.dataset.pickOrder)].click();});return;
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
 async function showSessionPlan(jobId){
  const current=StudioPage.begin('session-plan');
  if(!jobId)throw Error('Open Session Plan from Job Details.');
  genericView.innerHTML='<p role="status">Loading Session Plan…</p>';
  const [job,subjects,access]=await Promise.all([json('/api/jobs/'+jobId),json('/api/jobs/'+jobId+'/session-plan'),json('/api/jobs/'+jobId+'/access')]);
  if(!job.conversationId)throw Error('This job has no linked intake conversation. Link its intake before importing a Session Plan.');
  const roster=await json('/api/intake/'+job.conversationId+'/roster');if(!current())return;
  session={job,subjects:subjects.filter(s=>!s.isSystem),access:access.permissions||access,roster,step:0,cells:[],mapping:{},preview:[],errors:[],message:roster.incomingRows?'A conflicting website roster is waiting for review. Importing this file does not dismiss that conflict.':'',busy:false};
  paintSession();
 }
 function paintSession(){
  const s=session,subjects=s.subjects,importing=s.step>0;
  const outcome=r=>r.status==='Needs Retake'?'Reshoot':r.status||'Not Photographed';
  const overview=[['Subjects',subjects.length],['Photographed',subjects.filter(r=>outcome(r)==='Photographed').length],['Absent',subjects.filter(r=>outcome(r)==='Absent').length],['Reshoots',subjects.filter(r=>/reshoot|retake/i.test(outcome(r))).length]];
  const steps=['Choose file','Map columns','Preview & validate','Import'];
  const stepper=importing?'<ol class="workflow-import-steps">'+steps.map((label,i)=>`<li ${s.step===i+1?'aria-current="step"':''}><span>${i+1}</span>${label}</li>`).join('')+'</ol>':'';
  const rows=s.step===3?s.preview:subjects;
  const table=`<div class="workflow-table-scroll"><table class="workflow-table"><thead><tr><th>Subject</th><th>Group</th><th>Outcome</th><th>Photos</th></tr></thead><tbody>${rows.map(r=>`<tr><td><strong>${esc(r.name)}</strong><small>${esc(r.reference||'')}</small></td><td>${esc(r.collection||'General')}</td><td><span class="workflow-pill" data-tone="${/absent|reshoot|retake/i.test(outcome(r))?'danger':outcome(r)==='Photographed'?'success':'neutral'}">${esc(s.step===3&&!r.status?'New subject':outcome(r))}</span></td><td>${r.imageCount===undefined?'—':esc(r.imageCount)}</td></tr>`).join('')||'<tr><td colspan="4">No subjects yet. Import subjects to plan this session.</td></tr>'}</tbody></table></div>`;
  let controls='';
  if(s.step===1)controls='<section class="workflow-detail"><h3>Choose a roster</h3><label>CSV or XLSX<input type="file" accept=".csv,.xlsx" data-session-file></label><label>Or paste spreadsheet cells<textarea data-session-paste placeholder="Include the column headers"></textarea></label><button type="button" class="primary" data-session-pasted>Continue to column mapping</button></section>';
  if(s.step===2)controls='<section class="workflow-detail"><h3>Map columns</h3><div class="workflow-column-map">'+StudioRosterEditor.fields.map((field,i)=>`<label>${esc(StudioRosterEditor.labels[i])}<select data-session-map="${field}"><option value="">Not supplied</option>${s.cells[0].map((h,j)=>`<option value="${j}" ${String(s.mapping[field])===String(j)?'selected':''}>${esc(h||'Column '+(j+1))}</option>`).join('')}</select></label>`).join('')+'</div><button type="button" class="primary" data-session-preview>Preview & validate</button></section>';
  if(s.step===3)controls=`<section class="workflow-validation" role="status"><strong>${s.preview.length} subjects · ${s.errors.length} warnings</strong><span>Review any warnings before importing.</span><button type="button" class="action" data-session-warnings>Review warnings</button></section><div data-session-errors hidden>${s.errors.map(e=>'<p>'+esc(e)+'</p>').join('')||'<p>No validation warnings.</p>'}</div>`;
  const commit=s.step===3?'<footer class="workflow-detail-actions"><button type="button" class="primary" data-session-commit '+(s.busy||s.errors.length?'disabled':'')+'>Import Subjects</button><button type="button" class="action" data-session-back>Back to mapping</button></footer>':'';
  shellPage('session-plan','Session Plan',`<header class="workflow-page-title"><h1>${esc(s.job.title)}</h1><p>Import and plan subjects for this job.</p></header><div class="workflow-detail-actions" role="toolbar" aria-label="Session Plan actions">${button('import','Import Subjects',true)}${button('template','Download Template')}${button('job','Back to Job')}</div>${stepper}${controls}${s.step!==1&&s.step!==2?table:''}${commit}<p role="status" data-session-feedback>${esc(s.message)}</p><p class="workflow-note">Subject outcomes and photographs are tracked separately. Uploading photographs does not complete a session.</p>`,overview);
  StudioPage.setCount((s.step===3?s.preview.length:subjects.length)+' subjects');
  const host=genericView;
  const prepare=cells=>{if(cells.length<2)throw Error('Supply a header and at least one row.');s.cells=cells;s.mapping={};const norm=x=>String(x).replace(/[^a-z]/gi,'').toLowerCase();StudioRosterEditor.fields.forEach((field,i)=>{const index=cells[0].findIndex(h=>norm(h)===norm(field)||norm(h)===norm(StudioRosterEditor.labels[i])||field==='reference'&&['studentid','subjectid'].includes(norm(h))||field==='name'&&norm(h)==='studentname'||field==='collection'&&['class','group'].includes(norm(h))||field==='contactEmail'&&norm(h)==='parentemail');if(index>=0)s.mapping[field]=index;});s.step=2;paintSession();};
  const fail=e=>{if(session===s&&document.body.dataset.studioView==='session-plan')host.querySelector('[data-session-feedback]')?.replaceChildren(document.createTextNode(e.message));};
  host.querySelector('[data-session-file]')?.addEventListener('change',async e=>{const file=e.target.files[0];if(!file)return;try{const form=new FormData();form.append('file',file);const data=await json('/api/intake/'+s.job.conversationId+'/roster-file',{method:'POST',body:form});if(session===s&&document.body.dataset.studioView==='session-plan')prepare(data.cells);}catch(e){fail(e);}});
  host.querySelector('[data-session-pasted]')?.addEventListener('click',()=>{try{prepare(StudioRosterEditor.csv(host.querySelector('[data-session-paste]').value));}catch(e){fail(e);}});
  host.querySelector('[data-session-preview]')?.addEventListener('click',()=>{s.mapping=Object.fromEntries([...host.querySelectorAll('[data-session-map]')].map(el=>[el.dataset.sessionMap,el.value]));const incoming=StudioRosterEditor.mapRows(s.cells,s.mapping);s.preview=StudioRosterEditor.mergeRows(s.roster.rows,incoming).map(r=>({...subjects.find(x=>x.id===r.subjectId||x.reference===r.reference),...r}));s.errors=StudioRosterEditor.validate(s.preview);if(s.preview.length>5000)s.errors.push('Maximum 5000 subjects.');s.step=3;paintSession();});
  host.querySelector('[data-session-back]')?.addEventListener('click',()=>{s.step=2;paintSession();});
  host.querySelector('[data-session-warnings]')?.addEventListener('click',()=>{const el=host.querySelector('[data-session-errors]');el.hidden=!el.hidden;});
  host.querySelector('[data-session-commit]')?.addEventListener('click',async()=>{if(s.busy||s.errors.length)return;s.busy=true;paintSession();try{await json('/api/intake/'+s.job.conversationId+'/roster',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({revision:s.roster.revision,rows:s.preview,resolveConflict:false})});if(session===s&&document.body.dataset.studioView==='session-plan'){await showSessionPlan(s.job.id);if(document.body.dataset.studioView==='session-plan'&&session?.job.id===s.job.id){session.step=4;session.message='Subjects imported. Existing photo associations are retained.';paintSession();}}}catch(e){s.busy=false;if(session===s&&document.body.dataset.studioView==='session-plan'){s.message=e.message;paintSession();}}});
 }
 async function showDelivery(){
  const current=StudioPage.begin('delivery');
  const role=await loadFrontDeskRole();const me=await json('/api/auth/me');
  if(!role?.frontDesk&&!(me.user||me).isAdministrator)throw Error('Front Desk access is required to review delivery and payments.');
  genericView.innerHTML='<p role="status">Loading Delivery…</p>';
  const jobs=await json('/api/jobs');
  const candidates=jobs.filter(j=>['Ready for Client','Ready for Delivery','Published','Completed','Delivered'].includes(j.status));
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
