const {test}=require('node:test');
const assert=require('node:assert/strict');
const {environment}=require('./job-detail-fixture.cjs');
const settle=()=>new Promise(r=>setTimeout(r,20));
const mountNotes=(w,jobId='job')=>{const card=w.StudioMessageCard.create({id:'notesUnderTest',attributes:'data-message-job-id="'+jobId+'"'});w.document.body.append(card);w.StudioMessageCard.bind(card);return card;};
const openPhoto=async w=>{w.document.querySelector('.jd-photo').ondblclick();await settle();const dialog=w.document.querySelector('.jd-photo-viewer');dialog.querySelector('[data-photo-details]').click();return dialog;};
const button=(w,name)=>w.document.querySelector('[data-job-scroll="'+name+'"]');
const subjectDetails=async w=>{let select=w.document.querySelector('[data-job-subject]');if(!select){await settle();select=w.document.querySelector('[data-job-subject]');}select.value='__details';return select.onchange();};
test('Job Details opens the current media and instructions surface and retains Subjects controls',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 assert.equal(fixture.alerts.length,0);assert.ok(w.document.querySelector('.job-detail-page'));assert.ok(w.document.querySelector('.jd-job-brief'));
 assert.equal(w.document.querySelector('#hierarchyCompose'),null);assert.equal(w.document.querySelector('.job-detail-right'),null);
 assert.match(w.document.querySelector('#studioTodayBrandTitle strong').textContent,/Job Details/);assert.equal(w.document.querySelector('#studioFooterRate').textContent,'40 files · 1 subject');
 await subjectDetails(w);const subjects=w.document.querySelector('#jobDetail-subjects');assert.equal(subjects.hidden,false);assert.match(subjects.textContent,/Morgan/);
 const add=subjects.querySelector('[data-job-add-subject]');assert.equal(add.hidden,false);add.click();assert.equal(subjects.querySelector('form').hidden,false);assert.equal(w.document.querySelector('#jobDetail-media').hidden,true);
 assert.ok(fixture.calls.some(c=>c.url.endsWith('/booking-instructions')));
});

test('Job identity opens an overlay and Tasks retains its own dialog without retired footer actions',async t=>{
 const {w}=environment(t);await w.openWorkspaceJob('job');await settle();
 assert.equal(button(w,'overview'),null);assert.equal(w.document.getElementById('jobDetail-overview'),null);
 const toggle=w.document.querySelector('.jd-job-name-toggle'),facts=w.document.querySelector('#jobIdentityMenu');assert.equal(facts.hidden,true);toggle.click();assert.equal(facts.hidden,false);
 assert.deepEqual([...facts.querySelectorAll('dt')].map(e=>e.textContent),['Current Status','Time in Stage','Client','Owner','Job Type','Event Date','Location','Attending Job Role','Name of Worker']);assert.match(facts.textContent,/Williams & Clarke.*Photography.*Sun Oct 11 2026.*Kingston Studio/s);
 facts.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));assert.equal(facts.hidden,true);
 await w.document.querySelector('[data-job-tasks]').onclick();const tasks=w.document.querySelector('.jd-tasks-dialog');assert.equal(tasks.open,true);assert.match(tasks.textContent,/Job tasks are managed in the configured workflow graph/);assert.ok(tasks.querySelector('[data-open-job-workflow]'));tasks.close();
 assert.equal(w.document.querySelector('#studioFooterJobTools'),null);
});

test('Notes controller saves, views and edits job notes independently of messages',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 const card=mountNotes(w);await card.querySelector('[data-smc-notes-toggle]').onclick();assert.match(card.querySelector('.smc-notes').textContent,/No notes yet/);
 let form=card.querySelector('.smc-note-form');form.elements.body.value='Bring the large backdrop.';await form.onsubmit({preventDefault(){}});
 assert.match(card.querySelector('.smc-note-list').textContent,/Bring the large backdrop/);assert.equal(fixture.jobNotes.job.length,1);
 card.querySelector('[data-edit-note]').click();form=card.querySelector('.smc-note-form');form.elements.body.value='Bring the blue backdrop.';await form.onsubmit({preventDefault(){}});
 assert.equal(fixture.jobNotes.job[0].body,'Bring the blue backdrop.');assert.equal(fixture.jobNotes.job[0].revision,2);assert.equal(fixture.jobNotes.job[0].createdBy,'studio.admin');
 assert.equal(fixture.calls.some(c=>c.url.endsWith('/messages')&&c.options?.method==='POST'),false);
 card.remove();await w.openWorkspaceJob('job2');await settle();mountNotes(w,'job2');await w.document.querySelector('[data-smc-notes-toggle]').onclick();assert.match(w.document.querySelector('.smc-notes').textContent,/No notes yet/);
});
test('notes retry reuses the identifier after a lost save response',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();mountNotes(w);await w.document.querySelector('[data-smc-notes-toggle]').onclick();
 const original=w.json;let fail=true;w.json=async(url,options)=>{const result=await original(url,options);if(url.endsWith('/notes')&&options?.method==='POST'&&fail){fail=false;throw Error('Connection lost');}return result;};
 const form=w.document.querySelector('.smc-note-form');form.elements.body.value='Keep this note.';await form.onsubmit({preventDefault(){}});assert.match(form.textContent,/Connection lost/);assert.equal(form.elements.body.value,'Keep this note.');
 await form.onsubmit({preventDefault(){}});assert.equal(fixture.jobNotes.job.length,1);const posts=fixture.calls.filter(c=>c.url.endsWith('/notes')&&c.options?.method==='POST');assert.equal(JSON.parse(posts[0].options.body).id,JSON.parse(posts[1].options.body).id);
});
test('stale note edits preserve the draft and display the conflict',async t=>{
 const {w,fixture}=environment(t);fixture.jobNotes.job=[{id:'note',body:'Original',revision:1,createdBy:'studio.admin',createdAtUtc:'2026-10-12T03:00:00Z'}];
 await w.openWorkspaceJob('job');await settle();mountNotes(w);await w.document.querySelector('[data-smc-notes-toggle]').onclick();w.document.querySelector('[data-edit-note]').click();
 const form=w.document.querySelector('.smc-note-form');form.elements.body.value='My edit';fixture.jobNotes.job[0]={...fixture.jobNotes.job[0],body:'Newer edit',revision:2};
 await form.onsubmit({preventDefault(){}});assert.match(form.textContent,/note has changed/);assert.equal(form.elements.body.value,'My edit');assert.equal(fixture.jobNotes.job[0].body,'Newer edit');
});
test('leaving Job Details removes its open Tasks dialog and retired Activity surfaces',async t=>{
 const {w}=environment(t);await w.openWorkspaceJob('job');await settle();assert.equal(w.document.querySelector('#studioFooterJobTools [data-job-activity]'),null);
 await w.document.querySelector('[data-job-tasks]').onclick();await w.studioNavigate('today');await settle();assert.equal(w.document.querySelector('.jd-tasks-dialog'),null);assert.equal(w.document.querySelector('.jd-footer-drawer'),null);
});

test('viewer can read Notes without add or edit controls',async t=>{
 const {w,fixture}=environment(t);fixture.canEdit=false;await w.openWorkspaceJob('job');await settle();mountNotes(w);await w.document.querySelector('[data-smc-notes-toggle]').onclick();assert.match(w.document.querySelector('.smc-notes').textContent,/No notes yet/);assert.equal(w.document.querySelector('.smc-note-form'),null);assert.equal(w.document.querySelector('[data-edit-note]'),null);
});
test('a contributor can edit their own Notes but cannot edit another author',async t=>{
 const {w,fixture}=environment(t);fixture.jobNotes.job=[{id:'own',body:'My note',revision:1,createdBy:'studio.admin',createdAtUtc:'2026-10-12T03:00:00Z'},{id:'other',body:'Other note',revision:1,createdBy:'another.user',createdAtUtc:'2026-10-12T03:00:00Z'}];
 const original=w.json;w.json=(url,options)=>url.endsWith('/access')?Promise.resolve({permissions:{editJob:false,manageProduction:true}}):original(url,options);
 await w.openWorkspaceJob('job');await settle();mountNotes(w);await w.document.querySelector('[data-smc-notes-toggle]').onclick();
 assert.ok(w.document.querySelector('[data-edit-note="own"]'));assert.equal(w.document.querySelector('[data-edit-note="other"]'),null);
});
test('Subjects load errors stay visible and Retry restores the panel',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');fixture.failSubjects=true;
 await subjectDetails(w);const panel=w.document.querySelector('#jobDetail-subjects');assert.match(panel.querySelector('[role=alert]').textContent,/Subjects unavailable/);
 fixture.failSubjects=false;await panel.querySelector('.job-subjects-content > button').onclick();assert.match(panel.textContent,/Morgan/);
 assert.equal(fixture.alerts.length,0);
});
test('viewer can inspect Subjects without an Add Subject action',async t=>{
 const {w,fixture}=environment(t);fixture.canEdit=false;await w.openWorkspaceJob('job');await subjectDetails(w);
 assert.equal(w.document.querySelector('[data-job-add-subject]').hidden,true);assert.equal(w.document.querySelector('#addJobSubject'),null);
});
test('stale Subjects response cannot overwrite a subsequently opened job',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();let release;fixture.delaySubjects=new Promise(r=>release=r);
 const pending=subjectDetails(w);await w.openWorkspaceJob('job2');release();await pending;await settle();
 assert.match(w.document.querySelector('.jd-job-name-text').textContent,/Graduation portraits/);
 assert.equal(w.document.querySelector('#jobDetail-subjects').hidden,true);assert.equal(w.document.querySelector('#jobDetail-subjects .person-card'),null);
});
test('Job Details displays one workflow and keeps its editor out of the page',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 assert.equal(w.document.querySelectorAll('.jd-workflow-display').length,1);assert.ok(w.document.querySelector('[data-next-action]'));assert.equal(w.document.querySelector('.job-detail-workflow-dialog'),null);
 assert.equal(fixture.calls.some(c=>c.url.endsWith('/connected-workflow')),true);
});

test('subject View Photos returns to Media and refreshes the subject filter',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 assert.equal(button(w,'subjects'),null);
 const select=w.document.querySelector('[data-job-subject]');select.value='person';await select.onchange();
 assert.ok(w.document.querySelector('[data-job-subject]'));assert.equal(w.document.querySelector('[data-job-subject]').value,'person');
 assert.equal(select.value,'person');assert.ok(fixture.calls.some(c=>c.url.includes('/media?')&&c.url.includes('subjectId=person')));
 select.value='';await select.onchange();assert.equal(select.value,'');
 assert.equal(new URL(fixture.calls.filter(c=>c.url.includes('/media?')).at(-1).url,'https://studio.test').searchParams.has('subjectId'),false);
 await subjectDetails(w);
 w.document.querySelector('[data-show-media]').click();await settle();
 assert.equal(w.document.querySelector('#jobDetail-media').hidden,false);
 assert.ok(fixture.calls.some(c=>c.url.includes('/media?')&&c.url.includes('subjectId=person')));
});
test('job navigation resolves the application lexical workspace cache',async t=>{
 const {w,dom}=environment(t);
 require('vm').runInContext('let workspaceCache = window.workspaceCache; window.workspaceCache = undefined;',dom.getInternalVMContext());
 await w.openWorkspaceJob('job');await settle();
 assert.match(w.document.getElementById('studioBreadcrumb').textContent,/Demo \| Commercial and Products/);
 assert.equal(w.document.getElementById('studioContextRail'),null);
});
test('direct job opening shows Job Details immediately and skips the workspace list',async t=>{
 const {w,fixture}=environment(t);await settle();const original=w.json;let release;
 const delay=new Promise(r=>release=r);
 w.json=async(url,options)=>{if(url==='/api/jobs/job')await delay;return original(url,options);};
 w.document.getElementById('conversationView').classList.add('hidden');w.document.body.dataset.studioView='today';
 const opening=w.openWorkspaceJob('job');
 assert.equal(w.document.getElementById('conversationView').classList.contains('hidden'),false);
 assert.match(w.document.querySelector('#studioTodayBrandTitle strong').textContent,/Job Details/);
 assert.equal(w.detailPane.getAttribute('aria-busy'),'true');
 assert.match(w.detailPane.textContent,/Loading job/);assert.doesNotMatch(w.detailPane.textContent,/Select a job/);
 release();await opening;await settle();
 assert.equal(w.detailPane.hasAttribute('aria-busy'),false);
 assert.ok(w.document.querySelector('#jobDetail-media .jd-gallery-toolbar'));
 assert.equal(fixture.calls.some(c=>c.url==='/api/jobs'),false);
});
test('a slow earlier job lookup cannot replace the latest selection',async t=>{
 const {w}=environment(t);const original=w.json;let release;const delay=new Promise(r=>release=r);
 w.json=async(url,options)=>{if(url==='/api/jobs/job')await delay;return original(url,options);};
 const first=w.openWorkspaceJob('job');await w.openWorkspaceJob('job2');release();await first;
 assert.match(w.document.querySelector('.jd-job-name-text').textContent,/Graduation portraits/);
});
test('job loading failure keeps Job Details visible and supports Retry',async t=>{
 const {w}=environment(t);const original=w.json;let fail=true;
 w.json=async(url,options)=>{if(url==='/api/jobs/job'&&fail)throw new Error('Job unavailable');return original(url,options);};
 await w.openWorkspaceJob('job');
 assert.match(w.document.querySelector('#studioTodayBrandTitle strong').textContent,/Job Details/);
 assert.match(w.detailPane.querySelector('[role=alert]').textContent,/Job unavailable/);
 assert.equal(w.detailPane.hasAttribute('aria-busy'),false);
 fail=false;await w.detailPane.querySelector('button').onclick();await settle();
 assert.ok(w.document.querySelector('#jobDetail-media .jd-gallery-toolbar'));
});
test('leaving Job Details while loading does not replace the destination page',async t=>{
 const {w}=environment(t);const original=w.json;let release;const delay=new Promise(r=>release=r);
 w.json=async(url,options)=>{if(url==='/api/jobs/job')await delay;return original(url,options);};
 const opening=w.openWorkspaceJob('job');w.document.body.dataset.studioView='today';w.document.getElementById('conversationView').classList.add('hidden');
 release();await opening;await settle();
 assert.equal(w.document.body.dataset.studioView,'today');assert.equal(w.document.querySelector('#hierarchyCompose'),null);
});
test('approved layout uses twelve photos, real collection counts and cursor paging',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 assert.equal(w.document.querySelectorAll('.jd-photo').length,12);
 assert.match(w.document.querySelector('.jd-gallery-footer').textContent,/1–12 of 40/);
 const first=w.document.querySelector('.jd-photo').dataset.jdAsset;
 await w.document.querySelector('[data-next]').onclick();assert.match(w.document.querySelector('.jd-gallery-footer').textContent,/13–24 of 40/);
 assert.notEqual(w.document.querySelector('.jd-photo').dataset.jdAsset,first);
 await w.document.querySelector('[data-previous]').onclick();assert.equal(w.document.querySelector('.jd-photo').dataset.jdAsset,first);
 assert.ok(fixture.calls.some(c=>c.url.includes('after=')));
 await w.document.querySelector('[data-filter=Unclassified]').onclick();assert.equal(w.document.querySelectorAll('.jd-photo').length,0);
 assert.match(w.document.querySelector('#studioFooterRate').textContent,/40 files/);
});
test('Selected File saves collection and subjects while preserving production metadata',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 await openPhoto(w);
 const form=w.document.querySelector('.jd-file-form');form.elements.collection.value='Unclassified';
 await form.onsubmit({preventDefault(){},currentTarget:form});
 const write=fixture.calls.find(c=>c.url.includes('/media/assets/')&&c.options?.method==='PUT');assert.ok(write);
 const body=JSON.parse(write.options.body);assert.equal(body.collection,'Unclassified');assert.deepEqual(body.subjects,['person']);assert.equal(body.status,'Ready');
});
test('Selected Image has a thin metadata title and saves token tags on the photo',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();await openPhoto(w);
 const card=w.document.querySelector('.job-detail-selected-file');assert.equal(card.querySelector('.jd-card-heading h2'),null);assert.match(card.querySelector('.jd-selected-file-meta').textContent,/JPEG.*v1/);
 assert.equal(card.querySelector('.job-detail-selected-content > strong'),null);assert.equal(card.querySelectorAll('.jd-file-fields > label').length,2);
 let input=card.querySelector('[data-tag-input]');input.value=' Portrait, portrait, Blue backdrop ';let prevented=false;
 input.onkeydown({key:'Enter',preventDefault(){prevented=true;}});assert.equal(prevented,true);assert.equal(card.querySelectorAll('.jd-tag-token').length,2);
 const form=card.querySelector('form');await form.onsubmit({preventDefault(){}});
 const write=fixture.calls.find(c=>c.url.includes('/media/assets/')&&c.options?.method==='PUT');assert.deepEqual(JSON.parse(write.options.body).tags,['Portrait','Blue backdrop']);
 assert.equal(card.querySelectorAll('.jd-tag-token').length,2);card.querySelector('[data-remove-tag]').click();await card.querySelector('form').onsubmit({preventDefault(){}});
 assert.deepEqual(JSON.parse(fixture.calls.filter(c=>c.url.includes('/media/assets/')&&c.options?.method==='PUT').at(-1).options.body).tags,['Blue backdrop']);
});
test('Job Details exposes Subjects and upload permission through page controls without rails',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();assert.equal(w.document.querySelector('#studioContextRail,.job-detail-right'),null);assert.ok(w.document.querySelector('[data-files]'));
 await subjectDetails(w);assert.equal(w.document.querySelector('[data-job-add-subject]').hidden,false);
 fixture.canEdit=false;await w.openWorkspaceJob('job2');await settle();assert.equal(w.document.querySelector('[data-files],[data-add]'),null);await subjectDetails(w);assert.equal(w.document.querySelector('[data-job-add-subject]').hidden,true);
});

test('gallery search and list controls operate on the displayed page',async t=>{
 const {w}=environment(t);await w.openWorkspaceJob('job');await settle();
 const search=w.document.querySelector('[data-media-search]');search.value='Photo 10';search.oninput({target:search});
 assert.equal(w.document.querySelectorAll('.jd-photo').length,1);
 w.document.querySelector('[data-list]').click();assert.equal(w.document.querySelector('.jd-contact-sheet').classList.contains('list'),true);
 w.document.querySelector('[data-grid]').click();assert.equal(w.document.querySelector('.jd-contact-sheet').classList.contains('list'),false);
});

test('upload selection queues files immediately into the current collection',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 await w.document.querySelector('[data-filter=Selections]').onclick();
 const input=w.document.querySelector('[data-files]');Object.defineProperty(input,'files',{value:[new w.File(['image'],'photo.jpg',{type:'image/jpeg'})]});
 await input.onchange();const upload=fixture.calls.find(c=>c.url.endsWith('/media/uploads'));assert.ok(upload);const batch=fixture.calls.find(c=>c.url.endsWith('/media/upload-batches'));assert.equal(JSON.parse(batch.options.body).collection,'Selections');assert.ok(upload.options.body.get('uploadId'));
 assert.match(w.document.querySelector('[data-media-result]').textContent,/1\/1 stored/);
});
test('late media responses cannot overwrite a newer job or file inspector',async t=>{
 const {w}=environment(t);const original=w.json;let release;const delay=new Promise(r=>release=r);
 w.json=async(url,options)=>{if(url.startsWith('/api/jobs/job/media?'))await delay;return original(url,options);};
 await w.openWorkspaceJob('job');await w.openWorkspaceJob('job2');await settle();release();await settle();
 assert.match(w.document.querySelector('.jd-job-name-text').textContent,/Graduation portraits/);
 await openPhoto(w);assert.match(w.document.querySelector('.jd-selected-preview img').src,/jobs\/job2\/versions/);
});
test('preview failures show a recovery state and use the preview retry API',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 await openPhoto(w);const img=w.document.querySelector('.jd-selected-preview img');img.onerror();
 assert.equal(img.hidden,true);assert.equal(img.nextElementSibling.hidden,false);
 await w.document.querySelector('[data-retry-preview]').onclick();
 assert.ok(fixture.calls.some(c=>c.url.endsWith('/preview/retry')&&c.options.method==='POST'));
 assert.equal(w.document.querySelector('[data-retry-preview]').textContent,'Preview queued');
});

test('workflow next action requires the current owner acceptance before scheduling',async t=>{
 const {w}=environment(t),base={stage:'Needs Photographer Assignment',owner:'owner',ownerAssignment:{assigned_to:'owner',status:'Assigned'},sessions:[]};
 assert.equal(w.StudioWorkflow.nextAction({data:base,userName:'owner',canManage:true}).label,'Accept ownership');
 assert.equal(w.StudioWorkflow.nextAction({data:base,userName:'other',canManage:true}).selector,null);
 const accepted={...base,ownerAssignment:{...base.ownerAssignment,status:'Accepted'}};
 assert.equal(w.StudioWorkflow.nextAction({data:accepted,userName:'owner',canManage:true}).label,'Assign photographer');
 assert.equal(w.StudioWorkflow.nextAction({data:{...accepted,stage:'Awaiting Scheduling'},userName:'owner',canManage:true}).label,'Schedule session');
});

test('Job Details dropped files declare receipts and retries preserve operation identities',async t=>{
 const {w,fixture}=environment(t);const original=w.json;let fail=true;
 w.json=async(url,options)=>{if(url.endsWith('/media/uploads')){fixture.calls.push({url,options});if(fail)throw Error('Response lost');return {assetId:'asset'};}return original(url,options);};
 await w.openWorkspaceJob('job');await settle();const host=w.document.querySelector('#jobDetail-media .job-media-content')||w.document.querySelector('.jd-gallery-toolbar').parentElement;
 const file=new w.File(['image'],'field.jpg',{type:'image/jpeg'});
 w.studioDroppedPhotos=async()=>[file];await host.ondrop({preventDefault(){},dataTransfer:{files:[file]}});
 const first=fixture.calls.find(c=>c.url.endsWith('/media/upload-batches'));assert.ok(first);const declaration=JSON.parse(first.options.body);assert.equal(declaration.files.length,1);
 const retry=host.querySelector('[data-media-result] button');assert.ok(retry);fail=false;await retry.onclick();
 const declarations=fixture.calls.filter(c=>c.url.endsWith('/media/upload-batches')).map(c=>JSON.parse(c.options.body));assert.deepEqual(declarations[0],declarations[1]);
 const uploads=fixture.calls.filter(c=>c.url.endsWith('/media/uploads'));assert.equal(uploads[0].options.body.get('uploadId'),uploads[1].options.body.get('uploadId'));
});



test('Notes controller slides the editor open and preserves a draft when closed',async t=>{const {w}=environment(t);await w.openWorkspaceJob('job');await settle();const card=mountNotes(w);await card.querySelector('[data-smc-notes-toggle]').onclick();const opener=card.querySelector('[data-note-slide]'),slide=card.querySelector('.smc-note-slide'),form=card.querySelector('.smc-note-form');assert.equal(slide.dataset.open,'false');opener.click();assert.equal(slide.dataset.open,'true');form.elements.body.value='Draft instructions';form.querySelector('[data-close-note]').click();assert.equal(slide.dataset.open,'false');opener.click();assert.equal(form.elements.body.value,'Draft instructions');await form.onsubmit({preventDefault(){}});assert.equal(card.querySelector('.smc-note-slide').dataset.open,'false');card.querySelector('[data-edit-note]').click();assert.equal(card.querySelector('.smc-note-slide').dataset.open,'true');});


test('a failure after replacing the loading header reports the original error and supports Retry',async t=>{
 const {w}=environment(t),bind=w.StudioMessageCard.bind;w.StudioMessageCard.bind=()=>{throw Error('Composer initialization failed');};
 await w.openWorkspaceJob('job');assert.match(w.detailPane.querySelector('[role=alert]').textContent,/Composer initialization failed/);assert.equal(w.detailPane.getAttribute('aria-busy'),null);
 w.StudioMessageCard.bind=bind;await w.detailPane.querySelector('button').onclick();await settle();assert.ok(w.document.querySelector('.jd-workflow-display'));assert.equal(w.detailPane.querySelector('[role=alert]')?.textContent.includes('Composer initialization failed'),false);
});
