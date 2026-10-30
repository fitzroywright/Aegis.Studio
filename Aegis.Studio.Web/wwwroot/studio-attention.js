// Shared attention data is permission-filtered by the existing APIs, independent of page filters.
window.StudioAttention=(()=>{
 const closed=value=>/^(completed|cancelled|closed|archived|published|delivered|skipped|inactive)$/i.test(value||'');
 function project(jobs,tasks,workload={},messages={},now=Date.now(),galleryRequests=[],invoices=[]){
  const entries=new Map(),jobMap=new Map(jobs.map(job=>[String(job.id),job]));
  const add=item=>{const prior=entries.get(item.id);entries.set(item.id,prior?{...prior,context:[prior.context,item.context].filter(Boolean).filter((s,i,a)=>a.indexOf(s)===i).join(' · ')}:item);};
  const quantities=new Map();for(const item of workload.items||[]){const reasons=[];if(Number(item.awaitingQc)>0)reasons.push('Output awaiting quality control');if(Number(item.rework)>0)reasons.push('Output requires rework');if(reasons.length)quantities.set(String(item.activityId),reasons.join(' · '));}
  for(const task of [...tasks,...(workload.activities||[])]){
   const job=jobMap.get(String(task.jobId));if(closed(job?.status)||/^(cancelled|archived)$/i.test(task.status||'')||closed(task.status)&&!quantities.has(String(task.id)))continue;
   const reasons=[];if(quantities.has(String(task.id)))reasons.push(quantities.get(String(task.id)));
   if(task.dueAtUtc&&Date.parse(task.dueAtUtc)<now)reasons.push('Overdue');
   if(task.blockedReason)reasons.push(task.blockedReason);if(!task.assignedTo)reasons.push('Unassigned');
   if(!reasons.length&&task.scheduledAtUtc&&Date.parse(task.scheduledAtUtc)>now)continue;
   reasons.push(task.status||'Open task');
   add({id:'task:'+task.id,jobId:task.jobId,assignedTo:task.assignedTo,dueAtUtc:task.dueAtUtc,title:task.title||task.activityType||'Studio task',context:[job?.title,task.assignedTo,...reasons].filter(Boolean).join(' · ')});
  }
  for(const item of messages.items||[])add({id:'delivery:'+item.kind+':'+item.conversationId,title:item.title||'Message delivery needs attention',context:[item.summary,item.nextStep].filter(Boolean).join(' · '),route:'messages'});
  for(const request of galleryRequests)add({id:'gallery-request:'+request.conversationId,title:'Review gallery request — '+(request.request?.clientName||request.request?.clientEmail||'Client gallery'),context:request.status,route:'frontdesk'});
  for(const invoice of invoices.filter(item=>item.status==='Overdue'))add({id:'invoice:'+invoice.id,title:'Review overdue invoice — '+(invoice.invoiceNumber||'Invoice'),context:[jobMap.get(String(invoice.jobId))?.title,invoice.dueAtUtc].filter(Boolean).join(' · '),jobId:invoice.jobId,route:invoice.jobId?undefined:'finance'});
  return [...entries.values()];
 }
 let pending=null;
 async function load(){
  if(pending)return pending;
  pending=(async()=>{
   const urls=['/api/today/jobs','/api/operations/activities?includeWorkflowDetails=false','/api/production/workload','/api/messages/attention','/api/frontdesk/gallery-access-requests/unresolved','/api/accounts/invoices'];
   const results=await Promise.allSettled(urls.map(async(url,index)=>{let status;try{return await json(url,{onResponse:response=>status=response.status});}catch(error){if(index>1&&status===403)return [2,3].includes(index)?{}:[];throw error;}}).concat(window.StudioNextActions?[StudioNextActions.load()]:[]));
   const value=(i,fallback)=>results[i].status==='fulfilled'?results[i].value:fallback;
   let items=project(value(0,[]),value(1,[]),value(2,{}),value(3,{}),Date.now(),value(4,[]),value(5,[]));
   if(window.StudioNextActions&&results[6]?.status==='fulfilled'){
    const actions=results[6].value,taskIds=new Set(actions.map(a=>a.action.taskId).filter(Boolean).map(id=>'task:'+id));
    items=items.filter(item=>!taskIds.has(item.id));
    items.push(...actions.map(row=>({id:'next:'+row.kind+':'+row.entityId,jobId:row.jobId,title:row.action.title+' — '+row.entityTitle,context:[row.action.reason,row.assignedTo||'Unassigned',StudioNextActions.due(row).label,row.waitingReason?'Waiting: '+row.waitingReason:''].filter(Boolean).join(' · '),assignedTo:row.assignedTo,dueAtUtc:row.dueAtUtc,nextAction:row})));
   }
   return {items,loaded:true,error:results.some(r=>r.status==='rejected')?'Some work could not be loaded. Reopen Needs Attention to retry.':''};
  })();try{return await pending;}finally{pending=null;}
 }
 return {load,project};
})();

