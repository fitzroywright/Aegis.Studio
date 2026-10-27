const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot'),read=f=>fs.readFileSync(path.join(root,f),'utf8'),settle=()=>new Promise(r=>setImmediate(r));
test('Today attention lists individual jobs and tasks, combines reasons and opens the exact job',async t=>{
 const dom=new JSDOM(read('index.html'),{url:'https://studio.test',runScripts:'outside-only',pretendToBeVisual:true}),w=dom.window;t.after(()=>w.close());
 const jobs=[{id:'j1',title:'Miller portraits',status:'Scheduled',jobLead:'staff'},{id:'j2',title:'Campion graduation',status:'Awaiting Scheduling',jobLead:'staff'},{id:'j3',title:'Carter wedding',status:'Awaiting Upload',jobLead:'staff'}].map(j=>({...j,eventDate:new Date().toISOString().slice(0,10)}));
 const tasks=[{id:'t1',jobId:'j1',title:'Retouch selected portraits',assignedTo:'staff',status:'Blocked',blockedReason:'Missing originals',dueAtUtc:new Date(Date.now()-86400000).toISOString()},{id:'t2',jobId:'j1',title:'Already finished task',assignedTo:'staff',status:'Completed',dueAtUtc:'2020-01-01'}];
 Object.assign(w,{genericView:w.document.getElementById('genericView'),conversationView:w.document.getElementById('conversationView'),workspaceCache:[],loadWorkspaces:async()=>[],fetch:async()=>({ok:true,json:async()=>[]}),StudioStatusOrder:{sort:v=>[...new Set(v)]},StudioSubjectPicker:{isClosed:()=>false},json:async url=>url==='/api/today/jobs'?jobs:url==='/api/today/tasks'?tasks:url==='/api/auth/me'?{user:{userName:'staff'}}:[]});
 for(const name of ['calendar-layouts.js','studio-message-card.js','studio-page.js','studio-shell-nav.js','studio-navigation.js'])w.eval(read(name));
 w.document.dispatchEvent(new w.Event('DOMContentLoaded'));for(let i=0;i<6;i++)await settle();await w.studioTodayReady;await settle();
 const drawer=w.document.getElementById('studioAttentionDrawer'),items=[...drawer.querySelectorAll('[data-attention-item]')];
 assert.equal(items.length,3);assert.equal(w.document.getElementById('studioAttentionCount').textContent,'3');
 assert.match(items[0].textContent,/Retouch selected portraits.*Miller portraits.*Overdue.*Missing originals/s);
 assert.match(items[1].textContent,/Campion graduation.*Awaiting Scheduling/s);assert.match(items[2].textContent,/Carter wedding.*Awaiting Upload/s);
 assert.doesNotMatch(drawer.textContent,/Already finished task|Overdue tasks|Blocked tasks|Job exceptions/);
 const opened=[];w.studioNavigate=async(view,options)=>opened.push({view,...options});
 w.document.getElementById('studioAttentionTrigger').click();items[0].click();await settle();
 assert.deepEqual(opened,[{view:'workspaces',jobId:'j1'}]);assert.equal(drawer.hidden,true);
});
