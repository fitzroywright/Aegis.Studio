// Job Details owns the visible instructions and attachment index. Existing APIs enforce access.
window.StudioJobBrief={async mount(job,host,current){
 const esc=StudioPage.escape;
 const revision=host.studioBriefRevision=(host.studioBriefRevision||0)+1;
 const live=()=>current()&&host.studioBriefRevision===revision;
 host.innerHTML='<header><h2>Instructions & Files</h2><button type="button" class="jd-link" data-brief-refresh>Refresh</button></header><div class="jd-brief-columns"><section data-brief-instructions><h3>Instructions</h3><p>Loading instructions…</p></section><section data-brief-files><h3>Files</h3><p>Loading files…</p></section></div>';
 const refresh=()=>window.StudioJobBrief.mount(job,host,current);host.querySelector('[data-brief-refresh]').onclick=refresh;
 const results=await Promise.allSettled([json('/api/jobs/'+job.id+'/booking-instructions'),json('/api/jobs/'+job.id+'/notes'),json('/api/jobs/'+job.id+'/access'),job.conversationId?json('/api/conversations/'+job.conversationId+'/attachments'):Promise.resolve([]),json('/api/messages/threads')]);
 if(!live()||!host.isConnected)return;
 const instructions=host.querySelector('[data-brief-instructions]'),files=host.querySelector('[data-brief-files]');
 const texts=new Map();for(const index of [0,1])if(results[index].status==='fulfilled')for(const item of results[index].value||[]){const body=String(item.body||'').trim();if(body&&!texts.has(body))texts.set(body,item.source||'Job note');}
 instructions.innerHTML='<h3>Instructions</h3>'+[...texts].map(([body,source])=>'<article><small>'+esc(source)+'</small><p>'+esc(body)+'</p></article>').join('')+(results[0].status==='rejected'||results[1].status==='rejected'?'<p role="alert">Some instructions could not be loaded. Use Refresh to retry.</p>':texts.size?'':'<p>No special instructions recorded.</p>');
 const permission=results[2].status==='fulfilled'?(results[2].value.permissions||results[2].value):{};
 if(permission.editJob||permission.manageProduction){
  const form=document.createElement('form');form.innerHTML='<label>Add instruction<textarea name="body" required maxlength="10000"></textarea></label><button type="submit" class="jd-link">Save instruction</button><p role="alert"></p>';
  const noteId=crypto.randomUUID();instructions.append(form);form.onsubmit=async event=>{event.preventDefault();const button=form.querySelector('button');button.disabled=true;try{await json('/api/jobs/'+job.id+'/notes',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({id:noteId,body:form.elements.body.value})});if(live())await refresh();}catch(error){form.querySelector('[role=alert]').textContent=error.message;button.disabled=false;}};
 }
 const threads=results[4].status==='fulfilled'&&Array.isArray(results[4].value)?results[4].value.filter(t=>t.scopeType==='Job'&&String(t.scopeId)===String(job.id)):[];
 const attachments=await Promise.allSettled(threads.map(thread=>json('/api/messages/threads/'+thread.id+'/attachments')));
 if(!live()||!host.isConnected)return;
 const rows=new Map();if(results[3].status==='fulfilled')for(const file of results[3].value||[])rows.set(file.id,{...file,href:'/api/attachments/'+encodeURIComponent(file.id)});
 attachments.forEach((result,index)=>{if(result.status==='fulfilled')for(const file of result.value||[])rows.set(file.id,{...file,href:'/api/message-attachments/'+encodeURIComponent(file.id)});});
 files.innerHTML='<h3>Files</h3>'+[...rows.values()].map(file=>'<a class="jd-brief-file" href="'+esc(file.href)+'" target="_blank" rel="noopener">'+esc(file.fileName||file.name||'Attachment')+'</a>').join('')+(results[3].status==='rejected'||results[4].status==='rejected'||attachments.some(r=>r.status==='rejected')?'<p role="alert">Some files are unavailable or restricted. Use Refresh to retry.</p>':rows.size?'':'<p>No supporting files attached. Job photographs appear below.</p>');
 if(job.conversationId&&permission.addAttachments){const label=document.createElement('label');label.className='jd-link';label.textContent='Add supporting file ';const input=document.createElement('input');input.type='file';label.append(input);files.append(label);input.onchange=async()=>{if(!input.files[0])return;input.disabled=true;const data=new FormData();data.append('file',input.files[0]);try{await json('/api/conversations/'+job.conversationId+'/attachments?visibility=Internal',{method:'POST',body:data});if(live())await refresh();}catch(error){const message=document.createElement('p');message.setAttribute('role','alert');message.textContent=error.message;files.append(message);input.disabled=false;}};}
}};
