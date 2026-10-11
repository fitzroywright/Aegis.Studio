const {test}=require('node:test');
const assert=require('node:assert/strict');
const {environment}=require('./job-detail-fixture.cjs');
const settle=()=>new Promise(resolve=>setTimeout(resolve,20));
test('main flow stays outside the drawer with status in its original position and actions on the right',async t=>{
 const {w}=environment(t);await w.openWorkspaceJob('job');await settle();
 const header=w.document.querySelector('.job-detail-heading'),summary=header.querySelector('.jd-flow-summary'),drawer=header.querySelector('.jd-flow-drawer'),details=header.querySelector('[data-toggle-flow-details]'),status=header.querySelector('.jd-status-pill');
 assert.equal(summary.children.length,6);assert.equal(drawer.contains(summary),false);
 assert.equal(status.closest('.jd-job-status').previousElementSibling.className,'job-detail-title');
 assert.equal(header.querySelector('[data-job-workflow]').closest('.jd-flow-overview').firstElementChild,summary);
 assert.equal(drawer.hasAttribute('inert'),true);assert.equal(drawer.querySelector('.jd-flow-toolbar'),null);assert.equal(drawer.querySelector('[data-flow-stage="Pending"]').style.top,'0px');
 details.click();assert.equal(status.getAttribute('aria-expanded'),'true');assert.equal(drawer.hasAttribute('inert'),false);
 const stage=drawer.querySelector('[aria-current="step"]');stage.focus();stage.dispatchEvent(new w.KeyboardEvent('keydown',{key:'Escape',bubbles:true}));
 assert.equal(drawer.hasAttribute('inert'),true);assert.equal(w.document.activeElement,details);assert.equal(summary.children.length,6);
 status.click();header.querySelector('[data-close-job-flow]').click();assert.equal(w.document.activeElement,status);assert.equal(summary.children.length,6);
});
test('skipped history gates and their parent phases are checked, but the active shoot is not',async t=>{
 const {w}=environment(t);await w.openWorkspaceJob('job');await settle();
 const header=w.document.querySelector('.job-detail-heading'),track=header.querySelector('.jd-workflow-track'),summary=header.querySelector('.jd-flow-summary');
 const session={crew_finalized:true,crew:[{user_name:'photo'}],shoot:{status:'In Progress'}};
 const draw=data=>w.StudioJobDetails.workflow(track,data,{jobType:'Photography'},{},null,summary);
 const tick=label=>[...track.querySelectorAll('[data-flow-stage]')].find(b=>b.dataset.flowStage===label).querySelector('.jd-stage-light').textContent;
 draw({stage:'Photography In Progress',history:[],sessions:[session]});
 for(const label of ['Pending','Needs Assignment','Needs Photographer Assignment','Photographer Assigned','Awaiting Scheduling','Scheduled'])assert.equal(tick(label),'✓',label);
 for(const label of ['Booking','Assignment'])assert.equal(summary.querySelector(`[data-flow-phase="${label}"] .jd-stage-light`).textContent,'✓');
 assert.ok(summary.querySelector('[data-flow-phase="Shoot"]').classList.contains('current'));assert.equal(tick('Photography In Progress'),'');assert.equal(tick('Shoot Complete'),'');
 draw({stage:'Completed',history:[],sessions:[{...session,shoot:{status:'Completed',checked_out_at_utc:'2026-10-11T12:00:00Z'}}]});
 for(const phase of summary.children)assert.equal(phase.querySelector('.jd-stage-light').textContent,'✓',phase.dataset.flowPhase);
 for(const button of track.querySelectorAll('.jd-stage:not([data-flow-stage="Changes Requested"]):not([data-flow-stage="Cancelled"]):not([data-flow-stage="Blocked"]):not([data-flow-stage="Reshoot Required"])'))assert.equal(button.querySelector('.jd-stage-light').textContent,'✓',button.dataset.flowStage);
});
test('corrections, rollback and optional photography do not create false phase checks',async t=>{
 const {w}=environment(t);await w.openWorkspaceJob('job');await settle();
 const header=w.document.querySelector('.job-detail-heading'),track=header.querySelector('.jd-workflow-track'),summary=header.querySelector('.jd-flow-summary');
 w.StudioJobDetails.workflow(track,{stage:'Changes Requested',correctionKind:'Production',history:[],sessions:[]},{},{},null,summary);
 assert.ok(summary.querySelector('[data-flow-phase="Production"]').classList.contains('exception'));assert.equal(summary.querySelector('[data-flow-phase="Delivery"] .jd-stage-light').textContent,'');
 w.StudioJobDetails.workflow(track,{stage:'Active Production',history:[{new_stage:'Ready for Delivery',exited_at_utc:'now'},{new_stage:'Active Production'}],sessions:[]},{jobType:'Print'},{},null,summary);
 assert.equal(summary.querySelector('[data-flow-phase="Shoot"] .jd-stage-light').textContent,'–');assert.equal(summary.querySelector('[data-flow-phase="Delivery"] .jd-stage-light').textContent,'');
});
