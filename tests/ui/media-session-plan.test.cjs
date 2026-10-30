const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const settle=async()=>{for(let i=0;i<12;i++)await new Promise(r=>setImmediate(r));};
function fixture(t,{sessions=[{id:'morning',title:'Morning'}],edit=true,assets,subjects}={}){
 const dom=new JSDOM('<div id="conversationView"></div><div id="genericView"></div>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());
 const writes=[],navigation=[];
 const people=subjects||[{id:'p',name:'Amara',reference:'1001',collection:'Class 6A',imageCount:1,status:'Photographed'},{id:'missing',name:'Sabrina',collection:'Class 6A',imageCount:0,status:'Not Photographed'},{id:'absent',name:'Malik',collection:'Class 6B',imageCount:0,status:'Absent'}];
 const media=assets||[{id:'a',versionId:'v',fileName:'portrait.CR3',contentType:'application/octet-stream',collection:'Class 6A',subjects:['p'],status:'Needs Review',uploadedBy:'Bryan',assignedTo:'editor'},{id:'b',versionId:'vb',fileName:'other.jpg',contentType:'image/jpeg',collection:'Unclassified',subjects:[],status:'Needs Sorting',uploadedBy:'Nicole'}];
 Object.assign(w,{genericView:w.document.getElementById('genericView'),conversationView:w.document.getElementById('conversationView'),studioNavigate:async(...args)=>navigation.push(args),openWorkspaceJob:async id=>navigation.push(['job',id]),confirm:()=>true,prompt:()=> 'Wrong batch',StudioWorkflow:{open:async id=>navigation.push(['handoff',id])}});
 w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.remove();};
 w.studioDialog=(title,html)=>{const d=w.document.createElement('dialog');d.innerHTML=`<h2>${title}</h2>${html}`;w.document.body.append(d);d.showModal();return d;};
 w.json=async(url,o)=>{if(o){writes.push({url,options:o,body:typeof o.body==='string'?JSON.parse(o.body):o.body});return url.endsWith('/uploads')?{assetId:'new',unmatched:false}:{};}
 const u=new URL(url,'https://studio.test');
 if(u.pathname==='/api/jobs')return [{id:'j',workspaceId:'ws',title:'Graduation',status:'Photography'},{id:'other',workspaceId:'ws2',title:'Wedding',status:'Photography'}];
 if(u.pathname==='/api/workspaces')return [{id:'ws',name:'Kings Plaza'},{id:'ws2',name:'Weddings'}];
 if(u.pathname.endsWith('/access'))return {permissions:{manageProduction:edit}};
 if(u.pathname.endsWith('/shoot-sessions'))return sessions;
 if(u.pathname.endsWith('/session-plan'))return people;
 if(u.pathname.endsWith('/upload-batches'))return [];
 if(u.pathname.endsWith('/media')){const after=u.searchParams.get('after'),start=after?media.findIndex(a=>a.id===after)+1:0;return {collections:['Class 6A','Class 6B'],subjects:people,assets:media.slice(start,start+200),hasMore:start+200<media.length,totalAssets:media.length};}
 throw Error('Unexpected '+url);};
 w.eval(fs.readFileSync(path.join(root,'studio-status-order.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'subject-picker.js'),'utf8'));w.eval(fs.readFileSync(path.join(root,'media-review.js'),'utf8'));
 const q=s=>w.document.querySelector(s);
 const chooseJob=async(id='j')=>{q('[data-job]').value=id;await q('[data-job]').onchange();};
 const files=async names=>{Object.defineProperty(q('[data-files]'),'files',{configurable:true,value:names.map(name=>new w.File(['photo'],name))});q('[data-files]').onchange();await settle();};
 return {w,q,writes,navigation,chooseJob,files,media};
}
test('plan collections, coverage and missing-media destinations use real job subjects',async t=>{
 const f=fixture(t);await f.w.showMediaReview();await f.chooseJob();
 assert.match(f.q('[data-coverage]').textContent,/3 planned people \/ groups.*1 with media.*1 missing media.*1 absent/);
 assert.equal(f.q('[data-collection-name="Class 6B"]').textContent,'Class 6B0');
 f.q('[data-review-person="missing"]').click();assert.equal(f.q('[data-destination]').textContent,'Sabrina');assert.equal(f.q('[data-subject]').value,'missing');assert.equal(f.q('[data-collection]').value,'Class 6A');
 await f.files(['portrait.NEF']);assert.equal(f.writes[0].body.sessionId,'morning');assert.equal(f.writes[0].body.subjectId,'missing');assert.equal(f.writes[0].body.collection,'Class 6A');
 assert.equal(f.writes[1].body.get('uploadId'),f.writes[0].body.files[0].uploadId);
 assert.equal(f.writes.filter(x=>x.options.method==='PUT').length,0);assert.match(f.q('[data-progress]').textContent,/1 of 1 uploaded/);
});
test('multiple sessions require a choice, while a job without sessions permits unclassified upload',async t=>{
 const f=fixture(t,{sessions:[{id:'am',title:'Morning'},{id:'pm',title:'Afternoon'}]});await f.w.showMediaReview();await f.chooseJob();await f.files(['raw.CR3']);assert.equal(f.writes.length,0);assert.match(f.q('[data-feedback]').textContent,/Choose the upload session/);
 f.q('[data-session]').value='pm';f.q('[data-session]').onchange();await f.files(['raw.CR3']);assert.equal(f.writes[0].body.sessionId,'pm');assert.equal(f.writes[0].body.collection,'Unclassified');
});
test('a failed file retries with its original receipt, skips successful files and freezes destination',async t=>{
 const f=fixture(t),read=f.w.json;let fail=true;
 f.w.json=async(url,o)=>{const response=await read(url,o);if(url.endsWith('/uploads')&&o.body.get('file').name==='fail.jpg'&&fail){fail=false;throw Error('Connection lost');}return response;};
 await f.w.showMediaReview();await f.chooseJob();await f.files(['good.jpg','fail.jpg']);
 assert.equal(f.q('[data-retry]').hidden,false);assert.equal(f.q('[data-job]').disabled,true);
 f.q('[data-collection-name="Class 6A"]').click();assert.equal(f.q('[data-collection]').value,'Unclassified');
 await f.q('[data-retry]').onclick();const declarations=f.writes.filter(x=>x.url.endsWith('/upload-batches'));assert.deepEqual(declarations[0].body,declarations[1].body);
 const uploads=f.writes.filter(x=>x.url.endsWith('/uploads'));assert.equal(uploads.length,3);assert.equal(uploads[1].body.get('uploadId'),uploads[2].body.get('uploadId'));assert.equal(f.q('[data-job]').disabled,false);
});
test('all media pages are loaded and coverage is independent of gallery filtering',async t=>{
 const assets=Array.from({length:205},(_,i)=>({id:'a'+String(i).padStart(3,'0'),fileName:i===204?'last.jpg':'image'+i+'.jpg',collection:'Class 6A',subjects:['p'],status:'Needs Review'}));
 const f=fixture(t,{assets});await f.w.showMediaReview();await f.chooseJob();assert.equal(f.w.document.querySelectorAll('[data-asset]').length,205,f.q('[data-feedback]').textContent+' / '+f.q('[data-gallery]').textContent);
 f.q('[data-search]').value='last.jpg';f.q('[data-search]').oninput();assert.equal(f.w.document.querySelectorAll('[data-asset]').length,1);assert.match(f.q('[data-coverage]').textContent,/1 missing media/);
});
test('RAW preview, original download, list and status filters survive the redesign',async t=>{
 const f=fixture(t);await f.w.showMediaReview();await f.chooseJob();const img=f.q('img');assert.match(img.src,/versions\/by-id\/v\/preview/);img.onerror();assert.match(img.src,/preview\/fallback/);
 f.q('[data-detail="a"]').click();await settle();assert.match(f.q('dialog a').href,/versions\/by-id\/v\/content/);f.q('dialog').close();f.q('[data-view]').click();assert.ok(f.q('.studio-media-list'));
 f.q('[data-media-status]').value='Needs Sorting';f.q('[data-media-status]').onchange();assert.equal(f.w.document.querySelectorAll('[data-asset]').length,1);assert.equal(f.q('[data-asset]').dataset.asset,'b');
});
test('wedding photos support multiple people and collection moves preserve associations and production status',async t=>{
 const f=fixture(t);await f.w.showMediaReview();await f.chooseJob();f.q('[data-select="a"]').checked=true;f.q('[data-select="a"]').onchange();await f.q('[data-assign]').onclick();
 const target=f.q('dialog [data-target]');for(const option of target.options)option.selected=['p','missing'].includes(option.value);await f.q('dialog form').onsubmit({preventDefault(){}});
 assert.deepEqual(f.writes[0].body,{status:'Needs Review',collection:'Class 6A',subjects:['p','missing'],assignedTo:'editor',captureAtUtc:null});
 f.q('[data-select="a"]').checked=true;f.q('[data-select="a"]').onchange();await f.q('[data-move]').onclick();f.q('dialog [data-target]').value='Unclassified';await f.q('dialog form').onsubmit({preventDefault(){}});
 assert.equal(f.writes[1].body.collection,'Unclassified');assert.deepEqual(f.writes[1].body.subjects,['p']);
});
test('recursive folder drops read every directory batch and retain explicit destination',async t=>{
 const f=fixture(t);await f.w.showMediaReview();await f.chooseJob();f.q('[data-review-person="missing"]').click();
 const file=name=>({isFile:true,file:resolve=>resolve(new f.w.File(['photo'],name))});let read=0;
 const entry={isDirectory:true,createReader:()=>({readEntries:resolve=>resolve(read++===0?[file('one.jpg')]:read===2?[file('two.NEF')]:[])})};
 await f.q('[data-drop]').ondrop({preventDefault(){},dataTransfer:{items:[{webkitGetAsEntry:()=>entry}]}});
 assert.equal(f.writes[0].body.files.length,2);assert.equal(f.writes[0].body.collection,'Class 6A');assert.equal(f.writes[0].body.subjectId,'missing');
});
test('read-only staff can browse and navigate without upload or tagging controls enabled',async t=>{
 const f=fixture(t,{edit:false});await f.w.showMediaReview();await f.chooseJob();assert.equal(f.q('[data-choose]').disabled,true);await f.files(['photo.jpg']);assert.equal(f.writes.length,0);
 await f.q('[data-plan]').onclick();assert.equal(JSON.stringify(f.navigation[0]),JSON.stringify(['session-plan',{jobId:'j'}]));await f.q('[data-handoff]').onclick();assert.deepEqual(f.navigation[1],['handoff','j']);
});
test('paused upload survives leaving Media and returning, with the captured job and destination',async t=>{
 const f=fixture(t),read=f.w.json;let resume,started;const hold=new Promise(r=>resume=r),begin=new Promise(r=>started=r);
 f.w.json=async(url,o)=>{if(url.endsWith('/uploads')&&o.body.get('file').name==='one.jpg'){started();await hold;}return read(url,o);};
 await f.w.showMediaReview();await f.chooseJob();const input=f.q('[data-files]');Object.defineProperty(input,'files',{value:[new f.w.File(['x'],'one.jpg'),new f.w.File(['x'],'two.jpg')]});input.onchange();await begin;
 f.q('[data-pause]').click();await f.w.showMediaReview();assert.equal(f.q('[data-job]').value,'j',f.q('[data-feedback]').textContent+' / '+f.q('[data-job]').outerHTML);assert.equal(f.q('[data-job]').disabled,true);resume();await settle();assert.match(f.q('[data-progress]').textContent,/Paused/);f.q('[data-pause]').click();await settle();assert.equal(f.writes.filter(x=>x.url.endsWith('/uploads')).length,2);assert.equal(f.q('[data-job]').disabled,false);
});
test('failed batch can be explicitly cancelled without removing stored photos',async t=>{
 const f=fixture(t),read=f.w.json;f.w.json=async(url,o)=>{if(url.endsWith('/uploads'))throw Error('Offline');return read(url,o);};await f.w.showMediaReview();await f.chooseJob();await f.files(['fail.jpg']);await f.q('[data-cancel-upload]').onclick();const cancel=f.writes.find(x=>x.url.endsWith('/cancel'));assert.equal(cancel.body.reason,'Wrong batch');assert.equal(f.q('[data-job]').disabled,false);
});
test('out-of-order job loads cannot overwrite the current job or its destination',async t=>{
 const f=fixture(t),read=f.w.json;let finish;const held=new Promise(r=>finish=r);f.w.json=async(url,o)=>{if(url.startsWith('/api/jobs/j/media?'))await held;return read(url,o);};await f.w.showMediaReview();const first=f.chooseJob('j');await settle();await f.chooseJob('other');finish();await first;assert.equal(f.q('[data-job]').value,'other');assert.match(f.q('[data-destination-session]').textContent,/Wedding/);
});
