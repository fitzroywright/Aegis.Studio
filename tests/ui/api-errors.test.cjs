const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('jsdom');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
for(const status of [401,403])test('API denial retains HTTP '+status+' for revocation-aware consumers',async t=>{
 const dom=new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'https://studio.test',runScripts:'outside-only'}),w=dom.window;t.after(()=>w.close());
 w.fetch=async()=>({ok:false,status,json:async()=>({error:'Access unavailable'})});
 w.eval(fs.readFileSync(path.join(root,'app.js'),'utf8'));
 await assert.rejects(()=>w.json('/api/jobs'),error=>error.status===status&&error.message==='Access unavailable');
});
