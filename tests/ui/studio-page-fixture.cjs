// Use the actual shared placement/query contract alongside each fixture's API stubs.
const fs=require('node:fs');
const path=require('node:path');
module.exports=function loadStudioPage(w){
 const existing=w.StudioPage||{};
 w.eval(fs.readFileSync(path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot/studio-page.js'),'utf8'));
 Object.assign(w.StudioPage,existing);
};
