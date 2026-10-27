const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
test('personal inbox filters, opens details and persists read status without channel configuration',async()=>{
 const d=new JSDOM('<body data-studio-view="notifications"><section id="conversationView"></section><section id="genericView"></section></body>',{runScripts:'outside-only',url:'https://studio.test'}),w=d.window,calls=[];
 try{w.conversationView=w.document.querySelector('#conversationView');w.genericView=w.document.querySelector('#genericView');w.esc=v=>String(v??'').replaceAll('<','&lt;');w.StudioPage={setCount(){}};w.json=async(url,options)=>{calls.push(url);return options?null:[{id:'one',channel:'InApp',subject:'Assigned portrait',body:'Please review',eventType:'JobAssigned',referenceType:'Job',referenceId:'job1',isRead:false,createdAtUtc:'2026-10-17T12:00:00Z'},{id:'two',channel:'InApp',subject:'Read message',eventType:'WorkspaceMessage',isRead:true},{id:'email',channel:'Email',subject:'Hidden delivery'}];};require('./studio-page-fixture.cjs')(w);w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/notifications.js'),'utf8'));await w.showNotifications();assert.equal(w.document.querySelectorAll('[data-n-id]').length,2);assert.ok(!calls.includes('/api/email/status'));w.document.querySelector('[data-n-filter="unread"]').click();assert.equal(w.document.querySelectorAll('[data-n-id]').length,1);w.document.querySelector('[data-n-id]').click();assert.match(w.document.querySelector('.notification-detail').textContent,/Please review/);w.document.querySelector('[data-n-read]').click();await new Promise(r=>setImmediate(r));assert.ok(calls.includes('/api/notifications/one/read'));assert.equal(w.document.querySelectorAll('[data-n-id]').length,0);}finally{w.close();}
});


test('selecting a scrolled notification updates the detail without replacing its list',async t=>{
 const dom=new JSDOM('<body data-studio-view="notifications"><div id="conversationView"></div><div id="genericView"></div></body>',{runScripts:'outside-only',url:'https://studio.test',pretendToBeVisual:true}),w=dom.window;t.after(()=>w.close());
 w.conversationView=w.document.getElementById('conversationView');w.genericView=w.document.getElementById('genericView');w.esc=String;
 w.json=async()=>Array.from({length:10},(_,i)=>({id:'n'+i,channel:'InApp',subject:'Notification '+i,body:'Details '+i,eventType:'JobAssigned',createdAtUtc:'2026-10-17T12:00:00Z'}));
 require('./studio-page-fixture.cjs')(w);w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/notifications.js'),'utf8'));
 await w.showNotifications();const list=w.genericView.querySelector('.notification-list'),row=list.querySelector('[data-n-id="n6"]');list.scrollTop=300;row.focus();row.click();
 assert.equal(w.genericView.querySelector('.notification-list'),list);assert.equal(list.querySelector('[data-n-id="n6"]'),row);assert.equal(list.scrollTop,300);assert.equal(w.document.activeElement,row);
 assert.match(w.document.querySelector('.notification-detail').textContent,/Details 6/);assert.equal(row.getAttribute('aria-pressed'),'true');
});

test('message slide-down keeps the feed and job conversation in one scroll region',async t=>{
 const dom=new JSDOM('<body><button id="notificationButton"></button></body>',{runScripts:'outside-only',url:'https://studio.test'}),w=dom.window;t.after(()=>w.close());
 w.fetch=async url=>({ok:true,json:async()=>url.startsWith('/api/messages/inbox')?{items:[{message:{subject:'Proofs ready',body:'Review the gallery'}}]}:[]});
 w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/notifications.js'),'utf8'));
 const panel=w.document.getElementById('studioMessageBellPanel'),conversation=w.document.createElement('form');
 w.studioShellMountJobConversation(conversation);
 assert.equal(panel.querySelector('header h2').textContent,'Messages & Announcements');
 assert.equal(conversation.closest('.studio-message-bell-content'),panel.querySelector('.studio-message-bell-content'));
 await w.document.getElementById('notificationButton').onclick();
 assert.equal(panel.hidden,false);assert.match(panel.querySelector('.studio-message-bell-list').textContent,/Proofs ready/);
 assert.equal(panel.querySelector('footer').parentElement,panel);
 panel.querySelector('[data-bell-close]').click();assert.equal(panel.hidden,true);
});
