function jobHandoffSummary(job,shoots,tasks,assets){
    const open=tasks.filter(t=>!['Completed','Cancelled'].includes(t.status));
    const blocked=open.filter(t=>t.status==='Blocked');
    const finished=shoots.filter(s=>s.checkedOutAtUtc);
    let next=job.status==='Completed'?'Completed':job.status==='Ready for Client'?'FrontDesk delivery follow-up':blocked.length?'Resolve blocked tasks':!shoots.length?'Schedule the shoot':finished.length===shoots.length?'Prepare gallery and review handoff':'Complete the scheduled shoot';
    return {next,shootCount:shoots.length,finishedCount:finished.length,openTasks:open.length,blockedTasks:blocked.length,assets:assets===null?null:assets.reduce((n,x)=>n+Number(x.assetCount??1),0)};
}
async function renderJobHandoff(job,host,current){
    const [shoots,tasks,assets,access]=await Promise.all([json(`/api/operations/shoots?jobId=${job.id}`),json(`/api/operations/activities?jobId=${job.id}`),json(`/api/production/media-summary?jobId=${job.id}`).catch(()=>null),json(`/api/jobs/${job.id}/access`)]);
    if(!current())return;
    const summary=jobHandoffSummary(job,shoots,tasks,assets),permissions=access.permissions||access;
    host.innerHTML=`<strong>Next: ${esc(summary.next)}</strong><span>${summary.finishedCount}/${summary.shootCount} shoots finished · ${summary.openTasks} open tasks${summary.blockedTasks?' · '+summary.blockedTasks+' blocked':''}${summary.assets===null?'':' · '+summary.assets+' gallery assets'}</span>`;
    const button=host.querySelector('[data-schedule-job]');if(button)button.onclick=()=>openJobShootScheduler(job).catch(error=>alert(error.message));
}
async function openJobShootScheduler(job){ return openEmbeddedShootSchedule(job.id); }
