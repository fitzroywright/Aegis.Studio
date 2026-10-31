const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const settle=()=>new Promise(resolve=>setImmediate(resolve));
test('login options failure leaves credential sign-in disabled',async t=>{
 const dom=new JSDOM(fs.readFileSync(path.join(root,'login.html'),'utf8'),{url:'https://studio.test/login',runScripts:'outside-only'});t.after(()=>dom.window.close());
 const w=dom.window;w.fetch=async()=>({ok:false});w.eval(fs.readFileSync(path.join(root,'login.js'),'utf8'));await settle();
 assert.equal(w.document.getElementById('signInButton').disabled,true);assert.match(w.document.getElementById('error').textContent,/Could not load/);
});
test('pending login shows approval state and clears the password without navigating',async t=>{
 const dom=new JSDOM(fs.readFileSync(path.join(root,'login.html'),'utf8'),{url:'https://studio.test/login',runScripts:'outside-only'});t.after(()=>dom.window.close());const w=dom.window,requests=[];
 w.fetch=async(url,options)=>{requests.push({url,options});return {ok:true,status:200,json:async()=>url.endsWith('/options')?{sources:['LocalDatabase'],defaultSource:'LocalDatabase',entraEnabled:false}:{accountState:'PendingAccess'}}};
 w.eval(fs.readFileSync(path.join(root,'login.js'),'utf8'));await settle();w.document.getElementById('userName').value='approved-alias';w.document.getElementById('password').value='temporary-local-password';
 w.document.getElementById('loginForm').dispatchEvent(new w.Event('submit',{bubbles:true,cancelable:true}));await settle();
 assert.match(w.document.getElementById('error').textContent,/Pending Access/);assert.equal(w.location.pathname,'/login');assert.equal(w.document.getElementById('password').value,'');
 assert.equal(Object.hasOwn(JSON.parse(requests.at(-1).options.body),'source'),false);
});
test('account provisioning retry preserves the pending account after a profile save failure',async t=>{
 const dom=new JSDOM('<div id="genericView"></div>',{url:'https://studio.test',runScripts:'outside-only'});t.after(()=>dom.window.close());const w=dom.window;
 w.esc=x=>String(x??'');w.showFeedback=()=>{};w.studioFirstName=x=>x.displayName;w.eval(fs.readFileSync(path.join(root,'workspaces.js'),'utf8'));w.document.getElementById('genericView').innerHTML=w.renderEmployeeDirectorySection();
 let account=null,creates=0,profileAttempts=0;
 w.json=async(url,options)=>{if(url==='/api/auth/options')return {defaultSource:'ActiveDirectory',demoAccountsEnabled:false};if(url==='/api/admin/security/accounts/'){if(options){creates++;account={id:'stable-account',loginName:'local.operator',displayName:'Operator',providerKind:'LocalDatabase',status:'PendingAccess'};return account;}return account?[account]:[];}
 if(url.startsWith('/api/admin/studio-employees/')){if(options){profileAttempts++;if(profileAttempts===1)throw Error('Profile save unavailable');return {};}return [];}
 if(url==='/api/photographers'||url==='/api/security/global-roles/')return [];throw Error('Unexpected API '+url);};
 await w.refreshStudioEmployees();w.openStaffEditor(null);w.document.getElementById('employeeUserName').value='local.operator';w.document.getElementById('employeeAuthSource').value='LocalDatabase';w.document.getElementById('employeeLocalPassword').value='new-local-password';
 await w.saveStudioEmployee();assert.equal(creates,1);assert.match(w.document.getElementById('employeeAccountStatus').textContent,/Profile save unavailable/);assert.equal(w.document.getElementById('employeeLocalPassword').value,'');
 await w.saveStudioEmployee();assert.equal(creates,1);assert.equal(profileAttempts,2);assert.match(w.document.querySelector('[data-staff-approvals]').textContent,/awaiting approval/);assert.equal(w.document.querySelector('.staff-person-form input[readonly]').value,'Local account');
});


