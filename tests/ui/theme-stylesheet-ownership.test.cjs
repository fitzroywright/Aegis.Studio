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
 for(const href of hrefs)assert.ok(href==='studio.css'||/^css\/(core|components|pages|themes)\//.test(href),href);
});
test('four theme stylesheets and shared theme surfaces are loaded',()=>{
 for(const name of ['standard','comfort','light','dark'])assert.ok(hrefs.includes('css/themes/'+name+'.css'));
 assert.ok(hrefs.includes('css/components/theme-surfaces.css'));
});

test('Comfort is selectable and maps to its own theme scope',()=>{
 const script=fs.readFileSync(path.join(root,'theme.js'),'utf8');
 assert.match(script,/\['comfort',\s*'Comfort'\]/);
 assert.match(script,/value === 'comfort'\) return 'comfort'/);
 assert.match(script,/normalized === 'comfort'\) return 'studio-comfort'/);
});

test('attention component uses shared color variables',()=>{
 const card=fs.readFileSync(path.join(root,'studio.css'),'utf8');
 assert.ok(card.includes('var(--studio-drawer-background,'));assert.ok(card.includes('var(--studio-drawer-header,'));assert.equal(fs.existsSync(path.join(root,'css/components/needs-attention.css')),false);
 for(const theme of ['css/core/tokens.css','css/themes/light.css','css/themes/dark.css']){
  const styles=fs.readFileSync(path.join(root,theme),'utf8');
  assert.ok(styles.includes('--attention-surface:'),theme);
 }
});

