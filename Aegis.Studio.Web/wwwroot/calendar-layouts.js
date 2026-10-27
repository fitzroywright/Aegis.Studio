/* Shared read-only layouts. Callers supply already-authorized, filtered records. */
window.StudioCalendarLayouts=(()=>{
 'use strict';
 const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const modes=['jobs','calendar','timeline'];
 const day=d=>new Date(d.getFullYear(),d.getMonth(),d.getDate());
 const add=(d,n)=>new Date(d.getFullYear(),d.getMonth(),d.getDate()+n);
 const stamp=d=>Date.UTC(d.getFullYear(),d.getMonth(),d.getDate());
 function range(anchor,period){
  let from=day(anchor),days=1;
  if(period==='month'){from=new Date(from.getFullYear(),from.getMonth(),1);days=new Date(from.getFullYear(),from.getMonth()+1,0).getDate();}
  if(period==='week'){from=add(from,-((from.getDay()+6)%7));days=7;}
  return{from,to:add(from,days),days};
 }
 function switcher(layout,attribute='data-calendar-layout'){
  if(!/^data-[a-z-]+$/.test(attribute))throw Error('Invalid layout control attribute');
  return '<div class="studio-layout-switch" role="group" aria-label="Layout">'+modes.map(mode=>`<button type="button" ${attribute}="${mode}" aria-pressed="${layout===mode}">${mode[0].toUpperCase()+mode.slice(1)}</button>`).join('')+'</div>';
 }
 function parse(value,allDay){if(!value)return null;const s=String(value),d=allDay&&/^\d{4}-\d{2}-\d{2}/.test(s)?new Date(Number(s.slice(0,4)),Number(s.slice(5,7))-1,Number(s.slice(8,10))):new Date(value);return Number.isFinite(+d)?d:null;}
 function render({layout,items=[],anchor=new Date(),period='week',span,attribute='data-calendar-item'}){
  if(!/^data-[a-z-]+$/.test(attribute))throw Error('Invalid calendar item attribute');
  const r=range(anchor,period);if(Number.isInteger(span)&&span>0&&span<=31){r.days=span;r.to=add(r.from,span);}const dates=Array.from({length:r.days},(_,i)=>add(r.from,i));
  const values=items.map(item=>{const start=parse(item.start,item.allDay),end=parse(item.end,false);return{...item,start,displayEnd:end&&start&&end>=start?end:null,end:start?(end&&end>start?new Date(+end+(item.inclusiveEnd?1:0)):new Date(+start+1)):null};});
  const dated=values.filter(x=>x.start&&x.start<r.to&&x.end>r.from).sort((a,b)=>a.start-b.start||String(a.title).localeCompare(String(b.title)));
  const undated=values.filter(x=>!x.start);
  const when=x=>x.start?x.allDay?x.start.toLocaleDateString():x.start.toLocaleString()+(x.displayEnd?' → '+x.displayEnd.toLocaleString():''): 'Date not recorded';
  const button=x=>`<button type="button" ${attribute}="${esc(x.id)}" class="studio-calendar-item"><strong>${esc(x.title)}</strong><small>${esc(x.context||'')}</small><small>${esc(when(x))}${x.status?' · '+esc(x.status):''}</small></button>`;
  let body='';
  if(layout==='calendar'){
   const leading=period==='month'?(r.from.getDay()+6)%7:0;
   body=`<div class="studio-calendar-month" data-period="${esc(period)}">${'<div class="studio-calendar-blank"></div>'.repeat(leading)}${dates.map(d=>{const next=add(d,1),entries=dated.filter(x=>x.start<next&&x.end>d);return`<section class="studio-calendar-day"><h3>${esc(d.toLocaleDateString([],{weekday:'short',month:'short',day:'numeric'}))}</h3>${entries.map(button).join('')||'<p>No dated work</p>'}</section>`;}).join('')}</div>`;
  }else if(layout==='timeline'){
   body=`<div class="studio-calendar-timeline" style="--studio-layout-days:${r.days}"><header><span>Work</span><div>${dates.map(d=>'<span>'+esc(d.toLocaleDateString([],{month:'short',day:'numeric'}))+'</span>').join('')}</div></header>${dated.map(x=>{const first=Math.max(0,(stamp(day(x.start))-stamp(r.from))/86400000),last=Math.min(r.days,(stamp(day(new Date(+x.end-1)))-stamp(r.from))/86400000+1);return`<article><div><strong>${esc(x.title)}</strong><small>${esc(x.context||'')}</small></div><div><button type="button" class="studio-calendar-bar" ${attribute}="${esc(x.id)}" style="grid-column:${first+1} / ${Math.max(first+2,last+1)}" title="${esc(x.title+' · '+when(x))}">${esc(x.status||x.title)}</button></div></article>`;}).join('')||'<p>No dated work in this period.</p>'}</div>`;
  }else body='<div class="studio-calendar-list">'+(dated.map(button).join('')||'<p>No dated work in this period.</p>')+'</div>';
  return`<section class="studio-calendar-projection" aria-label="${esc(layout)} layout">${body}${undated.length?'<section class="studio-calendar-undated"><h3>Date not recorded</h3>'+undated.map(button).join('')+'</section>':''}</section>`;
 }
 function bind(host,{onLayout,onItem}={}){
  if(onLayout)host.querySelectorAll('[data-calendar-layout]').forEach(b=>b.onclick=()=>onLayout(b.dataset.calendarLayout));
  if(onItem)host.querySelectorAll('[data-calendar-item]').forEach(b=>b.onclick=()=>onItem(b.dataset.calendarItem));
 }
 return{switcher,render,bind,range};
})();
