(function(root){
 'use strict';
 root.openIntakeRoster=async function(cv){
  const data=await json(`/api/intake/${cv}/roster`),dialog=studioDialog('Subjects and roster','<div data-intake-roster></div>');
  let resolveConflict=false;const rosterHost=dialog.querySelector('[data-intake-roster]');
  const render=(rows)=>StudioRosterEditor.open(rosterHost,{...data,rows,existingReferences:data.rows.map(x=>x.reference),readFile:async file=>{const f=new FormData();f.append('file',file);return (await json(`/api/intake/${cv}/roster-file`,{method:'POST',body:f})).cells;},save:async value=>json(`/api/intake/${cv}/roster`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({...value,resolveConflict})})});
  render(data.rows);
  if(data.incomingRows){const b=document.createElement('button');b.type='button';b.textContent='Review conflicting website roster';b.onclick=()=>{resolveConflict=true;render(data.incomingRows);};rosterHost.before(b);}
  if(data.jobId){
   const b=document.createElement('button');b.type='button';b.textContent='Message selected subjects';rosterHost.before(b);b.onclick=async()=>{
    const latest=await json(`/api/intake/${cv}/roster`),d=studioDialog('Choose subject recipients','<form><div data-recipients></div><label>Subject<input name="subject" maxlength="200" required></label><label>Message<textarea name="body" maxlength="4000" required></textarea></label><p>Each selected contact receives a separate delivery.</p><button type="submit">Queue messages</button><p role="alert"></p></form>'),form=d.querySelector('form'),list=d.querySelector('[data-recipients]'),operationId=crypto.randomUUID();
    for(const r of latest.rows.filter(x=>x.subjectId&&x.contactEmail)){const label=document.createElement('label'),c=document.createElement('input');c.type='checkbox';c.dataset.subjectId=r.subjectId;c.dataset.email=r.contactEmail;label.append(c,document.createTextNode(`${r.name} — ${r.contactEmail}`));list.append(label);}
    form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('[type=submit]');button.disabled=true;try{const recipients=[...form.querySelectorAll('[data-subject-id]:checked')].map(x=>({subjectId:x.dataset.subjectId,email:x.dataset.email}));if(!recipients.length)throw Error('Select at least one contact.');const result=await json(`/api/jobs/${data.jobId}/subjects/messages`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({operationId,recipients,subject:form.elements.subject.value,body:form.elements.body.value})});form.querySelector('[role=alert]').textContent=`${result.deliveryIds.length} deliveries queued. Check intake / email status for provider acceptance.`;}catch(e){form.querySelector('[role=alert]').textContent=e.message;}finally{button.disabled=false;}};
   };
  }

 };
 root.openIntakeDeliveryStatus=async function(cv){
  const s=await json(`/api/intake/${cv}/status`),dialog=studioDialog('Intake and outbound status','<div data-intake-status></div>'),host=dialog.querySelector('[data-intake-status]');
  const title=document.createElement('p');title.textContent='Intake reference: '+(s.intakeReference||'Legacy intake');host.append(title);
  if(s.rosterConflict){const p=document.createElement('p');p.textContent='Website roster changes need review. Open Subjects / roster.';host.append(p);}
  for(const f of s.attachments||[]){const p=document.createElement('p');p.textContent=f.fileName+' · '+f.state;host.append(p);}
  for(const d of s.deliveries){const p=document.createElement('p');p.textContent=`${d.status} · ${d.providerAcceptedAtUtc?'Provider accepted '+new Date(d.providerAcceptedAtUtc).toLocaleString():'No provider acceptance recorded'} · Attempts ${d.attempts} · Delivery ${d.id}${d.error?' · '+d.error:''}`;host.append(p);}
 };
})(window);
