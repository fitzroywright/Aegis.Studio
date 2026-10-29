const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot'),settle=()=>new Promise(resolve=>setImmediate(resolve));
function setup(t){
 const dom=new JSDOM('<body><main></main></body>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());
 const fixture={calls:[],revision:7,failSave:false,failSessions:false,failHours:false};
 fixture.sessions=[{id:'portrait',name:'Portrait',description:'Family portrait session',durationMinutes:60,setupBufferMinutes:10,cleanupBufferMinutes:15,priceMinor:1250000,depositMinor:250000,currency:'JMD',locations:['BK','BE'],resourceRequirements:{Photographer:1,'Room-BK':1},isActive:true,isPublished:true,defaultWorkspaceId:'w'}];
 fixture.hours=[{locationCode:'BK',dayOfWeek:1,opensAt:'09:00',closesAt:'17:00',slotStepMinutes:30}];
 w.confirm=()=>true;w.HTMLElement.prototype.scrollIntoView=function(){};
 w.StudioPage={escape:value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))};
 w.studioDialog=(title,body)=>{const d=w.document.createElement('dialog');d.innerHTML=body;d.close=()=>d.remove();w.document.body.append(d);return d;};
 w.json=async(url,options)=>{
  fixture.calls.push({url,options});if(options?.method==='PATCH'){
   if(fixture.publishDelay)await fixture.publishDelay;if(fixture.failPublish)throw Error('Publication unavailable');
   const value=JSON.parse(options.body);if(value.expectedRevision!==fixture.revision)throw Error('Booking settings changed. Reload.');
   const row=fixture.sessions.find(item=>url.includes('/'+item.id+'/publication'));if(!row)throw Error('Save this service first');
   if(value.isPublished&&!row.isActive)throw Error('Activate and save this service first');
   row.isPublished=value.isPublished;return{revision:++fixture.revision,sessionType:JSON.parse(JSON.stringify(row))};
  }if(options?.method==='PUT'){
   if(fixture.saveDelay)await fixture.saveDelay;if(fixture.failSave)throw Error('Database unavailable');const value=JSON.parse(options.body);fixture.revision++;
   if(url.endsWith('/session-types/')){fixture.sessions=value;return{revision:fixture.revision,sessionTypes:value};}fixture.hours=value;return{revision:fixture.revision};
  }
  if(url.endsWith('/session-types/')){if(fixture.failSessions)throw Error('Catalog unavailable');return{revision:fixture.revision,sessionTypes:fixture.sessions};}
  if(url.endsWith('/hours')){if(fixture.failHours)throw Error('Hours unavailable');return fixture.hours;}
  if(url.endsWith('/workspaces'))return[{id:'w',name:'Portrait Team'}];if(url==='/api/schedule/resources')return[{resourceType:'Room-BK',isActive:true}];throw Error(url);
 };
 w.eval(fs.readFileSync(path.join(root,'booking-sessions.js'),'utf8'));const host=w.document.querySelector('main');host.innerHTML=w.StudioBookingSessions.html();
 return{w,fixture,host,mount:()=>w.StudioBookingSessions.mount(host,()=>host.isConnected)};
}
function input(w,selector,value){const field=w.document.querySelector(selector);if(field.type==='checkbox')field.checked=value;else field.value=value;field.dispatchEvent(new w.Event('input',{bubbles:true}));return field;}
const writes=fixture=>fixture.calls.filter(call=>call.options?.method==='PUT');
test('session grid directly edits commercial fields without losing stable IDs or advanced settings',async t=>{
 const {w,fixture,mount}=setup(t);await mount();assert.equal(w.document.querySelectorAll('.booking-session-grid tbody tr').length,1);assert.equal(w.document.querySelector('[data-field=price]').value,'12500');assert.ok(w.document.querySelector('[data-booking-editor] textarea'));assert.equal(w.document.querySelector('dialog'),null);assert.equal(w.document.querySelector('[data-booking-save]').disabled,true);
 input(w,'[data-field=price]','13000.25');input(w,'[data-field=deposit]','3000.50');input(w,'[data-field=name]','Family Portrait');input(w,'[data-field=durationMinutes]','90');
 w.document.querySelector('[data-booking-save]').click();await settle();const body=JSON.parse(writes(fixture)[0].options.body);assert.equal(body[0].priceMinor,1300025);assert.equal(body[0].depositMinor,300050);assert.equal(body[0].id,'portrait');assert.equal(body[0].durationMinutes,90);assert.equal(body[0].isPublished,true);assert.equal(body[0].setupBufferMinutes,10);assert.equal(body[0].defaultWorkspaceId,'w');assert.equal(body[0].resourceRequirements['Room-BK'],1);assert.equal(body[0].price,undefined);assert.match(w.document.querySelector('[data-booking-status]').textContent,/saved in Studio/);
});
test('selected session details edit inline and preserve other grid edits',async t=>{
 const {w,fixture,mount}=setup(t);await mount();input(w,'[data-field=price]','15000');w.document.querySelector('[data-booking-details]').click();const form=w.document.querySelector('[data-booking-editor] form');assert.equal(form.elements.workspace.value,'w');
 input(w,'[name=description]','New description');input(w,'[name=setup]','20');input(w,'[name=cleanup]','25');input(w,'[data-resource-count]','2');
 assert.equal(w.document.querySelector('dialog'),null);assert.equal(writes(fixture).length,0);assert.equal(w.document.querySelector('[data-field=price]').value,'15000');
 form.onsubmit({preventDefault(){}});await settle();const row=JSON.parse(writes(fixture)[0].options.body)[0];assert.equal(row.setupBufferMinutes,20);assert.equal(row.cleanupBufferMinutes,25);assert.equal(row.resourceRequirements.Photographer,2);assert.equal(row.description,'New description');
});
test('online booking toggle saves just publication immediately and preserves unrelated drafts',async t=>{
 const {w,fixture,mount}=setup(t);await mount();input(w,'[data-field=price]','15000');
 input(w,'[data-field=isPublished]',false);await settle();
 const patch=fixture.calls.find(call=>call.options?.method==='PATCH');assert.ok(patch);
 assert.equal(patch.url,'/api/admin/public-booking/session-types/portrait/publication');assert.deepEqual(JSON.parse(patch.options.body),{isPublished:false,expectedRevision:7});
 assert.equal(fixture.sessions[0].priceMinor,1250000);assert.equal(fixture.sessions[0].isPublished,false);
 assert.equal(w.document.querySelector('[data-field=price]').value,'15000');assert.equal(w.document.querySelector('[data-field=isPublished]').checked,false);
 assert.equal(w.document.querySelector('[data-booking-save]').disabled,false);
 w.document.querySelector('[data-booking-save]').click();await settle();assert.equal(fixture.sessions[0].priceMinor,1500000);assert.equal(fixture.sessions[0].isPublished,false);
 input(w,'[data-field=isPublished]',true);await settle();assert.equal(fixture.sessions[0].isPublished,true);
 w.document.querySelector('[data-booking-reload]').click();await settle();assert.equal(w.document.querySelector('[data-field=isPublished]').checked,true);
});
test('online toggle failure restores the saved value, blocks duplicate writes and retains drafts',async t=>{
 const {w,fixture,mount}=setup(t);await mount();input(w,'[data-field=price]','14000');fixture.failPublish=true;
 let release;fixture.publishDelay=new Promise(resolve=>release=resolve);
 input(w,'[data-field=isPublished]',false);assert.equal(w.document.querySelector('[data-field=isPublished]').disabled,true);
 w.document.querySelector('[data-field=isPublished]').click();assert.equal(fixture.calls.filter(call=>call.options?.method==='PATCH').length,1);
 release();await settle();assert.equal(w.document.querySelector('[data-field=isPublished]').checked,true);assert.equal(w.document.querySelector('[data-field=price]').value,'14000');
 assert.match(w.document.querySelector('[data-booking-status]').textContent,/could not be confirmed/);
 fixture.failPublish=false;input(w,'[data-field=isPublished]',false);await settle();assert.equal(fixture.sessions[0].isPublished,false);
});
test('new services cannot pretend publication succeeded before their first save',async t=>{
 const {w,fixture,mount}=setup(t);await mount();w.document.querySelector('[data-booking-add]').click();
 const row=w.document.querySelectorAll('[data-booking-session]')[1],toggle=row.querySelector('[data-field=isPublished]');
 toggle.checked=true;toggle.dispatchEvent(new w.Event('input',{bubbles:true}));await settle();
 assert.equal(toggle.checked,false);assert.equal(fixture.calls.filter(call=>call.options?.method==='PATCH').length,0);
 assert.match(w.document.querySelector('[data-booking-status]').textContent,/Save this new service/);
});
test('category and location filters retain all session rows and inline cancel preserves price edits',async t=>{
 const {w,fixture,mount}=setup(t);await mount();input(w,'[data-field=category]','Portrait');input(w,'[data-field=price]','14000');input(w,'[name=description]','Temporary');
 w.document.querySelector('[data-details-cancel]').click();assert.equal(w.document.querySelector('[name=description]').value,'Family portrait session');assert.equal(w.document.querySelector('[data-field=price]').value,'14000');
 w.document.querySelector('[data-booking-add]').click();const rows=w.document.querySelectorAll('[data-booking-session]');const last=rows[1];input(w,'[data-booking-session="'+last.dataset.bookingSession+'"] [data-field=name]','Graduation');input(w,'[data-booking-session="'+last.dataset.bookingSession+'"] [data-field=category]','Graduation');
 const category=w.document.querySelector('[data-booking-category]');category.innerHTML+='<option value="Portrait">Portrait</option>';category.value='Portrait';category.dispatchEvent(new w.Event('change'));
 assert.equal(w.document.querySelectorAll('[data-booking-session]').length,1);w.document.querySelector('[data-booking-save]').click();await settle();assert.equal(fixture.sessions.length,2);assert.equal(fixture.sessions[0].category,'Portrait');
});
test('catalog and hours drafts survive tab changes and are saved independently',async t=>{
 const {w,fixture,mount}=setup(t);await mount();input(w,'[data-field=name]','Wedding');w.document.querySelector('[data-booking-tab=hours]').click();assert.equal(w.document.querySelector('[data-booking-save]').disabled,true);input(w,'[data-field=opensAt]','10:00');w.document.querySelector('[data-booking-save]').click();await settle();assert.equal(writes(fixture)[0].url,'/api/admin/public-booking/hours');assert.equal(JSON.parse(writes(fixture)[0].options.body)[0].opensAt,'10:00');w.document.querySelector('[data-booking-tab=sessions]').click();assert.equal(w.document.querySelector('[data-field=name]').value,'Wedding');assert.equal(w.document.querySelector('[data-booking-save]').disabled,false);
});
test('invalid deposits and overlapping opening windows block writes',async t=>{
 const {w,fixture,mount}=setup(t);await mount();input(w,'[data-field=deposit]','99999');w.document.querySelector('[data-booking-save]').click();await settle();assert.equal(writes(fixture).length,0);assert.match(w.document.querySelector('[data-booking-status]').textContent,/deposit/);w.document.querySelector('[data-booking-tab=hours]').click();w.document.querySelector('[data-booking-add]').click();w.document.querySelector('[data-booking-save]').click();await settle();assert.equal(writes(fixture).length,0);assert.match(w.document.querySelector('[data-booking-status]').textContent,/overlap/);
});
test('failures preserve edits and prevent double-submit until a retry succeeds',async t=>{
 const {w,fixture,mount}=setup(t);await mount();input(w,'[data-field=name]','Draft');fixture.failSave=true;let release;fixture.saveDelay=new Promise(resolve=>release=resolve);const save=w.document.querySelector('[data-booking-save]');save.click();save.click();assert.equal(writes(fixture).length,1);release();await settle();assert.equal(w.document.querySelector('[data-field=name]').value,'Draft');assert.match(w.document.querySelector('[data-booking-status]').textContent,/Database unavailable/);assert.equal(save.disabled,false);fixture.failSave=false;save.click();await settle();assert.equal(writes(fixture).length,2);assert.equal(save.disabled,true);
});
test('failed catalog reads cannot be saved as an empty catalog while hours remain usable',async t=>{
 const {w,fixture,mount}=setup(t);fixture.failSessions=true;await mount();assert.match(w.document.querySelector('[data-booking-grid]').textContent,/could not be loaded/);assert.equal(w.document.querySelector('[data-booking-add]').disabled,true);assert.equal(w.document.querySelector('[data-booking-save]').disabled,true);w.document.querySelector('[data-booking-tab=hours]').click();assert.ok(w.document.querySelector('.booking-hours-grid'));assert.equal(w.document.querySelector('[data-booking-add]').disabled,false);
});
test('adding, removing and filtering sessions preserves the other draft rows',async t=>{
 const {w,fixture,mount}=setup(t);await mount();w.document.querySelector('[data-booking-add]').click();const rows=w.document.querySelectorAll('[data-booking-session]');assert.equal(rows.length,2);input(w,'[data-booking-session="'+rows[1].dataset.bookingSession+'"] [data-field=name]','Graduation');input(w,'[data-booking-search]','Graduation');assert.equal(w.document.querySelectorAll('[data-booking-session]').length,1);w.document.querySelector('[data-booking-save]').click();await settle();assert.equal(fixture.sessions.length,2);assert.equal(fixture.sessions[0].name,'Portrait');w.document.querySelector('[data-booking-remove]').click();assert.equal(writes(fixture).length,1);w.document.querySelector('[data-booking-save]').click();await settle();assert.equal(fixture.sessions.length,1);assert.equal(fixture.sessions[0].id,'portrait');
});
