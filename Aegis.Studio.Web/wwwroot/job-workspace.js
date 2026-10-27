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
    const canUpload=(permissions.permissions||permissions).manageProduction;
    const realSubjects=subjects.filter(subject=>!subject.isSystem);
    const pendingSubjectBatches=new Map();
    const uploadToSubject=async(subjectId,files)=>{
        if(!files.length)return;
        const subject=realSubjects.find(item=>item.id===subjectId),collection=subject?.collection||'Unclassified';
        let batch=pendingSubjectBatches.get(subjectId);
        if(!batch){
            const view=await json(`/api/jobs/${job.id}/shoot-sessions`),sessions=view||[];
            if(sessions.length>1)throw Error('Use Media Upload to choose the session for these files.');
            batch={batchId:crypto.randomUUID(),sessionId:sessions[0]?.id||null,subjectId,collection,files:files.map(file=>({uploadId:crypto.randomUUID(),fileName:file.name,sizeBytes:file.size}))};
            pendingSubjectBatches.set(subjectId,batch);
        }
        if(batch.files.length!==files.length||batch.files.some((x,i)=>x.fileName!==files[i].name||x.sizeBytes!==files[i].size))throw Error('Reselect the same files to retry this subject batch.');
        await json(`/api/jobs/${job.id}/media/upload-batches`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(batch)});
        for(let i=0;i<files.length;i++){
            const form=new FormData();form.append('file',files[i]);form.append('uploadId',batch.files[i].uploadId);
            await json(`/api/jobs/${job.id}/media/uploads`,{method:'POST',body:form});
        }
        pendingSubjectBatches.delete(subjectId);await refresh();
    };

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
                    ${canEdit?'<button type="button" class="action" data-import-roster>Session Plan</button><button type="button" class="primary" id="addJobSubject">＋ Add Person</button>':''}
                </div>
            </header>

            <div class="people-explainer">
                <strong>People</strong> identify who is in the photographs. <strong>Collections</strong> identify where the photographs belong, such as Ceremony, Reception or Portraits.
            </div>

            <div class="people-grid">
                ${realSubjects.map(subject=>{
                    const image=representative(subject);
                    return `
                        <article class="person-card subject-drop-target" data-subject-drop="${esc(subject.id)}" data-search-person="${esc((subject.name+' '+(subject.reference||'')+' '+role(subject)).toLowerCase())}">
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

    if(canUpload)panel.querySelectorAll('[data-subject-drop]').forEach(card=>{card.ondragover=e=>{if(e.dataTransfer?.types?.includes('Files')){e.preventDefault();card.classList.add('drag-target');}};card.ondragleave=()=>card.classList.remove('drag-target');card.ondrop=async e=>{e.preventDefault();e.stopPropagation();card.classList.remove('drag-target');try{const files=typeof studioDroppedPhotos==='function'?await studioDroppedPhotos(e.dataTransfer):Array.from(e.dataTransfer.files||[]);await uploadToSubject(card.dataset.subjectDrop,files);}catch(error){alert(error.message||String(error));}};});

    const search=panel.querySelector('.people-search input');
    if(search)search.oninput=()=>{
        const term=search.value.trim().toLowerCase();
        panel.querySelectorAll('[data-search-person]').forEach(card=>card.hidden=!!term&&!card.dataset.searchPerson.includes(term));
    };

    panel.querySelectorAll('[data-show-media]').forEach(button=>button.onclick=()=>{
        window.studioMediaFocus={jobId:job.id,subjectId:button.dataset.showMedia};
        if(panel.studioOpenMedia)panel.studioOpenMedia();
        else document.querySelector('[data-job-tab="gallery"]')?.click();
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
    if(rosterButton)rosterButton.onclick=async()=>{
        if(panel.studioOpenMedia)await panel.studioOpenMedia();
        else document.querySelector('[data-job-tab="gallery"]')?.click();
        if(panel.studioImportRoster)await panel.studioImportRoster();else document.querySelector('[data-roster]')?.click();
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
async function renderJobTasks(job,panel,current) {
    if(!current())return;
    panel.innerHTML=`<section class="job-section job-workflow-entry">
        <h2>Workflow</h2>
        <p>Job tasks are managed in the configured workflow graph.</p>
        <button type="button" class="primary" data-open-job-workflow>Next Action</button>
    </section>`;
    panel.querySelector('[data-open-job-workflow]').onclick=()=>{
        if(typeof window.StudioJobGraph?.launchTask!=='function'){alert('The workflow graph is unavailable.');return;}
        window.StudioJobGraph.launchTask(job.id).catch(error=>alert(error.message||String(error)));
    };
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




