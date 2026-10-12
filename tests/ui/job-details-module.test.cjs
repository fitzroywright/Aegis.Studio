const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('Job Details script parses and registers its media and workflow API before consumers load',()=>{
 const context=vm.createContext({window:{}});
 const script=new vm.Script(fs.readFileSync(path.join(root,'job-details.js'),'utf8'),{filename:'job-details.js'});
 script.runInContext(context);
 assert.equal(typeof context.window.StudioJobDetails.media,'function');
 assert.equal(typeof context.window.StudioJobDetails.workflow,'function');
 const html=fs.readFileSync(path.join(root,'index.html'),'utf8');
 const scripts=Array.from(html.matchAll(/<script[^>]+src="([^"?]+)[^"]*"/g),m=>m[1]);
 assert.equal(scripts.filter(s=>s==='/job-details.js').length,1);
 assert.ok(scripts.indexOf('/job-details.js')<scripts.indexOf('/workspace-outlook.js'));
});
