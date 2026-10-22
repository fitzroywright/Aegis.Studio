/* Job Details workflow display. Independent from the workflow editor. */
(function(root){
 'use strict';
 const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 function render(host,view,onSelect){
  const graph=view?.graph;if(!host||!graph?.nodes)return;
  const nodes=graph.nodes.filter(n=>!['Start','End'].includes(n.kind));
  const ev=new Map((view.evaluation?.nodes||[]).map(n=>[n.id,n.status]));
  const width=Math.max(240,host.clientWidth||760),gap=width/Math.max(1,nodes.length),active=host.dataset.expanded||'';
  host.innerHTML='<div class="jd-workflow-display" role="list" aria-label="Job workflow">'+nodes.map((n,i)=>{const state=ev.get(n.id)||'Definition',klass=state.toLowerCase().replace(/[^a-z0-9_-]/g,''),label=esc(n.label);return '<button type="button" class="jd-workflow-stage '+klass+(active===n.id?' is-expanded':'')+'" role="listitem" data-stage="'+esc(n.id)+'" aria-expanded="'+(active===n.id)+'" title="'+label+' · '+esc(state)+'"><span class="jd-workflow-stage-icon" aria-hidden="true">'+(state==='Completed'?'✓':state==='InProgress'?'●':state==='Blocked'?'!':'›')+'</span><span class="jd-workflow-stage-label">'+label+'</span></button>';}).join('')+'</div><div class="jd-workflow-expansion" '+(active?'':'hidden')+'></div>';
  host.querySelectorAll('[data-stage]').forEach(b=>b.onclick=()=>{const id=b.dataset.stage;host.dataset.expanded=active===id?'':id;render(host,view,onSelect);if(active!==id){const n=nodes.find(x=>x.id===id);if(n?.kind!=='Subflow')onSelect?.(id);}});
  if(!active)return;
  const node=nodes.find(n=>n.id===active);if(!node)return;
  const target=host.querySelector('.jd-workflow-expansion');
  if(node.kind!=='Subflow'||!node.subflow){target.hidden=true;return;}
  const steps=node.subflow.nodes.filter(n=>!['Start','End'].includes(n.kind)),stepStates=new Map((view.evaluation?.nodes||[]).map(n=>[n.id,n.status]));
  target.hidden=false;target.innerHTML='<span class="jd-subflow-endpoint">Start '+esc(node.label)+'</span><span class="jd-subflow-dashes" aria-hidden="true"></span>'+steps.map(s=>'<button type="button" class="jd-subflow-step" data-substep="'+esc(s.id)+'" title="'+esc(s.label)+'"><span class="jd-workflow-stage-icon">'+(stepStates.get(node.id+'/'+s.id)==='Completed'?'✓':'›')+'</span><span>'+esc(s.label)+'</span></button>').join('<span class="jd-subflow-dashes" aria-hidden="true"></span>')+'<span class="jd-subflow-dashes" aria-hidden="true"></span><span class="jd-subflow-endpoint">End '+esc(node.label)+'</span>';
  target.querySelectorAll('[data-substep]').forEach(b=>b.onclick=()=>onSelect?.(node.id+'/'+b.dataset.substep));
 }
 root.StudioJobWorkflowDisplay={render};
})(window);
