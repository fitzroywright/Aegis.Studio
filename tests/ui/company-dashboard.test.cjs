const {test}=require('node:test');
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const source=n=>fs.readFileSync(path.join(root,n),'utf8');
function setup(t,read){
 const dom=new JSDOM('<body data-studio-view="dashboard"><main><div id="genericView"></div></main><select id="studioFooterWorkspaces"><option value="">Workspaces: All</option></select></body>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;
 t.after(()=>w.close());w.json=read;w.navigation=[];w.studioNavigate=(view,options)=>w.navigation.push({view,options});
 for(const name of ['workload-overview.js','company-dashboard.js'])w.eval(source(name));return w;
}
const today=new Date().toLocaleDateString('en-CA');
test('Today charts use exact scoped counts and preserve operational links',async t=>{
 const data=fixture();data.jobs.push({id:'c',workspaceId:'one',title:'QC job',status:'Quality Control'});data.workload.jobs.push(data.jobs.at(-1));
 const w=setup(t,read(data));let crumbs;w.studioSetBreadcrumb=c=>crumbs=c;
 await w.StudioDashboard.render();await w.studioDashboardReady;
 assert.equal(crumbs[0].label,'Today');
 const segments=[...w.document.querySelectorAll('.cd-donut-segment')];assert.equal(segments.length,2);
 assert.equal(segments.reduce((sum,s)=>sum+Number(s.getAttribute('stroke-dasharray').split(' ')[0]),0),100);
 assert.match(w.document.querySelector('.cd-donut svg').getAttribute('aria-label'),/In Production 1, Quality Control 1/);
 assert.equal(w.document.querySelectorAll('.cd-bar-row').length,5);
 assert.equal(w.document.querySelectorAll('.cd-session-table tbody tr').length,1);assert.equal(w.document.querySelectorAll('table').length,2);
 w.document.querySelector('.cd-chart-legend button').click();assert.equal(w.navigation.at(-1).view,'production');
 w.sessionStorage.setItem('studio.footer.workspace.selection','two');await w.StudioDashboard.render();await w.studioDashboardReady;
 assert.equal(w.document.querySelectorAll('.cd-donut-segment').length,0);assert.match(w.document.querySelector('.cd-production-chart').parentNode.textContent,/No production jobs/);
 assert.match(w.document.querySelector('.cd-session-table').textContent,/No sessions today/);
});
test('zero or failed chart sources never display invalid or misleading proportions',async t=>{
 const w=setup(t,read({jobs:[],requests:[],inquiries:[],schedule:[],workload:{jobs:[],activities:[]}}));
 await w.StudioDashboard.render();await w.studioDashboardReady;
 assert.equal(w.document.querySelectorAll('.cd-donut-segment').length,0);assert.doesNotMatch(w.document.body.innerHTML,/NaN|Infinity/);
 assert.ok([...w.document.querySelectorAll('.cd-bar-track>span')].every(b=>b.style.width==='0%'));
 w.json=async()=>{throw Object.assign(Error('Forbidden'),{status:403})};await w.StudioDashboard.render();await w.studioDashboardReady;
 assert.equal(w.document.querySelector('.cd-donut'),null);assert.equal(w.document.querySelector('.cd-session-table'),null);
 assert.ok([...w.document.querySelectorAll('.cd-bar-row')].every(b=>b.disabled&&b.textContent.includes('Unavailable')));
});
function fixture(){return {jobs:[{id:'a',workspaceId:'one',title:'Wedding',status:'Active Production'},{id:'b',workspaceId:'two',title:'Portrait',status:'Ready for Delivery',jobLead:'lead'}],requests:[{id:'booking',jobId:'a',status:'AwaitingDeposit',request:{workspaceId:'one',requiredDeposit:100,conversationId:'inquiry'},recordedDeposit:20}],inquiries:[{id:'inquiry',workspaceId:'one'},{id:'new',workspaceId:'two'}],schedule:[{id:'s',jobId:'a',startsAtUtc:new Date().toISOString(),status:'Scheduled'}],workload:{jobs:[{id:'a',workspaceId:'one',status:'Active Production'},{id:'b',workspaceId:'two',status:'Ready for Delivery'}],activities:[{id:'delivery',jobId:'b',activityType:'Delivery',dueAtUtc:new Date().toISOString(),status:'NotStarted'}]}};}
function read(data){return async url=>url==='/api/jobs'?data.jobs:url.includes('booking-requests')?data.requests:url.includes('enquiries')?data.inquiries:url.includes('/media-summary')?(data.media||[]):url.endsWith('/schedule/resources')?(data.resources||[]):url.includes('/schedule')?data.schedule:data.workload;}
test('company projection deduplicates converted inquiries and uses recorded delivery deadlines',t=>{
 const w=setup(t,read(fixture())),data=fixture(),p=w.StudioDashboard.project(data,'',today);
 assert.equal(p.inquiries.length,1);assert.equal(p.deposits.length,1);assert.equal(p.unassigned.length,1);assert.equal(p.sessions.length,1);assert.equal(p.due.length,1);
 data.workload.activities[0].dueAtUtc=null;data.jobs[1].eventDate=today;assert.equal(w.StudioDashboard.project(data,'',today).due.length,0);
 const scoped=w.StudioDashboard.project(data,'one',today);assert.equal(scoped.jobs.length,1);assert.equal(scoped.inquiries.length,0);assert.equal(scoped.deliveries.length,0);
});
test('dashboard renders real summaries and links into queues and Job Details',async t=>{
 const w=setup(t,read(fixture()));await w.StudioDashboard.render();await w.studioDashboardReady;
 assert.match(w.document.body.textContent,/Studio at a glance/);assert.equal(w.document.querySelectorAll('.cd-metrics,.cd-metric').length,0);assert.equal(w.document.querySelectorAll('.cd-card').length,5);assert.equal(w.document.querySelector('.mw-today-jobs'),null);
 w.document.querySelector('[data-dashboard-link="deposits"]').click();assert.equal(w.navigation.at(-1).view,'frontdesk');assert.equal(w.navigation.at(-1).options.queue,'deposits');
 w.document.querySelector('[data-dashboard-job="a"]').click();assert.equal(w.navigation.at(-1).options.jobId,'a');
});
test('forbidden or failed sources show unavailable rather than false zero; retry succeeds',async t=>{
 let fail=true;const normal=read(fixture()),w=setup(t,async u=>{if(fail&&u.includes('booking-requests'))throw Error('Forbidden');return normal(u);});
 await w.StudioDashboard.render();await w.studioDashboardReady;
 assert.match(w.document.querySelector('[data-dashboard-link="deposits"]').textContent,/Unavailable/);assert.ok(w.document.querySelector('[data-dashboard-link="deposits"]').disabled);
 fail=false;w.document.querySelector('[data-dashboard-link="retry"]').click();await w.studioDashboardReady;assert.equal(w.document.querySelector('[data-dashboard-link="deposits"]').disabled,false);
});
test('navigation aborts requests and late results cannot repaint another page',async t=>{
 let release,signal;const wait=new Promise(r=>release=r),w=setup(t,async(u,o)=>{signal=o.signal;await wait;return read(fixture())(u);});
 await w.StudioDashboard.render();w.StudioDashboard.dispose();w.document.body.dataset.studioView='jobs';w.document.getElementById('genericView').innerHTML='Jobs';release();await w.studioDashboardReady;
 assert.equal(signal.aborted,true);assert.equal(w.document.getElementById('genericView').textContent,'Jobs');
});
test('old Today URLs resolve to the one Dashboard renderer',async t=>{
 const dom=new JSDOM('<body><div id="conversationView"></div><div id="genericView"></div></body>',{url:'https://studio.test/#studio=today',runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());
 let renders=0;w.StudioDashboard={render(){renders++;},dispose(){}};w.eval(source('studio-navigation.js'));await new Promise(r=>setImmediate(r));
 assert.equal(w.document.body.dataset.studioView,'dashboard');assert.equal(w.location.hash,'#studio=dashboard');assert.equal(renders,1);assert.doesNotMatch(source('studio-navigation.js'),/function (renderToday|paintToday)\(/);
});


test('seven-day forecast excludes today and cancelled sessions and applies workspace scope',t=>{
 const data=fixture(),w=setup(t,read(data));
 const tomorrow=new Date(today+'T12:00:00');tomorrow.setDate(tomorrow.getDate()+1);
 const next=tomorrow.toISOString();
 data.schedule.push({id:'next',jobId:'a',startsAtUtc:next,status:'Scheduled'},{id:'cancelled',jobId:'a',startsAtUtc:next,status:'Cancelled'},{id:'other',jobId:'b',startsAtUtc:next,status:'Scheduled'});
 data.workload.activities.push({id:'nextDelivery',jobId:'a',activityType:'Delivery',dueAtUtc:next,status:'NotStarted'});
 let p=w.StudioDashboard.project(data,'one',today);
 assert.equal(p.week.length,7);assert.equal(p.week[0].sessions,1);assert.equal(p.week[0].deliveries,1);assert.equal(p.sessions.length,1);
 data.workload.activities.at(-1).dueAtUtc=null;p=w.StudioDashboard.project(data,'one',today);assert.equal(p.week[0].deliveries,0);
});
test('forecast day navigation carries the selected date without a new source request',async t=>{
 const w=setup(t,read(fixture()));await w.StudioDashboard.render();await w.studioDashboardReady;
 const day=w.document.querySelector('[data-dashboard-day]');assert.ok(day);day.click();
 assert.equal(w.navigation.at(-1).view,'schedule');assert.equal(w.navigation.at(-1).options.date,day.dataset.dashboardDay);
});

test('session preview resolves assigned worker and authorized thumbnail summary without per-job media calls',async t=>{
 const data=fixture();data.resources=[{id:'person',name:'Samantha Lee',resourceType:'Photographer'}];data.media=[{jobId:'a',thumbnailVersionId:'version'}];data.schedule[0].resourceIds=['person'];
 const urls=[],normal=read(data),w=setup(t,async url=>{urls.push(url);return normal(url)});await w.StudioDashboard.render();await w.studioDashboardReady;
 assert.match(w.document.querySelector('.cd-session-table').textContent,/Samantha Lee/);
 assert.equal(w.document.querySelector('.cd-thumbnail img').getAttribute('src'),'/api/jobs/a/versions/by-id/version/preview?size=thumbnail');
 assert.equal(urls.filter(u=>u.includes('media-summary')).length,1);assert.equal(urls.some(u=>/\/api\/jobs\/.+\/media/.test(u)),false);
});
test('optional thumbnail and worker lookup failures keep operational counts available',async t=>{
 const normal=read(fixture()),w=setup(t,async url=>{if(url.includes('media-summary')||url.endsWith('/resources'))throw Error('Forbidden');return normal(url)});
 await w.StudioDashboard.render();await w.studioDashboardReady;
 assert.equal(w.document.querySelector('.cd-warning'),null);assert.equal(w.document.querySelector('[data-dashboard-link="deposits"]').disabled,false);
 assert.equal(w.document.querySelector('.cd-thumbnail img'),null);
});


test('a revoked login discards cached dashboard records and a fresh denial cannot revive them',async t=>{
 const w=setup(t,read(fixture()));await w.StudioDashboard.render();await w.studioDashboardReady;
 assert.match(w.document.body.textContent,/Wedding/);
 w.json=async()=>{throw Object.assign(Error('Sign in again'),{status:401});};
 await w.StudioDashboard.render();await w.studioDashboardReady;
 assert.doesNotMatch(w.document.body.textContent,/Wedding|Portrait/);assert.ok(w.document.querySelector('.cd-warning'));
 await w.StudioDashboard.render();await w.studioDashboardReady;assert.doesNotMatch(w.document.body.textContent,/Wedding|Portrait/);
});
