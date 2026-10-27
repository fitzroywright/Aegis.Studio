const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const read=name=>fs.readFileSync(path.join(root,name),'utf8');
const settle=()=>new Promise(resolve=>setImmediate(resolve));
function shell(t){
 const dom=new JSDOM(read('index.html'),{url:'https://studio.test/',runScripts:'outside-only',pretendToBeVisual:true});
 t.after(()=>dom.window.close());
 const w=dom.window;w.fetch=async()=>({ok:true,json:async()=>[]});
 w.json=async()=>[];w.workspaceCache=[];
 for(const file of ['studio-message-card.js','studio-page.js','studio-shell-nav.js'])w.eval(read(file));
 w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
 return w;
}
test('left context preserves live controls through shell refresh and rail collapse',t=>{
 const w=shell(t);w.document.body.dataset.studioView='jobs';
 const card=w.document.createElement('section');card.innerHTML='<header><h2>Work</h2></header><input value="initial"><button>Approve</button>';
 const button=card.querySelector('button'),input=card.querySelector('input');let clicks=0;
 button.onclick=()=>clicks++;input.value='kept';
 w.studioShellMountContext([card],'jobs');
 w.StudioShell.sync('jobs');w.document.querySelector('[data-rail-toggle="left"]').click();
 w.StudioShell.sync('jobs');button.click();
 assert.equal(clicks,1);assert.equal(input.value,'kept');
 assert.equal(card.parentElement.id,'studioContextRail');
 assert.equal(w.document.querySelector('[data-rail-toggle="right"]'),null);
});
test('Jobs Overview owns interactive status counts and recent changes without a Job Status card',async t=>{
 const w=shell(t);const fixture=require('./jobs-page-fixture.cjs').setup(w);
 w.sessionStorage.setItem('studio-jobs-view',JSON.stringify({view:'all',calendarView:'month',calendarDate:'2026-10-16'}));
 for(const file of ['studio-status-order.js','subject-picker.js','jobs-page.js'])w.eval(read(file));
 const host=w.document.getElementById('genericView');
 await w.StudioJobsPage.render(host,fixture.options);
 const rail=w.document.getElementById('studioContextRail'),overview=rail.querySelector('[data-rail-card="overview"]');
 assert.ok(overview.querySelector('[data-sj-statuses]'));
 assert.match(overview.textContent,/Recent Status Changes/);
 assert.equal([...w.document.querySelectorAll('h2')].some(n=>n.textContent==='Job Status'),false);
 overview.querySelector('[data-sj-stage="Scheduled"]').click();
 assert.equal(host.querySelector('[data-sj-status]').value,'Scheduled');
 assert.equal(host.querySelectorAll('tr[data-sj-job]').length,13,JSON.stringify({text:host.textContent.slice(-300),state:w.sessionStorage.getItem('studio-jobs-view'),footer:w.document.getElementById('studioFooterRate').textContent}));
 w.StudioShell.sync('jobs');
 assert.equal(rail.querySelector('[data-rail-card="overview"]'),overview);
 overview.querySelector('[data-sj-stage="Scheduled"]').click();
 assert.equal(host.querySelectorAll('tr[data-sj-job]').length,13);
 assert.equal(rail.querySelectorAll('[data-rail-card="context"]').length,0);
 assert.equal(w.document.querySelector('#jobsMessageCard,.smc-card'),null);
 assert.equal(rail.querySelector('[data-sj-attention]'),null);
 assert.ok(w.document.querySelector('#studioAttentionDrawer [data-sj-attention]'));
 const need=w.document.querySelector('#studioAttentionDrawer [data-sj-job="job-0"]');
 assert.match(need.textContent,/Portrait 0.*Job owner needed/s);need.click();await settle();assert.equal(fixture.opened.at(-1),'job-0');
 assert.equal(w.document.querySelector('.sj-right,.st-right,.job-detail-right'),null);
});
test('Job Details Overview shows status instead of repeating job and client identity',async t=>{
 const {w}=require('./job-detail-fixture.cjs').environment(t);
 for(const file of ['studio-page.js','notifications.js','job-workflow-display.js','job-workflow-graph.js'])w.eval(read(file));
 await w.openWorkspaceJob('job');await settle();await settle();
 const overview=w.document.querySelector('#studioContextRail [data-rail-card="overview"]');
 assert.ok(overview);assert.match(overview.textContent,/Awaiting Production Review/);
 assert.match(overview.textContent,/Days in Stage/);
 assert.doesNotMatch(overview.textContent,/Williams & Clarke|Wedding selects review/);
 assert.equal(w.document.querySelector('.job-detail-right,[data-rail-toggle="right"]'),null);
});
test('Activity stays out of every footer and job history opens from the avatar',async t=>{
 const {w}=require('./job-detail-fixture.cjs').environment(t);
 for(const file of ['studio-page.js','notifications.js','job-workflow-display.js','job-workflow-graph.js'])w.eval(read(file));
 await w.openWorkspaceJob('job');await settle();await settle();
 const footer=w.document.getElementById('studioShellFooter');
 const activity=w.document.querySelector('[data-studio-avatar-activity]');
 assert.equal(footer.querySelector('[data-job-activity],.jd-footer-link,#studioFooterJobTools'),null);
 assert.doesNotMatch(footer.textContent,/Activity/);
 activity.click();await settle();
 const drawer=w.document.getElementById('jobActivityDrawer');
 assert.equal(drawer.getAttribute('aria-hidden'),'false');
 drawer.querySelector('[data-close-drawer]').click();
 assert.equal(drawer.getAttribute('aria-hidden'),'true');
 assert.equal(w.document.activeElement,w.document.querySelector('.user-menu-trigger'));
 for(const view of ['today','frontdesk','jobs','media','schedule','production','clients','session-plan','delivery']){
  w.document.body.dataset.studioView=view;w.StudioShell.sync(view);await settle();
  assert.equal(w.document.querySelector('[data-studio-avatar-activity]'),activity);
  assert.equal(footer.querySelector('[data-job-activity],.jd-footer-link,#studioFooterJobTools'),null);
  assert.doesNotMatch(footer.textContent,/Activity/);
 }
});
test('page renderers and styles no longer define right rails',()=>{
 const retired=/\b(?:st-right|sj-right|job-detail-right|mw-today-right|mw-list-right|workspace-right-rail|studio-rail-collapsed-right)\b/;
 function files(dir){return fs.readdirSync(dir,{withFileTypes:true}).flatMap(d=>d.isDirectory()?files(path.join(dir,d.name)):[path.join(dir,d.name)]);}
 for(const file of files(root).filter(f=>/\.(?:js|css|html)$/.test(f)&&!f.endsWith('dagre.min.js')))
  assert.doesNotMatch(fs.readFileSync(file,'utf8'),retired,file);
});

test('attention entries live only in the shared drawer and retain actions, counts and route context',async t=>{
 const w=shell(t),host=w.document.getElementById('genericView');w.document.body.dataset.studioView='jobs';
 host.innerHTML='<section data-studio-context><div data-studio-attention><button data-attention-test>Review blocked job</button></div><section><header><h2>Other context</h2></header></section></section>';
 const button=host.querySelector('[data-attention-test]');let clicks=0;button.onclick=()=>clicks++;
 w.StudioPage.mountContext(host,'jobs');await settle();
 const drawer=w.document.getElementById('studioAttentionDrawer'),rail=w.document.getElementById('studioContextRail');
 assert.equal(drawer.querySelector('[data-attention-test]'),button);
 assert.equal(rail.querySelector('[data-studio-attention]'),null);assert.equal(host.querySelector('[data-studio-attention]'),null);
 assert.equal(w.document.getElementById('studioAttentionCount').textContent,'1');
 assert.equal(w.document.getElementById('studioAttentionItems').hidden,false);
 w.document.getElementById('studioAttentionTrigger').click();button.click();assert.equal(clicks,1);
 button.after(button.cloneNode(true));await settle();assert.equal(w.document.getElementById('studioAttentionCount').textContent,'2');button.nextElementSibling.remove();await settle();
 w.document.body.dataset.studioView='clients';w.StudioShell.sync('clients');await settle();
 assert.equal(drawer.querySelector('[data-attention-test]'),null);assert.equal(w.document.getElementById('studioAttentionCount').textContent,'0');
 w.document.body.dataset.studioView='jobs';w.StudioShell.sync('jobs');await settle();
 assert.equal(drawer.querySelector('[data-attention-test]'),button);button.click();assert.equal(clicks,2);
 assert.equal(w.document.querySelectorAll('[data-attention-test]').length,1);
 assert.equal([...w.document.querySelectorAll('h2')].filter(h=>h.textContent==='Needs Attention').length,1);
});

test('page renderers no longer create Needs Attention cards',()=>{
 for(const name of ['studio-page.js','jobs-page.js','clients.js','photographer-schedule.js','production-planning.js','tasks-page.js','workspace-outlook.js']){
  assert.doesNotMatch(read(name),/Needs Attention|sj-attention-card|st-task-attention/,name);
 }
 assert.equal(fs.existsSync(path.join(root,'css/components/needs-attention.css')),false);
 assert.doesNotMatch(read('index.html'),/needs-attention\.css/);
});
