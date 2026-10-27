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
 const photoPreview=(job,asset)=>`/api/jobs/${job.id}/versions/by-id/${asset.versionId}/preview`;
  function bindImages(container){
   container.querySelectorAll('img').forEach(img=>img.onerror=()=>{img.hidden=true;const fallback=img.nextElementSibling;if(fallback)fallback.hidden=false;});
  }
  const isPreviewable=asset=>String(asset.contentType||asset.mimeType||'').startsWith('image/')||/\.(3fr|arw|cr2|cr3|crw|dng|erf|fff|iiq|kdc|mos|mrw|nef|nrw|orf|pef|raf|raw|rw2|rwl|sr2|srf|srw|x3f)$/i.test(String(asset.fileName||asset.name||''));
  const photoPicture=(job,asset,large=false)=>isPreviewable(asset)?`<img ${large?'':'loading="lazy"'} src="${photoPreview(job,asset)}${large?'':'?size=thumbnail'}" alt="${escape(asset.fileName)}"><span class="jd-preview-fallback" hidden>Preview ${escape(asset.previewStatus||'unavailable')}<br>Original file is retained</span>`:'<span class="jd-preview-fallback">▤ Document</span>';
  function createGallery(job,{asset=null,data,canEdit,current,onSelect=()=>{},onSaved=async()=>{},favourites=new Set()}){
   if(asset&&!isPreviewable(asset))return;
   const dialog=studioDialog(asset?.fileName||job.title,`<section class="job-detail-selected-file jd-photo-dialog"><div class="jd-gallery-topline"><div class="jd-gallery-intro"><p>${escape(job.clientName||'')}${job.eventDate?' · '+escape(new Date(String(job.eventDate).includes('T')?job.eventDate:job.eventDate+'T12:00:00').toLocaleDateString(undefined,{day:'numeric',month:'long',year:'numeric'})):''}</p></div><div class="jd-gallery-actions"><button type="button" data-photo-grid>← Gallery</button><button type="button" data-gallery-favourites aria-pressed="false" title="Favourites for this visit">♡ Favourites</button>${canEdit?'<button type="button" data-gallery-select aria-pressed="false">Select photos</button>':''}<button type="button" data-photo-favourite aria-pressed="false" title="Favourite for this visit">♡ Favourite</button><a data-photo-download download aria-label="Download original">↓ Download</a><button type="button" data-photo-details aria-expanded="false">Details</button><button type="button" data-gallery-expand aria-label="Toggle fullscreen" aria-pressed="false">⛶</button></div></div><div class="jd-gallery-browser"><nav class="jd-gallery-tabs" aria-label="Gallery collections"></nav><div class="jd-gallery-mosaic" aria-label="Job photographs"></div></div><header class="jd-card-heading" hidden></header><div class="job-detail-selected-content" hidden></div><nav class="jd-photo-filmstrip" aria-label="Photo thumbnails" hidden></nav><footer class="jd-popup-footer"><span data-gallery-status role="status" aria-live="polite"></span><span data-gallery-selected></span><button type="button" data-gallery-more>Load more</button><button type="button" data-gallery-retry hidden>Retry</button>${canEdit?'<button type="button" data-gallery-download disabled>Download selected</button>':''}</footer></section>`);
   dialog.classList.add('jd-photo-viewer');
   dialog.setAttribute('aria-label',job.title+' gallery');
   const picture=(photo,large=false)=>photoPicture(job,photo,large);
   const content=dialog.querySelector('.job-detail-selected-content'),abort=new AbortController(),chosen=new Set();
   let items=[],cursor='',more=true,index=-1,busy=true,shown=asset,error='',mode=asset?'photo':'grid',activeCollection='',favouritesOnly=false,selecting=false,details=false,exporting=false;
   const live=()=>current()&&dialog.isConnected;
   const q=selector=>dialog.querySelector(selector);
   const discard=()=>content.dataset.dirty!=='true'||confirm('Discard unsaved file details and view another photo?');
   function renderGrid(){
    if(!live())return;
    const collections=[...new Set(items.map(photo=>photo.collection||'Unclassified'))];
    q('.jd-gallery-tabs').innerHTML=['',...collections].map(name=>`<button type="button" data-gallery-collection="${escape(name)}" aria-pressed="${name===activeCollection}">${escape(name||'All photographs')} <span>${items.filter(photo=>!name||(photo.collection||'Unclassified')===name).length}${more?'+':''}</span></button>`).join('');
    const visible=items.filter(photo=>(!activeCollection||(photo.collection||'Unclassified')===activeCollection)&&(!favouritesOnly||favourites.has(photo.id)));
    const grid=q('.jd-gallery-mosaic'),scroller=q('.jd-gallery-browser'),scroll=scroller.scrollTop;
    grid.innerHTML=visible.map(photo=>`<button type="button" class="jd-gallery-tile" data-gallery-photo="${escape(photo.id)}" aria-label="${escape(photo.fileName)}" aria-pressed="${chosen.has(photo.id)}">${picture(photo)}<span class="jd-gallery-tile-caption">${escape(photo.fileName)}</span><span class="jd-gallery-tick" ${chosen.has(photo.id)?'':'hidden'}>✓</span></button>`).join('')||`<p class="jd-empty">${busy?'Loading photographs…':favouritesOnly?'No favourites in this collection. Open a photograph and choose Favourite.':'No photographs in this collection.'}</p>`;
    scroller.scrollTop=scroll;bindImages(grid);
   }
   function renderFilmstrip(){
    q('.jd-photo-filmstrip').innerHTML=items.map(photo=>`<button type="button" data-gallery-photo="${escape(photo.id)}" aria-label="View ${escape(photo.fileName)}" aria-current="${photo.id===shown?.id}">${picture(photo)}</button>`).join('');
    bindImages(q('.jd-photo-filmstrip'));
    q('.jd-photo-filmstrip [aria-current="true"]')?.scrollIntoView({block:'nearest',inline:'nearest'});
   }
   const update=()=>{
    if(!live())return;
    dialog.dataset.galleryMode=mode;dialog.dataset.details=String(details);
    q('.jd-gallery-browser').hidden=mode!=='grid';content.hidden=mode!=='photo';
    q('.jd-card-heading').hidden=mode!=='photo';q('.jd-photo-filmstrip').hidden=mode!=='photo';
    q('.jd-gallery-intro').hidden=mode!=='grid';
    for(const selector of ['[data-photo-grid]','[data-photo-favourite]','[data-photo-download]','[data-photo-details]'])q(selector).hidden=mode!=='photo';
    for(const selector of ['[data-gallery-favourites]','[data-gallery-select]']){const el=q(selector);if(el)el.hidden=mode!=='grid';}
    q('[data-photo-details]').setAttribute('aria-expanded',String(details));
    q('.jd-photo-inspector')?.toggleAttribute('hidden',!details);
    q('.studio-dialog-heading h2').textContent=mode==='grid'?job.title:shown?.fileName||job.title;
    q('[data-gallery-favourites]').setAttribute('aria-pressed',String(favouritesOnly));
    q('[data-gallery-select]')?.setAttribute('aria-pressed',String(selecting));
    q('[data-gallery-selected]').textContent=chosen.size?`${chosen.size} selected`:'';
    q('[data-gallery-status]').textContent=error||(busy?'Loading photographs…':`${items.length}${more?'+':''} photographs${favouritesOnly?' · Favourites for this visit':''}`);
    q('[data-gallery-more]').hidden=!more||mode!=='grid';q('[data-gallery-more]').disabled=busy;
    q('[data-gallery-retry]').hidden=!error;
    const download=q('[data-gallery-download]');if(download){download.hidden=mode!=='grid';download.disabled=busy||exporting||!chosen.size;download.textContent=exporting?'Preparing download…':'Download selected';}
    if(mode==='photo'&&shown){
     q('.jd-photo-inspector').inert=busy;
     q('[data-photo-previous]').disabled=busy||index<=0;
     q('[data-photo-next]').disabled=busy||index<0||(!more&&index>=items.length-1);
     q('[data-photo-position]').textContent=error||(busy?'Loading photos…':index<0?'Photo unavailable in this job':`${shown.fileName} · Photo ${index+1}${more?'':' of '+items.length}`);
     q('[data-photo-retry]').hidden=!error;
     q('[data-photo-download]').href=`/api/jobs/${job.id}/versions/by-id/${shown.versionId}/content`;
     q('[data-photo-download]').download=shown.fileName;
     q('[data-photo-favourite]').setAttribute('aria-pressed',String(favourites.has(shown.id)));
     q('[data-photo-favourite]').textContent=favourites.has(shown.id)?'♥ Favourite':'♡ Favourite';
    }
   };
   const show=photo=>{shown=photo;mode='photo';onSelect(photo);renderPhotoDetails(job,photo,content,{data,canEdit,onSaved});renderFilmstrip();update();};
   async function readPage(){
    const params=new URLSearchParams({limit:'100'});if(cursor)params.set('after',cursor);
    const snapshot=await json(`/api/jobs/${job.id}/media?${params}`,{signal:abort.signal});
    if(!live())return;
    const batch=snapshot.assets||[],next=batch.at(-1)?.id;
    if(snapshot.hasMore&&(!next||next===cursor))throw Error('Photo navigation could not advance. Please retry.');
    const known=new Set(items.map(photo=>photo.id));items.push(...batch.filter(photo=>isPreviewable(photo)&&!known.has(photo.id)));
    cursor=next||cursor;more=!!snapshot.hasMore;
   }
   async function initialize(){
    if(!live()||busy&&items.length)return;
    busy=true;error='';update();
    try{
     do{await readPage();index=items.findIndex(photo=>photo.id===shown?.id);}while(live()&&shown&&index<0&&more);
     if(index>=0)items[index]=shown;
    }catch(e){if(live())error='Unable to load photos: '+e.message;}
    finally{busy=false;if(live()){renderGrid();renderFilmstrip();update();}}
   }
   async function move(direction){
    if(busy||index<0||(direction<0&&index===0)||(direction>0&&!more&&index===items.length-1)||!discard())return;
    busy=true;error='';update();
    try{
     const next=index+direction;
     while(live()&&next>=items.length&&more)await readPage();
     if(live()&&items[next]){index=next;show(items[index]);}
    }catch(e){if(live())error='Unable to load photos: '+e.message;}
    finally{busy=false;update();if(live()){const button=q(direction<0?'[data-photo-previous]':'[data-photo-next]');(button.disabled?q('.jd-selected-preview'):button).focus();}}
   }
   dialog.addEventListener('click',async event=>{
    const button=event.target.closest('button');if(!button)return;
    if(button.matches('[data-photo-previous]'))void move(-1);
    if(button.matches('[data-photo-next]'))void move(1);
    if(button.matches('[data-photo-retry],[data-gallery-retry]')){if(mode==='grid'||index<0)void initialize();else void move(1);}
    if(button.matches('[data-gallery-more]')&&!busy)void initialize();
    if(button.matches('[data-photo-grid]')&&discard()){content.dataset.dirty='false';mode='grid';renderGrid();update();q('.jd-gallery-tabs button')?.focus();}
    if(button.matches('[data-photo-details]')){details=!details;update();}
    if(button.matches('[data-gallery-favourites]')){favouritesOnly=!favouritesOnly;renderGrid();update();}
    if(button.matches('[data-photo-favourite]')&&shown){if(favourites.has(shown.id))favourites.delete(shown.id);else favourites.add(shown.id);update();}
    if(button.matches('[data-gallery-select]')){selecting=!selecting;update();}
    if(button.matches('[data-gallery-collection]')){activeCollection=button.dataset.galleryCollection;renderGrid();update();}
    if(button.matches('[data-gallery-photo]')&&!busy){
     const photo=items.find(item=>item.id===button.dataset.galleryPhoto);if(!photo)return;
     if(mode==='grid'&&selecting){if(chosen.has(photo.id))chosen.delete(photo.id);else chosen.add(photo.id);button.setAttribute('aria-pressed',String(chosen.has(photo.id)));button.querySelector('.jd-gallery-tick').hidden=!chosen.has(photo.id);update();}
     else if(discard()){index=items.indexOf(photo);show(photo);q('.jd-selected-preview').focus();}
    }
    if(button.matches('[data-gallery-expand]')){
     try{if(document.fullscreenElement===dialog)await document.exitFullscreen();else await dialog.requestFullscreen();}catch{error='Fullscreen is unavailable in this browser.';update();}
    }
    if(button.matches('[data-gallery-download]')&&canEdit&&chosen.size&&!exporting){
     exporting=true;error='';update();
     try{
      const response=await fetch(`/api/jobs/${job.id}/media/export`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({assets:[...chosen]}),signal:abort.signal});
      if(!response.ok)throw Error(await response.text());
      const blob=await response.blob();if(!live())return;
      const url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download='studio-gallery-selection.zip';link.click();setTimeout(()=>URL.revokeObjectURL(url),60000);
     }catch(e){if(live())error='Download failed: '+e.message;}
     finally{exporting=false;update();}
    }
   });
   const fullscreen=()=>{if(live())q('[data-gallery-expand]').setAttribute('aria-pressed',String(document.fullscreenElement===dialog));};
   document.addEventListener('fullscreenchange',fullscreen);
   const routeObserver=new MutationObserver(()=>{if(dialog.isConnected&&!current())dialog.close();});
   routeObserver.observe(document.body,{attributes:true,attributeFilter:['data-studio-view'],childList:true,subtree:true});
   dialog.addEventListener('keydown',event=>{
    if(mode!=='photo'||event.target.closest('input,textarea,select,[contenteditable="true"]'))return;
    if(event.key==='ArrowLeft'||event.key==='ArrowRight'){event.preventDefault();void move(event.key==='ArrowLeft'?-1:1);}
   });
   q('[data-close]').onclick=()=>{if(discard())dialog.close();};
   dialog.addEventListener('cancel',event=>{if(!discard())event.preventDefault();});
   dialog.addEventListener('close',()=>{abort.abort();routeObserver.disconnect();document.removeEventListener('fullscreenchange',fullscreen);},{once:true});
   if(asset)show(asset);else{renderGrid();update();}void initialize();return dialog;
  }
  async function versions(job,asset){
   const dialog=studioDialog('Versions & history','<p role="status">Loading versions…</p>'),body=dialog.querySelector('.studio-dialog-body');
   try{const history=await json(`/api/jobs/${job.id}/versions/${encodeURIComponent(asset.assetKey)}`);if(!dialog.isConnected)return;body.innerHTML=history.map(version=>`<article class="task-card"><a href="/api/jobs/${job.id}/versions/by-id/${version.id}/content">${escape(version.fileName)} · v${escape(version.versionNumber)}</a><small>${escape(studioFirstName(version.createdBy))} · ${escape(new Date(version.createdAtUtc).toLocaleString())}</small><p>${escape(version.note||'')}</p></article>`).join('')||'<p>No version history available.</p>';}
   catch(error){if(dialog.isConnected)body.textContent=error.message;}
  }
  function renderPhotoDetails(job,asset,selectedHost,{data,canEdit,onSaved}){
   selectedHost.dataset.dirty='false';
   const picture=(photo,large=false)=>photoPicture(job,photo,large);
   const collections=[...new Set(['Unclassified',...(data.collections||[]),asset.collection||'Unclassified'])];
   const heading=selectedHost.closest('.job-detail-selected-file').querySelector('.jd-card-heading');
   heading.innerHTML=`<p class="jd-selected-file-meta">${escape((asset.contentType||'File').replace('image/','').toUpperCase())} · ${escape(formatBytes(asset.sizeBytes||0))} · v${escape(asset.version||1)}</p><span class="jd-preview-mode">Full image · Fit to window</span>`;
   let tags=[...new Set(asset.tags||[])];
   const tagInputId='jd-photo-tags-'+asset.id;
   selectedHost.innerHTML=`<figure class="jd-selected-preview" tabindex="0" aria-label="Full photograph"><button type="button" class="jd-photo-arrow" data-photo-previous aria-label="Previous photo" title="Previous photo (Left arrow)">‹</button><div class="jd-preview-image">${picture(asset,true)}</div><button type="button" class="jd-photo-arrow" data-photo-next aria-label="Next photo" title="Next photo (Right arrow)">›</button><figcaption class="jd-photo-navigation"><span data-photo-position role="status" aria-live="polite"></span><button type="button" data-photo-retry hidden>Retry</button></figcaption></figure><div class="jd-photo-inspector"><form class="jd-file-form"><h3>File details</h3><div class="jd-file-fields"><label>Collection<select name="collection" ${canEdit?'':'disabled'}>${collections.map(name=>`<option ${name===(asset.collection||'Unclassified')?'selected':''}>${escape(name)}</option>`).join('')}</select></label><label>Subjects<select data-subject-picker name="subjects" multiple size="5" ${canEdit?'':'disabled'} aria-label="File subjects">${StudioSubjectPicker.options(data.subjects||[],asset.subjects||[])}</select></label></div><label for="${escape(tagInputId)}">Tags</label><div class="jd-tag-editor"><div class="jd-tag-tokens" role="list" aria-label="Photo tags"></div><input id="${escape(tagInputId)}" type="text" data-tag-input placeholder="Add tags; Enter or comma" aria-label="Add photo tags" ${canEdit?'':'disabled'}></div>${canEdit?'<button type="submit" class="jd-save">Save file details</button>':''}<p role="alert"></p></form><div class="jd-file-links"><a href="/api/jobs/${job.id}/versions/by-id/${asset.versionId}/content" download="${escape(asset.fileName)}">Download original</a><button type="button" data-versions>Version history</button>${canEdit?'<button type="button" data-retry-preview>Regenerate preview</button>':''}</div></div>`;
   bindImages(selectedHost);
   selectedHost.querySelector('[data-versions]').onclick=()=>versions(job,asset);
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
    try{const changes={status:asset.status,collection:form.elements.collection.value,subjects:Array.from(form.elements.subjects.selectedOptions,o=>o.value),assignedTo:asset.assignedTo||null,captureAtUtc:asset.captureAtUtc||null,tags};await put(`/api/jobs/${job.id}/media/assets/${asset.id}`,changes);Object.assign(asset,changes);selectedHost.dataset.dirty='false';await onSaved();}
    catch(error){if(form.isConnected)form.querySelector('[role=alert]').textContent=error.message;}
    finally{if(button.isConnected)button.disabled=false;}
   };
   const retry=selectedHost.querySelector('[data-retry-preview]');if(retry)retry.onclick=async()=>{retry.disabled=true;try{await json(`/api/jobs/${job.id}/versions/by-id/${asset.versionId}/preview/retry`,{method:'POST'});if(retry.isConnected)retry.textContent='Preview queued';}catch(error){if(form.isConnected)form.querySelector('[role=alert]').textContent=error.message;retry.disabled=false;}};
  }
 let standaloneGallery=null,galleryRequest=0;
 async function openGallery(job,{data,current=()=>true}={}){
  const request=++galleryRequest;standaloneGallery?.close();
  const [snapshot,access]=await Promise.all([data||json(`/api/jobs/${job.id}/media?limit=12`),json(`/api/jobs/${job.id}/access`)]);
  if(!current()||request!==galleryRequest)return null;
  const dialog=createGallery(job,{data:snapshot,canEdit:!!(access.permissions||access).manageProduction,current});
  standaloneGallery=dialog;dialog.addEventListener('close',()=>{if(standaloneGallery===dialog)standaloneGallery=null;},{once:true});return dialog;
 }
 async function media(job,host,selectedHost,isCurrent,onCounts){
  let disposed=false,request=0,selectedId='',data=null,canEdit=false,uploading=false,search='',list=false;
  let pendingBatch=null,photoDialog=null;const favourites=new Set();
  let collection='',subject='',assetId='',after='',page=1;const previous=[];
  const focus=root.studioMediaFocus?.jobId===job.id?root.studioMediaFocus:root.studioMediaPage?.[job.id];
  if(focus){subject=focus.subjectId||'';assetId=focus.assetId||'';root.studioMediaFocus=null;}
  const current=()=>!disposed&&isCurrent()&&host.isConnected;
  const announce=message=>{if(!current())return;let output=host.querySelector('[data-media-result]');if(!output){output=document.createElement('p');output.className='jd-media-result';output.dataset.mediaResult='';output.setAttribute('role','status');host.append(output);}output.textContent=message;};
  const picture=(asset,large=false)=>photoPicture(job,asset,large);
  function select(asset){
   selectedId=asset.id;
   host.querySelectorAll('[data-jd-asset]').forEach(tile=>{const active=tile.dataset.jdAsset===asset.id;tile.classList.toggle('selected',active);tile.setAttribute('aria-pressed',String(active));});
  }
  function openPreview(asset=null){
   photoDialog?.close();
   photoDialog=createGallery(job,{asset,data,canEdit,current,onSelect:select,onSaved:load,favourites});
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
   host.innerHTML=`<header class="jd-gallery-toolbar"><h2>All Media</h2><button type="button" class="jd-tool-button" data-open-gallery aria-haspopup="dialog">Gallery</button><select data-collection aria-label="Filter media by collection"><option value="">All collections</option>${collections.map(name=>`<option ${name===collection?'selected':''}>${escape(name)}</option>`).join('')}</select><select data-subject-picker data-job-subject aria-label="Filter media by subject"><option value="">All subjects</option>${StudioSubjectPicker.options(data.subjects,subject)}<option value="__details">Subject details…</option></select><input type="search" data-media-search placeholder="Search this page…" aria-label="Search photos on this page" value="${escape(search)}"><button type="button" class="jd-tool-button" data-job-tasks aria-haspopup="dialog">☑ Tasks</button><span class="jd-drop-hint">⇧ Drop photos or folders to upload</span><div class="jd-view-controls"><button type="button" data-grid aria-label="Grid view" aria-pressed="${!list}">▦</button><button type="button" data-list aria-label="List view" aria-pressed="${list}">☰</button></div>${canEdit?'<button type="button" class="jd-upload" data-add>＋ Upload</button><input type="file" data-files multiple hidden>':''}</header><nav class="jd-collections" aria-label="Media collections"><button type="button" data-filter="" class="${collection?'':'active'}">All <span>${Object.values(counts).length?Object.values(counts).reduce((a,b)=>a+Number(b),0):data.totalAssets}</span></button>${collections.map(name=>`<button type="button" data-filter="${escape(name)}" class="${collection===name?'active':''}">${escape(name)}${counts[name]!=null?` <span>${counts[name]}</span>`:''}</button>`).join('')}${subject?'<button type="button" data-clear-subject>Clear subject filter ×</button>':''}</nav><div class="jd-contact-sheet" tabindex="0" aria-label="Job photos and upload target"></div><footer class="jd-gallery-footer"><span>${search?`${data.assets.filter(a=>String(a.fileName||'').toLowerCase().includes(search.toLowerCase())).length} visible on this page · `:''}Showing ${data.assets.length?(page-1)*12+1:0}–${(page-1)*12+data.assets.length} of ${data.totalAssets??data.assets.length}</span><div><button type="button" data-previous ${previous.length?'':'disabled'} aria-label="Previous media page">‹</button><span>Page ${page}</span><button type="button" data-next ${data.hasMore?'':'disabled'} aria-label="Next media page">›</button></div></footer><p class="jd-media-result" role="status" data-media-result></p>`;
   renderTiles();
   host.querySelector('[data-open-gallery]').onclick=()=>openPreview();
   host.querySelector('[data-collection]').value=collection;
   host.querySelector('[data-collection]').onchange=e=>change({collection:e.target.value});
   const subjectControl=host.querySelector('[data-job-subject]');
   subjectControl.onchange=async()=>{if(subjectControl.value==='__details'){subjectControl.value=subject;await host.studioOpenSubjects?.();return;}await change({subject:subjectControl.value});};
   host.querySelectorAll('[data-filter]').forEach(button=>{button.onclick=()=>change({collection:button.dataset.filter});if(canEdit){button.ondragover=e=>{if(e.dataTransfer?.types?.includes('Files'))e.preventDefault();};button.ondrop=async e=>{e.preventDefault();e.stopPropagation();await upload(await studioDroppedPhotos(e.dataTransfer),button.dataset.filter||'Unclassified');};}});
   const clear=host.querySelector('[data-clear-subject]');if(clear)clear.onclick=()=>change({subject:''});
   const sessionButton=document.createElement('button');sessionButton.type='button';sessionButton.className='jd-tool-button';sessionButton.dataset.jobSessionPlan='';sessionButton.textContent='Session Plan';sessionButton.onclick=()=>root.studioNavigate('session-plan',{jobId:job.id});host.querySelector('.jd-gallery-toolbar')?.append(sessionButton);
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
 root.StudioJobDetails={media,openGallery,nextActionLink,workflowAction};
})(window);


