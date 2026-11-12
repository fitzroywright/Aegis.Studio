const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const tick=()=>new Promise(r=>setImmediate(r));
function setup(t){
 const dom=new JSDOM('<main id="genericView"></main><div id="conversationView"></div>',{runScripts:'outside-only',url:'https://studio.test'});t.after(()=>dom.window.close());
 const w=dom.window,calls=[],templates=[],workspaces=[{id:'w1',name:'Commercial'},{id:'w2',name:'Events'}];let failure=false;
 w.StudioPage={escape:v=>String(v??'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('"','&quot;'),begin:()=>()=>true};
 w.json=async(url,options)=>{calls.push({url,options});if(options){if(failure)throw Error('Changed on server');const payload=JSON.parse(options.body);return {id:url.split('/').at(-1),revision:1,...payload};}if(url==='/api/workspaces')return workspaces;return templates;};
 w.studioDialog=(title,html)=>{const d=w.document.createElement('dialog');d.innerHTML=html;d.close=()=>d.remove();w.document.body.append(d);return d;};
 w.eval(fs.readFileSync(path.join(root,'session-plan-templates.js'),'utf8'));
 return {w,calls,templates,workspaces,fail:()=>failure=true};
}
const definition=()=>({name:'Campaign',description:'Any structure',workspaceIds:[],categories:[{name:'Performers',subjectKind:'Individual',groupingLevels:['Region','Cast'],identificationFields:[{name:'Permit',required:true}],photoRequirements:[{name:'Programme portrait',required:true,instructions:'Neutral backdrop'}]}]});
test('blank templates contain no hard-coded categories and examples are explicit opt-in copies',async t=>{
 const {w}=setup(t);await w.StudioPlanTemplates.render();w.document.querySelector('[data-new]').click();assert.equal(w.document.querySelectorAll('[data-category-name]').length,0);assert.doesNotMatch(w.document.querySelector('form').textContent,/Students|Staff/);
 w.document.querySelector('[data-cancel]').click();w.document.querySelector('[data-examples]').click();assert.match(w.genericView.textContent,/Examples are editable/);w.document.querySelector('[data-use]').click();assert.equal(w.document.querySelector('[data-category-name]').value,'Students');assert.equal(w.document.querySelector('input[name=active]').checked,true);
});
test('editor saves arbitrary names, hierarchy order, optional shots and selected workspace scope',async t=>{
 const {w,workspaces}=setup(t),host=w.genericView;let saved;
 w.StudioPlanTemplates.edit(host,{id:'t',revision:4,active:true,definition:definition()},{workspaces,cancel:()=>{},save:async d=>saved=JSON.parse(JSON.stringify(d))});
 host.querySelector('[data-category-name]').value='Artists';host.querySelector('[data-move="groupingLevels:1:-1"]').click();
 host.querySelector('[data-add="groupingLevels"]').click();host.querySelectorAll('[data-level]')[2].value='Venue';
 host.querySelector('[data-field-row] input[type=text]').value='Accreditation';
 host.querySelector('[data-photo-row] input[type=checkbox]').checked=false;
 host.querySelector('[data-instructions]').value='Use campaign lighting';host.querySelector('[name=scope]').value='selected';host.querySelector('[data-workspace]').checked=true;
 host.querySelector('form').dispatchEvent(new w.Event('submit',{cancelable:true}));await tick();
 assert.equal(saved.revision,4);assert.deepEqual(saved.definition.workspaceIds,['w1']);assert.equal(saved.definition.categories[0].name,'Artists');assert.deepEqual(saved.definition.categories[0].groupingLevels,['Cast','Region','Venue']);assert.equal(saved.definition.categories[0].identificationFields[0].name,'Accreditation');assert.equal(saved.definition.categories[0].photoRequirements[0].required,false);assert.equal(saved.definition.categories[0].photoRequirements[0].instructions,'Use campaign lighting');
});
test('selected workspace mode requires a workspace and failed saves retain edits',async t=>{
 const {w,workspaces}=setup(t);let writes=0;w.StudioPlanTemplates.edit(w.genericView,{revision:0,active:true,definition:definition()},{workspaces,cancel:()=>{},save:async()=>{writes++;throw Error('Revision changed');}});
 const form=w.document.querySelector('form');form.elements.scope.value='selected';form.dispatchEvent(new w.Event('submit',{cancelable:true}));await tick();assert.equal(writes,0);assert.match(form.textContent,/Select at least one workspace/);
 form.elements.scope.value='all';form.elements.templateName.value='Retained edit';form.dispatchEvent(new w.Event('submit',{cancelable:true}));await tick();assert.equal(writes,1);assert.match(form.textContent,/Revision changed/);assert.equal(form.elements.templateName.value,'Retained edit');assert.equal(form.querySelector('[type=submit]').disabled,false);
});
test('applying a template sends both job and template revisions and never sends subject replacements',async t=>{
 const {w,calls,templates}=setup(t);templates.push({id:'template-id',revision:9,active:true,definition:definition()});let reload=0;
 await w.StudioPlanTemplates.apply({job:{id:'job-id',title:'Campaign job'},structure:{revision:3}},async()=>reload++);
 assert.match(w.document.querySelector('dialog').textContent,/Performers.*Region → Cast/s);const form=w.document.querySelector('form');form.dispatchEvent(new w.Event('submit',{cancelable:true}));await tick();
 const payload=JSON.parse(calls.find(c=>c.options).options.body);assert.deepEqual(payload,{revision:3,templateId:'template-id',templateRevision:9});assert.equal(reload,1);assert.equal(w.document.querySelector('dialog'),null);
});
test('a failed apply keeps preview open and does not report success',async t=>{
 const {w,templates,fail}=setup(t);templates.push({id:'t',revision:1,definition:definition()});let reload=0;await w.StudioPlanTemplates.apply({job:{id:'j',title:'Job'}},async()=>reload++);fail();w.document.querySelector('form').dispatchEvent(new w.Event('submit',{cancelable:true}));await tick();assert.match(w.document.querySelector('[role=alert]').textContent,/Changed on server/);assert.equal(reload,0);assert.ok(w.document.querySelector('dialog'));
});
test('customizing a job copies the definition and keeps the source template unmodified',async t=>{
 const {w,calls}=setup(t),source=definition(),s={job:{id:'j'},structure:{revision:2,definition:source}};
 w.StudioPlanTemplates.customize(s,async()=>{});w.document.querySelector('[data-category-name]').value='Local cast';w.document.querySelector('form').dispatchEvent(new w.Event('submit',{cancelable:true}));await tick();const payload=JSON.parse(calls[0].options.body);assert.equal(payload.revision,2);assert.equal(payload.templateId,null);assert.equal(payload.definition.categories[0].name,'Local cast');assert.equal(source.categories[0].name,'Performers');
});
test('new subject controls use the selected category hierarchy without rewriting existing subject structure',t=>{
 const {w}=setup(t);w.StudioRosterEditor={hierarchy:()=>[]};const s={structure:{definition:definition()},category:'Performers'},form=w.document.createElement('form');form.innerHTML=w.StudioPlanTemplates.subjectFields(s,{});w.StudioPlanTemplates.bindSubjectFields(form,s,{});form.querySelectorAll('[data-template-level]').forEach((input,i)=>input.value=['East','A'][i]);const updated={};w.StudioPlanTemplates.captureSubject(form,s,updated);assert.equal(updated.planCategory,'Performers');assert.deepEqual(JSON.parse(updated.planGroups),[{name:'Region',value:'East'},{name:'Cast',value:'A'}]);assert.equal(w.StudioPlanTemplates.subjectFields(s,{subjectId:'existing'}),'');
});
