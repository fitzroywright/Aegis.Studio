const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('Job Details registers one workflow renderer before consumers load',()=>{
 const context=vm.createContext({window:{StudioGraph:{esc:String}}});
 new vm.Script(fs.readFileSync(path.join(root,'job-details.js'),'utf8'),{filename:'job-details.js'}).runInContext(context);
 assert.equal(typeof context.window.StudioJobDetails.media,'function');
 assert.equal(typeof context.window.StudioJobDetails.nextStep,'function');
 assert.equal(typeof context.window.StudioJobDetails.compact,'function');
 assert.equal(context.window.StudioJobDetails.workflow,undefined);
});
