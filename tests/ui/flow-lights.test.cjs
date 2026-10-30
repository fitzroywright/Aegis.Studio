const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('Job Details uses the dedicated display renderer for generic and custom workflows',()=>{
 const details=fs.readFileSync(path.join(root,'job-details.js'),'utf8');
 const page=fs.readFileSync(path.join(root,'workspace-outlook.js'),'utf8');
 const display=fs.readFileSync(path.join(root,'job-workflow-display.js'),'utf8');
 assert.match(display,/root\.StudioJobWorkflowDisplay=\{render\}/);
 assert.match(page,/StudioJobWorkflowDisplay\.render\(header\.querySelector\('\.jd-workflow-track'\),graphView\)/);
 assert.doesNotMatch(details,/function (?:compact|renderWorkflowSummary|workflow)\(/);
 assert.doesNotMatch(page,/StudioJobDetails\.(?:compact|workflow)\(/);
});
