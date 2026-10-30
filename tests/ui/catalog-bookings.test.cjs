const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot'),read=name=>fs.readFileSync(path.join(root,name),'utf8'),settle=()=>new Promise(resolve=>setImmediate(resolve));
function setup(t,url='https://studio.test/#studio=catalog'){
 const dom=new JSDOM('<body data-studio-view="catalog"><section id="conversationView"></section><main id="genericView"></main></body>',{url,runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());
 const fixture={calls:[],admin:true,items:[{id:'print',code:'P1',name:'Print',category:'Prints',unitPrice:500,estimatedCost:100,taxRate:0,isActive:true,requiresProduction:true}],sessions:[{id:'portrait',name:'Portrait',description:'Portrait session',durationMinutes:60,setupBufferMinutes:10,cleanupBufferMinutes:0,priceMinor:100000,depositMinor:20000,currency:'JMD',locations:['BK'],resourceRequirements:{Photographer:1},isActive:true,isPublished:false,defaultWorkspaceId:null}]};
 Object.assign(w,{genericView:w.document.getElementById('genericView'),conversationView:w.document.getElementById('conversationView'),money:String,renderWorkspaceMenu(){},confirm:()=>true});
 w.HTMLElement.prototype.scrollIntoView=function(){};
 w.json=async(url,options)=>{fixture.calls.push({url,options});if(url==='/api/admin/public-booking/session-types/settings')return{bookingUrl:'https://web.bryanstudiosltd.com/#booking',publishingEnabled:true};if(url==='/api/auth/me')return{user:{isAdministrator:fixture.admin}};if(url==='/api/admin/pos/catalog/print'&&options?.method==='PUT'){fixture.items=[{id:'print',...JSON.parse(options.body)}];return fixture.items[0];}if(url==='/api/admin/pos/catalog/')return fixture.items;if(url==='/api/admin/commercial/templates/WorkOrder')return[];if(url==='/api/admin/public-booking/session-types/'){if(options?.method==='PUT')fixture.sessions=JSON.parse(options.body);return{revision:5,sessionTypes:fixture.sessions};}if(url.endsWith('/hours')||url.endsWith('/workspaces')||url==='/api/schedule/resources')return[];throw Error(url);};
 for(const file of ['studio-page.js','booking-sessions.js','commercial.js','studio-admin.js'])w.eval(read(file));w.esc=w.StudioPage.escape;
 w.studioDialog=(title,html)=>{const dialog=w.document.createElement('dialog');dialog.innerHTML='<h2>'+w.esc(title)+'</h2>'+html;dialog.close=()=>dialog.remove();w.document.body.append(dialog);return dialog;};
 return {w,fixture};
}
test('Booking Sessions shares Products and Services and retains unsaved edits across catalog tabs',async t=>{
 const {w,fixture}=setup(t);await w.showCommercialCatalog({tab:'booking-sessions'});assert.equal(w.document.querySelectorAll('h1').length,0);assert.equal(w.document.querySelector('.st-page').getAttribute('aria-label'),'Products and Services');const panel=w.document.querySelector('[data-catalog-panel="booking-sessions"]');assert.equal(panel.hidden,false);assert.equal(w.document.querySelector('[data-add]').hidden,false);
 const input=panel.querySelector('[data-field=name]');input.value='Wedding';input.dispatchEvent(new w.Event('input',{bubbles:true}));w.document.querySelector('[data-catalog-tab=products]').click();await settle();assert.equal(w.document.querySelector('[data-catalog-panel=products]').hidden,false);assert.match(w.document.querySelector('[data-item]').textContent,/Print/);assert.equal(w.document.querySelector('[data-add]').hidden,false);
 w.document.querySelector('[data-catalog-tab="booking-sessions"]').click();await settle();assert.equal(panel.querySelector('[data-field=name]').value,'Wedding');assert.equal(panel.querySelector('[data-booking-save]').disabled,false);assert.equal(fixture.calls.filter(call=>call.url==='/api/admin/public-booking/session-types/').length,1);panel.querySelector('[data-booking-save]').click();await settle();assert.equal(fixture.sessions[0].name,'Wedding');assert.equal(w.history.state.studioCatalogTab,'booking-sessions');
});
test('catalog product editing keeps the existing save service and item identity',async t=>{
 const {w,fixture}=setup(t);await w.showCommercialCatalog({tab:'products'});w.document.querySelector('[data-item]').click();const form=w.document.querySelector('dialog form');form.elements.price.value='750';await form.onsubmit({preventDefault(){},currentTarget:form});const write=fixture.calls.find(call=>call.options?.method==='PUT');assert.equal(write.url,'/api/admin/pos/catalog/print');assert.equal(JSON.parse(write.options.body).unitPrice,750);assert.equal(w.document.querySelector('[data-catalog-panel=products]').hidden,false);
});
test('booking settings still deny non-administrators before requesting the catalog or hours',async t=>{
 const {w,fixture}=setup(t);fixture.admin=false;await w.showCommercialCatalog({tab:'booking-sessions'});assert.match(w.document.querySelector('[data-catalog-panel="booking-sessions"]').textContent,/Administrator access required/);assert.equal(w.document.querySelector('[data-booking-save]'),null);assert.equal(fixture.calls.some(call=>call.url.startsWith('/api/admin/public-booking')),false);
});
test('old Booking Sessions bookmarks and browser back restore the catalog booking tab',async t=>{
 const {w}=setup(t,'https://studio.test/#studio=booking-sessions');w.StudioClients={render:async()=>{},invalidate(){}};w.eval(read('studio-navigation.js'));w.document.dispatchEvent(new w.Event('DOMContentLoaded'));await settle();assert.equal(w.document.body.dataset.studioView,'catalog');assert.equal(w.history.state.studioCatalogTab,'booking-sessions');assert.equal(w.document.querySelector('[data-catalog-panel="booking-sessions"]').hidden,false);
 await w.studioNavigate('clients');const restored=new Promise(resolve=>w.addEventListener('popstate',resolve,{once:true}));w.history.back();await restored;await new Promise(resolve=>setImmediate(resolve));assert.equal(w.document.body.dataset.studioView,'catalog');assert.equal(w.history.state.studioCatalogTab,'booking-sessions');assert.equal(w.document.querySelector('[data-catalog-panel="booking-sessions"]').hidden,false);
});
test('approved catalog layout has one tab strip, real hours and an inline editor',async t=>{
 const {w,fixture}=setup(t);await w.showCommercialCatalog();
 assert.equal(w.document.querySelectorAll('[data-catalog-tab]').length,3);assert.equal(w.document.querySelectorAll('.booking-tabs').length,1);
 assert.equal(w.document.querySelector('[data-catalog-tab="booking-sessions"]').getAttribute('aria-pressed'),'true');
 assert.ok(w.document.querySelector('[data-booking-editor] form'));assert.equal(w.document.querySelector('dialog'),null);
 assert.equal(w.document.querySelector('[data-catalog-preview]'),null);
 assert.match(w.document.querySelector('[data-catalog-publication]').textContent,/Automatic publishing enabled/);
 assert.doesNotMatch(w.document.querySelector('[data-catalog-publication]').textContent,/up to date/);
 w.document.querySelector('[data-catalog-hours]').click();await settle();assert.equal(w.history.state.studioCatalogTab,'hours');assert.ok(w.document.querySelector('.booking-hours-grid'));
 assert.equal(w.document.querySelector('[data-add]').textContent,'＋ Add opening window');
 w.document.querySelector('[data-catalog-tab="products"]').click();await settle();assert.equal(w.document.querySelector('[data-catalog-panel="products"]').hidden,false);
 assert.equal(fixture.calls.filter(call=>call.url==='/api/admin/public-booking/session-types/').length,1);
});

