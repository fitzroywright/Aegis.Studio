const {test}=require('node:test');const assert=require('node:assert/strict');const fs=require('node:fs');const path=require('node:path');const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const flush=()=>new Promise(r=>setTimeout(r,0));
function setup(read){
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;
 Object.assign(w,{json:read,esc:s=>String(s??''),alert(){},loadWorkspaces:async()=>{throw Error('Workspace cache must not block startup');},showPipeline:async()=>{w.document.getElementById('genericView').innerHTML='<h1>Pipeline</h1>';},console:{debug(){},info(){},error(){}}});
 w.eval(fs.readFileSync(path.join(root,'studio-navigation.js'),'utf8'));return {w,dom};
}
const jobs=[{id:'job',title:'Carter Wedding',clientName:'Carter',status:'Production',taskCount:8,completedTaskCount:4,createdAt:new Date().toISOString(),jobLead:'staff'}];
const tasks=[{id:'task',jobId:'job',title:'Retouch portraits',status:'NotStarted',assignedTo:'staff',activityType:'Retouch',priority:'Normal'}];
function response(url){if(url==='/api/my-work/jobs')return jobs;if(url==='/api/my-work/tasks')return tasks;if(url==='/api/auth/me')return {user:{userName:'staff'}};if(url==='/api/my-work/media-summary')return [{jobId:'job',assetCount:320,thumbnailVersionId:'version'}];if(url.startsWith('/api/my-work/schedule'))return [{id:'session',jobId:'job',title:'Wedding shoot',startsAtUtc:new Date().toISOString(),endsAtUtc:new Date(Date.now()+3600000).toISOString(),status:'Scheduled'}];return [];}
test('shell and essential work appear before media; startup never downloads full galleries',async()=>{
 let release;const held=new Promise(r=>release=r),urls=[];let active=0,max=0;
 const {w,dom}=setup(async(url,options)=>{urls.push(url);assert.ok(options.headers['X-Studio-Startup']);active++;max=Math.max(max,active);try{if(url==='/api/my-work/media-summary')await held;return response(url);}finally{active--;}});
 await flush();assert.ok(w.document.querySelector('.mw-page'));await flush();assert.match(w.document.querySelector('[data-mw-region=tasks]').textContent,/Retouch portraits/);assert.match(w.document.querySelector('[data-mw-region=schedule]').textContent,/Carter Wedding/);assert.ok(w.document.querySelectorAll('.mw-list-panel').length>=5);
 assert.equal(urls.filter(u=>/\/jobs\/.*\/media$/.test(u)).length,0);assert.ok(max<=2);assert.ok(w.studioMyWorkDiagnostics.shellMs!==null);assert.ok(w.studioMyWorkDiagnostics.coreMs!==null);
 release();await w.studioMyWorkReady;assert.match(w.document.querySelector('[data-mw-region=pipeline]').textContent,/Carter Wedding/);assert.match(w.document.querySelector('[data-mw-region=tasks]').textContent,/Retouch portraits/);assert.ok(w.studioMyWorkDiagnostics.completeMs!==null);dom.window.close();
});
test('leaving My Work aborts enrichment and late replies cannot repaint Pipeline',async()=>{
 let release,signal;const held=new Promise(r=>release=r);
 const {w,dom}=setup(async(url,options)=>{if(url==='/api/my-work/media-summary'){signal=options.signal;await held;}return response(url);});await flush();await flush();assert.ok(signal);
 await w.studioNavigate('pipeline');assert.equal(signal.aborted,true);release();await w.studioMyWorkReady;assert.equal(w.document.getElementById('genericView').textContent,'Pipeline');assert.equal(w.document.body.dataset.studioView,'pipeline');dom.window.close();
});
test('a failed media summary has a working retry without removing sessions and tasks',async()=>{
 let attempts=0;const {w,dom}=setup(async url=>{if(url==='/api/my-work/media-summary'&&++attempts===1)throw Error('Timed out');return response(url);});await flush();await w.studioMyWorkReady;
 assert.ok(w.document.querySelector('[data-mw-retry=media]'));assert.match(w.document.querySelector('[data-mw-region=tasks]').textContent,/Retouch portraits/);w.document.querySelector('[data-mw-retry=media]').click();await flush();assert.equal(attempts,2);assert.match(w.document.querySelector('[data-mw-region=pipeline]').textContent,/Carter Wedding/);assert.equal(w.document.querySelector('[data-mw-retry=media]'),null);dom.window.close();
});

test('administrator can assign a job lead from My Work',async()=>{
 const calls=[];
 const read=async(url,options)=>{
  if(options?.method==='PUT'){calls.push({url,body:JSON.parse(options.body)});return {jobId:'job',jobLead:'lead.user'};}
  if(url==='/api/auth/me')return {user:{userName:'admin',displayName:'Admin User',isAdministrator:true}};
  if(url==='/api/studio-employees?assignable=true')return [{userName:'lead.user',displayName:'Lead User',roles:['Photographer']}];
  return response(url);
 };
 const {w,dom}=setup(read);
 w.studioDialog=(title,body)=>{const dialog=w.document.createElement('dialog');dialog.innerHTML=body;dialog.close=()=>dialog.remove();w.document.body.append(dialog);return dialog;};
 await w.studioMyWorkReady;
 const action=w.document.querySelector('[data-assign-job-lead]');assert.ok(action);
 action.click();await flush();
 const form=w.document.querySelector('[data-job-lead-form]');assert.ok(form);form.elements.userName.value='lead.user';
 await form.onsubmit({preventDefault(){}});
 assert.deepEqual(calls,[{url:'/api/jobs/job/lead',body:{userName:'lead.user'}}]);
 assert.equal(w.document.querySelector('[data-job-lead-form]'),null);
 dom.window.close();
});
test('non-administrator does not see the My Work job lead action',async()=>{
 const {w,dom}=setup(async(url,options)=>response(url));await w.studioMyWorkReady;
 assert.equal(w.document.querySelector('[data-assign-job-lead]'),null);dom.window.close();
});
