const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('workflow summaries have one renderer shared by the graph projection',()=>{
 const details=fs.readFileSync(path.join(root,'job-details.js'),'utf8');
 assert.equal((details.match(/function renderWorkflowSummary\(/g)||[]).length,1);
 assert.match(details,/root\.StudioJobDetails\.renderWorkflowSummary\(summaryHost,/);
 assert.doesNotMatch(details,/jd-flow-mainline|jd-flow-exception-lane/);
});
