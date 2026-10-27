const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const read=name=>fs.readFileSync(path.join(root,name),'utf8');
function fixture(t){
 const dom=new JSDOM('<body><div class="shell"><main><div id="genericView"></div></main></div></body>',{url:'https://studio.test',runScripts:'outside-only'});t.after(()=>dom.window.close());
 const w=dom.window,calls=[],dismissed=new Set();let fail=false;
 w.loadWorkspaces=async()=>[{id:'w',name:'Studio',permissions:{accessWorkspace:true,view:true}},{id:'denied',permissions:{accessWorkspace:false}}];
 w.json=async(url,options)=>{calls.push(url);if(options){if(fail)throw Error('offline');dismissed.add(url.split('/').at(-2));return null;}return [{id:'a',title:'First <notice>',body:'Equipment'},{id:'b',title:'Second',body:'Briefing'}].filter(x=>!dismissed.has(x.id));};
 w.eval(read('announcements.js'));return {w,calls,dismissed,setFail:value=>fail=value};
}
test('login announcements use one horizontal banner, persist dismissals and do not remount on navigation/reload',async t=>{
 const {w,calls,dismissed,setFail}=fixture(t);
 await w.StudioAnnouncements.showAtLogin({userName:'Lisa'});
 const host=w.document.getElementById('studioLoginAnnouncements');assert.ok(host);assert.equal(host.children.length,2);assert.equal(host.parentElement,w.document.querySelector('.shell>main'));assert.match(host.textContent,/First <notice>/);assert.equal(host.querySelector('notice'),null);assert.equal(calls.some(x=>x.includes('denied')),false);
 await w.StudioAnnouncements.showAtLogin({userName:'Lisa'});assert.equal(w.document.querySelectorAll('#studioLoginAnnouncements').length,1);
 const button=host.querySelector('button');setFail(true);await button.onclick();assert.equal(host.children.length,2);assert.equal(button.disabled,false);assert.match(host.textContent,/Could not dismiss/);
 setFail(false);await button.onclick();assert.ok(dismissed.has('a'));assert.equal(host.children.length,1);
 host.remove();w.eval(read('announcements.js'));await w.StudioAnnouncements.showAtLogin({userName:'Lisa'});assert.equal(w.document.getElementById('studioLoginAnnouncements'),null);
 w.sessionStorage.removeItem('studio-announcements-login');w.eval(read('announcements.js'));await w.StudioAnnouncements.showAtLogin({userName:'Lisa'});
 const next=w.document.getElementById('studioLoginAnnouncements');assert.equal(next.children.length,1);assert.match(next.textContent,/Second/);assert.doesNotMatch(next.textContent,/First/);
 await next.querySelector('button').onclick();assert.equal(w.document.getElementById('studioLoginAnnouncements'),null);
 const css=read('studio.css'),rules=css.match(/\.workspace-announcement-banners \{([^}]+)\}/)[1];assert.match(rules,/display: flex/);assert.match(rules,/overflow-x: auto/);assert.match(rules,/flex: 0 0 auto/);assert.match(css,/\.workspace-announcement \{[^}]*flex: 0 0 100%/s);
});
test('announcement startup waits for authenticated identity',async t=>{
 const {w}=fixture(t);let ready;w.studioSignedInUserReady=new Promise(resolve=>ready=resolve);w.eval(read('announcements.js'));assert.equal(w.document.getElementById('studioLoginAnnouncements'),null);ready({userName:'Lisa'});await new Promise(resolve=>setImmediate(resolve));assert.ok(w.document.getElementById('studioLoginAnnouncements'));
});
test('bell requests only unread messages and never loads announcements',async t=>{
 const dom=new JSDOM('<button id="notificationButton"></button>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());const urls=[];
 w.fetch=async url=>{urls.push(url);return {ok:true,json:async()=>({items:[{isUnread:true,message:{subject:'Unread'}},{isUnread:false,message:{subject:'Already read'}}]})};};
 w.eval(read('notifications.js'));await w.document.getElementById('notificationButton').onclick();
 const panel=w.document.getElementById('studioMessageBellPanel');assert.match(panel.textContent,/Unread/);assert.doesNotMatch(panel.textContent,/Already read|Announcements/);assert.deepEqual(urls,['/api/messages/inbox?offset=0&limit=8&unreadOnly=true']);
});
test('Job conversation and notes open from Actions outside the unread slide-down',async t=>{
 const {w}=require('./job-detail-fixture.cjs').environment(t);
 w.eval(read('studio-page.js'));w.eval(read('notifications.js'));w.eval(read('job-workflow-display.js'));w.eval(read('job-workflow-graph.js'));
 await w.openWorkspaceJob('job');await new Promise(resolve=>setImmediate(resolve));
 assert.equal(w.document.querySelector('#studioMessageBellPanel #jobDetailMessages'),null);
 const action=w.document.querySelector('[data-job-action="messages"]');assert.ok(action);action.click();
 await new Promise(resolve=>setImmediate(resolve));
 const dialog=w.document.querySelector('.jd-messages-dialog');assert.ok(dialog?.open);assert.ok(dialog.querySelector('#jobDetailMessages [data-smc-notes-toggle]'));
 dialog.querySelector('[data-close]').click();assert.equal(w.document.querySelector('.jd-messages-dialog'),null);
 action.click();await new Promise(resolve=>setImmediate(resolve));assert.ok(w.document.querySelector('.jd-messages-dialog #jobDetailMessages'));
 w.StudioWorkspace.invalidate();assert.equal(w.document.querySelector('.jd-messages-dialog'),null);
});
