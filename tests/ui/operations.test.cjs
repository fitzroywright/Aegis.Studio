const {test}=require('node:test');const assert=require('node:assert/strict');const fs=require('node:fs');const path=require('node:path');const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
function setup(scripts=['management.js']){
 const dom=new JSDOM('<main id="genericView"></main><div id="conversationView"></div>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;
 Object.assign(w,{genericView:w.document.getElementById('genericView'),conversationView:w.document.getElementById('conversationView'),activateNavigation(){},esc:s=>String(s??''),money:n=>'JMD '+Number(n).toLocaleString(),studioFirstName:s=>String(s),studioRememberPeople(){},alert(){}});
 ['studio-page.js',...scripts].forEach(s=>w.eval(fs.readFileSync(path.join(root,s),'utf8')));return w;
}
const date=new Date().toISOString();
const jobs=[{id:'job',workspaceId:'workspace',clientId:'client',title:'Wedding',clientName:'Carter',status:'Production',financialEvents:[{kind:'Charge',amount:100,createdAt:date},{kind:'Deposit',amount:30,createdAt:date},{kind:'Payment',amount:50,createdAt:date},{kind:'Refund',amount:-10,createdAt:date},{kind:'Discount',amount:-5,createdAt:date}]}];
function endpoints(w){w.json=async u=>u==='/api/jobs/access'?{job:{manageProduction:true}}:u==='/api/production/media-summary'?[{jobId:'job',collection:'Selects',assetCount:1,completedCount:0,approvedCount:1,thumbnailVersionIds:[]}]:u==='/api/jobs'?jobs:u==='/api/accounts/invoices'?[{id:'invoice',jobId:'job',invoiceNumber:'001',clientName:'Carter',totalAmount:95,status:'Issued'}]:u==='/api/photographers'?[{userName:'staff',displayName:'Staff',scheduleResourceId:'resource',isActive:true}]:u.startsWith('/api/schedule/entries')?[{id:'session',jobId:'job',title:'Wedding',startsAtUtc:date,endsAtUtc:date,resourceIds:['resource'],status:'Scheduled'}]:u.endsWith('/media')?{assets:[]}:u==='/api/auth/me'?{user:{userName:'staff',isAdministrator:true}}:u==='/api/workspaces'?[{id:'workspace',name:'Weddings'}]:u==='/api/schedule/resources'?[]:u.endsWith('/access')?{permissions:{manageProduction:true}}:[];}
test('Finance reconciles signed refunds and discounts with the job balance',async()=>{
 const w=setup();endpoints(w);assert.deepEqual(JSON.parse(JSON.stringify(w.studioFinancials(jobs[0]))),{charges:95,received:70,balance:25});await w.showAccounts();
 assert.match(w.genericView.textContent,/JMD 70/);assert.match(w.genericView.textContent,/JMD 25/);assert.equal(w.document.querySelectorAll('.st-dashboard-grid>.op-card').length,4);
 let opened;w.openWorkspaceJob=async id=>opened=id;w.document.querySelector('[data-open-job]').click();assert.equal(opened,'job');
 const chart=w.studioRevenueChart([{kind:'Refund',amount:-100,createdAt:date}]);const fragment=new JSDOM(chart);assert.ok([...fragment.window.document.querySelectorAll('rect')].every(r=>Number(r.getAttribute('height'))>=0));
});
test('Management workload follows the same schedule resource IDs as Pipeline',async()=>{
 const w=setup();endpoints(w);w.pipelineStages=[['Production',['Production']]];await w.showManagement();assert.equal(w.document.querySelectorAll('.st-dashboard-grid>.op-card').length,8);assert.match(w.genericView.textContent,/0 open tasks · 1 sessions/);
 let route;w.studioNavigate=v=>route=v;w.document.querySelector('[data-stage]').click();assert.equal(route,'pipeline');assert.equal(w.pipelineSelectedStage,'Production');
});
test('Production filters collections and persists completion through the existing API',async()=>{
 const w=setup(['management.js','production.js']);endpoints(w);const read=w.json,calls=[];let status='NotStarted';
 w.json=async(u,o)=>{if(o){calls.push({u,body:JSON.parse(o.body)});status=JSON.parse(o.body).status;return {};}
 if(u==='/api/operations/activities')return [{id:'task',jobId:'job',activityType:'Retouch',title:'Retouch Selects',collectionName:'Selects',assignedTo:'staff',status,priority:'Normal',createdAtUtc:date}];
 if(u.endsWith('/media'))return {assets:[{collection:'Selects'},{collection:'Proofs'}]};return read(u,o);};
 await w.showProduction();let row=w.document.querySelector('tbody tr');assert.match(row.children[4].textContent,/^0\/1/);assert.match(row.children[4].textContent,/1 approved/);assert.ok(row.querySelector('[data-work-complete]'));
 await w.setOperationalActivityStatus('task','Completed');assert.equal(calls[0].u,'/api/operations/activities/task');assert.equal(calls[0].body.status,'Completed');assert.equal(w.document.querySelector('[data-work-complete]'),null);
});
test('folder ingest drains repeated directory batches and ignores non-photo files',async()=>{
 const w=setup(['media-production.js']);const file=name=>({isFile:true,file:done=>done({name,type:'image/jpeg'})});const batches=[[file('one.jpg')],[file('two.png'),file('notes.txt')],[]];
 const directory={isDirectory:true,createReader:()=>({readEntries:done=>done(batches.shift())})};const files=await w.studioDroppedPhotos({items:[{webkitGetAsEntry:()=>directory}],files:[]});assert.deepEqual(Array.from(files,f=>f.name),['one.jpg','two.png']);assert.equal(batches.length,0);
});

test('workspace integration settings are collapsed and save a routing override',async()=>{
 const w=setup(['workspaces.js']);const calls=[];
 w.json=async(u,o)=>{if(o){calls.push(JSON.parse(o.body));return {};}if(u==='/api/studio-employees?assignable=true')return [{userName:'staff',displayName:'Staff',roles:['Photographer']}];return {id:'workspace',name:'Weddings',emailAddress:'weddings@example.invalid',routeKey:'existing-route',isEnabled:true,sortOrder:7,defaultJobLead:'staff'};};
 w.studioDialog=(title,body)=>{const dialog=w.document.createElement('dialog');dialog.innerHTML=body;dialog.close=()=>dialog.remove();w.document.body.append(dialog);return dialog;};w.loadWorkspaces=async()=>{};w.studioNavigate=async()=>{};
 await w.editWorkspace('workspace');const dialog=w.document.querySelector('dialog'),form=dialog.querySelector('form'),route=form.elements.routeKey;
 assert.equal(route.value,'existing-route');assert.equal(route.closest('details').open,false);route.value='wedding-bookings';
 await form.onsubmit({preventDefault(){}});assert.equal(calls[0].routeKey,'wedding-bookings');assert.equal(calls[0].name,'Weddings');assert.equal(calls[0].sortOrder,7);assert.equal(calls[0].defaultJobLead,'staff');assert.equal(w.document.querySelector('dialog'),null);
});


test('Production QC displays the pinned source and records a rejection reason',async()=>{
 const w=setup(['media-production.js','production.js']);w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.dispatchEvent(new w.Event('close'));};w.showProduction=async()=>{};const writes=[];w.prompt=()=>"Colour needs reprint";
 w.json=async(u,o)=>{if(o){writes.push(JSON.parse(o.body));return {};}if(u.endsWith('/order-lines'))return [];return {quantity:3,completed:3,passed:0,total:1,rejected:writes.length,items:[{id:'item',sourceVersionId:'approved-v3',fileName:'Portrait.jpg',versionNumber:3,productCode:'PRINT-5X7',quantity:3,completedQuantity:3,qcState:writes.length?'Rejected':'Pending',qcReason:writes.length?'Colour needs reprint':null}]};};
 await w.openProductionItems({jobId:'job',id:'order',title:'Print order'});const link=w.document.querySelector('dialog a');assert.ok(link.href.endsWith('/approved-v3/content'));assert.match(link.textContent,/Approved v3/);await w.document.querySelector('[data-qc-reject]').onclick();assert.equal(writes[0].state,'Rejected');assert.equal(writes[0].reason,'Colour needs reprint');assert.match(w.document.querySelector('dialog').textContent,/Colour needs reprint/);w.close();
});

test('Finance filters signed records by method and staff without changing their balance',async()=>{
 const w=setup();endpoints(w);const read=w.json;
 w.json=async u=>u==='/api/jobs'?[{...jobs[0],financialEvents:[{kind:'Charge',amount:100,createdAt:date},{kind:'Payment',amount:70,createdAt:date,paymentMethod:'Cash',recordedBy:'Alice'},{kind:'Refund',amount:-10,createdAt:date,paymentMethod:'Card',recordedBy:'Bob'}]}]:read(u);
 await w.showAccounts();const method=w.document.querySelector('[data-finance-method]');method.value='Card';method.onchange();assert.match(w.document.querySelector('[data-finance-results]').textContent,/Refund/);assert.doesNotMatch(w.document.querySelector('[data-finance-results]').textContent,/Payment/);assert.match(w.document.querySelector('[data-reconcile]').textContent,/-10/);w.close();
});

