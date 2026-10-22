/* Job Details workflow display. Independent from the workflow editor. */
(function(root){
 'use strict';
 const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const renderedView=new WeakMap();
 function render(host,view,onSelect){
  const graph=view?.graph;if(!host||!graph?.nodes)return;
  if(renderedView.get(host)!==view){delete host.dataset.expanded;renderedView.set(host,view);}
  const nodes=graph.nodes.filter(n=>!['Start','End'].includes(n.kind)),states=new Map((view.evaluation?.nodes||[]).map(n=>[n.id,n.status])),active=host.dataset.expanded||'';
  host.innerHTML='<div class="jd-workflow-display" role="list" aria-label="Job workflow">'+nodes.map(n=>{const state=states.get(n.id)||'Definition',klass=state.toLowerCase().replace(/[^a-z0-9_-]/g,''),label=esc(n.label);return '<button type="button" class="jd-workflow-stage '+klass+(active===n.id?' is-expanded':'')+'" role="listitem" data-stage="'+esc(n.id)+'" aria-expanded="'+(active===n.id)+'" title="'+label+' · '+esc(state)+'"><span class="jd-workflow-stage-icon" aria-hidden="true">'+(state==='Completed'?'✓':state==='InProgress'?'●':state==='Blocked'?'!':'›')+'</span><span class="jd-workflow-stage-label">'+label+'</span></button>';}).join('')+'</div><div class="jd-workflow-expansion" hidden></div>';
  host.querySelectorAll('[data-stage]').forEach(button=>button.onclick=()=>{
   const id=button.dataset.stage,node=nodes.find(n=>n.id===id),expandable=node?.kind==='Subflow'&&Array.isArray(node.subflow?.nodes);
   if(expandable){host.dataset.expanded=active===id?'':id;render(host,view,onSelect);return;}
   delete host.dataset.expanded;render(host,view,onSelect);onSelect?.(id);
  });
  if(!active)return;
  const node=nodes.find(n=>n.id===active);
  if(node?.kind!=='Subflow'||!Array.isArray(node.subflow?.nodes))return;
  const expansion=host.querySelector('.jd-workflow-expansion'),steps=node.subflow.nodes.filter(n=>!['Start','End'].includes(n.kind)),substates=new Map((view.evaluation?.nodes||[]).map(n=>[n.id,n.status]));
  const width=Math.max(240,host.clientWidth||760),center=width/2,startX=center-82,endX=center+82,iconY=93,spacing=Math.min(92,Math.max(42,(endX-startX-70)/Math.max(1,steps.length-1))),firstX=center-(Math.max(0,steps.length-1)*spacing)/2;
  const escId=esc(node.id),startLabel='Start '+node.label,endLabel='End '+node.label,marker='jd-flow-arrow-'+Math.random().toString(36).slice(2);
  const label=(x,y,text,anchor='start')=>'<text class="jd-subflow-label" x="'+x+'" y="'+y+'" text-anchor="'+anchor+'" transform="rotate(45 '+x+' '+y+')">'+esc(text)+'</text>';
  const endpoint=(x,text,mark)=>'<g class="jd-subflow-endpoint" aria-label="'+esc(text)+'"><circle cx="'+x+'" cy="13" r="9"/><path d="'+(mark==='start'?'M'+(x-4)+' 13h7m-3-3 3 3-3 3':'M'+(x-3)+' 10l6 6m0-6-6 6')+'"/>'+label(x+6,29,text)+'</g>';
  const icons=steps.map((step,i)=>{const x=firstX+i*spacing,state=substates.get(node.id+'/'+step.id)||'Definition',klass=state.toLowerCase().replace(/[^a-z0-9_-]/g,''),glyph=state==='Completed'?'✓':state==='InProgress'?'●':state==='Blocked'?'!':'›';return '<g class="jd-subflow-step '+klass+'" data-substep="'+esc(step.id)+'" tabindex="0" role="button" aria-label="'+esc(step.label+': '+state)+'" transform="translate('+x+' '+iconY+')"><circle r="9"/><text class="jd-subflow-glyph" x="0" y="4" text-anchor="middle">'+glyph+'</text>'+label(5,19,step.label)+'</g>';}).join('');
  const routeLeft=steps.length?firstX-10:center-18,routeRight=steps.length?firstX+(steps.length-1)*spacing+10:center+18;
  const inPath='M'+startX+' 22 V38 C'+startX+' 48 '+(startX-8)+' 51 '+(startX-20)+' 51 H'+(startX-34)+' C'+(startX-44)+' 51 '+(startX-46)+' 58 '+(startX-46)+' 68 V78 Q'+(startX-46)+' 86 '+(startX-38)+' 86 H'+routeLeft;
  const outPath='M'+routeRight+' '+iconY+' H'+(endX+34)+' Q'+(endX+46)+' '+iconY+' '+(endX+46)+' 81 V65 Q'+(endX+46)+' 53 '+(endX+34)+' 53 H'+(endX+19)+' C'+(endX+7)+' 53 '+endX+' 46 '+endX+' 34 V22';
  const midPath='M'+(startX+10)+' 13 H'+(endX-10);
  const joins=steps.slice(0,-1).map((_,i)=>'<path class="jd-subflow-route" d="M'+(firstX+i*spacing+9)+' '+iconY+' H'+(firstX+(i+1)*spacing-9)+'"/>').join('');
  expansion.hidden=false;
  expansion.innerHTML='<svg class="jd-subflow-diagram" viewBox="0 0 '+width+' 148" preserveAspectRatio="none" role="group" aria-label="Expanded '+esc(node.label)+' workflow"><defs><marker id="'+marker+'" markerWidth="6" markerHeight="6" refX="5" refY="3" orient="auto"><path d="M0 0L6 3L0 6Z"/></marker></defs><path class="jd-subflow-main-dashes" d="'+midPath+'"/><path class="jd-subflow-route" d="'+inPath+' H'+routeLeft+'"/>'+joins+'<path class="jd-subflow-route" d="'+outPath+'" marker-end="url(#'+marker+')"/>'+endpoint(startX,startLabel,'start')+endpoint(endX,endLabel,'end')+icons+'</svg>';
  expansion.querySelectorAll('[data-substep]').forEach(step=>{const activate=()=>onSelect?.(node.id+'/'+step.dataset.substep);step.onclick=activate;step.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();activate();}};});
 }
 root.StudioJobWorkflowDisplay={render};
})(window);
