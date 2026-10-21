const {test}=require('node:test');
const assert=require('node:assert/strict');
const email=require('../../Aegis.Studio.Web/wwwroot/studio-email-body.js');
const notice='Notice of Confidentiality: The information contained in this communication is intended solely for the recipient.';
const header='On Wed, Oct 21, 2026, 08:53 Weddings Workspace <weddings@studio.test> wrote:';
test('flattened Gmail reply collapses nested history and repeated notices',()=>{
 const original="Check if this is reply 2  "+header+' Old message  '+notice+' '+notice;
 const result=email.split(original);assert.equal(result.body,'Check if this is reply 2');assert.equal(result.original,original);assert.equal(result.collapsed,true);
});
test('wrapped Gmail headers and quote-only mail',()=>{
 assert.equal(email.split('Well let’s see\nOn Wed, Oct 21, 2026,\n08:48 Weddings <weddings@studio.test>\nwrote:\nOld text').body,'Well let’s see');
 assert.equal(email.split(header+'\nOld text').body,'');
 assert.match(email.render({externalMessageId:'id',body:header+'\nOld text'}),/No new reply text/);
});
test('original disclaimer is collapsed without removing authored text',()=>{
 assert.equal(email.split('Did you see this? '+notice).body,'Did you see this?');
});
test('inline answers remain visible between explicit quoted lines',()=>{
 const result=email.split(header+'\n> Can we book Friday?\nYes, Friday works.\n> How many?\nSix people.');
 assert.equal(result.body,'Yes, Friday works.\nSix people.');
});
test('Outlook headers collapse while an ordinary From label stays visible',()=>{
 assert.equal(email.split('Confirmed\nFrom: Client <client@example.test>\nSent: Tuesday\nTo: Studio\nSubject: Booking\nOld text').body,'Confirmed');
 assert.equal(email.split('From: our discussion\nPlease book Friday.').collapsed,false);
});
test('unknown and substantive text stays visible',()=>{
 const value='Thanks\nPlease book Friday.\nOn arrival we will discuss what she wrote: a note.';
 assert.equal(email.split(value).body,value);assert.equal(email.split(value).collapsed,false);
});
test('signature and internal message handling',()=>{
 assert.equal(email.split('Confirmed\nKind regards,\nJane Smith').body,'Confirmed');
 const text='Internal note\n--\nDo not discard';
 assert.equal(email.parts({body:text}).body,text);assert.equal(email.parts({body:text}).collapsed,false);
});
test('original and displayed content are escaped, details closed by default',()=>{
 const html=email.render({externalMessageId:'id',body:'<img src=x onerror=alert(1)>  '+header+' <script>bad()</script>'});
 assert.ok(!html.includes('<img'));assert.ok(!html.includes('<script>'));assert.ok(!/<details[^>]*\bopen\b/.test(html));
 assert.match(html,/Show quoted text and signature/);
});
