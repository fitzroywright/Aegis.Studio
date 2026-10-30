const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const settle=()=>new Promise(resolve=>setImmediate(resolve));
async function fixture(t,options){
 const dom=new JSDOM(fs.readFileSync(path.join(root,'login.html'),'utf8'),{url:'https://studio.test/login',runScripts:'outside-only'});t.after(()=>dom.window.close());
 const w=dom.window,requests=[],navigation=[];
 w.navigateForTest=url=>navigation.push(url);
 w.fetch=async(url,request)=>{requests.push({url,request});return {ok:true,status:200,json:async()=>url.endsWith('/options')?options:{accountState:'PendingAccess'}};};
 w.eval(fs.readFileSync(path.join(root,'login.js'),'utf8').replace("location.assign('/api/auth/entra')","window.navigateForTest('/api/auth/entra')"));await settle();
 return {w,d:w.document,requests,navigation,submit:async()=>{w.document.getElementById('loginForm').dispatchEvent(new w.Event('submit',{cancelable:true}));await settle();}};
}
for(const provider of ['ActiveDirectory','LocalDatabase']) test(provider+' uses one credential form without a provider choice',async t=>{
 const {d,requests,submit}=await fixture(t,{defaultSource:provider});
 assert.equal(d.querySelector('select,#entraSignIn,#environment'),null);assert.doesNotMatch(d.body.textContent,/Sign-in method|Sign in with Microsoft|demonstration/);
 assert.equal(d.getElementById('password').required,true);assert.equal(d.getElementById('userNameLabel').hidden,false);
 assert.equal(d.getElementById('recoveryToggle').hidden,provider!=='LocalDatabase');
 d.getElementById('userName').value='operator';d.getElementById('password').value='secret';await submit();
 assert.deepEqual(JSON.parse(requests.at(-1).request.body),{userName:'operator',password:'secret'});
 assert.match(d.getElementById('error').textContent,/Pending Access/);assert.equal(d.getElementById('password').value,'');
});
test('Entra uses the single Sign in button and never posts credentials',async t=>{
 const {d,requests,navigation,submit}=await fixture(t,{defaultSource:'EntraId'});
 assert.equal(d.getElementById('userNameLabel').hidden,true);assert.equal(d.getElementById('passwordLabel').hidden,true);
 assert.equal(d.getElementById('userName').required,false);assert.equal(d.getElementById('password').required,false);
 assert.equal(d.getElementById('recoveryToggle').hidden,true);assert.equal(d.querySelectorAll('#loginForm button[type="submit"]').length,1);
 await submit();assert.deepEqual(navigation,['/api/auth/entra']);assert.equal(requests.length,1);
});
test('isolated demo uses configured identity without displaying provider details',async t=>{
 const {d,requests,submit}=await fixture(t,{defaultSource:'Mock',defaultUserName:'known',mockPasswordRequired:true});
 assert.equal(d.getElementById('userName').value,'known');assert.equal(d.getElementById('password').disabled,true);assert.equal(d.getElementById('passwordLabel').hidden,true);
 assert.equal(d.getElementById('recoveryToggle').hidden,true);
 await submit();assert.deepEqual(JSON.parse(requests.at(-1).request.body),{userName:'known',password:''});
});
test('an unknown configured provider leaves sign-in disabled',async t=>{
 const {d,requests,submit}=await fixture(t,{defaultSource:'unknown'});
 assert.equal(d.getElementById('signInButton').disabled,true);assert.match(d.getElementById('error').textContent,/not configured/);await submit();assert.equal(requests.length,1);
});
