/* Job Details components own their DOM and API requests. Production keeps its own gallery. */
(function(root){
 'use strict';
 const escape=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const put=(url,body)=>json(url,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
 function workflowAction(view){
  const tasks=new Map((view?.tasks||[]).map(task=>[task.nodeId,task]));
  const states=new Map((view?.evaluation?.nodes||[]).map(node=>[node.id,node]));
  const order=(view?.graph?.nodes||[]).filter(node=>node.kind==='Task').map(node=>node.id);
  const open=order.map(id=>({id,task:tasks.get(id),status:states.get(id)?.status||'Unknown'})).filter(x=>x.task&&!['Completed','Skipped','Inactive','Cancelled'].includes(x.status));
  const current=open.find(x=>x.status==='InProgress')||open.find(x=>x.status==='Available')||open[0]||null;
  return current?{nodeId:current.id,task:current.task,status:current.status}:null;
 }
 function nextActionLink(host,view,activate){
  if(!host)return;
  const current=workflowAction(view),task=current?.task;
  host.innerHTML=current?'<button type="button" class="jd-link jd-next-action-link" data-next-action>'+escape(task.description||task.title)+' <span aria-hidden="true">→</span></button>':'';
  host.querySelector('[data-next-action]')?.addEventListener('click',()=>activate(current.nodeId));
 }
 async function media(job,host,selectedHost,isCurrent,onCounts){
  let disposed=false,request=0,selectedId='',data=null,canEdit=false,uploading=false,search='',list=false;
  let pendingBatch=null,photoDialog=null;
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
   photoDialog?.close();
   const dialog=studioDialog(asset.fileName,'<section class="job-detail-selected-file jd-photo-dialog"><header class="jd-card-heading"></header><div class="job-detail-selected-content"></div></section>');
   photoDialog=dialog;dialog.classList.add('jd-photo-viewer');
   const content=dialog.querySelector('.job-detail-selected-content'),abort=new AbortController();
   let items=[],cursor='',more=true,index=-1,busy=true,shown=asset,error='';
   const live=()=>current()&&dialog.isConnected;
   const update=()=>{
    if(!live())return;
    content.querySelector('.jd-photo-inspector').inert=busy;
    content.querySelector('[data-photo-previous]').disabled=busy||index<=0;
    content.querySelector('[data-photo-next]').disabled=busy||index<0||(!more&&index>=items.length-1);
    content.querySelector('[data-photo-position]').textContent=error||(busy?'Loading photos…':index<0?'Photo unavailable in this job':`Photo ${index+1}${more?'':' of '+items.length}`);
    content.querySelector('[data-photo-retry]').hidden=!error;
   };
   const show=photo=>{
    shown=photo;select(photo,content);
    dialog.querySelector('.studio-dialog-heading h2').textContent=photo.fileName;
    update();
   };
   async function readPage(){
    const q=new URLSearchParams({limit:'100'});if(cursor)q.set('after',cursor);
    const snapshot=await json(`/api/jobs/${job.id}/media?${q}`,{signal:abort.signal});
    if(!live())return;
    const batch=snapshot.assets||[],next=batch.at(-1)?.id;
    if(snapshot.hasMore&&(!next||next===cursor))throw Error('Photo navigation could not advance. Please retry.');
    const known=new Set(items.map(photo=>photo.id));items.push(...batch.filter(photo=>isPreviewable(photo)&&!known.has(photo.id)));
    cursor=next||cursor;more=!!snapshot.hasMore;
   }
   async function initialize(){
    if(!live())return;
    busy=true;error='';update();
    try{
     while(live()&&index<0&&more){await readPage();index=items.findIndex(photo=>photo.id===shown.id);}
     if(index>=0)items[index]=shown;
    }catch(e){if(live())error='Unable to load photos: '+e.message;}
    finally{busy=false;update();}
   }
   async function move(direction){
    if(busy||index<0||(direction<0&&index===0)||(direction>0&&!more&&index===items.length-1))return;
    if(content.dataset.dirty==='true'&&!confirm('Discard unsaved file details and view another photo?'))return;
    busy=true;error='';update();
    try{
     let next=index+direction;
     while(live()&&next>=items.length&&more)await readPage();
     if(live()&&items[next]){index=next;show(items[index]);}
    }catch(e){if(live())error='Unable to load photos: '+e.message;}
    finally{busy=false;update();if(live()){const button=content.querySelector(direction<0?'[data-photo-previous]':'[data-photo-next]');(button.disabled?content.querySelector('.jd-selected-preview'):button).focus();}}
   }
   dialog.addEventListener('click',event=>{
    if(event.target.closest('[data-photo-previous]'))void move(-1);
    if(event.target.closest('[data-photo-next]'))void move(1);
    if(event.target.closest('[data-photo-retry]')){if(index<0)void initialize();else void move(1);}
   });
   dialog.addEventListener('keydown',event=>{
    if(event.target.closest('input,textarea,select,[contenteditable="true"]'))return;
    if(event.key==='ArrowLeft'||event.key==='ArrowRight'){event.preventDefault();void move(event.key==='ArrowLeft'?-1:1);}
   });
   dialog.addEventListener('close',()=>{abort.abort();if(photoDialog===dialog)photoDialog=null;},{once:true});
   show(asset);void initialize();
  }
  async function versions(asset){
   const dialog=studioDialog('Versions & history','<p role="status">Loading versions…</p>'),body=dialog.querySelector('.studio-dialog-body');
   try{const history=await json(`/api/jobs/${job.id}/versions/${encodeURIComponent(asset.assetKey)}`);if(!dialog.isConnected)return;body.innerHTML=history.map(version=>`<article class="task-card"><a href="/api/jobs/${job.id}/versions/by-id/${version.id}/content">${escape(version.fileName)} · v${escape(version.versionNumber)}</a><small>${escape(studioFirstName(version.createdBy))} · ${escape(new Date(version.createdAtUtc).toLocaleString())}</small><p>${escape(version.note||'')}</p></article>`).join('')||'<p>No version history available.</p>';}
   catch(error){if(dialog.isConnected)body.textContent=error.message;}
  }
  function select(asset,target){
   if(!target){selectedId=asset.id;host.querySelectorAll('[data-jd-asset]').forEach(tile=>{const active=tile.dataset.jdAsset===asset.id;tile.classList.toggle('selected',active);tile.setAttribute('aria-pressed',String(active));});return;}
   const selectedHost=target;selectedHost.dataset.dirty='false';
   selectedId=asset.id;
   host.querySelectorAll('[data-jd-asset]').forEach(tile=>{const active=tile.dataset.jdAsset===asset.id;tile.classList.toggle('selected',active);tile.setAttribute('aria-pressed',String(active));});
   const collections=[...new Set(['Unclassified',...(data.collections||[]),asset.collection||'Unclassified'])];
   const heading=selectedHost.closest('.job-detail-selected-file').querySelector('.jd-card-heading');
   heading.innerHTML=`<p class="jd-selected-file-meta">${escape((asset.contentType||'File').replace('image/','').toUpperCase())} · ${escape(formatBytes(asset.sizeBytes||0))} · v${escape(asset.version||1)}</p><span class="jd-preview-mode">Full image · Fit to window</span>`;
   let tags=[...new Set(asset.tags||[])];
   const tagInputId='jd-photo-tags-'+asset.id;
   selectedHost.innerHTML=`<figure class="jd-selected-preview" tabindex="0" aria-label="Full photograph"><button type="button" class="jd-photo-arrow" data-photo-previous aria-label="Previous photo" title="Previous photo (Left arrow)">‹</button><div class="jd-preview-image">${picture(asset,true)}</div><button type="button" class="jd-photo-arrow" data-photo-next aria-label="Next photo" title="Next photo (Right arrow)">›</button><figcaption class="jd-photo-navigation"><span data-photo-position role="status" aria-live="polite"></span><button type="button" data-photo-retry hidden>Retry</button></figcaption></figure><div class="jd-photo-inspector"><form class="jd-file-form"><h3>File details</h3><div class="jd-file-fields"><label>Collection<select name="collection" ${canEdit?'':'disabled'}>${collections.map(name=>`<option ${name===(asset.collection||'Unclassified')?'selected':''}>${escape(name)}</option>`).join('')}</select></label><label>Subjects<select data-subject-picker name="subjects" multiple size="5" ${canEdit?'':'disabled'} aria-label="File subjects">${StudioSubjectPicker.options(data.subjects,asset.subjects||[])}</select></label></div><label for="${escape(tagInputId)}">Tags</label><div class="jd-tag-editor"><div class="jd-tag-tokens" role="list" aria-label="Photo tags"></div><input id="${escape(tagInputId)}" type="text" data-tag-input placeholder="Add tags; Enter or comma" aria-label="Add photo tags" ${canEdit?'':'disabled'}></div>${canEdit?'<button type="submit" class="jd-save">Save file details</button>':''}<p role="alert"></p></form><div class="jd-file-links"><a href="/api/jobs/${job.id}/versions/by-id/${asset.versionId}/content" download="${escape(asset.fileName)}">Download original</a><button type="button" data-versions>Version history</button>${canEdit?'<button type="button" data-retry-preview>Regenerate preview</button>':''}</div></div>`;
   bindImages(selectedHost);
   selectedHost.querySelector('[data-versions]').onclick=()=>versions(asset);
   const form=selectedHost.querySelector('form');
   form.addEventListener('input',()=>selectedHost.dataset.dirty='true');form.addEventListener('change',()=>selectedHost.dataset.dirty='true');
   const tokens=form.querySelector('.jd-tag-tokens'),tagInput=form.querySelector('[data-tag-input]');
   const renderTags=()=>{
    tokens.innerHTML=tags.map((tag,index)=>`<span class="jd-tag-token" role="listitem">${escape(tag)}${canEdit?`<button type="button" data-remove-tag="${index}" aria-label="Remove tag ${escape(tag)}">×</button>`:''}</span>`).join('');
    tokens.querySelectorAll('[data-remove-tag]').forEach(button=>button.onclick=()=>{tags.splice(Number(button.dataset.removeTag),1);selectedHost.dataset.dirty='true';renderTags();tagInput.focus();});
   };
   const addTags=()=>{
    if(!canEdit)return;
    if(!tagInput.value.trim())return true;
    const additions=tagInput.value.split(/[,\n]/).map(value=>value.trim()).filter(Boolean),next=[...tags];
    for(const tag of additions)if(!next.some(value=>value.toLowerCase()===tag.toLowerCase()))next.push(tag);
    if(next.length>50||next.some(value=>value.length>80)){form.querySelector('[role=alert]').textContent='Use at most 50 tags, with up to 80 characters per tag.';return false;}
    tags=next;selectedHost.dataset.dirty='true';tagInput.value='';form.querySelector('[role=alert]').textContent='';renderTags();return true;
   };
   renderTags();tagInput.onkeydown=event=>{if(event.key==='Enter'||event.key===','){event.preventDefault();addTags();}};

   form.onsubmit=async event=>{
    event.preventDefault();if(!canEdit||addTags()===false)return;const button=form.querySelector('[type=submit]');button.disabled=true;
    try{const changes={status:asset.status,collection:form.elements.collection.value,subjects:Array.from(form.elements.subjects.selectedOptions,o=>o.value),assignedTo:asset.assignedTo||null,captureAtUtc:asset.captureAtUtc||null,tags};await put(`/api/jobs/${job.id}/media/assets/${asset.id}`,changes);Object.assign(asset,changes);selectedHost.dataset.dirty='false';await load();}
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
   grid.querySelectorAll('[data-jd-asset]').forEach(tile=>{const asset=data.assets.find(a=>a.id===tile.dataset.jdAsset);tile.onclick=()=>select(asset);tile.ondblclick=()=>openPreview(asset);tile.onkeydown=event=>{if(event.key==='Enter'){event.preventDefault();openPreview(asset);}};});
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
   if(selected)select(selected);else selectedId='';
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
    const view=await json(`/api/jobs/${job.id}/shoot-sessions`),sessions=view||[];
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

  const controller={reload:()=>{const focus=root.studioMediaFocus?.jobId===job.id?root.studioMediaFocus:root.studioMediaPage?.[job.id];if(focus?.subjectId!==undefined){subject=focus.subjectId||'';root.studioMediaFocus=null;after='';page=1;previous.length=0;}return load();},upload:()=>host.querySelector('[data-add]')?.click(),importRoster,dispose:()=>{disposed=true;request++;photoDialog?.close();host.ondrop=null;host.ondragover=null;}};
  host.studioJobMedia?.dispose();host.studioJobMedia=controller;
  await load();return controller;
 }
 root.StudioJobDetails={media,nextActionLink,workflowAction};
})(window);


