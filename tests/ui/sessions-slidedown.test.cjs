const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const read=name=>fs.readFileSync(path.join(root,name),'utf8');
const settle=()=>new Promise(resolve=>setImmediate(resolve));

async function setup(t,view,options={}){
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
 w.fetch=async url=>{requests.push(url);return options.fetch ? options.fetch(url,sessions) : {ok:true,json:async()=>sessions};};
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
 const status=w.document.querySelector('#studioNavigationToolstrip .studio-navigation-status');
 assert.deepEqual([...status.children].map(el=>el.id),['studioSessionsTrigger','studioAttentionTrigger']);
 assert.equal(w.document.querySelectorAll('#studioSessionsTrigger').length,1);
 assert.equal(w.document.querySelectorAll('#studioAttentionTrigger').length,1);
 const attention=w.document.getElementById('studioAttentionDrawer');
 w.document.getElementById('studioAttentionTrigger').click();
 assert.equal(attention.hidden,false);
 w.document.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));
 assert.equal(attention.hidden,true);
 w.StudioShell.sync(view);
 assert.equal(w.document.querySelector('.studio-navigation-status'),status);
 const drawer=w.document.getElementById('studioSessionsDrawer');
 assert.equal(drawer.hidden,true);
 w.document.getElementById('studioSessionsTrigger').click();
 await settle();
 assert.equal(drawer.hidden,false);
 assert.equal(attention.hidden,true);
 assert.equal(w.document.getElementById('studioSessionsTrigger').getAttribute('aria-expanded'),'true');
 assert.equal(w.document.getElementById('studioSessionsCount').textContent,'1');
 assert.match(drawer.textContent,/School portraits/);
 assert.match(drawer.textContent,/Studio A/);
 assert.doesNotMatch(drawer.textContent,/Cancelled session|Tomorrow session/);
 assert.equal(requests.filter(url=>url.startsWith('/api/today/schedule')).length,2);
});

test('sessions span midnight, remain global in Job Details, and refresh when opened',async t=>{
 let calls=0;
 const {w,requests}=await setup(t,'workspaces',{fetch:async(url,sessions)=>{
  calls++;
  const midnight=new Date();midnight.setHours(0,0,0,0);
  const at=hours=>new Date(+midnight+hours*3600000).toISOString();
  return {ok:true,json:async()=>[
   {...sessions[0],id:'overnight',jobId:'other-job',title:'Overnight shoot',startsAtUtc:at(-1),endsAtUtc:at(1)},
   {...sessions[0],id:'yesterday',title:'Ended at midnight',startsAtUtc:at(-2),endsAtUtc:at(0)},
   {...sessions[0],id:'tomorrow',title:'Starts tomorrow',startsAtUtc:at(24),endsAtUtc:at(25)},
   ...(calls>1?[{...sessions[0],id:'new',jobId:'other-job',title:'Newly scheduled shoot'}]:[])
  ]};
 }});
 const conversation=w.document.getElementById('conversationView');
 conversation.classList.add('has-open-job');
 conversation.innerHTML='<section class="job-detail-page" data-job-id="current-job"></section>';
 await settle();
 assert.equal(w.document.getElementById('studioSessionsCount').textContent,'1');
 w.document.getElementById('studioSessionsTrigger').click();await settle();
 const drawer=w.document.getElementById('studioSessionsDrawer');
 assert.match(drawer.textContent,/Overnight shoot/);
 assert.match(drawer.textContent,/Newly scheduled shoot/);
 assert.doesNotMatch(drawer.textContent,/Ended at midnight|Starts tomorrow/);
 assert.equal(w.document.getElementById('studioSessionsCount').textContent,'2');
 const range=new URL(requests.find(u=>u.startsWith('/api/today/schedule')),'https://studio.test');
 assert.equal(new Date(range.searchParams.get('from')).getHours(),0);
 assert.equal(new Date(range.searchParams.get('to')).getDate(),new Date(Date.now()+86400000).getDate());
});

test('failed or malformed session responses show an error and reopening retries',async t=>{
 let calls=0;
 const {w}=await setup(t,'today',{fetch:async(url,sessions)=>{
  calls++;return {ok:calls!==1,json:async()=>calls===2?{unexpected:[]}:sessions};
 }});
 const trigger=w.document.getElementById('studioSessionsTrigger'),drawer=w.document.getElementById('studioSessionsDrawer');
 assert.match(drawer.textContent,/Unable to load sessions/);
 trigger.click();await settle();assert.match(drawer.textContent,/Unable to load sessions/);
 trigger.click();trigger.click();await settle();
 assert.match(drawer.textContent,/School portraits/);
 assert.equal(w.document.getElementById('studioSessionsCount').textContent,'1');
});

test('Today schedule uses the calendar store and matching access rules without requiring a shoot record',()=>{
 const source=fs.readFileSync(path.join(root,'../Operations/TodayEndpoints.cs'),'utf8');
 const endpoint=source.slice(source.indexOf('app.MapGet("/api/today/schedule"'),source.indexOf('app.MapGet("/api/today/media-summary"'));
 assert.match(endpoint,/store\.GetEntries\(start,end\)/);
 assert.match(endpoint,/IsInRole\("Administrator"\)/);
 assert.match(endpoint,/Allowed\(context, access, logger\)\.ToHashSet\(\)/);
 assert.match(endpoint,/allowed\.Contains\(e\.JobId\.Value\)/);
 assert.match(endpoint,/e\.Status!="Cancelled"/);
 assert.doesNotMatch(endpoint,/join studio_shoot_sessions|s\.status|resourceIds\.Length/);
});
