const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const source=fs.readFileSync(path.join(__dirname,'../../Aegis.Studio.Web/wwwroot/messages.js'),'utf8');

function fixture(){
    const dom=new JSDOM('<div id="conversationView"></div><div id="genericView"></div>',{url:'https://studio.test/',runScripts:'outside-only'});
    const w=dom.window,requests=[];
    w.genericView=w.document.getElementById('genericView');w.conversationView=w.document.getElementById('conversationView');
    w.esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    w.StudioPage={begin:()=>()=>true,html:o=>o.content,bind(){},setCount(){}};
    w.CSS={escape:v=>String(v)};w.Element.prototype.scrollIntoView=function(){};
    w.fetch=async()=>({ok:false,status:403});w.studioFirstName=v=>String(v??'');w.studioRememberPeople=()=>{};w.formatBytes=v=>String(v);
    w.json=async url=>{
        requests.push(url);
        if(url.startsWith('/api/messages/inbox'))return {total:1,items:[{thread:{id:'thread-1',scopeType:'Job',scopeId:'job-1',title:'Wedding',kind:'Thread'},message:{id:'message-1',senderUserName:'paul',body:'Private message',subject:'Handoff',createdAtUtc:'2026-10-11T16:00:00Z',recipientAddresses:['tamara@studio.test']}}]};
        if(url==='/api/jobs')return [{id:'job-1',title:'Wedding',status:'Production'}];
        if(url==='/api/messages/address-book')return [{id:'job-1',scopeId:'job-1',contactType:'Job',displayName:'Wedding',status:'Active',deliveryMode:'InternalOnly'}];
        if(url==='/api/messages/threads')return [{id:'thread-1',scopeType:'Job',scopeId:'job-1',title:'Wedding'}];
        if(url==='/api/messages/threads/thread-1')return [{id:'message-1',senderUserName:'paul',subject:'Handoff',body:'Private message',createdAtUtc:'2026-10-11T16:00:00Z'}];
        if(url.endsWith('/deliveries'))return [{messageId:'message-1',recipient:'client@outside.test',transport:'Email',status:'Attention',detail:'ProviderOutcomeUncertain: lost response',deliveryId:'delivery-1'}];
        if(url.endsWith('/recipients'))return [];
        if(url==='/api/messages/identity')return {displayName:'Paul',emailAddress:'paul@studio.test'};
        return [];
    };
    for(const name of ['studio-status-order.js','subject-picker.js','studio-email-body.js','studio-message-card.js'])w.eval(fs.readFileSync(path.join(__dirname,'../../Aegis.Studio.Web/wwwroot',name),'utf8'));
    w.ResizeObserver=class{observe(){}disconnect(){}};w.requestAnimationFrame=fn=>w.setTimeout(fn,0);
    w.eval(source);return {dom,w,requests};
}

test('Inbox loads one bounded server page instead of fetching every conversation history',async()=>{
    const f=fixture();try{await f.w.showStudioMessages();assert(f.requests.some(url=>url.startsWith('/api/messages/inbox?')));assert(!f.requests.some(url=>/\/threads\//.test(url)));assert.match(f.w.document.getElementById('studioInboxRows').textContent,/Tamara|tamara@studio.test/);}finally{f.dom.window.close();}
});
test('Job filter is sent to the server and preserves the shared Messages page',async()=>{
    const f=fixture();try{await f.w.showStudioMessages();const select=f.w.document.getElementById('studioInboxJobSelect');select.value='job-1';select.dispatchEvent(new f.w.Event('change'));await new Promise(resolve=>setImmediate(resolve));assert(f.requests.some(url=>url.includes('jobId=job-1')));assert(f.w.document.getElementById('messageConversationPane'));}finally{f.dom.window.close();}
});
test('Reading pane shows professional identity and a controlled retry for uncertain delivery',async()=>{
    const f=fixture();try{await f.w.showStudioMessages();f.w.document.querySelector('[data-inbox-message]').click();await new Promise(resolve=>setImmediate(resolve));const pane=f.w.document.getElementById('messageConversationPane');assert.match(pane.textContent,/paul@studio.test/);assert.match(pane.textContent,/Needs attention/);const retry=pane.querySelector('[data-retry-delivery]');assert(retry);assert.equal(retry.dataset.uncertain,'true');}finally{f.dom.window.close();}
});

test('Incomplete attachment upload displays recovery instructions without a routing retry',async()=>{
    const f=fixture();try{
        const original=f.w.json;
        f.w.json=async url=>url.endsWith('/deliveries')?[{messageId:'message-1',recipient:'Attachments',transport:'Routing',status:'Attention',detail:'Attachment upload is incomplete. Repost the original request with its original files to resume.'}]:original(url);
        await f.w.showStudioMessages();f.w.document.querySelector('[data-inbox-message]').click();await new Promise(resolve=>setImmediate(resolve));
        const pane=f.w.document.getElementById('messageConversationPane');assert.match(pane.textContent,/original files/);assert.equal(pane.querySelector('[data-retry-routing]'),null);
    }finally{f.dom.window.close();}
});
test('Known delivery failure retries without uncertain outcome confirmation',async()=>{
    const f=fixture();try{
        const original=f.w.json;let confirmed=false,retryBody;
        f.w.confirm=()=>{confirmed=true;return false;};
        f.w.json=async (url,options)=>{
            if(url==='/api/messages/deliveries/delivery-1/retry'){retryBody=JSON.parse(options.body);return {};}
            if(url.endsWith('/deliveries'))return [{messageId:'message-1',recipient:'client@outside.test',transport:'Email',status:'Attention',detail:'ProviderRejected:403',deliveryId:'delivery-1'}];
            return original(url);
        };
        await f.w.showStudioMessages();f.w.document.querySelector('[data-inbox-message]').click();await new Promise(resolve=>setImmediate(resolve));
        f.w.document.querySelector('[data-retry-delivery]').click();await new Promise(resolve=>setImmediate(resolve));
        assert.equal(confirmed,false);assert.deepEqual(retryBody,{reconciledNotSent:false});
    }finally{f.dom.window.close();}
});
test('Reply attachments and request identity survive a failed send and retry',async()=>{
 const f=fixture();try{const w=f.w,sent=[];await w.showStudioMessages();w.document.querySelector('[data-inbox-message]').click();await new Promise(r=>setImmediate(r));w.document.getElementById('studioMessageBody').value='Production reply';Object.defineProperty(w.document.getElementById('studioMessageFile'),'files',{value:[new w.File(['document'],'handoff.pdf',{type:'application/pdf'})]});w.fetch=async(url,o)=>{if(url==='/api/messages/send'){sent.push(o.body);return {ok:sent.length>1,json:async()=>sent.length>1?{threadId:'thread-1'}:{error:'Temporary failure'}};}return {ok:true};};let form=w.document.getElementById('studioMessageComposer');await form.onsubmit({preventDefault(){},target:form});assert.match(w.document.getElementById('studioSendFeedback').textContent,/Temporary failure/);assert.equal(w.document.getElementById('studioMessageBody').value,'Production reply');await form.onsubmit({preventDefault(){},target:form});const first=JSON.parse(sent[0].get('message')),second=JSON.parse(sent[1].get('message'));assert.equal(first.requestId,second.requestId);assert.equal(first.threadId,'thread-1');assert.equal(first.body,'Production reply');assert.equal(sent[0].get('files').name,'handoff.pdf');}finally{f.dom.window.close();}
});

test('Inbound history renders the reply with recoverable collapsed original',async()=>{
 const f=fixture();try{
  const original=f.w.json;
  const body='Check if this is reply 2  On Wed, Oct 21, 2026, 08:53 Weddings <weddings@studio.test> wrote: Previous reply';
  f.w.json=async(url,...args)=>url==='/api/messages/threads/thread-1'
   ?[{id:'message-1',senderUserName:'Client',body,externalMessageId:'provider-id',createdAtUtc:'2026-10-21T13:00:00Z'}]
   :original(url,...args);
  await f.w.showStudioMessages();f.w.document.querySelector('[data-inbox-message]').click();await new Promise(r=>setImmediate(r));
  const pane=f.w.document.getElementById('messageConversationPane');
  assert.equal(pane.querySelector('.studio-email-body').textContent,'Check if this is reply 2');
  const details=pane.querySelector('.studio-email-signature');assert(details);assert.equal(details.open,false);
  assert.ok(details.textContent.includes(body));
 }finally{f.dom.window.close();}
});


test('scrolled inbox selection and the following read refresh preserve the seventh message row',async()=>{
 const {dom,w}=fixture();try{
 const base=w.json;
 w.json=async url=>{if(url.startsWith('/api/messages/inbox'))return {total:10,items:Array.from({length:10},(_,i)=>({thread:{id:'thread-1',kind:'Thread',title:'Portrait'},message:{id:'message-'+(i+1),senderUserName:'paul',subject:'Photo '+(i+1),body:'Hello',createdAtUtc:'2026-10-11T16:00:00Z'}}))};return base(url);};
 await w.showStudioMessages();
 const list=w.document.getElementById('studioInboxRows'),row=list.querySelector('[data-inbox-message="message-7"]');
 list.scrollTop=300;row.focus();row.click();await new Promise(r=>setImmediate(r));await new Promise(r=>setImmediate(r));
 assert.equal(w.document.getElementById('studioInboxRows'),list);assert.equal(list.querySelector('[data-inbox-message="message-7"]'),row);
 assert.equal(list.scrollTop,300);assert.equal(w.document.activeElement,row);assert.equal(row.classList.contains('selected'),true);
 assert.equal(list.querySelectorAll('.selected').length,1);
 }finally{dom.window.close();}
});

test('bell count uses all thread unread totals and updates to zero and 99+',async t=>{
 const dom=new JSDOM('<button id="notificationButton"><span id="messageUnreadBadge"></span></button>',{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());
 let poll,threads=[{id:'one',unreadCount:2},{id:'two',unreadCount:5}];
 w.setInterval=fn=>{poll=fn;return 1;};w.clearInterval=()=>{};w.setTimeout=()=>0;
 w.fetch=async()=>({ok:false});w.json=async url=>url==='/api/messages/threads'?threads:[];
 w.eval(source);await new Promise(resolve=>setImmediate(resolve));
 const badge=w.document.getElementById('messageUnreadBadge'),bell=w.document.getElementById('notificationButton');
 assert.equal(badge.textContent,'7');assert.equal(bell.getAttribute('aria-label'),'Messages, 7 unread');
 threads=[];await poll();assert.equal(badge.textContent,'0');assert.equal(badge.hidden,false);
 threads=[{id:'many',unreadCount:120}];await poll();assert.equal(badge.textContent,'99+');assert.match(bell.title,/120 unread/);
});
