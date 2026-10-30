const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot'),read=name=>fs.readFileSync(path.join(root,name),'utf8');
function environment(t,{edit=true}={}){
 const dom=new JSDOM('<section id="media"></section><section id="brief"></section>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());
 const calls=[],navigation=[],assets=[{id:'a',fileName:'family.jpg',versionId:'va',contentType:'image/jpeg',collection:'Family group',subjects:['family'],status:'Needs Review',assignedTo:'editor',captureAtUtc:'2026-10-30T12:00:00Z',tags:['Retake']},{id:'b',fileName:'parents.jpg',versionId:'vb',contentType:'image/jpeg',collection:'Parents',subjects:['parents'],status:'Ready',assignedTo:'other',tags:['Approved']}];
 const people=[{id:'family',name:'Wilson family',collection:'Family group',imageCount:1},{id:'parents',name:'Wilson parents',collection:'Parents',imageCount:1},{id:'child',name:'Child portrait',reference:'C1',collection:'Children',imageCount:0}];
 Object.assign(w,{studioNavigate:async(...args)=>navigation.push(args),formatBytes:()=>'',studioFirstName:String,confirm:()=>true});
 w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.dispatchEvent(new w.Event('close'));this.remove();};
 w.studioDialog=(title,html)=>{const d=w.document.createElement('dialog');d.innerHTML='<header><h2>'+title+'</h2><button data-close>Close</button></header><div class="studio-dialog-body">'+html+'</div>';w.document.body.append(d);d.querySelector('[data-close]').onclick=()=>d.close();d.showModal();return d;};
 w.json=async(url,options)=>{calls.push({url,options});if(options?.method==='PUT'){Object.assign(assets.find(a=>url.endsWith('/'+a.id)),JSON.parse(options.body));return null;}if(options)return {};
 const u=new URL(url,'https://studio.test');
 if(u.pathname.endsWith('/access'))return {permissions:{manageProduction:edit,editJob:edit,addAttachments:edit}};
 if(u.pathname.endsWith('/media')){const shown=assets.filter(a=>(!u.searchParams.get('collection')||a.collection===u.searchParams.get('collection'))&&(!u.searchParams.get('subjectId')||a.subjects.includes(u.searchParams.get('subjectId'))));return {assets:structuredClone(shown),subjects:people,collections:['Family group','Parents','Children'],collectionCounts:{'Family group':assets.filter(a=>a.collection==='Family group').length,Parents:assets.filter(a=>a.collection==='Parents').length,Children:assets.filter(a=>a.collection==='Children').length,Unclassified:assets.filter(a=>a.collection==='Unclassified').length},totalAssets:shown.length,totalSubjects:3,hasMore:false};}
 if(u.pathname.endsWith('/booking-instructions'))return [{body:'Keep siblings together',source:'Booking'}];
 if(u.pathname.endsWith('/notes'))return [{body:'Keep siblings together'},{body:'Retake the family portrait'}];
 if(u.pathname.endsWith('/shoot-sessions'))return [];
 if(u.pathname.endsWith('/attachments'))return [{id:'f',fileName:'Brief.pdf'}];
 if(u.pathname==='/api/messages/threads')return [];
 throw Error('Unexpected '+url);};
 w.eval(read('studio-page.js'));w.eval(read('subject-picker.js'));w.eval(read('job-details.js'));w.eval(read('job-brief.js'));
 const host=w.document.getElementById('media'),brief=w.document.getElementById('brief'),job={id:'job',title:'Wilson Family - Retake Day',conversationId:'conversation'};
 let live=true;const mount=()=>w.StudioJobDetails.media(job,host,null,()=>live,()=>{}),q=s=>w.document.querySelector(s);
 return {w,host,brief,job,assets,people,calls,navigation,mount,q,stop:()=>live=false};
}
test('Session Plan uses real collections, includes empty planned groups and opens the full job plan',async t=>{
 const f=environment(t);await f.mount();
 assert.equal(f.q('.jd-collections'),null);assert.match(f.q('.jd-plan-nav header').textContent,/Session Plan3 subjects/);
 assert.deepEqual(Array.from(f.host.querySelectorAll('[data-filter]'),b=>b.dataset.filter),['','Family group','Parents','Children','Unclassified']);
 assert.match(f.q('[data-filter="Children"]').textContent,/Children0/);
 await f.q('[data-job-session-plan]').onclick();assert.deepEqual(JSON.parse(JSON.stringify(f.navigation)),[['session-plan',{jobId:'job'}]]);
 await f.q('[data-filter="Children"]').onclick();assert.match(f.q('.jd-contact-sheet').textContent,/No media here/);
 assert.match(f.q('.jd-plan-subjects').textContent,/Child portrait/);
 assert.equal(f.q('[data-filter="Children"]').getAttribute('aria-pressed'),'true');
});
test('collection and subject navigation share the gallery filters and clear an incompatible subject',async t=>{
 const f=environment(t);await f.mount();await f.q('[data-plan-subject="family"]').onclick();
 assert.match(f.calls.filter(c=>c.url.includes('/media?')).at(-1).url,/subjectId=family/);
 await f.q('[data-filter="Parents"]').onclick();
 const request=new URL(f.calls.filter(c=>c.url.includes('/media?')).at(-1).url,'https://studio.test');
 assert.equal(request.searchParams.get('collection'),'Parents');assert.equal(request.searchParams.has('subjectId'),false);
 assert.deepEqual(Array.from(f.host.querySelectorAll('[data-jd-asset]'),b=>b.dataset.jdAsset),['b']);
 assert.equal(f.q('[data-collection]').value,'Parents');
});
test('multi-photo subject assignment and collection moves retain production details and tags',async t=>{
 const f=environment(t);await f.mount();f.q('[data-jd-asset="b"]').click();assert.equal(f.q('[data-selection-count]').textContent,'2 selected');
 f.q('[data-assign-selected]').click();let form=f.q('dialog form');form.elements.target.options[2].selected=true;
 await form.onsubmit({preventDefault(){}});assert.ok(f.assets.every(a=>a.subjects.length===1&&a.subjects[0]==='child'));
 assert.equal(f.assets[0].status,'Needs Review');assert.equal(f.assets[0].assignedTo,'editor');assert.deepEqual(f.assets[0].tags,['Retake']);assert.equal(f.assets[0].captureAtUtc,'2026-10-30T12:00:00Z');
 f.q('[data-move-selected]').click();form=f.q('dialog form');form.elements.target.value='Children';await form.onsubmit({preventDefault(){}});
 assert.ok(f.assets.every(a=>a.collection==='Children'));assert.ok(f.assets.every(a=>a.subjects[0]==='child'));assert.deepEqual(f.assets[1].tags,['Approved']);
 f.q('[data-clear-selected]').click();assert.equal(f.q('[data-selection-count]').textContent,'0 selected');assert.equal(f.q('[data-move-selected]').disabled,true);
});
test('partial selection edits report progress and retry only the unsaved photo',async t=>{
 const f=environment(t),read=f.w.json;let fail=true;const writes=[];
 f.w.json=async(url,options)=>{if(options?.method==='PUT'){writes.push(url);if(url.endsWith('/b')&&fail){fail=false;throw Error('Offline');}}return read(url,options);};
 await f.mount();f.q('[data-jd-asset="b"]').click();f.q('[data-move-selected]').click();const form=f.q('dialog form');form.elements.target.value='Children';await form.onsubmit({preventDefault(){}});
 assert.match(form.querySelector('[role=alert]').textContent,/1 of 2 saved.*Offline/);assert.equal(form.elements.target.disabled,true);
 await form.onsubmit({preventDefault(){}});assert.equal(writes.filter(url=>url.endsWith('/a')).length,1);assert.equal(writes.filter(url=>url.endsWith('/b')).length,2);assert.ok(f.assets.every(a=>a.collection==='Children'));
});
test('read-only staff retain browsing and the full plan link without mutation controls',async t=>{
 const f=environment(t,{edit:false});await f.mount();assert.equal(f.q('[data-add],[data-assign-selected],[data-move-selected]'),null);
 await f.q('[data-filter="Parents"]').onclick();assert.equal(f.q('[data-jd-asset]').dataset.jdAsset,'b');await f.q('[data-job-session-plan]').onclick();assert.equal(f.navigation.length,1);assert.equal(f.calls.some(c=>c.options?.method==='PUT'),false);
});
test('unfinished upload batches keep their original destination when gallery filters change',async t=>{
 const f=environment(t),read=f.w.json;f.w.json=async(url,options)=>{if(url.endsWith('/uploads'))throw Error('Offline');return read(url,options);};await f.mount();
 const input=f.q('[data-files]');Object.defineProperty(input,'files',{value:[new f.w.File(['photo'],'family.jpg')]});await input.onchange();
 await f.q('[data-filter="Parents"]').onclick();assert.equal(f.q('[data-collection]').value,'');assert.match(f.q('[data-media-result]').textContent,/current upload/);assert.equal(f.q('[data-assign-selected]').disabled,true);assert.match(f.q('[data-media-result] button').textContent,/Retry original batch/);
});
test('compact instructions start collapsed, reveal the editor on demand and save through job notes',async t=>{
 const f=environment(t);await f.w.StudioJobBrief.mount(f.job,f.brief,()=>true);
 assert.equal(f.q('.jd-brief-columns').hidden,true);assert.equal(f.q('form').hidden,true);assert.equal(f.q('[data-instruction-count]').textContent,'2');assert.equal(f.q('[data-file-count]').textContent,'1');
 f.q('[data-add-instruction]').click();assert.equal(f.q('.jd-brief-columns').hidden,false);assert.equal(f.q('form').hidden,false);assert.equal(f.w.document.activeElement,f.q('textarea'));
 const form=f.q('form');form.elements.body.value='Bring the blue backdrop';await form.onsubmit({preventDefault(){}});
 const request=f.calls.find(c=>c.url.endsWith('/notes')&&c.options?.method==='POST');assert.equal(JSON.parse(request.options.body).body,'Bring the blue backdrop');assert.equal(f.q('form').hidden,true);
});
test('supporting-file action launches the file picker directly and preserves internal visibility',async t=>{
 const f=environment(t);await f.w.StudioJobBrief.mount(f.job,f.brief,()=>true);const input=f.q('input[type=file]');assert.equal(input.hidden,true);let clicked=false;input.click=()=>clicked=true;
 f.q('[data-add-file]').click();assert.equal(clicked,true);Object.defineProperty(input,'files',{value:[new f.w.File(['brief'],'brief.pdf')]});await input.onchange();
 const request=f.calls.find(c=>c.url.includes('/attachments?visibility=Internal')&&c.options?.method==='POST');assert.ok(request);assert.equal(request.options.body.get('file').name,'brief.pdf');
});
test('collapsed read failures stay visible and permission failures do not expose editors',async t=>{
 const f=environment(t);f.w.json=async()=>{throw Error('Restricted');};await f.w.StudioJobBrief.mount(f.job,f.brief,()=>true);
 assert.match(f.q('.jd-brief-feedback').textContent,/instructions could not be loaded.*supporting files could not be loaded/s);
 assert.equal(f.q('[data-add-instruction]').hidden,true);assert.equal(f.q('[data-add-file]').hidden,true);assert.equal(f.q('textarea,input[type=file]'),null);
});

test('disposing a job closes the selection editor and prevents changes to the previous job',async t=>{
 const f=environment(t),controller=await f.mount();f.q('[data-assign-selected]').click();const form=f.q('dialog form');form.elements.target.options[2].selected=true;
 f.stop();controller.dispose();assert.equal(f.q('dialog'),null);await form.onsubmit({preventDefault(){}});assert.equal(f.calls.some(c=>c.options?.method==='PUT'),false);
});
