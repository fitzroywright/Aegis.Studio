const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),css=require('css-tree');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('shared CSS has one declaration for each selector, property and responsive scope',()=>{
 const ast=css.parse(fs.readFileSync(path.join(root,'studio.css'),'utf8')),seen=new Set();
 function visit(list,scope=[]){list.forEach(node=>{
  if(node.type==='Atrule'&&node.block&&!/keyframes|font-face|property/.test(node.name))visit(node.block.children,[...scope,node.name+':'+(node.prelude?css.generate(node.prelude):'')]);
  if(node.type!=='Rule'||node.prelude.type!=='SelectorList')return;
  node.prelude.children.forEach(selector=>node.block.children.forEach(d=>{
   if(d.type!=='Declaration')return;
   const key=[scope.join('|'),css.generate(selector),d.property].join('\0');
   assert.equal(seen.has(key),false,'Edit the owning CSS rule instead of appending an override: '+css.generate(selector)+' / '+d.property);seen.add(key);
  }));
 });}
 visit(ast.children);
});
test('retired inbox filter rail and obsolete Media controls have no presentation rules',()=>{
 const source=fs.readFileSync(path.join(root,'studio.css'),'utf8');
 assert.doesNotMatch(source,/\.studio-inbox-(?:filters|job-filters|table)(?![\w-])|\.studio-media-(?:destinations|upload-heading)(?![\w-])|\.studio-shared-(?:page-heading|heading-copy|heading-trail)(?![\w-])/);
 assert.match(source,/\.studio-inbox-table-wrap/);assert.match(source,/\.studio-media-upload-actions/);
});
