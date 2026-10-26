const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const read=name=>fs.readFileSync(path.join(root,name),'utf8');
const settle=()=>new Promise(resolve=>setImmediate(resolve));

async function setup(t,view){
 const dom=new JSDOM(read('index.html'),{url:'https://studio.test/',runScripts:'outside-only',pretendToBeVisual:true});
 t.after(()=>dom.window.close());
 const w=dom.window,requests=[];
 w.document.body.dataset.studioView=view;
 w.genericView=w.document.getElementById('genericView');
 w.conversationView=w.document.getElementById('conversationView');
 const at=(day,hour)=>{const d=new Date();d.setDate(d.getDate()+day);d.setHours(hour,0,0,0);return d.toISOString();};
 const session={id:'session',jobId:'job',title:'School portraits',startsAtUtc:at(0,10),endsAtUtc:at(0,11),location:'Studio A',status:'Scheduled',resourceIds:['resource']};
 const sessions=[session,{...session,id:'cancelled',title:'Cancelled session',status:'Cancelled'},{...session,id:'tomorrow',title:'Tomorrow session',startsAtUtc:at(1,10),endsAtUtc:at(1,11)}];
 w.json=async url=>{
  requests.push(url);
  if(url.startsWith('/api/schedule/photographer-data'))return {photographers:[{userName:'alice',displayName:'Alice',scheduleResourceId:'resource',isActive:true}],entries:[session],jobs:[{id:'job',title:'School portraits',workspaceId:'workspace'}]};
  if(url==='/api/auth/me')return {user:{userName:'alice'}};
  if(url==='/api/today/jobs')return [{id:'job',title:'School portraits'}];
  if(url.startsWith('/api/operations/activities'))return [{id:'task',jobId:'job',title:'Prepare portraits',assignedTo:'alice',status:'NotStarted'}];
  return [];
 };
 w.fetch=async url=>{requests.push(url);return {ok:true,json:async()=>sessions};};
 w.loadWorkspaces=async()=>[];
 for(const file of ['studio-message-card.js','studio-page.js','scheduling.js','tasks-page.js','photographer-schedule.js','studio-shell-nav.js'])w.eval(read(file));
 w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
 await settle();
 return {w,requests};
}

for(const view of ['tasks','schedule'])test(`${view} keeps sessions in the shared slide-down without rendering a card`,async t=>{
 const {w,requests}=await setup(t,view);
 if(view==='tasks')await w.StudioTasks.render();
 else await w.StudioPhotographerSchedule.render({view:'day'});
 await settle();
 assert.equal(w.document.querySelector('.ps-todays-sessions,[data-mw-region="schedule"]'),null);
 assert.doesNotMatch(w.genericView.textContent,/Today[’']s Sessions/);
 assert.ok(w.document.querySelector(view==='tasks'?'[data-task-rows]':'.ps-calendar'),w.genericView.textContent);
 const drawer=w.document.getElementById('studioSessionsDrawer');
 assert.equal(drawer.hidden,true);
 w.document.getElementById('studioSessionsTrigger').click();
 assert.equal(drawer.hidden,false);
 assert.equal(w.document.getElementById('studioSessionsCount').textContent,'1');
 assert.match(drawer.textContent,/School portraits/);
 assert.match(drawer.textContent,/Studio A/);
 assert.doesNotMatch(drawer.textContent,/Cancelled session|Tomorrow session/);
 assert.equal(requests.filter(url=>url.startsWith('/api/today/schedule')).length,1);
});
