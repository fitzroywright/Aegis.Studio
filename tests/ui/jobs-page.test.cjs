const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('fs'),path=require('path');
const {JSDOM}=require('jsdom');const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const {setup}=require('./jobs-page-fixture.cjs');
function environment(t){const dom=new JSDOM('<body><div id="genericView"></div><span id="studioFooterRate"></span></body>',{runScripts:'outside-only',url:'https://studio.test'});t.after(()=>dom.window.close());const w=dom.window;const fixture=setup(w);w.sessionStorage.setItem('studio-jobs-view',JSON.stringify({view:'all',calendarView:'month',calendarDate:'2026-10-16'}));w.ResizeObserver=class {observe(){} disconnect(){}};w.requestAnimationFrame=fn=>w.setTimeout(fn,0);w.eval(fs.readFileSync(path.join(root,'studio-status-order.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'subject-picker.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'studio-message-card.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'jobs-page.js'),'utf8'));return {w,fixture,host:w.document.getElementById('genericView')};}
const change=(w,el,value,type='change')=>{el.value=value;el.dispatchEvent(new w.Event(type,{bubbles:true}));};
test('Jobs loads five columns, paginates the selected month and reports the filtered total',async t=>{
 const {w,fixture,host}=environment(t);await w.StudioJobsPage.render(host,fixture.options);
 assert.equal(host.querySelectorAll('th').length,5);assert.equal(host.querySelectorAll('tr[data-sj-job]').length,13);
 assert.equal(w.document.getElementById('studioFooterRate').textContent,'27 jobs');assert.match(host.textContent,/Alice Person/);
 assert.equal(host.querySelector('.sj-right'),null);assert.equal(host.querySelector('#jobsMessageCard'),null);
});
test('Search retains focus and workspace scope and pagination retain the full filtered count',async t=>{
 const {w,fixture,host}=environment(t);await w.StudioJobsPage.render(host,fixture.options);
 const input=host.querySelector('[data-sj-search]');input.focus();change(w,input,'Acme','input');
 assert.equal(w.document.activeElement,input);assert.equal(host.querySelectorAll('tr[data-sj-job]').length,1);
 change(w,input,'','input');host.querySelector('[data-sj-next]').click();
 assert.match(host.querySelector('[data-sj-range]').textContent,/14–26 of 27/);assert.equal(w.document.getElementById('studioFooterRate').textContent,'27 jobs');
 fixture.options.workspaceFilter='w2';await w.StudioJobsPage.render(host,fixture.options);
 assert.equal(w.document.getElementById('studioFooterRate').textContent,'7 jobs');
 assert.ok([...host.querySelectorAll('tr[data-sj-job]')].every(row=>Number(row.dataset.sjJob.slice(4))>=20));
});
test('Status filtering and row click and keyboard navigation use the exact job',async t=>{
 const {w,fixture,host}=environment(t);await w.StudioJobsPage.render(host,fixture.options);
 change(w,host.querySelector('[data-sj-status]'),'Scheduled');assert.equal(w.document.getElementById('studioFooterRate').textContent,'13 jobs');
 const row=host.querySelector('tr[data-sj-job]'),id=row.dataset.sjJob;
 row.querySelector('strong').click();row.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Enter',bubbles:true}));assert.deepEqual(fixture.opened,[id,id]);
 change(w,host.querySelector('[data-sj-status]'),'');
 const attention=host.querySelector('[data-attention-item="job-0"]');assert.match(attention.textContent,/Job owner needed/);attention.click();assert.equal(fixture.opened.at(-1),'job-0');
});
test('Sort uses local dates and excludes undated jobs from the selected calendar period',async t=>{
 const {w,fixture,host}=environment(t);await w.StudioJobsPage.render(host,fixture.options);change(w,host.querySelector('[data-sj-sort]'),'date-asc');
 assert.equal(host.querySelector('tr[data-sj-job]').dataset.sjJob,'job-0');assert.match(host.querySelector('tr[data-sj-job]').textContent,/Thu Oct 01 2026/);
 host.querySelector('[data-sj-next]').click();host.querySelector('[data-sj-next]').click();assert.equal([...host.querySelectorAll('tr[data-sj-job]')].at(-1).dataset.sjJob,'job-26');
 assert.equal(host.querySelector('[data-sj-job="job-27"]'),null);
});
test('Errors offer Retry and unavailable workflow never invents a next action',async t=>{
 const {w,fixture,host}=environment(t);fixture.failJobs=true;await w.StudioJobsPage.render(host,fixture.options);assert.match(host.textContent,/Unable to load jobs: Offline/);
 fixture.failJobs=false;fixture.failWorkflow=true;await fixture.options.retry();assert.match(host.textContent,/Workflow unavailable/);assert.equal(host.querySelectorAll('tr[data-sj-job]').length,13);
});
test('Leaving Jobs during fetch prevents stale render',async t=>{const {w,fixture,host}=environment(t);let release;fixture.delay=new Promise(r=>release=r);const pending=w.StudioJobsPage.render(host,fixture.options);w.StudioJobsPage.dispose();fixture.current=false;host.innerHTML='<p>Another page</p>';release();await pending;assert.equal(host.textContent,'Another page');});
test('Jobs styling has one owner and old renderer is removed',()=>{const script=fs.readFileSync(path.join(root,'studio-navigation.js'),'utf8');const css=fs.readFileSync(path.join(root,'jobs-page.css'),'utf8');const oldCss=fs.readFileSync(path.join(root,'studio.css'),'utf8');assert.match(script,/StudioJobsPage.render/);assert.doesNotMatch(script,/jobs-row|jobs-page-heading|Search is intentionally absent/);assert.doesNotMatch(css,/!important/);assert.doesNotMatch(oldCss,/\.jobs-row|\.jobs-page-heading/);});
module.exports={setup,root};
test('Jobs route uses the new component and workspace links without an intermediate page',async t=>{
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{runScripts:'outside-only',url:'https://studio.test/#studio=jobs'});t.after(()=>dom.window.close());const w=dom.window,fixture=setup(w);w.sessionStorage.setItem('studio-jobs-view',JSON.stringify({view:'all',calendarView:'month',calendarDate:'2026-10-16'}));
 Object.assign(w,{workspaceCache:fixture.options.workspaces,selectedWorkspace:'',json:fixture.options.json,loadWorkspaces:async()=>{},openWorkspaceJob:fixture.options.openJob,selectWorkspace:async()=>{}});
 w.ResizeObserver=class {observe(){} disconnect(){}};w.requestAnimationFrame=fn=>w.setTimeout(fn,0);
 for(const script of ['studio-status-order.js','subject-picker.js','studio-message-card.js','jobs-page.js','studio-navigation.js','studio-shell-nav.js'])w.eval(fs.readFileSync(path.join(root,script),'utf8'));
 await new Promise(r=>setTimeout(r,20));await w.studioOpenJobs('w2');
 assert.equal(w.document.body.dataset.studioView,'jobs');assert.equal(w.document.getElementById('studioFooterRate').textContent,'7 jobs');assert.equal(w.document.querySelector('.sj-right'),null);
 w.document.querySelector('tr[data-sj-job] strong').click();assert.equal(fixture.opened.length,1);
 assert.match(w.document.getElementById('studioTodayBrandTitle').textContent,/Jobs/);
});
test('An older Jobs request cannot replace a newer render and opening errors stay inline',async t=>{
 const {w,fixture,host}=environment(t);let release;fixture.delay=new Promise(r=>release=r);const first=w.StudioJobsPage.render(host,fixture.options);fixture.delay=null;fixture.options.workspaceFilter='w2';fixture.options.openJob=async()=>{throw new Error('Access denied');};await w.StudioJobsPage.render(host,fixture.options);release();await first;
 assert.equal(w.document.getElementById('studioFooterRate').textContent,'7 jobs');host.querySelector('tr[data-sj-job]').click();await new Promise(r=>setTimeout(r,0));assert.match(host.querySelector('[data-sj-error]').textContent,/Access denied/);assert.equal(host.querySelector('[data-sj-error]').hidden,false);
});


test('workspace settings follows the active filter and keeps errors inline',async t=>{
 const {w,fixture,host}=environment(t),opened=[];
 fixture.options.openWorkspaceSettings=async id=>{opened.push(id);};
 await w.StudioJobsPage.render(host,fixture.options);
 const settings=host.querySelector('[data-sj-settings]');assert.equal(settings.hidden,true);
 fixture.options.workspaceFilter='w2';await w.StudioJobsPage.render(host,fixture.options);const scopedSettings=host.querySelector('[data-sj-settings]');assert.equal(scopedSettings.hidden,false);
 host.querySelector('[data-sj-settings]').click();await new Promise(r=>setTimeout(r,0));assert.deepEqual(opened,['w2']);
 fixture.options.openWorkspaceSettings=async()=>{throw Error('Settings unavailable');};
 host.querySelector('[data-sj-settings]').click();await new Promise(r=>setTimeout(r,0));assert.match(host.querySelector('[data-sj-error]').textContent,/Settings unavailable/);
 fixture.options.workspaceFilter='';await w.StudioJobsPage.render(host,fixture.options);assert.equal(host.querySelector('[data-sj-settings]').hidden,true);
});

test('Closed and archived jobs are excluded by default and Closed can be explicitly selected',async t=>{
 const {w,fixture,host}=environment(t);fixture.jobs[0].status='Closed';fixture.jobs[1].isArchived=true;
 await w.StudioJobsPage.render(host,fixture.options);assert.equal(w.document.getElementById('studioFooterRate').textContent,'25 jobs');
 change(w,host.querySelector('[data-sj-status]'),'Closed');assert.equal(host.querySelectorAll('tr[data-sj-job]').length,1);assert.equal(host.querySelector('tr[data-sj-job]').dataset.sjJob,'job-0');
 change(w,host.querySelector('[data-sj-status]'),'');assert.equal(w.document.getElementById('studioFooterRate').textContent,'25 jobs');
});
