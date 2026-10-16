const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const {setup}=require('./jobs-page-fixture.cjs');
async function environment(t,saved={view:'all'}){
 const dom=new JSDOM('<body><div id="genericView"></div><span id="studioFooterRate"></span></body>',{runScripts:'outside-only',url:'https://studio.test',pretendToBeVisual:true});t.after(()=>dom.window.close());const w=dom.window;
 const D=w.Date;w.Date=class extends D{constructor(...args){super(...(args.length?args:[new D(2026,9,16,12).getTime()]));}};
 w.sessionStorage.setItem('studio-jobs-view',JSON.stringify(saved));
 const fixture=setup(w);
 for(const script of ['studio-status-order.js','subject-picker.js','studio-message-card.js','jobs-page.js'])w.eval(fs.readFileSync(path.join(root,script),'utf8'));
 const host=w.document.getElementById('genericView');await w.StudioJobsPage.render(host,fixture.options);
 return {w,host,fixture,rows:()=>[...host.querySelectorAll('tr[data-sj-job]')].map(r=>r.dataset.sjJob)};
}
test('Jobs starts with an active day filter and All dates explicitly clears its selection',async t=>{
 const {w,host,rows,fixture}=await environment(t);
 assert.deepEqual(rows(),['job-15']);assert.equal(w.document.getElementById('studioFooterRate').textContent,'1 jobs');
 assert.equal(host.querySelector('[data-sj-calendar-view="day"]').getAttribute('aria-pressed'),'true');
 assert.equal(host.querySelector('#jobsMessageCard .smc-title strong').textContent,'Messages');
 host.querySelector('[data-sj-calendar-clear]').click();
 assert.equal(w.document.getElementById('studioFooterRate').textContent,'28 jobs');
 assert.equal(host.querySelectorAll('[data-sj-calendar-view][aria-pressed="true"]').length,0);
 assert.equal(host.querySelector('[data-sj-calendar-clear]').getAttribute('aria-pressed'),'true');
 await w.StudioJobsPage.render(host,fixture.options);
 assert.equal(host.querySelector('[data-sj-calendar-clear]').getAttribute('aria-pressed'),'true');
 host.querySelector('[data-sj-calendar-today]').click();assert.deepEqual(rows(),['job-15']);
});
test('Jobs week and month ranges compose with status and search and reset pagination',async t=>{
 const {w,host,rows}=await environment(t);
 host.querySelector('[data-sj-calendar-view="week"]').click();assert.equal(rows().length,7);assert.equal(w.document.getElementById('studioFooterRate').textContent,'7 jobs');
 const status=host.querySelector('[data-sj-status]');status.value='Scheduled';status.dispatchEvent(new w.Event('change'));assert.equal(rows().length,4);
 const search=host.querySelector('[data-sj-search]');search.value='Portrait 15';search.dispatchEvent(new w.Event('input'));assert.deepEqual(rows(),['job-15']);
 search.value='';search.dispatchEvent(new w.Event('input'));status.value='';status.dispatchEvent(new w.Event('change'));
 host.querySelector('[data-sj-calendar-view="month"]').click();assert.equal(w.document.getElementById('studioFooterRate').textContent,'27 jobs');
 host.querySelector('[data-sj-next]').click();assert.match(host.querySelector('[data-sj-range]').textContent,/14–26/);
 host.querySelector('[data-sj-calendar-view="day"]').click();assert.deepEqual(rows(),['job-15']);assert.match(host.querySelector('[data-sj-range]').textContent,/1–1/);
});
test('Jobs month navigation clamps month-end dates and preserves leap day',async t=>{
 const {w,host}=await environment(t,{view:'all',calendarDate:'2028-01-31',calendarEnabled:true,calendarView:'month'});
 host.querySelector('[data-sj-calendar-step="1"]').click();assert.equal(host.querySelector('[data-sj-calendar-date]').value,'2028-02-29');
 host.querySelector('[data-sj-calendar-step="1"]').click();assert.equal(host.querySelector('[data-sj-calendar-date]').value,'2028-03-29');
});
