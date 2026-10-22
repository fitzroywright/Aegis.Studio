/* Job Details workflow display. Independent from the workflow editor. */
(function(root){
 'use strict';
 const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const renderedView=new WeakMap();
 function render(host,view,onSelect){
  const graph=view?.graph;if(!host||!graph?.nodes)return;
  if(renderedView.get(host)!==view){delete host.dataset.expanded;renderedView.set(host,view);}
  const nodes=graph.nodes.filter(n=>!['Start','End'].includes(n.kind)),states=new Map((view.evaluation?.nodes||[]).map(n=>[n.id,n.status])),active=host.dataset.expanded||'',expandedNode=nodes.find(n=>n.id===active&&n.kind==='Subflow'&&Array.isArray(n.subflow?.nodes));
  const items=nodes.map((n,i)=>{
   const state=states.get(n.id)||'Definition',klass=state.toLowerCase().replace(/[^a-z0-9_-]/g,''),label=esc(n.label);
   const before=i?'<span class="jd-workflow-connector" aria-hidden="true"></span>':'';
   if(n.id===expandedNode?.id)return before+'<div class="jd-workflow-subflow-main" data-expanded-stage="'+esc(n.id)+'" aria-label="Expanded '+label+'"><button type="button" class="jd-workflow-endpoint" data-collapse="'+esc(n.id)+'" aria-expanded="true" aria-label="Start '+label+'"><span class="jd-workflow-stage-icon" aria-hidden="true">›</span><span class="jd-workflow-endpoint-label">Start '+label+'</span></button><span class="jd-workflow-main-dashes" aria-hidden="true"></span><button type="button" class="jd-workflow-endpoint" data-collapse="'+esc(n.id)+'" aria-expanded="true" aria-label="End '+label+'"><span class="jd-workflow-stage-icon" aria-hidden="true">×</span><span class="jd-workflow-endpoint-label">End '+label+'</span></button></div>';
   return before+'<button type="button" class="jd-workflow-stage '+klass+'" role="listitem" data-stage="'+esc(n.id)+'" aria-expanded="false" title="'+label+' · '+esc(state)+'"><span class="jd-workflow-stage-icon" aria-hidden="true">'+(state==='Completed'?'✓':state==='InProgress'?'●':state==='Blocked'?'!':'›')+'</span><span class="jd-workflow-stage-label">'+label+'</span></button>';
  }).join('');
  host.innerHTML='<div class="jd-workflow-display" role="list" aria-label="Job workflow">'+items+'</div><div class="jd-workflow-expansion" hidden></div>';
  host.querySelectorAll('[data-stage]').forEach(button=>button.onclick=()=>{
   const id=button.dataset.stage,node=nodes.find(n=>n.id===id),expandable=node?.kind==='Subflow'&&Array.isArray(node.subflow?.nodes);
   if(expandable){host.dataset.expanded=id;render(host,view,onSelect);return;}
   delete host.dataset.expanded;render(host,view,onSelect);onSelect?.(id);
  });
  host.querySelectorAll('[data-collapse]').forEach(button=>button.onclick=()=>{delete host.dataset.expanded;render(host,view,onSelect);});
  if(expandedNode)drawSubflow(host,view,expandedNode,onSelect);
 }
 function drawSubflow(host,view,node,onSelect){
  const expansion=host.querySelector('.jd-workflow-expansion');if(!expansion)return;
  const width=Math.max(240,host.clientWidth||760),main=host.querySelector('[data-expanded-stage]'),mainRect=main?.getBoundingClientRect(),hostRect=host.getBoundingClientRect();
  const center=mainRect?.width?mainRect.left-hostRect.left+mainRect.width/2:width/2;
  const startRect=host.querySelector('[aria-label="Start '+node.label+'"]')?.getBoundingClientRect(),endRect=host.querySelector('[aria-label="End '+node.label+'"]')?.getBoundingClientRect();
  const sx=startRect?.width?startRect.left-hostRect.left+startRect.width/2:center-70,ex=endRect?.width?endRect.left-hostRect.left+endRect.width/2:center+70;
  const steps=node.subflow.nodes.filter(n=>!['Start','End'].includes(n.kind)),states=new Map((view.evaluation?.nodes||[]).map(n=>[n.id,n.status])),rowY=101;
  const left=Math.min(sx,ex)+38,right=Math.max(sx,ex)-38,gap=steps.length>1?Math.max(32,(right-left)/(steps.length-1)):0,first=steps.length>1?left:(sx+ex)/2;
  const stepX=i=>steps.length>1?first+i*gap:first;
  const firstX=steps.length?stepX(0):center,lastX=steps.length?stepX(steps.length-1):center,marker='jd-subflow-arrow-'+String(node.id).replace(/[^a-zA-Z0-9_-]/g,'-');
  const incoming='M'+sx+' 24 V39 C'+sx+' 50 '+(sx-8)+' 54 '+(sx-20)+' 54 H'+(sx-38)+' C'+(sx-50)+' 54 '+(sx-54)+' 62 '+(sx-54)+' 74 V88 Q'+(sx-54)+' 98 '+(sx-42)+' 98 H'+(firstX-10);
  const outgoing='M'+(lastX+10)+' '+rowY+' H'+(ex+42)+' Q'+(ex+54)+' '+rowY+' '+(ex+54)+' 89 V73 Q'+(ex+54)+' 60 '+(ex+42)+' 60 H'+(ex+20)+' C'+(ex+8)+' 60 '+ex+' 52 '+ex+' 40 V24';
  const stageLinks=steps.slice(0,-1).map((_,i)=>'<path class="jd-subflow-route" d="M'+(stepX(i)+10)+' '+rowY+' H'+(stepX(i+1)-10)+'"/>').join('');
  const stageMarkup=steps.map((step,i)=>{const state=states.get(node.id+'/'+step.id)||'Definition',klass=state.toLowerCase().replace(/[^a-z0-9_-]/g,''),x=stepX(i),glyph=state==='Completed'?'✓':state==='InProgress'?'●':state==='Blocked'?'!':'›';return '<g class="jd-subflow-step '+klass+'" data-substep="'+esc(step.id)+'" tabindex="0" role="button" aria-label="'+esc(step.label+': '+state)+'" transform="translate('+x+' '+rowY+')"><circle r="10"/><text class="jd-subflow-glyph" x="0" y="4" text-anchor="middle">'+glyph+'</text><text class="jd-subflow-label" x="7" y="25" transform="rotate(45 7 25)">'+esc(step.label)+'</text></g>';}).join('');
  expansion.hidden=false;
  expansion.innerHTML='<svg class="jd-subflow-diagram" viewBox="0 0 '+width+' 170" preserveAspectRatio="none" role="group" aria-label="Expanded '+esc(node.label)+'"><defs><marker id="'+marker+'" markerWidth="7" markerHeight="7" refX="6" refY="3.5" orient="auto"><path d="M0 0L7 3.5L0 7Z"/></marker></defs><path class="jd-subflow-route jd-subflow-main-dashes" d="M'+(sx+12)+' 13 H'+(ex-12)+'"/><path class="jd-subflow-route" d="'+incoming+'" marker-end="url(#'+marker+')"/>'+stageLinks+'<path class="jd-subflow-route" d="'+outgoing+'"/>'+stageMarkup+'</svg>';
  expansion.querySelectorAll('[data-substep]').forEach(step=>{const activate=()=>onSelect?.(node.id+'/'+step.dataset.substep);step.onclick=activate;step.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();activate();}};});
 }
 root.StudioJobWorkflowDisplay={render};
})(window);
