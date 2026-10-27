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
 assert.ok(host.querySelector('.workspace-announcement-symbol svg'));assert.equal(host.querySelector('.workspace-announcement-kicker').textContent,'Staff announcement');assert.equal(host.querySelector('h2').textContent,'First <notice>');assert.match(host.querySelector('.workspace-announcement-position').textContent,/1 of 2.*Click for next/);
 const button=host.querySelector('button');setFail(true);await button.onclick();assert.equal(host.children.length,2);assert.equal(button.disabled,false);assert.match(host.textContent,/Could not dismiss/);
 setFail(false);await button.onclick();assert.ok(dismissed.has('a'));assert.equal(host.children.length,1);assert.equal(host.querySelector('.workspace-announcement-position').textContent,'1 of 1');
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
test('Messages composer creates only staff workspace announcements using its dedicated endpoint',async t=>{
 const {w,calls}=fixture(t);
 w.document.querySelector('main').innerHTML='<div class="studio-inbox-tools"></div>';
 w.loadWorkspaces=async()=>[{id:'staff',name:'Bryan’s staff',permissions:{accessWorkspace:true,view:true,notifyMembers:true}},{id:'restricted',name:'Restricted',permissions:{accessWorkspace:true,view:true,notifyMembers:false}}];
 w.studioDialog=(title,html)=>{const dialog=w.document.createElement('dialog');dialog.innerHTML=html;w.document.body.append(dialog);return dialog;};
 let payload;w.json=async(url,options)=>{calls.push(url);payload=JSON.parse(options.body);};
 const toolbar=w.document.querySelector('.studio-inbox-tools');await w.StudioAnnouncements.attachComposer(toolbar,()=>true);
 toolbar.querySelector('[data-post-announcement]').click();const form=w.document.querySelector('dialog form');
 assert.equal(form.elements.workspace.options.length,1);assert.equal(form.elements.workspace.value,'staff');assert.match(w.document.querySelector('dialog').textContent,/not sent to clients/);
 form.elements.title.value='Tomorrow';form.elements.body.value='Team briefing';await form.onsubmit({preventDefault(){}});
 assert.deepEqual(calls,['/api/workspaces/staff/announcements']);assert.deepEqual(payload,{title:'Tomorrow',body:'Team briefing'});
 assert.equal(form.querySelector('button').disabled,false);assert.match(form.textContent,/posted/);
 assert.doesNotMatch(read('job-workspace.js'),/renderWorkspaceAnnouncements|renderAnnouncementAdministration|announcement-form/);
 assert.doesNotMatch(read('studio-navigation.js'),/renderAnnouncementAdministration/);
 assert.match(read('messages.js'),/StudioAnnouncements\?\.attachComposer/);
});
test('staff without announcement permission receive no creation control',async t=>{
 const {w}=fixture(t);const toolbar=w.document.createElement('div');w.document.body.append(toolbar);
 await w.StudioAnnouncements.attachComposer(toolbar,()=>true);assert.equal(toolbar.children.length,0);
});
test('bell uses the same icon and adjacent count treatment as Upcoming Sessions',t=>{
 const dom=new JSDOM('<button id="notificationButton" class="action icon-action"><svg><use href="#bell"></use></svg></button>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());
 w.eval(read('notifications.js'));const button=w.document.getElementById('notificationButton'),count=button.querySelector('strong');
 assert.equal(button.className,'studio-attention-trigger');assert.ok(button.querySelector('svg.studio-status-icon'));assert.equal(count.id,'messageUnreadBadge');assert.equal(count.previousElementSibling.tagName.toLowerCase(),'svg');
 assert.doesNotMatch(read('studio.css'),/\.studio-message-bell-count\s*\{/);
});
test('clicking announcements scrolls their strip, wraps around and keeps dismissal separate',async t=>{
 const {w}=fixture(t);w.matchMedia=()=>({matches:false});await w.StudioAnnouncements.showAtLogin({userName:'Lisa'});
 const host=w.document.getElementById('studioLoginAnnouncements'),slides=[...host.children],moves=[];
 slides.forEach((slide,index)=>Object.defineProperty(slide,'offsetLeft',{get:()=>20+index*600}));
 host.scrollTo=options=>{moves.push(options);host.scrollLeft=options.left;};
 slides[0].querySelector('h2').click();assert.equal(moves.at(-1).left,600);assert.equal(moves.at(-1).behavior,'smooth');
 slides[1].querySelector('p').click();assert.equal(moves.at(-1).left,0);
 host.dispatchEvent(new w.KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true,cancelable:true}));assert.equal(moves.at(-1).left,600);
 w.matchMedia=()=>({matches:true});host.dispatchEvent(new w.KeyboardEvent('keydown',{key:'ArrowLeft',bubbles:true,cancelable:true}));assert.equal(moves.at(-1).left,0);assert.equal(moves.at(-1).behavior,'auto');
 const before=moves.length;slides[0].querySelector('button').click();await new Promise(resolve=>setImmediate(resolve));assert.equal(host.children.length,1);assert.equal(moves.length,before);assert.equal(host.dataset.multiple,'false');
 host.querySelector('h2').click();assert.equal(moves.length,before);
 const css=read('studio.css');assert.match(css,/\.workspace-announcement-banners \{[^}]*overflow-x: auto;[^}]*scrollbar-width: none;/s);assert.match(css,/\.workspace-announcement-banners::-webkit-scrollbar \{ display:none; \}/);
});
