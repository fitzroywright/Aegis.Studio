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
 const session={id:'session',jobId:'job',title:'School portraits',startsAtUtc:new Date(Date.now()+3600000).toISOString(),endsAtUtc:new Date(Date.now()+7200000).toISOString(),location:'Studio A',status:'Scheduled',resourceIds:['resource']};
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
 assert.equal(w.document.getElementById('studioSessionsCount').textContent,'2');
 assert.match(drawer.textContent,/School portraits/);
 assert.match(drawer.textContent,/Studio A/);
 assert.match(drawer.textContent,/Tomorrow session/);
 assert.match(drawer.textContent,/Upcoming Sessions/);
 assert.match(drawer.textContent,/Next 30 days/);
 assert.equal(drawer.getAttribute('aria-label'),'Upcoming Sessions');
 assert.match(w.document.getElementById('studioSessionsTrigger').textContent,/Upcoming Sessions/);
 assert.doesNotMatch(drawer.textContent,/Cancelled session/);
 assert.equal(requests.filter(url=>url.startsWith('/api/today/schedule')).length,2);
});

test('upcoming sessions use the next 30 days, exclude past/cancelled/completed, stay global and refresh',async t=>{
 let calls=0;
 const {w,requests}=await setup(t,'workspaces',{fetch:async(url,sessions)=>{
  calls++;
  const range=new URL(url,'https://studio.test'),from=+new Date(range.searchParams.get('from')),to=+new Date(range.searchParams.get('to'));
  const entry=(id,start,status='Scheduled')=>({...sessions[0],id,title:id,jobId:'other-job',status,startsAtUtc:new Date(start).toISOString(),endsAtUtc:new Date(start+3600000).toISOString()});
  return {ok:true,json:async()=>[
   entry('Next week',from+7*86400000),entry('At start',from),entry('Overnight already started',from-3600000),
   entry('At horizon',to),entry('Cancelled shoot',from+86400000,'Cancelled'),entry('Completed shoot',from+86400000,'Completed'),
   ...(calls>1?[entry('Newly scheduled shoot',from+86400000)]:[])
  ]};
 }});
 const conversation=w.document.getElementById('conversationView');
 conversation.classList.add('has-open-job');conversation.innerHTML='<section class="job-detail-page" data-job-id="current-job"></section>';
 await settle();assert.equal(w.document.getElementById('studioSessionsCount').textContent,'2');
 w.document.getElementById('studioSessionsTrigger').click();await settle();
 const drawer=w.document.getElementById('studioSessionsDrawer');
 assert.deepEqual([...drawer.querySelectorAll('.studio-shared-session-row strong')].map(el=>el.textContent),['At start','Newly scheduled shoot','Next week']);
 assert.doesNotMatch(drawer.textContent,/Overnight already started|At horizon|Cancelled shoot|Completed shoot|Finished/);
 assert.equal(w.document.getElementById('studioSessionsCount').textContent,'3');
 const range=new URL(requests.find(u=>u.startsWith('/api/today/schedule')),'https://studio.test');
 const from=new Date(range.searchParams.get('from')),to=new Date(range.searchParams.get('to')),expected=new Date(from);expected.setDate(expected.getDate()+30);
 assert.equal(+to,+expected);assert.ok(Math.abs(Date.now()-from)<10000);
 const meta=drawer.querySelector('.studio-shared-session-row span').textContent;
 assert.ok(meta.startsWith(from.toLocaleString([],{weekday:'short',month:'short',day:'numeric',hour:'numeric',minute:'2-digit'})));
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
 assert.equal(w.document.getElementById('studioSessionsCount').textContent,'2');
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

test('session and attention drawers respect shared banner/footer bounds without dimming the page',()=>{
 const css=read('studio.css');
 const csstree=require('css-tree'),ast=csstree.parse(css);
 const rules=selector=>{const matches=[];csstree.walk(ast,{visit:'Rule',enter(node){if(csstree.generate(node.prelude)===selector)matches.push(node);}});assert.equal(matches.length,1,'one styling owner for '+selector);return Object.fromEntries(matches[0].block.children.toArray().filter(n=>n.type==='Declaration').map(n=>[n.property,csstree.generate(n.value)]));};
 const drawer=rules('.studio-attention-drawer:not([hidden])');
 assert.equal(drawer.top,'var(--studio-banner-height)');assert.equal(drawer.bottom,'var(--studio-footer-height)');
 assert.equal(drawer.overflow,'hidden');assert.equal(drawer['min-height'],'0');
 const backdrop=rules('.studio-attention-backdrop:not([hidden])');
 assert.equal(backdrop.inset,csstree.generate(csstree.parse('var(--studio-banner-height) 0 var(--studio-footer-height)',{context:'value'})));assert.equal(backdrop.background,undefined);
 const list=rules('.studio-attention-details');assert.equal(list.flex,'1 1 auto');assert.equal(list['min-height'],'0');assert.equal(list['overflow-y'],'auto');
 assert.equal(rules('.studio-attention-drawer header').flex,'0 0 auto');
 assert.equal(rules('.studio-attention-drawer>[data-sessions-calendar]').flex,'0 0 auto');
 assert.equal(rules('.studio-sessions-range').flex,'0 0 auto');
});
