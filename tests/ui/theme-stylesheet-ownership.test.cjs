const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
const html=fs.readFileSync(path.join(root,'index.html'),'utf8');
const hrefs=[...html.matchAll(/<link\s+rel="stylesheet"\s+href="\/([^"?]+\.css)(?:\?[^"]*)?"/g)].map(match=>match[1]);
test('all linked Studio stylesheets exist and have unique paths',()=>{
 assert.ok(hrefs.length>0);
 assert.equal(new Set(hrefs).size,hrefs.length);
 for(const href of hrefs)assert.ok(fs.existsSync(path.join(root,href)), 'Missing CSS: '+href);
});
test('all application CSS is loaded from the canonical CSS directory',()=>{
 for(const href of hrefs)assert.match(href,/^css\/(core|components|pages|themes)\//);
});
test('three theme stylesheets and shared theme surfaces are loaded',()=>{
 for(const name of ['standard','light','dark'])assert.ok(hrefs.includes('css/themes/'+name+'.css'));
 assert.ok(hrefs.includes('css/components/theme-surfaces.css'));
});
