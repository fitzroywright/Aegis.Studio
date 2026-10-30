const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot'),read=name=>fs.readFileSync(path.join(root,name),'utf8'),settle=()=>new Promise(r=>setImmediate(r));
const row=(changes={})=>({kind:'job',entityId:'j',jobId:'j',conversationId:'c',workspaceId:'w',clientId:'client',entityTitle:'Family portraits',action:{key:'session-plan',title:'Complete Session Plan',reason:'No subjects recorded.',route:'session-plan',after:'Assign photographer'},assignedTo:'lisa',dueAtUtc:null,waitingReason:null,version:0,canManage:true,...changes});
function setup(t){
 const dom=new JSDOM('<body><main></main></body>',{runScripts:'outside-only',url:'https://studio.test',pretendToBeVisual:true}),w=dom.window;t.after(()=>w.close());const calls=[],actions=[];let current=row();
 w.studioDialog=(title,html)=>{const dialog=w.document.createElement('dialog');dialog.innerHTML='<h2>'+title+'</h2>'+html;dialog.close=()=>{dialog.dispatchEvent(new w.Event('close'));dialog.remove();};w.document.body.append(dialog);return dialog;};
 w.studioNavigate=async(...args)=>actions.push(args);w.json=async(url,options)=>{calls.push({url,options});if(url.startsWith('/api/work/next-actions'))return options?current:[current];if(url.startsWith('/api/studio-employees'))return[{userName:'lisa',displayName:'Lisa'}];throw Error(url);};w.eval(read('studio-next-actions.js'));
 return {w,calls,actions,host:w.document.querySelector('main'),set:value=>current=value};
}
test('due labels use Jamaica time, preserve missing dates and identify overdue work',t=>{
 const {w}=setup(t),now=Date.parse('2026-10-30T16:00:00Z');
 assert.equal(w.StudioNextActions.due(row(),now).label,'No due date');
 assert.equal(w.StudioNextActions.due(row({dueAtUtc:'2026-10-31T04:59:59Z'}),now).label,'Today');
 assert.equal(w.StudioNextActions.due(row({dueAtUtc:'2026-10-30T15:00:00Z'}),now).overdue,true);
});
test('next action shows escaped reason, responsibility, due date and following step',t=>{
 const {w,host}=setup(t);w.StudioNextActions.mount(host,row({waitingReason:'Waiting for <client>',action:{title:'Reply <script>',reason:'<img src=x>',route:'reply',after:'Prepare quote'}}));
 assert.match(host.textContent,/Next required action.*Reply <script>.*<img src=x>.*Responsible: lisa.*No due date.*Waiting for <client>.*Prepare quote/s);
 assert.equal(host.querySelector('script,img'),null);assert.ok(host.querySelector('[data-next-edit]'));
 w.StudioNextActions.mount(host,row({canManage:false}));assert.equal(host.querySelector('[data-next-edit]'),null);
});
test('activation refreshes the server projection before opening the current action',async t=>{
 const {w,set,actions}=setup(t),stale=row();set(row({action:{key:'session-plan',route:'session-plan'}}));await w.StudioNextActions.activate(stale);
 assert.equal(actions[0][0],'session-plan');assert.equal(actions[0][1].jobId,'j');
 set(null);await assert.rejects(()=>w.StudioNextActions.activate(stale),/no outstanding action/);
});
test('assignment saves a versioned action-specific record with a Jamaica end-of-day deadline',async t=>{
 const {w,calls}=setup(t);const dialog=await w.StudioNextActions.edit(row());const form=dialog.querySelector('form');form.elements.due.value='2026-10-30';form.elements.waiting.value='Waiting for client';await form.onsubmit({preventDefault(){}});
 const save=calls.find(c=>c.options?.method==='PUT'),payload=JSON.parse(save.options.body);
 assert.equal(save.url,'/api/work/next-actions/job/j');assert.equal(payload.actionKey,'session-plan');assert.equal(payload.expectedVersion,0);assert.equal(payload.dueAtUtc,'2026-10-31T04:59:59.000Z');assert.equal(payload.waitingReason,'Waiting for client');assert.equal(dialog.isConnected,false);
});
test('an assignment conflict preserves the entered details and enables retry',async t=>{
 const {w}=setup(t),api=w.json;w.json=(url,options)=>options?Promise.reject(Error('This action changed. Refresh.')):api(url,options);
 const dialog=await w.StudioNextActions.edit(row()),form=dialog.querySelector('form');form.elements.waiting.value='Keep my draft';await form.onsubmit({preventDefault(){}});
 assert.equal(form.elements.waiting.value,'Keep my draft');assert.match(form.textContent,/This action changed/);assert.equal(form.querySelector('button').disabled,false);
});
test('enquiry popup keeps a failed reply draft and focuses its existing composer',async t=>{
 const {w,set}=setup(t);set(row({kind:'enquiry',entityId:'c',jobId:null,action:{key:'reply',route:'reply',title:'Reply to enquiry',reason:'New message'}}));
 const api=w.json;w.json=async(url,options)=>{if(url==='/api/conversations/c')return {id:'c',subject:'Graduation',contactName:'School',contactAddress:'school@example.com',channel:'Email',messages:[{author:'School',visibility:'External',body:'Pricing?',createdAt:'2026-10-30T14:00:00Z'}]};if(url.endsWith('/sending-identities'))return{identities:[{type:'Workspace',label:'Graduations'}]};if(url.endsWith('/messages')&&options)throw Error('Delivery failed');return api(url,options);};
 const dialog=await w.StudioNextActions.openEnquiry('c'),form=dialog.querySelector('.studio-enquiry-reply');form.elements.body.value='Your reply draft';await form.onsubmit({preventDefault(){}});assert.equal(form.elements.body.value,'Your reply draft');assert.match(form.textContent,/Delivery failed/);
 dialog.querySelector('[data-next-activate]').click();assert.equal(w.document.activeElement,form.elements.body);assert.equal(w.document.querySelectorAll('dialog').length,1);
});
test('an enquiry converted by another user opens its linked job without offering another booking',async t=>{
 const {w,actions}=setup(t),api=w.json;w.json=async(url,options)=>url==='/api/conversations/c'?{id:'c',jobId:'linked'}:url.endsWith('/sending-identities')?{identities:[]}:api(url,options);
 const dialog=await w.StudioNextActions.openEnquiry('c');assert.equal(dialog.querySelector('form'),null);dialog.querySelector('[data-linked-job]').click();assert.equal(actions[0][1].jobId,'linked');
});
test('Front Desk All view shows actions and filters responsible and due without replacing appointment controls',async t=>{
 const setupFrontDesk=require('./frontdesk-reception-fixture.cjs'),{w,fixture,render}=setupFrontDesk(t);
 w.eval(read('studio-next-actions.js'));const actions=[row({entityId:'expected',jobId:'expected',assignedTo:'lisa'}),row({entityId:'arrived',jobId:'arrived',assignedTo:'paul',dueAtUtc:'2020-01-01T00:00:00Z'})];
 fixture.custom=async url=>url==='/api/work/next-actions'?actions:undefined;
 await render();w.document.querySelector('[data-fd-tab=all]').click();assert.match(w.document.querySelector('.fd-next-table thead').textContent,/Next required action.*Responsible.*Due/);
 assert.equal(w.document.querySelectorAll('[data-fd-row-open]').length,5);assert.equal(w.document.querySelector('.fd-selected-request'),null);
 const owner=w.document.querySelector('[data-fd-action-owner]');owner.value='paul';owner.dispatchEvent(new w.Event('change'));assert.equal(w.document.querySelectorAll('[data-fd-row-open]').length,1);assert.match(w.document.querySelector('tbody').textContent,/Overdue/);
});
test('Needs Attention replaces duplicate status summaries with the actual next action',async t=>{
 const {w}=setup(t),current=row({action:{key:'review',title:'Review source photographs',reason:'Awaiting review'}});const api=w.json;
 w.json=async(url,options)=>url==='/api/today/jobs'?[{id:'j',title:'Family portraits',status:'Awaiting Production Review'}]:url.includes('/activities')?[]:url==='/api/production/workload'||url==='/api/messages/attention'?{}:url==='/api/work/next-actions'?[current]:url.startsWith('/api/work/next-actions?')?[current]:[];
 w.eval(read('studio-attention.js'));const result=await w.StudioAttention.load();assert.equal(result.items.length,1);assert.equal(result.items[0].id,'next:job:j');assert.equal(result.items[0].nextAction.action.key,'review');assert.match(result.items[0].context,/No due date/);
});
test('Job Details shows the same server-projected action used by Front Desk',async t=>{
 const {environment}=require('./job-detail-fixture.cjs'),{w}=environment(t);
 w.eval(read('job-workflow-display.js'));w.eval(read('studio-next-actions.js'));const api=w.json;
 w.json=(url,options)=>url.startsWith('/api/work/next-actions')?Promise.resolve([row({entityId:'job',jobId:'job',assignedTo:'Paul',action:{key:'classify-media',title:'Classify uploaded photographs',reason:'23 photographs need subjects.',route:'media'}})]):api(url,options);
 await w.openWorkspaceJob('job');await new Promise(r=>setTimeout(r,70));
 const next=w.document.querySelector('.job-detail-heading .studio-next-action');assert.ok(next);assert.match(next.textContent,/Classify uploaded photographs.*23 photographs.*Paul/s);
 assert.equal(w.document.querySelectorAll('.job-detail-heading .studio-next-action').length,1);
});
