const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
async function environment(t,config={}){
 const dom=new JSDOM('<body data-studio-view="messages"><div id="conversationView"></div><div id="genericView"></div><span id="studioFooterRate"></span></body>',{url:'https://studio.test',runScripts:'outside-only',pretendToBeVisual:true});t.after(()=>dom.window.close());const w=dom.window,calls=[];
 const thread={id:config.legacy?'conversation':'thread',kind:config.legacy?'Conversation':'Thread',scopeType:'Direct',title:'Portrait booking'},message={id:'message',senderUserName:'Client',subject:'Portrait booking',body:'Please confirm the session time.',createdAtUtc:'2026-10-16T12:00:00Z'};
 Object.assign(w,{conversationView:w.document.getElementById('conversationView'),genericView:w.document.getElementById('genericView'),StudioSubjectPicker:{jobOptions:()=>''},CSS:{escape:s=>s},setTimeout:()=>1,setInterval:()=>1,fetch:async(url,options)=>{calls.push({url,options});return {ok:false,status:503};},json:async (url,options)=>{
  calls.push({url,options});
  if(url==='/api/conversations/conversation')return {subject:'Portrait booking',channel:'Email',workspaceId:'workspace',contactName:'Client',contactAddress:'client@example.invalid',messages:[]};
  if(url.endsWith('/sending-identities'))return {defaultIdentity:'Workspace',identities:config.identities||[]};
  if(url.startsWith('/api/messages/inbox'))return {items:[{thread,message}],total:1};
  if(url==='/api/messages/threads')return [thread];if(url==='/api/messages/threads/thread')return [message];
  if(url==='/api/messages/identity')return {displayName:'Studio'};return [];
 }});w.HTMLElement.prototype.scrollIntoView=function(){};
 for(const f of ['studio-message-card.js','studio-page.js','messages.js'])w.eval(fs.readFileSync(path.join(root,f),'utf8'));
 await w.showStudioMessages();return {w,calls};
}
test('Messages puts controls above both panes and keeps only context and attention on the rail',async t=>{
 const {w}=await environment(t),d=w.document,content=d.querySelector('.st-content'),banner=content.querySelector(':scope > .studio-inbox-toolbar'),inbox=content.querySelector(':scope > .st-inbox');
 assert.ok(banner);assert.equal(banner.nextElementSibling,inbox);assert.equal(d.querySelector('.st-board > .st-toolbar'),null);
 assert.equal(d.querySelector('.smc-card'),null);assert.equal(d.querySelectorAll('.st-right > section').length,2);
 assert.match(d.querySelector('.st-right').textContent,/Conversation Context/);assert.match(d.querySelector('.st-right').textContent,/Needs Attention/);
 for(const id of ['studioInboxJobSelect','studioInboxSearch','studioInboxRefresh','studioAddressBook','newDirectMessage'])assert.ok(banner.querySelector('#'+id),id);
 assert.equal(d.getElementById('newDirectMessage').textContent,'New Message');
 assert.ok(inbox.querySelector(':scope > .studio-inbox-center'));assert.ok(inbox.querySelector(':scope > #messageConversationPane'));
 // Default shared pages retain their existing title and message card.
 const template=d.createElement('template');template.innerHTML=w.StudioPage.html({title:'Another page'});assert.ok(template.content.querySelector('.st-toolbar h1'));assert.ok(template.content.querySelector('.smc-card'));
});
test('Selecting a message still renders history, reply controls and conversation context',async t=>{
 const {w,calls}=await environment(t),d=w.document;await d.querySelector('[data-inbox-message]').onclick();
 assert.match(d.getElementById('studioMessageHistory').textContent,/Please confirm the session time/);
 assert.ok(d.getElementById('studioMessageComposer'));assert.ok(d.getElementById('studioMessageBody'));assert.match(d.getElementById('studioMessageContext').textContent,/Portrait booking/);
 assert.ok(calls.some(c=>c.url==='/api/messages/threads/thread/read'));assert.equal(d.querySelector('.smc-card'),null);
});

test('Legacy reply split button selects permitted sender without sending until submitted',async t=>{
 const {w,calls}=await environment(t,{legacy:true,identities:[{type:'Workspace',label:'Workspace — studio@example.invalid',isDefault:true},{type:'Self',label:'Myself — photographer@example.invalid'}]}),d=w.document;
 await d.querySelector('[data-inbox-message]').onclick();
 const form=d.getElementById('legacyMessageComposer'),mode=form.elements.replyMode,button=form.querySelector('[type=submit]');
 assert.equal(d.getElementById('openConversationWorkspace'),null);
 assert.equal(mode.value,'Workspace');assert.equal(button.textContent,'Send as Workspace');assert.equal(form.querySelector('input[name="internal"]'),null);
 assert.deepEqual([...mode.options].map(o=>o.value),['Workspace','Self','Internal']);assert.equal(mode.parentElement,button.parentElement);
 mode.value='Self';mode.dispatchEvent(new w.Event('change'));assert.equal(button.textContent,'Send as Myself');
 const submissions=()=>calls.filter(c=>c.url==='/api/conversations/conversation/messages'&&c.options?.method==='POST');
 assert.equal(submissions().length,0);form.elements.body.value='Test reply';await form.onsubmit({preventDefault(){},target:form});
 const payload=JSON.parse(submissions()[0].options.body);assert.equal(payload.sendingIdentity,'Self');assert.equal(payload.isInternal,false);
});
test('Internal note is a split-button choice and absence of Self permission never offers it',async t=>{
 const {w,calls}=await environment(t,{legacy:true,identities:[{type:'Workspace',label:'Workspace',isDefault:true}]}),d=w.document;await d.querySelector('[data-inbox-message]').onclick();
 const form=d.getElementById('legacyMessageComposer'),mode=form.elements.replyMode;
 assert.equal([...mode.options].some(o=>o.value==='Self'),false);mode.value='Internal';mode.dispatchEvent(new w.Event('change'));
 assert.equal(form.querySelector('[type=submit]').textContent,'Add internal note');form.elements.body.value='Internal test note';await form.onsubmit({preventDefault(){},target:form});
 const post=calls.find(c=>c.url==='/api/conversations/conversation/messages'&&c.options?.method==='POST');assert.equal(JSON.parse(post.options.body).isInternal,true);
});
test('Unconfigured sender options still provide a usable Reply and Internal note menu',async t=>{
 const {w}=await environment(t,{legacy:true}),d=w.document;await d.querySelector('[data-inbox-message]').onclick();
 const mode=d.getElementById('legacyMessageComposer').elements.replyMode;assert.deepEqual([...mode.options].map(o=>o.textContent),['Reply','Internal note']);assert.equal(mode.value,'Workspace');
});
