const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');

for(const available of [false,true]){
 test('Today renders shared message card with ResizeObserver '+(available?'available':'unavailable'),async()=>{
  const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{
   url:'https://studio.test',runScripts:'outside-only',pretendToBeVisual:true
  });
  const w=dom.window,errors=[],requests=[];
  Object.assign(w,{
   json:async url=>{requests.push(url);return url==='/api/auth/me'?{user:{userName:'staff'}}:[];},
   esc:value=>String(value??''),workspaceCache:[],loadWorkspaces:async()=>{},
   console:{debug(){},info(){},warn(){},error(...args){errors.push(args.map(String).join(' '));}}
  });
  if(available)w.ResizeObserver=class{observe(){}disconnect(){}};
  else delete w.ResizeObserver;
  try{
   for(const name of ['studio-status-order.js','subject-picker.js','studio-message-card.js','studio-navigation.js'])
    w.eval(fs.readFileSync(path.join(root,name),'utf8'));
   await new Promise(resolve=>w.setTimeout(resolve,0));
   await w.studioTodayReady;
   assert.deepEqual(errors,[]);
   assert.ok(w.document.querySelector('[data-mw-region="jobs"]'));
   assert.ok(w.document.querySelector('#todayMessageCard'));
   assert.equal(w.studioTodayDiagnostics.cancelled,false);
   assert.equal(requests.length,6);
  }finally{dom.window.close();}
 });
}
