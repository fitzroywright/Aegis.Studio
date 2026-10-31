const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
async function setup(t,{failure=false}={}){
 const dom=new JSDOM('<main id="genericView"></main>',{url:'https://studio.test',runScripts:'outside-only'});t.after(()=>dom.window.close());const w=dom.window,writes=[];
 const employee={userName:'photo.user',displayName:'Photo Person',isActive:true,isAssignable:true,skills:['Portrait']};let workers=[];
 const roles=[{code:'PHOTOGRAPHER',displayName:'Photographer',isActive:true},{code:'EDITOR',displayName:'Editor',isActive:true},{code:'OLD',displayName:'Inactive role',isActive:false}];
 w.esc=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));w.confirm=()=>true;
 w.studioDialog=(title,content)=>{const d=w.document.createElement('dialog');d.innerHTML=content;d.close=()=>{d.dispatchEvent(new w.Event('close'));d.remove();};w.document.body.append(d);return d;};
 w.json=async(url,options)=>{
  if(options){const body=JSON.parse(options.body);writes.push({url,body});if(url.endsWith('/roles')){if(failure)throw Error('Role save failed');workers=body.roleCodes.map(jobRoleCode=>({...employee,jobRoleCode}));return workers;}if(url==='/api/admin/job-roles'){roles.push(body);return body;}return {};}
  if(url==='/api/auth/options')return {defaultSource:'ActiveDirectory',demoAccountsEnabled:false};
  if(url==='/api/admin/security/accounts/'||url==='/api/security/global-roles/')return [];
  if(url==='/api/admin/studio-employees/')return [{...employee}];
  if(url==='/api/admin/job-roles')return roles;
  if(url==='/api/admin/workers')return workers;
  throw Error('Unexpected API: '+url);
 };
 w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/workspaces.js'),'utf8'));w.genericView=w.document.getElementById('genericView');w.genericView.innerHTML=w.renderEmployeeDirectorySection();await w.refreshStudioEmployees();return {w,writes};
}
function add(w,code){const editor=w.document.querySelector('[data-token-kind=job]');editor.querySelector('input').focus();editor.querySelector('[data-add-token="'+code+'"]').click();}
test('Photographer and Editor use one token selector and persist together as pills after reload',async t=>{
 const {w,writes}=await setup(t);assert.equal(w.document.getElementById('rolesTab'),null);assert.equal(w.document.querySelector('[data-register-photographer]'),null);
 add(w,'PHOTOGRAPHER');add(w,'EDITOR');const form=w.document.querySelector('.staff-person-form');await form.onsubmit({preventDefault(){}});
 assert.deepEqual(writes.find(write=>write.url.endsWith('/roles')).body.roleCodes,['PHOTOGRAPHER','EDITOR']);
 await w.refreshStudioEmployees();assert.equal(w.document.querySelectorAll('.staff-directory-table .staff-role-badge').length,2);
 const editor=w.document.querySelector('[data-token-kind=job]');assert.equal(editor.querySelectorAll('[data-remove-token]').length,2);assert.equal(editor.querySelector('[data-add-token=OLD]'),null);
 editor.querySelector('[data-remove-token=PHOTOGRAPHER]').click();await w.document.querySelector('form').onsubmit({preventDefault(){}});await w.refreshStudioEmployees();assert.equal(w.document.querySelectorAll('.staff-directory-table .staff-role-badge').length,1);assert.match(w.document.querySelector('.staff-directory-table').textContent,/Editor/);
});
test('failed Job Role save retains multiple tokens and retries without losing other edits',async t=>{
 const {w}=await setup(t,{failure:true});add(w,'PHOTOGRAPHER');add(w,'EDITOR');const form=w.document.querySelector('form');form.elements.displayName.value='Updated';await form.onsubmit({preventDefault(){}});assert.match(form.querySelector('[data-editor-status]').textContent,/Role save failed/);assert.equal(form.querySelectorAll('[data-remove-token]').length,2);assert.equal(form.elements.displayName.value,'Updated');assert.equal(form.querySelector('[type=submit]').disabled,false);
});
test('token search, keyboard selection and clear-all preserve explicit empty role selection',async t=>{
 const {w,writes}=await setup(t);const editor=w.document.querySelector('[data-token-kind=job]'),input=editor.querySelector('input');input.value='edit';input.dispatchEvent(new w.Event('input'));assert.equal(editor.querySelectorAll('[data-add-token]').length,1);input.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Enter',bubbles:true}));assert.equal(editor.querySelector('[data-remove-token=EDITOR]').textContent.trim(),'Editor ×');await w.document.querySelector('form').onsubmit({preventDefault(){}});editor.querySelector('[data-remove-token=EDITOR]').click();await w.document.querySelector('form').onsubmit({preventDefault(){}});assert.deepEqual(writes.at(-1).body.roleCodes,[]);
});
test('Manage Job Roles edits definitions through the existing role API without a Roles tab',async t=>{
 const {w,writes}=await setup(t);await w.document.querySelector('[data-manage-job-roles]').onclick();const form=w.document.querySelector('[data-job-role-form]');form.elements.code.value='assistant';form.elements.displayName.value='Assistant';await form.onsubmit({preventDefault(){}});const saved=writes.find(write=>write.url==='/api/admin/job-roles');assert.equal(saved.body.code,'ASSISTANT');assert.equal(saved.body.displayName,'Assistant');assert.match(w.document.querySelector('dialog').textContent,/Assistant/);
});
