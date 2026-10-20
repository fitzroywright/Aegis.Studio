const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');

test('pending booking inquiry form initializes template controls and save handler',async t=>{
 const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
 const dom=new JSDOM('<body><main id="genericView"></main></body>',{url:'https://studio.test',runScripts:'outside-only',pretendToBeVisual:true});
 const w=dom.window;t.after(()=>w.close());
 w.genericView=w.document.querySelector('main');
 w.esc=value=>String(value??'');
 w.json=async()=>[];
 w.StudioGraphTemplates={preview(){}};
 w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};
 w.eval(fs.readFileSync(path.join(root,'frontdesk.js'),'utf8'));
 w.eval("frontDeskWorkspaces=[{id:'workspace',name:'Weddings'}]");
 await w.openPendingBookingForm({id:'inquiry',workspaceId:'workspace',contactName:'Client',contactAddress:'client@example.test',channel:'Email',subject:'Session'},null);
 const form=w.document.querySelector('dialog form');
 assert.ok(form);
 assert.ok(form.querySelector('[data-template-preview]'));
 assert.ok(form.querySelector('[data-template-services]'));
 assert.equal(typeof form.onsubmit,'function');
});
