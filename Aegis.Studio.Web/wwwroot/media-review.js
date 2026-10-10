/* Studio Media Review & Upload: full-width operational workspace. */
(function(){
  const escMedia=value=>String(value??'').replace(/[&<>"']/g,ch=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch]));
  let revision=0;
  window.showMediaReview=async function(){
    const current=++revision;
    conversationView.classList.add('hidden');genericView.classList.remove('hidden');
    genericView.innerHTML='<div class="today-loading">Loading Media Review & Upload…</div>';
    let jobs=[];
    try{
      const result=await json('/api/today/jobs');
      jobs=Array.isArray(result)?result:Array.isArray(result?.jobs)?result.jobs:[];
    }catch(error){
      if(current!==revision)return;
      genericView.innerHTML='<section class="job-section"><h2>Media Review & Upload</h2><p role="alert">'+escMedia(error.message||'Unable to load jobs.')+'</p></section>';return;
    }
    if(current!==revision)return;
    genericView.innerHTML=`<div class="studio-media-page">
      <header class="studio-media-heading"><div><h1>Media Review &amp; Upload</h1><p>Upload, organise and review Job media and source files.</p></div></header>
      <section class="studio-media-panel"><h2>Upload Destination</h2>
        <div class="studio-media-destinations">
          <label><input type="radio" name="mediaDestination" value="job" checked><strong>Job</strong><small>Upload to a Job, Collection and Subject</small></label>
          <label><input type="radio" name="mediaDestination" value="scratch"><strong>Scratch Area</strong><small>Temporary sorting and staging</small></label>
          <label><input type="radio" name="mediaDestination" value="legacy"><strong>Legacy Area</strong><small>Historical archive</small></label>
        </div>
        <div id="studioMediaDestinationNotice" class="studio-media-notice" hidden></div>
        <div class="studio-media-targets">
          <label>Job<select id="studioMediaJob"><option value="">Select a Job…</option>${jobs.map(j=>'<option value="'+escMedia(j.id)+'">'+escMedia(j.title||j.name||j.clientName||j.id)+'</option>').join('')}</select></label>
          <label>Collection<select id="studioMediaCollection"><option value="Unclassified">Unclassified</option></select></label>
          <label>Subject<select id="studioMediaSubject"><option value="">No Subject</option></select></label>
        </div>
      </section>
      <section class="studio-media-panel studio-media-upload"><h2>Upload Files</h2>
        <div class="studio-media-drop" id="studioMediaDrop" tabindex="0"><span aria-hidden="true">⇧</span><strong>Drag files here to upload</strong><p>Or choose images and other media from your device</p><button type="button" class="primary" id="studioMediaChoose">Choose Files</button><input type="file" id="studioMediaFiles" multiple hidden></div>
        <div class="studio-media-upload-actions"><span id="studioMediaProgress" role="status">Select a Job and files to begin.</span><button class="primary" id="studioMediaUpload" type="button" disabled>Upload Selected Files</button></div>
      </section>
      <section class="studio-media-panel"><header class="studio-media-gallery-header"><h2>Media Library</h2><span id="studioMediaCount"></span></header>
        <div class="studio-media-gallery" id="studioMediaGallery"><p class="muted">Select a Job to view its media.</p></div>
      </section></div>`;
    const root=genericView.querySelector('.studio-media-page'),job=root.querySelector('#studioMediaJob'),collection=root.querySelector('#studioMediaCollection'),subject=root.querySelector('#studioMediaSubject'),files=root.querySelector('#studioMediaFiles'),upload=root.querySelector('#studioMediaUpload'),progress=root.querySelector('#studioMediaProgress'),gallery=root.querySelector('#studioMediaGallery'),count=root.querySelector('#studioMediaCount'),notice=root.querySelector('#studioMediaDestinationNotice');
    let selectedFiles=[],assets=[],media=null;
    const destination=()=>root.querySelector('input[name="mediaDestination"]:checked').value;
    function sync(){
      const isJob=destination()==='job';
      root.querySelector('.studio-media-targets').hidden=!isJob;
      root.querySelector('.studio-media-upload').hidden=!isJob;
      notice.hidden=isJob;
      if(!isJob)notice.textContent=(destination()==='scratch'?'Scratch Area':'Legacy Area')+' requires shared server-side storage and transfer auditing. Uploads are unavailable until that storage is commissioned; files will not be silently saved to this browser or misfiled under a Job.';
      upload.disabled=!isJob||!job.value||!selectedFiles.length;
    }
    root.querySelectorAll('input[name="mediaDestination"]').forEach(input=>input.onchange=sync);
    root.querySelector('#studioMediaChoose').onclick=()=>files.click();
    const drop=root.querySelector('#studioMediaDrop');
    drop.addEventListener('dragover',event=>{event.preventDefault();drop.classList.add('drag-over')});
    drop.addEventListener('dragleave',()=>drop.classList.remove('drag-over'));
    drop.addEventListener('drop',event=>{event.preventDefault();drop.classList.remove('drag-over');selectedFiles=Array.from(event.dataTransfer.files);progress.textContent=selectedFiles.length+' files ready to upload.';sync()});
    files.onchange=()=>{selectedFiles=Array.from(files.files);progress.textContent=selectedFiles.length+' files ready to upload.';sync()};
    async function refresh(){
      if(!job.value){gallery.innerHTML='<p class="muted">Select a Job to view its media.</p>';count.textContent='';return;}
      gallery.innerHTML='<p class="muted">Loading media…</p>';
      try{
        media=await json('/api/jobs/'+encodeURIComponent(job.value)+'/media');
        collection.innerHTML='<option value="Unclassified">Unclassified</option>'+Array.from(new Set((media.collections||[]).map(c=>typeof c==='string'?c:c.name).filter(Boolean))).filter(c=>c!=='Unclassified').map(c=>'<option value="'+escMedia(c)+'">'+escMedia(c)+'</option>').join('');
        subject.innerHTML='<option value="">No Subject</option>'+(media.subjects||[]).filter(s=>!s.isSystem).map(s=>'<option value="'+escMedia(s.id)+'" data-collection="'+escMedia(s.collection||'Unclassified')+'">'+escMedia(s.name)+'</option>').join('');
        assets=media.assets||[];count.textContent=assets.length+' files';
        gallery.innerHTML=assets.length?assets.map(a=>'<article class="studio-media-asset">'+(a.versionId&&String(a.contentType||'').startsWith('image/')?'<img loading="lazy" src="/api/jobs/'+encodeURIComponent(job.value)+'/versions/by-id/'+encodeURIComponent(a.versionId)+'/content" alt="'+escMedia(a.fileName||'Image')+'">':'<div class="studio-media-file-icon">▧</div>')+'<strong>'+escMedia(a.fileName||a.assetKey||'Media file')+'</strong><small>'+escMedia(a.productionStatus||a.status||'Needs Review')+'</small></article>').join(''):'<p class="muted">No media uploaded for this Job.</p>';
      }catch(error){gallery.innerHTML='<p role="alert">'+escMedia(error.message||'Unable to load media.')+'</p>';}
    }
    job.onchange=()=>{sync();refresh()};
    subject.onchange=()=>{const selected=subject.selectedOptions[0];if(selected?.dataset.collection)collection.value=selected.dataset.collection};
    upload.onclick=async()=>{
      if(!job.value||!selectedFiles.length||destination()!=='job')return;
      upload.disabled=true;const jobId=job.value,chosenCollection=collection.value,chosenSubject=subject.value;
      let succeeded=0;
      try{
        for(const file of selectedFiles){
          progress.textContent='Uploading '+(succeeded+1)+' of '+selectedFiles.length+': '+file.name;
          const form=new FormData();form.append('file',file);form.append('returned','false');form.append('note','Media Review & Upload');form.append('collection',chosenCollection);
          const result=await json('/api/jobs/'+encodeURIComponent(jobId)+'/media/uploads',{method:'POST',body:form});
          if(chosenSubject&&result.assetId)await json('/api/jobs/'+encodeURIComponent(jobId)+'/media/assets/'+encodeURIComponent(result.assetId),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({status:'Needs Sorting',collection:chosenCollection,subjects:[chosenSubject],assignedTo:null})});
          succeeded++;
        }
        progress.textContent=succeeded+' files uploaded successfully.';selectedFiles=[];files.value='';await refresh();
      }catch(error){progress.textContent=succeeded+' uploaded; '+(error.message||'Upload failed')+'. Remaining files were not uploaded.';}
      finally{sync()}
    };
    sync();
  };
})();
