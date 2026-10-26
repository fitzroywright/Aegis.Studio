/* Studio Media Review & Upload: full-width operational workspace. */
(function(){
  const escMedia=value=>String(value??'').replace(/[&<>"']/g,ch=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
  let revision=0;
  window.showMediaReview=async function(){
    const current=++revision;
    conversationView.classList.add('hidden');genericView.classList.remove('hidden');
    genericView.innerHTML='<div class="today-loading">Loading Media Review & Upload…</div>';
    let jobs=[],workspaces=[];
    try{
      const [result,ws]=await Promise.all([json('/api/jobs'),json('/api/workspaces')]);
      workspaces=Array.isArray(ws)?ws:ws?.workspaces||[];
      jobs=Array.isArray(result)?result:Array.isArray(result?.jobs)?result.jobs:[];
    }catch(error){
      if(current!==revision)return;
      genericView.innerHTML='<section class="job-section"><h2>Media Review & Upload</h2><p role="alert">'+escMedia(error.message||'Unable to load jobs.')+'</p></section>';return;
    }
    if(current!==revision)return;
    genericView.innerHTML=`<div class="studio-media-page">
      <header class="studio-media-heading"><div><h1>Media Review &amp; Upload</h1><p>Upload, organise and review Job media and source files.</p></div></header>
      <div class="studio-media-workspace"><section class="studio-media-panel studio-media-browser"><h2>Media Destinations</h2>
        <div class="studio-media-tree-layout">
          <div><input id="studioMediaJobSearch" type="search" placeholder="Search Workspaces, Jobs, Collections, Subjects…" aria-label="Search media destinations"><select id="studioMediaJobStatus" aria-label="Filter jobs by status"><option value="">All Open Jobs</option>${StudioStatusOrder.sort(jobs.map(j=>j.status||'Unknown')).map(x=>`<option>${escMedia(x)}</option>`).join('')}</select><label>Job<select id="studioMediaQuickJob" data-job-picker><option value="">Choose a job…</option>${StudioSubjectPicker.jobOptions(jobs)}</select></label><div id="studioMediaTree" class="studio-media-tree" role="tree"></div><small id="studioMediaJobMatches"></small></div>

        </div>
        <div class="studio-media-targets" hidden><select id="studioMediaJob"><option value="">Select Job</option></select><select id="studioMediaCollection"><option value="Unclassified">Unclassified</option></select><select id="studioMediaSubject"><option value="">No Subject</option></select></div>
      </section>
      <div class="studio-media-main"><div class="studio-media-tree-summary studio-media-announcement" role="status"><div class="studio-media-announcement-copy"><small>Selected destination</small><strong id="studioMediaPath">Select a destination</strong><p id="studioMediaDestinationNotice"></p></div><button type="button" class="action" id="studioMediaOpenJob" disabled>Open Job Details ↗</button></div><section class="studio-media-panel studio-media-upload"><label class="studio-media-session" id="studioMediaSessionLabel" hidden>Photography session<select id="studioMediaSession" aria-label="Upload photography session"></select></label>
        <div class="studio-media-drop" id="studioMediaDrop" tabindex="0"><span aria-hidden="true">⇧</span><strong>Drag files here to upload</strong><p>Or choose images and other media from your device</p><button type="button" class="primary" id="studioMediaChoose">Choose Files</button><input type="file" id="studioMediaFiles" multiple hidden></div>
        <div class="studio-media-upload-actions"><span id="studioMediaProgress" role="status" aria-live="polite"></span><button type="button" class="action" id="studioMediaRetryUpload" hidden>Retry Upload</button></div>
      </section>
<section class="studio-media-panel studio-media-library"><header class="studio-media-gallery-header"><div><h2>Media Library</h2><span id="studioMediaCount"></span></div><div class="studio-media-gallery-tools"><input type="search" id="studioMediaFileSearch" placeholder="Search files…" aria-label="Search files"><select id="studioMediaStatus" aria-label="Filter by status"><option value="">All statuses</option><option value="Needs Sorting">Needs Sorting</option><option value="Needs Review">Needs Review</option><option value="Approved">Approved</option><option value="Rejected">Rejected</option></select><select id="studioMediaSort" aria-label="Sort media"><option value="new">Newest first</option><option value="name">File name</option></select><button type="button" class="action" id="studioMediaRefresh">Refresh</button></div></header><div class="studio-media-status-tabs"><button type="button" data-media-tab="" class="active">All Files</button><button type="button" data-media-tab="Needs Review">Awaiting Review</button><button type="button" data-media-tab="Approved">Approved</button><button type="button" data-media-tab="Rejected">Rejected</button></div>
        <div class="studio-media-carousel-layout">
          <div class="studio-media-carousel-wrap">
            <button type="button" class="studio-media-carousel-arrow" id="studioMediaPrevious" aria-label="Previous images">‹</button>
            <div class="studio-media-gallery" id="studioMediaGallery"><p class="muted">Select a Job to view its media.</p></div>
            <button type="button" class="studio-media-carousel-arrow" id="studioMediaNext" aria-label="Next images">›</button>
          </div>

        </div></section>      </div><aside class="studio-media-panel studio-media-detail st-right" id="studioMediaDetail"><h3>Selected Image</h3><p class="muted">Select an image to see its details.</p></aside></div></div>`;
    const root=genericView.querySelector('.studio-media-page'),job=root.querySelector('#studioMediaJob'),collection=root.querySelector('#studioMediaCollection'),subject=root.querySelector('#studioMediaSubject'),files=root.querySelector('#studioMediaFiles'),progress=root.querySelector('#studioMediaProgress'),gallery=root.querySelector('#studioMediaGallery'),count=root.querySelector('#studioMediaCount'),notice=root.querySelector('#studioMediaDestinationNotice');
    const search=root.querySelector('#studioMediaJobSearch'),matches=root.querySelector('#studioMediaJobMatches'),tree=root.querySelector('#studioMediaTree'),path=root.querySelector('#studioMediaPath');
    root.querySelector('#studioMediaJobStatus').onchange=renderTree;
    const workspaceNames=new Map(workspaces.map(w=>[String(w.id),w.name||w.title||'Workspace']));
    const describe=j=>[j.jobNumber||j.reference||'',j.title||j.name||'Job',j.clientName||j.clientName||''].filter(Boolean).join(' · ');
    const expanded=new Set(['workspaces']),cache=new Map();
    let activeTab='',selectedAssetId='';
    let selection='';
    function renderTree(){
      const q=search.value.trim().toLowerCase(),stage=root.querySelector('#studioMediaJobStatus').value;
      const visibleJobs=jobs.filter(j=>(stage ? j.status===stage : !StudioSubjectPicker.isClosed(j)));
      const matchesText=(...parts)=>!q||parts.join(' ').toLowerCase().includes(q);
      const node=(id,name,depth,children,icon)=>'<button type="button" role="treeitem" data-tree-id="'+escMedia(id)+'" class="studio-media-tree-node'+(selection===id?' selected':'')+'" style="padding-left:'+(12+depth*20)+'px"><span>'+ (children?(expanded.has(id)||q?'⌄':'›'):'·') +'</span><span>'+icon+'</span><span>'+escMedia(name)+'</span></button>';
      let html=node('workspaces','Workspaces',0,true,'▣');
      if(expanded.has('workspaces')||q){
        const groups=workspaces.map(w=>({id:String(w.id),name:w.name||w.title||'Workspace',jobs:visibleJobs.filter(j=>String(j.workspaceId)===String(w.id))}));
        const other=visibleJobs.filter(j=>!workspaceNames.has(String(j.workspaceId)));
        if(other.length)groups.push({id:'other',name:'Other Jobs',jobs:other});
        for(const w of groups){
          const matching=w.jobs.filter(j=>matchesText(w.name,describe(j)));
          if(!matchesText(w.name)&&!matching.length)continue;
          const wid='w:'+w.id;html+=node(wid,w.name,1,true,'▰');
          if(expanded.has(wid)||q)for(const j of matching){
            const jid='j:'+j.id;html+=node(jid,describe(j),2,true,'▧');
            if(expanded.has(jid)||q){
              const data=cache.get(String(j.id));
              if(!data){html+=node('pending:'+j.id,'Select Job to load collections',3,false,'⋯');continue;}
              const names=['Unclassified',...new Set((data.collections||[]).map(c=>typeof c==='string'?c:c.name).filter(x=>x&&x!=='Unclassified'))];
              for(const name of names){
                const subjects=(data.subjects||[]).filter(x=>!x.isSystem&&(x.collection||'Unclassified')===name);
                if(!matchesText(name,describe(j))&&!subjects.some(x=>matchesText(x.name,x.reference,describe(j))))continue;
                const cid='c:'+j.id+':'+encodeURIComponent(name);html+=node(cid,name,3,subjects.length>0,'▱');
                if(expanded.has(cid)||q)for(const sub of subjects)if(matchesText(sub.name,sub.reference,name,describe(j)))html+=node('s:'+j.id+':'+sub.id,sub.name,4,false,'♙');
              }
            }
          }
        }
      }
      html+=node('legacy','Legacy',0,true,'▤');
      if(expanded.has('legacy')||q)html+=node('legacy:unclassified','Unclassified',1,false,'▱');
      html+=node('scratch','Scratch Space',0,false,'▱');
      tree.innerHTML=html;
      matches.textContent=visibleJobs.length+' matching Jobs';
    }
    search.oninput=renderTree;
    tree.onclick=async e=>{
      const button=e.target.closest('[data-tree-id]');if(!button)return;
      await selectDestination(button.dataset.treeId);
    };
    root.querySelector('#studioMediaQuickJob').onchange=e=>{if(e.target.value)void selectDestination('j:'+e.target.value);};
    async function selectDestination(id){
      const parts=id.split(':'),jobId=['j','c','s'].includes(parts[0])?parts[1]:'';
      if(['workspaces','legacy'].includes(id)||parts[0]==='w'){expanded.has(id)?expanded.delete(id):expanded.add(id);renderTree();return;}
      if(parts[0]==='pending')return;
      if(parts[0]==='j'||parts[0]==='c'){expanded.has(id)?expanded.delete(id):expanded.add(id);}
      selection=id;root.querySelector('#studioMediaQuickJob').value=jobId;StudioSubjectPicker.enhance(root.querySelector('#studioMediaQuickJob')).sync();job.innerHTML=jobId?'<option value="'+escMedia(jobId)+'">Selected Job</option>':'<option value="">Select Job</option>';job.value=jobId;collection.value='Unclassified';subject.value='';
      if(jobId){
        if(!cache.has(jobId)){try{cache.set(jobId,await json('/api/jobs/'+encodeURIComponent(jobId)+'/media'));}catch(error){progress.textContent=error.message||'Unable to load media.';}}
        await refresh();
        await loadUploadSessions(jobId);
        if(parts[0]==='c')collection.value=decodeURIComponent(parts.slice(2).join(':'));
        if(parts[0]==='s'){subject.value=parts[2];collection.value=subject.selectedOptions[0]?.dataset.collection||'Unclassified';}
      }else{gallery.innerHTML='<p class="muted">Select a Job to view its media.</p>';count.textContent='';}
      const j=jobs.find(x=>String(x.id)===jobId);
      path.textContent=jobId?[workspaceNames.get(String(j?.workspaceId))||'Workspace',describe(j||{}),parts[0]==='c'||parts[0]==='s'?collection.value:'',parts[0]==='s'?subject.selectedOptions[0]?.textContent:''].filter(Boolean).join(' / '):id.startsWith('legacy')?'Legacy / Unclassified':'Scratch Space';
      renderGallery();sync();renderTree();
    };
    renderTree();
    let selectedFiles=[],assets=[],media=null,pendingUpload=null,sessionJobId='',sessionReady=false;
    const sessionControl=root.querySelector('#studioMediaSession'),sessionLabel=root.querySelector('#studioMediaSessionLabel'),retryUpload=root.querySelector('#studioMediaRetryUpload');
    async function loadUploadSessions(jobId){
      if(sessionJobId===jobId&&sessionReady)return;
      sessionReady=false;sessionJobId=jobId;sessionControl.replaceChildren();sessionLabel.hidden=true;
      try{
        const sessions=await json('/api/jobs/'+encodeURIComponent(jobId)+'/shoot-sessions');
        if(job.value!==jobId||!root.isConnected)return;
        
        sessionControl.innerHTML=(sessions.length>1?'<option value="">Choose session before uploading</option>':'')+sessions.map(x=>'<option value="'+escMedia(x.id)+'">'+escMedia(x.title||x.name||x.planned_start_utc||'Photography session')+'</option>').join('');
        sessionLabel.hidden=sessions.length<2;sessionReady=true;
      }catch(error){if(job.value===jobId)progress.textContent='Upload sessions could not load: '+(error.message||'Please refresh and retry.');}
    }
    function sync(){
      const allowed=/^[jcs]:/.test(selection);
      notice.textContent=allowed?'Uploads will be tagged to the selected Job, Collection and optional Subject.':'Legacy and Scratch Space require server-side storage before uploads can be enabled.';
      drop.setAttribute('aria-disabled',String(!allowed||!job.value));
      root.querySelector('#studioMediaOpenJob').disabled=!job.value;
    }
    root.querySelector('#studioMediaChoose').onclick=()=>files.click();
    const drop=root.querySelector('#studioMediaDrop');
    let uploading=false;
    async function acceptFiles(incoming){
      if(uploading){progress.textContent='An upload is already in progress. Please wait.';return;}
      if(!job.value||!/^[jcs]:/.test(selection)){
        progress.textContent='Select a Job, Collection or Subject before uploading.';
        files.value='';return;
      }
      if(pendingUpload){progress.textContent='Retry the pending upload before selecting another batch.';return;}
      selectedFiles=Array.from(incoming||[]);
      if(!selectedFiles.length)return;
      await uploadFiles();
    }
    drop.addEventListener('dragover',event=>{event.preventDefault();if(!uploading)drop.classList.add('drag-over')});
    drop.addEventListener('dragleave',()=>drop.classList.remove('drag-over'));
    drop.addEventListener('drop',event=>{event.preventDefault();drop.classList.remove('drag-over');void acceptFiles(event.dataTransfer?.files)});
    files.onchange=()=>{void acceptFiles(files.files)};
    const fileSearch=root.querySelector('#studioMediaFileSearch'),statusFilter=root.querySelector('#studioMediaStatus'),sortControl=root.querySelector('#studioMediaSort'),detail=root.querySelector('#studioMediaDetail');
    const statusOf=a=>String(a.productionStatus||a.status||'Needs Review');
    const nameOf=a=>String(a.fileName||a.assetKey||'Media file');
    const assetIdOf=a=>String(a.id||a.assetId||a.assetKey||a.versionId||'');
    function visibleAssets(){
      let result=assets.slice();
      const parts=selection.split(':');
      if(parts[0]==='c'){const target=decodeURIComponent(parts.slice(2).join(':'));result=result.filter(a=>(a.collection||'Unclassified')===target);}
      if(parts[0]==='s')result=result.filter(a=>(a.subjects||a.subjectIds||[]).some(x=>String(typeof x==='object'?x.id:x)===parts[2]));
      const q=fileSearch.value.trim().toLowerCase();if(q)result=result.filter(a=>nameOf(a).toLowerCase().includes(q));
      const status=activeTab||statusFilter.value;if(status)result=result.filter(a=>statusOf(a).toLowerCase()===status.toLowerCase());
      if(sortControl.value==='name')result.sort((a,b)=>nameOf(a).localeCompare(nameOf(b)));
      else result.sort((a,b)=>String(b.createdAt||b.uploadedAt||b.createdUtc||'').localeCompare(String(a.createdAt||a.uploadedAt||a.createdUtc||'')));
      return result;
    }
    function renderGallery(){
      const shown=visibleAssets();count.textContent=shown.length+' of '+assets.length+' files';
      if(root.isConnected)window.StudioPage?.setCount(shown.length+' files');
      if(!shown.some(a=>assetIdOf(a)===selectedAssetId))selectedAssetId=shown.length?assetIdOf(shown[0]):'';
      const jobId=job.value;
      gallery.innerHTML=shown.length?shown.map(a=>{
        const id=assetIdOf(a),version=a.versionId||a.latestVersionId||a.thumbnailVersionId;
        const image=version&&(String(a.contentType||a.mimeType||'image/').startsWith('image/')||/\.(3fr|arw|cr2|cr3|crw|dng|erf|fff|iiq|kdc|mos|mrw|nef|nrw|orf|pef|raf|raw|rw2|rwl|sr2|srf|srw|x3f)$/i.test(String(a.fileName||nameOf(a)||'')));
        const preview=image?'/api/jobs/'+encodeURIComponent(jobId)+'/versions/by-id/'+encodeURIComponent(version)+'/preview?size=thumbnail':'';
        const fallback=image?'/api/jobs/'+encodeURIComponent(jobId)+'/versions/by-id/'+encodeURIComponent(version)+'/preview/fallback':'';
        return '<button type="button" class="studio-media-asset studio-media-asset-button'+(selectedAssetId===id?' selected':'')+'" data-media-asset="'+escMedia(id)+'">'+(image?'<img loading="lazy" src="'+preview+'" onerror="this.onerror=null;this.src=\''+fallback+'\'" alt="'+escMedia(nameOf(a))+'">':'<span class="studio-media-file-icon">▧</span>')+'<strong>'+escMedia(nameOf(a))+'</strong><small>'+escMedia(statusOf(a))+'</small></button>';
      }).join(''):'<p class="muted">No media in this location matching the filters.</p>';
      function showDetails(){
        const a=shown.find(x=>assetIdOf(x)===selectedAssetId);
        if(!a){detail.innerHTML='<h3>Selected Image</h3><p class="muted">Select an image to see its details.</p>';return;}
        const jobRecord=jobs.find(j=>String(j.id)===String(job.value));
        const currentCollection=a.collection||'Unclassified';
        const currentSubjects=(a.subjects||a.subjectIds||[]).map(x=>String(typeof x==='object'?x.id:x));
        const collectionNames=['Unclassified',...new Set((media?.collections||[]).map(c=>typeof c==='string'?c:c.name).filter(x=>x&&x!=='Unclassified'))];
        const collectionOptions=collectionNames.map(x=>'<option value="'+escMedia(x)+'"'+(x===currentCollection?' selected':'')+'>'+escMedia(x)+'</option>').join('');
        const subjects=(media?.subjects||[]).filter(x=>!x.isSystem);
        const version=a.versionId||a.latestVersionId||a.thumbnailVersionId;
        const image=version&&(String(a.contentType||a.mimeType||'image/').startsWith('image/')||/\.(3fr|arw|cr2|cr3|crw|dng|erf|fff|iiq|kdc|mos|mrw|nef|nrw|orf|pef|raf|raw|rw2|rwl|sr2|srf|srw|x3f)$/i.test(nameOf(a)));
        const versionUrl='/api/jobs/'+encodeURIComponent(job.value)+'/versions/by-id/'+encodeURIComponent(version);
        const bytes=Number(a.sizeBytes||a.size||0);
        const size=bytes?(typeof formatBytes==='function'?formatBytes(bytes):(bytes/1048576).toFixed(1)+' MB'):'';
        detail.innerHTML='<h3 class="studio-media-selected-heading">'+escMedia(nameOf(a))+'<small>'+escMedia([size,a.contentType||a.mimeType,a.version?'v'+a.version:''].filter(Boolean).join(' · '))+'</small></h3>'+(image?'<img class="studio-media-selected-preview" src="'+versionUrl+'/preview?size=preview" onerror="this.onerror=null;this.src=\''+versionUrl+'/preview/fallback\'" alt="'+escMedia(nameOf(a))+'">':'<div class="studio-media-selected-placeholder">'+escMedia(a.previewStatus||'File preview unavailable')+'</div>')+'<div class="studio-media-detail-fields"><label class="studio-media-job-field">Job<input type="text" value="'+escMedia(describe(jobRecord||{})||job.value)+'" readonly title="Changing the Job requires moving the asset between Jobs"></label><label>Collection<select id="studioMediaDetailCollection">'+collectionOptions+'</select></label><label>Subjects<select data-subject-picker multiple aria-label="Photo subjects" id="studioMediaDetailSubjects"></select></label><button type="button" class="primary" id="studioMediaSaveTags">Save Tags</button><button type="button" id="studioMediaDeletePhoto">Delete Photo</button><small id="studioMediaTagFeedback" role="status" aria-live="polite"></small>'+(version?'<a class="studio-media-original" href="'+versionUrl+'/content" target="_blank" rel="noopener">Open Original ↗</a>':'')+'</div>';
        const collectionInput=detail.querySelector('#studioMediaDetailCollection');
        const subjectList=detail.querySelector('#studioMediaDetailSubjects');
        subjectList.innerHTML=StudioSubjectPicker.options(subjects,currentSubjects);
        const save=detail.querySelector('#studioMediaSaveTags'),feedback=detail.querySelector('#studioMediaTagFeedback');
        detail.querySelector('#studioMediaDeletePhoto').onclick=async()=>{
          const assetId=a.id||a.assetId;
          if(!assetId){feedback.textContent='This file cannot be deleted without an asset identifier.';return;}
          if(!window.confirm('Remove this photograph from Studio Media? It can be restored by an authorized user.'))return;
          const button=detail.querySelector('#studioMediaDeletePhoto');
          button.disabled=true;feedback.textContent='Removing photograph…';
          try{
            await json('/api/jobs/'+encodeURIComponent(job.value)+'/media/assets/'+encodeURIComponent(assetId),{method:'DELETE'});
            selectedAssetId='';await refresh();renderTree();
          }catch(error){feedback.textContent=error.message||'Unable to remove photograph.';button.disabled=false;}
        };
        save.onclick=async()=>{
          const chosenCollection=collectionInput.value,chosenSubjects=Array.from(subjectList.selectedOptions,x=>x.value);
          const assetId=a.id||a.assetId;
          if(!assetId){feedback.textContent='This file has no editable asset identifier.';return;}
          save.disabled=true;feedback.textContent='Saving tags…';
          try{
            await json('/api/jobs/'+encodeURIComponent(job.value)+'/media/assets/'+encodeURIComponent(assetId),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({status:statusOf(a),collection:chosenCollection,subjects:chosenSubjects,assignedTo:a.assignedTo||null})});
            feedback.textContent='Tags saved.';
            a.collection=chosenCollection;a.subjects=chosenSubjects;
            await refresh();renderTree();
          }catch(error){feedback.textContent=error.message||'Unable to save tags.';}
          finally{save.disabled=false;}
        };
        gallery.querySelectorAll('[data-media-asset]').forEach(button=>button.classList.toggle('selected',button.dataset.mediaAsset===selectedAssetId));
      }
      gallery.querySelectorAll('[data-media-asset]').forEach(button=>button.onclick=()=>{selectedAssetId=button.dataset.mediaAsset;showDetails();});
      showDetails();
    }
    root.querySelector('#studioMediaPrevious').onclick=()=>gallery.scrollBy({left:-Math.max(240,gallery.clientWidth*.8),behavior:'smooth'});
    root.querySelector('#studioMediaNext').onclick=()=>gallery.scrollBy({left:Math.max(240,gallery.clientWidth*.8),behavior:'smooth'});
    fileSearch.oninput=renderGallery;statusFilter.onchange=()=>{activeTab='';root.querySelectorAll('[data-media-tab]').forEach(b=>b.classList.remove('active'));renderGallery();};
    sortControl.onchange=renderGallery;
    root.querySelectorAll('[data-media-tab]').forEach(b=>b.onclick=()=>{activeTab=b.dataset.mediaTab;statusFilter.value='';root.querySelectorAll('[data-media-tab]').forEach(x=>x.classList.toggle('active',x===b));renderGallery();});
    root.querySelector('#studioMediaRefresh').onclick=async()=>{await refresh();if(job.value)await loadUploadSessions(job.value);};
    retryUpload.onclick=()=>uploadFiles();
    async function refresh(){
      if(!job.value){assets=[];renderGallery();return;}
      gallery.innerHTML='<p class="muted">Loading media…</p>';
      try{
        media=await json('/api/jobs/'+encodeURIComponent(job.value)+'/media');
        cache.set(String(job.value),media);
        collection.innerHTML='<option value="Unclassified">Unclassified</option>'+Array.from(new Set((media.collections||[]).map(c=>typeof c==='string'?c:c.name).filter(Boolean))).filter(c=>c!=='Unclassified').map(c=>'<option value="'+escMedia(c)+'">'+escMedia(c)+'</option>').join('');
        subject.innerHTML='<option value="">No Subject</option>'+(media.subjects||[]).filter(x=>!x.isSystem).map(x=>'<option value="'+escMedia(x.id)+'" data-collection="'+escMedia(x.collection||'Unclassified')+'">'+escMedia(x.name)+'</option>').join('');
        assets=media.assets||[];renderGallery();
      }catch(error){gallery.innerHTML='<p role="alert">'+escMedia(error.message||'Unable to load media.')+'</p>';}
    }
    const openJobButton=root.querySelector('#studioMediaOpenJob');
    openJobButton.onclick=()=>{
      if(!job.value)return;
      if(typeof openWorkspaceJob!=='function'){progress.textContent='Job detail navigation is unavailable.';return;}
      Promise.resolve(openWorkspaceJob(job.value)).catch(error=>{progress.textContent=error.message||'Unable to open Job details.';});
    };

    collection.onchange=()=>{
      // A subject from a different collection must not be attached to new uploads.
      const selected=subject.selectedOptions[0];
      if(selected?.dataset.collection && selected.dataset.collection!==collection.value)subject.value='';
    };
    subject.onchange=()=>{const selected=subject.selectedOptions[0];if(selected?.dataset.collection)collection.value=selected.dataset.collection};
    async function uploadFiles(){
      if(uploading||(!pendingUpload&&(!job.value||!selectedFiles.length)))return;
      if(!pendingUpload){
        if(!sessionReady){progress.textContent='Refresh the job to load upload sessions before uploading.';return;}
        if(!sessionLabel.hidden&&!sessionControl.value){progress.textContent='Choose the photography session before uploading.';return;}
        if(selectedFiles.some(file=>file.size<1||file.size>100000000)){progress.textContent='Each file must contain between 1 byte and 100 MB.';selectedFiles=[];files.value='';return;}
        pendingUpload={jobId:job.value,collection:collection.value,subject:subject.value,files:selectedFiles.slice(),declaration:{batchId:crypto.randomUUID(),sessionId:sessionControl.value||null,collection:collection.value,subjectId:subject.value||null,returned:false,note:'Media Review & Upload',files:selectedFiles.map(file=>({uploadId:crypto.randomUUID(),fileName:file.name,sizeBytes:file.size}))}};
      }
      const batch=pendingUpload;uploading=true;retryUpload.hidden=true;drop.classList.add('uploading');
      let succeeded=0,complete=false;
      try{
        await json('/api/jobs/'+encodeURIComponent(batch.jobId)+'/media/upload-batches',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(batch.declaration)});
        for(let index=0;index<batch.files.length;index++){
          const file=batch.files[index];progress.textContent='Uploading '+(index+1)+' of '+batch.files.length+': '+file.name;
          const form=new FormData();form.append('file',file);form.append('uploadId',batch.declaration.files[index].uploadId);
          const result=await json('/api/jobs/'+encodeURIComponent(batch.jobId)+'/media/uploads',{method:'POST',body:form});
          if(batch.subject&&result.assetId)await json('/api/jobs/'+encodeURIComponent(batch.jobId)+'/media/assets/'+encodeURIComponent(result.assetId),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({status:'Needs Sorting',collection:batch.collection,subjects:[batch.subject],assignedTo:null})});
          succeeded++;
        }
        complete=true;pendingUpload=null;progress.textContent=succeeded+' files uploaded successfully.';
        if(job.value===batch.jobId){await refresh();renderTree();}
      }catch(error){progress.textContent=succeeded+' processed; '+(error.message||'Upload failed')+'. Retry uses the same upload identities.';}
      finally{selectedFiles=[];files.value='';uploading=false;drop.classList.remove('uploading');retryUpload.hidden=complete;sync();}
    }
    sync();
  };
})();
