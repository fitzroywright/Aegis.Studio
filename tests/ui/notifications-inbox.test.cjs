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

test('message slide-down contains only its unread feed',async t=>{
 const dom=new JSDOM('<body><button id="notificationButton"></button></body>',{runScripts:'outside-only',url:'https://studio.test'}),w=dom.window;t.after(()=>w.close());
 w.fetch=async url=>({ok:true,json:async()=>url.startsWith('/api/messages/inbox')?{items:[{isUnread:true,message:{subject:'Proofs ready',body:'Review the gallery'}}]}:[]});
 w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/notifications.js'),'utf8'));
 const panel=w.document.getElementById('studioMessageBellPanel');
 assert.equal(panel.querySelector('header h2').textContent,'Unread Messages');
 assert.ok(w.document.querySelector('#notificationButton #messageUnreadBadge.studio-message-bell-count'));
 assert.equal(panel.querySelector('#studioShellJobConversation,.smc-card'),null);
 await w.document.getElementById('notificationButton').onclick();
 assert.equal(panel.hidden,false);assert.match(panel.querySelector('.studio-message-bell-list').textContent,/Proofs ready/);
 assert.equal(panel.querySelector('footer').parentElement,panel);
 panel.querySelector('[data-bell-close]').click();assert.equal(panel.hidden,true);
});


test('Job Details bell requests only current-job messages, including read messages',async t=>{
 const dom=new JSDOM('<body data-studio-view="workspaces"><section id="conversationView" class="has-open-job"><div id="detailPane" class="job-detail-page" data-job-id="job-123"></div></section><button id="notificationButton"></button></body>',{runScripts:'outside-only',url:'https://studio.test'}),w=dom.window;t.after(()=>w.close());
 const requests=[],opened=[];
 w.fetch=async url=>{requests.push(url);return {ok:true,json:async()=>({items:[
  {isUnread:false,thread:{scopeType:'Job',scopeId:'job-123'},message:{subject:'Job update',body:'Ready for client'}},
  {isUnread:true,thread:{scopeType:'Job',scopeId:'other-job'},message:{subject:'Unrelated',body:'Not this job'}}
 ]})};};
 w.openStudioJobMessages=async id=>opened.push(id);
 w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/notifications.js'),'utf8'));
 await w.document.getElementById('notificationButton').onclick();
 const panel=w.document.getElementById('studioMessageBellPanel');
 assert.match(requests[0],/jobId=job-123/);
 assert.doesNotMatch(requests[0],/unreadOnly/);
 assert.equal(panel.querySelector('header h2').textContent,'Job Messages');
 assert.match(panel.textContent,/Job update/);
 assert.doesNotMatch(panel.textContent,/Unrelated/);
 panel.querySelector('.studio-message-bell-list button').click();
 assert.deepEqual(opened,['job-123']);
});

test('Clicking a bell preview opens the exact message, not just the job conversation',async t=>{
 const dom=new JSDOM('<body data-studio-view="workspaces"><section id="conversationView" class="has-open-job"><div id="detailPane" class="job-detail-page" data-job-id="job-123"></div></section><button id="notificationButton"></button></body>',{runScripts:'outside-only',url:'https://studio.test'}),w=dom.window;t.after(()=>w.close());
 const opened=[];
 w.fetch=async()=>({ok:true,json:async()=>({items:[
  {isUnread:false,thread:{id:'thread-5',kind:'Thread',scopeType:'Job',scopeId:'job-123'},message:{id:'message-42',subject:'Proof approved',body:'Ready to print'}}
 ]})});
 w.openStudioInboxMessage=async(...args)=>opened.push(args);
 w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/notifications.js'),'utf8'));
 await w.document.getElementById('notificationButton').onclick();
 w.document.querySelector('.studio-message-bell-list button').click();
 assert.deepEqual(opened,[['thread-5','message-42','job-123','Thread']]);
});
