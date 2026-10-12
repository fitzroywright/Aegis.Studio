const {test}=require('node:test');
const assert=require('node:assert/strict');
const {environment}=require('./job-detail-fixture.cjs');
const settle=()=>new Promise(r=>setTimeout(r,20));
const button=(w,name)=>w.document.querySelector('[data-job-scroll="'+name+'"]');
const subjectDetails=async w=>{let select=w.document.querySelector('[data-job-subject]');if(!select){await settle();select=w.document.querySelector('[data-job-subject]');}select.value='__details';return select.onchange();};
test('Today job opens with working reply and Subjects controls in their final containers',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 assert.equal(fixture.alerts.length,0);assert.ok(w.document.querySelector('.job-detail-page'));
 assert.equal(typeof w.document.querySelector('#hierarchyCompose').onsubmit,'function');
 assert.match(w.document.querySelector('#studioTodayBrandTitle strong').textContent,/Job Details/);
 assert.equal(w.document.querySelector('#studioFooterRate').textContent,'40 files · 1 subject');
 await subjectDetails(w);
 const subjects=w.document.querySelector('#jobDetail-subjects');assert.equal(subjects.hidden,false);assert.match(subjects.textContent,/Morgan/);
 const add=subjects.querySelector('[data-job-add-subject]');assert.equal(add.hidden,false);add.click();assert.equal(subjects.querySelector('form').hidden,false);
 assert.equal(w.document.querySelector('#jobDetail-media').hidden,true);
 const form=w.document.querySelector('#hierarchyCompose');form.querySelector('textarea').value='Confirmed';
 await form.onsubmit({preventDefault(){},currentTarget:form});
 assert.ok(fixture.calls.some(c=>c.url==='/api/conversations/conversation/messages'&&JSON.parse(c.options.body).body==='Confirmed'));
});
test('job facts appear below the title while Tasks and Activity render their own panels',async t=>{
 const {w}=environment(t);await w.openWorkspaceJob('job');await settle();
 assert.equal(button(w,'overview'),null);assert.equal(w.document.getElementById('jobDetail-overview'),null);
 const facts=w.document.querySelector('.job-detail-heading .job-detail-facts');
 assert.equal(w.document.querySelector('.job-detail-title .jd-client-name strong').textContent,'Williams & Clarke');
 assert.deepEqual(Array.from(facts.querySelectorAll('dt'),field=>field.textContent),['Type','Event date','Location','Photographer']);
 assert.deepEqual(Array.from(facts.querySelectorAll('dd'),field=>field.textContent),['Photography','Sun Oct 11 2026','Kingston Studio','Unassigned']);
 assert.equal(button(w,'tasks'),null);assert.equal(button(w,'activity'),null);
 assert.equal(w.document.querySelector('[data-media-search]').nextElementSibling.matches('[data-job-tasks]'),true);
 await w.document.querySelector('[data-job-tasks]').onclick();
 const tasks=w.document.querySelector('.jd-tasks-dialog');assert.equal(tasks.open,true);assert.match(tasks.textContent,/Retouch portraits/);tasks.close();
 const activity=w.document.querySelector('#studioFooterJobTools [data-job-activity]');await activity.onclick();
 const drawer=w.document.getElementById('jobActivityDrawer');assert.equal(drawer.getAttribute('aria-hidden'),'false');assert.match(drawer.textContent,/Front Desk booking/);
 drawer.querySelector('[data-close-drawer]').click();assert.equal(activity.getAttribute('aria-expanded'),'false');
 assert.equal(w.document.querySelectorAll('.job-detail-center > section:not([hidden])').length,1);
 w.document.querySelector('[data-message-more]').click();assert.equal(w.document.activeElement.id,'hierarchyBody');
});
test('footer Notes exists on empty jobs and saves authored notes independently of messages',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 const notes=w.document.querySelector('#studioFooterJobTools [data-job-notes]');assert.ok(notes);
 await notes.onclick();const drawer=w.document.getElementById('jobNotesDrawer');assert.match(drawer.textContent,/No notes yet/);
 const form=drawer.querySelector('form');form.elements.body.value='Bring the large backdrop.';
 await form.onsubmit({preventDefault(){}});assert.match(drawer.querySelector('.jd-note-list').textContent,/Bring the large backdrop/);
 assert.equal(fixture.jobNotes.job.length,1);assert.equal(form.elements.body.value,'');
 assert.equal(fixture.calls.some(c=>c.url.endsWith('/messages')&&c.options?.method==='POST'),false);
 await w.openWorkspaceJob('job2');await settle();await w.document.querySelector('[data-job-notes]').onclick();
 assert.match(w.document.querySelector('#jobNotesDrawer').textContent,/No notes yet/);assert.doesNotMatch(w.document.querySelector('#jobNotesDrawer').textContent,/large backdrop/);
});
test('notes retry reuses the note identifier after a lost save response',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();await w.document.querySelector('[data-job-notes]').onclick();
 const original=w.json;let fail=true;
 w.json=async(url,options)=>{const result=await original(url,options);if(url.endsWith('/notes')&&options?.method==='POST'&&fail){fail=false;throw Error('Connection lost');}return result;};
 const form=w.document.querySelector('.jd-note-form');form.elements.body.value='Keep this note.';
 await form.onsubmit({preventDefault(){}});assert.match(form.textContent,/Connection lost/);assert.equal(form.elements.body.value,'Keep this note.');
 await form.onsubmit({preventDefault(){}});assert.equal(fixture.jobNotes.job.length,1);
 const posts=fixture.calls.filter(c=>c.url.endsWith('/notes')&&c.options?.method==='POST');assert.equal(JSON.parse(posts[0].options.body).id,JSON.parse(posts[1].options.body).id);
});
test('footer drawers switch, close with Escape and disappear with task popup on navigation',async t=>{
 const {w}=environment(t);await w.openWorkspaceJob('job');await settle();
 await w.document.querySelector('[data-job-activity]').onclick();await w.document.querySelector('[data-job-notes]').onclick();
 assert.equal(w.document.getElementById('jobActivityDrawer').getAttribute('aria-hidden'),'true');
 const notes=w.document.getElementById('jobNotesDrawer');notes.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));
 assert.equal(notes.getAttribute('aria-hidden'),'true');assert.equal(w.document.activeElement.dataset.jobNotes,'');
 await w.document.querySelector('[data-job-tasks]').onclick();await w.studioNavigate('today');await settle();
 assert.equal(w.document.querySelector('.jd-tasks-dialog'),null);assert.equal(w.document.querySelector('[data-job-notes]'),null);assert.equal(w.document.querySelector('.jd-footer-drawer'),null);
});
test('viewer can read Notes without a save form',async t=>{
 const {w,fixture}=environment(t);fixture.canEdit=false;await w.openWorkspaceJob('job');await settle();await w.document.querySelector('[data-job-notes]').onclick();
 assert.match(w.document.getElementById('jobNotesDrawer').textContent,/No notes yet/);assert.equal(w.document.querySelector('.jd-note-form'),null);
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
 assert.match(w.document.querySelector('.job-detail-title h1').textContent,/Graduation portraits/);
 assert.equal(w.document.querySelector('#jobDetail-subjects').hidden,true);assert.equal(w.document.querySelector('#jobDetail-subjects .person-card'),null);
});
test('compact workflow opens full assignment controls without clipping the page',async t=>{
 const {w}=environment(t);await w.openWorkspaceJob('job');await settle();
 assert.match(w.document.querySelector('[data-job-owner]').textContent,/Owner: studio/);
 await w.document.querySelector('[data-job-workflow]').onclick();
 const dialog=w.document.querySelector('.job-detail-workflow-dialog');assert.ok(dialog.open);assert.ok(dialog.querySelector('[data-wf-session]'));assert.ok(dialog.querySelector('[data-wf-owner]'));
 dialog.close();await settle();assert.equal(w.document.querySelector('.job-detail-workflow-dialog'),null);
});
test('subject View Photos returns to Media and refreshes the subject filter',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 assert.equal(button(w,'subjects'),null);
 const select=w.document.querySelector('[data-job-subject]');select.value='person';await select.onchange();
 assert.equal(w.document.querySelector('[data-collection]').nextElementSibling.matches('[data-job-subject]'),true);
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
 assert.match(w.document.getElementById('studioContextRail').textContent,/Demo \| Commercial and Products/);
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
 release();await opening;
 assert.equal(w.detailPane.hasAttribute('aria-busy'),false);
 assert.ok(w.document.querySelector('#hierarchyCompose'));
 assert.equal(fixture.calls.some(c=>c.url==='/api/jobs'),false);
});
test('a slow earlier job lookup cannot replace the latest selection',async t=>{
 const {w}=environment(t);const original=w.json;let release;const delay=new Promise(r=>release=r);
 w.json=async(url,options)=>{if(url==='/api/jobs/job')await delay;return original(url,options);};
 const first=w.openWorkspaceJob('job');await w.openWorkspaceJob('job2');release();await first;
 assert.match(w.document.querySelector('.job-detail-title h1').textContent,/Graduation portraits/);
});
test('job loading failure keeps Job Details visible and supports Retry',async t=>{
 const {w}=environment(t);const original=w.json;let fail=true;
 w.json=async(url,options)=>{if(url==='/api/jobs/job'&&fail)throw new Error('Job unavailable');return original(url,options);};
 await w.openWorkspaceJob('job');
 assert.match(w.document.querySelector('#studioTodayBrandTitle strong').textContent,/Job Details/);
 assert.match(w.detailPane.querySelector('[role=alert]').textContent,/Job unavailable/);
 assert.equal(w.detailPane.hasAttribute('aria-busy'),false);
 fail=false;await w.detailPane.querySelector('button').onclick();
 assert.ok(w.document.querySelector('#hierarchyCompose'));
});
test('leaving Job Details while loading does not replace the destination page',async t=>{
 const {w}=environment(t);const original=w.json;let release;const delay=new Promise(r=>release=r);
 w.json=async(url,options)=>{if(url==='/api/jobs/job')await delay;return original(url,options);};
 const opening=w.openWorkspaceJob('job');w.document.body.dataset.studioView='today';w.document.getElementById('conversationView').classList.add('hidden');
 release();await opening;
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
 const selected=w.document.querySelector('.jd-photo');selected.click();
 const form=w.document.querySelector('.jd-file-form');form.elements.collection.value='Unclassified';
 await form.onsubmit({preventDefault(){},currentTarget:form});
 const write=fixture.calls.find(c=>c.url.includes('/media/assets/')&&c.options?.method==='PUT');assert.ok(write);
 const body=JSON.parse(write.options.body);assert.equal(body.collection,'Unclassified');assert.deepEqual(body.subjects,['person']);assert.equal(body.status,'Ready');
});
test('Job Details left rail owns actions and respects media edit permissions',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 assert.deepEqual(Array.from(w.document.querySelectorAll('.jd-rail-card h2'),el=>el.textContent),['Navigation','My Workspaces','Actions']);
 assert.equal(w.document.querySelector('[data-job-action=subject]').disabled,false);
 w.document.querySelector('[data-job-action=subject]').click();await settle();
 assert.equal(w.document.querySelector('#jobDetail-subjects').hidden,false);assert.equal(w.document.querySelector('#jobDetail-subjects form').hidden,false);
 fixture.canEdit=false;await w.openWorkspaceJob('job2');await settle();
 assert.equal(w.document.querySelector('[data-job-action=upload]').disabled,true);assert.equal(w.document.querySelector('.jd-file-form button'),null);
});
test('gallery search and list controls operate on the displayed page',async t=>{
 const {w}=environment(t);await w.openWorkspaceJob('job');await settle();
 const search=w.document.querySelector('[data-media-search]');search.value='Photo 10';search.oninput({target:search});
 assert.equal(w.document.querySelectorAll('.jd-photo').length,1);
 w.document.querySelector('[data-list]').click();assert.equal(w.document.querySelector('.jd-contact-sheet').classList.contains('list'),true);
 w.document.querySelector('[data-grid]').click();assert.equal(w.document.querySelector('.jd-contact-sheet').classList.contains('list'),false);
});
test('workflow ribbon reflects current stage, corrections and cancellation',async t=>{
 const {w}=environment(t);const host=w.document.createElement('div');
 for(const [stage,correctionKind,index] of [['Pending',null,0],['Active Production',null,3],['Awaiting Approval',null,4],['Changes Requested','Source',2],['Changes Requested','Production',3]]){
  w.StudioJobDetails.workflow(host,{stage,correctionKind});assert.equal(Array.from(host.children).indexOf(host.querySelector('.current')),index);
 }
 w.StudioJobDetails.workflow(host,{stage:'Cancelled'});assert.equal(host.querySelectorAll('.complete,.current').length,0);
 w.StudioJobDetails.workflow(host,{stage:'Completed'});assert.equal(host.querySelectorAll('.complete').length,6);
});
test('upload selection queues files immediately into the current collection',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 await w.document.querySelector('[data-filter=Selections]').onclick();
 const input=w.document.querySelector('[data-files]');Object.defineProperty(input,'files',{value:[new w.File(['image'],'photo.jpg',{type:'image/jpeg'})]});
 await input.onchange();const upload=fixture.calls.find(c=>c.url.endsWith('/media/uploads'));assert.ok(upload);assert.equal(upload.options.body.get('collection'),'Selections');
 assert.match(w.document.querySelector('[data-media-result]').textContent,/1 files uploaded/);
});
test('late media responses cannot overwrite a newer job or file inspector',async t=>{
 const {w}=environment(t);const original=w.json;let release;const delay=new Promise(r=>release=r);
 w.json=async(url,options)=>{if(url.startsWith('/api/jobs/job/media?'))await delay;return original(url,options);};
 await w.openWorkspaceJob('job');await w.openWorkspaceJob('job2');await settle();release();await settle();
 assert.match(w.document.querySelector('.job-detail-title h1').textContent,/Graduation portraits/);
 assert.match(w.document.querySelector('.jd-selected-preview img').src,/jobs\/job2\/versions/);
});
test('preview failures show a recovery state and use the preview retry API',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 const img=w.document.querySelector('.jd-selected-preview img');img.onerror();
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
test('workflow phases are keyboard buttons and activate the state aware next action',async t=>{
 const {w}=environment(t),host=w.document.createElement('div');w.document.body.append(host);let activated;
 const data={stage:'Needs Photographer Assignment',owner:'owner',ownerAssignment:{assigned_to:'owner',status:'Assigned'},sessions:[]};
 w.StudioJobDetails.workflow(host,data,{id:'job'},{userName:'owner',canManage:true},selector=>activated=selector);
 const current=host.querySelector('button.current');assert.ok(current);assert.equal(current.getAttribute('aria-label'),'Accept ownership');current.click();
 assert.equal(activated,'[data-wf-owner-response="accept-owner"]');
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

