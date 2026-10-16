const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
async function setup(t){
 const dom=new JSDOM('<body data-studio-view="today"><div id="conversationView"></div><div id="genericView"></div><div id="studioBreadcrumb"></div><div id="studioPageTools"></div><div id="studioFooterRate"></div></body>',{url:'https://studio.test/',runScripts:'outside-only',pretendToBeVisual:true});
 t.after(()=>dom.window.close());const w=dom.window,errors=[],requests=[];
 const NativeDate=w.Date;
 w.Date=class extends NativeDate{constructor(...args){super(...(args.length?args:[new NativeDate(2026,9,16,12).getTime()]));}static now(){return new NativeDate(2026,9,16,12).getTime();}};
 const jobs=[['later','2026-10-17','Awaiting Scheduling'],['today-b','2026-10-16','Needs Assignment'],['today-a','2026-10-16','Needs Scheduling'],['monday','2026-10-12','Needs Photographer Assignment'],['sunday','2026-10-18','Scheduled'],['next-week','2026-10-19','Awaiting Upload'],['month-end','2026-10-31','Scheduled'],['next-month','2026-11-01','Scheduled'],['year-end','2026-12-31','Scheduled'],['new-year','2027-01-01','Scheduled'],['undated',null,'Scheduled']].map(([id,eventDate,status])=>({id,title:id,eventDate,status,jobLead:'staff',createdAtUtc:'2026-10-01T12:00:00Z'}));
 Object.assign(w,{esc:s=>String(s??''),workspaceCache:[],StudioStatusOrder:{sort:v=>[...new Set(v)]},StudioSubjectPicker:{isClosed:j=>['Completed','Cancelled'].includes(j.status)},console:{debug(){},info(){},warn(){},error(...args){errors.push(args);}},json:async url=>{
 requests.push(url);if(url==='/api/today/jobs')return jobs;if(url==='/api/auth/me')return {user:{userName:'staff'}};
 if(url.startsWith('/api/today/schedule'))return [{id:'session',jobId:'today-a',title:'Today shoot',startsAtUtc:new w.Date(2026,9,16,10).toISOString(),endsAtUtc:new w.Date(2026,9,16,11).toISOString(),status:'Scheduled'}];return [];
 }});
 w.eval(fs.readFileSync(path.join(root,'studio-message-card.js'),'utf8'));
 w.eval(fs.readFileSync(path.join(root,'studio-navigation.js'),'utf8'));
 await new Promise(r=>setTimeout(r,0));await w.studioTodayReady;assert.deepEqual(errors,[]);
 return {w,requests,errors,rows:()=>[...w.document.querySelectorAll('.mw-today-job-row')].map(el=>el.dataset.openJob)};
}
test('date navigation filters and sorts jobs while sessions and attention stay independent',async t=>{
 const {w,requests,rows,errors}=await setup(t),d=w.document;
 assert.deepEqual(rows(),['today-a','today-b']);assert.equal(d.getElementById('studioFooterRate').textContent,'2 Jobs');
 const session=d.querySelector('[data-mw-region="schedule"]').textContent;
 assert.match(session,/Today's Sessions/);assert.match(session,/today-a/);
 assert.match(d.querySelector('[data-mw-region="attention"]').textContent,/Needs scheduling \(led by me\)4/);
 assert.equal(d.querySelector('#todayMessageCard .smc-title strong').textContent,'Messages');
 const scheduleRequests=requests.filter(u=>u.startsWith('/api/today/schedule'));
 assert.equal(scheduleRequests.length,1);
 const initial=new URL(scheduleRequests[0],'https://studio.test');
 assert.equal(new w.Date(initial.searchParams.get('from')).getDate(),16);
 assert.equal(new w.Date(initial.searchParams.get('to')).getDate(),17);
 d.querySelector('[data-mw-day="1"]').click();assert.deepEqual(rows(),['later']);
 d.querySelector('[data-mw-calendar-view="week"]').click();assert.deepEqual(rows(),['monday','today-a','today-b','later','sunday']);
 d.querySelector('[data-mw-day="1"]').click();assert.deepEqual(rows(),['next-week']);
 d.querySelector('[data-mw-calendar-view="month"]').click();assert.deepEqual(rows(),['monday','today-a','today-b','later','sunday','next-week','month-end']);
 assert.equal(d.getElementById('studioFooterRate').textContent,'7 Jobs');
 d.querySelector('[data-mw-day="1"]').click();assert.deepEqual(rows(),['next-month']);
 d.querySelector('[data-mw-day="today"]').click();d.querySelector('[data-mw-calendar-view="day"]').click();assert.deepEqual(rows(),['today-a','today-b']);
 assert.equal(d.querySelector('[data-mw-region="schedule"]').textContent,session);
 assert.equal(requests.filter(u=>u.startsWith('/api/today/schedule')).length,1);
 assert.deepEqual(errors,[]);
});
test('search and status compose with dates and the footer counts only matching rows',async t=>{
 const {w,rows}=await setup(t),d=w.document;
 let input=d.querySelector('[data-mw-job-search]');input.value='today-b';input.dispatchEvent(new w.Event('input'));assert.deepEqual(rows(),['today-b']);
 assert.equal(d.getElementById('studioFooterRate').textContent,'1 Jobs');
 d.querySelector('[data-mw-day="1"]').click();assert.deepEqual(rows(),[]);assert.match(d.querySelector('.mw-today-jobs-empty').textContent,/selected date range/);
 input=d.querySelector('[data-mw-job-search]');input.value='';input.dispatchEvent(new w.Event('input'));assert.deepEqual(rows(),['later']);
 const status=d.querySelector('[data-mw-job-status]');status.value='Needs Assignment';status.dispatchEvent(new w.Event('change'));assert.deepEqual(rows(),[]);
 d.querySelector('[data-mw-day="today"]').click();assert.deepEqual(rows(),['today-b']);
});

test('calendar picker crosses year boundaries without losing date-only jobs',async t=>{
 const {w,rows}=await setup(t),d=w.document;
 d.querySelector('[data-mw-calendar-toggle]').click();
 d.querySelector('[data-calendar-month="1"]').click();
 d.querySelector('[data-calendar-month="1"]').click();
 d.querySelector('[data-calendar-day="31"]').click();assert.deepEqual(rows(),['year-end']);
 d.querySelector('[data-mw-day="1"]').click();assert.deepEqual(rows(),['new-year']);
 d.querySelector('[data-mw-calendar-view="week"]').click();assert.deepEqual(rows(),['year-end','new-year']);
 d.querySelector('[data-mw-calendar-view="month"]').click();assert.deepEqual(rows(),['new-year']);
});
