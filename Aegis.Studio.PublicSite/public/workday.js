const $=id=>document.getElementById(id);
let csrf='',destinations=[],paused=false,controller=null,poll=null;
async function api(path,options={}){
  const headers={...options.headers};
  if(options.body&&typeof options.body==='string')headers['Content-Type']='application/json';
  if(options.method&&options.method!=='GET')headers['X-CSRF-Token']=csrf;
  const r=await fetch(path,{credentials:'same-origin',...options,headers});
  const p=await r.json();if(r.status===401){$('workspace').hidden=true;$('login-panel').hidden=false;$('identity').textContent='';$('logout').hidden=true;if(poll){clearInterval(poll);poll=null;}}if(!r.ok)throw new Error(p.message||'Request failed.');return p;
}
function notice(text){$('notice').textContent=text;}
async function refresh(){
  const user=await api('/api/v1/auth/me');
  $('identity').textContent=user.displayName;$('login-panel').hidden=true;$('workspace').hidden=false;$('logout').hidden=false;
  const data=await api('/api/v1/workday/destinations');destinations=data.items;
  const previous=$('job').value;$('job').replaceChildren(new Option('Choose a job and session',''));
  for(const d of destinations)$('job').add(new Option(d.title+' · '+(d.reference||d.assignmentId.slice(0,8))+' · '+(d.startsAtUtc?new Date(d.startsAtUtc).toLocaleString():''),d.assignmentId));
  if(destinations.some(d=>d.assignmentId===previous))$('job').value=previous;
  $('snapshot-time').textContent='Last synchronized with Studio: '+new Date(data.updatedAt).toLocaleString();chooseJob();
  await uploads();if(!poll)poll=setInterval(()=>uploads().catch(e=>notice(e.message)),10000);
}
function current(){return destinations.find(d=>d.assignmentId===$('job').value);}
function chooseJob(){const d=current(),selected=$('collection').value;$('collection').replaceChildren(new Option('Choose collection',''));for(const c of d?.collections||[])$('collection').add(new Option(c,c));if(d?.collections.includes(selected))$('collection').value=selected;chooseCollection();}
function chooseCollection(){const d=current(),c=$('collection').value;$('subject').replaceChildren(new Option('No specific subject',''));for(const s of d?.subjects||[])if(s.collection===c)$('subject').add(new Option(s.name,s.id));showDestination();}
function showDestination(){$('destination').textContent=current()&&$('collection').value?'Destination: '+current().title+' / '+$('collection').value+($('subject').value?' / '+$('subject').selectedOptions[0].text:''):'';}
$('job').onchange=chooseJob;$('collection').onchange=chooseCollection;$('subject').onchange=showDestination;
$('files').onchange=()=>{$('selection').textContent=$('files').files.length+' files selected';};
$('drop').ondragover=e=>e.preventDefault();
$('drop').ondrop=e=>{e.preventDefault();if($('upload').disabled)return;$('files').files=e.dataTransfer.files;$('files').onchange();};
const hex=bytes=>Array.from(new Uint8Array(bytes),b=>b.toString(16).padStart(2,'0')).join('');
async function checksum(blob){return hex(await crypto.subtle.digest('SHA-256',await blob.arrayBuffer()));}
function supportedType(file){return ({jpg:'image/jpeg',jpeg:'image/jpeg',png:'image/png',webp:'image/webp',tif:'image/tiff',tiff:'image/tiff'}[file.name.split('.').pop().toLowerCase()]||file.type||'');}
$('upload-form').onsubmit=async e=>{
  e.preventDefault();const d=current(),collection=$('collection').value,subjectId=$('subject').value||null,files=Array.from($('files').files);
  if(!d||!collection||!files.length)return notice('Choose a job, collection and files.');
  paused=false;$('upload').disabled=true;$('pause').disabled=false;
  const controls=[$('job'),$('collection'),$('subject'),$('files')];controls.forEach(x=>x.disabled=true);
  try{
    const prepared=[];
    for(const file of files){
      if(file.size<1||file.size>100000000)throw new Error(file.name+': file must be 1 byte to 100 MB.');
      notice('Preparing '+file.name);prepared.push({file,hash:await checksum(file)});
    }
    const key='workday-batch:'+d.assignmentId+':'+collection+':'+(subjectId||'')+':'+await checksum(new Blob([JSON.stringify(prepared.map(x=>({name:x.file.name,size:x.file.size,hash:x.hash})))]));
    let batch;try{batch=JSON.parse(localStorage.getItem(key)||'null');}catch{}
    batch||={batchId:crypto.randomUUID(),ids:prepared.map(()=>crypto.randomUUID())};localStorage.setItem(key,JSON.stringify(batch));
    const declared=await api('/api/v1/workday/remote-media/batches',{method:'POST',body:JSON.stringify({batchId:batch.batchId,assignmentId:d.assignmentId,jobId:d.jobId,sessionId:d.sessionId,collection,subjectId,
      files:prepared.map((x,index)=>({uploadId:batch.ids[index],fileName:x.file.name,sizeBytes:x.file.size,sha256:x.hash,contentType:supportedType(x.file)}))})});
    for(let index=0;index<prepared.length;index++){
      if(paused)break;
      const file=prepared[index].file,identity={uploadId:batch.ids[index]};
      let state=declared.files[index];
      if(['Cancelled','Expired'].includes(state.state)){localStorage.removeItem(key);throw new Error('Previous upload expired or was cancelled. Choose Upload again to start a new transfer.');}
      while(state.state==='Uploading'&&state.receivedBytes<file.size&&!paused){
        const part=file.slice(state.receivedBytes,Math.min(file.size,state.receivedBytes+state.chunkBytes));
        controller=new AbortController();
        state=await api('/api/v1/workday/remote-media/'+identity.uploadId+'/chunks',{method:'PUT',body:part,signal:controller.signal,headers:{'X-Upload-Offset':String(state.receivedBytes),'X-Content-Sha256':await checksum(part)}});
        notice(file.name+': '+Math.round(100*state.receivedBytes/file.size)+'% received');await uploads();
      }
      if(!paused&&['Uploading','Quarantined'].includes(state.state))state=await api('/api/v1/workday/remote-media/'+identity.uploadId+'/complete',{method:'POST',body:'{}'});
      notice(paused?'Transfer paused. Select Upload to resume.':file.name+': '+state.state+'.');await uploads();
    }
  }catch(error){notice(error.name==='AbortError'?'Transfer paused. Upload again to resume.':error.message);}
  finally{controller=null;$('upload').disabled=false;$('pause').disabled=true;controls.forEach(x=>x.disabled=false);}
};
$('pause').onclick=()=>{paused=true;controller?.abort();};
async function uploads(){
  const data=await api('/api/v1/workday/remote-media');$('uploads').replaceChildren();
  for(const item of data.items){
    const row=document.createElement('tr'),file=document.createElement('td'),progress=document.createElement('td'),status=document.createElement('td'),time=document.createElement('td'),actions=document.createElement('td');
    file.textContent=item.fileName;const target=document.createElement('small');target.textContent='Batch '+item.batchId.slice(0,8)+' · '+item.batchImportedCount+'/'+item.batchFileCount+' imported · '+(destinations.find(d=>d.assignmentId===item.destination.assignmentId)?.title||item.destination.jobId)+' / '+item.destination.collection;file.append(target);
    const bar=document.createElement('progress');bar.max=item.sizeBytes;bar.value=item.receivedBytes;progress.append(bar);
    status.textContent=({ReceivedByApi:'Received by API · Waiting for Studio',Imported:'Imported into Studio',Purged:'Imported · Temporary copy cleaned up',NeedsReview:'Needs review in Studio',Quarantined:'Awaiting file safety scan'})[item.state]||item.state;
    time.textContent=new Date(item.updatedAt).toLocaleString();
    if(['Uploading','Quarantined'].includes(item.state)){const cancel=document.createElement('button');cancel.textContent='Cancel';cancel.onclick=async()=>{try{await api('/api/v1/workday/remote-media/'+item.uploadId,{method:'DELETE'});await uploads();}catch(e){notice(e.message);}};actions.append(cancel);}
    row.append(file,progress,status,time,actions);$('uploads').append(row);
  }
}
$('login').onsubmit=async e=>{e.preventDefault();try{await api('/api/v1/auth/login',{method:'POST',body:JSON.stringify(Object.fromEntries(new FormData(e.target)))});e.target.reset();await refresh();notice('Signed in.');}catch(error){notice(error.message);}};
$('enroll').onsubmit=async e=>{e.preventDefault();try{await api('/api/v1/workday/enroll',{method:'POST',body:JSON.stringify(Object.fromEntries(new FormData(e.target)))});e.target.reset();notice('Account activated. Sign in above.');}catch(error){notice(error.message);}};
$('logout').onclick=async()=>{if($('upload').disabled)return notice('Pause the active transfer before signing out.');await api('/api/v1/auth/logout',{method:'POST',body:'{}'});clearInterval(poll);poll=null;location.reload();};
$('refresh').onclick=()=>refresh().catch(e=>notice(e.message));
$('jobs-tab').onclick=()=>{$('jobs-panel').hidden=false;$('jobs-tab').setAttribute('aria-pressed','true');$('uploads-tab').setAttribute('aria-pressed','false');};
$('uploads-tab').onclick=()=>{$('jobs-panel').hidden=true;$('jobs-tab').setAttribute('aria-pressed','false');$('uploads-tab').setAttribute('aria-pressed','true');uploads().catch(e=>notice(e.message));};
(async()=>{csrf=(await api('/api/intake-session')).csrf;try{await refresh();}catch{notice('Sign in to your WorkDay account.');}})();
