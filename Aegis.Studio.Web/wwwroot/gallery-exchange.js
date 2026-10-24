// One review dialog, opened from Front Desk and the existing Job media toolbar.
window.openGalleryExchange=async function(jobId=null){
 const dialog=document.createElement('dialog');dialog.className='studio-dialog';dialog.style.maxWidth='960px';dialog.style.width='94vw';
 dialog.innerHTML='<form method="dialog"><h2>Client Gallery Requests</h2><button class="action">Close</button></form><p data-result role="status"></p><div data-list></div>';
 document.body.append(dialog);dialog.addEventListener('close',()=>dialog.remove());dialog.showModal();
 const result=dialog.querySelector('[data-result]'),host=dialog.querySelector('[data-list]');
 const api=(path,body)=>json('/api/gallery-exchange/'+path,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
 function element(tag,text){const e=document.createElement(tag);e.textContent=text;return e;}
 async function run(action){try{await action();await load();result.textContent='Saved. External work synchronizes in the background.';}catch(e){result.textContent=e.message;}}
 async function load(){
   const [requests,jobs,capabilities]=await Promise.all([api('requests'+(jobId?'?jobId='+jobId:'')),api('jobs'),json('/api/workflow/capabilities')]);
   host.replaceChildren();
   if(!requests.length)host.append(element('p','No gallery requests are waiting here.'));
   for(const r of requests){
     const card=document.createElement('section');card.className='job-section';
     card.append(element('h3',(r.hints?.name||'Gallery request')+' · '+r.state),element('p','Request: '+r.id+' · Website: '+r.integrationId),
       element('p','Contact hint: '+(r.hints?.email||'')+' · Booking: '+(r.hints?.bookingReference||'')+' · Session: '+(r.hints?.sessionDate||'')));
     const job=document.createElement('select');job.add(new Option('Match the client’s job',''));
     for(const j of jobs)job.add(new Option(j.title+' · '+(j.clientName||'No client assigned'),j.id));
     job.value=r.jobId||jobId||'';const verification=document.createElement('input');verification.placeholder='Trusted-contact verification reference';verification.maxLength=500;verification.value=r.verificationReference||'';
     const reason=document.createElement('input');reason.placeholder='Client-facing review or withdrawal reason';reason.maxLength=1000;reason.value=r.reason||'';
     const approved=document.createElement('input');approved.type='checkbox';
     const confirm=document.createElement('label');confirm.append(approved,document.createTextNode(' I verified this requester’s entitlement through a trusted contact channel.'));
     const publication=document.createElement('select');publication.add(new Option('Choose approved finished publication',''));
     const republish=document.createElement('input');republish.type='checkbox';const republishLabel=document.createElement('label');republishLabel.append(republish,document.createTextNode(' Explicitly approve a new release after withdrawal'));
     function choose(){publication.replaceChildren(new Option('Choose approved finished publication',''));for(const p of jobs.find(j=>j.id===job.value)?.publications||[])publication.add(new Option((p.reference||p.id)+' · '+new Date(p.createdAt).toLocaleString(),p.id));}
     job.onchange=choose;choose();
     card.append(element('p','A matching name, email or reference alone does not authorize release.'),job,verification,reason,confirm);
     const actions=document.createElement('div');actions.className='job-actions';
     function button(label,action,refresh=true){const b=element('button',label);b.type='button';b.className='action';b.onclick=()=>refresh?run(action):action().catch(e=>result.textContent=e.message);actions.append(b);}
     const selected=()=>jobs.find(j=>j.id===job.value);
     if(capabilities.frontDesk||jobs.find(j=>j.id===r.jobId)?.canReview){
       for(const state of ['Approved','Held','Rejected'])button(state==='Approved'?'Approve verified request':state,async()=>{
         if(state==='Approved'&&(!approved.checked||!verification.value.trim()||!selected()?.clientId))throw new Error('Match the client/job and record trusted-channel verification before approval.');
         await api('requests/'+r.id+'/review',{state,jobId:job.value||null,clientId:selected()?.clientId||null,verificationReference:verification.value,reason:reason.value,expectedVersion:r.decisionVersion});
       });
     }
     if(jobs.find(j=>j.id===r.jobId)?.canPublish){
       card.append(publication,republishLabel);
       button(r.transfers.length?'Publish updated version':'Publish approved gallery',async()=>{
         if(!publication.value)throw new Error('Choose an approved finished publication.');
         await api('requests/'+r.id+'/publish',{publicationId:publication.value,expectedVersion:Math.max(0,...r.transfers.map(t=>t.version)),republishApproval:republish.checked});
       });
       button('Request revocation',async()=>{if(!reason.value.trim())throw new Error('Record a withdrawal reason.');await api('requests/'+r.id+'/revoke',{reason:reason.value});});
     }
     if(capabilities.frontDesk||jobs.find(j=>j.id===r.jobId)?.canReview)button('Retry synchronization',()=>api('requests/'+r.id+'/retry',{}));
     button('Activity history',async()=>{
       const data=await api('requests/'+r.id+'/activity'),panel=document.createElement('details');panel.open=true;panel.append(element('summary','Activity history'));
       const filter=document.createElement('input');filter.placeholder='Filter by event, actor or date';panel.append(filter);const records=document.createElement('div');records.style.maxHeight='35vh';records.style.overflow='auto';panel.append(records);
       function render(){records.replaceChildren();for(const a of data.filter(x=>JSON.stringify(x).toLowerCase().includes(filter.value.toLowerCase())))records.append(element('p',new Date(a.occurredAt).toLocaleString()+' · '+a.actor+' · '+a.action+' · '+JSON.stringify(a.details)));}
       filter.oninput=render;render();card.append(panel);
     },false);
     card.append(actions);
     for(const t of r.transfers)card.append(element('p','Publication version '+t.version+': '+t.state));
     for(const v of r.revocations){
       const labels={Requested:'Revocation requested · awaiting external receipt',Received:'External site received revocation',Completed:'External site confirmed revocation',Failed:'External revocation failed · follow-up required'};
       card.append(element('p',(v.overdue?'OVERDUE · ':'')+(labels[v.state]||v.state)+' · '+v.id));
     }
     for(const q of r.queue)card.append(element('p',q.kind+' · Pending synchronization · Attempts: '+q.attempts+(q.error?' · '+q.error:'')+(q.lastAttemptAt?' · Last attempt: '+new Date(q.lastAttemptAt).toLocaleString():'')+' · Next attempt: '+new Date(q.nextAttemptAt).toLocaleString()));
     if(r.pollError)card.append(element('p','External status refresh will retry: '+r.pollError));
     const external=r.remote?.revocations||[];for(const v of external)if(v.acknowledgment){let a;try{a=JSON.parse(v.acknowledgment);}catch{}if(a?.limitations)card.append(element('p','External deletion report: '+a.limitations));}
     host.append(card);
   }
 }
 try{await load();}catch(e){result.textContent=e.message;}
};

window.openRemoteMediaReview=async function(jobId){
 const dialog=document.createElement('dialog');dialog.className='studio-dialog';dialog.style.maxWidth='850px';dialog.style.width='94vw';
 dialog.innerHTML='<form method="dialog"><h2>Remote media imports</h2><button class="action">Close</button></form><p data-result role="status"></p><div data-files></div>';
 document.body.append(dialog);dialog.addEventListener('close',()=>dialog.remove());dialog.showModal();
 const result=dialog.querySelector('[data-result]');
 async function load(){
  const host=dialog.querySelector('[data-files]');host.replaceChildren();
  const files=await json('/api/jobs/'+jobId+'/remote-media');
  if(!files.length){const p=document.createElement('p');p.textContent='No remote import results recorded for this job.';host.append(p);}
  for(const file of files){
   const card=document.createElement('section');card.className='job-section';const label=document.createElement('p');
   label.textContent=(file.metadata.fileName||file.uploadId)+' · '+file.result.state+' · '+(file.metadata.destination?.collection||'')+' · '+(file.result.code||'');card.append(label);
   if(file.result.state==='NeedsReview'&&file.canRetry){
    const input=document.createElement('input');input.placeholder='Correction made to the job, destination or permissions';input.maxLength=1000;const retry=document.createElement('button');retry.textContent='Retry original destination';retry.className='action';
    retry.onclick=async()=>{retry.disabled=true;try{if(!input.value.trim())throw new Error('Record the correction before retrying.');await json('/api/jobs/'+jobId+'/remote-media/'+file.uploadId+'/retry',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({reason:input.value.trim()})});result.textContent='Retry queued for the original destination. Synchronization can proceed when the API is available.';}catch(e){result.textContent=e.message;}finally{retry.disabled=false;}};
    card.append(input,retry);
   }
   host.append(card);
  }
 }
 try{await load();}catch(e){result.textContent=e.message;}
};
