/* Job Details workflow display. Independent from workflow editing. */
(function(root){
 'use strict';
 const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const renderedView=new WeakMap();
 function statusReader(view){
  const evaluation=new Map((view.evaluation?.nodes||[]).map(n=>[n.id,n]));
  const tasks=new Map((view.tasks||[]).map(t=>[t.nodeId,t]));
  const taskState=id=>{
   const evaluated=evaluation.get(id),actual=tasks.get(id)?.status;
   if(actual==='Completed'||evaluated?.status==='Completed')return 'Completed';
   if(actual==='Skipped'||actual==='Cancelled'||evaluated?.status==='Skipped')return 'Skipped';
   if(['InProgress','Partial','ReadyForDelivery'].includes(actual)||evaluated?.status==='InProgress')return 'InProgress';
   if(['Blocked','Failed','ReworkRequired','ReprintRequired','WaitingOnClient'].includes(actual))return 'Blocked';
   if(evaluated?.status==='Inactive')return 'Inactive';
   if(evaluated?.status==='Available')return 'Available';
   if(evaluated?.status==='Blocked')return 'Pending';
   return 'Pending';
  };
  const leafTaskIds=(flow,prefix='')=>(flow?.nodes||[]).flatMap(n=>{
   if(n.kind==='Subflow'&&n.subflow)return leafTaskIds(n.subflow,prefix+n.id+'/');
   return n.kind==='Task'?[prefix+n.id]:[];
  });
  const stageState=node=>{
   if(node.kind!=='Subflow'||!node.subflow)return taskState(node.id);
   const states=leafTaskIds(node.subflow,node.id+'/').map(taskState),active=states.filter(s=>s!=='Inactive');
   if(!active.length)return 'Inactive';
   if(active.every(s=>s==='Completed'||s==='Skipped'))return 'Completed';
   if(active.some(s=>s==='InProgress'))return 'InProgress';
   if(active.some(s=>s==='Blocked'))return 'Blocked';
   if(active.some(s=>s==='Available'))return active.some(s=>s==='Completed'||s==='Skipped')?'InProgress':'Available';
   return 'Pending';
  };
  return {taskState,stageState};
 }
 function icon(state){return state==='Completed'||state==='Skipped'?'✓':state==='InProgress'?'●':state==='Blocked'?'!':'›';}
 const stateLabel=state=>({Completed:'Completed',Skipped:'Skipped',InProgress:'In progress',Available:'Ready',Pending:'Pending',Blocked:'Blocked'})[state]||state;
 function stageIcon(node){
  const name=String(node.task?.activityType||'')+' '+String(node.label||''),svg=paths=>'<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'+paths+'</svg>';
  if(/photograph|shoot|camera/i.test(name))return svg('<path d="M14 4h-4L8 7H4a2 2 0 0 0-2 2v10h20V9a2 2 0 0 0-2-2h-4z"/><circle cx="12" cy="13" r="4"/>');
  if(/print/i.test(name))return svg('<path d="M6 9V3h12v6M6 17H3V9h18v8h-3M6 14h12v7H6zM17 11h1"/>');
  if(/gallery|image|photo|proof/i.test(name))return svg('<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="8" cy="8" r="2"/><path d="m3 17 5-5 4 4 4-6 5 7"/>');
  if(/complete|approv|review|quality|qc/i.test(name))return svg('<circle cx="12" cy="12" r="9"/><path d="m8 12 3 3 5-6"/>');
  if(/service/i.test(name))return svg('<rect x="3" y="7" width="18" height="14" rx="2"/><path d="M8 7V3h8v4M3 12h18m-11 0v3h4v-3"/>');
  if(/order|invoice|receipt/i.test(name))return svg('<path d="M6 3h12v19l-3-2-3 2-3-2-3 2zM9 7h6M9 11h6M9 15h4"/>');
  if(/work|production|edit|retouch/i.test(name))return svg('<path d="m14 6 4 4M4 20l8-8M14 3a6 6 0 0 0-2 9l7 8 2-2-8-7a6 6 0 0 0 8-7l-4 4-3-1z"/>');
  if(/deliver|archive/i.test(name))return svg('<path d="M3 7h18v14H3zM2 3h20v4H2zM9 11h6"/>');
  return svg('<rect x="5" y="4" width="14" height="18" rx="2"/><path d="M9 4V2h6v2M9 10h6M9 15h6"/>');
 }
 function pillContents(node,state){return '<span class="jd-workflow-stage-icon">'+stageIcon(node)+'</span><span class="jd-workflow-stage-label">'+esc(node.label)+'</span>'+(['Completed','Skipped','Blocked'].includes(state)?'<span class="jd-workflow-status" aria-hidden="true">'+icon(state)+'</span>':'');}
 function render(host,view){
  const graph=view?.graph;if(!host||!graph?.nodes)return;
  if(renderedView.get(host)!==view){delete host.dataset.expanded;delete host.dataset.selectedStage;renderedView.set(host,view);}
  const states=statusReader(view),nodes=graph.nodes.filter(n=>!['Start','End'].includes(n.kind)).map(n=>({...n,_displayState:states.stageState(n)})).filter(n=>n._displayState!=='Inactive');
  const expandedId=host.dataset.expanded||'',expandedNode=nodes.find(n=>n.id===expandedId&&n.kind==='Subflow'&&Array.isArray(n.subflow?.nodes));
  const items=nodes.map(node=>{
   const label=esc(node.label),state=node._displayState,klass=state.toLowerCase();
   if(node.id===expandedNode?.id)return '<div class="jd-workflow-subflow-main '+klass+'" data-expanded-stage="'+esc(node.id)+'" aria-label="Expanded '+label+'"><button type="button" class="jd-workflow-endpoint" data-collapse="'+esc(node.id)+'" data-endpoint="start" aria-expanded="true" aria-label="Start '+label+'"><span class="jd-workflow-stage-icon" aria-hidden="true">›</span><span class="jd-workflow-endpoint-label">Start '+label+'</span></button><span class="jd-workflow-main-dashes" aria-hidden="true"></span><button type="button" class="jd-workflow-endpoint" data-collapse="'+esc(node.id)+'" data-endpoint="end" aria-expanded="true" aria-label="End '+label+'"><span class="jd-workflow-stage-icon" aria-hidden="true">×</span><span class="jd-workflow-endpoint-label">End '+label+'</span></button></div>';
   if(node.kind==='Subflow'&&Array.isArray(node.subflow?.nodes))return '<button type="button" class="jd-workflow-stage '+klass+'" data-subflow-stage="'+esc(node.id)+'" aria-expanded="false" aria-label="'+label+' · '+stateLabel(state)+'" title="'+label+' · '+stateLabel(state)+'">'+pillContents(node,state)+'</button>';
   return '<button type="button" class="jd-workflow-stage '+klass+'" data-stage-info="'+esc(node.id)+'" aria-expanded="'+(host.dataset.selectedStage===node.id)+'" aria-label="'+label+' · '+stateLabel(state)+'" title="'+label+' · '+stateLabel(state)+'">'+pillContents(node,state)+'</button>';
  }).join('');
  host.innerHTML='<div class="jd-workflow-display" role="group" aria-label="Job workflow">'+items+'</div><div class="jd-workflow-info" hidden></div><div class="jd-workflow-expansion" hidden></div>';
  host.querySelectorAll('[data-subflow-stage]').forEach(button=>button.onclick=()=>{delete host.dataset.selectedStage;host.dataset.expanded=button.dataset.subflowStage;render(host,view);host.querySelector('[data-collapse]')?.focus();});
  host.querySelectorAll('[data-collapse]').forEach(button=>button.onclick=()=>{delete host.dataset.expanded;render(host,view);host.querySelectorAll('[data-subflow-stage]').forEach(next=>{if(next.dataset.subflowStage===button.dataset.collapse)next.focus();});});
  host.querySelectorAll('[data-stage-info]').forEach(button=>button.onclick=()=>{delete host.dataset.expanded;host.dataset.selectedStage=host.dataset.selectedStage===button.dataset.stageInfo?'':button.dataset.stageInfo;render(host,view);host.querySelectorAll('[data-stage-info]').forEach(next=>{if(next.dataset.stageInfo===button.dataset.stageInfo)next.focus();});});
  const selected=nodes.find(node=>node.id===host.dataset.selectedStage);
  if(selected){const info=host.querySelector('.jd-workflow-info'),reason=view.tasks?.find(task=>task.nodeId===selected.id)?.reason||view.evaluation?.nodes?.find(node=>node.id===selected.id)?.reason;info.hidden=false;info.innerHTML='<strong>'+esc(selected.label)+'</strong><span>'+esc(stateLabel(selected._displayState))+'</span>'+(reason?'<small>'+esc(reason)+'</small>':'');}
  if(expandedNode)drawSubflow(host,expandedNode,states);
 }
 function drawSubflow(host,node,states){
  const expansion=host.querySelector('.jd-workflow-expansion');if(!expansion)return;
  const width=Math.max(240,host.clientWidth||760),hostRect=host.getBoundingClientRect();
  const point=selector=>{const rect=host.querySelector(selector)?.getBoundingClientRect();return rect?.width?{x:rect.left-hostRect.left+rect.width/2,y:rect.top-hostRect.top+rect.height/2}:null;};
  const center=point('[data-expanded-stage]')||{x:width/2,y:36},start=point('[data-endpoint="start"]')||{x:center.x-70,y:center.y},end=point('[data-endpoint="end"]')||{x:center.x+70,y:center.y};
  const sx=start.x,sy=start.y,ex=end.x,ey=end.y,steps=(node.subflow.nodes||[]).filter(n=>!['Start','End'].includes(n.kind));
  const laneLeft=Math.min(sx,ex)+38,laneRight=Math.max(sx,ex)-38,gap=steps.length>1?Math.max(1,(laneRight-laneLeft)/(steps.length-1)):0,stepX=i=>steps.length>1?laneLeft+gap*i:(sx+ex)/2;
  const firstX=steps.length?stepX(0):center.x,lastX=steps.length?stepX(steps.length-1):center.x,rowY=Math.max(103,sy+66),detour=Math.min(54,Math.max(24,(ex-sx)*.28)),left=sx-detour,right=ex+detour;
  const incoming='M'+sx+' '+(sy+12)+' V'+(sy+23)+' C'+sx+' '+(sy+38)+' '+left+' '+(rowY-16)+' '+left+' '+(rowY-16)+' V'+(rowY-12)+' Q'+left+' '+rowY+' '+(left+12)+' '+rowY+' H'+(firstX-10);
  const outgoing='M'+(lastX+10)+' '+rowY+' H'+(right-12)+' Q'+right+' '+rowY+' '+right+' '+(rowY-12)+' V'+(ey+44)+' C'+right+' '+(ey+32)+' '+ex+' '+(ey+34)+' '+ex+' '+(ey+22)+' V'+(ey+12);
  const stepLinks=steps.slice(0,-1).map((_,i)=>'<path class="jd-subflow-route" d="M'+(stepX(i)+10)+' '+rowY+' H'+(stepX(i+1)-10)+'"/>').join('');
  const labels=steps.map((step,i)=>{const state=states.taskState(node.id+'/'+step.id),klass=state.toLowerCase(),x=stepX(i);return '<g class="jd-subflow-step '+klass+'" aria-label="'+esc(step.label)+' · '+state+'" transform="translate('+x+' '+rowY+')"><circle r="10"/><text class="jd-subflow-glyph" x="0" y="4" text-anchor="middle">'+icon(state)+'</text><text class="jd-subflow-label" x="0" y="28" text-anchor="middle">'+esc(step.label)+'</text></g>';}).join('');
  const marker='jd-subflow-arrow-'+String(node.id).replace(/[^a-zA-Z0-9_-]/g,'-');
  expansion.hidden=false;
  expansion.innerHTML='<svg class="jd-subflow-diagram" viewBox="0 0 '+width+' 170" preserveAspectRatio="none" role="group" aria-label="Expanded '+esc(node.label)+'"><defs><marker id="'+marker+'" markerWidth="7" markerHeight="7" refX="6" refY="3.5" orient="auto"><path d="M0 0L7 3.5L0 7Z"/></marker></defs><path class="jd-subflow-route" d="'+incoming+'" marker-end="url(#'+marker+')"/>'+stepLinks+'<path class="jd-subflow-route" d="'+outgoing+'"/>'+labels+'</svg>';
 }
 root.StudioJobWorkflowDisplay={render};
})(window);

