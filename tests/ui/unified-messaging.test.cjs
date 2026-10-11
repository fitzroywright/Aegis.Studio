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
        if(url==='/api/messages/address-book')return [{id:'job-1',scopeId:'job-1',contactType:'Job',displayName:'Wedding',status:'Active',deliveryMode:'InternalOnly'}];
        if(url==='/api/messages/threads')return [{id:'thread-1',scopeType:'Job',scopeId:'job-1',title:'Wedding'}];
        if(url==='/api/messages/threads/thread-1')return [{id:'message-1',senderUserName:'paul',subject:'Handoff',body:'Private message',createdAtUtc:'2026-10-11T16:00:00Z'}];
        if(url.endsWith('/deliveries'))return [{messageId:'message-1',recipient:'client@outside.test',transport:'Email',status:'Attention',detail:'ProviderOutcomeUncertain: lost response',deliveryId:'delivery-1'}];
        if(url.endsWith('/recipients'))return [];
        if(url==='/api/messages/identity')return {displayName:'Paul',emailAddress:'paul@studio.test'};
        return [];
    };
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
