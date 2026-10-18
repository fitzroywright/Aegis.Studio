const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const source=fs.readFileSync(path.join(__dirname,'../../Aegis.Studio.Web/wwwroot/inquiry-association.js'),'utf8');
const tick=()=>new Promise(resolve=>setTimeout(resolve,0));
function fixture(overrides={}){
    const dom=new JSDOM('<main><div class="message-conversation-header"></div></main>',{runScripts:'outside-only'});
    const w=dom.window;w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;this.onclose?.();};
    const calls=[];let association={clientId:null,clientName:null,reference:'AEGIS-REFERENCE',candidates:[],canEdit:true,history:[],...overrides};
    w.json=async(url,options)=>{calls.push({url,options});if(url.endsWith('/association/client')){association={...association,clientId:JSON.parse(options.body).clientId,clientName:null};return association;}if(url.includes('/association/clients'))return [{id:'client-1',name:'A <client>',email:'client@example.test'}];if(url.endsWith('/association'))return association;if(url==='/api/jobs')return[];return{};};
    w.eval(source);return {w,calls,host:w.document.querySelector('main'),close:()=>w.close()};
}
test('unassigned inquiries expose client assignment and keep dangerous text escaped',async()=>{
    const f=fixture({clientName:'<img src=x onerror=alert(1)>'});try{await f.w.StudioInquiryAssociation.mount(f.host,'inquiry',{messages:[]});assert.match(f.host.textContent,/Assign client/);assert.equal(f.host.querySelector('img'),null);assert.match(f.host.textContent,/AEGIS-REFERENCE/);}finally{f.close();}
});
test('staff can find and select a client without selecting search input on each keystroke',async()=>{
    const f=fixture();try{await f.w.StudioInquiryAssociation.mount(f.host,'inquiry',{messages:[]});f.host.querySelector('[data-client]').click();await tick();const input=f.w.document.querySelector('[name=query]');input.value='Cli';input.setSelectionRange(3,3);input.dispatchEvent(new f.w.Event('input'));await tick();assert.equal(input.selectionStart,3);assert.equal(input.selectionEnd,3);const form=input.form;form.elements.client.value='client-1';form.dispatchEvent(new f.w.Event('submit',{cancelable:true}));await tick();const save=f.calls.find(c=>c.url.endsWith('/association/client'));assert.equal(JSON.parse(save.options.body).clientId,'client-1');}finally{f.close();}
});
test('multiple email matches require a choice and do not save automatically',async()=>{
    const f=fixture({candidates:[{id:'one'},{id:'two'}]});try{await f.w.StudioInquiryAssociation.mount(f.host,'inquiry',{messages:[]});f.host.querySelector('[data-client]').click();await tick();assert.match(f.w.document.querySelector('dialog').textContent,/2 clients share/);assert.equal(f.calls.filter(c=>c.options?.method==='PUT').length,0);}finally{f.close();}
});
test('removing a client writes an explicit null association',async()=>{
    const f=fixture({clientId:'client-1',clientName:'Client'});try{await f.w.StudioInquiryAssociation.mount(f.host,'inquiry',{messages:[]});f.host.querySelector('[data-remove]').click();await tick();const save=f.calls.find(c=>c.options?.method==='PUT');assert.equal(JSON.parse(save.options.body).clientId,null);}finally{f.close();}
});
test('read-only staff see history without association actions',async()=>{
    const f=fixture({canEdit:false,history:[{action:'Automatic client match',actor:'Studio',previous:'null',next:'client-1',at:'2026-10-18T12:00:00Z'}]});try{await f.w.StudioInquiryAssociation.mount(f.host,'inquiry',{messages:[]});assert.equal(f.host.querySelector('button'),null);assert.match(f.host.textContent,/Automatic client match/);}finally{f.close();}
});
test('unfamiliar-sender review displays the hold and sends nothing automatically',async()=>{
    const f=fixture();f.w.json=async url=>{f.calls.push({url});return[{id:'review',subject:'New email',failureReason:'Reference matched—unrecognized sender',fromName:'Unknown',fromAddress:'unknown@example.test',body:'Please help'}];};try{await f.w.StudioInquiryAssociation.review();assert.match(f.w.document.querySelector('dialog').textContent,/Reference matched—unrecognized sender/);assert.deepEqual(f.calls.map(c=>c.url),['/api/email/routing-review']);}finally{f.close();}
});
test('late inquiry responses cannot mount controls onto another conversation',async()=>{
    const f=fixture();let finish;f.w.json=()=>new Promise(resolve=>{finish=resolve;});try{
        const request=f.w.StudioInquiryAssociation.mount(f.host,'old-inquiry',{messages:[]});
        f.host.innerHTML='<div class="message-conversation-header">Another conversation</div>';
        finish({clientId:null,clientName:null,reference:'OLD',candidates:[],canEdit:true,history:[]});await request;
        assert.equal(f.host.querySelector('[data-inquiry-association]'),null);
    }finally{f.close();}
});
