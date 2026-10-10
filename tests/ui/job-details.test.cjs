const {test}=require('node:test');
const assert=require('node:assert/strict');
const {environment}=require('./job-detail-fixture.cjs');
const settle=()=>new Promise(r=>setTimeout(r,20));
const button=(w,name)=>w.document.querySelector('[data-job-scroll="'+name+'"]');
test('Today job opens with working reply and Subjects controls in their final containers',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();
 assert.equal(fixture.alerts.length,0);assert.ok(w.document.querySelector('.job-detail-page'));
 assert.equal(typeof w.document.querySelector('#hierarchyCompose').onsubmit,'function');
 assert.match(w.document.querySelector('#studioTodayBrandTitle strong').textContent,/Job Details/);
 assert.equal(w.document.querySelector('#studioFooterRate').textContent,'1 Job');
 await button(w,'subjects').onclick();
 const subjects=w.document.querySelector('#jobDetail-subjects');assert.equal(subjects.hidden,false);assert.match(subjects.textContent,/Morgan/);
 const add=subjects.querySelector('[data-job-add-subject]');assert.equal(add.hidden,false);add.click();assert.equal(subjects.querySelector('form').hidden,false);
 assert.equal(w.document.querySelector('#jobDetail-media').hidden,true);
 const form=w.document.querySelector('#hierarchyCompose');form.querySelector('textarea').value='Confirmed';
 await form.onsubmit({preventDefault(){},currentTarget:form});
 assert.ok(fixture.calls.some(c=>c.url==='/api/conversations/conversation/messages'&&JSON.parse(c.options.body).body==='Confirmed'));
});
test('Details, Tasks and Activity render their own visible panels',async t=>{
 const {w}=environment(t);await w.openWorkspaceJob('job');await settle();
 for(const [name,text] of [['overview','Kingston Studio'],['tasks','Retouch portraits'],['activity','Front Desk booking']]){
  await button(w,name).onclick();const panel=w.document.getElementById('jobDetail-'+name);assert.equal(panel.hidden,false);assert.match(panel.textContent,new RegExp(text));
  assert.equal(w.document.querySelectorAll('.job-detail-center > section:not([hidden])').length,1);
 }
 await button(w,'messages').onclick();assert.equal(w.document.activeElement.id,'hierarchyBody');
});
test('Subjects load errors stay visible and Retry restores the panel',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');fixture.failSubjects=true;
 await button(w,'subjects').onclick();const panel=w.document.querySelector('#jobDetail-subjects');assert.match(panel.querySelector('[role=alert]').textContent,/Subjects unavailable/);
 fixture.failSubjects=false;await panel.querySelector('.job-subjects-content > button').onclick();assert.match(panel.textContent,/Morgan/);
 assert.equal(fixture.alerts.length,0);
});
test('viewer can inspect Subjects without an Add Subject action',async t=>{
 const {w,fixture}=environment(t);fixture.canEdit=false;await w.openWorkspaceJob('job');await button(w,'subjects').onclick();
 assert.equal(w.document.querySelector('[data-job-add-subject]').hidden,true);assert.equal(w.document.querySelector('#addJobSubject'),null);
});
test('stale Subjects response cannot overwrite a subsequently opened job',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');let release;fixture.delaySubjects=new Promise(r=>release=r);
 const pending=button(w,'subjects').onclick();await w.openWorkspaceJob('job2');release();await pending;await settle();
 assert.match(w.document.querySelector('.job-detail-title h1').textContent,/Graduation portraits/);
 assert.equal(w.document.querySelector('#jobDetail-subjects').hidden,true);assert.equal(w.document.querySelector('#jobDetail-subjects .person-card'),null);
});
test('compact workflow opens full assignment controls without clipping the page',async t=>{
 const {w}=environment(t);await w.openWorkspaceJob('job');await settle();
 assert.match(w.document.querySelector('.job-detail-flow').textContent,/Job Owner: studio/);
 await w.document.querySelector('[data-job-workflow]').onclick();
 const dialog=w.document.querySelector('.job-detail-workflow-dialog');assert.ok(dialog.open);assert.ok(dialog.querySelector('[data-wf-session]'));assert.ok(dialog.querySelector('[data-wf-owner]'));
 dialog.close();await settle();assert.equal(w.document.querySelector('.job-detail-workflow-dialog'),null);
});
test('subject View Photos returns to Media and refreshes the subject filter',async t=>{
 const {w,fixture}=environment(t);await w.openWorkspaceJob('job');await settle();await button(w,'subjects').onclick();
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
