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
  const positions=new Map();groups.forEach(([,labels],column)=>labels.forEach((label,row)=>positions.set(label,{x:column*170+18,y:9+row*25})));
  [['Cancelled',1],['Reshoot Required',2],['Changes Requested',4],['Blocked',5]].forEach(([label,column])=>positions.set(label,{x:column*170+18,y:128}));
  if(!positions.has(data.stage))positions.set(data.stage,{x:18,y:128});
  const labels=[...positions.keys()];
  const nodes=labels.map(label=>{
   const skipped=noShoot&&(photographyStages.has(label)||label==='Reshoot Required'),current=label===data.stage||(label==='Blocked'&&!!data.blockedReason)||(label==='Reshoot Required'&&reshoot);
   const milestone=label==='Photographer Assigned'||label==='Shoot Complete';
   const complete=hostStageComplete(label),finished=label==='Completed'&&current;
   const state=skipped?'skipped':current&&complete?'current complete':current?(label==='Cancelled'||label==='Changes Requested'||label==='Blocked'||label==='Reshoot Required'?'current exception':'current'):complete?'complete':'pending';
   const description=skipped?'Not required':finished?'Successfully completed':current?'Current':complete?(milestone?'Confirmed':'Successfully passed'):'Not yet passed';
   const point=positions.get(label);
   return `<button type="button" style="left:${point.x-9}px;top:${point.y-9}px" class="jd-stage ${state}" data-flow-stage="${escape(label)}" title="${escape(label+' · '+description)}" ${label===data.stage?'aria-current="step"':''}><span class="jd-stage-light" aria-hidden="true">${skipped?'–':current&&state.includes('exception')?'!':complete?'✓':''}</span><span>${escape(label)}${skipped?' <small>Not required</small>':''}</span></button>`;
  }).join('');
  const edge=(from,to,kind='main',returning=false)=>{
   const a=positions.get(from),b=positions.get(to);if(!a||!b)return '';
   const recorded=hostStageComplete(from)&&hostStageComplete(to),highlight=kind!=='main'&&(to===data.stage||(to==='Blocked'&&data.blockedReason)||(to==='Reshoot Required'&&reshoot));
   let path;
   if(kind==='main'&&a.x===b.x)path=`M ${a.x} ${a.y} L ${b.x} ${b.y}`;
   else if(kind==='main')path=`M ${a.x} ${a.y} L ${a.x} 92 Q ${a.x} 101 ${a.x+9} 101 L ${b.x-32} 101 Q ${b.x-41} 101 ${b.x-41} 92 L ${b.x-41} ${b.y+9} Q ${b.x-41} ${b.y} ${b.x-32} ${b.y} L ${b.x} ${b.y}`;
   else {const lane=returning?(kind==='reshoot'?158:153):({correction:109,reshoot:114,cancel:119,block:124}[kind]||114);path=`M ${a.x} ${a.y} C ${a.x-14} ${a.y+14} ${a.x-14} ${lane} ${a.x} ${lane} L ${b.x} ${lane} C ${b.x+14} ${lane} ${b.x+14} ${b.y+14} ${b.x} ${b.y}`;}
   return `<path data-flow-edge="${escape(from+' → '+to)}" class="jd-flow-edge ${kind} ${highlight?'attention':recorded?'recorded':''}" d="${path}"/>`;
  };
  function hostStageComplete(label){return label==='Photographer Assigned'?crewAssigned:label==='Shoot Complete'?shootComplete:label==='Completed'?data.stage==='Completed':passed.has(label);}
  const mainEdges=main.slice(1).map((label,index)=>edge(main[index],label)).join('');
  const correctionEdges=(data.correctionKind? [data.correctionKind]:['Source','Production']).map(kind=>{const origin=kind==='Source'?'Awaiting Production Review':'Awaiting Approval',target=kind==='Source'?'Awaiting Production Review':'Active Production';return edge(origin,'Changes Requested','correction')+edge('Changes Requested',target,'correction',true);}).join('');
  const previous=(data.history||[]).filter(h=>positions.has(h.new_stage)&&!['Cancelled','Blocked','Reshoot Required','Changes Requested'].includes(h.new_stage)).at(-1)?.new_stage;
  const branchOrigin=main.includes(data.stage)?data.stage:previous||'Pending';
  const branches=correctionEdges+edge('Shoot Complete','Reshoot Required','reshoot')+edge('Reshoot Required','Awaiting Scheduling','reshoot',true)+edge(branchOrigin,'Cancelled','cancel')+edge(branchOrigin,'Blocked','block');
  host.innerHTML=`<div class="jd-flow-canvas"><svg class="jd-flow-lines" viewBox="0 0 1020 161" aria-hidden="true" focusable="false">${mainEdges}${branches}</svg>${nodes}</div>`;
  host.setAttribute('aria-label','Job workflow: '+data.stage+(data.blockedReason?' · Blocked: '+data.blockedReason:''));
  const focusPoint=positions.get(data.stage);if(focusPoint&&host.clientWidth)host.scrollLeft=Math.max(0,focusPoint.x-host.clientWidth/2);
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
  host.querySelectorAll('.jd-stage').forEach(button=>{
   const label=button.dataset.flowStage,current=label===data.stage;
   const selector=current?next?.selector:label==='Blocked'&&data.blockedReason&&view.canManage?'[data-wf-block]':label==='Reshoot Required'&&reshoot&&view.canManage&&data.ownerAssignment?.status==='Accepted'&&['Scheduled','Photography In Progress','Awaiting Upload'].includes(data.stage)&&(data.outstandingRoster||[]).length?'[data-wf-remainder]':null;
   button.setAttribute('aria-label',current?(next?.label||data.stage):button.title+' · View workflow details');
   button.onclick=()=>activate?activate(selector):root.StudioWorkflow?.open(job.id,selector);
  });
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
  const picture=(asset,large=false)=>String(asset.contentType||'').startsWith('image/')?`<img ${large?'':'loading="lazy"'} src="${preview(asset)}${large?'':'?size=thumbnail'}" alt="${escape(asset.fileName)}"><span class="jd-preview-fallback" hidden>Preview ${escape(asset.previewStatus||'unavailable')}<br>Original file is retained</span>`:'<span class="jd-preview-fallback">▤ Document</span>';
  function openPreview(asset){
   if(!String(asset.contentType||'').startsWith('image/'))return;
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
   selectedHost.innerHTML=`<div class="jd-selected-preview" tabindex="0" role="button" aria-label="Open selected photo preview">${picture(asset,true)}</div><strong>${escape(asset.fileName)}</strong><small>${escape((asset.contentType||'File').replace('image/','').toUpperCase())} · ${escape(formatBytes(asset.sizeBytes||0))} · v${escape(asset.version||1)}</small><form class="jd-file-form"><label>Collection<select name="collection" ${canEdit?'':'disabled'}>${collections.map(name=>`<option ${name===(asset.collection||'Unclassified')?'selected':''}>${escape(name)}</option>`).join('')}</select></label><label>Subjects<select name="subjects" multiple size="2" ${canEdit?'':'disabled'} aria-label="File subjects">${data.subjects.map(person=>`<option value="${escape(person.id)}" ${(asset.subjects||[]).includes(person.id)?'selected':''}>${escape(person.name)}</option>`).join('')}</select></label>${canEdit?'<button type="submit" class="jd-save">Save file details</button>':''}<p role="alert"></p></form><div class="jd-file-links"><a href="/api/jobs/${job.id}/versions/by-id/${asset.versionId}/content" download="${escape(asset.fileName)}">Download original</a><button type="button" data-versions>Versions</button>${canEdit?'<button type="button" data-retry-preview>Regenerate preview</button>':''}</div>`;
   bindImages(selectedHost);
   const previewButton=selectedHost.querySelector('.jd-selected-preview');previewButton.onclick=()=>openPreview(asset);previewButton.onkeydown=event=>{if(event.key==='Enter'||event.key===' '){event.preventDefault();openPreview(asset);}};
   selectedHost.querySelector('[data-versions]').onclick=()=>versions(asset);
   const form=selectedHost.querySelector('form');
   form.onsubmit=async event=>{
    event.preventDefault();if(!canEdit)return;const button=form.querySelector('button');button.disabled=true;
    try{await put(`/api/jobs/${job.id}/media/assets/${asset.id}`,{status:asset.status,collection:form.elements.collection.value,subjects:Array.from(form.elements.subjects.selectedOptions,o=>o.value),assignedTo:asset.assignedTo||null,captureAtUtc:asset.captureAtUtc||null});await load();}
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
   if('collection' in changes)collection=changes.collection;
   if('subject' in changes)subject=changes.subject;
   after='';page=1;previous.length=0;assetId='';selectedId='';await load();
  }
  function draw(){
   const counts=data.collectionCounts||{},collections=[...new Set(['Unclassified',...(data.collections||[])])];
   host.innerHTML=`<header class="jd-gallery-toolbar"><h2>All Media</h2><select data-collection aria-label="Filter media by collection"><option value="">All collections</option>${collections.map(name=>`<option ${name===collection?'selected':''}>${escape(name)}</option>`).join('')}</select><input type="search" data-media-search placeholder="Search this page…" aria-label="Search photos on this page" value="${escape(search)}"><span class="jd-drop-hint">⇧ Drop photos or folders to upload</span><div class="jd-view-controls"><button type="button" data-grid aria-label="Grid view" aria-pressed="${!list}">▦</button><button type="button" data-list aria-label="List view" aria-pressed="${list}">☰</button></div>${canEdit?'<button type="button" class="jd-upload" data-add>＋ Upload</button><input type="file" data-files multiple hidden>':''}</header><nav class="jd-collections" aria-label="Media collections"><button type="button" data-filter="" class="${collection?'':'active'}">All <span>${Object.values(counts).length?Object.values(counts).reduce((a,b)=>a+Number(b),0):data.totalAssets}</span></button>${collections.map(name=>`<button type="button" data-filter="${escape(name)}" class="${collection===name?'active':''}">${escape(name)}${counts[name]!=null?` <span>${counts[name]}</span>`:''}</button>`).join('')}${subject?'<button type="button" data-clear-subject>Clear subject filter ×</button>':''}</nav><div class="jd-contact-sheet" tabindex="0" aria-label="Job photos and upload target"></div><footer class="jd-gallery-footer"><span>${search?`${data.assets.filter(a=>String(a.fileName||'').toLowerCase().includes(search.toLowerCase())).length} visible on this page · `:''}Showing ${data.assets.length?(page-1)*12+1:0}–${(page-1)*12+data.assets.length} of ${data.totalAssets??data.assets.length}</span><div><button type="button" data-previous ${previous.length?'':'disabled'} aria-label="Previous media page">‹</button><span>Page ${page}</span><button type="button" data-next ${data.hasMore?'':'disabled'} aria-label="Next media page">›</button></div></footer><p class="jd-media-result" role="status" data-media-result></p>`;
   renderTiles();
   host.querySelector('[data-collection]').value=collection;
   host.querySelector('[data-collection]').onchange=e=>change({collection:e.target.value,subject:''});
   host.querySelectorAll('[data-filter]').forEach(button=>{button.onclick=()=>change({collection:button.dataset.filter,subject:''});if(canEdit){button.ondragover=e=>{if(e.dataTransfer?.types?.includes('Files'))e.preventDefault();};button.ondrop=async e=>{e.preventDefault();e.stopPropagation();await upload(await studioDroppedPhotos(e.dataTransfer),button.dataset.filter||'Unclassified');};}});
   const clear=host.querySelector('[data-clear-subject]');if(clear)clear.onclick=()=>change({subject:''});
   host.querySelector('[data-media-search]').oninput=e=>{search=e.target.value;renderTiles();};
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
   if(selected)select(selected);else{selectedId='';selectedHost.innerHTML='<p class="jd-empty">Select a photo in the gallery to inspect it.</p>';}
  }
  async function load(){
   const version=++request;
   host.setAttribute('aria-busy','true');
   try{
    const q=new URLSearchParams({limit:'12'});for(const [name,value] of Object.entries({collection,subjectId:subject,assetId,after}))if(value)q.set(name,value);
    const [snapshot,access]=await Promise.all([json(`/api/jobs/${job.id}/media?${q}`),json(`/api/jobs/${job.id}/access`)]);
    if(!current()||version!==request)return;
    data=snapshot;canEdit=!!(access.permissions||access).manageProduction;draw();
    const counts=Object.values(data.collectionCounts||{});onCounts(counts.length?counts.reduce((a,b)=>a+Number(b),0):data.totalAssets??data.assets.length,data.totalSubjects??data.subjects.length,access.permissions||access,data.subjects,subject);
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
   const dialog=studioDialog('Import subject roster','<form class="subject-form"><label>Roster file<input type="file" name="roster" accept=".csv,.xlsx" required></label><label>Name column<input name="nameColumn" value="Name" required></label><label>Subject ID column<input name="idColumn" value="Subject ID" required></label><button type="submit" class="primary">Import</button><p role="alert"></p></form>');
   dialog.querySelector('form').onsubmit=async event=>{event.preventDefault();const f=event.currentTarget,button=f.querySelector('button');button.disabled=true;try{const body=new FormData();body.append('file',f.elements.roster.files[0]);body.append('nameColumn',f.elements.nameColumn.value);body.append('idColumn',f.elements.idColumn.value);await json(`/api/jobs/${job.id}/media/roster-file`,{method:'POST',body});dialog.close();await load();}catch(error){if(f.isConnected)f.querySelector('[role=alert]').textContent=error.message;}finally{if(button.isConnected)button.disabled=false;}};
  }
  const controller={reload:()=>{const focus=root.studioMediaFocus?.jobId===job.id?root.studioMediaFocus:root.studioMediaPage?.[job.id];if(focus?.subjectId!==undefined){subject=focus.subjectId||'';root.studioMediaFocus=null;after='';page=1;previous.length=0;}return load();},upload:()=>host.querySelector('[data-add]')?.click(),importRoster,dispose:()=>{disposed=true;request++;host.ondrop=null;host.ondragover=null;}};
  host.studioJobMedia?.dispose();host.studioJobMedia=controller;
  await load();return controller;
 }
 root.StudioJobDetails={workflow,phaseIndex,media};
})(window);


