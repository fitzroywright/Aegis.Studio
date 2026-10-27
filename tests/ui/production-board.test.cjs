const {test}=require('node:test'),assert=require('node:assert/strict'),{JSDOM}=require('jsdom'),fs=require('node:fs'),path=require('node:path');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot'),settle=()=>new Promise(r=>setImmediate(r));
function setup(t,data){
 const dom=new JSDOM('<body data-studio-view="production"><div id="genericView"></div><div id="conversationView"></div><span id="studioFooterRate"></span></body>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());let revision=0;
 Object.assign(w,{genericView:w.document.getElementById('genericView'),conversationView:w.document.getElementById('conversationView'),studioFirstName:v=>v,loadWorkspaces:async()=>[{id:'w',name:'Work'}],alert:e=>{w.errors.push(e);},errors:[],money:String,studioNavigate:async(view,options)=>w.navigation.push({view,options}),navigation:[]});
 const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));w.esc=esc;
 w.StudioPage={begin:()=>{const at=++revision;return ()=>at===revision&&w.document.body.dataset.studioView==='production';},escape:esc,html:o=>'<aside data-overview>'+o.summary+'</aside><section class="st-board"><div class="st-content">'+o.content+'</div></section>',bind(){},query:(host,s)=>host.querySelector(s),queryAll:(host,s)=>host.querySelectorAll(s)};
 const calls=[];w.json=async url=>{calls.push(url);if(url==='/api/production/workload')return data;if(url==='/api/production/media-summary')return [];if(url.startsWith('/api/production/work-orders?'))return data.orders.filter(o=>o.id===new URL(url,'https://studio.test').searchParams.get('orderIds')).map(o=>({...o,tasks:data.activities.filter(a=>a.workOrderId===o.id)}));throw Error(url);};
 for(const file of ['workload-overview.js','commercial.js'])w.eval(fs.readFileSync(path.join(root,file),'utf8'));
 const opened=[];w.openCommercialWorkOrder=async o=>opened.push(o);return {w,calls,opened};
}
function fixture(){const data={jobs:[{id:'j',workspaceId:'w',status:'Active Production',title:'School portraits'}],activities:[],orders:[],items:[],eligibility:[],productionJobIds:['j']};return data;}
function order(data,id,status='NotStarted',extra={}){data.orders.push({id,jobId:'j',quantity:1,product:'Portraits',activityIds:[id+'-task']});data.activities.push({id:id+'-task',jobId:'j',workOrderId:id,title:'Retouch',activityType:'Retouch',status,stepOrder:1,...extra});return data.activities.at(-1);}
const lane=(w,key)=>w.genericView.querySelector('[data-production-lane="'+key+'"]');
function addJob(data,id,status,taskStatus=null){data.jobs.push({id,title:'Job '+id,clientName:'Client '+id,status,workspaceId:'w'});if(taskStatus)data.activities.push({id:id+'-task',jobId:id,title:'Retouch '+id,activityType:'Retouch',status:taskStatus});}
test('default stage lists include all jobs and use compact rows without job cards',async t=>{
 const data=fixture();data.jobs=[];for(const [id,status] of [['ready','Awaiting Upload'],['progress','Active Production'],['qc','Awaiting Production Review'],['redo','Changes Requested'],['finished','Completed']])addJob(data,id,status);
 const {w,calls}=setup(t,data);await w.showCatalogProduction();
 for(const [key,id] of [['ready','ready'],['progress','progress'],['qc','qc'],['attention','redo'],['complete','finished']])assert.ok(lane(w,key).querySelector('tr[data-production-job="'+id+'"]'));
 assert.equal(w.genericView.querySelectorAll('[data-production-job]').length,5);assert.equal(w.genericView.querySelectorAll('.production-order-card').length,0);assert.match(w.genericView.textContent,/Client qc/);assert.equal(calls.filter(u=>u.includes('media')||u.includes('work-orders')).length,0);
});
test('a large Ready list cannot hide jobs in other stages and each column pages locally',async t=>{
 const data=fixture();data.jobs=[];for(let i=0;i<65;i++)addJob(data,'r'+i,'Pending');addJob(data,'p','Active Production');addJob(data,'q','Awaiting Approval');addJob(data,'x','Changes Requested');const {w,calls}=setup(t,data);await w.showCatalogProduction();
 assert.equal(lane(w,'ready').querySelector('header>span').textContent,'65');assert.equal(lane(w,'ready').querySelectorAll('[data-production-job]').length,20);
 for(const [key,id] of [['progress','p'],['qc','q'],['attention','x']])assert.ok(lane(w,key).querySelector('[data-production-job="'+id+'"]'));
 lane(w,'ready').querySelector('[data-production-more]').click();assert.equal(lane(w,'ready').querySelectorAll('[data-production-job]').length,40);assert.equal(calls.filter(u=>u==='/api/production/workload').length,1);
});
test('a job appears once even with multiple orders and blocked future tasks',async t=>{
 const data=fixture();order(data,'order-1','InProgress');order(data,'order-2','Blocked',{activityType:'QC',blockedReason:'Waiting for prerequisite'});
 const {w}=setup(t,data);await w.showCatalogProduction();assert.equal(w.genericView.querySelectorAll('[data-production-job="j"]').length,1);assert.ok(lane(w,'progress').querySelector('[data-production-job="j"]'));assert.equal(lane(w,'attention').querySelectorAll('[data-production-job]').length,0);
});
test('default entry clears an old Rework filter while an explicitly requested scope is retained',async t=>{
 const data=fixture();addJob(data,'redo','Changes Requested','Blocked');const {w}=setup(t,data);w.studioWorkloadSelection={filter:'rework'};await w.showCatalogProduction();assert.equal(w.genericView.querySelectorAll('[data-production-job]').length,2);assert.equal(w.studioWorkloadSelection.filter,'');
 w.studioWorkloadSelection={filter:'waiting'};w.studioProductionScopeRequested=true;await w.showCatalogProduction();assert.equal(w.genericView.querySelectorAll('[data-production-job]').length,1);assert.ok(lane(w,'attention').querySelector('[data-production-job="redo"]'));
});
test('search and group filters use the snapshot and Refresh preserves the chosen scope',async t=>{
 const data=fixture();addJob(data,'qc','Awaiting Production Review','NotStarted');data.activities[0].dueAtUtc='2020-01-01T00:00:00Z';const {w,calls}=setup(t,data);await w.showCatalogProduction();const selector=w.genericView.querySelector('[data-workload-filter-select]');selector.value='overdue';selector.dispatchEvent(new w.Event('change'));assert.equal(w.genericView.querySelectorAll('[data-production-job]').length,1);
 w.genericView.querySelector('[data-production-refresh]').click();await settle();assert.equal(w.studioWorkloadSelection.filter,'overdue');assert.equal(w.genericView.querySelectorAll('[data-production-job]').length,1);assert.equal(calls.filter(u=>u==='/api/production/workload').length,2);
 w.genericView.querySelector('[data-workload-reset]').click();const search=w.genericView.querySelector('[data-production-search]');search.value='Client qc';search.dispatchEvent(new w.Event('input'));await new Promise(r=>setTimeout(r,210));assert.equal(w.genericView.querySelectorAll('[data-production-job]').length,1);assert.equal(calls.filter(u=>u==='/api/production/workload').length,2);
});
test('clicking or pressing Enter on a row opens Job Details',async t=>{
 const {w}=setup(t,fixture());await w.showCatalogProduction();const row=w.genericView.querySelector('[data-production-job="j"]');row.click();row.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Enter',bubbles:true}));assert.equal(w.navigation.length,2);assert.equal(w.navigation[0].view,'workspaces');assert.equal(w.navigation[0].options.jobId,'j');
});
test('the Completed toggle controls completed jobs without removing jobs that have no tasks',async t=>{
 const data=fixture();addJob(data,'done','Delivered');addJob(data,'new','Pending');const {w}=setup(t,data);await w.showCatalogProduction();assert.equal(w.genericView.querySelectorAll('[data-production-job]').length,3);w.genericView.querySelector('[data-completed]').click();assert.equal(lane(w,'complete'),null);assert.equal(w.genericView.querySelectorAll('[data-production-job]').length,2);assert.ok(lane(w,'ready').querySelector('[data-production-job="new"]'));
});
test('current job stage wins over overdue or blocked future work',async t=>{
 const data=fixture();data.jobs=[];for(const [id,status] of [['ready','Scheduled'],['progress','Active Production'],['qc','Awaiting Approval'],['redo','Changes Requested']])addJob(data,id,status,'Blocked');for(const a of data.activities){a.dueAtUtc='2020-01-01T00:00:00Z';a.blockedReason='Waiting for prerequisite';}
 const {w}=setup(t,data);await w.showCatalogProduction();for(const [key,id] of [['ready','ready'],['progress','progress'],['qc','qc'],['attention','redo']])assert.ok(lane(w,key).querySelector('[data-production-job="'+id+'"]'));
});
test('load failures show Retry and stale responses cannot replace a newer list',async t=>{
 const data=fixture(),{w}=setup(t,data),base=w.json;let release;w.json=url=>url==='/api/production/workload'?new Promise(r=>release=r):base(url);const old=w.showCatalogProduction();await settle();w.json=base;await w.showCatalogProduction();release({...fixture(),jobs:[]});await old;assert.ok(w.genericView.querySelector('[data-production-job="j"]'));
 w.json=async()=>{throw Error('Database unavailable');};await w.showCatalogProduction();assert.match(w.genericView.textContent,/Database unavailable/);assert.ok(w.genericView.querySelector('[data-production-retry]'));
});
