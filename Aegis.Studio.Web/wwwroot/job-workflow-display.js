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
 function shortStageLabel(label){const value=String(label??'').trim(),normalized=value.toLowerCase().replace(/[\u2013\u2014]/g,'-');if(/job\s+template/.test(normalized))return 'Job';if(/work\s+order\s+template/.test(normalized)||/work\s+order/.test(normalized))return 'Work';if(/workflow\s+automation/.test(normalized)||/automation/.test(normalized))return 'Automate';const words=value.split(/[\s/]+/).filter(Boolean);return words.length?words[words.length-1]:'';}
 function icon(state){return state==='Completed'||state==='Skipped'?'✓':state==='InProgress'?'●':state==='Blocked'?'!':'›';}
 function renderDiagram(host,view){
  const graph=view?.graph;if(!host||!graph?.nodes)return;
  if(renderedView.get(host)!==view){delete host.dataset.expanded;renderedView.set(host,view);}
  const states=statusReader(view),nodes=graph.nodes.filter(n=>!['Start','End'].includes(n.kind)).map(n=>({...n,_displayState:states.stageState(n)})).filter(n=>n._displayState!=='Inactive');
  const expandedId=host.dataset.expanded||'',expandedNode=nodes.find(n=>n.id===expandedId&&n.kind==='Subflow'&&Array.isArray(n.subflow?.nodes));
  const items=nodes.map((node,index)=>{
   const label=esc(node.label),shortLabel=esc(shortStageLabel(node.label)),state=node._displayState,klass=state.toLowerCase(),before=index?'<span class="jd-workflow-connector" aria-hidden="true"></span>':'';
   if(node.id===expandedNode?.id)return before+'<div class="jd-workflow-subflow-main '+klass+'" data-expanded-stage="'+esc(node.id)+'" aria-label="Expanded '+label+'"><button type="button" class="jd-workflow-endpoint" data-collapse="'+esc(node.id)+'" data-endpoint="start" aria-expanded="true" aria-label="Start '+label+'"><span class="jd-workflow-stage-icon" aria-hidden="true">›</span><span class="jd-workflow-endpoint-label">Start '+label+'</span></button><span class="jd-workflow-main-dashes" aria-hidden="true"></span><button type="button" class="jd-workflow-endpoint" data-collapse="'+esc(node.id)+'" data-endpoint="end" aria-expanded="true" aria-label="End '+label+'"><span class="jd-workflow-stage-icon" aria-hidden="true">×</span><span class="jd-workflow-endpoint-label">End '+label+'</span></button></div>';
   if(node.kind==='Subflow'&&Array.isArray(node.subflow?.nodes))return before+'<button type="button" class="jd-workflow-stage '+klass+'" data-subflow-stage="'+esc(node.id)+'" aria-expanded="false" title="'+label+' · '+state+'"><span class="jd-workflow-stage-icon" aria-hidden="true">'+icon(state)+'</span><span class="jd-workflow-stage-label">'+shortLabel+'</span></button>';
   return before+'<div class="jd-workflow-stage '+klass+'" role="listitem" aria-label="'+label+' · '+state+'"><span class="jd-workflow-stage-icon" aria-hidden="true">'+icon(state)+'</span><span class="jd-workflow-stage-label">'+shortLabel+'</span></div>';
  }).join('');
  host.innerHTML='<div class="jd-workflow-display" role="list" aria-label="Job workflow">'+items+'</div><div class="jd-workflow-expansion" hidden></div>';
  host.querySelectorAll('[data-subflow-stage]').forEach(button=>button.onclick=()=>{host.dataset.expanded=button.dataset.subflowStage;renderDiagram(host,view);host.querySelector('[data-collapse]')?.focus();});
  host.querySelectorAll('[data-collapse]').forEach(button=>button.onclick=()=>{delete host.dataset.expanded;renderDiagram(host,view);host.querySelectorAll('[data-subflow-stage]').forEach(next=>{if(next.dataset.subflowStage===button.dataset.collapse)next.focus();});});
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

 const progressViews=new WeakMap();
 const drawerTimeoutMs=8000;
 let progressId=0;
 function render(host,view){
  if(!host||!view?.graph?.nodes)return;
  const previous=progressViews.get(host);if(previous)clearTimeout(previous.timer);
  const states=statusReader(view),stages=view.graph.nodes.filter(node=>!['Start','End'].includes(node.kind)).map(node=>({...node,state:states.stageState(node)})).filter(node=>node.state!=='Inactive');
  const completed=stages.filter(node=>node.state==='Completed').length,total=stages.length,percent=total?Math.round(completed*100/total):0;
  const id='job-workflow-drawer-'+(++progressId),label=completed+' of '+total+' stages completed · '+percent+'%';
  host.innerHTML='<section class="jd-workflow-summary" data-open="false"><button type="button" class="jd-workflow-progress-trigger" aria-expanded="false" aria-controls="'+id+'" title="Show workflow diagram"><span class="jd-workflow-progress-heading"><strong>Workflow progress</strong><span>'+percent+'% complete <span aria-hidden="true">⌄</span></span></span><span class="jd-workflow-progress-track" role="progressbar" aria-label="Job workflow completion" aria-valuemin="0" aria-valuemax="100" aria-valuenow="'+percent+'" aria-valuetext="'+esc(label)+'"><span class="jd-workflow-progress-fill" style="width:'+percent+'%"></span></span><small>'+completed+' of '+total+' stages completed</small></button><div id="'+id+'" class="jd-workflow-drawer" role="region" aria-label="Job workflow diagram" aria-hidden="true" inert><div class="jd-workflow-drawer-surface"><button type="button" data-workflow-close aria-label="Close workflow diagram">×</button><div class="jd-workflow-diagram-scroll"><div class="jd-workflow-diagram"></div></div></div></div></section>';
  const summary=host.querySelector('.jd-workflow-summary'),trigger=host.querySelector('.jd-workflow-progress-trigger'),drawer=host.querySelector('.jd-workflow-drawer'),diagram=host.querySelector('.jd-workflow-diagram');
  const state={timer:null};progressViews.set(host,state);
  const current=()=>host.isConnected&&progressViews.get(host)===state;
  const hide=()=>{
   clearTimeout(state.timer);if(!current())return;
   const restore=drawer.contains(document.activeElement);
   summary.dataset.open='false';trigger.setAttribute('aria-expanded','false');drawer.setAttribute('aria-hidden','true');drawer.inert=true;
   if(restore)trigger.focus({preventScroll:true});
  };
  const show=()=>{
   if(!current())return;
   summary.dataset.open='true';trigger.setAttribute('aria-expanded','true');drawer.setAttribute('aria-hidden','false');drawer.inert=false;
   clearTimeout(state.timer);state.timer=setTimeout(hide,drawerTimeoutMs);
   renderDiagram(diagram,view);
  };
  trigger.onclick=show;
  trigger.onkeydown=event=>{if(event.key==='ArrowDown'){event.preventDefault();show();drawer.querySelector('button')?.focus();}};
  summary.onmouseenter=show;
  summary.onmouseleave=hide;
  summary.addEventListener('focusout',()=>queueMicrotask(()=>{if(current()&&!summary.contains(document.activeElement))hide();}));
  summary.addEventListener('keydown',event=>{if(event.key==='Escape'){event.preventDefault();hide();}});
  host.querySelector('[data-workflow-close]').onclick=hide;
  renderDiagram(diagram,view);
 }

 root.StudioJobWorkflowDisplay={render};
})(window);

