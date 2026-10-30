const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
async function setup(t,{accounts=true,roleFailure=false}={}){
 const dom=new JSDOM('<div id="genericView"></div>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());
 w.genericView=w.document.getElementById('genericView');w.esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));w.confirm=()=>true;
 const employee={userName:'domain/user',displayName:'Sam Lee',emailAddress:'sam@example.test',isActive:true,isAssignable:true,source:'Directory',externalId:'immutable',skills:['Portraits'],workDayUserName:'sam-day',lastSyncedAtUtc:'2026-10-27T12:00:00Z'};
 const account={id:'account',loginName:employee.userName,displayName:employee.displayName,providerKind:'ActiveDirectory',status:'PendingAccess'};
 const roles=[{id:'front',name:'Front Desk',description:'Reception',isActive:true},{id:'inactive',name:'Old role',isActive:false}];
 const writes=[];let fail=roleFailure;
 w.json=async(url,options)=>{
  if(options){writes.push({url,method:options.method,body:options.body?JSON.parse(options.body):null});if(fail&&url.includes('/members/')){fail=false;throw Error('Role unavailable');}return {id:'created'};}
  if(url.includes('studio-employees'))return [{...employee}, {userName:'second',displayName:'Anna Clarke',isActive:true,isAssignable:false,skills:[]}];
  if(url==='/api/photographers')return [];
  if(url==='/api/admin/security/accounts/')return accounts?[account]:[];
  if(url==='/api/security/global-roles/')return roles;
  if(url.endsWith('/members'))return [];
  throw Error('Unexpected URL '+url);
 };
 w.eval(fs.readFileSync(path.join(root,'global-roles.js'),'utf8'));
 w.eval(fs.readFileSync(path.join(root,'workspaces.js'),'utf8'));
 w.genericView.innerHTML=w.renderEmployeeDirectorySection();await w.refreshStudioEmployees();
 const selectSam=()=>{const button=[...w.document.querySelectorAll('.staff-person')].find(b=>b.textContent.includes('Sam Lee'));w.openStaffEditor(Number(button.closest('[data-staff-index]').dataset.staffIndex));return w.document.querySelector('.staff-person-form');};
 return {w,employee,writes,selectSam,dom};
}
test('staff page has one editor, searchable rows, separate role tab and collapsed technical controls',async t=>{
 const {w,selectSam}=await setup(t);selectSam();assert.equal(w.document.querySelectorAll('.staff-person-form').length,1);assert.equal(w.document.getElementById('rolesPanel').hidden,true);assert.equal(w.document.querySelector('.staff-advanced').open,false);assert.equal(w.document.querySelector('.staff-person-form input[readonly]').value,'Active Directory');
 w.filterStudioStaff('sam@example');assert.equal(w.document.querySelectorAll('[data-staff-index]').length,1);w.filterStudioStaff('nobody');assert.match(w.document.getElementById('studioEmployeeList').textContent,/No matching/);
 w.selectStaffTab('roles');assert.equal(w.document.getElementById('staffPanel').hidden,true);assert.equal(w.document.getElementById('rolesTab').getAttribute('aria-selected'),'true');
});
test('staff save preserves fresh directory and WorkDay metadata and encodes identity paths',async t=>{
 const {w,writes,employee,selectSam}=await setup(t);const form=selectSam();form.elements.displayName.value='Updated name';await w.saveStaffDetails({...employee,securityAccount:{id:'account',status:'PendingAccess'}},form);
 const save=writes.find(x=>x.url.includes('studio-employees/'));assert.equal(save.url,'/api/admin/studio-employees/domain%2Fuser');assert.equal(save.body.displayName,'Updated name');for(const key of ['source','externalId','skills','workDayUserName','lastSyncedAtUtc'])assert.deepEqual(save.body[key],employee[key]);assert.ok(!('securityAccount'in save.body));assert.equal(form.querySelector('[data-editor-status]').textContent,'Saved');
});
test('role assignments save before account approval and inactive roles stay disabled',async t=>{
 const {w,writes,selectSam}=await setup(t);const form=selectSam();form.querySelector('[data-staff-role="0"]').checked=true;form.elements.accountStatus.value='Active';await form.onsubmit({preventDefault(){}});
 assert.equal(writes[1].url,'/api/security/global-roles/front/members/domain%2Fuser');assert.equal(writes[1].body.isActive,true);assert.equal(writes[2].url,'/api/admin/security/accounts/account/status');assert.equal(writes[2].body.status,'Active');assert.equal(form.querySelector('[data-staff-role="1"]').disabled,true);
});
test('partial role failures retain edits and retry completes without approving access early',async t=>{
 const {writes,selectSam}=await setup(t,{roleFailure:true});const form=selectSam();form.querySelector('[data-staff-role="0"]').checked=true;form.elements.accountStatus.value='Active';form.elements.displayName.value='Draft';await form.onsubmit({preventDefault(){}});
 assert.match(form.querySelector('[data-editor-status]').textContent,/Role unavailable/);assert.equal(form.elements.displayName.value,'Draft');assert.equal(writes.filter(x=>x.url.endsWith('/status')).length,0);assert.equal(form.querySelector('[type=submit]').disabled,false);
 await form.onsubmit({preventDefault(){}});assert.equal(writes.filter(x=>x.url.endsWith('/status')).length,1);assert.equal(form.querySelector('[data-editor-status]').textContent,'Saved');
});
test('directory staff without an enrolled account cannot receive global roles and can start sign-in setup',async t=>{
 const {w,selectSam}=await setup(t,{accounts:false});const form=selectSam();assert.equal(form.querySelector('[data-staff-role="0"]').disabled,true);form.querySelector('[data-enroll]').click();assert.equal(w.document.getElementById('employeeUserName').value,'domain/user');assert.equal(w.document.getElementById('employeeUserName').readOnly,true);assert.equal(w.document.getElementById('employeeEmail').value,'sam@example.test');
});
test('new staff form only requires a password for local authentication and preserves creation API',async t=>{
 const {w,writes}=await setup(t);w.openStaffEditor(null);const d=w.document;d.getElementById('employeeUserName').value='new.user';d.getElementById('employeeDisplayName').value='New User';
 assert.equal(d.querySelector('[data-local-password]').hidden,true);const source=d.getElementById('employeeAuthSource');source.value='LocalDatabase';source.onchange();assert.equal(d.querySelector('[data-local-password]').hidden,false);assert.equal(d.getElementById('employeeLocalPassword').required,true);d.getElementById('employeeLocalPassword').value='a sufficiently long password';await w.saveStudioEmployee();
 const created=writes.find(x=>x.url==='/api/admin/security/accounts/');assert.equal(created.body.authenticationSource,'LocalDatabase');assert.equal(created.body.initialPassword,'a sufficiently long password');
});
test('dirty editor cancellation protects entered changes and Cancel restores saved details',async t=>{
 const {w,selectSam}=await setup(t);const form=selectSam();form.elements.displayName.value='Draft';form.oninput();w.confirm=()=>false;w.openStaffEditor(null);assert.equal(w.document.querySelector('.staff-person-form').elements.displayName.value,'Draft');w.confirm=()=>true;form.querySelector('[data-cancel]').click();assert.equal(w.document.querySelector('.staff-person-form').elements.displayName.value,'Sam Lee');
});
test('stale staff loading cannot replace a newer view and errors provide retry',async t=>{
 const {w}=await setup(t);w.json=async()=>{throw Error('Directory unavailable');};await w.refreshStudioEmployees();assert.match(w.document.getElementById('studioEmployeeList').textContent,/Directory unavailable/);assert.match(w.document.getElementById('studioEmployeeList').textContent,/Retry/);
 let release;const barrier=new Promise(resolve=>release=resolve);w.json=()=>barrier;const pending=w.refreshStudioEmployees();w.genericView.innerHTML='<p>Another view</p>';release([]);await pending;assert.equal(w.genericView.textContent,'Another view');
});
