// Job Details owns the visible instructions and attachment index. Existing APIs enforce access.
window.StudioJobBrief={async mount(job,host,current){
 const esc=StudioPage.escape;
 const revision=host.studioBriefRevision=(host.studioBriefRevision||0)+1;
 const live=()=>current()&&host.studioBriefRevision===revision&&host.isConnected;
 const expanded=host.querySelector('[data-brief-toggle]')?.getAttribute('aria-expanded')==='true';
 const contentId='job-brief-'+job.id;
 host.innerHTML=`<header><button type="button" class="jd-link jd-brief-toggle" data-brief-toggle aria-expanded="${expanded}" aria-controls="${esc(contentId)}"><span aria-hidden="true">${expanded?'⌄':'›'}</span>Instructions & Files</button><span class="jd-brief-summary">Instructions <span data-instruction-count>…</span></span><button type="button" class="jd-link" data-add-instruction hidden>＋ Add instruction</button><span class="jd-brief-summary">Supporting files <span data-file-count>…</span></span><button type="button" class="jd-link" data-add-file hidden>＋ Add supporting file</button><button type="button" class="jd-link" data-brief-refresh>Refresh</button></header><div id="${esc(contentId)}" class="jd-brief-columns" ${expanded?'':'hidden'}><section data-brief-instructions><h3>Instructions</h3><p>Loading instructions…</p></section><section data-brief-files><h3>Supporting files</h3><p>Loading files…</p></section></div><p class="jd-brief-feedback" role="status" aria-live="polite"></p>`;
 const toggle=host.querySelector('[data-brief-toggle]'),columns=host.querySelector('.jd-brief-columns');
 const setExpanded=value=>{toggle.setAttribute('aria-expanded',String(value));toggle.querySelector('span').textContent=value?'⌄':'›';columns.hidden=!value;};
 toggle.onclick=()=>setExpanded(toggle.getAttribute('aria-expanded')!=='true');
 const refresh=()=>window.StudioJobBrief.mount(job,host,current);host.querySelector('[data-brief-refresh]').onclick=refresh;
 const results=await Promise.allSettled([json('/api/jobs/'+job.id+'/booking-instructions'),json('/api/jobs/'+job.id+'/notes'),json('/api/jobs/'+job.id+'/access'),job.conversationId?json('/api/conversations/'+job.conversationId+'/attachments'):Promise.resolve([]),json('/api/messages/threads')]);
 if(!live())return;
 const instructions=host.querySelector('[data-brief-instructions]'),files=host.querySelector('[data-brief-files]');
 const errors=[];
 const texts=new Map();for(const index of [0,1])if(results[index].status==='fulfilled')for(const item of results[index].value||[]){const body=String(item.body||'').trim();if(body&&!texts.has(body))texts.set(body,item.source||'Job note');}
 const instructionsFailed=results[0].status==='rejected'||results[1].status==='rejected';
 host.querySelector('[data-instruction-count]').textContent=texts.size+(instructionsFailed?' · unavailable':'');
 instructions.innerHTML='<h3>Instructions</h3>'+[...texts].map(([body,source])=>'<article><small>'+esc(source)+'</small><p>'+esc(body)+'</p></article>').join('')+(instructionsFailed?'<p role="alert">Some instructions could not be loaded. Use Refresh to retry.</p>':texts.size?'':'<p>No special instructions recorded.</p>');
 if(instructionsFailed)errors.push('Some instructions could not be loaded.');
 const permission=results[2].status==='fulfilled'?(results[2].value.permissions||results[2].value):{};
 if(results[2].status==='rejected')errors.push('Editing permissions could not be loaded.');
 if(permission.editJob||permission.manageProduction){
  const form=document.createElement('form');form.hidden=true;form.innerHTML='<label>Add instruction<textarea name="body" required maxlength="10000"></textarea></label><button type="submit" class="jd-link">Save instruction</button><button type="button" class="jd-link" data-cancel-instruction>Cancel</button><p role="alert"></p>';
  const noteId=crypto.randomUUID();instructions.append(form);
  const add=host.querySelector('[data-add-instruction]');add.hidden=false;add.setAttribute('aria-expanded','false');add.onclick=()=>{setExpanded(true);form.hidden=false;add.setAttribute('aria-expanded','true');form.elements.body.focus();};
  form.querySelector('[data-cancel-instruction]').onclick=()=>{form.hidden=true;add.setAttribute('aria-expanded','false');add.focus();};
  form.onsubmit=async event=>{event.preventDefault();if(!live())return;const button=form.querySelector('[type=submit]');button.disabled=true;try{await json('/api/jobs/'+job.id+'/notes',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:noteId,body:form.elements.body.value})});if(live())await refresh();}catch(error){if(live()){form.querySelector('[role=alert]').textContent=error.message;button.disabled=false;}}};
 }
 const threads=results[4].status==='fulfilled'&&Array.isArray(results[4].value)?results[4].value.filter(t=>t.scopeType==='Job'&&String(t.scopeId)===String(job.id)):[];
 const attachments=await Promise.allSettled(threads.map(thread=>json('/api/messages/threads/'+thread.id+'/attachments')));
 if(!live())return;
 const rows=new Map();if(results[3].status==='fulfilled')for(const file of results[3].value||[])rows.set(file.id,{...file,href:'/api/attachments/'+encodeURIComponent(file.id)});
 attachments.forEach(result=>{if(result.status==='fulfilled')for(const file of result.value||[])rows.set(file.id,{...file,href:'/api/message-attachments/'+encodeURIComponent(file.id)});});
 const filesFailed=results[3].status==='rejected'||results[4].status==='rejected'||attachments.some(r=>r.status==='rejected');
 host.querySelector('[data-file-count]').textContent=rows.size+(filesFailed?' · unavailable':'');
 files.innerHTML='<h3>Supporting files</h3>'+[...rows.values()].map(file=>'<a class="jd-brief-file" href="'+esc(file.href)+'" target="_blank" rel="noopener">'+esc(file.fileName||file.name||'Attachment')+'</a>').join('')+(filesFailed?'<p role="alert">Some files are unavailable or restricted. Use Refresh to retry.</p>':rows.size?'':'<p>No supporting files attached. Job photographs appear below.</p>');
 if(filesFailed)errors.push('Some supporting files could not be loaded.');
 host.querySelector('.jd-brief-feedback').textContent=errors.length?errors.join(' ')+' Use Refresh to retry.':'';
 if(job.conversationId&&permission.addAttachments){
  const input=document.createElement('input');input.type='file';input.hidden=true;input.setAttribute('aria-label','Add supporting file');files.append(input);
  const add=host.querySelector('[data-add-file]');add.hidden=false;add.onclick=()=>input.click();
  input.onchange=async()=>{if(!input.files[0]||!live())return;input.disabled=true;add.disabled=true;const data=new FormData();data.append('file',input.files[0]);try{await json('/api/conversations/'+job.conversationId+'/attachments?visibility=Internal',{method:'POST',body:data});if(live())await refresh();}catch(error){if(live()){setExpanded(true);const message=document.createElement('p');message.setAttribute('role','alert');message.textContent=error.message;files.append(message);input.disabled=false;add.disabled=false;}}};
 }
}};
