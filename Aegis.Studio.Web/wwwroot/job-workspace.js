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
    const [originalSubjects, permissions, media] = await Promise.all([json(`/api/jobs/${job.id}/subjects`),json(`/api/jobs/${job.id}/access`),json(`/api/jobs/${job.id}/media`).catch(()=>null)]);
    const subjects=media?.subjects||originalSubjects;
    if(!current()) return;
    const canEdit = (permissions.permissions || permissions).editJob;
    const editButton = subject => canEdit && !subject.isSystem ? `<button class="action" data-edit-subject="${subject.id}">Edit</button>` : '';
    panel.innerHTML = `${subjects.length===1 ? `<article class="subject-card"><h3>${esc(subjects[0].name)}</h3><p>${esc(subjects[0].reference)}</p><p>${esc(subjects[0].notes)}</p>${editButton(subjects[0])}</article>` : subjects.length ? `<table class="subject-roster"><thead><tr><th>Name</th><th>Reference</th><th>Images</th><th>Notes</th><th></th></tr></thead><tbody>${subjects.map(s=>`<tr><td>${esc(s.name)}</td><td>${esc(s.reference)}</td><td>${esc(s.imageCount??"—")}</td><td>${esc(s.notes)}</td><td>${editButton(s)}</td></tr>`).join('')}</tbody></table>` : '<p>No subjects added. Add the person being photographed; additional people form a roster.</p>'}${canEdit ? '<button class="action" id="addJobSubject">Add subject</button><form class="subject-form" hidden><input name="subjectId" type="hidden"><label>Name<input name="name" maxlength="200" required></label><label>Reference<input name="reference" maxlength="200"></label><label>Notes<textarea name="notes" maxlength="4000"></textarea></label><button class="primary">Save subject</button><button type="button" data-cancel>Cancel</button><p role="alert"></p></form>' : ''}`;
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
    const row=t=>`<article class="task-card"><strong>${esc(t.title)}</strong><small>${esc(t.status)} · ${esc(t.assignedTo ? studioFirstName(t.assignedTo) : 'Unassigned')}${jobTaskDate(t)?' · '+esc(new Date(jobTaskDate(t)).toLocaleString()):' · Unscheduled'}</small></article>`;
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
    panel.innerHTML=`${items.map(x=>`<article class="task-card"><strong>${esc(x.action)}</strong><p>${esc(x.detail||'')}</p><small>${esc(new Date(x.createdAtUtc).toLocaleString())}</small></article>`).join('')||'<p>No operational activity recorded yet.</p>'}`;
}

function galleryActionIcon(name) {
    const paths={history:'<path d="M3 11a9 9 0 1 1 2 7M3 4v7h7M12 7v5l3 2"/>',download:'<path d="M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5"/>',folder:'<path d="M3 7V4h7l2 3h9v13H3zM9 13h6m-3-3v6"/>',upload:'<path d="M12 16V4m-5 5 5-5 5 5M4 17v4h16v-4"/>'};
    return '<svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">'+paths[name]+'</svg>';
}
// Compatibility entry point; production media has one implementation.
async function renderVersionedGallery(job,host,current) { return renderProductionMedia(job,host,current); }

async function renderAnnouncementBanners(host,workspaceId,current) {
    const items=await json(`/api/workspaces/${workspaceId}/announcements`);
    if(!current())return;
    host.hidden=!items.length;
    host.innerHTML=items.map(x=>`<article class="workspace-announcement" role="status"><div><strong>${esc(x.title)}</strong><p>${esc(x.body)}</p><small class="dismiss-error" role="alert"></small></div><button class="action" data-dismiss="${esc(x.id)}" aria-label="Dismiss announcement">Dismiss</button></article>`).join('');
    host.querySelectorAll('[data-dismiss]').forEach(button=>button.onclick=async()=>{
        button.disabled=true;
        try {
            await json(`/api/workspaces/${workspaceId}/announcements/${encodeURIComponent(button.dataset.dismiss)}/dismiss`,{method:'POST'});
            if(current()){button.closest('article').remove();host.hidden=!host.querySelector('article');}
        }catch(error){button.closest('article').querySelector('.dismiss-error').textContent='Could not dismiss. Please try again.';button.disabled=false;}
    });
}

// Posting remains available in workspace administration, without a navigation tab.
(() => {
    const previous=showWorkspaces;
    showWorkspaces=async function(){
        await previous();
        const eligible=workspaceCache.filter(w=>w.permissions?.notifyMembers);
        if(!eligible.length)return;
        const section=document.createElement('section');section.className='job-section';
        section.innerHTML=`<h2>Post workspace announcement</h2><form class="announcement-form"><label>Workspace<select name="workspace">${eligible.map(w=>`<option value="${esc(w.id)}">${esc(w.name)}</option>`).join('')}</select></label><label>Title<input name="title" required maxlength="200"></label><label>Announcement<textarea name="body" required maxlength="10000"></textarea></label><button class="primary">Post announcement</button><p role="status"></p></form>`;
        genericView.appendChild(section);
        const form=section.querySelector('form');form.onsubmit=async e=>{
            e.preventDefault();const button=form.querySelector('button');button.disabled=true;
            try{await json(`/api/workspaces/${form.elements.workspace.value}/announcements`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({title:form.elements.title.value,body:form.elements.body.value})});form.elements.title.value='';form.elements.body.value='';form.querySelector('[role=status]').textContent='Announcement posted.';}
            catch(error){form.querySelector('[role=status]').textContent=error.message;}
            finally{button.disabled=false;}
        };
    };
})();

