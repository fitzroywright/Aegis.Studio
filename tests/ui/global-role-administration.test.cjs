const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const script=fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/global-roles.js'),'utf8');
const names=['Administrator','User','Front Desk','Operations Manager','Finance Manager','Production Manager','Media Custodian'];
function setup(t){
 const dom=new JSDOM('<div id="genericView"><div data-staff-global-roles></div></div>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());
 w.genericView=w.document.getElementById('genericView');w.esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));w.confirm=()=>true;w.eval(script);
 const fixture={roles:names.map((name,i)=>({id:'r'+i,name,description:name+' permissions',isActive:true})),members:{},writes:[],administrator:true};
 w.json=async(url,options)=>{
  if(url==='/api/auth/me')return {user:{isAdministrator:fixture.administrator}};
  if(url==='/api/security/global-roles/')return fixture.roles;
  const match=url.match(/^\/api\/security\/global-roles\/([^/]+)\/members(?:\/(.*))?$/);
  if(!match)throw Error('Unexpected API '+url);
  const role=decodeURIComponent(match[1]),user=match[2]&&decodeURIComponent(match[2]);
  if(!options)return (fixture.members[role]||[]).map(x=>({...x}));
  fixture.writes.push({url,method:options.method,body:options.body&&JSON.parse(options.body)});
  if(fixture.failure)throw Error(fixture.failure);
  const members=fixture.members[role]||=[];
  if(options.method==='DELETE'){fixture.members[role]=members.filter(x=>x.userName!==user);return {};}
  const payload=JSON.parse(options.body),existing=members.find(x=>x.userName===user);
  if(existing)Object.assign(existing,payload);else members.push(payload);return payload;
 };
 return {w,fixture,card:id=>w.document.querySelector('[data-global-role-card="'+id+'"]')};
}
const submit=(w,form)=>form.onsubmit(new w.Event('submit',{cancelable:true}));

test('all global roles and additional server roles have one escaped administration card',async t=>{
 const {w,fixture}=setup(t);fixture.roles.push({id:'custom',name:'Custom <role>',description:'<img src=x onerror=alert(1)>',isActive:true});
 await w.renderGlobalRoleAdministration();
 assert.deepEqual([...w.document.querySelectorAll('[data-global-role-card] h3')].map(x=>x.textContent),[...names,'Custom <role>']);
 assert.equal(w.document.querySelectorAll('img').length,0);assert.match(w.genericView.textContent,/Approve Pending Access accounts separately/);
 assert.equal(w.document.querySelectorAll('form').length,8);assert.equal(new Set([...w.document.querySelectorAll('[id]')].map(x=>x.id)).size,w.document.querySelectorAll('[id]').length);
});

test('non-administrators see no global role controls and trigger no privileged API call',async t=>{
 const {w}=setup(t);w.genericView.querySelector('[data-staff-global-roles]').innerHTML='<button>Stale admin control</button>';const calls=[];
 w.json=async url=>{calls.push(url);return {user:{isAdministrator:false}};};await w.renderGlobalRoleAdministration();
 assert.deepEqual(calls,['/api/auth/me']);assert.equal(w.document.querySelectorAll('button,input').length,0);
});

test('Administrator assignment uses the audited membership API and keeps another role draft',async t=>{
 const {w,fixture,card}=setup(t);await w.renderGlobalRoleAdministration();
 const form=card('r0').querySelector('form'),other=card('r4').querySelector('input[name=userName]');other.value='Unfinished finance edit';
 form.elements.namedItem('userName').value='demo/admin';form.elements.namedItem('displayName').value='Demo Administrator';await submit(w,form);
 assert.equal(fixture.writes.length,1);assert.deepEqual(fixture.writes[0],{url:'/api/security/global-roles/r0/members/demo%2Fadmin',method:'PUT',body:{userName:'demo/admin',displayName:'Demo Administrator',isActive:true}});
 assert.match(card('r0').textContent,/demo\/admin/);assert.equal(form.elements.namedItem('userName').value,'');assert.equal(other.value,'Unfinished finance edit');
});

test('assignment failures retain inputs and allow retry without changing account approval',async t=>{
 const {w,fixture,card}=setup(t);await w.renderGlobalRoleAdministration();const form=card('r0').querySelector('form');
 form.elements.namedItem('userName').value='demo.admin';form.elements.namedItem('displayName').value='Demo admin';fixture.failure='Account is not enrolled';
 await submit(w,form);assert.equal(form.elements.namedItem('userName').value,'demo.admin');assert.equal(form.elements.namedItem('displayName').value,'Demo admin');
 assert.match(form.textContent,/Account is not enrolled/);assert.equal(form.querySelector('button').disabled,false);
 fixture.failure=null;await submit(w,form);assert.match(form.textContent,/Assigned/);assert.ok(fixture.writes.every(x=>x.url.includes('/global-roles/')));
});

test('Administrator membership edits retain failed changes and save active state on retry',async t=>{
 const {w,fixture,card}=setup(t);fixture.members.r0=[{userName:'demo.admin',displayName:'Admin',isActive:true}];await w.renderGlobalRoleAdministration();
 const row=card('r0').querySelector('[data-global-member]');row.querySelector('[data-member-name]').value='Updated admin';row.querySelector('[data-member-active]').checked=false;
 fixture.failure='Write unavailable';await row.querySelector('[data-member-save]').onclick();assert.match(row.textContent,/Write unavailable/);
 assert.equal(row.querySelector('[data-member-name]').value,'Updated admin');assert.equal(row.querySelector('[data-member-active]').checked,false);
 fixture.failure=null;await row.querySelector('[data-member-save]').onclick();assert.deepEqual(fixture.writes.at(-1).body,{userName:'demo.admin',displayName:'Updated admin',isActive:false});assert.match(row.textContent,/Saved/);
});

test('role removal requires confirmation, reports failure and refreshes after a successful retry',async t=>{
 const {w,fixture,card}=setup(t);fixture.members.r0=[{userName:'demo/admin',displayName:'Admin',isActive:true}];await w.renderGlobalRoleAdministration();
 const row=card('r0').querySelector('[data-global-member]');let question;w.confirm=text=>{question=text;return false;};
 await row.querySelector('[data-member-remove]').onclick();assert.equal(fixture.writes.length,0);assert.match(question,/Administrator/);
 w.confirm=()=>true;fixture.failure='Delete unavailable';await row.querySelector('[data-member-remove]').onclick();assert.match(row.textContent,/Delete unavailable/);assert.equal(row.querySelector('button').disabled,false);
 fixture.failure=null;await row.querySelector('[data-member-remove]').onclick();assert.equal(fixture.writes.at(-1).method,'DELETE');assert.match(fixture.writes.at(-1).url,/demo%2Fadmin$/);assert.match(card('r0').textContent,/No users assigned/);
});

test('membership loading errors are visible and retry only the affected role',async t=>{
 const {w,card}=setup(t),json=w.json;let failed=true,roleRequests=0;
 w.json=async(url,options)=>{if(url==='/api/security/global-roles/')roleRequests++;if(url==='/api/security/global-roles/r0/members'&&failed)throw Error('Members unavailable');return json(url,options);};
 await w.renderGlobalRoleAdministration();assert.match(card('r0').textContent,/Members unavailable/);
 failed=false;await card('r0').querySelector('.staff-grid-scroll button').onclick();assert.match(card('r0').textContent,/No users assigned/);assert.equal(roleRequests,1);
});

test('inactive roles cannot receive assignments or membership edits',async t=>{
 const {w,fixture,card}=setup(t);fixture.roles[0].isActive=false;fixture.members.r0=[{userName:'demo.admin',displayName:'Admin',isActive:true}];await w.renderGlobalRoleAdministration();
 assert.match(card('r0').querySelector('h3').textContent,/inactive/);assert.equal(card('r0').querySelector('form button').disabled,true);
 assert.equal(card('r0').querySelector('[data-member-save]').disabled,true);await submit(w,card('r0').querySelector('form'));assert.equal(fixture.writes.length,0);
 fixture.failure='Delete unavailable';await card('r0').querySelector('[data-member-remove]').onclick();assert.equal(card('r0').querySelector('[data-member-save]').disabled,true);
});

test('global role loading failures offer Retry and stale navigation responses cannot restore controls',async t=>{
 const {w}=setup(t),json=w.json;let fail=true;w.json=async url=>{if(url==='/api/security/global-roles/'&&fail)throw Error('Roles unavailable');return json(url);};
 await w.renderGlobalRoleAdministration();assert.match(w.genericView.textContent,/Roles unavailable/);fail=false;await w.genericView.querySelector('button').onclick();assert.equal(w.document.querySelectorAll('form').length,7);
 let release,current=true;w.json=async url=>url==='/api/auth/me'?{user:{isAdministrator:true}}:new Promise(resolve=>{release=resolve;});
 const pending=w.renderGlobalRoleAdministration(()=>current);await new Promise(resolve=>setImmediate(resolve));current=false;w.genericView.innerHTML='<p>Another page</p>';release([]);await pending;assert.equal(w.genericView.textContent,'Another page');
});
