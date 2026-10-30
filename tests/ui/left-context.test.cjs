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
test('shell refresh preserves live page controls without restoring retired rails',t=>{
 const w=shell(t);w.document.body.dataset.studioView='jobs';const card=w.document.createElement('section');card.innerHTML='<input value="initial"><button>Approve</button>';w.document.getElementById('genericView').append(card);
 const button=card.querySelector('button'),input=card.querySelector('input');let clicks=0;button.onclick=()=>clicks++;input.value='kept';
 w.StudioShell.sync('jobs');w.StudioShell.sync('jobs');button.click();assert.equal(clicks,1);assert.equal(input.value,'kept');assert.equal(card.parentElement.id,'genericView');assert.equal(w.document.querySelector('#studioContextRail,[data-rail-toggle]'),null);
});

test('Jobs uses its inline status filter and the shared attention drawer without an Overview rail',async t=>{
 const w=shell(t),fixture=require('./jobs-page-fixture.cjs').setup(w);w.sessionStorage.setItem('studio-jobs-view',JSON.stringify({view:'all',calendarView:'month',calendarDate:'2026-10-16'}));for(const file of ['studio-status-order.js','subject-picker.js','jobs-page.js'])w.eval(read(file));
 const host=w.document.getElementById('genericView');await w.StudioJobsPage.render(host,fixture.options);const filter=host.querySelector('[data-sj-status]');filter.value='Scheduled';filter.dispatchEvent(new w.Event('change'));
 assert.equal(host.querySelectorAll('tr[data-sj-job]').length,13);w.StudioShell.sync('jobs');assert.equal(host.querySelector('[data-sj-status]'),filter);assert.equal(filter.value,'Scheduled');
 filter.value='';filter.dispatchEvent(new w.Event('change'));assert.equal(w.document.querySelector('#studioContextRail,.sj-right,#jobsMessageCard'),null);
 const need=w.document.querySelector('#studioAttentionDrawer [data-sj-job="job-0"]');assert.match(need.textContent,/Portrait 0.*Job owner needed/s);need.click();await settle();assert.equal(fixture.opened.at(-1),'job-0');
});

test('Job Details shows current status and stage age in the identity overlay without a rail',async t=>{
 const {w}=require('./job-detail-fixture.cjs').environment(t);await w.openWorkspaceJob('job');await settle();
 const menu=w.document.querySelector('#jobIdentityMenu');assert.equal(menu.hidden,true);w.document.querySelector('.jd-job-name-toggle').click();assert.equal(menu.hidden,false);assert.match(menu.textContent,/Current StatusProduction.*Time in Stage.*Williams & Clarke/s);assert.equal(w.document.querySelector('#studioContextRail,.job-detail-right'),null);
});

test('retired Activity actions stay out of the avatar, footer and page',async t=>{
 const {w}=require('./job-detail-fixture.cjs').environment(t);await w.openWorkspaceJob('job');await settle();
 assert.equal(w.document.querySelector('[data-studio-avatar-activity],[data-job-action="activity"],#studioFooterJobTools'),null);assert.doesNotMatch(w.document.getElementById('studioShellFooter').textContent,/Activity/);
 await w.document.querySelector('[data-job-tasks]').onclick();assert.ok(w.document.querySelector('.jd-tasks-dialog'));await w.studioNavigate('today');await settle();assert.equal(w.document.querySelector('.jd-tasks-dialog,.jd-footer-drawer'),null);
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
 assert.equal(rail,null);assert.equal(host.querySelector('[data-studio-attention]'),null);
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

