// Shared deterministic API fixture for Job Details interaction and viewport checks.
const fs=require('fs');
const path=require('path');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const scripts=['studio-message-card.js','job-handoff.js','job-workspace.js','media-production.js','connected-workflow.js','job-details.js','workspace-outlook.js','studio-shell-nav.js'];
function setup(w){
 const jobs=[{id:'job',workspaceId:'workspace',conversationId:'conversation',title:'Wedding selects review',clientName:'Williams & Clarke',status:'Awaiting Production Review',eventDate:'2026-10-11'},{id:'job2',workspaceId:'workspace',conversationId:'conversation2',title:'Graduation portraits',clientName:'School',status:'Awaiting Scheduling'}];
 const calls=[],alerts=[];
 const fixture={jobs,calls,alerts,canEdit:true,failSubjects:false,delaySubjects:null,jobNotes:{}};
 const subjects=[{id:'person',name:'Morgan',reference:'001',status:'Photographed',imageCount:12}];
 const assets=Array.from({length:40},(_,i)=>({id:'image-'+i,assetKey:'image-'+i,versionId:'version-'+i,fileName:'Photo '+i+'.jpg',contentType:'image/jpeg',sizeBytes:1024,version:1,status:'Ready',subjects:['person'],collection:'Selections',tags:[]}));
 const workflow=job=>({data:{stage:job.status,owner:'studio.admin',enteredAt:new Date(Date.now()-180000000).toISOString(),sessions:[],history:[],submissions:[],revision:1},userName:'studio.admin',canManage:fixture.canEdit,canReview:fixture.canEdit,canAssignOwner:fixture.canEdit,canFrontDesk:fixture.canEdit});
 Object.assign(w,{showWorkspaces:async()=>{},workspaceCache:[{id:'workspace',name:'Demo | Commercial and Products',permissions:{accessWorkspace:true,view:true}}],selectedWorkspace:'workspace',currentWorkspacePermissions:{},selectedConversation:null,openConversation:async()=>{},formatBytes:()=>'',esc:s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])),studioFirstName:p=>String(typeof p==='string'?p:p?.displayName||'Studio').split(/[ .]/)[0],studioRememberPeople(){},alert:message=>alerts.push(message),openJobDetailsEditor:async()=>{},money:n=>'JMD '+n,studioNavigate:async view=>{w.document.body.dataset.studioView=view;},loadWorkspaces:async()=>{}});
 for(const id of ['conversationList','detailPane','workspaceTitle','genericView'])w[id]=w.document.getElementById(id);
 w.fetch=async()=>({ok:true,json:async()=>({user:{userName:'studio.admin',displayName:'Studio Admin'},channel:'Email',messages:[{id:'external',author:'Williams',body:'Please confirm availability.',createdAt:'2026-10-10T12:00:00Z',visibility:'External'}],identities:[{type:'Workspace'},{type:'Self'}]})});
 w.json=async(url,options)=>{
  calls.push({url,options});const base=url.split('?')[0];
  if(base==='/api/jobs')return jobs;
  if(base==='/api/jobs/job'||base==='/api/jobs/job2')return jobs.find(j=>base.endsWith('/'+j.id));
  if(base.endsWith('/connected-workflow'))return workflow(jobs.find(j=>base.includes('/'+j.id+'/'))||jobs[0]);
  if(base.endsWith('/subjects')){if(fixture.delaySubjects)await fixture.delaySubjects;if(fixture.failSubjects)throw new Error('Subjects unavailable');return subjects;}
  if(/\/media\/assets\/[^/]+$/.test(base)&&options?.method==='PUT'){const asset=assets.find(a=>base.endsWith('/'+a.id));if(!asset)throw Error('Unknown photo');Object.assign(asset,JSON.parse(options.body));return null;}
  if(base.endsWith('/media')){const q=new URL(url,'https://studio.test').searchParams;const filtered=assets.filter(a=>(!q.get('subjectId')||a.subjects.includes(q.get('subjectId')))&&(!q.get('collection')||a.collection===q.get('collection'))).sort((a,b)=>a.id.localeCompare(b.id));const matching=filtered.filter(a=>!q.get('after')||a.id>q.get('after'));const limit=Number(q.get('limit')||100);return {assets:matching.slice(0,limit),subjects,unmatched:[],collections:['Selections'],collectionCounts:{Selections:40},gallery:{default_lens:'Auto'},totalAssets:filtered.length,totalSubjects:1,hasMore:matching.length>limit};}
  if(base.endsWith('/access'))return {permissions:{editJob:fixture.canEdit,manageProduction:fixture.canEdit}};
  if(/\/notes\/[^/]+$/.test(base)&&options?.method==='PUT'){const jobId=base.split('/')[3],id=base.split('/')[5],request=JSON.parse(options.body),note=(fixture.jobNotes[jobId]||[]).find(n=>n.id===id);if(!note)throw Error('Not found');if(note.revision!==request.expectedRevision)throw Error('This note has changed. Reload it before saving your edit.');Object.assign(note,{body:request.body,revision:note.revision+1,updatedBy:'studio.admin',updatedAtUtc:'2026-10-12T04:00:00Z'});return null;}
  if(base.endsWith('/notes')){
   const jobId=base.split('/')[3];fixture.jobNotes[jobId]??=[];
   if(options?.method==='POST'){const note=JSON.parse(options.body);if(!fixture.jobNotes[jobId].some(n=>n.id===note.id))fixture.jobNotes[jobId].push({...note,createdBy:'studio.admin',createdAtUtc:'2026-10-12T03:00:00Z',revision:1});return {id:note.id};}
   return fixture.jobNotes[jobId];
  }
  if(url.includes('/threads/Job/'))return {id:'thread'};
  if(base==='/api/auth/me')return {user:{userName:'studio.admin',displayName:'Studio Admin'}};
  if(base==='/api/operations/activities')return [{id:'task',title:'Retouch portraits',status:'NotStarted',assignedTo:'studio.admin'}];
  if(base==='/api/schedule/entries')return [{location:'Kingston Studio'}];
  if(base.endsWith('/operations/audit'))return [{action:'Job created',detail:'Front Desk booking',createdAtUtc:'2026-10-10T12:00:00Z'}];
  if(base==='/api/studio-employees')return [{userName:'studio.admin',displayName:'Studio Admin',roles:[]}];
  if(options?.method==='POST')return {id:'sent',messageId:'sent'};
  return [];
 };
 w.auditFixture=fixture;return fixture;
}
function environment(t){
 const {JSDOM}=require('jsdom');
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{runScripts:'outside-only',url:'https://studio.test'});
 const w=dom.window;t.after(()=>w.close());
 w.HTMLElement.prototype.scrollIntoView=function(){};
 w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};
 w.HTMLDialogElement.prototype.close=function(){this.open=false;this.dispatchEvent(new w.Event('close'));};
 const fixture=setup(w);
 for(const script of scripts)w.eval(fs.readFileSync(path.join(root,script),'utf8'));
 return {w,fixture,dom};
}
module.exports={environment,setup,scripts,root};


