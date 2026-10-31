const {JSDOM,VirtualConsole}=require('jsdom');const fs=require('fs');const path=require('path');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const {test}=require('node:test');const assert=require('node:assert/strict');
const now=new Date(),iso=(h,d=0)=>new Date(new Date().setHours(h,0,0,0)+d*86400000).toISOString();
const permissions={accessWorkspace:true,view:true,editJob:true,changeStatus:true,manageSchedule:true,manageProduction:true,recordFinancialActivity:true,viewFinancials:true,viewConversation:true,viewAttachments:true,addAttachments:true,replyRequester:true,manageMembers:true,createJob:true,notifyMembers:true};
const names=['Miller Family - Fall Portraits','Carter Wedding','Campion Graduation 2026','Carter Executive Headshots','Peterson Newborn','Wilson Family + Dog'];
const workspaces=['Portraits and IDs','Weddings and Events','Schools and Corporate','Graduations','Commercial and Products','Sports and Groups'].map((name,i)=>({id:'w'+i,name,permissions,emailAddress:'studio@example.invalid',openCount:4})).concat([{id:'unassigned',name:'Unassigned',systemKey:'unassigned',permissions}]);
const jobs=names.map((title,i)=>({id:'j'+i,workspaceId:'w'+i,conversationId:'c'+i,title,clientName:['Miller Family','Emily & Daniel Carter','Campion College','Carter Corp','Peterson Family','Wilson Family'][i],clientId:'cl'+i,status:['Scheduled','Production','Photography','Editing','Scheduled','Scheduled'][i],eventDate:now.toISOString().slice(0,10),createdAt:iso(10,-6),assignedTo:'demo.sam',jobLead:'demo.sam',financialEvents:[{id:'charge'+i,kind:'Charge',amount:45000,createdAt:iso(10,-5)},{id:'pay'+i,kind:'Deposit',amount:22500,createdAt:iso(10,-3)}]}));
const tasks=jobs.flatMap((j,i)=>[0,1,2].map(n=>({id:'t'+i+'-'+n,jobId:j.id,title:['Cull images','Retouch portraits','Proof selection'][n],activityType:['Cull','Retouch','Proof'][n],status:['NotStarted','InProgress','Blocked'][n],priority:n===2?'High':'Normal',assignedTo:'demo.sam',collectionName:'Selects',scheduledAtUtc:iso(11),dueAtUtc:iso(17,n-1),createdAtUtc:iso(9,-3),blockedReason:n===2?'Awaiting client selection':null})));
const sessions=jobs.map((j,i)=>({id:'s'+i,jobId:j.id,title:j.title,startsAtUtc:iso(9+i),endsAtUtc:iso(11+i),location:i===4?'Client Home':'Studio A',status:'Scheduled',resourceIds:['r1']}));
const photographers=[{userName:'demo.sam',displayName:'Samantha Lee',isActive:true,scheduleResourceId:'r1',state:'Available',utilizationPercent:40,scheduledMinutes:120,remainingMinutes:180}];
const media=j=>({gallery:{id:j,defaultLens:'Auto',default_lens:'Auto',defaultCollection:'All'},subjects:[{id:'sub'+j,name:'Sarah Miller',reference:'P001',productionStatus:'Ready',collectionName:'Selects'}],collections:['Selects','Proofs','Finals'],assets:Array.from({length:20},(_,i)=>({id:'a'+j+i,assetKey:'photo'+i,versionId:'v'+j+i,contentType:'image/jpeg',collection:'Selects',fileName:'Portrait '+i+'.jpg',productionStatus:['Needs Editing','Ready','Needs Review'][i%3],assignedTo:'demo.sam',subjects:['sub'+j],status:'Needs Editing',version:1,versions:[{id:'v'+j+i,versionNumber:1,contentType:'image/jpeg'}]})),unmatched:[]});
function api(u){const p=u.pathname;
 if(p==='/api/workspace-access')return workspaces.map(workspace=>({workspace,permissions:workspace.permissions}));
 if(p==='/api/auth/me')return {user:{userName:'demo.sam',displayName:'Samantha Lee',isAdministrator:true}};
 if(p==='/api/workspaces'||p==='/api/frontdesk/workspaces')return workspaces;
 if(p==='/api/jobs'||p==='/api/today/jobs')return jobs;
 if(p==='/api/today/tasks')return tasks;
 if(p==='/api/today/media-summary')return jobs.map(j=>({jobId:j.id,assetCount:20,thumbnailVersionId:'v'+j.id+'0'}));
 if(/^\/api\/jobs\/j\d$/.test(p))return jobs.find(j=>j.id===p.split('/').pop());
 if(p.endsWith('/access'))return {permissions};
 if(p.endsWith('/appearance'))return {color:'#b99750',icon:'folder'};
 if(p.endsWith('/media')){const snapshot=media(p.split('/')[3]),limit=Number(u.searchParams.get('limit')||100);return {...snapshot,totalAssets:snapshot.assets.length,assets:snapshot.assets.slice(0,limit),hasMore:snapshot.assets.length>limit};}
 if(p.endsWith('/subjects'))return media(p.split('/')[3]).subjects;
 if(p.includes('/activities'))return p.includes('/jobs/')?tasks.filter(t=>t.jobId===p.split('/')[3]):tasks;
 if(p==='/api/schedule/entries'||p==='/api/today/schedule')return sessions;
 if(p.includes('/shoots'))return [];
 if(p==='/api/photographers')return photographers;
 if(p==='/api/studio-employees')return photographers.map(x=>({userName:x.userName,displayName:x.displayName,isActive:true,isAssignable:true,roles:['Photographer']}));
 if(p==='/api/schedule/resources')return [{id:'r1',name:'Samantha Lee',resourceType:'Photographer',isActive:true},{id:'kit',name:'Canon EOS R5',resourceType:'Equipment',isActive:true,notes:'Available'}];
 if(p==='/api/accounts/invoices')return jobs.map((j,i)=>({id:'inv'+i,jobId:j.id,invoiceNumber:'2026-00'+i,clientName:j.clientName,totalAmount:45000,status:i?'Issued':'Overdue'}));
 if(p==='/api/security/global-role-me')return {frontDesk:true,permissions};
 if(p==='/api/frontdesk/clients')return jobs.map((j,i)=>({id:j.clientId,displayName:j.clientName,email:'client'+i+'@example.invalid',phone:'876-555-000'+i}));
 if(p==='/api/notifications')return jobs.map((j,i)=>({id:'n'+i,subject:j.title,eventType:'WorkspaceMessage',body:'Please confirm our session and delivery timing.',referenceType:'Job',referenceId:j.id,isRead:false,createdAtUtc:iso(10)}));
 if(p.includes('/sending-identities'))return {identities:[{type:'Workspace',isDefault:true},{type:'Self'}]};
 if(p.startsWith('/api/conversations/')&&p.endsWith('/attachments'))return [];
 if(p.includes('/conversations/c'))return {id:'c0',channel:'Email',subject:'Client session',contactName:'Sarah Miller',messages:[{id:'m1',body:'Please confirm our session and package.',authorDisplayName:'Sarah Miller',visibility:'External',createdAt:iso(10,-1)}]};
 if(p.includes('/messages/threads/Job/'))return {id:'thread'};if(p.includes('/messages/threads/'))return [];
 if(p.includes('settings'))return {};
 return [];
}

test('the full shell loads every operational screen with a shared data graph',async t=>{const errors=[],vc=new VirtualConsole();vc.on('jsdomError',e=>errors.push(e.message));const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'http://studio.test/',runScripts:'outside-only',virtualConsole:vc,pretendToBeVisual:true}),w=dom.window;t.after(()=>dom.window.close());
 w.HTMLDialogElement.prototype.showModal=function(){this.open=true};w.HTMLDialogElement.prototype.close=function(){this.remove()};w.HTMLElement.prototype.scrollIntoView=function(){};w.alert=e=>errors.push(String(e));w.fetch=async url=>({ok:true,status:200,json:async()=>api(new URL(url,'http://studio.test')),text:async()=>JSON.stringify(api(new URL(url,'http://studio.test')))});
 for(const script of w.document.querySelectorAll('script[src]'))require('vm').runInContext(require('./studio-script-fixture.cjs')(new URL(script.src).pathname),dom.getInternalVMContext());
 await new Promise(r=>setTimeout(r,25));await w.studioTodayReady;
 for(const view of ['today','workspaces','pipeline','production','frontdesk','finance','management','admin']){try{await w.studioNavigate(view);assert.ok(w.document.querySelector('#genericView').textContent.trim().length>0,view);}catch(e){errors.push(view+': '+e.message)}}
 await w.studioNavigate('workspace-management');w.document.querySelector('[data-create-workspace]').click();const routeField=w.document.getElementById('workspaceRouteKey');assert.ok(routeField.closest('details'));assert.equal(routeField.closest('details').open,false);
 await w.studioNavigate('about');let finishDemo;const held=new Promise(r=>finishDemo=r),writes=[];w.prompt=()=> 'REMOVE STUDIO DEMO DATA';
 const loader=w.studioLoadDemoData;w.studioLoadDemoData=async(progress,options)=>{writes.push(options);await held;return {message:'Full demo generated.'};};
 const demoButton=w.document.querySelector('[data-load-demo]'),loadingDemo=demoButton.onclick();assert.equal(demoButton.disabled,true);assert.match(w.document.querySelector('[data-demo-status]').textContent,/Starting full demo reset/);
 finishDemo();await loadingDemo;assert.equal(demoButton.disabled,false);assert.deepEqual(JSON.parse(JSON.stringify(writes)),[{reset:true}]);assert.match(w.document.querySelector('[data-demo-status]').textContent,/demo generated/);
 w.studioLoadDemoData=async()=>{throw Error('Rolled back');};await demoButton.onclick();assert.equal(demoButton.disabled,false);assert.match(w.document.querySelector('[data-demo-status]').textContent,/Rolled back/);w.studioLoadDemoData=loader;
 try{await w.openWorkspaceJob('j0');await new Promise(r=>setTimeout(r,25));assert.ok(w.document.querySelector('.job-detail-board'),w.document.getElementById('detailPane').textContent);assert.equal(w.document.querySelectorAll('.jd-photo').length,12)}catch(e){errors.push('job: '+e.message)}
 dom.window.close();assert.deepEqual(errors,[]);});

test('cold workspace opening loads server permissions and presents actual denial in the shared view',async()=>{
 const errors=[],requests=[],vc=new VirtualConsole();let denied=false;
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'http://studio.test/',runScripts:'outside-only',virtualConsole:vc,pretendToBeVisual:true}),w=dom.window;
 w.HTMLDialogElement.prototype.showModal=function(){this.open=true};w.HTMLDialogElement.prototype.close=function(){this.remove()};w.HTMLElement.prototype.scrollIntoView=function(){};w.alert=e=>errors.push(String(e));
 w.fetch=async url=>{const u=new URL(url,'http://studio.test');requests.push(u.pathname);const body=u.pathname==='/api/workspace-access'?workspaces.map(workspace=>({workspace,permissions:{...permissions,view:!denied}})):api(u);return {ok:true,status:200,json:async()=>body,text:async()=>JSON.stringify(body)};};
 try{
  for(const script of w.document.querySelectorAll('script[src]'))require('vm').runInContext(require('./studio-script-fixture.cjs')(new URL(script.src).pathname),dom.getInternalVMContext());
  await new Promise(r=>setTimeout(r,25));await w.studioTodayReady;
  assert.ok(w.eval('workspaceCache.length')>=0);
  await w.studioNavigate('workspaces');
  assert.ok(requests.includes('/api/workspace-access'));
  await w.document.querySelector('[data-open-workspace="w0"]').onclick();
  assert.equal(w.eval('selectedWorkspace'),'w0');assert.deepEqual(errors,[]);
  assert.equal(w.document.getElementById('conversationView').classList.contains('hidden'),true);
  assert.equal(w.document.body.dataset.studioView,'jobs');
  assert.equal(w.history.state.studioWorkspaceId,'w0');
  denied=true;await w.studioNavigate('workspaces');
  await w.document.querySelector('[data-open-workspace="w1"]').onclick();
  assert.deepEqual(errors,[]);assert.match(w.document.getElementById('genericView').textContent,/You don’t have access to this page/);
  assert.equal(w.eval('selectedWorkspace'),'w0');
  assert.equal(w.document.getElementById('genericView').classList.contains('hidden'),false);
 }finally{dom.window.close();}
});



