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
 function visible(graph,expanded=new Set()){
  const nodes=[],edges=[];
  function add(g,prefix){
   for(const n of g.nodes){const id=prefix+n.id;if(n.kind==='Subflow'&&n.subflow&&expanded.has(id))add(n.subflow,id+'/');else nodes.push({...n,id,source:n});}
   const endpoint=(id,enter)=>{const n=g.nodes.find(x=>x.id===id);if(n?.kind==='Subflow'&&n.subflow&&expanded.has(prefix+id))return prefix+id+'/'+n.subflow.nodes.find(x=>x.kind===(enter?'Start':'End'))?.id;return prefix+id;};
   edges.push(...g.edges.map(e=>({...e,id:prefix+e.id,from:endpoint(e.from,false),to:endpoint(e.to,true),source:e})));
  }
  add(graph,'');return {nodes,edges};
 }
 function canvas(host,graph,options={}){
  const expanded=options.expanded||new Set(),model=visible(graph,expanded),states=new Map((options.evaluation?.nodes||[]).map(n=>[n.id,n]));
  for(const n of model.nodes.filter(n=>n.kind==='Subflow')){const children=[...states.values()].filter(s=>s.id.startsWith(n.id+'/'));if(children.length)states.set(n.id,{status:children.every(s=>s.status==='Inactive')?'Inactive':children.every(s=>['Completed','Skipped','Inactive'].includes(s.status))?'Completed':children.some(s=>s.status==='InProgress')?'InProgress':children.some(s=>s.status==='Available')?'Available':'Blocked'});}
  const layout=new dagre.graphlib.Graph({multigraph:true}).setGraph({rankdir:'LR',nodesep:46,ranksep:80,marginx:24,marginy:28}).setDefaultEdgeLabel(()=>({}));
  for(const n of model.nodes)layout.setNode(n.id,{width:180,height:74});
  for(const e of model.edges)if(layout.hasNode(e.from)&&layout.hasNode(e.to))layout.setEdge(e.from,e.to,{width:e.label?Math.min(240,e.label.length*6):0,height:e.label?22:0},e.id);
  dagre.layout(layout);
  const width=Math.max(layout.graph().width||400,400),height=Math.max(layout.graph().height||160,160), marker='graph-arrow-'+crypto.randomUUID();
  host.classList.add('studio-graph');
  host.innerHTML=`<div class="sg-tools"><button type="button" data-zoom="1.2" aria-label="Zoom in">＋</button><button type="button" data-zoom="0.83" aria-label="Zoom out">−</button><button type="button" data-fit>Fit</button><button type="button" data-layout>Auto layout</button><span>${options.editable?'Drag nodes or connect the right port to a left port.':'Drag empty space to pan. Select a task to view details.'}</span></div><div class="sg-viewport" tabindex="0" aria-label="Workflow graph canvas"><svg role="img" aria-label="Connected workflow" viewBox="0 0 ${width} ${height}"><defs><marker id="${marker}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto"><path d="M 0 0 L 10 5 L 0 10 z"/></marker></defs><g class="sg-world">${model.edges.filter(e=>layout.hasEdge(e.from,e.to,e.id)).map(e=>{
    const l=layout.edge(e.from,e.to,e.id),state=options.evaluation?.edges?.[e.id]||'Pending',points=l.points.map(p=>({...p}));
    if(options.editable){const a=model.nodes.find(n=>n.id===e.from),b=model.nodes.find(n=>n.id===e.to),ap=layout.node(e.from),bp=layout.node(e.to);points[0]={x:(a.x??ap.x)+90,y:a.y??ap.y};points[points.length-1]={x:(b.x??bp.x)-90,y:b.y??bp.y};}
    return `<g class="sg-edge ${esc(state.toLowerCase())}" tabindex="${options.editable?'0':'-1'}" role="button" aria-label="Connection ${esc(e.label||e.id)}" data-edge="${esc(e.id)}"><polyline points="${points.map(p=>p.x+','+p.y).join(' ')}" marker-end="url(#${marker})"/><text x="${l.x||points[Math.floor(points.length/2)].x}" y="${(l.y||points[Math.floor(points.length/2)].y)-7}">${esc((e.label||'').length>36?e.label.slice(0,35)+'…':e.label||'')}</text><title>${esc(e.label||e.id)}</title></g>`;
   }).join('')}${model.nodes.map(n=>{
    const p=layout.node(n.id),state=states.get(n.id)?.status||'Definition',x=options.editable&&Number.isFinite(n.x)?n.x:p.x,y=options.editable&&Number.isFinite(n.y)?n.y:p.y;
    const lines=(n.label.match(/.{1,23}(?:\s|$)|.{1,23}/g)||[n.label]).slice(0,2);
    return `<g class="sg-node ${esc(state.toLowerCase())} ${n.id===options.selected?'selected':''}" tabindex="0" role="button" aria-label="${esc(n.label)}: ${esc(state)}" data-node="${esc(n.id)}" transform="translate(${x-90},${y-37})"><rect width="180" height="74" rx="${n.kind==='Decision'?24:8}"/><text x="90" y="21" text-anchor="middle">${esc(n.kind)}${n.kind==='Subflow'?' · expand':''}</text>${lines.map((l,i)=>`<text class="sg-label" x="90" y="${39+i*14}" text-anchor="middle">${esc(l)}</text>`).join('')}<text class="sg-state" x="90" y="67" text-anchor="middle">${esc(state==='Definition'?n.join==='Any'?'Any qualifying branch':'':state)}</text>${options.editable?'<circle data-port="in" cx="0" cy="37" r="7"/><circle data-port="out" cx="180" cy="37" r="7"/>':''}<title>${esc(n.label)}${n.task?.defaultAssignee?' · Assigned to '+esc(n.task.defaultAssignee):''}${n.role?' · '+esc(n.role):''}</title></g>`;
   }).join('')}</g></svg></div>`;
  const viewport=host.querySelector('.sg-viewport'),svg=host.querySelector('svg');let box={x:0,y:0,w:width,h:height};
  const update=()=>svg.setAttribute('viewBox',`${box.x} ${box.y} ${box.w} ${box.h}`);
  if(options.editable&&viewport.clientWidth>0&&viewport.clientHeight>0){box={x:0,y:(height-viewport.clientHeight)/2,w:viewport.clientWidth,h:viewport.clientHeight};update();}
  host.querySelectorAll('[data-zoom]').forEach(b=>b.onclick=()=>{const f=Number(b.dataset.zoom),w=box.w/f,h=box.h/f;box.x+=(box.w-w)/2;box.y+=(box.h-h)/2;box.w=w;box.h=h;update();});
  host.querySelector('[data-fit]').onclick=()=>{box={x:0,y:0,w:width,h:height};update();};
  if(expanded.size){const collapse=document.createElement('button');collapse.type='button';collapse.textContent='Collapse subflows';collapse.onclick=()=>{expanded.clear();canvas(host,graph,{...options,expanded});};host.querySelector('.sg-tools').append(collapse);}
  host.querySelector('[data-layout]').onclick=()=>{if(options.editable){for(const n of model.nodes){delete n.source.x;delete n.source.y;}options.onChange?.();}else host.querySelector('[data-fit]').click();};
  viewport.onwheel=e=>{e.preventDefault();host.querySelector(`[data-zoom="${e.deltaY<0?'1.2':'0.83'}"]`).click();};
  let drag=null,connect=null;
  svg.onpointerdown=e=>{
   const node=e.target.closest('[data-node]'),port=e.target.closest('[data-port]');
   if(port&&options.editable&&port.dataset.port==='out'){connect=node.dataset.node;e.preventDefault();return;}
   const rect=svg.getBoundingClientRect();drag={id:node?.dataset.node,startX:e.clientX,startY:e.clientY,ratio:Math.max(box.w/rect.width,box.h/rect.height),box:{...box},moved:false};
   if(node&&options.editable)drag.node=model.nodes.find(n=>n.id===drag.id);
   if(!node||options.editable)svg.setPointerCapture?.(e.pointerId);
  };
  svg.onpointermove=e=>{if(!drag)return;const dx=(e.clientX-drag.startX)*drag.ratio,dy=(e.clientY-drag.startY)*drag.ratio;drag.moved=Math.abs(dx)+Math.abs(dy)>5;
   if(drag.node){const p=layout.node(drag.id);host.querySelectorAll('[data-node]').forEach(el=>{if(el.dataset.node===drag.id)el.setAttribute('transform',`translate(${(drag.node.x??p.x)-90+dx},${(drag.node.y??p.y)-37+dy})`);});}
   else if(!drag.id){box.x=drag.box.x-dx;box.y=drag.box.y-dy;update();}
  };
  svg.onpointerup=e=>{
   if(connect){const target=e.target.closest('[data-node]');if(target&&e.target.closest('[data-port="in"]')&&target.dataset.node!==connect)options.onConnect?.(connect,target.dataset.node);connect=null;}
   if(drag?.node&&drag.moved){const p=layout.node(drag.id);drag.node.source.x=(drag.node.x??p.x)+(e.clientX-drag.startX)*drag.ratio;drag.node.source.y=(drag.node.y??p.y)+(e.clientY-drag.startY)*drag.ratio;options.onChange?.();}
   else if(drag?.id&&!drag.moved)options.onSelect?.(drag.id);drag=null;
  };
  host.querySelectorAll('[data-node]').forEach(el=>{
   el.ondblclick=()=>{const n=model.nodes.find(n=>n.id===el.dataset.node);if(n.kind==='Subflow'){expanded.has(n.id)?expanded.delete(n.id):expanded.add(n.id);canvas(host,graph,{...options,expanded});}};
   el.onkeydown=e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();options.onSelect?.(el.dataset.node);}if(e.key==='Delete'&&options.editable)options.onDelete?.(el.dataset.node);};
  });
  host.querySelectorAll('[data-edge]').forEach(el=>{el.onclick=()=>options.onEdge?.(el.dataset.edge);el.onkeydown=e=>{if(e.key==='Enter')options.onEdge?.(el.dataset.edge);};});
  viewport.onkeydown=e=>{if(e.target!==viewport)return;const moves={ArrowLeft:[-40,0],ArrowRight:[40,0],ArrowUp:[0,-40],ArrowDown:[0,40]};if(moves[e.key]){e.preventDefault();box.x+=moves[e.key][0];box.y+=moves[e.key][1];update();}};
  return {model,layout,expanded};
 }
 root.StudioGraph={canvas,visible,fromSteps,esc};
})(window);
