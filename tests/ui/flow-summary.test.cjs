const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('workflow summaries have one display owner, separate from the graph editor',()=>{
 const display=fs.readFileSync(path.join(root,'job-workflow-display.js'),'utf8');
 const details=fs.readFileSync(path.join(root,'job-details.js'),'utf8');
 const page=fs.readFileSync(path.join(root,'workspace-outlook.js'),'utf8');
 assert.equal((display.match(/function render\(/g)||[]).length,1);
 assert.equal((page.match(/StudioJobWorkflowDisplay\.render\(/g)||[]).length,1);
 assert.doesNotMatch(display,/StudioGraph\.render|StudioJobGraph\.open/);
 assert.doesNotMatch(details,/function renderWorkflowSummary\(/);
});
