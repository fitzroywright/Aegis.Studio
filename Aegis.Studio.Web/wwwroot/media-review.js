/* Canonical Media page: job Session Plan, library, destinations and upload receipts. */
(function(){
 'use strict';
 const esc=value=>String(value??'').replace(/[&<>"']/g,ch=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
 let revision=0;
 const transfer={batch:null,running:false,paused:false,notify:null};
 window.addEventListener('beforeunload',event=>{if(transfer.batch){event.preventDefault();event.returnValue='';}});
 const ids=a=>(a.subjects||[]).map(x=>String(typeof x==='object'?x.id:x));
 const raw=name=>/\.(3fr|arw|cr2|cr3|crw|dng|erf|fff|iiq|kdc|mos|mrw|nef|nrw|orf|pef|raf|raw|rw2|rwl|sr2|srf|srw|x3f)$/i.test(name||'');
 const version=a=>a.versionId||a.latestVersionId;
 const url=(job,a,size='thumbnail')=>version(a)?`/api/jobs/${encodeURIComponent(job)}/versions/by-id/${encodeURIComponent(version(a))}/preview?size=${size}`:'';
 const post=(method,body)=>({method,headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
 window.showMediaReview=async function(){
  const generation=++revision;
  conversationView.classList.add('hidden');genericView.classList.remove('hidden');
  genericView.innerHTML='<p role="status">Loading Media…</p>';
  let jobs,workspaces;
  try{[jobs,workspaces]=await Promise.all([json('/api/jobs'),json('/api/workspaces')]);jobs=Array.isArray(jobs)?jobs:jobs.jobs||[];workspaces=Array.isArray(workspaces)?workspaces:workspaces.workspaces||[];}
  catch(error){if(generation===revision)genericView.innerHTML=`<p role="alert">${esc(error.message)}</p>`;return;}
  if(generation!==revision)return;
  genericView.innerHTML=`<div class="studio-media-page">
   <header class="studio-media-heading"><h1>Media</h1><button type="button" class="action" data-plan disabled>Open session plan ↗</button></header>
   <section class="studio-media-panel studio-media-context" aria-label="Media context">
    <label>Workspace<select data-workspace aria-label="Workspace"><option value="">All workspaces</option>${workspaces.map(w=>`<option value="${esc(w.id)}">${esc(w.name||w.title)}</option>`).join('')}</select></label>
    <label>Job<select data-job data-job-picker aria-label="Job"><option value="">Choose a job…</option>${StudioSubjectPicker.jobOptions(jobs)}</select></label>
    <label>Upload session<select data-session aria-label="Upload session" disabled><option value="">Choose a job first</option></select></label>
    <button type="button" class="action" data-open-job disabled>View job details ↗</button>
   </section>
   <section class="studio-media-panel studio-media-coverage" data-coverage aria-live="polite">Choose a job to load its Session Plan and media.</section>
   <nav class="studio-media-collections" data-collections aria-label="Job collections"></nav>
   <div class="studio-media-workspace">
    <section class="studio-media-panel studio-media-library">
     <header class="studio-media-gallery-header"><h2 data-library-title>Job media</h2><span data-count></span><select data-sort aria-label="Sort media"><option value="newest">Newest first</option><option value="name">File name</option></select><button type="button" class="action" data-refresh>Refresh</button></header>
     <div class="studio-media-gallery-tools"><label>Uploaded by<select data-photographer aria-label="Uploaded by"><option value="">All uploaders</option></select></label><label>People / planned groups<select data-person aria-label="People or planned groups"><option value="">All people / groups</option></select></label><label>Search<input type="search" data-search placeholder="Files, people or reference" aria-label="Search media"></label><label>Status<select data-media-status aria-label="Media status"><option value="">All statuses</option><option>Needs Sorting</option><option>Needs Editing</option><option>Needs Review</option><option>Ready</option><option>Ready for Production</option><option>Completed</option><option>Blocked</option></select></label><button type="button" class="action" data-view aria-label="Toggle media list">List view</button></div>
     <div class="studio-media-status-tabs" role="group" aria-label="Media view"><button type="button" class="active" data-tab="photos" aria-pressed="true">Photos</button><button type="button" data-tab="plan" aria-pressed="false">Session plan</button><button type="button" data-tab="unclassified" aria-pressed="false">Unclassified</button></div>
     <div class="studio-media-plan-note" data-plan-note></div>
     <div class="studio-media-gallery" data-gallery><p>Choose a job to view media.</p></div>
     <footer class="studio-media-selection"><label><input type="checkbox" data-select-all aria-label="Select visible photos"> <span data-selected>0 selected</span></label><button type="button" class="action" data-assign disabled>Assign people / group</button><button type="button" class="action" data-move disabled>Move to collection</button><button type="button" class="action" data-clear>Clear</button></footer>
    </section>
    <div class="studio-media-tools">
     <section class="studio-media-panel studio-media-upload"><header><h2>Upload destination</h2><button type="button" class="action" data-change>Change destination</button></header><strong data-destination>Choose a job</strong><small data-destination-session></small><label>Collection<select data-collection aria-label="Upload collection"><option>Unclassified</option></select></label><label>Person / planned group<select data-subject aria-label="Upload person or group"><option value="">Assign later</option></select></label>
      <div class="studio-media-drop" data-drop tabindex="0" role="group" aria-label="Upload files or folders"><strong>Drop photos or folders here</strong><p>Includes subfolders · Upload first, assign later</p><button type="button" class="primary" data-choose>Choose files</button><button type="button" class="action" data-folder>Choose folder</button><input type="file" data-files multiple hidden><input type="file" data-folders webkitdirectory multiple hidden></div><button type="button" class="link-button" data-unclassified>Upload to Unclassified if unsure</button>
     </section>
     <section class="studio-media-panel studio-media-queue"><header><h2>Current upload</h2><button type="button" class="link-button" data-history>History</button></header><p role="status" aria-live="polite" data-progress>No upload running.</p><progress data-meter value="0" max="1" aria-label="Files uploaded"></progress><div class="studio-media-queue-files" data-queue></div><div class="studio-media-upload-actions"><button type="button" class="action" data-pause disabled>Pause</button><button type="button" class="primary" data-retry hidden>Retry failed</button><button type="button" class="action" data-cancel-upload hidden>Cancel remaining</button></div></section>
     <section class="studio-media-panel studio-media-handoff"><h2>Coverage to review</h2><div data-review>Choose a job to review its plan.</div><small>Receiving files does not confirm planned shots or complete a session.</small><button type="button" class="primary" data-handoff disabled>Review handoff</button></section>
    </div>
   </div><p class="studio-media-feedback" role="status" aria-live="polite" data-feedback></p>
  </div>`;
  const root=genericView.querySelector('.studio-media-page'),q=selector=>root.querySelector(selector);
  const alive=()=>generation===revision&&root.isConnected;
  const state={job:'',collection:'',subject:'',session:'',assets:[],subjects:[],sessions:[],collections:[],counts:{},batches:[],selected:new Set(),tab:'photos',list:false,request:0,canEdit:false,ready:false,get batch(){return transfer.batch;},set batch(value){transfer.batch=value;},get running(){return transfer.running;},set running(value){transfer.running=value;},get paused(){return transfer.paused;},set paused(value){transfer.paused=value;}};
  const feedback=error=>{if(alive())q('[data-feedback]').textContent=error.message||String(error);};
  const run=fn=>async()=>{try{await fn();}catch(error){feedback(error);}};
  const api=path=>'/api/jobs/'+encodeURIComponent(state.job)+path;
  const title=j=>[j.jobNumber||j.reference,j.title||j.name||'Job'].filter(Boolean).join(' · ');
  const options=(values,selected)=>values.map(([value,label])=>`<option value="${esc(value)}" ${String(value)===String(selected)?'selected':''}>${esc(label)}</option>`).join('');
  function remember(){window.studioMediaContext={jobId:state.job,sessionId:state.session,collection:state.collection,subjectId:state.subject};}
  function showDestination(){
   const person=state.subjects.find(s=>String(s.id)===state.subject),session=state.sessions.find(s=>String(s.id)===state.session);
   q('[data-destination]').textContent=state.job?(person?person.name:state.collection||'Unclassified'):'Choose a job';
   q('[data-destination-session]').textContent=[session?.title,state.job?'Job: '+title(jobs.find(j=>String(j.id)===state.job)||{}):''].filter(Boolean).join(' · ');
   q('[data-collection]').innerHTML=options(['Unclassified',...state.collections.filter(c=>c!=='Unclassified')].map(c=>[c,c]),state.collection||'Unclassified');
   q('[data-subject]').innerHTML='<option value="">Assign later</option>'+options(state.subjects.filter(s=>!state.collection||(s.collection||'Unclassified')===state.collection).map(s=>[s.id,[s.name,s.reference].filter(Boolean).join(' · ')]),state.subject);
   const locked=state.running||Boolean(state.batch);
   for(const control of root.querySelectorAll('[data-job],[data-workspace],[data-session],[data-collection],[data-subject]'))control.disabled=locked||!state.ready&&control.matches('[data-session],[data-collection],[data-subject]');
   for(const control of root.querySelectorAll('[data-choose],[data-folder]'))control.disabled=!state.ready||!state.canEdit||locked;
   q('[data-drop]').setAttribute('aria-disabled',String(!state.ready||!state.canEdit||locked));
   q('[data-plan]').disabled=!state.ready;q('[data-open-job]').disabled=!state.job;q('[data-handoff]').disabled=!state.ready||state.running||Boolean(state.batch);
   remember();
  }
  function planRows(){return state.subjects.filter(s=>!state.collection||(s.collection||'Unclassified')===state.collection);}
  function renderCoverage(){
   const all=state.subjects,received=all.filter(s=>Number(s.imageCount)>0).length,absent=all.filter(s=>/absent/i.test(s.status||'')).length,missing=all.filter(s=>!Number(s.imageCount)&&!/absent/i.test(s.status||'')).length;
   q('[data-coverage]').innerHTML=`<strong>Job Session Plan</strong><span>${all.length} planned people / groups</span><span class="studio-media-good">${received} with media</span><span class="studio-media-warning">${missing} missing media</span><span>${absent} absent</span><small>Coverage across all job sessions</small>`;
   const names=['',...state.collections,'Unclassified'].filter((x,i,a)=>a.indexOf(x)===i);
   q('[data-collections]').innerHTML=names.map(name=>`<button type="button" class="studio-media-collection ${state.collection===name?'active':''}" data-collection-name="${esc(name)}" aria-pressed="${state.collection===name}"><strong>${esc(name||'All media')}</strong><span>${name?Number(state.counts[name]||0):Number(state.total||0)}</span></button>`).join('');
   q('[data-collections]').querySelectorAll('button').forEach(b=>b.onclick=()=>{if(state.batch){feedback('Finish or retry the current batch before changing destination.');return;}state.collection=b.dataset.collectionName;state.subject='';state.selected.clear();q('[data-person]').value='';state.tab='photos';render();});
   const rows=planRows(),withMedia=rows.filter(s=>Number(s.imageCount)>0).length;
   q('[data-plan-note]').textContent=rows.length?`Planned people / groups: ${withMedia} of ${rows.length} have media. Review photographs before confirming coverage.`:'No people or groups planned here. Organise them in the job’s Session Plan.';
   const outstanding=all.filter(s=>!Number(s.imageCount)||/retake|reshoot/i.test(s.status||''));
   q('[data-review]').innerHTML=outstanding.length?outstanding.slice(0,3).map(s=>`<button type="button" class="studio-media-review-row" data-review-person="${esc(s.id)}"><span>${esc(s.name)}</span><small>${esc(/absent|retake|reshoot/i.test(s.status||'')?s.status:'Missing media')}</small></button>`).join('')+ (outstanding.length>3?`<small>+${outstanding.length-3} more in Session Plan</small>`:''):'No missing media found in the job plan. Review quality and planned shots before handoff.';
   q('[data-review]').querySelectorAll('button').forEach(b=>b.onclick=()=>choosePerson(b.dataset.reviewPerson));
  }
  function choosePerson(id){
   if(state.batch){feedback('Finish or retry the current batch before changing destination.');return;}
   const person=state.subjects.find(s=>String(s.id)===id);if(!person)return;
   state.subject=id;state.collection=person.collection||'Unclassified';q('[data-person]').value=id;state.tab='photos';state.selected.clear();render();
  }
  function filteredAssets(){
   const term=q('[data-search]').value.trim().toLowerCase(),person=q('[data-person]').value,by=q('[data-photographer]').value;
   const filtered=state.assets.filter(a=>(!state.collection||(a.collection||'Unclassified')===state.collection)&&(!person||ids(a).includes(person))&&(!by||a.uploadedBy===by)&&(!q('[data-media-status]').value||a.status===q('[data-media-status]').value)&&(state.tab!=='unclassified'||!a.collection||a.collection==='Unclassified')&&(!term||[a.fileName,a.assetKey,...state.subjects.filter(s=>ids(a).includes(String(s.id))).flatMap(s=>[s.name,s.reference])].join(' ').toLowerCase().includes(term)));
   return filtered.sort(q('[data-sort]').value==='name'?(a,b)=>String(a.fileName||a.assetKey).localeCompare(String(b.fileName||b.assetKey)):(a,b)=>String(b.uploadedAtUtc||'').localeCompare(String(a.uploadedAtUtc||'')));
  }
  function renderSelection(){
   q('[data-selected]').textContent=state.selected.size+' selected';q('[data-assign]').disabled=q('[data-move]').disabled=!state.canEdit||!state.selected.size;
   const shown=filteredAssets();q('[data-select-all]').disabled=state.tab==='plan';q('[data-select-all]').checked=shown.length>0&&shown.every(a=>state.selected.has(a.id));
   q('[data-select-all]').indeterminate=shown.some(a=>state.selected.has(a.id))&&!q('[data-select-all]').checked;
  }
  function render(){
   if(!alive())return;
   showDestination();renderCoverage();
   const shown=filteredAssets(),host=q('[data-gallery]');
   q('[data-library-title]').textContent=state.collection||'Job media';
   q('[data-count]').textContent=shown.length+' photos · all sessions';
   root.querySelectorAll('[data-tab]').forEach(b=>{b.classList.toggle('active',b.dataset.tab===state.tab);b.setAttribute('aria-pressed',String(b.dataset.tab===state.tab));});
   host.classList.toggle('studio-media-list',state.list);host.classList.toggle('studio-media-plan-table',state.tab==='plan');
   if(state.tab==='plan'){
    const term=q('[data-search]').value.toLowerCase();
    host.innerHTML=`<table><thead><tr><th>Person / planned group</th><th>Collection</th><th>Photos</th><th>Field outcome</th><th></th></tr></thead><tbody>${planRows().filter(s=>[s.name,s.reference].join(' ').toLowerCase().includes(term)).map(s=>`<tr><td><strong>${esc(s.name)}</strong><small>${esc(s.reference)}</small></td><td>${esc(s.collection)}</td><td class="${Number(s.imageCount)?'studio-media-good':'studio-media-warning'}">${Number(s.imageCount)||0}</td><td>${esc(s.status||'Not reported')}</td><td><button type="button" class="action" data-person-id="${esc(s.id)}">View / upload</button></td></tr>`).join('')||'<tr><td colspan="5">No planned people or groups match.</td></tr>'}</tbody></table>`;
    host.querySelectorAll('[data-person-id]').forEach(b=>b.onclick=()=>choosePerson(b.dataset.personId));
   }else{
    host.innerHTML=shown.map(a=>`<article class="studio-media-asset ${state.selected.has(a.id)?'selected':''}" data-asset="${esc(a.id)}"><label class="studio-media-check"><input type="checkbox" data-select="${esc(a.id)}" ${state.selected.has(a.id)?'checked':''} aria-label="Select ${esc(a.fileName||a.assetKey)}"></label><button type="button" class="studio-media-preview" data-detail="${esc(a.id)}" aria-label="Details for ${esc(a.fileName||a.assetKey)}">${version(a)&&(String(a.contentType||'').startsWith('image/')||raw(a.fileName))?`<img loading="lazy" data-preview="${esc(version(a))}" src="${esc(url(state.job,a))}" alt="${esc(a.fileName||a.assetKey)}">`:'<span>Preview unavailable</span>'}</button><strong>${esc(a.fileName||a.assetKey)} ${raw(a.fileName)?'<small class="studio-media-raw">RAW</small>':''}</strong><small>${esc(state.subjects.filter(s=>ids(a).includes(String(s.id))).map(s=>s.name).join(', ')||'Unassigned')}</small><small>${esc(a.uploadedBy||'')} · ${esc(a.status||'Needs Sorting')}</small></article>`).join('')||'<p class="studio-media-empty">No photos match this location or filter. Drop photos into the selected destination, or upload to Unclassified.</p>';
    host.querySelectorAll('img').forEach(img=>img.onerror=()=>{img.onerror=null;img.src=api('/versions/by-id/'+encodeURIComponent(img.dataset.preview)+'/preview/fallback');});
    host.querySelectorAll('[data-select]').forEach(input=>input.onchange=()=>{input.checked?state.selected.add(input.dataset.select):state.selected.delete(input.dataset.select);input.closest('article').classList.toggle('selected',input.checked);renderSelection();});
    host.querySelectorAll('[data-detail]').forEach(b=>b.onclick=run(()=>showDetail(state.assets.find(a=>a.id===b.dataset.detail))));
   }
   renderSelection();
  }
  async function loadJob(id,keep=false){
   if(state.batch&&(!keep||id!==state.batch.jobId)){feedback('Finish or retry the current upload before changing jobs.');q('[data-job]').value=state.job;return;}
   const request=++state.request;
   state.job=id;state.ready=false;state.canEdit=false;state.assets=[];state.subjects=[];state.sessions=[];state.collections=[];state.counts={};state.selected.clear();q('[data-feedback]').textContent='';
   for(const control of root.querySelectorAll('[data-plan],[data-handoff],[data-open-job]'))control.disabled=true;
   if(!keep){state.collection='';state.subject='';state.session='';state.tab='photos';q('[data-person]').value='';q('[data-photographer]').value='';}
   q('[data-gallery]').innerHTML='<p role="status">Loading job media and Session Plan…</p>';showDestination();
   if(!id){state.assets=[];state.subjects=[];state.sessions=[];state.collections=[];state.total=0;state.counts={};render();return;}
   try{
    const [first,subjects,sessions,access,batches]=await Promise.all([json(api('/media?limit=200')),json(api('/session-plan')),json(api('/shoot-sessions')),json(api('/access')),json(api('/media/upload-batches'))]);
    if(!alive()||request!==state.request)return;
    const assets=[...(first.assets||[])];let page=first;
    // Read every page: missing-media and person filters must never use only the first 100 files.
    while(page.hasMore){const after=page.assets?.at(-1)?.id;if(!after)throw Error('Media pagination did not return a cursor.');page=await json(api('/media?limit=200&after='+encodeURIComponent(after)));if(!alive()||request!==state.request)return;if(!page.assets?.length)throw Error('Media pagination returned an empty page. Refresh and retry.');assets.push(...page.assets);}
    state.assets=assets;state.subjects=subjects;state.sessions=sessions;state.batches=batches;if(state.batch){const receipt=batches.find(b=>b.batchId===state.batch.declaration.batchId);if(receipt){for(const item of state.batch.files){if(receipt.files?.some(f=>f.uploadId===item.uploadId&&f.state==='Stored'))item.state='Stored';}if(state.batch.files.every(f=>f.state==='Stored')){state.batch=null;state.paused=false;q('[data-progress]').textContent='All declared files received.';q('[data-cancel-upload]').hidden=true;q('[data-retry]').hidden=true;}}}state.total=first.totalAssets??assets.length;
    state.counts=Object.fromEntries([...new Set(assets.map(a=>a.collection||'Unclassified'))].map(name=>[name,assets.filter(a=>(a.collection||'Unclassified')===name).length]));
    const status=q('[data-media-status]').value;q('[data-media-status]').innerHTML='<option value="">All statuses</option>'+options([...new Set(assets.map(a=>a.status).filter(Boolean))].map(v=>[v,v]),status);
    state.collections=[...new Set([...subjects.map(s=>s.collection),...(first.collections||[]).map(c=>typeof c==='string'?c:c.name),...assets.map(a=>a.collection)].filter(c=>c&&c!=='Unclassified'))];
    state.canEdit=Boolean((access.permissions||access).manageProduction);state.ready=true;
    if(state.collection&&!state.collections.includes(state.collection)&&state.collection!=='Unclassified')state.collection='';
    if(state.subject&&!subjects.some(s=>String(s.id)===state.subject))state.subject='';
    if(!sessions.some(s=>String(s.id)===state.session))state.session=sessions.length===1?String(sessions[0].id):'';
    q('[data-session]').innerHTML=(sessions.length?'<option value="">Choose session before uploading</option>':'<option value="">No photography sessions planned</option>')+options(sessions.map(s=>[s.id,s.title||'Photography session']),state.session);q('[data-session]').value=state.session;
    q('[data-person]').innerHTML='<option value="">All people / groups</option>'+options(subjects.map(s=>[s.id,[s.name,s.reference].filter(Boolean).join(' · ')]),state.subject);
    const uploader=q('[data-photographer]').value;q('[data-photographer]').innerHTML='<option value="">All uploaders</option>'+options([...new Set(assets.map(a=>a.uploadedBy).filter(Boolean))].map(v=>[v,v]),uploader);
    render();
   }catch(error){if(alive()&&request===state.request){state.ready=false;q('[data-gallery]').innerHTML='<p role="alert">Unable to load this job. Use Refresh to retry.</p>';q('[data-coverage]').textContent='Session Plan coverage unavailable.';q('[data-collections]').replaceChildren();q('[data-review]').textContent='Coverage unavailable until this job reloads.';showDestination();feedback(error);}}
  }
  function openPlan(){if(state.job)return studioNavigate('session-plan',{jobId:state.job});}
  q('[data-plan]').onclick=run(openPlan);q('[data-handoff]').onclick=run(()=>state.job&&StudioWorkflow.open(state.job));
  q('[data-open-job]').onclick=run(()=>state.job&&openWorkspaceJob(state.job));
  q('[data-job]').onchange=run(()=>loadJob(q('[data-job]').value));
  q('[data-workspace]').onchange=run(async()=>{const ws=q('[data-workspace]').value;const available=jobs.filter(j=>!ws||String(j.workspaceId)===ws);q('[data-job]').innerHTML='<option value="">Choose a job…</option>'+StudioSubjectPicker.jobOptions(available);StudioSubjectPicker.enhance(q('[data-job]')).sync();await loadJob('');});
  q('[data-session]').onchange=()=>{state.session=q('[data-session]').value;showDestination();};
  q('[data-refresh]').onclick=run(()=>loadJob(state.job,true));
  q('[data-collection]').onchange=()=>{state.collection=q('[data-collection]').value;state.subject='';state.selected.clear();q('[data-person]').value='';render();};
  q('[data-subject]').onchange=()=>{state.subject=q('[data-subject]').value;q('[data-person]').value=state.subject;render();};
  q('[data-person]').onchange=()=>{if(!state.batch){state.subject=q('[data-person]').value;const person=state.subjects.find(s=>String(s.id)===state.subject);if(person)state.collection=person.collection||'Unclassified';}render();};
  q('[data-sort]').onchange=render;q('[data-photographer]').onchange=render;q('[data-media-status]').onchange=render;q('[data-search]').oninput=render;
  q('[data-view]').onclick=()=>{state.list=!state.list;q('[data-view]').textContent=state.list?'Grid view':'List view';render();};
  root.querySelectorAll('[data-tab]').forEach(b=>b.onclick=()=>{state.tab=b.dataset.tab;render();});
  q('[data-select-all]').onchange=e=>{for(const a of filteredAssets())e.target.checked?state.selected.add(a.id):state.selected.delete(a.id);render();};
  q('[data-clear]').onclick=()=>{state.selected.clear();render();};
  q('[data-change]').onclick=()=>q('[data-collection]').focus();
  q('[data-unclassified]').onclick=()=>{if(state.batch){feedback('Finish or retry the current batch before changing destination.');return;}state.collection='Unclassified';state.subject='';state.selected.clear();q('[data-person]').value='';render();};
  async function editSelected(move){
   if(!state.canEdit||!state.selected.size)return;
   const jobId=state.job,selected=[...state.selected];
   const dialog=studioDialog(move?'Move to collection':'Assign people / planned groups',`<form class="studio-media-edit"><label>${move?'Collection':'People / groups'}<select data-target ${move?'':'multiple size="8"'} required>${move?options(['Unclassified',...state.collections].map(v=>[v,v]),state.collection):options(state.subjects.map(s=>[s.id,[s.collection,s.name,s.reference].filter(Boolean).join(' · ')]),state.subject)}</select></label><small>${move?'Person associations are retained.':'Replaces the selected photos’ person/group assignments. Multiple people may share one photograph.'}</small><p role="alert"></p><button type="submit" class="primary">Save ${selected.length} photos</button></form>`);
   const form=dialog.querySelector('form');form.onsubmit=async e=>{e.preventDefault();const button=form.querySelector('button');button.disabled=true;try{const values=Array.from(form.querySelector('select').selectedOptions,x=>x.value);for(const id of selected){const a=state.assets.find(a=>a.id===id);await json('/api/jobs/'+encodeURIComponent(jobId)+'/media/assets/'+encodeURIComponent(id),post('PUT',{status:a.status||'Needs Sorting',collection:move?values[0]:a.collection||'Unclassified',subjects:move?ids(a):values,assignedTo:a.assignedTo||null,captureAtUtc:a.captureAtUtc||null}));}dialog.close();await loadJob(jobId,true);}catch(error){form.querySelector('[role=alert]').textContent='Some updates may have saved. '+error.message;}finally{button.disabled=false;}};
  }
  q('[data-assign]').onclick=run(()=>editSelected(false));q('[data-move]').onclick=run(()=>editSelected(true));
  async function showDetail(a){
   const jobId=state.job,base='/api/jobs/'+encodeURIComponent(jobId),v=version(a);
   const dialog=studioDialog(a.fileName||a.assetKey,`<div class="studio-media-edit">${v?`<img class="studio-media-detail-preview" src="${esc(url(jobId,a,'preview'))}" alt="${esc(a.fileName)}"><a href="${base}/versions/by-id/${encodeURIComponent(v)}/content" target="_blank" rel="noopener">Open original ↗</a>`:''}<p>${esc(a.uploadedBy||'')} · ${esc(a.status)} · ${esc(a.collection||'Unclassified')}</p><button type="button" class="action" data-tags ${state.canEdit?'':'disabled'}>Edit tags</button><button type="button" class="action" data-delete ${state.canEdit?'':'disabled'}>Delete photo</button><p role="alert"></p></div>`);
   const img=dialog.querySelector('img');if(img)img.onerror=()=>{img.onerror=null;img.src=base+'/versions/by-id/'+encodeURIComponent(v)+'/preview/fallback';};
   dialog.querySelector('[data-tags]').onclick=()=>{dialog.close();state.selected=new Set([a.id]);render();void editSelected(false);};
   dialog.querySelector('[data-delete]').onclick=async()=>{if(!confirm('Remove this photograph from Studio Media? It can be restored by an authorized user.'))return;try{await json(base+'/media/assets/'+encodeURIComponent(a.id),{method:'DELETE'});dialog.close();await loadJob(jobId,true);}catch(error){dialog.querySelector('[role=alert]').textContent=error.message;}};
  }
  q('[data-history]').onclick=run(async()=>{if(!state.job)return;const batches=await json(api('/media/upload-batches'));if(!alive())return;studioDialog('Upload history',`<div class="studio-media-history">${batches.map(b=>`<article><strong>${esc(b.createdBy)}</strong><small>${esc(new Date(b.createdAtUtc).toLocaleString())} · ${esc(state.sessions.find(s=>String(s.id)===String(b.sessionId))?.title||'No session')}</small><p>${(b.files||[]).filter(f=>f.state==='Stored').length} stored / ${(b.files||[]).length} declared ${b.cancelledAtUtc?'· Cancelled':''}</p>${(b.files||[]).filter(f=>!['Stored','Cancelled'].includes(f.state)).map(f=>`<small>${esc(f.fileName)} · ${esc(f.state)}</small>`).join('')}</article>`).join('')||'<p>No declared uploads yet.</p>'}<small>Most recent 100 batches. Pending files from earlier visits must be reselected by the original uploader.</small></div>`);});
  q('[data-choose]').onclick=()=>q('[data-files]').click();q('[data-folder]').onclick=()=>q('[data-folders]').click();
  async function acceptFiles(incoming){
   if(!state.ready||!state.canEdit||state.running||state.batch){feedback('Choose an editable job and finish the current batch before adding files.');return;}
   if(state.sessions.length&&!state.session){feedback('Choose the upload session before selecting files.');return;}
   const files=Array.from(incoming||[]);if(!files.length)return;
   if(files.some(f=>f.size<1||f.size>100000000)){feedback('Each file must contain between 1 byte and 100 MB.');return;}
   if(files.length>5000){feedback('Choose at most 5000 files per batch.');return;}
   // Folder names are never used to guess identities. The visible destination is authoritative.
   state.batch={jobId:state.job,files:files.map(file=>({file,uploadId:crypto.randomUUID(),state:'Queued'})),declaration:{batchId:crypto.randomUUID(),sessionId:state.session||null,collection:state.collection||'Unclassified',subjectId:state.subject||null,returned:false,note:'Media · '+(state.session||'Job upload')}};
   state.batch.declaration.files=state.batch.files.map(x=>({uploadId:x.uploadId,fileName:x.file.name,sizeBytes:x.file.size}));
   state.paused=false;await upload();
  }
  q('[data-files]').onchange=()=>void acceptFiles(q('[data-files]').files);q('[data-folders]').onchange=()=>void acceptFiles(q('[data-folders]').files);
  async function readEntry(entry){
   if(entry.isFile)return [await new Promise((resolve,reject)=>entry.file(resolve,reject))];
   if(!entry.isDirectory)return [];
   const reader=entry.createReader(),files=[];let entries;
   do{entries=await new Promise((resolve,reject)=>reader.readEntries(resolve,reject));for(const child of entries)files.push(...await readEntry(child));}while(entries.length);
   return files;
  }
  const drop=q('[data-drop]');
  drop.ondragover=e=>{e.preventDefault();drop.classList.add('drag-over');};drop.ondragleave=()=>drop.classList.remove('drag-over');
  drop.ondrop=async e=>{e.preventDefault();drop.classList.remove('drag-over');try{const entries=Array.from(e.dataTransfer?.items||[]).map(i=>i.webkitGetAsEntry?.()).filter(Boolean);const files=entries.length?(await Promise.all(entries.map(readEntry))).flat():Array.from(e.dataTransfer?.files||[]);await acceptFiles(files);}catch(error){feedback(error);}};
  function renderQueue(batch){
   if(!alive()||!batch)return;
   const done=batch.files.filter(f=>f.state==='Stored').length,failed=batch.files.filter(f=>f.state==='Failed').length;
   q('[data-progress]').textContent=`${done} of ${batch.files.length} uploaded · ${failed} failed${state.paused?' · Paused':''}`;
   q('[data-meter]').max=batch.files.length;q('[data-meter]').value=done;
   q('[data-queue]').innerHTML=batch.files.filter(f=>f.state!=='Stored').slice(0,8).map(f=>`<div><strong>${esc(f.file.name)}</strong><small>${esc(f.state)}${f.error?' · '+esc(f.error):''}</small></div>`).join('');
   q('[data-pause]').disabled=!state.running&&!state.paused;q('[data-pause]').textContent=state.paused?'Resume':'Pause';
   q('[data-retry]').hidden=state.running||!batch.files.some(f=>['Failed','Unmatched'].includes(f.state));
   q('[data-cancel-upload]').hidden=state.running;
   q('[data-retry]').textContent=batch.files.some(f=>f.state==='Unmatched')?'Review unmatched files':'Retry failed';showDestination();
  }
  async function upload(){
   const batch=state.batch;if(!batch||state.running)return;
   state.running=true;showDestination();transfer.notify?.();
   try{
    await json('/api/jobs/'+encodeURIComponent(batch.jobId)+'/media/upload-batches',post('POST',batch.declaration));
    for(const item of batch.files){
     if(state.paused)break;if(item.state==='Stored'||item.state==='Unmatched')continue;
     item.state='Uploading';delete item.error;transfer.notify?.();
     try{const form=new FormData();form.append('file',item.file);form.append('uploadId',item.uploadId);const result=await json('/api/jobs/'+encodeURIComponent(batch.jobId)+'/media/uploads',{method:'POST',body:form});
      if(result.unmatched){item.state='Unmatched';item.error='Resolve this file from the job Media tab.';continue;}
      item.state='Stored';
     }catch(error){item.state='Failed';item.error=error.message;}
     transfer.notify?.();
    }
   }catch(error){feedback(error);for(const item of batch.files)if(item.state==='Queued'){item.state='Failed';item.error=error.message;}}
   finally{
    state.running=false;transfer.notify?.();
    if(batch.files.every(f=>f.state==='Stored')){state.batch=null;state.paused=false;q('[data-files]').value='';q('[data-folders]').value='';q('[data-pause]').disabled=true;q('[data-retry]').hidden=true;q('[data-cancel-upload]').hidden=true;showDestination();if(alive())await loadJob(batch.jobId,true);else transfer.notify?.(batch);}
   }
  }
  q('[data-pause]').onclick=()=>{if(state.running){state.paused=true;renderQueue(state.batch);}else if(state.paused){state.paused=false;void upload();}};
  q('[data-cancel-upload]').onclick=run(async()=>{const batch=state.batch;if(!batch||state.running)return;const reason=prompt('Explain why the remaining files should be cancelled. Already stored files are retained.');if(!reason?.trim())return;await json('/api/jobs/'+encodeURIComponent(batch.jobId)+'/media/upload-batches/'+encodeURIComponent(batch.declaration.batchId)+'/cancel',post('POST',{reason}));state.batch=null;state.paused=false;q('[data-queue]').replaceChildren();q('[data-progress]').textContent='Remaining files cancelled. Stored files retained.';q('[data-retry]').hidden=true;q('[data-cancel-upload]').hidden=true;q('[data-pause]').disabled=true;await loadJob(state.job,true);});
  q('[data-retry]').onclick=run(async()=>{if(state.batch?.files.some(f=>f.state==='Unmatched')){await openWorkspaceJob(state.job);return;}state.paused=false;await upload();});
  transfer.notify=completed=>{if(!alive())return;if(state.batch)renderQueue(state.batch);else if(completed){q('[data-progress]').textContent=completed.files.length+' of '+completed.files.length+' uploaded';q('[data-queue]').replaceChildren();q('[data-pause]').disabled=true;q('[data-retry]').hidden=true;q('[data-cancel-upload]').hidden=true;void loadJob(state.job,true);}};
  const initial=transfer.batch?{jobId:transfer.batch.jobId,sessionId:transfer.batch.declaration.sessionId,collection:transfer.batch.declaration.collection,subjectId:transfer.batch.declaration.subjectId}:window.studioMediaFocus||window.studioMediaContext||{};
  const initialJob=transfer.batch?.jobId||window.history.state?.studioJobId||initial.jobId;
  if(initialJob&&jobs.some(j=>String(j.id)===String(initialJob))){q('[data-job]').value=String(initialJob);const matching=String(initial.jobId)===String(initialJob);state.collection=matching?initial.collection||'':'';state.subject=matching?initial.subjectId||'':'';state.session=matching?initial.sessionId||'':'';await loadJob(String(initialJob),true);if(state.batch)renderQueue(state.batch);}else showDestination();
 };
})();
