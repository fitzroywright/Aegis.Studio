/* Job Details workflow display. Independent from workflow editing. */
(function(root){
 'use strict';
 const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const renderedView=new WeakMap();
 function render(host,view){
  const graph=view?.graph;if(!host||!graph?.nodes)return;
  if(renderedView.get(host)!==view){delete host.dataset.expanded;renderedView.set(host,view);}
  const evaluation=new Map((view.evaluation?.nodes||[]).map(n=>[n.id,n]));
  const tasks=new Map((view.tasks||[]).map(t=>[t.nodeId,t]));
  const taskState=id=>{
   const evaluated=evaluation.get(id),task=tasks.get(id),actual=task?.status;
   if(actual==='Completed'||evaluated?.status==='Completed')return 'Completed';
   if(['InProgress','Partial','ReadyForDelivery'].includes(actual)||evaluated?.status==='InProgress')return 'InProgress';
   if(['Blocked','Failed','ReworkRequired','ReprintRequired','WaitingOnClient'].includes(actual))return 'Blocked';
   if(actual==='Skipped'||actual==='Cancelled'||evaluated?.status==='Skipped')return 'Skipped';
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
   if(active.some(s=>s==='Available'))return 'Available';
   if(active.some(s=>s==='Blocked'))return 'Blocked';
   return 'Pending';
  };
  const nodes=graph.nodes.filter(n=>!['Start','End'].includes(n.kind)).map(n=>({...n,_displayState:stageState(n)})).filter(n=>n._displayState!=='Inactive');
  const expandedId=host.dataset.expanded||'';
  const expandedNode=nodes.find(n=>n.id===expandedId&&n.kind==='Subflow'&&Array.isArray(n.subflow?.nodes));
  const glyph=state=>state==='Completed'||state==='Skipped'?'✓':state==='InProgress'?'●':state==='Blocked'?'!':'›';
  const items=nodes.map((node,index)=>{
   const label=esc(node.label),state=node._displayState,klass=state.toLowerCase();
   const before=index?'<span class="jd-workflow-connector" aria-hidden="true"></span>':'';
   if(node.id===expandedNode?.id)return before+'<div class="jd-workflow-subflow-main '+klass+'" data-expanded-stage="'+esc(node.id)+'" aria-label="Expanded '+label+'"><button type="button" class="jd-workflow-endpoint" data-collapse="'+esc(node.id)+'" data-endpoint="start" aria-expanded="true" aria-label="Start '+label+'"><span class="jd-workflow-stage-icon" aria-hidden="true">›</span><span class="jd-workflow-endpoint-label">Start '+label+'</span></button><span class="jd-workflow-main-dashes" aria-hidden="true"></span><button type="button" class="jd-workflow-endpoint" data-collapse="'+esc(node.id)+'" data-endpoint="end" aria-expanded="true" aria-label="End '+label+'"><span class="jd-workflow-stage-icon" aria-hidden="true">×</span><span class="jd-workflow-endpoint-label">End '+label+'</span></button></div>';
   if(node.kind==='Subflow'&&Array.isArray(node.subflow?.nodes))return before+'<button type="button" class="jd-workflow-stage '+klass+'" data-subflow-stage="'+esc(node.id)+'" aria-expanded="false" title="'+label+' · '+state+'"><span class="jd-workflow-stage-icon" aria-hidden="true">'+glyph(state)+'</span><span class="jd-workflow-stage-label">'+label+'</span></button>';
   return before+'<div class="jd-workflow-stage '+klass+'" role="listitem" aria-label="'+label+' · '+state+'"><span class="jd-workflow-stage-icon" aria-hidden="true">'+glyph(state)+'</span><span class="jd-workflow-stage-label">'+label+'</span></div>';
  }).join('');
  host.innerHTML='<div class="jd-workflow-display" role="list" aria-label="Job workflow">'+items+'</div><div class="jd-workflow-expansion" hidden></div>';
  host.querySelectorAll('[data-subflow-stage]').forEach(button=>button.onclick=()=>{host.dataset.expanded=button.dataset.subflowStage;render(host,view);});
  host.querySelectorAll('[data-collapse]').forEach(button=>button.onclick=()=>{delete host.dataset.expanded;render(host,view);});
  if(expandedNode)drawSubflow(host,view,expandedNode);
 }
 function drawSubflow(host,view,node){
  const expansion=host.querySelector('.jd-workflow-expansion');if(!expansion)return;
  const width=Math.max(240,host.clientWidth||760),hostRect=host.getBoundingClientRect();
  const centerOf=selector=>{const rect=host.querySelector(selector)?.getBoundingClientRect();return rect?.width?rect.left-hostRect.left+rect.width/2:null;};
  const center=centerOf('[data-expanded-stage]')??width/2;
  const sx=centerOf('[data-endpoint="start"]')??center-70;
  const ex=centerOf('[data-endpoint="end"]')??center+70;
  const evaluation=new Map((view.evaluation?.nodes||[]).map(n=>[n.id,n]));
  const tasks=new Map((view.tasks||[]).map(t=>[t.nodeId,t]));
  const steps=(node.subflow.nodes||[]).filter(n=>!['Start','End'].includes(n.kind));
  const laneLeft=Math.min(sx,ex)+38,laneRight=Math.max(sx,ex)-38;
  const gap=steps.length>1?Math.max(1,(laneRight-laneLeft)/(steps.length-1)):0;
  const stepX=index=>steps.length>1?laneLeft+gap*index:(sx+ex)/2;
  const firstX=steps.length?stepX(0):center,lastX=steps.length?stepX(steps.length-1):center,rowY=101;
  const detour=Math.min(54,Math.max(24,(ex-sx)*.28)),left=sx-detour,right=ex+detour;
  const incoming='M'+sx+' 24 V39 C'+sx+' 53 '+left+' 51 '+left+' 65 V84 C'+left+' 95 '+(left+6)+' '+rowY+' '+(left+18)+' '+rowY+' H'+(firstX-10);
  const outgoing='M'+(lastX+10)+' '+rowY+' H'+(right-18)+' C'+(right-6)+' '+rowY+' '+right+' 95 '+right+' 84 V65 C'+right+' 51 '+ex+' 53 '+ex+' 39 V24';
  const stageLinks=steps.slice(0,-1).map((_,i)=>'<path class="jd-subflow-route" d="M'+(stepX(i)+10)+' '+rowY+' H'+(stepX(i+1)-10)+'"/>').join('');
  const statusFor=id=>{
   const evaluated=evaluation.get(id),task=tasks.get(id),actual=task?.status;
   if(actual==='Completed'||evaluated?.status==='Completed')return 'Completed';
   if(['InProgress','Partial','ReadyForDelivery'].includes(actual)||evaluated?.status==='InProgress')return 'InProgress';
   if(['Blocked','Failed','ReworkRequired','ReprintRequired','WaitingOnClient'].includes(actual))return 'Blocked';
   if(actual==='Skipped'||actual==='Cancelled'||evaluated?.status==='Skipped')return 'Skipped';
   if(evaluated?.status==='Inactive')return 'Inactive';
   if(evaluated?.status==='Available')return 'Available';
   return 'Pending';
  };
  const stageMarkup=steps.map((step,index)=>{
   const state=statusFor(node.id+'/'+step.id),klass=state.toLowerCase(),icon=state==='Completed'||state==='Skipped'?'✓':state==='InProgress'?'●':state==='Blocked'?'!':'›',x=stepX(index);
   return '<g class="jd-subflow-step '+klass+'" aria-label="'+esc(step.label)+' · '+state+'" transform="translate('+x+' '+rowY+')"><circle r="10"/><text class="jd-subflow-glyph" x="0" y="4" text-anchor="middle">'+icon+'</text><text class="jd-subflow-label" x="0" y="28" text-anchor="middle">'+esc(step.label)+'</text></g>';
  }).join('');
  const marker='jd-subflow-arrow-'+String(node.id).replace(/[^a-zA-Z0-9_-]/g,'-');
  expansion.hidden=false;
  expansion.innerHTML='<svg class="jd-subflow-diagram" viewBox="0 0 '+width+' 170" preserveAspectRatio="none" role="group" aria-label="Expanded '+esc(node.label)+'"><defs><marker id="'+marker+'" markerWidth="7" markerHeight="7" refX="6" refY="3.5" orient="auto"><path d="M0 0L7 3.5L0 7Z"/></marker></defs><path class="jd-subflow-route" d="'+incoming+'" marker-end="url(#'+marker+')"/>'+stageLinks+'<path class="jd-subflow-route" d="'+outgoing+'"/>'+stageMarkup+'</svg>';
 }
 root.StudioJobWorkflowDisplay={render};
})(window);
