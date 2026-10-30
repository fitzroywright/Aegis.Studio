const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');const {api,jobs,workspaces,permissions}=require('./shared-controller-fixture.cjs');
async function environment(t,full=false,url='https://studio.test/'){
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url,runScripts:'outside-only'});t.after(()=>dom.window.close());const w=dom.window,calls=[],alerts=[];const spaces=JSON.parse(JSON.stringify(workspaces));
 w.console.info=()=>{};
 w.ResizeObserver=class {observe(){} disconnect(){}};w.requestAnimationFrame=fn=>w.setTimeout(fn,0);
 w.alert=s=>alerts.push(s);w.confirm=()=>false;w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;};w.HTMLElement.prototype.scrollIntoView=function(){};
 w.fetch=async (url,options)=>{const u=new URL(url,'https://studio.test'),p=u.pathname;calls.push({url,options});let body=api(u);
 if(p==='/api/workspace-access')body=spaces.map(workspace=>({workspace,permissions:workspace.permissions}));
 if(p.endsWith('/attachments'))body=[];
 else if(p.startsWith('/api/conversations/'))body=p.endsWith('/sending-identities')?{identities:[{type:'Workspace',isDefault:true,label:'Workspace'},{type:'Self',label:'Myself'}]}:{id:'c0',workspaceId:'w0',channel:'Email',contactName:'Sarah Miller',contactAddress:'sarah@example.invalid',subject:'Session confirmation',messages:[{id:'m',body:'Please confirm',visibility:'External',author:'Sarah Miller',createdAt:'2026-10-10T12:00:00Z'}]};
 if(p==='/api/jobs/access')body=Object.fromEntries(jobs.map(j=>[j.id,permissions]));
 if(p.endsWith('/connected-workflow'))body={data:{stage:'Scheduled',owner:'demo.sam',sessions:[],history:[],submissions:[],revision:1},userName:'demo.sam',canManage:true,canReview:true,canAssignOwner:true,canFrontDesk:true};
 return {ok:true,status:200,json:async()=>body,text:async()=>JSON.stringify(body),headers:{get:()=>null}};
 };
 if(!full)w.studioOpenJobs=async()=>{};
 const scripts=full?[...w.document.querySelectorAll('script[src]')].map(el=>new URL(el.src).pathname.slice(1)):['studio-page.js','studio-email-body.js','studio-message-card.js','app.js','workspace-access.js','attachments.js','auth.js','job-handoff.js','job-workspace.js','media-production.js','connected-workflow.js','job-details.js','workspace-outlook.js'];
 for(const script of scripts)require('vm').runInContext(require('./studio-script-fixture.cjs')(script),dom.getInternalVMContext());
 await new Promise(r=>setTimeout(r,0));if(full)await w.studioTodayReady;await w.loadWorkspaces();return {w,calls,alerts,spaces};
}
test('navigation remains usable after applying independent rail state',async t=>{const {w}=await environment(t,true);await w.studioNavigate('jobs');assert.equal(w.document.body.dataset.studioView,'jobs');assert.doesNotMatch(w.document.getElementById('genericView').textContent,/Maximum call stack size exceeded/);});
test('navigation remains available without retired rail toggles or right-rail controls',async t=>{const {w}=await environment(t,true);await w.studioNavigate('jobs');assert.equal(w.document.querySelector('#studioContextRail,.sj-right,[data-rail-toggle]'),null);const nav=w.document.getElementById('studioNavigationTools');assert.ok(nav.querySelector('[data-studio-side="jobs"]'));assert.notEqual(nav.inert,true);});
test('workspace selection has one controller and preserves denial and job loading',async t=>{const {w,calls,alerts}=await environment(t,true);const entry=w.selectWorkspace;const loader=w.loadConversations;await w.selectWorkspace('w0','Portraits and IDs');assert.equal(w.selectWorkspace,entry);assert.equal(w.loadConversations,loader);assert.equal(w.eval('selectedWorkspace'),'w0');assert.equal(w.document.body.dataset.studioView,'jobs');assert.equal(w.document.querySelectorAll('tr[data-sj-job]').length,1);assert.match(w.document.querySelector('tr[data-sj-job]').textContent,/Miller Family/);assert.equal(w.document.querySelector('.workspace-tabs'),null);assert.equal(w.document.getElementById('conversationView').classList.contains('hidden'),true);w.eval("workspaceCache.find(w=>w.id==='w1').permissions.view=false");calls.length=0;await w.selectWorkspace('w1','Denied');assert.equal(w.eval('selectedWorkspace'),'w0');assert.match(alerts.at(-1),/permission/);assert.equal(calls.some(c=>c.url==='/api/jobs'),false);});
test('conversation permission, reply identity and attachment hooks execute once',async t=>{const {w,calls}=await environment(t);w.eval("selectedWorkspace='w0';selectedConversation='c0';currentWorkspacePermissions=workspaceCache[0].permissions;workspaceCache[0].permissions.replyRequester=false;workspaceCache[0].permissions.editJob=false;workspaceCache[0].permissions.createJob=false;workspaceCache[0].permissions.manageProduction=false");await w.openConversation('c0');assert.equal(w.document.querySelectorAll('.reply-identity').length,1);assert.equal(w.document.querySelectorAll('.attachment-section').length,1);assert.equal(w.document.querySelector('.composer .primary').disabled,true);assert.equal(w.document.getElementById('internalToggle').disabled,true);assert.match(w.document.getElementById('messageBody').placeholder,/Read only/);w.document.getElementById('messageBody').value='Blocked reply';calls.length=0;await w.sendMessage();await w.createJob();assert.equal(calls.some(c=>c.options?.method==='POST'),false);});
test('authorized conversation retains reply identity and attachment operation',async t=>{const {w,calls}=await environment(t);w.eval("selectedWorkspace='w0';selectedConversation='c0';currentWorkspacePermissions=workspaceCache[0].permissions");await w.openConversation('c0');assert.equal(w.document.getElementById('sendingIdentity').options.length,2);w.document.getElementById('messageBody').value='Confirmed';await w.sendMessage();const post=calls.find(c=>c.url==='/api/conversations/c0/messages'&&c.options?.method==='POST');assert.deepEqual(JSON.parse(post.options.body),{body:'Confirmed',isInternal:false,sendingIdentity:'Workspace'});});
test('shell sync leaves the router identity intact and browser Back loads one destination',async t=>{const {w,calls}=await environment(t,true);const router=w.studioNavigate;assert.equal(w.studioNavigate.__studioShellWrapped,undefined);await w.studioNavigate('jobs');assert.equal(w.studioNavigate,router);await w.studioNavigate('workspaces');calls.length=0;w.dispatchEvent(new w.PopStateEvent('popstate',{state:{studioView:'jobs'}}));await new Promise(r=>setTimeout(r,10));assert.equal(w.document.body.dataset.studioView,'jobs');assert.equal(calls.filter(c=>c.url==='/api/jobs').length,1);assert.equal(w.document.querySelector('.sj-right,#studioContextRail'),null);});
test('shared handlers no longer replace already defined workspace, conversation or navigation functions',()=>{for(const file of ['workspace-access.js','attachments.js','workspace-outlook.js','job-access.js','studio-shell-nav.js']){const code=fs.readFileSync(path.join(root,file),'utf8');assert.doesNotMatch(code,/originalSelectWorkspaceForAccess|originalOpenConversationForAccess|originalSendMessageForAccess|originalCreateJobForAccess|baseOpenConversation|originalOpenJob|__studioShellWrapped/);assert.doesNotMatch(code,/(?:^|\n)\s*(?:selectWorkspace|loadConversations|openConversation|sendMessage|createJob)\s*=\s*async/);}});
test('browser Back restores Workspaces after selecting its Jobs grid',async t=>{
 const {w,calls}=await environment(t,true);await w.studioNavigate('workspaces');await w.selectWorkspace('w0','Portraits and IDs');assert.equal(w.document.body.dataset.studioView,'jobs');calls.length=0;
 w.dispatchEvent(new w.PopStateEvent('popstate',{state:{studioView:'workspaces'}}));await new Promise(r=>setTimeout(r,10));assert.equal(w.document.body.dataset.studioView,'workspaces');assert.equal(calls.filter(c=>c.url==='/api/jobs').length,1);assert.equal(w.document.getElementById('conversationView').classList.contains('hidden'),true);
});


test('workspace cards and footer selection open the same filtered Jobs grid and a row opens Job Details',async t=>{
 const {w}=await environment(t,true);
 await w.studioNavigate('workspaces');
 w.document.querySelector('[data-open-workspace="w0"]').click();
 await new Promise(r=>setTimeout(r,20));
 assert.equal(w.document.body.dataset.studioView,'jobs');
 assert.equal(w.history.state.studioWorkspaceId,'w0');
 assert.equal(w.document.querySelectorAll('tr[data-sj-job]').length,1);
 assert.equal(w.document.querySelector('[data-sj-settings]').hidden,false);
 w.document.querySelector('tr[data-sj-job]').click();
 await new Promise(r=>setTimeout(r,20));
 assert.equal(w.document.querySelector('.jd-job-name-text').textContent,jobs[0].title);
 assert.equal(w.document.querySelector('.workspace-tabs'),null);
 await w.studioNavigate('jobs');
 const workspace=w.document.getElementById('studioFooterWorkspaces');workspace.value='w1';workspace.dispatchEvent(new w.Event('change'));
 await new Promise(r=>setTimeout(r,20));
 assert.equal(w.document.body.dataset.studioView,'jobs');
 assert.equal(w.history.state.studioWorkspaceId,'w1');
 assert.equal(w.document.querySelector('tr[data-sj-job]').dataset.sjJob,'j1');
});

async function travel(w,direction){const event=new Promise(resolve=>w.addEventListener('popstate',resolve,{once:true}));w.history[direction]();await event;await new Promise(resolve=>setTimeout(resolve,25));}
test('real browser Back and Forward restore workspace Jobs and individual Job Details without adding entries',async t=>{
 const {w}=await environment(t,true);await w.studioOpenJobs('w0');assert.equal(w.history.state.studioWorkspaceId,'w0');await w.openWorkspaceJob('j0');assert.equal(w.history.state.studioJobId,'j0');assert.match(w.location.hash,/job=j0/);await w.studioNavigate('messages');const length=w.history.length;
 await travel(w,'back');assert.equal(w.history.state.studioJobId,'j0');assert.equal(w.document.body.dataset.studioView,'workspaces');assert.equal(w.document.querySelector('.jd-job-name-text').textContent,jobs.find(j=>j.id==='j0').title);
 await travel(w,'back');assert.equal(w.document.body.dataset.studioView,'jobs');assert.equal(w.history.state.studioWorkspaceId,'w0');assert.equal(w.history.length,length);
 await travel(w,'forward');assert.equal(w.history.state.studioJobId,'j0');assert.ok(w.document.querySelector('.jd-job-name-text'));assert.equal(w.history.length,length);
});
test('Back between jobs restores the preceding job; repeat open does not create a duplicate entry',async t=>{
 const {w}=await environment(t,true);await w.openWorkspaceJob('j0');await w.openWorkspaceJob('j1');const length=w.history.length;await w.openWorkspaceJob('j1');assert.equal(w.history.length,length);await travel(w,'back');assert.equal(w.history.state.studioJobId,'j0');assert.equal(w.document.querySelector('.jd-job-name-text').textContent,jobs.find(j=>j.id==='j0').title);
});
test('changing workspace scope on the same Jobs view is a separate browser destination',async t=>{
 const {w}=await environment(t,true);await w.studioOpenJobs('w0');await w.studioOpenJobs('w1');await travel(w,'back');assert.equal(w.history.state.studioWorkspaceId,'w0');assert.equal(w.document.body.dataset.studioView,'jobs');
});
test('refresh at a Job Details URL restores that job without routing through Today',async t=>{
 const {w}=await environment(t,true,'https://studio.test/#studio=workspaces&job=j0');assert.equal(w.history.state.studioJobId,'j0');assert.equal(w.history.length,1);assert.equal(w.document.querySelector('.jd-job-name-text').textContent,jobs.find(j=>j.id==='j0').title);
});

test('Jobs and Today status filters follow workflow event order',async t=>{const {w}=await environment(t,true);assert.ok(w.document.querySelector('.company-dashboard'));await w.studioNavigate('jobs');const control=w.document.querySelector('[data-sj-status]');const values=Array.from(control.options,o=>o.value).filter(Boolean);assert.deepEqual(values,Array.from(w.StudioStatusOrder.sort(values)));});
test('shared and Front Desk popups place functional Close controls beside the title',async t=>{const {w}=await environment(t,true);const shared=w.studioDialog('Assignments & Workflow','<p>Details</p>');const close=shared.querySelector('[data-close]');assert.equal(close.parentElement,shared.querySelector('.studio-dialog-heading'));assert.equal(close.previousElementSibling.tagName,'H2');close.click();assert.equal(shared.open,false);const front=w.frontDeskDialog('Booking','<p>Details</p>');assert.equal(front.querySelector('.studio-dialog-close').parentElement,front.querySelector('.studio-dialog-heading'));front.querySelector('.studio-dialog-close').click();assert.equal(front.open,false);});

