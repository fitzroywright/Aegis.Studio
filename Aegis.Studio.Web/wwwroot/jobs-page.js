// Jobs has one renderer and one scoped stylesheet. The shell owns navigation cards.
(function () {
 'use strict';
 let active;
 const esc=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const array=value=>Array.isArray(value)?value:[];
 const tone=status=>/cancel|changes/i.test(status)?'rose':/completed|delivered|ready for delivery/i.test(status)?'green':/approval|review/i.test(status)?'purple':/production|upload/i.test(status)?'blue':/scheduled/i.test(status)&&!/awaiting/i.test(status)?'teal':'amber';
 const date=value=>{if(!value)return null;const d=new Date(/^\d{4}-\d{2}-\d{2}$/.test(value)?value+'T12:00:00':value);return Number.isFinite(+d)?d:null;};
 const dateLabel=value=>date(value)?.toLocaleDateString('en-US',{weekday:'short',month:'short',day:'2-digit',year:'numeric'}).replaceAll(',','')||'Not scheduled';
 async function render(host,options) {
  active?.dispose();
  let disposed=false;
  const controller={dispose(){disposed=true;host.onclick=null;host.onkeydown=null;}};active=controller;
  const current=()=>!disposed&&options.isCurrent();
  host.innerHTML='<div class="sj-loading" role="status">Loading jobs…</div>';
  try {
   const [jobData,workflowResult,notificationResult,peopleResult,authResult,taskResult]=await Promise.all([
    options.json('/api/jobs'),
    options.json('/api/workflow/jobs').then(value=>({value:array(value)}),()=>({error:true,value:[]})),
    options.json('/api/notifications?count=100').then(value=>({value:array(value)}),()=>({error:true,value:[]})),
    options.json('/api/studio-employees?assignable=true').catch(()=>[]),
    options.json('/api/auth/me').catch(()=>({})),
    options.json('/api/today/tasks').catch(()=>[])
   ]);
   if(!current())return controller;
   const jobs=array(jobData),workspaces=array(options.workspaces).filter(w=>w.permissions?.accessWorkspace!==false&&w.permissions?.view!==false),workspacesById=new Map(workspaces.map(w=>[String(w.id),w]));
   const flows=new Map(workflowResult.value.map(w=>[String(w.jobId),w]));
   const people=new Map(array(peopleResult).map(p=>[String(p.userName).toLowerCase(),p.displayName||p.fullName||p.userName]));
   const belongs=(job,id)=>!id||String(job.workspaceId)===id||array(job.participatingWorkspaceIds).map(String).includes(id);
   const status=job=>flows.get(String(job.id))?.stage||job.status||'Status unavailable';
   const owner=job=>flows.get(String(job.id))?.owner||job.jobLead||job.assignedTo||'';
   const ownerLabel=job=>people.get(String(owner(job)).toLowerCase())||owner(job)||'Unassigned';
   const me=authResult?.user||authResult||{};
   const user=String(me.userName||me.loginName||'').trim().toLowerCase();
   const normalized=value=>String(value||'').trim().toLowerCase();
   const tasks=array(taskResult);
   const isLead=job=>Boolean(user)&&normalized(job.jobLead||flows.get(String(job.id))?.jobLead)===user;
   const isInvolved=job=>isLead(job)||Boolean(user)&&[job.assignedTo,job.jobOwner,job.photographerUserName,owner(job)].some(value=>normalized(value)===user)||job.isCrewMember===true||tasks.some(task=>String(task.jobId)===String(job.id)&&normalized(task.assignedTo)===user)||(array(job.crew).some(member=>normalized(member.userName||member)===user));
   let saved={};try{saved=JSON.parse(sessionStorage.getItem('studio-jobs-view')||'{}');}catch{}
   let view=['led','involved','all'].includes(saved.view)?saved.view:'involved';
   let workspace=String(options.workspaceFilter??''),term=String(saved.term||''),stage=String(saved.stage||''),sort=saved.sort==='date-asc'?'date-asc':'date-desc',calendarView=['day','week','month'].includes(saved.calendarView)?saved.calendarView:'day',calendarDate=date(saved.calendarDate)||new Date(),calendarEnabled=saved.calendarEnabled!==false,attention='',includeClosed=false,page=1;
   const stages=StudioStatusOrder.sort(jobs.map(status));if(!stages.includes(stage))stage='';
   const card=(title,body,cls='')=>`<section class="sj-card ${cls}"><header><h2>${title}</h2></header>${body}</section>`;
   host.innerHTML=`<section class="sj-page" aria-label="Jobs"><section class="sj-board">
    <div class="sj-toolbar" hidden><button type="button" class="sj-gold" data-sj-settings hidden>Workspace settings</button></div>
    <div class="sj-summary"><div class="mw-today-view-tabs sj-view-tabs" role="group" aria-label="Job view"><button type="button" data-sj-view="led">Jobs Led By Me</button><button type="button" data-sj-view="involved">Jobs I’m Involved In</button><button type="button" data-sj-view="all">All Jobs</button></div><div class="sj-calendar mw-today-calendar-tools" aria-label="Filter jobs by event date"><div class="mw-calendar-view-switch" role="group" aria-label="Calendar view"><button type="button" data-sj-calendar-view="month">Month</button><button type="button" data-sj-calendar-view="week">Week</button><button type="button" data-sj-calendar-view="day">Day</button></div><div class="mw-day-nav" aria-label="Jobs date navigator"><button type="button" class="mw-day-step" data-sj-calendar-step="-1" aria-label="Previous date range">‹</button><button type="button" class="mw-day-current" data-sj-calendar-today title="Return to today">Today</button><button type="button" class="mw-day-step" data-sj-calendar-step="1" aria-label="Next date range">›</button><div class="mw-calendar-control"><button type="button" class="mw-calendar-trigger" data-sj-calendar-toggle aria-label="Choose event date" title="Choose event date"><svg aria-hidden="true" width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="5" width="18" height="16" rx="2"/><path d="M7 3v4M17 3v4M3 10h18"/><path d="m9 15 2 2 4-4"/></svg></button><input type="date" data-sj-calendar-date aria-label="Choose event date"></div></div></div></div>
    <div class="sj-error" role="alert" data-sj-error hidden></div>
    <div class="sj-table-frame"><div class="sj-table-head-clip"><table class="sj-table"><colgroup><col style="width:30%"><col style="width:17%"><col style="width:18%"><col style="width:18%"><col style="width:17%"></colgroup><thead><tr><th><div class="sj-job-header"><span>Job / Client</span><input type="search" data-sj-search aria-label="Search jobs, clients or references" placeholder="Search jobs…" autocomplete="off"></div></th><th><label class="sj-column-filter sj-column-filter-inline"><span>Event date</span><select data-sj-sort aria-label="Sort by event date"><option value="date-desc">Newest first ↓</option><option value="date-asc">Oldest first ↑</option></select></label></th><th>Job Owner</th><th><label class="sj-column-filter sj-column-filter-inline"><span>Status</span><select data-sj-status aria-label="Filter by status"><option value="">All Open Jobs</option>${stages.map(s=>`<option>${esc(s)}</option>`).join('')}</select></label></th><th>Next Action</th></tr></thead></table></div><div class="sj-table-scroll"><table class="sj-table"><colgroup><col style="width:30%"><col style="width:17%"><col style="width:18%"><col style="width:18%"><col style="width:17%"></colgroup><tbody></tbody></table></div></div>
    <footer class="sj-pagination"><span data-sj-range></span><div><button type="button" data-sj-prev aria-label="Previous page">‹</button><span data-sj-page></span><button type="button" data-sj-next aria-label="Next page">›</button></div></footer>
   </section><div data-studio-context>${card('Overview','<div class="sj-card-body" data-sj-statuses></div>')}<div data-studio-attention><div data-sj-attention></div></div>${StudioMessageCard.html({id:'jobsMessageCard',title:'Messages',body:'<div data-sj-messages><p>Loading messages…</p></div>',footer:'<button type="button" data-sj-message>✉ Send Message →</button>'})}</div></section>`;
   const find=s=>StudioPage.query(host,s);
   host.querySelector('[data-studio-context] > section').dataset.railCard='overview';
   StudioPage.mountContext(host,'jobs');
      StudioMessageCard.bind(find('.smc-card'));
   find('[data-sj-search]').value=term;find('[data-sj-status]').value=stage;find('[data-sj-sort]').value=sort;
   host.studioJobsWorkspaceCounts=Object.fromEntries(workspaces.map(w=>[String(w.id),jobs.filter(j=>belongs(j,String(w.id))).length]));
   window.dispatchEvent(new CustomEvent('studio-jobs-view-changed'));
   async function open(id){if(!current())return;const error=find('[data-sj-error]');error.hidden=true;try{await options.openJob(id);}catch(e){if(current()){error.hidden=false;error.textContent='Unable to open job: '+e.message;}}}
   const need=(job,key)=>key==='owner'?!owner(job)&&!/completed|delivered|cancelled/i.test(status(job)):key==='changes'?/changes requested/i.test(status(job)):/needs photographer assignment/i.test(status(job));
   function apply(reset=true){
    if(!current())return;if(reset)page=1;
    const base=jobs.filter(j=>(view==='all'||(view==='led'?isLead(j):isInvolved(j)))&&belongs(j,workspace)&&(stage ? status(j)===stage : !StudioSubjectPicker.isClosed({...j,stage:status(j)})));
    StudioPage.queryAll(host,'[data-sj-view]').forEach(button=>{const selected=button.dataset.sjView===view;button.classList.toggle('active',selected);button.setAttribute('aria-pressed',String(selected));});
    StudioPage.queryAll(host,'[data-sj-calendar-view]').forEach(button=>{const selected=calendarEnabled&&button.dataset.sjCalendarView===calendarView;button.classList.toggle('active',selected);button.setAttribute('aria-pressed',String(selected));});
    const isoDate=d=>[d.getFullYear(),String(d.getMonth()+1).padStart(2,'0'),String(d.getDate()).padStart(2,'0')].join('-');
    find('[data-sj-calendar-date]').value=isoDate(calendarDate);
    calendarEnabled=true;
    const selectedDate=new Date(calendarDate.getFullYear(),calendarDate.getMonth(),calendarDate.getDate());
    const today=new Date();today.setHours(0,0,0,0);
    let selectedDateLabel;
    if(calendarView==='month')selectedDateLabel=selectedDate.toLocaleDateString([],{month:'long',year:'numeric'});
    else if(calendarView==='week'){const start=new Date(selectedDate);start.setDate(start.getDate()-((start.getDay()+6)%7));const end=new Date(start);end.setDate(end.getDate()+6);selectedDateLabel=start.toLocaleDateString([],{month:'short',day:'numeric'})+' – '+end.toLocaleDateString([],{month:'short',day:'numeric'});}
    else selectedDateLabel=+selectedDate===+today?'Today':selectedDate.toLocaleDateString([],{month:'short',day:'numeric'});
    find('[data-sj-calendar-today]').textContent=selectedDateLabel;
    find('[data-sj-calendar-today]').title=+selectedDate===+today?'Today':'Return to today';
    const inCalendar=job=>{if(!calendarEnabled)return true;const event=date(job.eventDate);if(!event)return false;const start=new Date(calendarDate.getFullYear(),calendarDate.getMonth(),calendarDate.getDate());const end=new Date(start);if(calendarView==='month'){start.setDate(1);end.setMonth(end.getMonth()+1,1);}else if(calendarView==='week'){start.setDate(start.getDate()-((start.getDay()+6)%7));end.setTime(start.getTime());end.setDate(end.getDate()+7);}else end.setDate(end.getDate()+1);return event>=start&&event<end;};
    const filtered=base.filter(j=>inCalendar(j)&&(!stage||status(j)===stage)&&(!attention||need(j,attention))&&[j.title,j.clientName,j.reference,j.jobNumber,workspacesById.get(String(j.workspaceId))?.name,ownerLabel(j)].join(' ').toLowerCase().includes(term.toLowerCase().trim()));
    filtered.sort((a,b)=>{const ad=date(a.eventDate),bd=date(b.eventDate);return !ad&&!bd?String(a.id).localeCompare(String(b.id)):!ad?1:!bd?-1:(+ad-+bd)*(sort==='date-asc'?1:-1)||String(a.id).localeCompare(String(b.id));});
    const pages=Math.max(1,Math.ceil(filtered.length/13));page=Math.min(page,pages);
    const visible=filtered.slice((page-1)*13,page*13);
    find('tbody').innerHTML=visible.map(j=>{const name=ownerLabel(j),initials=owner(j)?name.split(/[\s.]+/).filter(Boolean).slice(0,2).map(x=>x[0]).join('').toUpperCase():'–';return `<tr tabindex="0" data-sj-job="${esc(j.id)}" aria-label="Open ${esc(j.title||j.reference||'job')}"><td><strong>${esc(j.title||j.reference||'Untitled job')}</strong><small>${esc([j.clientName,j.reference||j.jobNumber].filter(Boolean).join(' · ')||'Client not linked')}</small></td><td>${esc(dateLabel(j.eventDate))}</td><td><span class="sj-owner"><span class="sj-initials" aria-hidden="true">${esc(initials)}</span>${esc(name)}</span></td><td><span class="sj-pill sj-${tone(status(j))}">${esc(status(j))}</span></td><td>${esc(flows.get(String(j.id))?.nextAction||'Workflow unavailable')}</td></tr>`;}).join('')||'<tr><td colspan="5" class="sj-empty">No jobs match these filters.</td></tr>';

    find('[data-sj-range]').textContent=filtered.length?`Showing ${(page-1)*13+1}–${Math.min(page*13,filtered.length)} of ${filtered.length} jobs`:'0 jobs';
    find('[data-sj-page]').innerHTML=Array.from({length:Math.min(3,pages)},(_,i)=>Math.min(Math.max(1,page-1),Math.max(1,pages-2))+i).map(n=>`<button type="button" data-sj-page-number="${n}" aria-label="Page ${n}" ${n===page?'aria-current="page"':''}>${n}</button>`).join('');find('[data-sj-prev]').disabled=page===1;find('[data-sj-next]').disabled=page===pages;
    find('[data-sj-settings]').hidden=!workspace||!options.openWorkspaceSettings;
    const footer=document.getElementById('studioFooterRate');if(footer)footer.textContent=filtered.length+' jobs';
    const counts=StudioStatusOrder.sort(base.map(status)).map(s=>[s,base.filter(j=>status(j)===s).length]);
    const changes=notificationResult.value.filter(n=>/status|workflow/i.test(n.eventType||n.type||n.title||'')&&base.some(j=>String(j.id)===String(n.referenceId))).sort((a,b)=>+(date(b.createdAtUtc||b.createdAt)||0)-+(date(a.createdAtUtc||a.createdAt)||0)).slice(0,3);
    find('[data-sj-statuses]').innerHTML=(workflowResult.error?'<p>Workflow unavailable. Showing recorded job statuses.</p>':'')+counts.map(([s,n])=>`<button type="button" class="sj-status-count sj-${tone(s)}" data-sj-stage="${esc(s)}" aria-pressed="${stage===s}"><span>${esc(s)}</span><b>${n}</b><meter min="0" max="${Math.max(1,base.length)}" value="${n}" aria-label="${esc(s)} jobs">${n}</meter></button>`).join('')+'<h3>Recent Status Changes</h3>'+(changes.map(n=>`<button type="button" class="sj-note" data-sj-job="${esc(n.referenceId)}"><strong>${esc(n.title||n.subject||n.eventType)}</strong><small>${esc(n.body||'')} · ${esc(dateLabel(n.createdAtUtc||n.createdAt))}</small></button>`).join('')||'<p>No recent status changes.</p>');
    find('[data-sj-attention]').innerHTML=[['owner','Owner needed'],['changes','Changes requested'],['photographers','Photographers needed']].map(([key,label])=>`<button type="button" class="sj-attention" data-sj-need="${key}" aria-pressed="${attention===key}"><span>${label}</span><b>${base.filter(j=>need(j,key)).length}</b></button>`).join('');
    try{sessionStorage.setItem('studio-jobs-view',JSON.stringify({term,stage,sort,view,calendarView,calendarDate:isoDate(calendarDate),calendarEnabled}));}catch{}
   }
   StudioPage.queryAll(host,'[data-sj-calendar-view]').forEach(button=>button.onclick=()=>{calendarView=button.dataset.sjCalendarView;calendarEnabled=true;apply();});
   StudioPage.queryAll(host,'[data-sj-calendar-step]').forEach(button=>button.onclick=()=>{const n=Number(button.dataset.sjCalendarStep);const next=new Date(calendarDate);if(calendarView==='month'){const day=next.getDate();next.setDate(1);next.setMonth(next.getMonth()+n);next.setDate(Math.min(day,new Date(next.getFullYear(),next.getMonth()+1,0).getDate()));}else next.setDate(next.getDate()+n*(calendarView==='week'?7:1));calendarDate=next;calendarEnabled=true;apply();});
   find('[data-sj-calendar-today]').onclick=()=>{calendarDate=new Date();calendarEnabled=true;apply();};
   find('[data-sj-calendar-date]').onchange=e=>{const chosen=date(e.target.value);if(chosen){calendarDate=chosen;calendarEnabled=true;e.target.blur();apply();}};
   find('[data-sj-calendar-toggle]').onclick=()=>{const input=find('[data-sj-calendar-date]');if(typeof input.showPicker==='function')input.showPicker();else input.click();};
   find('[data-sj-search]').oninput=e=>{term=e.target.value;apply();};
   find('[data-sj-status]').onchange=e=>{stage=e.target.value;apply();};
   
   find('[data-sj-sort]').onchange=e=>{sort=e.target.value;apply();};
   StudioPage.queryAll(host,'[data-sj-view]').forEach(button=>button.onclick=()=>{view=button.dataset.sjView;attention='';apply();});
   find('[data-sj-prev]').onclick=()=>{page--;apply(false);find('.sj-table-scroll').scrollTop=0;};find('[data-sj-next]').onclick=()=>{page++;apply(false);find('.sj-table-scroll').scrollTop=0;};
   const handleJobClick=e=>{if(!current())return;const number=e.target.closest('[data-sj-page-number]');if(number){page=Number(number.dataset.sjPageNumber);apply(false);find('.sj-table-scroll').scrollTop=0;return;}const row=e.target.closest('[data-sj-job]');if(row){void open(row.dataset.sjJob);return;}const s=e.target.closest('[data-sj-stage]');if(s){stage=stage===s.dataset.sjStage?'':s.dataset.sjStage;find('[data-sj-status]').value=stage;apply();}const n=e.target.closest('[data-sj-need]');if(n){attention=attention===n.dataset.sjNeed?'':n.dataset.sjNeed;apply();}};
   host.onclick=handleJobClick;
   find('[data-sj-statuses]').onclick=handleJobClick;
   find('[data-sj-attention]').onclick=handleJobClick;
   host.onkeydown=e=>{const row=e.target.closest('tr[data-sj-job]');if(row&&(e.key==='Enter'||e.key===' ')){e.preventDefault();void open(row.dataset.sjJob);}};
   find('[data-sj-settings]').onclick=async()=>{try{await options.openWorkspaceSettings(workspace);}catch(error){if(current()){find('[data-sj-error]').textContent=error.message||String(error);find('[data-sj-error]').hidden=false;}}};
   find('[data-sj-message]').onclick=()=>options.sendMessage?options.sendMessage():options.navigate('messages');apply();
   const announcements=await Promise.allSettled(workspaces.map(async w=>array(await options.json('/api/workspaces/'+encodeURIComponent(w.id)+'/announcements')).map(a=>({...a,workspaceId:w.id,workspaceName:w.name}))));
   if(!current())return controller;
   const messages=notificationResult.value.filter(n=>/message|conversation|reply/i.test(n.eventType||n.title||n.type||'')).map(n=>({at:n.createdAtUtc||n.createdAt,item:n}));
   messages.push(...announcements.flatMap(r=>r.status==='fulfilled'?r.value.map(a=>({at:a.createdAtUtc||a.createdAt,item:a,announcement:true})):[]));messages.sort((a,b)=>+(date(b.at)||0)-+(date(a.at)||0));
   find('[data-sj-messages]').innerHTML=(notificationResult.error||announcements.some(r=>r.status==='rejected')?'<p>Some messages could not load. <button type="button" data-sj-retry>Retry</button></p>':'')+(messages.slice(0,8).map(({item:n,announcement})=>`<button type="button" class="smc-row" data-sj-communication="${esc(announcement?n.workspaceId:n.referenceType==='Job'?n.referenceId||'':'')}" data-sj-kind="${announcement?'workspace':'message'}"><span class="smc-copy"><strong>${esc(n.subject||n.title||'Message')}</strong><small>${esc(n.body||n.workspaceName||'Studio message')}</small></span></button>`).join('')||'<p>No messages or announcements.</p>');
   find('[data-sj-retry]')?.addEventListener('click',()=>options.retry());
   StudioPage.queryAll(host,'[data-sj-communication]').forEach(b=>b.onclick=()=>{if(b.dataset.sjKind==='workspace')options.openWorkspace(b.dataset.sjCommunication);else if(b.dataset.sjCommunication)void open(b.dataset.sjCommunication);else options.navigate('messages');});
  } catch(error){if(current()){host.innerHTML='<div class="sj-loading" role="alert">Unable to load jobs: '+esc(error.message)+' <button type="button">Retry</button></div>';StudioPage.query(host,'button').onclick=()=>options.retry();}}
  return controller;
 }
 window.StudioJobsPage={render,dispose(){active?.dispose();active=null;}};
})();


