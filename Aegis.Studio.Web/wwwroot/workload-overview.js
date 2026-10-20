/* Shared read projection and navigation. No workflow commands or quantity writes. */
(function(root){
 'use strict';
 const closed=new Set(['Completed','Cancelled','Archived','Delivered','Published','Closed']),done=a=>['Completed','Cancelled','Archived'].includes(a.status),n=x=>Number(x)||0;
 function model(data,jobs=[],now=Date.now()){
  const jobMap=new Map(jobs.map(j=>[j.id,j])),eligibility=new Map((data.eligibility||[]).map(e=>[e.activityId,e]));
  const activities=(data.activities||[]).map(a=>({...a,eligibility:eligibility.get(a.id)?.status,blockedReason:a.blockedReason||eligibility.get(a.id)?.reason}));
  const active=a=>!done(a)&&!['Inactive','Skipped'].includes(a.eligibility)&&!closed.has(jobMap.get(a.jobId)?.status);
  const gate=a=>['Pending','Needs Assignment','Needs Photographer Assignment','Photographer Assigned','Awaiting Scheduling','Awaiting Production Review','Awaiting Approval'].includes(jobMap.get(a.jobId)?.status)&&['Edit','Retouch','Print','QC','Delivery','Archive','Other'].includes(a.activityType);
  const waiting=a=>active(a)&&(gate(a)||(!!a.blockedReason||['Blocked','WaitingOnClient','Failed','ReworkRequired','ReprintRequired'].includes(a.eligibility||a.status)||(!a.eligibility&&!['Active Production','Changes Requested'].includes(jobMap.get(a.jobId)?.status))));
  const overdue=a=>active(a)&&Number.isFinite(Date.parse(a.dueAtUtc))&&Date.parse(a.dueAtUtc)<now;
  const bucket=a=>!active(a)?'complete':waiting(a)?'waiting':['InProgress','Partial'].includes(a.eligibility||a.status)?'progress':a.eligibility==='Available'||['Active Production','Changes Requested'].includes(jobMap.get(a.jobId)?.status)?'ready':'unknown';
  const itemMap=new Map();for(const i of data.items||[])itemMap.set(i.activityId,[...(itemMap.get(i.activityId)||[]),i]);
  const matches=(a,filter)=>!filter||filter==='all'||(filter==='refunds'?(itemMap.get(a.id)||[]).some(i=>n(i.refundedProduced)>0):filter==='qc'?(itemMap.get(a.id)||[]).some(i=>i.kind==='Physical outputs'&&n(i.awaitingQc)>0):filter==='item-rework'?(itemMap.get(a.id)||[]).some(i=>i.kind==='Physical outputs'&&n(i.rework)>0):filter==='overdue'?overdue(a):filter==='unassigned'?active(a)&&!a.assignedTo:filter==='handoffs'?active(a)&&!!a.handoffTo:filter==='rework'?active(a)&&/Rework|Reprint|Failed/.test(a.status):filter==='unknown'?active(a)&&!Number.isFinite(Date.parse(a.dueAtUtc)):bucket(a)===filter);
  function summary(selected){
   const ids=new Set(selected.map(a=>a.id)),selectedMap=new Map(selected.map(a=>[a.id,a]));
   const physical={ready:0,progress:0,waiting:0,unknown:0,awaitingQc:0,rework:0,refundedProduced:0},other={quantity:0},images={records:0};
   for(const item of data.items||[]){const a=selectedMap.get(item.activityId);if(!a)continue;
    if(item.kind==='Physical outputs'){const b=bucket(a);physical.refundedProduced+=n(item.refundedProduced);if(b!=='complete'){if(b in physical)physical[b]+=n(item.remaining);}if(!closed.has(jobMap.get(a.jobId)?.status)&&a.status!=='Cancelled'&&a.status!=='Archived'){physical.awaitingQc+=n(item.awaitingQc);physical.rework+=n(item.rework);}}
    else if(item.kind==='Approved images/items')images.records+=n(item.itemRecords);else other.quantity+=n(item.quantity);
   }
   const orders=(data.orders||[]).filter(o=>o.activityIds?.some(id=>ids.has(id))),units=orders.reduce((sum,o)=>sum+n(o.quantity),0);
   const missing=selected.filter(a=>active(a)&&!(data.items||[]).some(i=>i.activityId===a.id)&&!orders.some(o=>o.activityIds?.includes(a.id))).length;
   const restricted=new Set(selected.filter(a=>!(data.productionJobIds||[]).includes(a.jobId)).map(a=>a.jobId)).size;
   return {physical,other,images,restricted,orders:orders.length,orderedUnits:units,partialUnknown:orders.filter(o=>n(o.quantity)>0&&(o.activityIds||[]).some(id=>{const a=activities.find(a=>a.id===id);return a&&active(a);})).length,missing,jobs:new Set(selected.map(a=>a.jobId)).size,activities:selected.length};
  }
  return {activities,active,waiting,overdue,bucket,matches,summary};
 }
 function cards(model,selected,esc,section='all'){
  const summary=model.summary(selected),p=summary.physical;
  const row=(key,value,label)=>({value:Number(value)||0,html:`<button type="button" class="workload-indicator" data-workload-filter="${key}"><strong>${esc(value)}</strong><span>${label}</span></button>`});
  const overview=[
   row('all',summary.jobs,'Jobs'),
   row('all',summary.activities,'Activities'),
   row('ready',selected.filter(a=>model.bucket(a)==='ready').length,'Ready activities'),
   row('ready',p.ready,'Physical units awaiting production'),
   row('progress',p.progress,'Physical units in progress'),
   row('all',summary.images.records,'Approved image/item records'),
   row('all',summary.other.quantity,'Other purchased output units'),
   row('all',summary.orderedUnits,'Catalog ordered units across '+summary.orders+' work orders')
  ];
  const attention=[
   row('waiting',selected.filter(model.waiting).length,'Waiting activities'),
   row('overdue',selected.filter(model.overdue).length,'Overdue activities'),
   row('rework',selected.filter(a=>/Rework|Reprint|Failed/.test(a.status)).length,'Rework activities'),
   row('waiting',p.waiting,'Physical units waiting'),
   row('unknown',p.unknown,'Physical units with unknown readiness'),
   row('qc',p.awaitingQc,'Produced units awaiting QC'),
   row('item-rework',p.rework,'Physical units requiring rework'),
   row('refunds',p.refundedProduced,'Produced units refunded')
  ];
  const notes=[
   summary.partialUnknown?summary.partialUnknown+' work orders have unknown partial output progress.':'',
   summary.missing?summary.missing+' open activities have no linked quantity records.':'',
   summary.restricted?summary.restricted+' jobs have unavailable item quantities for this account.':''
  ].filter(Boolean);
  const entries=section==='overview'?overview:section==='attention'?attention.filter(x=>x.value>0):[...overview,...attention];
  const empty=section==='attention'&&!entries.length&&!notes.length?'<p>No production exceptions in this view.</p>':'';
  return `<section class="studio-workload-summary" aria-label="${section==='attention'?'Production items needing attention':'Workload for current filters'}"><div class="workload-indicators">${entries.map(x=>x.html).join('')}${empty}</div>${section==='attention'&&notes.length?'<div class="workload-attention-notes">'+notes.map(note=>'<p>'+esc(note)+'</p>').join('')+'</div>':''}</section>`;
 }
 function production(filter,workspace='',assignee='',stage='',jobIds=null){
  root.studioWorkloadSelection={filter:filter||'',workspace,assignee,stage,jobIds};commercialQueueOffset=0;
  productionWorkloadJobIds=jobIds;productionQueueScope='all';productionWorkloadFilter=filter||'';productionWorkspaceFilter=workspace;productionAssigneeFilter=assignee;productionStageFilter=stage;productionDueFilter='';productionSearch='';productionPriority='';return root.studioNavigate('production');
 }
 root.StudioWorkload={model,cards,production};
})(window);
