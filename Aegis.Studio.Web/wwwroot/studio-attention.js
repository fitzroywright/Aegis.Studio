// Shared attention data is permission-filtered by the existing APIs, independent of page filters.
window.StudioAttention=(()=>{
 const closed=value=>/^(completed|cancelled|closed|archived|published|delivered|skipped|inactive)$/i.test(value||'');
 function project(jobs,tasks,workload={},requests=[],messages={},now=Date.now(),enquiries=[],galleryRequests=[],invoices=[]){
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
   add({id:'task:'+task.id,jobId:task.jobId,title:task.title||task.activityType||'Studio task',context:[job?.title,task.assignedTo,...reasons].filter(Boolean).join(' · ')});
  }
  const pendingBookingJobs=new Set(requests.filter(request=>!/confirmed|cancelled|completed/i.test(request.status||'')).map(request=>String(request.jobId)));
  for(const job of jobs){if(/pending|awaiting deposit/i.test(job.status||'')&&pendingBookingJobs.has(String(job.id)))continue;if(closed(job.status))continue;if(/pending|confirmed|needs|awaiting|blocked|exception|changes requested|rework/i.test(job.status||''))add({id:'job:'+job.id,jobId:job.id,title:(/pending/i.test(job.status)?'Review booking':/needs.*photographer/i.test(job.status)?'Assign photographer':/confirmed|needs.*owner|needs assignment/i.test(job.status)?'Assign job owner':/scheduling/i.test(job.status)?'Schedule session':/upload/i.test(job.status)?'Upload photographs':/production review/i.test(job.status)?'Review uploaded photographs':/approval/i.test(job.status)?'Review and approve production':/changes|rework/i.test(job.status)?'Resolve requested changes':'Review job')+' — '+(job.title||'Job'),context:[job.clientName,job.status,job.blockedReason].filter(Boolean).join(' · ')});}
  for(const request of requests){if(/confirmed|cancelled|completed/i.test(request.status||''))continue;add({id:'booking:'+request.id,jobId:request.jobId,title:'Review booking — '+(request.request?.title||'Booking'),context:[request.request?.clientName,request.status,'Front Desk review'].filter(Boolean).join(' · '),route:'frontdesk'});}
  for(const item of messages.items||[])add({id:'delivery:'+item.kind+':'+item.conversationId,title:item.title||'Message delivery needs attention',context:[item.summary,item.nextStep].filter(Boolean).join(' · '),route:'messages'});
  for(const enquiry of enquiries)add({id:'enquiry:'+enquiry.id,title:'Review inquiry — '+(enquiry.subject||enquiry.contactName||'Client inquiry'),context:[enquiry.contactName,enquiry.contactAddress,enquiry.channel].filter(Boolean).join(' · '),route:'frontdesk'});
  for(const request of galleryRequests)add({id:'gallery-request:'+request.conversationId,title:'Review gallery request — '+(request.request?.clientName||request.request?.clientEmail||'Client gallery'),context:request.status,route:'frontdesk'});
  for(const invoice of invoices.filter(item=>item.status==='Overdue'))add({id:'invoice:'+invoice.id,title:'Review overdue invoice — '+(invoice.invoiceNumber||'Invoice'),context:[jobMap.get(String(invoice.jobId))?.title,invoice.dueAtUtc].filter(Boolean).join(' · '),jobId:invoice.jobId,route:invoice.jobId?undefined:'finance'});
  return [...entries.values()];
 }
 let pending=null;
 async function load(){
  if(pending)return pending;
  pending=(async()=>{
   const urls=['/api/today/jobs','/api/operations/activities?includeWorkflowDetails=false','/api/production/workload','/api/frontdesk/booking-requests','/api/messages/attention','/api/frontdesk/enquiries','/api/frontdesk/gallery-access-requests/unresolved','/api/accounts/invoices'];
   const results=await Promise.allSettled(urls.map(async(url,index)=>{let status;try{return await json(url,{onResponse:response=>status=response.status});}catch(error){if(index>1&&status===403)return [2,4].includes(index)?{}:[];throw error;}}));
   const value=(i,fallback)=>results[i].status==='fulfilled'?results[i].value:fallback;
   const items=project(value(0,[]),value(1,[]),value(2,{}),value(3,[]),value(4,{}),Date.now(),value(5,[]),value(6,[]),value(7,[]));
   return {items,loaded:true,error:results.some(r=>r.status==='rejected')?'Some work could not be loaded. Reopen Needs Attention to retry.':''};
  })();try{return await pending;}finally{pending=null;}
 }
 return {load,project};
})();
