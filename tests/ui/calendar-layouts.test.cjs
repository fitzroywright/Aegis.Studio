const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
function setup(t){const dom=new JSDOM('<main></main>',{runScripts:'outside-only'});t.after(()=>dom.window.close());const w=dom.window;w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/calendar-layouts.js'),'utf8'));return{w,host:w.document.querySelector('main')};}
test('calendar layouts retain real dates, omit outside periods and show undated records explicitly',t=>{
 const {w,host}=setup(t),anchor=new w.Date(2026,9,27,12),items=[{id:'one',title:'Portrait',start:'2026-10-27',allDay:true},{id:'outside',title:'Future',start:'2026-11-01',allDay:true},{id:'none',title:'Not scheduled'}];
 host.innerHTML=w.StudioCalendarLayouts.render({layout:'calendar',period:'day',anchor,items});
 assert.equal(host.querySelectorAll('.studio-calendar-day').length,1);assert.ok(host.querySelector('[data-calendar-item="one"]'));assert.equal(host.querySelector('[data-calendar-item="outside"]'),null);assert.match(host.querySelector('.studio-calendar-undated').textContent,/Not scheduled/);
});
test('end-exclusive midnight intervals appear only on days actually occupied',t=>{
 const {w,host}=setup(t);host.innerHTML=w.StudioCalendarLayouts.render({layout:'calendar',period:'week',anchor:new w.Date(2026,9,27),items:[{id:'night',title:'Evening',start:'2026-10-27T23:00:00',end:'2026-10-28T00:00:00'}]});
 assert.equal(host.querySelectorAll('[data-calendar-item="night"]').length,1);
});
test('layout controls and item links use callbacks and escape user-supplied labels',t=>{
 const {w,host}=setup(t),seen=[];host.innerHTML=w.StudioCalendarLayouts.switcher('jobs')+w.StudioCalendarLayouts.render({layout:'jobs',period:'day',anchor:new w.Date(2026,9,27),items:[{id:'a',title:'<img src=x>',context:'A & B',start:'2026-10-27',allDay:true}]});
 w.StudioCalendarLayouts.bind(host,{onLayout:value=>seen.push(value),onItem:id=>seen.push(id)});host.querySelector('[data-calendar-layout="calendar"]').click();host.querySelector('[data-calendar-item="a"]').click();assert.deepEqual(seen,['calendar','a']);assert.equal(host.querySelector('img'),null);assert.match(host.textContent,/<img src=x>/);
});

test('production deadlines at the period boundary retain their inclusive due date',t=>{
 const {w,host}=setup(t);host.innerHTML=w.StudioCalendarLayouts.render({layout:'calendar',period:'week',anchor:new w.Date(2026,9,27),items:[{id:'due',title:'Approve',start:'2026-10-25T09:00:00',end:'2026-10-26T00:00:00',inclusiveEnd:true}]});
 assert.equal(host.querySelectorAll('[data-calendar-item="due"]').length,1);
});
