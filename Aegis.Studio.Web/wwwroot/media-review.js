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
      <section class="studio-media-panel"><h2>Upload Destination</h2>
        <div class="studio-media-tree-layout">
          <div><input id="studioMediaJobSearch" type="search" placeholder="Search Workspaces, Jobs, Collections, Subjects…" aria-label="Search media destinations"><div id="studioMediaTree" class="studio-media-tree" role="tree"></div><small id="studioMediaJobMatches"></small></div>
          <div class="studio-media-tree-summary"><strong id="studioMediaPath">Select a destination</strong><p id="studioMediaDestinationNotice"></p><button type="button" class="action" id="studioMediaOpenJob" disabled>Open Job Details ↗</button></div>
        </div>
        <div class="studio-media-targets" hidden><select id="studioMediaJob"><option value="">Select Job</option></select><select id="studioMediaCollection"><option value="Unclassified">Unclassified</option></select><select id="studioMediaSubject"><option value="">No Subject</option></select></div>
      </section>
      <section class="studio-media-panel studio-media-upload"><h2>Upload Files</h2>
        <div class="studio-media-drop" id="studioMediaDrop" tabindex="0"><span aria-hidden="true">⇧</span><strong>Drag files here to upload</strong><p>Or choose images and other media from your device</p><button type="button" class="primary" id="studioMediaChoose">Choose Files</button><input type="file" id="studioMediaFiles" multiple hidden></div>
        <div class="studio-media-upload-actions"><span id="studioMediaProgress" role="status">Select a Job and files to begin.</span><button class="primary" id="studioMediaUpload" type="button" disabled>Upload Selected Files</button></div>
      </section>
      <section class="studio-media-panel"><header class="studio-media-gallery-header"><h2>Media Library</h2><span id="studioMediaCount"></span></header>
        <div class="studio-media-gallery" id="studioMediaGallery"><p class="muted">Select a Job to view its media.</p></div>
      </section></div>`;
    const root=genericView.querySelector('.studio-media-page'),job=root.querySelector('#studioMediaJob'),collection=root.querySelector('#studioMediaCollection'),subject=root.querySelector('#studioMediaSubject'),files=root.querySelector('#studioMediaFiles'),upload=root.querySelector('#studioMediaUpload'),progress=root.querySelector('#studioMediaProgress'),gallery=root.querySelector('#studioMediaGallery'),count=root.querySelector('#studioMediaCount'),notice=root.querySelector('#studioMediaDestinationNotice');
    const search=root.querySelector('#studioMediaJobSearch'),matches=root.querySelector('#studioMediaJobMatches'),tree=root.querySelector('#studioMediaTree'),path=root.querySelector('#studioMediaPath');
    const workspaceNames=new Map(workspaces.map(w=>[String(w.id),w.name||w.title||'Workspace']));
    const describe=j=>[j.jobNumber||j.reference||'',j.title||j.name||'Job',j.clientName||j.customerName||''].filter(Boolean).join(' · ');
    const expanded=new Set(['workspaces']),cache=new Map();
    let selection='';
    function renderTree(){
      const q=search.value.trim().toLowerCase();
      const matchesText=(...parts)=>!q||parts.join(' ').toLowerCase().includes(q);
      const node=(id,name,depth,children,icon)=>'<button type="button" role="treeitem" data-tree-id="'+escMedia(id)+'" class="studio-media-tree-node'+(selection===id?' selected':'')+'" style="padding-left:'+(12+depth*20)+'px"><span>'+ (children?(expanded.has(id)||q?'⌄':'›'):'·') +'</span><span>'+icon+'</span><span>'+escMedia(name)+'</span></button>';
      let html=node('workspaces','Workspaces',0,true,'▣');
      if(expanded.has('workspaces')||q){
        const groups=workspaces.map(w=>({id:String(w.id),name:w.name||w.title||'Workspace',jobs:jobs.filter(j=>String(j.workspaceId)===String(w.id))}));
        const other=jobs.filter(j=>!workspaceNames.has(String(j.workspaceId)));
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
                if(!matchesText(name,describe(j))&&!subjects.some(x=>matchesText(x.name,describe(j))))continue;
                const cid='c:'+j.id+':'+encodeURIComponent(name);html+=node(cid,name,3,subjects.length>0,'▱');
                if(expanded.has(cid)||q)for(const sub of subjects)if(matchesText(sub.name,name,describe(j)))html+=node('s:'+j.id+':'+sub.id,sub.name,4,false,'♙');
              }
            }
          }
        }
      }
      html+=node('legacy','Legacy',0,true,'▤');
      if(expanded.has('legacy')||q)html+=node('legacy:unclassified','Unclassified',1,false,'▱');
      html+=node('scratch','Scratch Space',0,false,'▱');
      tree.innerHTML=html;
      matches.textContent=jobs.length+' accessible Jobs';
    }
    search.oninput=renderTree;
    tree.onclick=async e=>{
      const button=e.target.closest('[data-tree-id]');if(!button)return;
      const id=button.dataset.treeId,parts=id.split(':'),jobId=['j','c','s'].includes(parts[0])?parts[1]:'';
      if(['workspaces','legacy'].includes(id)||parts[0]==='w'){expanded.has(id)?expanded.delete(id):expanded.add(id);renderTree();return;}
      if(parts[0]==='pending')return;
      if(parts[0]==='j'||parts[0]==='c'){expanded.has(id)?expanded.delete(id):expanded.add(id);}
      selection=id;job.value=jobId;collection.value='Unclassified';subject.value='';
      if(jobId){
        if(!cache.has(jobId)){try{cache.set(jobId,await json('/api/jobs/'+encodeURIComponent(jobId)+'/media'));}catch(error){progress.textContent=error.message||'Unable to load media.';}}
        await refresh();
        if(parts[0]==='c')collection.value=decodeURIComponent(parts.slice(2).join(':'));
        if(parts[0]==='s'){subject.value=parts[2];collection.value=subject.selectedOptions[0]?.dataset.collection||'Unclassified';}
      }else{gallery.innerHTML='<p class="muted">Select a Job to view its media.</p>';count.textContent='';}
      const j=jobs.find(x=>String(x.id)===jobId);
      path.textContent=jobId?[workspaceNames.get(String(j?.workspaceId))||'Workspace',describe(j||{}),parts[0]==='c'||parts[0]==='s'?collection.value:'',parts[0]==='s'?subject.selectedOptions[0]?.textContent:''].filter(Boolean).join(' / '):id.startsWith('legacy')?'Legacy / Unclassified':'Scratch Space';
      sync();renderTree();
    };
    renderTree();
    let selectedFiles=[],assets=[],media=null;
    function sync(){
      const allowed=/^[jcs]:/.test(selection);
      notice.textContent=allowed?'Uploads will be tagged to the selected Job, Collection and optional Subject.':'Legacy and Scratch Space require server-side storage before uploads can be enabled.';
      upload.disabled=!allowed||!job.value||!selectedFiles.length;
      root.querySelector('#studioMediaOpenJob').disabled=!job.value;
    }
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
        cache.set(String(job.value),media);
        collection.innerHTML='<option value="Unclassified">Unclassified</option>'+Array.from(new Set((media.collections||[]).map(c=>typeof c==='string'?c:c.name).filter(Boolean))).filter(c=>c!=='Unclassified').map(c=>'<option value="'+escMedia(c)+'">'+escMedia(c)+'</option>').join('');
        subject.innerHTML='<option value="">No Subject</option>'+(media.subjects||[]).filter(s=>!s.isSystem).map(s=>'<option value="'+escMedia(s.id)+'" data-collection="'+escMedia(s.collection||'Unclassified')+'">'+escMedia(s.name)+'</option>').join('');
        assets=media.assets||[];count.textContent=assets.length+' files';
        gallery.innerHTML=assets.length?assets.map(a=>'<article class="studio-media-asset">'+(a.versionId&&String(a.contentType||'').startsWith('image/')?'<img loading="lazy" src="/api/jobs/'+encodeURIComponent(job.value)+'/versions/by-id/'+encodeURIComponent(a.versionId)+'/content" alt="'+escMedia(a.fileName||'Image')+'">':'<div class="studio-media-file-icon">▧</div>')+'<strong>'+escMedia(a.fileName||a.assetKey||'Media file')+'</strong><small>'+escMedia(a.productionStatus||a.status||'Needs Review')+'</small></article>').join(''):'<p class="muted">No media uploaded for this Job.</p>';
      }catch(error){gallery.innerHTML='<p role="alert">'+escMedia(error.message||'Unable to load media.')+'</p>';}
    }
    const openJobButton=root.querySelector('#studioMediaOpenJob');
    openJobButton.onclick=()=>{
      if(!job.value)return;
      if(typeof openWorkspaceJob!=='function'){progress.textContent='Job detail navigation is unavailable.';return;}
      Promise.resolve(openWorkspaceJob(job.value)).catch(error=>{progress.textContent=error.message||'Unable to open Job details.';});
    };

    subject.onchange=()=>{const selected=subject.selectedOptions[0];if(selected?.dataset.collection)collection.value=selected.dataset.collection};
    upload.onclick=async()=>{
      if(!job.value||!selectedFiles.length||upload.disabled)return;
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
        progress.textContent=succeeded+' files uploaded successfully.';selectedFiles=[];files.value='';await refresh();renderTree();
      }catch(error){progress.textContent=succeeded+' uploaded; '+(error.message||'Upload failed')+'. Remaining files were not uploaded.';}
      finally{sync()}
    };
    sync();
  };
})();
