const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
function setup(t){
 const dom=new JSDOM('<body data-studio-view="frontdesk"><main id="genericView"></main><span id="studioFooterRate"></span></body>',{url:'https://studio.test',runScripts:'outside-only'});
 t.after(()=>dom.window.close());const w=dom.window,calls=[],errors=[];
 w.genericView=w.document.querySelector('main');w.esc=String;w.money=String;w.json=async()=>[];w.alert=message=>errors.push(message);
 for(const name of ['studio-status-order.js','studio-message-card.js'])w.eval(fs.readFileSync(path.join(root,name),'utf8'));
 w.eval(fs.readFileSync(path.join(root,'frontdesk.js'),'utf8')+"\nloadFrontDeskCommunications=()=>{};frontDeskRequests=[];frontDeskJobs=[];window.switchFrontDeskActionsTest=()=>{frontDeskTab='sales';renderFrontDesk();};renderFrontDesk();");
 w.studioLaunchNewBooking=async()=>calls.push(['booking']);
 w.pickFrontDeskFinancialAction=async action=>calls.push(['financial',action]);
 w.showFrontDeskClients=async openNew=>calls.push(['clients',openNew]);
 w.launchFrontDeskPos=async action=>calls.push(['pos',action]);
 return {w,calls,errors};
}
test('Front Desk actions sit above the grid and retain the existing seven command handlers after repaint',async t=>{
 const {w,calls,errors}=setup(t);
 for(let pass=0;pass<2;pass++){
  const toolbar=w.document.querySelector('.fd-grid-actions');
  assert.equal(w.document.querySelectorAll('.fd-grid-actions').length,1);
  assert.equal(toolbar.getAttribute('aria-label'),'Front Desk actions');
  assert.ok(toolbar.compareDocumentPosition(w.document.querySelector('.fd-table-frame'))&w.Node.DOCUMENT_POSITION_FOLLOWING);
  const buttons=[...toolbar.querySelectorAll('button')];
  assert.deepEqual(buttons.map(b=>b.textContent),['New Booking','Take Payment','Record Deposit','Issue Refund','New Sale','New Client','Close Register']);
  for(const button of buttons)await button.onclick();
  w.switchFrontDeskActionsTest();
 }
 const expected=[['booking'],['financial','TakePayment'],['financial','RecordDeposit'],['financial','IssueRefund'],['financial','NewSale'],['clients',true],['pos','CloseRegister']];
 assert.deepEqual(calls,[...expected,...expected]);assert.deepEqual(errors,[]);
 assert.equal(w.document.querySelector('.fd-overview'),null);
});
test('relocated financial, client and register actions report existing service errors',async t=>{
 const {w,errors}=setup(t);const fail=async()=>{throw Error('Service unavailable');};
 w.pickFrontDeskFinancialAction=fail;w.showFrontDeskClients=fail;w.launchFrontDeskPos=fail;
 for(const selector of ['[data-fd-financial-action="TakePayment"]','[data-fd-new-client]','[data-fd-close-register]'])await w.document.querySelector(selector).onclick();
 assert.deepEqual(errors,['Service unavailable','Service unavailable','Service unavailable']);
});
