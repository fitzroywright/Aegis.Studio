/* Job Details components own their DOM and API requests. Production keeps its own gallery. */
(function(root){
 'use strict';
 const escape=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const put=(url,body)=>json(url,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
 const phases=['Booking','Assigned','Shoot','Production','Review','Delivery'];
 function phaseIndex(data){
  const stage=data.stage;
  if(stage==='Changes Requested')return data.correctionKind==='Production'?3:2;
  return ({Pending:0,'Needs Assignment':1,'Needs Photographer Assignment':1,'Awaiting Scheduling':2,Scheduled:2,'Photography In Progress':2,'Awaiting Upload':2,'Awaiting Production Review':3,'Active Production':3,'Awaiting Approval':4,'Ready for Delivery':5,Delivered:5,Completed:6})[stage]??-1;
 }
 function workflow(host,data,job={}){
  const active=phaseIndex(data);
  const noShoot=/print|edit/i.test(job.jobType||job.type||'')&&!(data.sessions||[]).length;
  host.innerHTML=phases.map((label,index)=>`<div class="jd-stage ${index===2&&noShoot?'skipped':active<0?'':index<active?'complete':index===active?'current':''}"><span class="jd-stage-light" aria-hidden="true">${index===2&&noShoot?'–':active>=0&&index<active?'✓':''}</span><span>${index===2&&noShoot?'No shoot':label}</span></div>`).join('');
  host.setAttribute('aria-label','Job workflow: '+data.stage);
 }
 async function media(job,host,selectedHost,isCurrent,onCounts){
  let disposed=false,request=0,selectedId='',data=null,canEdit=false,uploading=false,search='',list=false;
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
    const counts=Object.values(data.collectionCounts||{});onCounts(counts.length?counts.reduce((a,b)=>a+Number(b),0):data.totalAssets??data.assets.length,data.totalSubjects??data.subjects.length,access.permissions||access);
   }catch(error){if(current()&&version===request){host.innerHTML='<p role="alert"></p><button type="button" class="jd-save">Retry</button>';host.querySelector('p').textContent=error.message;host.querySelector('button').onclick=load;}}
   finally{if(current()&&version===request)host.removeAttribute('aria-busy');}
  }
  async function upload(files,target=collection||data?.gallery?.default_collection||'Unclassified'){
   if(!canEdit||uploading||!files.length)return;uploading=true;const failed=[];let completed=0;
   try{
    for(const file of files){if(!current())break;announce(`Uploading ${completed+failed.length+1}/${files.length}: ${file.name}`);
     try{const form=new FormData();form.append('file',file);form.append('collection',target);form.append('returned','false');const saved=await json(`/api/jobs/${job.id}/media/uploads`,{method:'POST',body:form});if(subject&&saved.assetId)await put(`/api/jobs/${job.id}/media/assets/${saved.assetId}`,{status:'Needs Sorting',collection:target,subjects:[subject],assignedTo:null});completed++;}
     catch(error){failed.push({file,error:error.message});}
    }
    if(current()){await load();announce(`${completed} files uploaded.${failed.length?' Failed: '+failed.map(f=>f.file.name+' ('+f.error+')').join('; '):''}`);}
   }finally{uploading=false;}
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
