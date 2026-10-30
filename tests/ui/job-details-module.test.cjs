const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('Job Details registers one workflow renderer before consumers load',()=>{
 const context=vm.createContext({window:{StudioGraph:{esc:String},addEventListener(){}},document:{addEventListener(){}}});
 new vm.Script(fs.readFileSync(path.join(root,'job-details.js'),'utf8'),{filename:'job-details.js'}).runInContext(context);
 assert.equal(typeof context.window.StudioJobDetails.media,'function');
 new vm.Script(fs.readFileSync(path.join(root,'studio-next-actions.js'),'utf8'),{filename:'studio-next-actions.js'}).runInContext(context);
 assert.equal(typeof context.window.StudioNextActions.mount,'function');
 assert.equal(typeof context.window.StudioNextActions.activate,'function');
 assert.equal(context.window.StudioJobDetails.nextActionLink,undefined);
 assert.equal(context.window.StudioJobDetails.compact,undefined);
 assert.equal(context.window.StudioJobDetails.workflow,undefined);
});

