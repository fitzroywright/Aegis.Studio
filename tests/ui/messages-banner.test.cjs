const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
async function environment(t){
 const dom=new JSDOM('<body data-studio-view="messages"><div id="conversationView"></div><div id="genericView"></div><span id="studioFooterRate"></span></body>',{url:'https://studio.test',runScripts:'outside-only',pretendToBeVisual:true});t.after(()=>dom.window.close());const w=dom.window,calls=[];
 const thread={id:'thread',scopeType:'Direct',title:'Portrait booking'},message={id:'message',senderUserName:'Client',subject:'Portrait booking',body:'Please confirm the session time.',createdAtUtc:'2026-10-16T12:00:00Z'};
 Object.assign(w,{conversationView:w.document.getElementById('conversationView'),genericView:w.document.getElementById('genericView'),StudioSubjectPicker:{jobOptions:()=>''},CSS:{escape:s=>s},setTimeout:()=>1,setInterval:()=>1,fetch:async(url,options)=>{calls.push({url,options});return {ok:false,status:503};},json:async url=>{
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
