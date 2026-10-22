/* Shared Studio graph canvas. Dagre owns layout; the server owns execution. */
(function(root){
 'use strict';
 const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 function fromSteps(steps){
  const nodes=[{id:'start',label:'Start',kind:'Start'},...steps.map(t=>({id:t.key,label:t.title,kind:'Task',task:t,join:'All'})),{id:'end',label:'Complete',kind:'End'}],edges=[];
  for(const t of steps)for(const p of t.dependsOn?.length?t.dependsOn:['start'])edges.push({id:crypto.randomUUID(),from:p,to:t.key});
  for(const t of steps.filter(t=>!steps.some(x=>(x.dependsOn||[]).includes(t.key))))edges.push({id:crypto.randomUUID(),from:t.key,to:'end'});
  return {nodes,edges};
 }
 function restoreEndpoints(graph){
  let restored=0;
  function restore(flow){
   if(!Array.isArray(flow.nodes)||!Array.isArray(flow.edges))throw Error('This flow has no editable node or connection list.');
   for(const node of flow.nodes)if(node.kind==='Subflow'&&node.subflow)restore(node.subflow);
   for(const kind of ['Start','End']){
    if(flow.nodes.some(n=>n.kind===kind))continue;
    const preferred=kind==='Start'?'start':'end',id=flow.nodes.some(n=>n.id===preferred)?crypto.randomUUID():preferred;
    flow.nodes.push({id,label:kind==='Start'?'Start':'Complete',kind,join:'All'});restored++;
    const targets=flow.nodes.filter(n=>n.id!==id&&!flow.edges.some(e=>kind==='Start'?e.to===n.id:e.from===n.id));
    for(const target of targets)flow.edges.push({id:crypto.randomUUID(),from:kind==='Start'?id:target.id,to:kind==='Start'?target.id:id});
   }
  }
  restore(graph);return restored;
 }
 function visible(graph,expanded=new Set()){
  const nodes=[],edges=[];
  function add(g,prefix){
   for(const n of g.nodes){const id=prefix+n.id;if(n.kind==='Subflow'&&n.subflow&&expanded.has(id))add(n.subflow,id+'/');else nodes.push({...n,id,source:n});}
   const endpoint=(id,enter)=>{const n=g.nodes.find(x=>x.id===id);if(n?.kind==='Subflow'&&n.subflow&&expanded.has(prefix+id))return prefix+id+'/'+n.subflow.nodes.find(x=>x.kind===(enter?'Start':'End'))?.id;return prefix+id;};
   edges.push(...g.edges.map(e=>({...e,id:prefix+e.id,from:endpoint(e.from,false),to:endpoint(e.to,true),source:e})));
  }
  add(graph,'');return {nodes,edges};
 }
 function expandedSubflowMarkup(node,parent,states,model,positions,marker,options){
  const flow=node.subflow||{nodes:[],edges:[]},prefix=node.id+'/',stages=flow.nodes.filter(n=>n.kind!=='Start'&&n.kind!=='End');
  const start=flow.nodes.find(n=>n.kind==='Start'),end=flow.nodes.find(n=>n.kind==='End');
  const cx=parent.x,cy=parent.y,startX=cx-80,endX=cx+80,rowY=cy+150,gap=84,stagePositions=new Map();
  stages.forEach((stage,index)=>{
   const id=prefix+stage.id,visibleNode=model.nodes.find(n=>n.id===id),saved=visibleNode?.source;
   const pos={x:options.editable&&Number.isFinite(saved?.x)?saved.x:cx+(index-(stages.length-1)/2)*gap,
    y:options.editable&&Number.isFinite(saved?.y)?saved.y:rowY};
   stagePositions.set(stage.id,pos);positions.set(id,pos);
  });
  const paths=[],arrow=' marker-end="url(#'+marker+')"',edgeAttrs=edge=>' data-edge="'+esc(prefix+edge.id)+'" tabindex="'+(options.editable?'0':'-1')+'" role="button"';
  const entry=(flow.edges||[]).find(e=>e.from===start?.id&&stagePositions.has(e.to));
  const firstId=entry?.to||stages[0]?.id,first=firstId?stagePositions.get(firstId):null;
  if(first){
   const left=startX-36,turn=cy+50;
   paths.push('<path class="sg-subflow-path"'+edgeAttrs(entry||{id:'entry'})+' d="M'+startX+' '+(cy+11)+' V'+(cy+35)+' Q'+startX+' '+turn+' '+(startX-14)+' '+turn+' H'+left+' Q'+(left-12)+' '+turn+' '+(left-12)+' '+(turn+12)+' V'+first.y+' H'+(first.x-13)+'"'+arrow+'/>');
  }
  for(const edge of flow.edges||[]){
   const a=stagePositions.get(edge.from),b=stagePositions.get(edge.to);
   if(!a||!b)continue;
   const startPoint=a.x+12,endPoint=b.x-12;
   const d=b.x>=a.x?'M'+startPoint+' '+a.y+' H'+endPoint:'M'+startPoint+' '+a.y+' Q'+(startPoint+18)+' '+(a.y+18)+' '+endPoint+' '+b.y;
   paths.push('<path class="sg-subflow-path"'+edgeAttrs(edge)+' d="'+d+'"'+arrow+'/>');
  }
  const terminal=(flow.edges||[]).filter(e=>e.to===end?.id&&stagePositions.has(e.from));
  terminal.forEach((edge,index)=>{
   const last=stagePositions.get(edge.from);
   const farX=Math.max(last.x+52,endX+48+index*18),topY=rowY-42-index*12;
   const d='M'+(last.x+12)+' '+last.y+' H'+(farX-12)+' Q'+farX+' '+last.y+' '+farX+' '+(last.y-12)+' V'+(topY+12)+' Q'+farX+' '+topY+' '+(farX-12)+' '+topY+' H'+(endX+12)+' Q'+endX+' '+topY+' '+endX+' '+(topY+12)+' V'+(cy+11);
   paths.push('<path class="sg-subflow-path"'+edgeAttrs(edge)+' d="'+d+'"'+arrow+'/>');
  });
  const parentState=states.get(node.id)?.status||'Definition',parentClass=String(parentState).toLowerCase().replace(/[^a-z0-9_-]/g,'');
  const endpoint=(x,label,kind)=>'<g class="sg-subflow-endpoint '+parentClass+'" transform="translate('+x+' '+cy+')"><circle r="11"/><path d="'+(kind==='start'?'M-5 0h9m-4-4 4 4-4 4':'M-4 -4l8 8m0-8-8 8')+'"/><text x="4" y="27" text-anchor="start" transform="rotate(45 4 27)">'+esc(label+' '+node.label)+'</text></g>';
  const icons=stages.map(stage=>{
   const id=prefix+stage.id,pos=stagePositions.get(stage.id),state=states.get(id)?.status||states.get(stage.id)?.status||'Definition';
   const status=String(state).toLowerCase().replace(/[^a-z0-9_-]/g,''),glyph=state==='Completed'?'M-5 0l3 3 7-7':state==='InProgress'?'M-4 0h8':'M-3 -4L4 0l-7 4';
   const ports=options.editable?'<circle class="sg-substage-port" data-port="in" cx="-13" cy="0" r="4"/><circle class="sg-substage-port" data-port="out" cx="13" cy="0" r="4"/>':'';
   return '<g class="sg-substage '+status+(id===options.selected?' selected':'')+'" tabindex="0" role="button" aria-label="'+esc(stage.label+': '+state)+'" data-node="'+esc(id)+'" data-kind="'+esc(stage.kind)+'" transform="translate('+pos.x+' '+pos.y+')"><circle class="sg-substage-mark" r="11"/><path class="sg-substage-glyph" d="'+glyph+'"/>'+ports+'<text x="0" y="29" text-anchor="middle">'+esc(stage.label)+'</text><title>'+esc(stage.label)+' · '+esc(state)+'</title></g>';
  }).join('');
  return '<g class="sg-node sg-expanded-subflow '+parentClass+'" tabindex="0" role="button" aria-label="'+esc(node.label)+' expanded stage" data-node="'+esc(node.id)+'" data-kind="Subflow" data-subflow-toggle="'+esc(node.id)+'">'+
   '<path class="sg-subflow-path sg-subflow-dashed" d="M'+(startX+11)+' '+cy+' H'+(endX-11)+'"/>'+paths.join('')+
   endpoint(startX,'Start','start')+endpoint(endX,'End','end')+icons+'</g>';
 }
 function renderEdgeMarkup(edge,layout,baseModel,states,expanded,marker,options){
  if(!layout.hasEdge(edge.from,edge.to,edge.id))return '';
  const line=layout.edge(edge.from,edge.to,edge.id),state=options.evaluation?.edges?.[edge.id]||'Pending',points=line.points.map(p=>({...p}));
  if(options.editable){
   const from=baseModel.nodes.find(n=>n.id===edge.from),to=baseModel.nodes.find(n=>n.id===edge.to),fp=layout.node(edge.from),tp=layout.node(edge.to);
   points[0]={x:(from.x??fp.x)+90,y:from.y??fp.y};points[points.length-1]={x:(to.x??tp.x)-90,y:to.y??tp.y};
  }
  const fromPos=layout.node(edge.from),toPos=layout.node(edge.to),endpointOffset=options.summary?12:91;
  if(expanded.has(edge.from))points[0]={x:fromPos.x+endpointOffset,y:fromPos.y};
  if(expanded.has(edge.to))points[points.length-1]={x:toPos.x-endpointOffset,y:toPos.y};
  const label=edge.label||'';
  return '<g class="sg-edge '+esc(state.toLowerCase())+'" tabindex="'+(options.editable?'0':'-1')+'" role="button" aria-label="Connection '+esc(label||edge.id)+'" data-edge="'+esc(edge.id)+'"><polyline points="'+points.map(p=>p.x+','+p.y).join(' ')+'" marker-end="url(#'+marker+')"/><text x="'+(line.x||points[Math.floor(points.length/2)].x)+'" y="'+((line.y||points[Math.floor(points.length/2)].y)-7)+'">'+esc(label.length>36?label.slice(0,35)+'…':label)+'</text><title>'+esc(label||edge.id)+'</title></g>';
 }
 function renderNodeMarkup(node,layout,baseModel,model,states,positions,expanded,marker,options){
  const p=layout.node(node.id),state=states.get(node.id)?.status||'Definition',x=options.editable&&Number.isFinite(node.x)?node.x:p.x,y=options.editable&&Number.isFinite(node.y)?node.y:p.y;
  positions.set(node.id,{x,y});
  if(node.kind==='Subflow'&&node.subflow&&expanded.has(node.id))return expandedSubflowMarkup(node,{x,y},states,model,positions,marker,options);
  if(options.summary){
   const stateClass=String(state).toLowerCase().replace(/[^a-z0-9_-]/g,''),glyph=state==='Completed'?'M-5 0l3 3 7-7':state==='InProgress'?'M-4 0h8':state==='Blocked'?'M-4 -4l8 8m0-8-8 8':'M-3 -4L4 0l-7 4';
   return '<g class="sg-node sg-summary-node '+stateClass+(node.id===options.selected?' selected':'')+'" tabindex="0" role="button" aria-label="'+esc(node.label+': '+state)+'" data-node="'+esc(node.id)+'" data-kind="'+esc(node.kind)+'" transform="translate('+x+' '+y+')"><circle class="sg-summary-mark" r="11"/><path class="sg-summary-glyph" d="'+glyph+'"/><text x="4" y="23" text-anchor="start" transform="rotate(45 4 23)">'+esc(node.label)+'</text><title>'+esc(node.label)+' · '+esc(state)+'</title></g>';
  }
  const lines=(node.label.match(/.{1,23}(?:\\s|$)|.{1,23}/g)||[node.label]).slice(0,2);
  const type=node.kind==='Subflow'?'':esc(node.kind);
  return '<g class="sg-node '+esc(state.toLowerCase())+' '+(node.id===options.selected?'selected':'')+'" tabindex="0" role="button" aria-label="'+esc(node.label)+': '+esc(state)+'" data-node="'+esc(node.id)+'" data-kind="'+esc(node.kind)+'" transform="translate('+(x-90)+' '+(y-37)+')"><rect width="180" height="74" rx="'+(node.kind==='Decision'?24:8)+'"/><text x="90" y="21" text-anchor="middle">'+type+'</text>'+lines.map((line,index)=>'<text class="sg-label" x="90" y="'+(39+index*14)+'" text-anchor="middle">'+esc(line)+'</text>').join('')+'<text class="sg-state" x="90" y="67" text-anchor="middle">'+esc(state==='Definition'?node.join==='Any'?'Any qualifying branch':'':state)+'</text>'+(options.editable?'<circle data-port="in" cx="0" cy="37" r="7"/><circle data-port="out" cx="180" cy="37" r="7"/>':'')+'<title>'+esc(node.label)+(node.task?.defaultAssignee?' · Assigned to '+esc(node.task.defaultAssignee):'')+(node.role?' · '+esc(node.role):'')+'</title></g>';
 }
 function canvas(host,graph,options={}){
  const expanded=options.expanded||new Set(),model=visible(graph,expanded),baseModel=visible(graph,new Set()),states=new Map((options.evaluation?.nodes||[]).map(n=>[n.id,n]));
  for(const n of baseModel.nodes.filter(n=>n.kind==='Subflow')){const children=[...states.values()].filter(s=>s.id.startsWith(n.id+'/'));if(children.length)states.set(n.id,{status:children.every(s=>s.status==='Inactive')?'Inactive':children.every(s=>['Completed','Skipped','Inactive'].includes(s.status))?'Completed':children.some(s=>s.status==='InProgress')?'InProgress':children.some(s=>s.status==='Available')?'Available':'Blocked'});}
  const layoutModel=options.summary?{nodes:baseModel.nodes.filter(n=>n.kind!=='Start'&&n.kind!=='End'),edges:baseModel.edges.filter(e=>baseModel.nodes.some(n=>n.id===e.from&&n.kind!=='Start'&&n.kind!=='End')&&baseModel.nodes.some(n=>n.id===e.to&&n.kind!=='Start'&&n.kind!=='End'))}:baseModel;
  const layout=new dagre.graphlib.Graph({multigraph:true}).setGraph({rankdir:'LR',nodesep:options.summary?30:46,ranksep:options.summary?46:80,marginx:24,marginy:28}).setDefaultEdgeLabel(()=>({}));
  for(const n of layoutModel.nodes)layout.setNode(n.id,{width:options.summary?24:180,height:options.summary?24:74});
  for(const e of layoutModel.edges)if(layout.hasNode(e.from)&&layout.hasNode(e.to))layout.setEdge(e.from,e.to,{width:e.label?Math.min(240,e.label.length*6):0,height:e.label?22:0},e.id);
  dagre.layout(layout);
  const expandedNodes=layoutModel.nodes.filter(n=>n.kind==='Subflow'&&n.subflow&&expanded.has(n.id));
  const naturalWidth=Math.max(layout.graph().width||400,400,...expandedNodes.map(n=>layout.node(n.id).x+200));
  const height=Math.max(layout.graph().height||100,100,...expandedNodes.map(n=>layout.node(n.id).y+220));
  const availableWidth=host.clientWidth||host.parentElement?.clientWidth||0;
  const width=(expandedNodes.length||options.summary)&&availableWidth>0&&naturalWidth>availableWidth?availableWidth:naturalWidth;
  const marker='graph-arrow-'+crypto.randomUUID(),positions=new Map();
  host.classList.remove(options.summary?'studio-graph':'sg-summary');host.classList.add(options.summary?'sg-summary':'studio-graph');
  const svgMarkup=layoutModel.edges.map(e=>renderEdgeMarkup(e,layout,layoutModel,states,expanded,marker,options)).join('')+layoutModel.nodes.map(n=>renderNodeMarkup(n,layout,layoutModel,model,states,positions,expanded,marker,options)).join('');
  const toolsMarkup=options.summary?'':`<div class="sg-tools"><button type="button" data-zoom="1.2" aria-label="Zoom in">＋</button><button type="button" data-zoom="0.83" aria-label="Zoom out">−</button><button type="button" data-fit>Fit</button><button type="button" data-layout>Auto layout</button><span>${options.editable?'Drag nodes or connect the right port to a left port.':'Drag empty space to pan. Select a task to view details.'}</span></div>`;
  const svgStyle=options.summary?` style="width:${width}px;height:${height}px"`:'';
  host.innerHTML=toolsMarkup+`<div class="sg-viewport" tabindex="0" aria-label="Workflow graph canvas"><svg role="img" aria-label="Connected workflow"${svgStyle} viewBox="0 0 ${width} ${height}"><defs><marker id="${marker}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path d="M 0 0 L 10 5 L 0 10 z"/></marker></defs><g class="sg-world">${svgMarkup}</g></svg></div>`;
  const viewport=host.querySelector('.sg-viewport'),svg=host.querySelector('svg');let box={x:0,y:0,w:width,h:height};
  const update=()=>svg.setAttribute('viewBox',`${box.x} ${box.y} ${box.w} ${box.h}`);
  if(options.editable&&viewport.clientWidth>0&&viewport.clientHeight>0){box={x:0,y:(height-viewport.clientHeight)/2,w:Math.min(width,viewport.clientWidth),h:viewport.clientHeight};update();}
  host.querySelectorAll('[data-zoom]').forEach(b=>b.onclick=()=>{const f=Number(b.dataset.zoom),w=box.w/f,h=box.h/f;box.x+=(box.w-w)/2;box.y+=(box.h-h)/2;box.w=w;box.h=h;update();});
  const fitButton=host.querySelector('[data-fit]');if(fitButton)fitButton.onclick=()=>{box={x:0,y:0,w:width,h:height};update();};
  if(expanded.size&&!options.summary){const collapse=document.createElement('button');collapse.type='button';collapse.textContent='Close expanded stage';collapse.onclick=()=>{canvas(host,graph,{...options,expanded:new Set()});};host.querySelector('.sg-tools').append(collapse);}
  const layoutButton=host.querySelector('[data-layout]');if(layoutButton)layoutButton.onclick=()=>{if(options.editable){for(const n of model.nodes){delete n.source.x;delete n.source.y;}options.onChange?.();}else fitButton?.click();};
  if(!options.summary)viewport.onwheel=e=>{e.preventDefault();host.querySelector(`[data-zoom="${e.deltaY<0?'1.2':'0.83'}"]`).click();};
  let drag=null,connect=null;
  svg.onpointerdown=e=>{
   const node=e.target.closest('[data-node]'),port=e.target.closest('[data-port]');
   if(port&&options.editable&&port.dataset.port==='out'){connect=node.dataset.node;e.preventDefault();return;}
   const rect=svg.getBoundingClientRect();drag={id:node?.dataset.node,subflowId:node?.dataset.subflowToggle||null,startX:e.clientX,startY:e.clientY,ratio:Math.max(box.w/rect.width,box.h/rect.height),box:{...box},moved:false};
   if(node?.dataset.kind==='Subflow'&&baseModel.nodes.some(n=>n.id===drag.id&&n.kind==='Subflow'))drag.subflowId=drag.id;
   if(node&&options.editable)drag.node=model.nodes.find(n=>n.id===drag.id)||baseModel.nodes.find(n=>n.id===drag.id);
   if(!node||options.editable)svg.setPointerCapture?.(e.pointerId);
  };
  svg.onpointermove=e=>{if(!drag)return;const dx=(e.clientX-drag.startX)*drag.ratio,dy=(e.clientY-drag.startY)*drag.ratio;drag.moved=Math.abs(dx)+Math.abs(dy)>5;
   if(drag.node){const p=positions.get(drag.id)||layout.node(drag.id);host.querySelectorAll('[data-node]').forEach(el=>{if(el.dataset.node===drag.id)el.setAttribute('transform',el.classList.contains('sg-substage')?`translate(${(drag.node.x??p.x)+dx},${(drag.node.y??p.y)+dy})`:`translate(${(drag.node.x??p.x)-90+dx},${(drag.node.y??p.y)-37+dy})`);});}
   else if(!drag.id){box.x=drag.box.x-dx;box.y=drag.box.y-dy;update();}
  };
  svg.onpointerup=e=>{
   if(connect){const target=e.target.closest('[data-node]');if(target&&e.target.closest('[data-port="in"]')&&target.dataset.node!==connect)options.onConnect?.(connect,target.dataset.node);connect=null;}
   if(drag?.node&&drag.moved){const p=positions.get(drag.id)||layout.node(drag.id);drag.node.source.x=(drag.node.x??p.x)+(e.clientX-drag.startX)*drag.ratio;drag.node.source.y=(drag.node.y??p.y)+(e.clientY-drag.startY)*drag.ratio;options.onChange?.();}
   else if(drag?.id&&!drag.moved){if(drag.subflowId)toggleSubflow(drag.subflowId);else options.onSelect?.(drag.id);}drag=null;
  };
  function toggleSubflow(id){const next=new Set(expanded);if(next.has(id))next.delete(id);else{next.clear();next.add(id);}canvas(host,graph,{...options,expanded:next});}
  host.querySelectorAll('[data-node]').forEach(el=>{
   el.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();if(el.dataset.subflowToggle||el.dataset.kind==='Subflow'&&baseModel.nodes.some(n=>n.id===el.dataset.node&&n.kind==='Subflow'))toggleSubflow(el.dataset.subflowToggle||el.dataset.node);else options.onSelect?.(el.dataset.node);}if(e.key==='Delete'&&options.editable)options.onDelete?.(el.dataset.node);};
  });
  host.querySelectorAll('[data-edge]').forEach(el=>{el.onclick=()=>options.onEdge?.(el.dataset.edge);el.onkeydown=e=>{if(e.key==='Enter')options.onEdge?.(el.dataset.edge);};});
  viewport.onkeydown=e=>{if(e.target!==viewport)return;const moves={ArrowLeft:[-40,0],ArrowRight:[40,0],ArrowUp:[0,-40],ArrowDown:[0,40]};if(moves[e.key]){e.preventDefault();box.x+=moves[e.key][0];box.y+=moves[e.key][1];update();}};
  return {model,layout,expanded};
 }
 root.StudioGraph={canvas,visible,fromSteps,restoreEndpoints,esc};
})(window);

