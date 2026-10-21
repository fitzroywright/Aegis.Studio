const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('Job Details uses one graph renderer for generic and custom workflows',()=>{
 const details=fs.readFileSync(path.join(root,'job-details.js'),'utf8');
 const page=fs.readFileSync(path.join(root,'workspace-outlook.js'),'utf8');
 assert.match(details,/function compact\(/);
 assert.doesNotMatch(details,/function workflow\(/);
 assert.doesNotMatch(page,/StudioJobDetails\.workflow\(/);
 assert.match(page,/StudioJobDetails\.compact\(/);
});
