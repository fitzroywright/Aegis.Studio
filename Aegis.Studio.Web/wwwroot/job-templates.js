/* Job Templates page; the graph editor retains draft/version ownership. */
window.StudioJobTemplates=(()=>{
 'use strict';
 const esc=StudioPage.escape;
 async function render(){
  const current=StudioPage.begin('job-templates');
  const [templates,drafts]=await Promise.all([json('/api/admin/commercial/templates/Job'),json('/api/admin/workflow-templates/drafts')]);if(!current())return;
  const rows=templates.map(t=>({...t,draft:drafts.find(d=>d.templateId===t.id)}));
  rows.push(...drafts.filter(d=>!templates.some(t=>t.id===d.templateId)).map(d=>({id:d.templateId,version:0,definition:d.definition,draft:d,active:d.active})));
  commercialPage('Job Templates',`<div class="commercial-grid"><table class="pos-grid"><thead><tr><th>Job Template</th><th>Published</th><th>Draft</th><th>Status</th></tr></thead><tbody>${rows.map(t=>`<tr tabindex="0" data-template="${t.id}"><td><b>${esc(t.definition.name)}</b><small>${esc(t.definition.description)}</small></td><td>${t.version?'v'+t.version:'Not published'}</td><td>${t.draft?'Revision '+t.draft.revision:'No draft'}</td><td>${t.active?'Active':'Retired'}</td></tr>`).join('')||'<tr><td colspan="4">Create your first Job Template.</td></tr>'}</tbody></table></div>`,`<button class="primary" data-create>Create Job Template</button>`);
  genericView.querySelector('[data-create]').onclick=()=>StudioGraphTemplates.edit(null);
  genericView.querySelectorAll('[data-template]').forEach(el=>{el.onclick=()=>StudioGraphTemplates.edit(rows.find(t=>t.id===el.dataset.template));el.onkeydown=e=>{if(e.key==='Enter')el.click();};});StudioPage.setCount(rows.length+' templates');
 }
 return {render};
})();
