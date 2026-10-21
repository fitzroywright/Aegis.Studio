/* Job Details components own their DOM and API requests. Production keeps its own gallery. */
(function(root){
 'use strict';
 const escape=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const put=(url,body)=>json(url,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
 // Job stages stay aligned with WorkflowRules; milestones read existing session facts.
 const phases=['Pending','Needs Assignment','Needs Photographer Assignment','Photographer Assigned','Awaiting Scheduling','Scheduled','Photography In Progress','Shoot Complete','Awaiting Upload','Awaiting Production Review','Active Production','Awaiting Approval','Ready for Delivery','Delivered','Completed','Changes Requested','Cancelled','Blocked','Reshoot Required'];
 const photographyStages=new Set(['Needs Photographer Assignment','Photographer Assigned','Awaiting Scheduling','Scheduled','Photography In Progress','Shoot Complete','Awaiting Upload']);
 function phaseIndex(data){return phases.indexOf(data.stage);}
 function workflow(host,data,job={},view={},activate=null,summaryHost=null){
  const next=root.StudioWorkflow?.nextAction({...view,data}),sessions=(data.sessions||[]).filter(s=>s.required!==false);
  const noShoot=/print|edit/i.test(job.jobType||job.type||'')&&!(data.sessions||[]).length;
  const crewAssigned=sessions.length>0&&sessions.every(s=>s.crew_finalized&&(s.crew||[]).length>0);
  const shootComplete=sessions.length>0&&sessions.every(s=>s.shoot?.checked_out_at_utc&&s.shoot.status!=='Cancelled');
  const reshoot=sessions.some(s=>s.shoot?.reshoot_required)||(data.outstandingRoster||[]).some(s=>s.status==='Needs Retake');
  const groups=[['Booking',['Pending']],['Assignment',['Needs Assignment','Needs Photographer Assignment','Photographer Assigned']],['Shoot',['Awaiting Scheduling','Scheduled','Photography In Progress','Shoot Complete']],['Upload',['Awaiting Upload','Awaiting Production Review']],['Production',['Active Production','Awaiting Approval']],['Delivery',['Ready for Delivery','Delivered','Completed']]];
  const main=groups.flatMap(([,labels])=>labels),history=data.history||[],passed=new Set();
  // Leaving a stage for cancellation, corrections or an earlier gate is not a successful pass.
  // Re-entering a stage replaces its earlier result until that stage passes again.
  history.forEach((entry,index)=>{
   const from=entry.new_stage,to=history[index+1]?.new_stage||data.stage;
   passed.delete(from);
   if(!entry.exited_at_utc)return;
   const forward=main.includes(from)&&main.includes(to)&&main.indexOf(to)>main.indexOf(from);
   const corrected=from==='Changes Requested'&&['Awaiting Production Review','Active Production'].includes(to);
   if(forward||corrected)passed.add(from);
  });
  // The current forward gate also proves earlier gates that the service may skip in history.
  // Exceptions retain the last normal gate without treating its failed exit as success.
  const lastNormal=history.filter(h=>main.includes(h.new_stage)).at(-1)?.new_stage||(data.stage==='Changes Requested'?(data.correctionKind==='Source'?'Awaiting Production Review':data.correctionKind==='Production'?'Awaiting Approval':null):null);
  const frontier=main.indexOf(main.includes(data.stage)?data.stage:lastNormal);
  main.forEach((label,index)=>{if(index<frontier)passed.add(label);else passed.delete(label);});
  function hostStageComplete(label){return label==='Photographer Assigned'?crewAssigned:label==='Shoot Complete'?shootComplete:label==='Completed'?data.stage==='Completed':passed.has(label);}
  // The expanded detail has one milestone line and exactly two exception lanes.
  // Keep all stage detail available through the existing workflow dialog.
  const groupState=groups.map(([title,steps])=>{
   const applicable=steps.filter(label=>!(noShoot&&photographyStages.has(label)));
   const done=applicable.length>0&&applicable.every(hostStageComplete);
   const active=steps.includes(data.stage);
   return {title,done,active,skipped:!applicable.length,stage:active?data.stage:steps.at(-1)};
  });
  // Only exceptional stages are nodes on branch lanes. Return targets are
  // annotations, not duplicate main-flow nodes; terminal states never rejoin.
  const branches=[
   {label:'Changes Requested',from:'Production',to:data.correctionKind==='Source'?'Upload':'Production',active:data.stage==='Changes Requested'},
   {label:'Reshoot Required',from:'Shoot',to:'Shoot',active:data.stage==='Reshoot Required'||reshoot}
  ];
  const terminal=['Cancelled','Blocked'].includes(data.stage)?data.stage:data.blockedReason?'Blocked':null;
  host.innerHTML=`<div class="jd-flow-compact" role="group" aria-label="Workflow milestones and rejoining exception routes">
   ${summaryHost?'':`<div class="jd-flow-mainline">${groupState.map((g,i)=>`<button type="button" class="jd-compact-stage ${g.done?'complete':g.active?'current':g.skipped?'skipped':''}" data-flow-stage="${escape(g.stage)}" title="${escape(g.title)}: ${g.done?'Complete':g.active?'Current':g.skipped?'Not required':'Pending'}"><span class="jd-compact-dot" aria-hidden="true">${g.done?'✓':i+1}</span><span>${escape(g.title)}</span></button>`).join('')}</div>`}
   ${branches.map(line=>`<div class="jd-flow-exception-lane ${line.active?'is-active':''}"><span class="jd-flow-lane-origin">${escape(line.from)}</span><span class="jd-flow-lane-rule" aria-hidden="true"></span><button type="button" class="jd-compact-exception ${line.active?'current':''}" data-flow-stage="${escape(line.label)}" title="View ${escape(line.label)} details">${escape(line.label)}</button><span class="jd-flow-lane-rule" aria-hidden="true"></span><span class="jd-flow-lane-return" title="Returns to main workflow">↗ ${escape(line.to)}</span></div>`).join('')}
   ${terminal?`<div class="jd-flow-terminal" role="status">⛔ ${escape(terminal)} · workflow stopped (does not rejoin)</div>`:''}
  </div>`;
  host.setAttribute('aria-label','Job workflow: '+data.stage+(data.blockedReason?' · Blocked: '+data.blockedReason:''));
  if(summaryHost){
   const activeStage=main.includes(data.stage)?data.stage:lastNormal;
   summaryHost.innerHTML=groups.map(([title,steps])=>{
    const applicable=steps.filter(label=>!(noShoot&&photographyStages.has(label)));
    const skipped=!applicable.length,complete=!skipped&&applicable.every(hostStageComplete);
    const current=steps.includes(activeStage),exception=current&&(!main.includes(data.stage)||!!data.blockedReason||title==='Shoot'&&reshoot);
    const state=skipped?'skipped':complete?'complete':exception?'current exception':current?'current':'pending';
    const description=skipped?'Not required':complete?'Successfully passed':exception?'Needs attention':current?'Current':'Not yet passed';
    return `<li class="jd-flow-phase ${state}" data-flow-phase="${title}" title="${title} · ${description}"><span class="jd-stage-light" aria-hidden="true">${skipped?'–':complete?'✓':exception?'!':''}</span><span>${title}</span><span class="sr-only">${description}</span></li>`;
   }).join('');
   summaryHost.setAttribute('aria-label','Main job flow: '+data.stage);
  }
  host.querySelectorAll('[data-flow-stage]').forEach(button=>{
   const label=button.dataset.flowStage,current=label===data.stage;
   const selector=current?next?.selector:label==='Blocked'&&data.blockedReason&&view.canManage?'[data-wf-block]':label==='Reshoot Required'&&reshoot&&view.canManage&&data.ownerAssignment?.status==='Accepted'&&['Scheduled','Photography In Progress','Awaiting Upload'].includes(data.stage)&&(data.outstandingRoster||[]).length?'[data-wf-remainder]':null;
   button.setAttribute('aria-label',current?(next?.label||data.stage):button.title+' · View workflow details');
   button.onclick=()=>activate?activate(selector):root.StudioWorkflow?.open(job.id,selector);
  });
 }
 function nextStep(host,view,activate){
  if(!host)return;
  if(!view){host.dataset.state='unavailable';host.innerHTML='<div class="jd-next-step-copy"><p><strong>Next Required Action</strong> · Workflow could not be loaded.</p></div><button type="button" class="action" data-next-step-action>Retry workflow</button>';host.querySelector('button').onclick=()=>activate(null);return;}
  const data=view.data||{},decision=root.StudioWorkflow.nextAction(view),closed=['Completed','Cancelled'].includes(data.stage);
  const owner=data.ownerAssignment,waiting=owner?.status==='Assigned'&&!decision.selector;
  const descriptions={Pending:'Confirm the booking after the deposit requirement or an approved override is satisfied.','Needs Assignment':'Assign a Job Owner to take responsibility for the next stage.','Needs Photographer Assignment':'Assign the photography crew before scheduling.','Awaiting Scheduling':'Set the date, time and location for the required sessions.',Scheduled:'Record the photographer’s progress to move the job forward.','Photography In Progress':'Finish the required sessions and record their outcomes.','Awaiting Upload':'Complete the required contributions and submit source media for review.','Awaiting Production Review':'Review the uploaded source material before accepting it into production.','Active Production':'Complete the required production work, then submit it for approval.','Awaiting Approval':'Review and approve the finished work before delivery.','Changes Requested':'Resolve the requested corrections before resubmitting the work.','Ready for Delivery':'Check the approved deliverables and record delivery. Financial release checks still apply.',Delivered:'Complete the job after delivery.'};
  const reason=closed?'This job is '+data.stage.toLowerCase()+'.':data.blockedReason?'Blocked: '+data.blockedReason:owner?.status==='Assigned'&&decision.selector?.includes('owner-response')?'Accept ownership of this job before continuing.':decision.selector==='[data-wf-owner]'?'Assign a Job Owner to take responsibility for the next stage.':waiting?'Waiting for '+(data.owner||'the assigned owner')+' to accept ownership.':!decision.selector?'Next required action: '+decision.label+'. An authorized member of staff must complete it.':descriptions[data.stage]||'Complete the required action to continue the job.';
  host.dataset.state=closed?'closed':data.blockedReason?'blocked':decision.selector?'ready':'waiting';
  const cardLabel=closed?'Job Status':'Next Required Action',detail=reason.replace(/^Next required action:\\s*/i,'');
  host.innerHTML='<div class="jd-next-step-copy"><p><strong>'+cardLabel+'</strong> · '+escape(detail)+'</p></div>'+(!closed&&decision.selector?'<button type="button" class="primary" data-next-step-action>'+escape(decision.label)+' <span aria-hidden="true">→</span></button>':'<button type="button" class="action" data-next-step-action>Review workflow</button>');
  host.querySelector('button').onclick=()=>activate(closed?null:decision.selector);
 }
 async function media(job,host,selectedHost,isCurrent,onCounts){
  let disposed=false,request=0,selectedId='',data=null,canEdit=false,uploading=false,search='',list=false;
  let pendingBatch=null;
  let collection='',subject='',assetId='',after='',page=1;const previous=[];
  const focus=root.studioMediaFocus?.jobId===job.id?root.studioMediaFocus:root.studioMediaPage?.[job.id];
  if(focus){subject=focus.subjectId||'';assetId=focus.assetId||'';root.studioMediaFocus=null;}
  const current=()=>!disposed&&isCurrent()&&host.isConnected;
  const preview=asset=>`/api/jobs/${job.id}/versions/by-id/${asset.versionId}/preview`;
  const announce=message=>{if(!current())return;let output=host.querySelector('[data-media-result]');if(!output){output=document.createElement('p');output.className='jd-media-result';output.dataset.mediaResult='';output.setAttribute('role','status');host.append(output);}output.textContent=message;};
  function bindImages(container){
   container.querySelectorAll('img').forEach(img=>img.onerror=()=>{img.hidden=true;const fallback=img.nextElementSibling;if(fallback)fallback.hidden=false;});
  }
  const isPreviewable=asset=>String(asset.contentType||asset.mimeType||'').startsWith('image/')||/\.(3fr|arw|cr2|cr3|crw|dng|erf|fff|iiq|kdc|mos|mrw|nef|nrw|orf|pef|raf|raw|rw2|rwl|sr2|srf|srw|x3f)$/i.test(String(asset.fileName||asset.name||''));
  const picture=(asset,large=false)=>isPreviewable(asset)?`<img ${large?'':'loading="lazy"'} src="${preview(asset)}${large?'':'?size=thumbnail'}" alt="${escape(asset.fileName)}"><span class="jd-preview-fallback" hidden>Preview ${escape(asset.previewStatus||'unavailable')}<br>Original file is retained</span>`:'<span class="jd-preview-fallback">▤ Document</span>';
  function openPreview(asset){
   if(!isPreviewable(asset))return;
   const dialog=studioDialog(asset.fileName,`<div class="jd-full-preview">${picture(asset,true)}</div>`);
   bindImages(dialog);
  }
  async function versions(asset){
   const dialog=studioDialog('Versions & history','<p role="status">Loading versions…</p>'),body=dialog.querySelector('.studio-dialog-body');
   try{const history=await json(`/api/jobs/${job.id}/versions/${encodeURIComponent(asset.assetKey)}`);if(!dialog.isConnected)return;body.innerHTML=history.map(version=>`<article class="task-card"><a href="/api/jobs/${job.id}/versions/by-id/${version.id}/content">${escape(version.fileName)} · v${escape(version.versionNumber)}</a><small>${escape(studioFirstName(version.createdBy))} · ${escape(new Date(version.createdAtUtc).toLocaleString())}</small><p>${escape(version.note||'')}</p></article>`).join('')||'<p>No version history available.</p>';}
   catch(error){if(dialog.isConnected)body.textContent=error.message;}
  }
  function select(asset){
   selectedId=asset.id;
   host.querySelectorAll('[data-jd-asset]').forEach(tile=>{const active=tile.dataset.jdAsset===asset.id;tile.classList.toggle('selected',active);tile.setAttribute('aria-pressed',String(active));});
   const collections=[...new Set(['Unclassified',...(data.collections||[]),asset.collection||'Unclassified'])];
   const heading=selectedHost.closest('.job-detail-selected-file').querySelector('.jd-card-heading');
   heading.innerHTML=`<h2 title="${escape(asset.fileName)}">${escape(asset.fileName)}</h2><small class="jd-selected-file-meta">${escape((asset.contentType||'File').replace('image/','').toUpperCase())} · ${escape(formatBytes(asset.sizeBytes||0))} · v${escape(asset.version||1)}</small>`;
   let tags=[...new Set(asset.tags||[])];
   const tagInputId='jd-photo-tags-'+asset.id;
   selectedHost.innerHTML=`<div class="jd-selected-preview" tabindex="0" role="button" aria-label="Open selected photo preview">${picture(asset,true)}</div><form class="jd-file-form"><div class="jd-file-fields"><label>Collection<select name="collection" ${canEdit?'':'disabled'}>${collections.map(name=>`<option ${name===(asset.collection||'Unclassified')?'selected':''}>${escape(name)}</option>`).join('')}</select></label><label>Subjects<select data-subject-picker name="subjects" multiple size="2" ${canEdit?'':'disabled'} aria-label="File subjects">${StudioSubjectPicker.options(data.subjects,asset.subjects||[])}</select></label></div><label for="${escape(tagInputId)}">Tags</label><div class="jd-tag-editor"><div class="jd-tag-tokens" role="list" aria-label="Photo tags"></div><input id="${escape(tagInputId)}" type="text" data-tag-input placeholder="Add tags; Enter or comma" aria-label="Add photo tags" ${canEdit?'':'disabled'}></div>${canEdit?'<button type="submit" class="jd-save">Save file details</button>':''}<p role="alert"></p></form><div class="jd-file-links"><a href="/api/jobs/${job.id}/versions/by-id/${asset.versionId}/content" download="${escape(asset.fileName)}">Download original</a><button type="button" data-versions>Versions</button>${canEdit?'<button type="button" data-retry-preview>Regenerate preview</button>':''}</div>`;
   bindImages(selectedHost);
   const previewButton=selectedHost.querySelector('.jd-selected-preview');previewButton.onclick=()=>openPreview(asset);previewButton.onkeydown=event=>{if(event.key==='Enter'||event.key===' '){event.preventDefault();openPreview(asset);}};
   selectedHost.querySelector('[data-versions]').onclick=()=>versions(asset);
   const form=selectedHost.querySelector('form');
   const tokens=form.querySelector('.jd-tag-tokens'),tagInput=form.querySelector('[data-tag-input]');
   const renderTags=()=>{
    tokens.innerHTML=tags.map((tag,index)=>`<span class="jd-tag-token" role="listitem">${escape(tag)}${canEdit?`<button type="button" data-remove-tag="${index}" aria-label="Remove tag ${escape(tag)}">×</button>`:''}</span>`).join('');
    tokens.querySelectorAll('[data-remove-tag]').forEach(button=>button.onclick=()=>{tags.splice(Number(button.dataset.removeTag),1);renderTags();tagInput.focus();});
   };
   const addTags=()=>{
    if(!canEdit)return;
    if(!tagInput.value.trim())return true;
    const additions=tagInput.value.split(/[,\n]/).map(value=>value.trim()).filter(Boolean),next=[...tags];
    for(const tag of additions)if(!next.some(value=>value.toLowerCase()===tag.toLowerCase()))next.push(tag);
    if(next.length>50||next.some(value=>value.length>80)){form.querySelector('[role=alert]').textContent='Use at most 50 tags, with up to 80 characters per tag.';return false;}
    tags=next;tagInput.value='';form.querySelector('[role=alert]').textContent='';renderTags();return true;
   };
   renderTags();tagInput.onkeydown=event=>{if(event.key==='Enter'||event.key===','){event.preventDefault();addTags();}};

   form.onsubmit=async event=>{
    event.preventDefault();if(!canEdit||addTags()===false)return;const button=form.querySelector('[type=submit]');button.disabled=true;
    try{await put(`/api/jobs/${job.id}/media/assets/${asset.id}`,{status:asset.status,collection:form.elements.collection.value,subjects:Array.from(form.elements.subjects.selectedOptions,o=>o.value),assignedTo:asset.assignedTo||null,captureAtUtc:asset.captureAtUtc||null,tags});await load();}
    catch(error){if(form.isConnected)form.querySelector('[role=alert]').textContent=error.message;}
    finally{if(button.isConnected)button.disabled=false;}
   };
   const retry=selectedHost.querySelector('[data-retry-preview]');if(retry)retry.onclick=async()=>{retry.disabled=true;try{await json(`/api/jobs/${job.id}/versions/by-id/${asset.versionId}/preview/retry`,{method:'POST'});if(retry.isConnected)retry.textContent='Preview queued';}catch(error){if(form.isConnected)form.querySelector('[role=alert]').textContent=error.message;retry.disabled=false;}};
  }
  function renderTiles(){
   const assets=data.assets.filter(asset=>!search||String(asset.fileName||'').toLowerCase().includes(search.toLowerCase()));
   const grid=host.querySelector('.jd-contact-sheet');grid.classList.toggle('list',list);
   grid.innerHTML=assets.map(asset=>`<button type="button" class="jd-photo ${selectedId===asset.id?'selected':''}" data-jd-asset="${escape(asset.id)}" aria-pressed="${selectedId===asset.id}"><span class="jd-photo-image">${picture(asset)}</span><strong>${escape(asset.fileName)}</strong><small>${escape(asset.collection||'Unclassified')} · ${escape(asset.status)}</small></button>`).join('')||'<p class="jd-empty">'+(search?'No matching files on this page.':'No media here. Drop photos or use Upload to add them.')+'</p>';
   bindImages(grid);
   grid.querySelectorAll('[data-jd-asset]').forEach(tile=>{const asset=data.assets.find(a=>a.id===tile.dataset.jdAsset);tile.onclick=()=>select(asset);tile.ondblclick=()=>openPreview(asset);});
  }
  async function change(changes){
   if('collection' in changes){collection=changes.collection;if(collection&&data.subjects.find(s=>s.id===subject)?.collection!==collection)subject='';}
   if('subject' in changes)subject=changes.subject;
   after='';page=1;previous.length=0;assetId='';selectedId='';await load();
  }
  function draw(){
   const counts=data.collectionCounts||{},collections=[...new Set(['Unclassified',...(data.collections||[])])];
   host.innerHTML=`<header class="jd-gallery-toolbar"><h2>All Media</h2><select data-collection aria-label="Filter media by collection"><option value="">All collections</option>${collections.map(name=>`<option ${name===collection?'selected':''}>${escape(name)}</option>`).join('')}</select><select data-subject-picker data-job-subject aria-label="Filter media by subject"><option value="">All subjects</option>${StudioSubjectPicker.options(data.subjects,subject)}<option value="__details">Subject details…</option></select><input type="search" data-media-search placeholder="Search this page…" aria-label="Search photos on this page" value="${escape(search)}"><button type="button" class="jd-tool-button" data-job-tasks aria-haspopup="dialog">☑ Tasks</button><span class="jd-drop-hint">⇧ Drop photos or folders to upload</span><div class="jd-view-controls"><button type="button" data-grid aria-label="Grid view" aria-pressed="${!list}">▦</button><button type="button" data-list aria-label="List view" aria-pressed="${list}">☰</button></div>${canEdit?'<button type="button" class="jd-upload" data-add>＋ Upload</button><input type="file" data-files multiple hidden>':''}</header><nav class="jd-collections" aria-label="Media collections"><button type="button" data-filter="" class="${collection?'':'active'}">All <span>${Object.values(counts).length?Object.values(counts).reduce((a,b)=>a+Number(b),0):data.totalAssets}</span></button>${collections.map(name=>`<button type="button" data-filter="${escape(name)}" class="${collection===name?'active':''}">${escape(name)}${counts[name]!=null?` <span>${counts[name]}</span>`:''}</button>`).join('')}${subject?'<button type="button" data-clear-subject>Clear subject filter ×</button>':''}</nav><div class="jd-contact-sheet" tabindex="0" aria-label="Job photos and upload target"></div><footer class="jd-gallery-footer"><span>${search?`${data.assets.filter(a=>String(a.fileName||'').toLowerCase().includes(search.toLowerCase())).length} visible on this page · `:''}Showing ${data.assets.length?(page-1)*12+1:0}–${(page-1)*12+data.assets.length} of ${data.totalAssets??data.assets.length}</span><div><button type="button" data-previous ${previous.length?'':'disabled'} aria-label="Previous media page">‹</button><span>Page ${page}</span><button type="button" data-next ${data.hasMore?'':'disabled'} aria-label="Next media page">›</button></div></footer><p class="jd-media-result" role="status" data-media-result></p>`;
   renderTiles();
   host.querySelector('[data-collection]').value=collection;
   host.querySelector('[data-collection]').onchange=e=>change({collection:e.target.value});
   const subjectControl=host.querySelector('[data-job-subject]');
   subjectControl.onchange=async()=>{if(subjectControl.value==='__details'){subjectControl.value=subject;await host.studioOpenSubjects?.();return;}await change({subject:subjectControl.value});};
   host.querySelectorAll('[data-filter]').forEach(button=>{button.onclick=()=>change({collection:button.dataset.filter});if(canEdit){button.ondragover=e=>{if(e.dataTransfer?.types?.includes('Files'))e.preventDefault();};button.ondrop=async e=>{e.preventDefault();e.stopPropagation();await upload(await studioDroppedPhotos(e.dataTransfer),button.dataset.filter||'Unclassified');};}});
   const clear=host.querySelector('[data-clear-subject]');if(clear)clear.onclick=()=>change({subject:''});
   const rosterButton=document.createElement('button');rosterButton.type='button';rosterButton.textContent='Subjects / roster';rosterButton.onclick=()=>root.openIntakeRoster(job.conversationId);host.querySelector('.jd-gallery-toolbar')?.append(rosterButton);
   host.querySelector('[data-media-search]').oninput=e=>{search=e.target.value;renderTiles();};
   host.querySelector('[data-job-tasks]').onclick=event=>host.studioOpenTasks?.(event);
   for(const [selector,value] of [['[data-grid]',false],['[data-list]',true]])host.querySelector(selector).onclick=()=>{list=value;renderTiles();host.querySelector('[data-grid]').setAttribute('aria-pressed',String(!list));host.querySelector('[data-list]').setAttribute('aria-pressed',String(list));};
   host.querySelector('[data-previous]').onclick=async()=>{after=previous.pop()||'';page--;await load();};
   host.querySelector('[data-next]').onclick=async()=>{previous.push(after);after=data.assets.map(a=>a.id).sort().at(-1)||'';page++;await load();};
   if(canEdit){
    const files=host.querySelector('[data-files]');host.querySelector('[data-add]').onclick=()=>files.click();files.onchange=()=>upload(Array.from(files.files));
    host.ondragover=e=>{if(e.dataTransfer?.types?.includes('Files')){e.preventDefault();host.classList.add('jd-drag-over');}};
    host.ondragleave=e=>{if(!host.contains(e.relatedTarget))host.classList.remove('jd-drag-over');};
    host.ondrop=async e=>{e.preventDefault();host.classList.remove('jd-drag-over');try{await upload(await studioDroppedPhotos(e.dataTransfer));}catch(error){announce(error.message);}};
   }
   const selected=data.assets.find(a=>a.id===selectedId)||data.assets[0];
   if(selected)select(selected);else{selectedId='';selectedHost.closest('.job-detail-selected-file').querySelector('.jd-card-heading').innerHTML='<h2>Selected Image</h2>';selectedHost.innerHTML='<p class="jd-empty">Select a photo in the gallery to inspect it.</p>';}
  }
  async function load(){
   const version=++request;
   host.setAttribute('aria-busy','true');
   try{
    const q=new URLSearchParams({limit:'12'});for(const [name,value] of Object.entries({collection,subjectId:subject,assetId,after}))if(value)q.set(name,value);
    const [snapshot,access]=await Promise.all([json(`/api/jobs/${job.id}/media?${q}`),json(`/api/jobs/${job.id}/access`)]);
    if(!current()||version!==request)return;
    data=snapshot;canEdit=!!(access.permissions||access).manageProduction;draw();
    const counts=Object.values(data.collectionCounts||{});onCounts(counts.length?counts.reduce((a,b)=>a+Number(b),0):data.totalAssets??data.assets.length,data.totalSubjects??data.subjects.length,access.permissions||access);
   }catch(error){if(current()&&version===request){host.innerHTML='<p role="alert"></p><button type="button" class="jd-save">Retry</button>';host.querySelector('p').textContent=error.message;host.querySelector('button').onclick=load;}}
   finally{if(current()&&version===request)host.removeAttribute('aria-busy');}
  }
  async function upload(files,target=collection||data?.gallery?.default_collection||'Unclassified'){
   if(!canEdit||uploading||!files.length)return;uploading=true;
   try{
    if(pendingBatch)throw Error('Retry the pending batch before adding more files.');
    const view=await json(`/api/jobs/${job.id}/connected-workflow`),sessions=view.data.sessions||[];
    let sessionId=sessions[0]?.id||null;
    if(sessions.length>1){
     sessionId=await new Promise(resolve=>{const d=studioDialog('Choose upload session',`<form><label>Session<select name="session">${sessions.map(s=>`<option value="${escape(s.id)}">${escape(s.title)}</option>`).join('')}</select></label><button type="submit" class="primary">Upload</button></form>`);d.addEventListener('close',()=>resolve(null),{once:true});d.querySelector('form').onsubmit=e=>{e.preventDefault();resolve(e.currentTarget.elements.session.value);d.close();};});
     if(!sessionId)return;
    }
    pendingBatch={declaration:{batchId:crypto.randomUUID(),sessionId,subjectId:subject||null,collection:target,files:files.map(file=>({uploadId:crypto.randomUUID(),fileName:file.name,sizeBytes:file.size}))},files};
    await sendBatch();
   }catch(error){announce(error.message);if(pendingBatch)retryButton();}finally{uploading=false;}
  }
  function retryButton(){
   const output=host.querySelector('[data-media-result]');if(!output||output.querySelector('button'))return;
   const b=document.createElement('button');b.type='button';b.className='jd-save';b.textContent='Retry original batch';output.append(b);
   b.onclick=async()=>{if(uploading)return;uploading=true;b.disabled=true;try{await sendBatch();}catch(e){announce(e.message);retryButton();}finally{uploading=false;}};
  }
  async function sendBatch(){
   const batch=pendingBatch;
   await json(`/api/jobs/${job.id}/media/upload-batches`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(batch.declaration)});
   let completed=0;const failed=[];
   for(let i=0;i<batch.files.length;i++){
    const file=batch.files[i];announce(`Uploading ${i+1}/${batch.files.length}: ${file.name}`);
    try{const form=new FormData();form.append('file',file);form.append('uploadId',batch.declaration.files[i].uploadId);await json(`/api/jobs/${job.id}/media/uploads`,{method:'POST',body:form});completed++;}catch(e){failed.push(file.name+': '+e.message);}
   }
   if(!failed.length)pendingBatch=null;
   if(current()){await load();announce(`${completed}/${batch.files.length} stored.`+(failed.length?' Failed: '+failed.join('; '):''));if(failed.length)retryButton();}
  }
  async function importRoster(){
   if(!canEdit)return;
   await root.openIntakeRoster(job.conversationId);
  }

  const controller={reload:()=>{const focus=root.studioMediaFocus?.jobId===job.id?root.studioMediaFocus:root.studioMediaPage?.[job.id];if(focus?.subjectId!==undefined){subject=focus.subjectId||'';root.studioMediaFocus=null;after='';page=1;previous.length=0;}return load();},upload:()=>host.querySelector('[data-add]')?.click(),importRoster,dispose:()=>{disposed=true;request++;host.ondrop=null;host.ondragover=null;}};
  host.studioJobMedia?.dispose();host.studioJobMedia=controller;
  await load();return controller;
 }
 root.StudioJobDetails={workflow,phaseIndex,media,nextStep};
})(window);

// Job-only compact projection. The template designer retains the complete editable canvas.
(function(root){
 'use strict';
 function compact(host,view,job,summaryHost,authority={}){
  const esc=root.StudioGraph.esc,states=new Map(view.evaluation.nodes.map(n=>[n.id,n])),tasks=new Map(view.tasks.map(t=>[t.nodeId,t]));
  let expanded=null,selected=null;
  const terminal=new Set(['Completed','Skipped','Inactive']);
  function leaves(node,id){return node.kind==='Subflow'&&node.subflow?node.subflow.nodes.flatMap(n=>leaves(n,id+'/'+n.id)):[{node,id}];}
  function state(node,id){
   if(node.kind!=='Subflow'){const evaluated=states.get(id),task=tasks.get(id),reason=task?.reason||evaluated?.reason||'';return evaluated?.status==='Blocked'&&(!task||['NotStarted','Scheduled'].includes(task.status))&&/waiting for (active )?prerequisites/i.test(reason)?'Pending':evaluated?.status||'Unknown';}
   const children=leaves(node,id),work=children.filter(x=>x.node.kind==='Task'),end=node.subflow.nodes.find(n=>n.kind==='End');
   if(children.every(x=>states.get(x.id)?.status==='Inactive'))return 'Inactive';
   if(end&&states.get(id+'/'+end.id)?.status==='Completed'&&work.every(x=>terminal.has(states.get(x.id)?.status)))return 'Completed';
   const values=work.map(x=>state(x.node,x.id));
   if(values.some(s=>['InProgress','Partial','ReadyForDelivery'].includes(s)))return 'InProgress';
   if(values.includes('Available'))return 'Available';
   return values.some(s=>['Blocked','Failed','ReworkRequired','ReprintRequired','WaitingOnClient'].includes(s))?'Blocked':'Pending';
  }
  function summary(node,id){
   const work=leaves(node,id).filter(x=>x.node.kind==='Task'&&states.get(x.id)?.status!=='Inactive');
   if(node.kind!=='Subflow')return state(node,id)+(tasks.get(id)?.optional?' · Optional':'');
   const complete=work.filter(x=>['Completed','Skipped'].includes(states.get(x.id)?.status)).length;
   const blockers=work.filter(x=>['Blocked','Failed','ReworkRequired','ReprintRequired','WaitingOnClient'].includes(states.get(x.id)?.status));
   return `${complete} of ${work.length} steps complete${blockers.length?' · '+blockers.length+' blocked / waiting':''}`;
  }
  function ranks(graph){
   const rank=new Map(graph.nodes.map(n=>[n.id,0]));
   // Definitions are server validated DAGs; ignore inactive connections in the job projection.
   const edges=graph.edges.filter(e=>view.evaluation.edges[e.id]!=='Inactive');
   for(let i=0;i<graph.nodes.length;i++){let changed=false;for(const e of edges)if(rank.has(e.from)&&rank.has(e.to)&&rank.get(e.to)<=rank.get(e.from)){rank.set(e.to,rank.get(e.from)+1);changed=true;}if(!changed)break;}
   return rank;
  }
  function shortLabel(value){
   const label=String(value||'Step'),rules=[[/quality|\\bqc\\b/i,'QC'],[/photograph|shoot/i,'Shoot'],[/assign/i,'Assign'],[/upload/i,'Upload'],[/edit|retouch/i,'Edit'],[/print/i,'Print'],[/approv|review/i,'Review'],[/pack|assembl/i,'Package'],[/deliver/i,'Delivery'],[/produc/i,'Production'],[/book|deposit/i,'Booking'],[/schedul/i,'Schedule'],[/publish|gallery/i,'Publish'],[/complet|finish/i,'Complete']];
   return rules.find(([pattern])=>pattern.test(label))?.[1]||label.split(/\\s+/).filter(word=>! /^(the|a|an|to|for|and|of)$/i.test(word))[0]||'Step';
  }
  function dot(node,id,small=false){
   const status=state(node,id),tone=status==='Completed'?'green':['Available','InProgress','Partial','ReadyForDelivery'].includes(status)?'amber':['Blocked','Failed','ReworkRequired','ReprintRequired','WaitingOnClient'].includes(status)?'red':'gray',icon=tone==='green'?'✓':status==='Skipped'||status==='Inactive'?'–':status==='Available'?'▶':tone==='amber'?'◉':tone==='red'?'!':'○';
   return `<button type="button" class="jd-workflow-dot ${small?'is-component':''} ${esc(status.toLowerCase())}" data-flow-tone="${tone}" data-workflow-node="${esc(id)}" ${node.kind==='Subflow'?`aria-expanded="${expanded===id||expanded?.startsWith(id+'/')}"`:''} title="${esc(node.label+(node.description||node.task?.description||tasks.get(id)?.description?' · '+(node.description||node.task?.description||tasks.get(id)?.description):'')+': '+summary(node,id)+(tasks.get(id)?' · Responsible: '+(tasks.get(id).assignedTo||'Unassigned')+(tasks.get(id).reason?' · '+tasks.get(id).reason:''):''))}" aria-label="${esc(node.label+': '+summary(node,id))}"><span class="jd-stage-light" aria-hidden="true">${icon}</span><strong>${esc(shortLabel(node.label))}</strong></button>`;
  }
  function expansion(node,id){
   return `<div class="jd-dot-subflow" role="group" aria-label="${esc(node.label)} subflow">${summaryHost?'':`<span class="jd-subflow-endpoint" title="Start of ${esc(node.label)}"><span class="jd-stage-light" aria-hidden="true">○</span><strong>Stage Start</strong></span>`}${flow(node.subflow,id+'/',true)}${summaryHost?'':`<span class="jd-subflow-endpoint" title="${esc(node.label)}: ${esc(state(node,id))}"><span class="jd-stage-light" aria-hidden="true">${state(node,id)==='Completed'?'✓':'○'}</span><strong>Stage Complete</strong></span>`}</div>`;
  }
  function flow(graph,prefix='',small=false){
   const model={nodes:graph.nodes.map(n=>({...n,id:prefix+n.id})),edges:graph.edges.map(e=>({...e,id:prefix+e.id,from:prefix+e.from,to:prefix+e.to}))},rank=ranks(model);
   const nodes=graph.nodes.filter(n=>n.kind!=='Start'&&n.kind!=='End'&&state(n,prefix+n.id)!=='Inactive');
   const levels=[...new Set(nodes.map(n=>rank.get(prefix+n.id)))].sort((a,b)=>a-b);
   return `<div class="jd-dot-levels" aria-label="Workflow dependency groups; steps in a column may run in parallel">${levels.map(level=>`<div class="jd-dot-level">${nodes.filter(n=>rank.get(prefix+n.id)===level).map(n=>{
    const id=prefix+n.id,open=n.kind==='Subflow'&&(expanded===id||expanded?.startsWith(id+'/'));
    return `<section class="jd-dot-stage ${open?'is-expanded':''}">${dot(n,id,small)}${open?`<div class="jd-dot-expansion">${expansion(n,id)}</div>`:''}</section>`;
   }).join('')}</div>`).join('')}</div>`;
  }
  function find(graph,id,prefix=''){for(const n of graph.nodes){if(prefix+n.id===id)return n;if(n.subflow){const found=find(n.subflow,id,prefix+n.id+'/');if(found)return found;}}}
  function draw(focus){
   host.style.height='auto';host.classList.add('jd-compact-workflow');
   const expandedNode=expanded?find(view.graph,expanded):null;
   const content=summaryHost?(expandedNode?`<section class="jd-dot-stage is-expanded"><div class="jd-dot-expansion">${expansion(expandedNode,expanded)}</div></section>`:''):flow(view.graph);
   host.innerHTML=content+'<div data-workflow-details aria-live="polite"></div>';
   host.querySelectorAll('[data-workflow-node]').forEach(button=>button.onclick=()=>{
    const id=button.dataset.workflowNode,node=find(view.graph,id);
    if(node.kind==='Subflow'){expanded=expanded===id?null:id;selected=null;draw(id);}else{selected=id;details(node,id);}
   });
   if(focus)[...host.querySelectorAll('[data-workflow-node]')].find(b=>b.dataset.workflowNode===focus)?.focus();
   if(summaryHost){summaryHost.innerHTML=view.graph.nodes.filter(n=>!['Start','End'].includes(n.kind)&&state(n,n.id)!=='Inactive').map(n=>{const open=n.kind==='Subflow'&&(expanded===n.id||expanded?.startsWith(n.id+'/'));return `<li class="${open?'jd-main-expanded':''}">${open?`<div class="jd-main-endpoints" data-main-expanded="${esc(n.id)}">${dot(n,n.id).replace('class="jd-workflow-dot ','data-main-start class="jd-workflow-dot ').replace(`<strong>${esc(shortLabel(n.label))}</strong>`,'<strong>Start</strong>')}<span class="jd-main-dashed" aria-hidden="true"></span>${dot(n,n.id).replace('class="jd-workflow-dot ','data-main-complete class="jd-workflow-dot ').replace(`<strong>${esc(shortLabel(n.label))}</strong>`,'<strong>Complete</strong>')}</div>`:dot(n,n.id)}</li>`;}).join('');summaryHost.querySelectorAll('[data-workflow-node]').forEach(b=>b.onclick=()=>{const id=b.dataset.workflowNode,node=find(view.graph,id);if(node.kind==='Subflow'){expanded=expanded===id?null:id;selected=null;draw();}else{const close=selected===id;expanded=null;selected=close?null:id;draw();if(!close)details(node,id);}summaryHost.querySelectorAll('[data-workflow-node]').forEach(button=>{if(button.dataset.workflowNode===id)button.focus();});});}
   if(summaryHost){notifyDrawer();scheduleRoutes();}
  }
  let routeFrame=0;
  function scheduleRoutes(){
   const scope=host.closest('.job-detail-heading')||host.parentElement;
   scope?.querySelector('[data-subflow-routes]')?.remove();
   if(!summaryHost||!expanded||!root.requestAnimationFrame)return;
   root.cancelAnimationFrame?.(routeFrame);
   routeFrame=root.requestAnimationFrame(()=>{
    if(!host.isConnected)return;
    const pair=summaryHost.querySelector('[data-main-expanded]'),pathNode=find(view.graph,expanded);
    if(!pair||!pathNode?.subflow)return;
    const a=pair.querySelector('[data-main-start] .jd-stage-light'),b=pair.querySelector('[data-main-complete] .jd-stage-light');
    const bounds=scope.getBoundingClientRect();
    const point=element=>{const r=element.getBoundingClientRect();return{x:r.left+r.width/2-bounds.left-scope.clientLeft,y:r.top+r.height/2-bounds.top-scope.clientTop,rx:r.width/2,ry:r.height/2};};
    if(!a||!b||!bounds.width)return;
    // Constrain component dots to the horizontal interval of the main endpoints.
    const subflow=host.querySelector('.jd-dot-subflow'),startRect=a.getBoundingClientRect(),endRect=b.getBoundingClientRect();
    if(subflow){
     const parent=subflow.parentElement.getBoundingClientRect(),left=Math.min(startRect.left+startRect.width/2,endRect.left+endRect.width/2),right=Math.max(startRect.left+startRect.width/2,endRect.left+endRect.width/2);
     const inset=Math.min(32,(right-left)*.12);
     subflow.style.marginLeft=Math.max(0,left-parent.left+inset)+'px';
     subflow.style.marginRight=Math.max(0,parent.right-right+inset)+'px';
     subflow.style.paddingLeft='0';subflow.style.paddingRight='0';
     subflow.classList.add('jd-subflow-between-endpoints');
    }
    const begin=point(a),end=point(b),positions=new Map();
    for(const n of pathNode.subflow.nodes){
     if(n.kind==='Start')positions.set(n.id,begin);
     else if(n.kind==='End')positions.set(n.id,end);
     else{const button=[...host.querySelectorAll('[data-workflow-node]')].find(el=>el.dataset.workflowNode===expanded+'/'+n.id);if(button)positions.set(n.id,point(button.querySelector('.jd-stage-light')));}
    }
    const paths=pathNode.subflow.edges.flatMap(edge=>{
     const from=positions.get(edge.from),to=positions.get(edge.to);if(!from||!to||view.evaluation.edges[expanded+'/'+edge.id]==='Inactive')return [];
     const start=pathNode.subflow.nodes.find(n=>n.id===edge.from)?.kind==='Start',finish=pathNode.subflow.nodes.find(n=>n.id===edge.to)?.kind==='End';
     // Attach to the circle's rim, with Git-style curves into and out of branch lanes.
     const direction=to.x>=from.x?1:-1;
     const x1=start?from.x:from.x+direction*from.rx,y1=start?from.y+from.ry:from.y;
     const x2=finish?to.x:to.x-direction*to.rx,y2=finish?to.y+to.ry:to.y;
     const bend=Math.abs(x2-x1)*0.55228475,drop=Math.abs(y2-y1)*0.55228475;
     const radius=Math.min(12,Math.abs(y2-y1)/3,Math.abs(x2-x1)/3);
     const d=start?`M ${x1} ${y1} V ${y2-radius} Q ${x1} ${y2}, ${x1+direction*radius} ${y2} H ${x2}`:finish?`M ${x1} ${y1} H ${x2-direction*radius} Q ${x2} ${y1}, ${x2} ${y1-radius} V ${y2}`:`M ${x1} ${y1} C ${x1+direction*bend} ${y1}, ${x2-direction*bend} ${y2}, ${x2} ${y2}`;
     return [`<path d="${d}"/>`];
    });
    const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');svg.dataset.subflowRoutes='';svg.classList.add('jd-subflow-routes');svg.setAttribute('aria-hidden','true');svg.setAttribute('viewBox',`0 0 ${scope.clientWidth||bounds.width} ${scope.clientHeight||bounds.height}`);svg.setAttribute('preserveAspectRatio','none');svg.innerHTML=paths.join('');scope.prepend(svg);
   });
  }
  host.__workflowRouteCleanup?.();
  host.__workflowRouteObserver?.disconnect();
  const routeScope=host.closest('.job-detail-heading')||host.parentElement;
  routeScope?.addEventListener('transitionend',scheduleRoutes);
  routeScope?.addEventListener('scroll',scheduleRoutes,true);
  host.__workflowRouteCleanup=()=>{routeScope?.removeEventListener('transitionend',scheduleRoutes);routeScope?.removeEventListener('scroll',scheduleRoutes,true);root.cancelAnimationFrame?.(routeFrame);routeScope?.querySelector('[data-subflow-routes]')?.remove();};
  if(root.ResizeObserver){host.__workflowRouteObserver=new ResizeObserver(scheduleRoutes);host.__workflowRouteObserver.observe(host);if(summaryHost)host.__workflowRouteObserver.observe(summaryHost);}
  function notifyDrawer(){host.dispatchEvent(new CustomEvent('studio-flow-details-toggle',{bubbles:true,detail:{open:!!(expanded||selected)}}));}
  function details(node,id){
   const task=tasks.get(id),evaluation=states.get(id),reason=task?.reason||evaluation?.reason,next=root.StudioWorkflow?.nextAction?.(authority.data?authority:{...authority,data:{}}),canAssign=task&&!task.assignedTo&&(authority.canManage||authority.canReview);
   const hostDetails=host.querySelector('[data-workflow-details]');
   hostDetails.innerHTML=`<section class="jd-dot-details"><strong>${esc(node.label)} · ${esc(state(node,id))}</strong><p>${esc(reason||(['Available','Completed','Skipped'].includes(state(node,id))?'No outstanding prerequisite reported.':'No specific blocker recorded; review workflow requirements.'))}</p><p>Responsible: ${esc(task?.assignedTo||'Unassigned')}${task?.role?' · Team / role: '+esc(task.role):''}${task?.optional?' · Optional step':''}</p><p>Depends on: ${esc((task?.dependencyLabels||evaluation?.dependencies||[]).join(', ')||'Workflow start')}</p><button type="button" data-existing-task>Review requirements, assignment and available actions →</button>${canAssign?'<button type="button" data-step-assign>Assign responsible person →</button>':''}${next?.selector?`<button type="button" data-step-next>${esc(next.label)} →</button>`:''}<button type="button" class="jd-link" data-close-step>Close details</button></section>`;
   hostDetails.querySelector('[data-existing-task]').onclick=()=>root.StudioJobGraph.open(job.id,task?.id).catch(e=>alert(e.message));
   hostDetails.querySelector('[data-step-assign]')?.addEventListener('click',async()=>{try{const dialog=await root.StudioJobGraph.open(job.id,task.id);[...dialog.querySelectorAll('[data-assign]')].find(b=>b.dataset.assign===task.id)?.click();}catch(e){alert(e.message);}});
   hostDetails.querySelector('[data-step-next]')?.addEventListener('click',()=>root.StudioWorkflow.open(job.id,next.selector).catch(e=>alert(e.message)));
   hostDetails.querySelector('[data-close-step]').onclick=()=>{selected=null;hostDetails.innerHTML='';notifyDrawer();[...host.querySelectorAll('[data-workflow-node]'),...(summaryHost?.querySelectorAll('[data-workflow-node]')||[])].find(b=>b.dataset.workflowNode===id)?.focus();};
  }
  draw();return {get expanded(){return expanded;},get selected(){return selected;}};
 }
 root.StudioJobDetails.compact=compact;
})(window);

