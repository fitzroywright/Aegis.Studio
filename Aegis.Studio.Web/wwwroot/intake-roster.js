(function(root){
 'use strict';
 root.openIntakeRoster=async function(cv){
  const data=await json(`/api/intake/${cv}/roster`),dialog=studioDialog('Subjects and roster','<div data-intake-roster></div>');
  StudioRosterEditor.open(dialog.querySelector('[data-intake-roster]'),{...data,existingReferences:data.rows.map(x=>x.reference),readFile:async file=>{const f=new FormData();f.append('file',file);return (await json(`/api/intake/${cv}/roster-file`,{method:'POST',body:f})).cells;},save:async value=>json(`/api/intake/${cv}/roster`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(value)})});
 };
 root.openIntakeDeliveryStatus=async function(cv){
  const s=await json(`/api/intake/${cv}/status`),dialog=studioDialog('Intake and outbound status','<div data-intake-status></div>'),host=dialog.querySelector('[data-intake-status]');
  const title=document.createElement('p');title.textContent='Intake reference: '+(s.intakeReference||'Legacy intake');host.append(title);
  for(const d of s.deliveries){const p=document.createElement('p');p.textContent=`${d.status} · ${d.providerAcceptedAtUtc?'Provider accepted '+new Date(d.providerAcceptedAtUtc).toLocaleString():'No provider acceptance recorded'} · Attempts ${d.attempts} · Delivery ${d.id}${d.error?' · '+d.error:''}`;host.append(p);}
 };
})(window);
