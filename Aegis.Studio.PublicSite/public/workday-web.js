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
let galleryController=null,activeUpload=null,nextCursor=null;
const selectedPhotos=new Map(),savedPhotos=new Map();
const assignmentId=()=>el('workday-assignment').value;
const assigned=id=>currentAssignments.some(x=>String(x.id||x.Id)===id);
const uploadPath=id=>'/api/v1/workday/assignments/'+encodeURIComponent(id)+'/uploads';
const stateLabel=item=>({Ready:'Ready to upload',Uploading:'Uploading · '+(item.progress||0)+'%',
  Pending:'Queued for Studio',Acknowledged:'Acknowledged by Studio',Conflict:'Needs review',Failed:'Upload not confirmed'})[item.state]||'Queued for Studio';
function releasePhoto(item){if(item.objectUrl)URL.revokeObjectURL(item.objectUrl);item.objectUrl=null;item.file=null;}
function resetUploads(){
  sessionVersion++;galleryController?.abort();activeUpload?.abort();activeUpload=null;
  for(const item of selectedPhotos.values())releasePhoto(item);
  selectedPhotos.clear();savedPhotos.clear();nextCursor=null;busy=false;
  el('workday-gallery-message').textContent='';el('workday-dropzone').classList.remove('is-dragging');
  el('workday-photos').value='';renderGallery();
}
function setControls(){
  const valid=assigned(assignmentId());
  el('workday-assignment').disabled=busy;
  el('workday-browse').disabled=busy||!valid;el('workday-photos').disabled=busy||!valid;
  const pending=[...selectedPhotos.values()].some(x=>x.assignmentId===assignmentId()&&x.file);
  el('workday-upload').disabled=busy||!valid||!pending;
  el('workday-clear').disabled=busy||!pending;
  el('workday-gallery-refresh').disabled=busy||!valid;
  el('workday-gallery-more').disabled=busy||!nextCursor;
  el('workday-dropzone').setAttribute('aria-disabled',String(busy||!valid));
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
      remove.onclick=()=>{releasePhoto(item);selectedPhotos.delete(item.uploadId);renderGallery();};caption.append(remove);
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
    nextCursor=page.nextCursor;el('workday-gallery-message').textContent='';renderGallery(true);
  }catch(error){
    if(controller.signal.aborted||version!==sessionVersion)return;
    el('workday-gallery-message').textContent='Could not load photographs. Use Refresh photographs to retry.';renderGallery();
  }
}
function addPhotos(files){
  if(busy)return;
  const id=assignmentId();
  if(!assigned(id)){status('Choose one of your assigned Jobs first.');el('workday-assignment').focus();return;}
  let added=0,skipped=0;
  const fingerprints=new Set([...selectedPhotos.values()].filter(x=>x.assignmentId===id&&x.file).map(x=>x.fingerprint));
  for(const file of files){
    const fingerprint=[file.name,file.size,file.lastModified].join('|');
    if(!/^image\/(jpeg|png|webp)$/.test(file.type)||file.size<1||file.size>25_000_000||
       !/\.(jpe?g|png|webp)$/i.test(file.name)||fingerprints.has(fingerprint)||
       [...selectedPhotos.values()].filter(x=>x.file).length>=100){skipped++;continue;}
    const uploadId=crypto.randomUUID();
    selectedPhotos.set(uploadId,{uploadId,assignmentId:id,file,fileName:file.name,fingerprint,
      objectUrl:URL.createObjectURL(file),state:'Ready',progress:0,error:''});
    fingerprints.add(fingerprint);added++;
  }
  el('workday-photos').value='';renderGallery();
  status(added+' photographs added.'+(skipped?' '+skipped+' skipped: choose unique JPEG, PNG or WebP files up to 25 MB, with at most 100 selected photographs.':''));
}
function postPhoto(item){
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
  if(busy||!assigned(assignmentId()))return;
  items=items.filter(x=>x.file&&x.assignmentId===assignmentId());if(!items.length){status('Choose photographs to upload.');return;}
  const version=sessionVersion;busy=true;let queued=0,failed=0;setControls();
  for(const item of items){
    if(version!==sessionVersion)break;
    item.state='Uploading';item.progress=0;item.error='';renderGallery();status('Uploading '+item.fileName+'…');
    try{
      const reply=await postPhoto(item);if(version!==sessionVersion)break;
      item.state=reply.storageStatus||'Pending';item.progress=100;releasePhoto(item);queued++;
    }catch(error){if(version!==sessionVersion)break;item.state='Failed';item.error=error.message;failed++;}
    finally{if(version===sessionVersion)activeUpload=null;}
    if(version===sessionVersion)renderGallery();
  }
  if(version!==sessionVersion)return;
  busy=false;renderGallery();status(queued+' photographs queued for Studio.'+(failed?' '+failed+' could not be confirmed. Retry those photographs.':''));
  await refreshGallery();
  if(version===sessionVersion){
    failed=items.filter(x=>x.file&&x.state==='Failed').length;queued=items.length-failed;
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
el('workday-assignment').addEventListener('change',()=>{savedPhotos.clear();nextCursor=null;el('workday-gallery-message').textContent='';renderGallery();void refreshGallery();});
el('workday-browse').addEventListener('click',()=>el('workday-photos').click());
el('workday-photos').addEventListener('change',event=>addPhotos(Array.from(event.target.files||[])));
const dropzone=el('workday-dropzone');let dragDepth=0;
for(const name of ['dragenter','dragover'])dropzone.addEventListener(name,event=>{
  event.preventDefault();if(busy||!assigned(assignmentId()))return;
  if(name==='dragenter')dragDepth++;dropzone.classList.add('is-dragging');if(event.dataTransfer)event.dataTransfer.dropEffect='copy';
});
dropzone.addEventListener('dragleave',event=>{event.preventDefault();dragDepth=Math.max(0,dragDepth-1);if(!dragDepth)dropzone.classList.remove('is-dragging');});
dropzone.addEventListener('drop',event=>{event.preventDefault();dragDepth=0;dropzone.classList.remove('is-dragging');addPhotos(Array.from(event.dataTransfer?.files||[]));});
el('workday-clear').addEventListener('click',()=>{
  if(busy)return;
  for(const [id,item] of selectedPhotos)if(item.assignmentId===assignmentId()&&item.file){releasePhoto(item);selectedPhotos.delete(id);}
  renderGallery();status('Selection cleared. Uploaded photographs are kept.');
});
el('workday-gallery-refresh').addEventListener('click',()=>{void refreshGallery();});
el('workday-gallery-more').addEventListener('click',()=>{void refreshGallery(true);});
el('workday-upload-form').addEventListener('submit',event=>{event.preventDefault();void sendPhotos([...selectedPhotos.values()]);});
renderGallery();void loadWorkday();
