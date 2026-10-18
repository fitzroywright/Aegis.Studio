window.StudioClientReminders=(()=>{
 'use strict';
 const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const api=(url,method,body)=>json(url,method?{method,headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}:undefined);
 const date=v=>new Date(v).toLocaleString();
 const local=v=>{const d=new Date(v);return new Date(+d-d.getTimezoneOffset()*60000).toISOString().slice(0,16);};
 const base=job=>'/api/jobs/'+encodeURIComponent(job)+'/client-reminders';
 const fill=(text,preview,source)=>text.replace(/\{(client|job|detail|location)\}/g,(_,key)=>key==='client'||key==='job'?preview[key]||'':source?.[key]||'');
 function rows(items){return items.map(r=>`<tr data-rem-row><td><button type="button" class="action" data-rem-job="${esc(r.jobId)}">${esc(r.jobTitle)}</button><small>${esc(r.kind)} · ${r.automatic?'Automatic':'Staff scheduled'}</small></td><td>${esc(date(r.scheduledAtUtc))}</td><td>${esc(r.channel)}<small>${esc(r.recipient)}</small></td><td>${esc(r.status)}${r.deliveryStatus?' · '+esc(r.deliveryStatus):''}<small>${esc(r.deliveryError||r.error||'')}</small></td><td><details><summary>${esc(r.subject)}</summary><p style="white-space:pre-wrap">${esc(r.body)}</p><button type="button" class="action" data-rem-history="${esc(r.id)}" data-rem-job-id="${esc(r.jobId)}">Change history</button><div data-rem-history-content></div>${r.providerAcceptedAtUtc?'<p>Provider accepted: '+esc(date(r.providerAcceptedAtUtc))+'</p>':''}</details></td><td>${r.canManage&&['Scheduled','Failed','Queued'].includes(r.status)?`${r.status==='Scheduled'?`<button type="button" class="action" data-rem-action="send" data-rem-id="${esc(r.id)}" data-rem-job-id="${esc(r.jobId)}">Send now</button><button type="button" class="action" data-rem-action="edit" data-rem-id="${esc(r.id)}" data-rem-job-id="${esc(r.jobId)}">Edit</button><button type="button" class="action" data-rem-action="postpone" data-rem-id="${esc(r.id)}" data-rem-job-id="${esc(r.jobId)}">Postpone</button>`:r.status==='Failed'&&!r.deliveryStatus?`<button type="button" class="action" data-rem-action="retry" data-rem-id="${esc(r.id)}" data-rem-job-id="${esc(r.jobId)}">Retry</button>`:''}<button type="button" class="action" data-rem-action="cancel" data-rem-id="${esc(r.id)}" data-rem-job-id="${esc(r.jobId)}">Cancel</button>`:''}</td></tr>`).join('')||'<tr><td colspan="6">No reminders yet. Open a job to schedule one or enable an automatic rule.</td></tr>';}
 function table(items){return `<div style="overflow:auto"><table class="sj-table"><thead><tr><th>Job / Type</th><th>Scheduled</th><th>Channel / Recipient</th><th>Status</th><th>Message</th><th>Actions</th></tr></thead><tbody>${rows(items)}</tbody></table></div><p role="alert" data-rem-error></p>`;}
 function bind(host,items,refresh){
  host.querySelectorAll('[data-rem-history]').forEach(b=>b.onclick=async()=>{const target=b.nextElementSibling;b.disabled=true;try{const events=await api(base(b.dataset.remJobId)+'/'+b.dataset.remHistory+'/history');target.innerHTML=events.map(e=>'<p>'+esc(date(e.at))+' · '+esc(e.snapshot.status)+' · '+esc(e.actor||'')+' · Scheduled '+esc(date(e.snapshot.scheduled_at_utc))+'</p>').join('')||'<p>No changes recorded.</p>';}catch(e){target.textContent=e.message;}finally{b.disabled=false;}});
  host.querySelectorAll('[data-rem-job]').forEach(b=>b.onclick=()=>open(b.dataset.remJob));
  host.querySelectorAll('[data-rem-action]').forEach(b=>b.onclick=async()=>{
   const item=items.find(r=>r.id===b.dataset.remId),action=b.dataset.remAction;
   if(action==='postpone'||action==='edit'){
    const d=studioDialog(action==='edit'?'Edit reminder':'Postpone reminder',`<form>${action==='edit'?`<label>Subject<input name="subject" maxlength="200" required value="${esc(item.subject)}"></label><label>Message<textarea name="body" maxlength="4000" required>${esc(item.body)}</textarea></label>`:`<label>New time (your local time)<input type="datetime-local" name="due" required value="${esc(local(Date.now()+3600000))}"></label>`}<button type="submit" class="primary">Save</button><p role="alert"></p></form>`);
    d.querySelector('form').onsubmit=async e=>{e.preventDefault();const f=e.currentTarget;try{await api(base(item.jobId)+'/'+item.id,'POST',action==='edit'?{action,subject:f.elements.subject.value,body:f.elements.body.value}:{action,scheduledAtUtc:new Date(f.elements.due.value).toISOString()});d.close();await refresh();}catch(error){f.querySelector('[role=alert]').textContent=error.message;}};return;
   }
   b.disabled=true;try{await api(base(item.jobId)+'/'+item.id,'POST',{action});await refresh();}catch(e){host.querySelector('[data-rem-error]').textContent=e.message;b.disabled=false;}
  });
 }
 async function open(job){
  const d=studioDialog('Client reminders','<div data-rem-host role="status">Loading reminders…</div>'),host=d.querySelector('[data-rem-host]');let revision=0;
  async function refresh(){const token=++revision;try{
   const view=await api(base(job));if(!d.isConnected||token!==revision)return;
   host.removeAttribute('role');host.innerHTML=`<p>Reminders for <strong>${esc(view.preview.job)}</strong>. Messages are checked again before delivery. Sending hours: 9 AM–6 PM (${esc(view.preview.timeZone)}), including Send now. Delivery may take a minute.</p>${view.canManage?`<details><summary>Communication preferences</summary><form data-rem-prefs><label><input type="checkbox" name="email" ${view.preferences.emailAllowed?'checked':''}> Client permits email reminders for this job</label><label><input type="checkbox" name="whatsapp" ${view.preferences.whatsAppAllowed?'checked':''}> Client permits WhatsApp reminders for this job</label><p>Email: ${esc(view.preview.email||'Not set')} · Phone: ${esc(view.preview.phone||'Not set')}</p><button class="action" type="submit">Save preferences</button><p role="alert"></p></form></details><div data-rem-composer></div><details><summary>Automatic reminder rules</summary><div data-rem-rules></div></details>`:''}<h3>Reminder history</h3>${table(view.items)}`;
   bind(host,view.items,refresh);
   if(!view.canManage)return;
   const prefs=host.querySelector('[data-rem-prefs]');prefs.onsubmit=async e=>{e.preventDefault();try{await api(base(job)+'/preferences','PUT',{emailAllowed:prefs.elements.email.checked,whatsAppAllowed:prefs.elements.whatsapp.checked});await refresh();}catch(error){prefs.querySelector('[role=alert]').textContent=error.message;}};
   composer(host.querySelector('[data-rem-composer]'),job,view,refresh);
   rules(host.querySelector('[data-rem-rules]'),job,view,refresh);
  }catch(e){if(d.isConnected)host.innerHTML='<p role="alert">'+esc(e.message)+'</p><button type="button" class="action" data-rem-retry>Retry</button>';host.querySelector('[data-rem-retry]')?.addEventListener('click',refresh);}}
  await refresh();return d;
 }
 function composer(host,job,view,refresh){
  host.innerHTML=`<h3>Schedule a reminder</h3><form><label>Type<select name="kind">${view.templates.map(t=>`<option value="${esc(t.kind)}">${esc(t.label)}</option>`).join('')}</select></label><label>Event<select name="source"></select></label><label>Channel<select name="channel"><option>Email</option><option>WhatsApp</option></select></label><label>Send at (your local time)<input type="datetime-local" name="due" required></label><label>Subject<input name="subject" required maxlength="200"></label><label>Message<textarea name="body" required maxlength="4000" rows="4"></textarea></label><p>Editable preview; no message is sent until the scheduled time and final checks pass.</p><button type="submit" class="primary">Schedule reminder</button><p role="alert"></p></form>`;
  const form=host.querySelector('form');let requestId=crypto.randomUUID();
  function populateSources(){const sources=view.sources.filter(s=>s.kind===form.elements.kind.value);form.elements.source.innerHTML=sources.map(s=>`<option value="${esc(s.sourceId)}">${esc(s.detail||s.kind)} · ${esc(date(s.anchorAtUtc))}</option>`).join('')||'<option value="">No outstanding event</option>';populateTemplate();}
  function populateTemplate(){const t=view.templates.find(t=>t.kind===form.elements.kind.value),s=view.sources.find(s=>s.kind===t.kind&&s.sourceId===form.elements.source.value);form.elements.subject.value=fill(t.subject,view.preview,s);form.elements.body.value=fill(t.body,view.preview,s);form.elements.due.value=local(Math.max(Date.now(),s?+new Date(s.anchorAtUtc)+t.offsetHours*3600000:Date.now()+3600000));}
  form.elements.kind.onchange=populateSources;form.elements.source.onchange=populateTemplate;populateSources();
  form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('[type=submit]');button.disabled=true;try{
   if(form.elements.kind.value!=='FollowUp'&&!form.elements.source.value)throw Error('No outstanding event for this reminder type.');
   await api(base(job),'POST',{id:requestId,kind:form.elements.kind.value,sourceId:form.elements.source.value||null,channel:form.elements.channel.value,scheduledAtUtc:new Date(form.elements.due.value).toISOString(),subject:form.elements.subject.value,body:form.elements.body.value});requestId=crypto.randomUUID();await refresh();
  }catch(error){form.querySelector('[role=alert]').textContent=error.message;button.disabled=false;}};
 }
 function rules(host,job,view,refresh){
  host.innerHTML='<p>Off by default. One reminder per event; at most three automatic reminders of each type per job in seven days. Template edits apply to future reminders.</p>'+view.templates.filter(t=>t.kind!=='FollowUp').map(t=>{const r=view.rules.find(r=>r.kind===t.kind)||{...t,enabled:false,channel:'Email'};return `<details><summary>${esc(t.label)} · ${r.enabled?'On':'Off'}</summary><form data-rem-rule="${esc(t.kind)}"><label><input name="enabled" type="checkbox" ${r.enabled?'checked':''}> Enable automatically</label><label>Channel<select name="channel"><option ${r.channel==='Email'?'selected':''}>Email</option><option ${r.channel==='WhatsApp'?'selected':''}>WhatsApp</option></select></label><label>Hours ${t.kind==='Shoot'?'before shoot':'after event'}<input name="hours" type="number" min="0" max="720" required value="${Math.abs(r.offsetHours)}"></label><label>Subject<input name="subject" maxlength="200" required value="${esc(r.subject)}"></label><label>Message<textarea name="body" maxlength="4000" required rows="3">${esc(r.body)}</textarea></label><p>Placeholders: {client}, {job}, {detail}, {location}</p><button type="submit" class="action">Save rule</button><p role="alert"></p></form></details>`;}).join('');
  host.querySelectorAll('form').forEach(form=>form.onsubmit=async e=>{e.preventDefault();try{await api(base(job)+'/rules','PUT',{kind:form.dataset.remRule,enabled:form.elements.enabled.checked,channel:form.elements.channel.value,offsetHours:Number(form.elements.hours.value)*(form.dataset.remRule==='Shoot'?-1:1),subject:form.elements.subject.value,body:form.elements.body.value});await refresh();}catch(error){form.querySelector('[role=alert]').textContent=error.message;}});
 }
 async function mountClient(host,jobs,isCurrent=()=>true){
  const live=()=>host.isConnected&&isCurrent();
  async function refresh(){
   if(!live())return;
   host.innerHTML='<p role="status">Loading client reminders…</p>';
   const results=new Array(jobs.length);let next=0;
   async function worker(){while(next<jobs.length){if(!live())return;const index=next++;try{results[index]={job:jobs[index],view:await api(base(jobs[index].id))};}catch(error){results[index]={job:jobs[index],error:error.message};}}}
   await Promise.all([worker(),worker(),worker()]);if(!live())return;
   const available=results.filter(r=>r?.view),items=available.flatMap(r=>r.view.items);
   items.sort((a,b)=>new Date(a.scheduledAtUtc)-new Date(b.scheduledAtUtc));
   host.innerHTML=(jobs.length?'<div class="st-tools"><label>Job<select data-rem-client-job><option value="">Choose a job…</option>'+available.map(r=>'<option value="'+esc(r.job.id)+'">'+esc(r.job.title)+'</option>').join('')+'</select></label><button type="button" class="action" data-rem-client-open>Manage reminders</button></div>':'<p>Create a job for this client to schedule reminders.</p>')+results.filter(r=>r?.error).map(r=>'<p role="alert">Reminders unavailable for '+esc(r.job.title)+': '+esc(r.error)+'</p>').join('')+table(items)+'<button type="button" class="action" data-rem-client-refresh>Refresh reminders</button>';
   bind(host,items,refresh);
   host.querySelector('[data-rem-client-refresh]').onclick=refresh;
   const button=host.querySelector('[data-rem-client-open]');
   if(button)button.onclick=async()=>{const job=host.querySelector('[data-rem-client-job]').value;if(!job)return;const d=await open(job);d.addEventListener('close',()=>{if(live())void refresh();},{once:true});};
  }
  await refresh();
 }
 return {open,mountClient};
})();
