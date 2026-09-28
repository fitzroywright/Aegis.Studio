// Job records are shared by all workspace views; no separate calendar task store.
async function renderWorkspaceAnnouncements(host, workspaceId, current) {
    const items = await json(`/api/workspaces/${workspaceId}/announcements`);
    if (!current()) return;
    host.innerHTML = `<div class="detail-header"><h1>Announcements</h1></div><div class="messages">${items.map(x => `<article class="message"><h2>${esc(x.title)}</h2><p style="white-space:pre-wrap">${esc(x.body)}</p><small>${esc(new Date(x.createdAtUtc).toLocaleString())}</small></article>`).join('') || '<p>No announcements yet.</p>'}</div>${currentWorkspacePermissions?.notifyMembers ? '<form class="composer announcement-form"><label>Title<input name="title" required maxlength="200"></label><label>Announcement<textarea name="body" required maxlength="10000"></textarea></label><button class="primary">Post announcement</button><p role="alert"></p></form>' : ''}`;
    const form = host.querySelector('form');
    if (form) form.onsubmit = async event => {
        event.preventDefault(); const button = form.querySelector('button'); button.disabled = true;
        try { await json(`/api/workspaces/${workspaceId}/announcements`, {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({title:form.elements.title.value,body:form.elements.body.value})}); if(current()) await renderWorkspaceAnnouncements(host,workspaceId,current); }
        catch(error) { form.querySelector('[role=alert]').textContent = error.message; }
        finally {button.disabled=false;}
    };
}

async function renderJobSubjects(job, panel, current, refresh) {
    const [subjects, permissions] = await Promise.all([json(`/api/jobs/${job.id}/subjects`),json(`/api/jobs/${job.id}/access`)]);
    if(!current()) return;
    const canEdit = (permissions.permissions || permissions).editJob;
    const editButton = subject => canEdit ? `<button class="action" data-edit-subject="${subject.id}">Edit</button>` : '';
    panel.innerHTML = `<h2>Subjects</h2>${subjects.length===1 ? `<article class="subject-card"><h3>${esc(subjects[0].name)}</h3><p>${esc(subjects[0].reference)}</p><p>${esc(subjects[0].notes)}</p>${editButton(subjects[0])}</article>` : subjects.length ? `<table class="subject-roster"><thead><tr><th>Name</th><th>Reference</th><th>Notes</th><th></th></tr></thead><tbody>${subjects.map(s=>`<tr><td>${esc(s.name)}</td><td>${esc(s.reference)}</td><td>${esc(s.notes)}</td><td>${editButton(s)}</td></tr>`).join('')}</tbody></table>` : '<p>No subjects added. Add the person being photographed; additional people form a roster.</p>'}${canEdit ? '<button class="action" id="addJobSubject">Add subject</button><form class="subject-form" hidden><input name="subjectId" type="hidden"><label>Name<input name="name" maxlength="200" required></label><label>Reference<input name="reference" maxlength="200"></label><label>Notes<textarea name="notes" maxlength="4000"></textarea></label><button class="primary">Save subject</button><button type="button" data-cancel>Cancel</button><p role="alert"></p></form>' : ''}`;
    if(!canEdit) return;
    const form=panel.querySelector('form');
    const edit=subject=>{form.hidden=false;form.elements.subjectId.value=subject?.id || ''; for(const key of ['name','reference','notes']) form.elements[key].value=subject?.[key] || '';form.elements.name.focus();};
    panel.querySelector('#addJobSubject').onclick=()=>edit(null);
    panel.querySelectorAll('[data-edit-subject]').forEach(b=>b.onclick=()=>edit(subjects.find(s=>s.id===b.dataset.editSubject)));
    form.querySelector('[data-cancel]').onclick=()=>form.hidden=true;
    form.onsubmit=async event=>{event.preventDefault();const id=form.elements.subjectId.value;const button=form.querySelector('button');button.disabled=true;try{await json(`/api/jobs/${job.id}/subjects${id?'/'+id:''}`,{method:id?'PUT':'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:form.elements.name.value,reference:form.elements.reference.value,notes:form.elements.notes.value})});if(current())await refresh();}catch(error){form.querySelector('[role=alert]').textContent=error.message;}finally{button.disabled=false;}};
}

function jobTaskDate(task) { return task.scheduledAtUtc || task.dueAtUtc; }
function localTaskDay(value) { const d=new Date(value);return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`; }
async function renderJobTasks(job,panel,current,refresh) {
    const [tasks,access]=await Promise.all([json(`/api/operations/activities?jobId=${job.id}`),json(`/api/jobs/${job.id}/access`)]);
    if(!current())return;
    panel.innerHTML='<div class="job-actions"><button class="action" data-task-view="list">Tasks</button><button class="action" data-task-view="calendar">Calendar</button></div><div class="task-view"></div>';
    const host=panel.querySelector('.task-view');
    const row=t=>`<article class="task-card"><strong>${esc(t.title)}</strong><small>${esc(t.status)} · ${esc(t.assignedTo || 'Unassigned')}${jobTaskDate(t)?' · '+esc(new Date(jobTaskDate(t)).toLocaleString()):' · Unscheduled'}</small></article>`;
    let month=new Date();month.setDate(1);
    const render=view=>{
        panel.querySelectorAll('[data-task-view]').forEach(b=>b.classList.toggle('active',b.dataset.taskView===view));
        if(view==='list'){host.innerHTML=tasks.map(row).join('') || '<p>No tasks yet.</p>';return;}
        const year=month.getFullYear(),m=month.getMonth(),days=new Date(year,m+1,0).getDate();
        host.innerHTML=`<div class="job-actions"><button class="action" data-month="-1" aria-label="Previous month">←</button><strong>${esc(month.toLocaleDateString(undefined,{month:'long',year:'numeric'}))}</strong><button class="action" data-month="1" aria-label="Next month">→</button></div><div class="job-calendar">${['Sun','Mon','Tue','Wed','Thu','Fri','Sat'].map(d=>`<b>${d}</b>`).join('')}${'<div></div>'.repeat(month.getDay())}${Array.from({length:days},(_,i)=>{const day=localTaskDay(new Date(year,m,i+1));return `<section><b>${i+1}</b>${tasks.filter(t=>jobTaskDate(t)&&localTaskDay(jobTaskDate(t))===day).map(row).join('')}</section>`;}).join('')}</div><h3>Unscheduled</h3>${tasks.filter(t=>!jobTaskDate(t)).map(row).join('')||'<p>None</p>'}`;
        host.querySelectorAll('[data-month]').forEach(b=>b.onclick=()=>{month.setMonth(month.getMonth()+Number(b.dataset.month));render('calendar');});
    };
    panel.querySelectorAll('[data-task-view]').forEach(b=>b.onclick=()=>render(b.dataset.taskView));render('list');
    if(!(access.permissions||access).manageProduction)return;
    const form=document.createElement('form');form.className='subject-form';form.innerHTML='<h3>Add task</h3><label>Title<input name="title" required maxlength="200"></label><label>Assigned to<input name="assignedTo"></label><label>Scheduled<input name="scheduled" type="datetime-local"></label><label>Due<input name="due" type="datetime-local"></label><button class="primary">Add task</button><p role="alert"></p>';panel.appendChild(form);
    form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('button');button.disabled=true;try{await json(`/api/jobs/${job.id}/operations/activities`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({title:form.elements.title.value,activityType:'Other',status:'NotStarted',priority:'Normal',assignedTo:form.elements.assignedTo.value||null,scheduledAtUtc:form.elements.scheduled.value?new Date(form.elements.scheduled.value).toISOString():null,dueAtUtc:form.elements.due.value?new Date(form.elements.due.value).toISOString():null})});if(current())await refresh();}catch(error){form.querySelector('[role=alert]').textContent=error.message;}finally{button.disabled=false;}};
}

async function renderJobActivity(job,panel,current) {
    const items=await json(`/api/jobs/${job.id}/operations/audit`);if(!current())return;
    panel.innerHTML=`<h2>Activity</h2>${items.map(x=>`<article class="task-card"><strong>${esc(x.action)}</strong><p>${esc(x.detail||'')}</p><small>${esc(new Date(x.createdAtUtc).toLocaleString())}</small></article>`).join('')||'<p>No operational activity recorded yet.</p>'}`;
}

async function renderVersionedGallery(job,host,current) {
    const [assets,access]=await Promise.all([json(`/api/jobs/${job.id}/gallery-assets`),json(`/api/jobs/${job.id}/access`)]);if(!current())return;
    const canEdit=(access.permissions||access).manageProduction;
    const collections=[...new Set(assets.map(a=>a.collection))];
    host.innerHTML=`<h2>Gallery</h2><label>Collection <select id="jobCollectionFilter"><option value="">All collections</option>${collections.map(c=>`<option>${esc(c)}</option>`).join('')}</select></label><div class="job-file-gallery"></div>${canEdit?'<form class="job-file-upload"><label>File<input name="file" type="file" required></label><label>Asset<input name="asset" maxlength="200" placeholder="New asset, or choose a version below"></label><label>Provenance note<input name="note" maxlength="1000" placeholder="Source / reason for this version"></label><button class="primary">Upload version</button><p role="alert"></p></form>':''}<div class="version-history"></div>`;
    const grid=host.querySelector('.job-file-gallery');
    const render=()=>{const chosen=host.querySelector('select').value;grid.innerHTML=assets.filter(a=>!chosen||a.collection===chosen).map(a=>`<article class="job-file-tile">${/\.(jpe?g|png|gif|webp|avif)$/i.test(a.fileName)?`<img loading="lazy" alt="${esc(a.fileName)}" src="/api/jobs/${job.id}/versions/by-id/${a.id}/content">`:'<span class="job-file-document">▤</span>'}<strong>${esc(a.fileName)}</strong><small>${esc(a.collection)} · v${a.versionNumber}</small><button class="action" data-history="${esc(a.assetKey)}">Versions / provenance</button>${canEdit?`<button class="action" data-collection="${esc(a.assetKey)}">Move to collection</button><button class="action" data-version="${esc(a.assetKey)}">New version</button>`:''}</article>`).join('')||'<p>No versioned assets in this collection.</p>';
        grid.querySelectorAll('[data-version]').forEach(b=>b.onclick=()=>{host.querySelector('form').elements.asset.value=b.dataset.version;host.querySelector('form').elements.file.click();});
        grid.querySelectorAll('[data-collection]').forEach(b=>b.onclick=async()=>{const name=prompt('Collection name');if(!name?.trim())return;try{await json(`/api/jobs/${job.id}/gallery-assets/${encodeURIComponent(b.dataset.collection)}/collection`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({name})});if(current())await renderVersionedGallery(job,host,current);}catch(e){alert(e.message);}});
        grid.querySelectorAll('[data-history]').forEach(b=>b.onclick=async()=>{const target=host.querySelector('.version-history');try{const history=await json(`/api/jobs/${job.id}/versions/${encodeURIComponent(b.dataset.history)}`);if(!current())return;target.innerHTML=`<h3>Versions and provenance</h3>${history.map(v=>`<article class="task-card"><a href="/api/jobs/${job.id}/versions/by-id/${v.id}/content" target="_blank" rel="noopener">${esc(v.fileName)} — v${v.versionNumber}</a><small>${esc(v.createdBy)} · ${esc(new Date(v.createdAtUtc).toLocaleString())}</small><p>${esc(v.note||'No source note recorded')}</p><small>SHA-256: ${esc(v.sha256)}${v.sourceVersionId?' · Restored from '+esc(v.sourceVersionId):''}</small></article>`).join('')}`;}catch(e){target.textContent=e.message;}});
    };host.querySelector('select').onchange=render;render();
    const form=host.querySelector('form');if(form)form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('button');button.disabled=true;try{const data=new FormData();data.append('file',form.elements.file.files[0]);data.append('note',form.elements.note.value);const key=form.elements.asset.value.trim()||crypto.randomUUID();const response=await fetch(`/api/jobs/${job.id}/versions/${encodeURIComponent(key)}`,{method:'POST',body:data});if(!response.ok)throw new Error(await response.text());if(current())await renderVersionedGallery(job,host,current);}catch(error){form.querySelector('[role=alert]').textContent=error.message;}finally{button.disabled=false;}};
}
