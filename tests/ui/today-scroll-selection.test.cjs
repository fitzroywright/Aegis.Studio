const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const flush=()=>new Promise(resolve=>setImmediate(resolve));
async function setup(t){
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'https://studio.test/',runScripts:'outside-only',pretendToBeVisual:true});
 t.after(()=>dom.window.close());const w=dom.window,opened=[];
 let releaseMedia;const mediaPending=new Promise(resolve=>releaseMedia=resolve);
 const date=new Date().toISOString().slice(0,10);
 const jobs=Array.from({length:10},(_,i)=>({id:'job-'+(i+1),title:'Portrait '+String(i+1).padStart(2,'0'),clientName:'Client '+(i+1),eventDate:date,status:'Scheduled',jobLead:'staff'}));
 Object.assign(w,{esc:s=>String(s??''),workspaceCache:[],alert:message=>assert.fail(message),
  StudioStatusOrder:{sort:v=>[...new Set(v)]},StudioSubjectPicker:{isClosed:()=>false},
  json:async url=>{
   if(url==='/api/today/jobs')return jobs;
   if(url==='/api/auth/me')return {user:{userName:'staff'}};
   if(url==='/api/today/media-summary'){await mediaPending;return jobs.map(j=>({jobId:j.id,assetCount:40,thumbnailVersionId:'photo-'+j.id}));}
   return [];
  },openWorkspaceJob:async id=>{opened.push(id);w.studioBeginJobNavigation(id);w.document.body.dataset.studioView='workspaces';}
 });
 w.eval(fs.readFileSync(path.join(root,'studio-message-card.js'),'utf8'));
 w.eval(fs.readFileSync(path.join(root,'studio-navigation.js'),'utf8'));
 for(let i=0;i<8;i++)await flush();
 return {w,opened,releaseMedia};
}
test('a scrolled seventh row retains its identity, focus, and scroll through late previews and opens only that job',async t=>{
 const {w,opened,releaseMedia}=await setup(t),d=w.document;
 const list=d.querySelector('.mw-today-job-list'),row=list.querySelector('[data-open-job="job-7"]');
 assert.equal(list.querySelectorAll('[data-open-job]').length,10);
 list.scrollTop=300;list.scrollLeft=25;row.focus();
 row.dispatchEvent(new w.MouseEvent('mousedown',{bubbles:true}));
 releaseMedia();await w.studioTodayReady;
 assert.equal(d.querySelector('.mw-today-job-list'),list);
 assert.equal(list.querySelector('[data-open-job="job-7"]'),row);
 assert.equal(d.activeElement,row);
 assert.equal(list.scrollTop,300);assert.equal(list.scrollLeft,25);
 assert.ok(row.querySelector('img'),'late preview is rendered in the retained row');
 row.dispatchEvent(new w.MouseEvent('mouseup',{bubbles:true}));row.click();await flush();
 assert.deepEqual(opened,['job-7']);
 assert.equal(w.history.state.studioJobId,'job-7');
 // A queued click on the now-hidden Today list cannot replace the opened job.
 list.querySelector('[data-open-job="job-2"]').click();await flush();
 assert.deepEqual(opened,['job-7']);
});
test('retained rows still filter correctly and keyboard activation opens their job ID',async t=>{
 const {w,opened,releaseMedia}=await setup(t);releaseMedia();await w.studioTodayReady;
 const d=w.document,list=d.querySelector('.mw-today-job-list'),row=list.querySelector('[data-open-job="job-7"]');
 const input=d.querySelector('[data-mw-job-search]');input.value='Portrait 07';input.dispatchEvent(new w.Event('input'));
 assert.equal(d.querySelector('.mw-today-job-list'),list);
 assert.equal(list.querySelectorAll('[data-open-job]').length,1);
 assert.equal(list.querySelector('[data-open-job]'),row);
 row.focus();row.dispatchEvent(new w.MouseEvent('click',{bubbles:true,detail:0}));await flush();
 assert.deepEqual(opened,['job-7']);
});
