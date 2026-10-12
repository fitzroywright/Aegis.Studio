// Canonical always-open message card and job Notes editor.
(() => {
 'use strict';
 const escape=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 function html({id,title='Messages & Announcements',body='',footer='',actions='',attributes=''}){
  return `<section id="${escape(id)}" class="smc-card" ${attributes}><header class="smc-header"><h2 class="smc-title" data-message-title="${escape(title)}"><span aria-hidden="true">✉</span><strong>${escape(title)}</strong></h2><div class="smc-actions"><button type="button" data-smc-notes-toggle aria-pressed="false" aria-controls="${escape(id)}-notes">✎ Notes</button><span data-smc-message-actions>${actions}</span></div></header><div class="smc-content" data-smc-messages><div class="smc-feed">${body}</div>${footer?`<footer class="smc-footer">${footer}</footer>`:''}</div><div id="${escape(id)}-notes" class="smc-notes" hidden></div></section>`;
 }
 function create(options){const template=document.createElement('template');template.innerHTML=html(options);return template.content.firstElementChild;}
 function state(card){return card.__studioMessageCard??=( {version:0,jobId:card.dataset.messageJobId||'',loaded:false,notes:[],permissions:{},userName:''} );}
 const live=(card,s,version)=>card.isConnected&&s.version===version;
 const firstName=name=>window.studioFirstName?window.studioFirstName(name):String(name||'Studio');
 function renderNotes(card,s){
  const host=card.querySelector('[data-smc-note-editor]');if(!host)return;
  const canAdd=!!(s.permissions.editJob||s.permissions.manageProduction);
  const canEdit=note=>canAdd&&(s.permissions.editJob||String(note.createdBy).toLowerCase()===s.userName.toLowerCase());
  host.innerHTML='<div class="smc-note-list">'+(s.notes.map(note=>`<article class="smc-note"><header><strong>${escape(firstName(note.createdBy))}</strong><time datetime="${escape(note.createdAtUtc)}">${escape(new Date(note.createdAtUtc).toLocaleString())}</time>${canEdit(note)?`<button type="button" data-edit-note="${escape(note.id)}">Edit</button>`:''}</header><p>${escape(note.body)}</p>${note.updatedAtUtc?`<small>Edited by ${escape(firstName(note.updatedBy))} · ${escape(new Date(note.updatedAtUtc).toLocaleString())}</small>`:''}</article>`).join('')||'<p>No notes yet.</p>')+'</div>'+(canAdd?'<form class="smc-note-form"><label data-note-label>New note<textarea name="body" maxlength="10000" required placeholder="Add a note to this job…"></textarea></label><div><button type="submit" data-save-note>Save note</button><button type="button" data-cancel-edit hidden>Cancel edit</button><button type="button" data-reload-notes>Reload notes</button></div><p role="status" aria-live="polite"></p></form>':'');
  const form=host.querySelector('form');if(!form)return;
  let editing=null,pendingNote=null,saving=false;
  const output=form.querySelector('[role=status]'),save=form.querySelector('[data-save-note]'),cancel=form.querySelector('[data-cancel-edit]');
  host.querySelectorAll('[data-edit-note]').forEach(button=>button.onclick=()=>{
   if(saving||pendingNote)return;
   editing=s.notes.find(note=>note.id===button.dataset.editNote);form.elements.body.value=editing.body;
   form.querySelector('[data-note-label]').firstChild.textContent='Edit note';save.textContent='Save changes';cancel.hidden=false;form.elements.body.focus();
  });
  cancel.onclick=()=>{if(saving)return;editing=null;pendingNote=null;form.elements.body.value='';form.elements.body.readOnly=false;form.querySelector('[data-note-label]').firstChild.textContent='New note';save.textContent='Save note';cancel.hidden=true;output.textContent='';};
  form.querySelector('[data-reload-notes]').onclick=()=>{if(saving)return;loadJobNotes(card,s,true);};
  form.onsubmit=async event=>{
   event.preventDefault();const body=form.elements.body.value.trim();if(!body||saving)return;
   const version=s.version,jobId=s.jobId;saving=true;save.disabled=true;cancel.disabled=true;form.elements.body.readOnly=true;output.textContent='Saving…';
   try{
    if(editing){await json(`/api/jobs/${jobId}/notes/${editing.id}`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({body,expectedRevision:editing.revision})});}
    else {pendingNote??={id:crypto.randomUUID(),body};await json(`/api/jobs/${jobId}/notes`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(pendingNote)});}
    pendingNote=null;
    if(!live(card,s,version))return;
    form.elements.body.value='';editing=null;cancel.hidden=true;output.textContent='Note saved.';
    const notes=await json(`/api/jobs/${jobId}/notes`);if(!live(card,s,version))return;
    s.notes=notes;renderNotes(card,s);card.querySelector('.smc-note-form [role=status]').textContent='Note saved.';
   }catch(error){if(live(card,s,version))output.textContent=error.message||String(error);}
   finally{if(live(card,s,version)){saving=false;save.disabled=false;cancel.disabled=false;form.elements.body.readOnly=!!pendingNote;}}
  };
 }
 async function loadJobNotes(card,s,force=false){
  if(s.loading||(!force&&s.loaded))return;
  const host=card.querySelector('[data-smc-note-editor]');if(!host)return;
  if(!s.jobId){host.innerHTML='<p>Choose a job to view its notes.</p>';return;}
  const version=++s.version;s.loading=true;host.innerHTML='<p role="status">Loading notes…</p>';
  try{
   const [notes,access,profile]=await Promise.all([json(`/api/jobs/${s.jobId}/notes`),json(`/api/jobs/${s.jobId}/access`),json('/api/auth/me')]);
   if(!live(card,s,version))return;
   s.notes=notes;s.permissions=access.permissions||access;s.userName=(profile.user||profile).userName||'';s.loaded=true;renderNotes(card,s);
  }catch(error){if(live(card,s,version)){host.innerHTML='<p role="alert"></p><button type="button" data-notes-retry>Retry</button>';host.querySelector('p').textContent=error.message||String(error);host.querySelector('button').onclick=()=>loadJobNotes(card,s,true);}}
  finally{if(s.version===version)s.loading=false;}
 }
 async function ensureNotes(card){
  const s=state(card),panel=card.querySelector('.smc-notes');
  if(panel.querySelector('[data-smc-note-editor]'))return loadJobNotes(card,s);
  panel.innerHTML=(card.dataset.messageJobId?'':'<label class="smc-note-job">Job<select data-notes-job aria-label="Choose a job for Notes"><option value="">Choose a job…</option></select></label>')+'<div data-smc-note-editor></div>';
  const select=panel.querySelector('[data-notes-job]');
  if(select){
   const version=++s.version;select.disabled=true;
   try{
    const jobs=await json('/api/jobs');if(!live(card,s,version))return;
    select.innerHTML='<option value="">Choose a job…</option>'+jobs.map(job=>`<option value="${escape(job.id)}">${escape(job.title)}${job.clientName?' · '+escape(job.clientName):''}</option>`).join('');
    select.disabled=false;select.onchange=()=>{s.version++;s.jobId=select.value;s.loaded=false;s.loading=false;return loadJobNotes(card,s);};
   }catch(error){if(live(card,s,version)){panel.innerHTML='<p role="alert"></p><button type="button">Retry</button>';panel.querySelector('p').textContent=error.message||String(error);panel.querySelector('button').onclick=()=>ensureNotes(card);return;}}
  }
  return loadJobNotes(card,s);
 }
 function setMode(card,mode){
  if(!card)return;
  const notes=mode==='notes';card.dataset.smcMode=notes?'notes':'messages';
  card.querySelector('[data-smc-messages]').hidden=notes;card.querySelector('.smc-notes').hidden=!notes;
  const toggle=card.querySelector('[data-smc-notes-toggle]');toggle.setAttribute('aria-pressed',String(notes));toggle.textContent=notes?'✉ Messages':'✎ Notes';
  const title=card.querySelector('.smc-title');title.querySelector('strong').textContent=notes?'Job Notes':title.dataset.messageTitle;
  card.querySelector('[data-smc-message-actions]').hidden=notes;
  if(notes)return ensureNotes(card);
 }
 function bind(card){if(!card)return;state(card);card.querySelector('[data-smc-notes-toggle]').onclick=()=>setMode(card,card.dataset.smcMode==='notes'?'messages':'notes');}
 window.StudioMessageCard={html,create,bind,setMode};
})();
