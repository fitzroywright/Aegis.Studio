const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('status filters follow event order, retain unknown values, and recognize legacy status spellings',t=>{
 const dom=new JSDOM('',{runScripts:'outside-only'});t.after(()=>dom.window.close());const w=dom.window;w.eval(fs.readFileSync(path.join(root,'studio-status-order.js'),'utf8'));
 assert.deepEqual(Array.from(w.StudioStatusOrder.sort(['Completed','Scheduled','Pending','Awaiting Upload','Photographer Assigned','Cancelled','Active Production','Ready for Delivery'])),['Pending','Photographer Assigned','Scheduled','Awaiting Upload','Active Production','Ready for Delivery','Completed','Cancelled']);
 assert.deepEqual(Array.from(w.StudioStatusOrder.sort(['Custom B','Closed','InProduction','Custom A','New','InProduction'])),['New','InProduction','Closed','Custom B','Custom A']);
});
