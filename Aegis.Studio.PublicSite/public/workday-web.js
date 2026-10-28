const el=id=>document.getElementById(id);
const status=message=>{el('workday-status').textContent=message;};
async function call(path,options={}){
  const response=await fetch(path,{credentials:'same-origin',...options});
  const body=(response.headers.get('content-type')||'').includes('json')?await response.json():null;
  if(!response.ok)throw Object.assign(Error(body?.message||body?.error||'Request failed ('+response.status+').'),{status:response.status});
  return body;
}
function assignmentLabel(item){
  const date=item.startsAtUtc||item.StartsAtUtc||'';
  return (item.title||item.Title||'Assigned session')+(date?' · '+new Date(date).toLocaleString('en-JM'):'');
}
let currentAssignments=[],currentUser='',busy=false,authVersion=0,sessionVersion=0;
let galleryController=null,activeUpload=null,hashWorker=null,hashReject=null,transferController=null,nextCursor=null;
const selectedPhotos=new Map(),savedPhotos=new Map();
let shootSummary=null,completionController=null,pauseRequested=false;
const assignmentId=()=>el('workday-assignment').value;
const assigned=id=>currentAssignments.some(x=>String(x.id||x.Id)===id);
const uploadPath=id=>'/api/v1/workday/assignments/'+encodeURIComponent(id)+'/uploads';
const stateLabel=item=>({Ready:'Ready to upload',Uploading:'Uploading · '+(item.progress||0)+'%',
  Pending:'Queued for Studio',Acknowledged:'Acknowledged by Studio',Conflict:'Needs review',Failed:'Upload not confirmed'})[item.state]||'Queued for Studio';
function releasePhoto(item){if(item.objectUrl)URL.revokeObjectURL(item.objectUrl);item.objectUrl=null;item.file=null;}
function resetUploads(){
  sessionVersion++;transferController?.abort();transferController=null;hashWorker?.terminate();hashReject?.(Error('WorkDay session changed.'));hashWorker=null;hashReject=null;galleryController?.abort();completionController?.abort();activeUpload?.abort();activeUpload=null;
  shootSummary=null;el('workday-finish-confirm').checked=false;el('workday-finish-status').textContent='';
  for(const item of selectedPhotos.values())releasePhoto(item);
  selectedPhotos.clear();savedPhotos.clear();nextCursor=null;busy=false;
  el('workday-gallery-message').textContent='';el('workday-dropzone').classList.remove('is-dragging');
  el('workday-photos').value='';renderGallery();
}
function setControls(){
  const valid=assigned(assignmentId());
  const waiting=shootSummary?.completion?.state==='Pending',locked=busy||waiting;
  el('workday-assignment').disabled=busy;
  el('workday-browse').disabled=locked||!valid;el('workday-photos').disabled=locked||!valid;
  const pending=[...selectedPhotos.values()].some(x=>x.assignmentId===assignmentId()&&x.file);
  el('workday-upload').disabled=locked||!valid||!pending;
  el('workday-clear').disabled=busy||!pending;
  el('workday-pause').disabled=!busy;
  el('workday-gallery-refresh').disabled=busy||!valid;
  el('workday-gallery-more').disabled=busy||!nextCursor;
  el('workday-dropzone').setAttribute('aria-disabled',String(locked||!valid));
  const done=shootSummary?.completion?.state==='Acknowledged';
  el('workday-finish-confirm').disabled=busy||!valid||waiting||done;
  el('workday-finish').disabled=busy||!valid||pending||waiting||done||!el('workday-finish-confirm').checked||
    !shootSummary?.total||shootSummary.ready!==shootSummary.total;
}
async function refreshShoot(){
  const id=assignmentId(),version=sessionVersion;
  completionController?.abort();completionController=new AbortController();const controller=completionController;
  if(!assigned(id)){shootSummary=null;el('workday-finish-status').textContent='Choose an assigned Job.';setControls();return;}
  try{
    const summary=await call('/api/v1/workday/assignments/'+encodeURIComponent(id)+'/shoot-completion',{signal:controller.signal});
    if(controller.signal.aborted||version!==sessionVersion||id!==assignmentId())return;
    shootSummary=summary;const result=summary.completion;
    el('workday-finish-status').textContent=result?.state==='Acknowledged'?'Your shoot is done. Studio recorded your confirmation.':
      result?.state==='Pending'?'Shoot confirmation sent. Waiting for Studio.':
      result?.state==='Conflict'?'Studio could not finish the shoot: '+(result.message||'Ask the Job Owner to review it.'):
      summary.total?summary.ready+' of '+summary.total+' photographs imported into Studio.':'Upload your photographs before finishing the shoot.';
  }catch(error){
    if(controller.signal.aborted||version!==sessionVersion||id!==assignmentId())return;
    shootSummary=null;el('workday-finish-status').textContent='Could not check shoot status. Refresh photographs to retry.';
  }
  setControls();
}
async function finishShoot(){
  if(el('workday-finish').disabled)return;
  const id=assignmentId(),version=sessionVersion;busy=true;setControls();
  try{
    const snapshot=await call('/api/v1/workday/bootstrap');
    if(version!==sessionVersion||id!==assignmentId())return;
    const assignment=[...(snapshot.today||snapshot.Today||[]),...(snapshot.upcoming||snapshot.Upcoming||[])]
      .find(x=>String(x.id||x.Id)===id);
    if(!assignment)throw Error('This Job is no longer assigned to you.');
    await call('/api/v1/workday/assignments/'+encodeURIComponent(id)+'/shoot-completion',{
      method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({
        operationId:crypto.randomUUID(),expectedVersion:assignment.version??assignment.Version??0
      })});
    if(version!==sessionVersion)return;
    el('workday-finish-confirm').checked=false;
    await refreshShoot();
  }catch(error){if(version===sessionVersion)el('workday-finish-status').textContent=error.message;}
  finally{if(version===sessionVersion){busy=false;setControls();}}
}
function renderGallery(retryPreviews=false){
  const grid=el('workday-photo-grid'),existing=new Map([...grid.children].map(card=>[card.dataset.uploadId,card]));
  const items=new Map([...savedPhotos,...[...selectedPhotos].filter(([,x])=>x.assignmentId===assignmentId())]);
  el('workday-gallery-empty').hidden=items.size>0;
  el('workday-gallery-empty').textContent=assigned(assignmentId())?'No photographs yet. Drop files above or choose photographs.':'Choose an assigned Job to view and upload photographs.';
  el('workday-gallery-count').textContent=items.size+' photographs shown';
  el('workday-gallery-more').hidden=!nextCursor;
  let position=0;
  for(const item of items.values()){
    let card=existing.get(item.uploadId);existing.delete(item.uploadId);
    if(!card){
      card=document.createElement('figure');card.className='workday-photo';card.dataset.uploadId=item.uploadId;
      const image=document.createElement('img');image.loading='lazy';image.decoding='async';
      const unavailable=document.createElement('span');unavailable.className='workday-preview-unavailable';unavailable.textContent='Preview unavailable';unavailable.hidden=true;
      image.addEventListener('error',()=>{image.hidden=true;unavailable.hidden=false;});
      card.append(image,unavailable,document.createElement('figcaption'));
    }
    const image=card.querySelector('img'),source=item.objectUrl||uploadPath(item.assignmentId)+'/'+encodeURIComponent(item.uploadId)+'/thumbnail';
    image.alt=item.fileName;
    if(image.dataset.source!==source||(retryPreviews&&image.hidden)){
      image.dataset.source=source;image.hidden=false;card.querySelector('.workday-preview-unavailable').hidden=true;image.src=source;
    }
    const caption=card.querySelector('figcaption'),name=document.createElement('strong'),message=document.createElement('span');caption.replaceChildren();
    name.textContent=item.fileName;message.className='workday-photo-status';message.textContent=item.error||stateLabel(item);message.dataset.photoStatus='';
    caption.append(name,message);
    if(item.state==='Uploading'){
      const progress=document.createElement('progress');progress.max=100;progress.value=item.progress||0;
      progress.setAttribute('aria-label','Uploading '+item.fileName);caption.append(progress);
    }
    if(item.file){
      const remove=document.createElement('button');remove.type='button';remove.className='text-button';remove.textContent='Remove';remove.disabled=busy;
      remove.setAttribute('aria-label','Remove '+item.fileName+' from selection');
      remove.onclick=()=>{void removePhoto(item);};caption.append(remove);
      if(item.state==='Failed'){
        const retry=document.createElement('button');retry.type='button';retry.className='text-button';retry.textContent='Retry';retry.disabled=busy;
        retry.setAttribute('aria-label','Retry '+item.fileName);retry.onclick=()=>sendPhotos([item]);caption.append(retry);
      }
    }
    if(grid.children[position]!==card)grid.insertBefore(card,grid.children[position]||null);position++;
  }
  for(const card of existing.values())card.remove();
  setControls();
}
async function removePhoto(item){
  if(busy)return;
  const version=sessionVersion;
  try{
    if(item.resumableSession)await call('/api/v1/workday/assignments/'+encodeURIComponent(item.assignmentId)+'/upload-sessions/'+item.uploadId,{method:'DELETE'});
    if(version!==sessionVersion)return;
    releasePhoto(item);selectedPhotos.delete(item.uploadId);renderGallery();status('Selection removed. Completed originals are retained.');
  }catch(error){
    if(version===sessionVersion&&error.status===404){releasePhoto(item);selectedPhotos.delete(item.uploadId);renderGallery();}
    else if(version===sessionVersion)status(error.message);
  }
}
async function refreshGallery(more=false){
  const id=assignmentId();galleryController?.abort();galleryController=new AbortController();
  const controller=galleryController,version=sessionVersion;
  if(!more){savedPhotos.clear();nextCursor=null;}
  if(!assigned(id)){renderGallery();return;}
  el('workday-gallery-message').textContent='Loading photographs…';
  try{
    const page=await call(uploadPath(id)+(more&&nextCursor?'?before='+nextCursor:''),{signal:controller.signal});
    if(controller.signal.aborted||version!==sessionVersion||assignmentId()!==id)return;
    for(const item of page.items||[]){
      savedPhotos.set(item.uploadId,{...item,assignmentId:id});
      const local=selectedPhotos.get(item.uploadId);
      if(local&&local.state!=='Uploading'){releasePhoto(local);selectedPhotos.delete(item.uploadId);}
    }
    nextCursor=page.nextCursor;el('workday-gallery-message').textContent='';renderGallery(true);await refreshShoot();
  }catch(error){
    if(controller.signal.aborted||version!==sessionVersion)return;
    el('workday-gallery-message').textContent='Could not load photographs. Use Refresh photographs to retry.';renderGallery();
  }
}
function addPhotos(files){
  if(busy||shootSummary?.completion?.state==='Pending')return;
  const id=assignmentId();
  if(!assigned(id)){status('Choose one of your assigned Jobs first.');el('workday-assignment').focus();return;}
  let added=0,skipped=0;
  const fingerprints=new Set([...selectedPhotos.values()].filter(x=>x.assignmentId===id&&x.file).map(x=>x.fingerprint));
  for(const file of files){
    const fingerprint=[file.name,file.size,file.lastModified].join('|');
    if(file.size<1||file.size>536_870_912||
       !/\.(jpe?g|png|webp|tiff?|heic|heif|cr2|cr3|nef|nrw|arw|raf|orf|rw2|dng)$/i.test(file.name)||fingerprints.has(fingerprint)||
       [...selectedPhotos.values()].filter(x=>x.file).length>=100){skipped++;continue;}
    const uploadId=crypto.randomUUID();
    selectedPhotos.set(uploadId,{uploadId,assignmentId:id,file,fileName:file.name,fingerprint,
      objectUrl:/\.(jpe?g|png|webp)$/i.test(file.name)?URL.createObjectURL(file):null,state:'Ready',progress:0,error:''});
    fingerprints.add(fingerprint);added++;
  }
  el('workday-photos').value='';renderGallery();
  status(added+' photographs added.'+(skipped?' '+skipped+' skipped: choose unique supported photographs up to 512 MiB, with at most 100 selected photographs.':''));
}
function postPhoto(item){
  if(!/\.(jpe?g|png|webp)$/i.test(item.fileName)||item.file.size>25_000_000)return postResumablePhoto(item);
  return postLegacyPhoto(item);
}
function hashPhoto(item){
  return new Promise((resolve,reject)=>{
    const version=sessionVersion,worker=new Worker('/upload-hash-worker.js',{type:'module'});hashWorker=worker;hashReject=reject;
    worker.onmessage=({data})=>{
      if(version!==sessionVersion){worker.terminate();reject(Error('WorkDay session changed.'));return;}
      if(data.error){worker.terminate();hashWorker=null;hashReject=null;reject(Error(data.error));}
      else if(data.sha256){worker.terminate();hashWorker=null;hashReject=null;resolve(data.sha256);}
      else status('Checking original checksum · '+Math.round(100*data.hashed/item.file.size)+'%');
    };
    worker.onerror=()=>{worker.terminate();hashWorker=null;hashReject=null;reject(Error('Checksum worker unavailable. Original has not been uploaded.'));};
    worker.postMessage({file:item.file});
  });
}
async function postResumablePhoto(item){
  const version=sessionVersion,controller=new AbortController();transferController=controller;
  const ensureSession=()=>{if(version!==sessionVersion)throw Error('WorkDay session changed.');if(pauseRequested)throw Error('Transfer paused. Retry to resume.');};
  item.sha256||=await hashPhoto(item);ensureSession();
  // Re-selecting the identical file after a reload recovers the same server session.
  const identity=JSON.stringify([currentUser.toLowerCase(),item.assignmentId,item.fileName,item.file.size,item.sha256]);
  const digest=[...new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(identity)))].map(x=>x.toString(16).padStart(2,'0')).join('');
  ensureSession();
  const id=digest.slice(0,8)+'-'+digest.slice(8,12)+'-8'+digest.slice(13,16)+'-a'+digest.slice(17,20)+'-'+digest.slice(20,32);
  if(item.uploadId!==id){selectedPhotos.delete(item.uploadId);item.uploadId=id;selectedPhotos.set(id,item);renderGallery();}
  const base='/api/v1/workday/assignments/'+encodeURIComponent(item.assignmentId)+'/upload-sessions';
  item.resumableSession=true;
  const session=await call(base,{signal:controller.signal,method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({uploadId:id,fileName:item.fileName,sizeBytes:item.file.size,sha256:item.sha256})});
  ensureSession();
  if(session.state==='Cancelled')throw Error('This upload was cancelled. Select a different replacement file.');
  if(session.state==='Acknowledged'||session.state==='Purged')return {uploadId:id,storageStatus:'Acknowledged'};
  const received=new Map(session.receivedChunks.map(x=>[x.chunkIndex,x]));
  if(session.state==='Receiving')for(let offset=0,index=0;offset<item.file.size;offset+=session.chunkBytes,index++){
    ensureSession();const bytes=await item.file.slice(offset,offset+session.chunkBytes).arrayBuffer();ensureSession();
    const checksum=[...new Uint8Array(await crypto.subtle.digest('SHA-256',bytes))].map(x=>x.toString(16).padStart(2,'0')).join('');
    if(received.has(index)){if(received.get(index).sha256!==checksum)throw Error('Stored chunk differs from this file. Original retained; cancel this session before replacing it.');continue;}
    await new Promise((resolve,reject)=>{
      const xhr=new XMLHttpRequest();activeUpload=xhr;xhr.open('PUT',base+'/'+id+'/chunks/'+index);xhr.withCredentials=true;xhr.timeout=120000;
      xhr.setRequestHeader('Content-Type','application/octet-stream');xhr.setRequestHeader('X-Chunk-SHA256',checksum);
      xhr.upload.onprogress=event=>{if(event.lengthComputable){item.progress=Math.round(100*(offset+event.loaded)/item.file.size);renderGallery();}};
      xhr.onload=()=>xhr.status===200?resolve():reject(Error('Chunk was not confirmed. Retry resumes received chunks.'));
      xhr.onerror=xhr.ontimeout=xhr.onabort=()=>reject(Error('Transfer paused. Retry resumes the original without sending received chunks again.'));
      xhr.send(bytes);
    });
  }
  ensureSession();return call(base+'/'+id+'/complete',{signal:controller.signal,method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});
}
function postLegacyPhoto(item){
  return new Promise((resolve,reject)=>{
    const body=new FormData();body.append('file',item.file,item.fileName);body.append('uploadId',item.uploadId);
    const xhr=new XMLHttpRequest();activeUpload=xhr;
    xhr.open('POST',uploadPath(item.assignmentId));xhr.withCredentials=true;xhr.timeout=120000;
    xhr.upload.onprogress=event=>{
      if(!event.lengthComputable)return;
      item.progress=Math.round(100*event.loaded/event.total);
      const card=el('workday-photo-grid').querySelector('[data-upload-id="'+item.uploadId+'"]');
      if(card){card.querySelector('[data-photo-status]').textContent=stateLabel(item);card.querySelector('progress').value=item.progress;}
    };
    xhr.onload=()=>{
      let reply;try{reply=JSON.parse(xhr.responseText);}catch{}
      if(xhr.status===202&&reply?.uploadId===item.uploadId)resolve(reply);
      else reject(Error(reply?.message||'Upload could not be confirmed. Retry this photograph.'));
    };
    xhr.onerror=xhr.ontimeout=xhr.onabort=()=>reject(Error('Connection interrupted. Retry this photograph to confirm its upload.'));
    xhr.send(body);
  });
}
async function sendPhotos(items){
  if(busy||shootSummary?.completion?.state==='Pending'||!assigned(assignmentId()))return;
  items=items.filter(x=>x.file&&x.assignmentId===assignmentId());if(!items.length){status('Choose photographs to upload.');return;}
  const version=sessionVersion;busy=true;pauseRequested=false;let queued=0,failed=0;setControls();
  for(const item of items){
    if(version!==sessionVersion||pauseRequested)break;
    item.state='Uploading';item.progress=0;item.error='';renderGallery();status('Uploading '+item.fileName+'…');
    try{
      const reply=await postPhoto(item);if(version!==sessionVersion)break;
      item.state=reply.storageStatus||'Pending';item.progress=100;releasePhoto(item);queued++;
    }catch(error){if(version!==sessionVersion)break;item.state='Failed';item.error=error.message;failed++;}
    finally{if(version===sessionVersion){activeUpload=null;transferController=null;}}
    if(version===sessionVersion)renderGallery();
  }
  if(version!==sessionVersion)return;
  busy=false;renderGallery();status(queued+' photographs queued for Studio.'+(failed?' '+failed+' could not be confirmed. Retry those photographs.':''));
  await refreshGallery();
  if(version===sessionVersion){
    failed=items.filter(x=>x.file).length;queued=items.length-failed;
    status(queued+' photographs queued for Studio.'+(failed?' '+failed+' could not be confirmed. Retry those photographs.':''));
  }
}
async function loadWorkday(){
  const version=++authVersion;
  try{
    const me=await call('/api/v1/auth/me'),bootstrap=await call('/api/v1/workday/bootstrap');
    if(version!==authVersion)return;
    if(currentUser!==me.userName){resetUploads();currentUser=me.userName;}
    el('workday-login').hidden=true;el('workday-panel').hidden=false;el('workday-user').textContent=me.displayName||me.userName;
    currentAssignments=[...new Map([...(bootstrap.today||bootstrap.Today||[]),...(bootstrap.upcoming||bootstrap.Upcoming||[])]
      .filter(x=>x.id||x.Id).map(x=>[String(x.id||x.Id),x])).values()];
    const select=el('workday-assignment'),previous=select.value;select.replaceChildren();select.add(new Option('Choose an assigned Job',''));
    for(const item of currentAssignments)select.add(new Option(assignmentLabel(item),item.id||item.Id));
    if(assigned(previous))select.value=previous;
    const list=el('workday-jobs');list.replaceChildren();
    for(const item of currentAssignments){const li=document.createElement('li');li.textContent=assignmentLabel(item);list.append(li);}
    status(currentAssignments.length+' assigned sessions available.');await refreshGallery();
  }catch(error){
    if(version!==authVersion)return;
    resetUploads();currentUser='';currentAssignments=[];el('workday-login').hidden=false;el('workday-panel').hidden=true;
    status(error.status===401?'Sign in to WorkDay to see your assigned Jobs.':error.message);
  }
}
el('workday-signin').addEventListener('submit',async event=>{
  event.preventDefault();authVersion++;
  const form=event.currentTarget,submit=form.querySelector('[type=submit]');submit.disabled=true;status('Signing in…');
  try{
    await call('/api/v1/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify({userName:form.elements.userName.value,password:form.elements.password.value})});
    form.elements.password.value='';await loadWorkday();
  }catch(error){status(error.message);}finally{submit.disabled=false;}
});
el('workday-signout').addEventListener('click',async()=>{
  authVersion++;resetUploads();
  try{
    await call('/api/v1/auth/logout',{method:'POST'});currentUser='';currentAssignments=[];
    el('workday-panel').hidden=true;el('workday-login').hidden=false;status('Signed out.');
  }catch{status('Sign-out could not be confirmed. Please retry.');}
});
el('workday-assignment').addEventListener('change',()=>{savedPhotos.clear();nextCursor=null;shootSummary=null;el('workday-finish-confirm').checked=false;el('workday-finish-status').textContent='';el('workday-gallery-message').textContent='';renderGallery();void refreshGallery();void refreshShoot();});
el('workday-browse').addEventListener('click',()=>el('workday-photos').click());
el('workday-photos').addEventListener('change',event=>addPhotos(Array.from(event.target.files||[])));
const dropzone=el('workday-dropzone');let dragDepth=0;
for(const name of ['dragenter','dragover'])dropzone.addEventListener(name,event=>{
  event.preventDefault();if(busy||!assigned(assignmentId()))return;
  if(name==='dragenter')dragDepth++;dropzone.classList.add('is-dragging');if(event.dataTransfer)event.dataTransfer.dropEffect='copy';
});
dropzone.addEventListener('dragleave',event=>{event.preventDefault();dragDepth=Math.max(0,dragDepth-1);if(!dragDepth)dropzone.classList.remove('is-dragging');});
dropzone.addEventListener('drop',event=>{event.preventDefault();dragDepth=0;dropzone.classList.remove('is-dragging');addPhotos(Array.from(event.dataTransfer?.files||[]));});
el('workday-clear').addEventListener('click',async()=>{
  if(busy)return;
  await Promise.all([...selectedPhotos.values()].filter(item=>item.assignmentId===assignmentId()&&item.file).map(removePhoto));
});
el('workday-gallery-refresh').addEventListener('click',()=>{void refreshGallery();});
el('workday-gallery-more').addEventListener('click',()=>{void refreshGallery(true);});
el('workday-pause').addEventListener('click',()=>{pauseRequested=true;transferController?.abort();hashWorker?.terminate();hashReject?.(Error('Checksum paused. Retry to resume.'));hashWorker=null;hashReject=null;activeUpload?.abort();status('Pausing transfer. Retry keeps the same upload identity.');});
el('workday-upload-form').addEventListener('submit',event=>{event.preventDefault();void sendPhotos([...selectedPhotos.values()]);});
el('workday-finish-confirm').addEventListener('change',setControls);
el('workday-finish').addEventListener('click',()=>{void finishShoot();});
setInterval(()=>{if(!el('workday-panel').hidden&&!busy&&assigned(assignmentId()))void refreshShoot();},10000);
renderGallery();void loadWorkday();
