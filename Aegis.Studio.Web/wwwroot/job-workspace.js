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
    const [originalSubjects, permissions, media] = await Promise.all([
        json(`/api/jobs/${job.id}/subjects`),
        json(`/api/jobs/${job.id}/access`),
        json(`/api/jobs/${job.id}/media`).catch(()=>null)
    ]);
    const subjects=media?.subjects||originalSubjects;
    const assets=media?.assets||[];
    if(!current()) return;

    const canEdit=(permissions.permissions||permissions).editJob;
    const realSubjects=subjects.filter(subject=>!subject.isSystem);
    const representative=subject=>{
        const asset=assets.find(a=>(a.subjects||[]).includes(subject.id)&&String(a.contentType||'').startsWith('image/'));
        return asset?.versionId ? `/api/jobs/${job.id}/versions/by-id/${asset.versionId}/content` : null;
    };
    const role=subject=>subject.attributes?.role||subject.attributes?.type||subject.notes||'Person / group being photographed';
    const status=subject=>subject.status||'Not Photographed';

    panel.innerHTML = `
        <section class="people-workspace">
            <header class="people-heading">
                <div>
                    <h2>People</h2>
                    <p>People and groups being photographed in this job. Assign photos to them from Media.</p>
                </div>
                <div class="people-heading-actions">
                    <label class="people-search">
                        <span aria-hidden="true">⌕</span>
                        <input type="search" placeholder="Search people…" aria-label="Search people">
                    </label>
                    ${canEdit?'<button type="button" class="action" data-import-roster>Import Roster</button><button type="button" class="primary" id="addJobSubject">＋ Add Person</button>':''}
                </div>
            </header>

            <div class="people-explainer">
                <strong>People</strong> identify who is in the photographs. <strong>Collections</strong> identify where the photographs belong, such as Ceremony, Reception or Portraits.
            </div>

            <div class="people-grid">
                ${realSubjects.map(subject=>{
                    const image=representative(subject);
                    return `
                        <article class="person-card" data-search-person="${esc((subject.name+' '+(subject.reference||'')+' '+role(subject)).toLowerCase())}">
                            <div class="person-photo">
                                ${image?`<img src="${image}" alt="">`:`<span>${esc((subject.name||'?').slice(0,1))}</span>`}
                            </div>
                            <div class="person-copy">
                                <div class="person-title-line">
                                    <strong>${esc(subject.name)}</strong>
                                    <span class="person-status" data-status="${esc(status(subject))}">${esc(status(subject))}</span>
                                </div>
                                <span class="person-role">${esc(role(subject))}</span>
                                <div class="person-meta">
                                    ${subject.reference?`<span>Ref: ${esc(subject.reference)}</span>`:''}
                                    <span>${esc(subject.imageCount??0)} photo${Number(subject.imageCount??0)===1?'':'s'}</span>
                                    ${subject.scheduledAtUtc?`<span>${esc(new Date(subject.scheduledAtUtc).toLocaleString())}</span>`:''}
                                </div>
                            </div>
                            <div class="person-actions">
                                <button type="button" class="action" data-show-media="${esc(subject.id)}">View Photos</button>
                                ${canEdit?`<button type="button" class="action" data-edit-subject="${esc(subject.id)}">Edit</button>`:''}
                            </div>
                        </article>`;
                }).join('')||'<div class="people-empty"><strong>No people added yet.</strong><span>Add the people or groups being photographed, or import a roster.</span></div>'}
            </div>

            ${canEdit?'<form class="subject-form people-editor" hidden><input name="subjectId" type="hidden"><label>Name<input name="name" maxlength="200" required></label><label>Reference / ID<input name="reference" maxlength="200"></label><label>Notes / role<textarea name="notes" maxlength="4000" placeholder="Bride, Groom, Student, Bridal Party…"></textarea></label><div class="dialog-actions"><button class="primary">Save</button><button type="button" data-cancel>Cancel</button></div><p role="alert"></p></form>':''}
        </section>`;

    const search=panel.querySelector('.people-search input');
    if(search)search.oninput=()=>{
        const term=search.value.trim().toLowerCase();
        panel.querySelectorAll('[data-search-person]').forEach(card=>card.hidden=!!term&&!card.dataset.searchPerson.includes(term));
    };

    panel.querySelectorAll('[data-show-media]').forEach(button=>button.onclick=()=>{
        window.studioMediaFocus={jobId:job.id,subjectId:button.dataset.showMedia};
        const mediaTab=document.querySelector('[data-job-tab="gallery"]');
        mediaTab?.click();
    });

    if(!canEdit) return;

    const form=panel.querySelector('form');
    const edit=subject=>{
        form.hidden=false;
        form.elements.subjectId.value=subject?.id||'';
        for(const key of ['name','reference','notes']) form.elements[key].value=subject?.[key]||'';
        form.elements.name.focus();
    };

    panel.querySelector('#addJobSubject').onclick=()=>edit(null);
    panel.querySelectorAll('[data-edit-subject]').forEach(button=>button.onclick=()=>edit(realSubjects.find(s=>s.id===button.dataset.editSubject)));
    form.querySelector('[data-cancel]').onclick=()=>form.hidden=true;

    const rosterButton=panel.querySelector('[data-import-roster]');
    if(rosterButton)rosterButton.onclick=()=>{
        const mediaTab=document.querySelector('[data-job-tab="gallery"]');
        mediaTab?.click();
        setTimeout(()=>document.querySelector('[data-roster]')?.click(),0);
    };

    form.onsubmit=async event=>{
        event.preventDefault();
        const id=form.elements.subjectId.value;
        const button=form.querySelector('button');
        button.disabled=true;
        try{
            await json(`/api/jobs/${job.id}/subjects${id?'/'+id:''}`,{
                method:id?'PUT':'POST',
                headers:{'Content-Type':'application/json'},
                body:JSON.stringify({
                    name:form.elements.name.value,
                    reference:form.elements.reference.value,
                    notes:form.elements.notes.value
                })
            });
            if(current())await refresh();
        }catch(error){
            form.querySelector('[role=alert]').textContent=error.message;
        }finally{
            button.disabled=false;
        }
    };
}

function jobTaskDate(task) { return task.scheduledAtUtc || task.dueAtUtc; }
function localTaskDay(value) { const d=new Date(value);return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`; }
async function renderJobTasks(job,panel,current,refresh) {
    const [tasks,access,employees]=await Promise.all([json(`/api/operations/activities?jobId=${job.id}`),json(`/api/jobs/${job.id}/access`),json('/api/studio-employees?assignable=true')]);
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
    const form=document.createElement('form');form.className='subject-form';form.innerHTML='<h3>Add task</h3><label>Title<input name="title" required maxlength="200"></label><label>Assigned to<select name="assignedTo"><option value="">Unassigned</option>'+employees.map(e=>'<option value="'+esc(e.userName)+'">'+esc(e.displayName)+(e.roles?.length?' · '+esc(e.roles.join(', ')):'')+'</option>').join('')+'</select></label><label>Scheduled<input name="scheduled" type="datetime-local"></label><label>Due<input name="due" type="datetime-local"></label><button class="primary">Add task</button><p role="alert"></p>';panel.appendChild(form);
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
async function renderAnnouncementAdministration(){
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
}


