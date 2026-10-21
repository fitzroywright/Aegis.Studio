const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),css=require('css-tree');
const root=path.resolve(__dirname,'../../Aegis.Studio.Web/wwwroot');
test('shared CSS has one declaration for each selector, property and responsive scope',()=>{
 const ast=css.parse(fs.readFileSync(path.join(root,'css/core/studio-legacy.css'),'utf8')),seen=new Set();
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
 const source=fs.readFileSync(path.join(root,'css/core/studio-legacy.css'),'utf8');
 assert.doesNotMatch(source,/\.studio-inbox-(?:filters|job-filters|table)(?![\w-])|\.studio-media-(?:destinations|upload-heading)(?![\w-])|\.studio-shared-(?:page-heading|heading-copy|heading-trail)(?![\w-])/);
 assert.match(fs.readFileSync(path.join(root,'css/pages/messages.css'),'utf8'),/\.studio-inbox-table-wrap/);assert.match(fs.readFileSync(path.join(root,'css/pages/media-review.css'),'utf8'),/\.studio-media-upload-actions/);assert.doesNotMatch(source,/\.studio-media-page|\.studio-inbox-center/);
});

test('grid row highlight and motion have one shared stylesheet owner',()=>{
 const row=/mw-today-job-row|workspace-wow-(?:row|main)|fd-speedbump-row|studio-inbox-message-row|pipeline-timeline-row|job-row|op-link-row|task-card|pos-catalog-item|studio-speedbump-row|mw-(?:status|session|task|pipeline|message|quick-link)-row|sj-table.*data-sj-job|commercial-grid.*tr/;
 for(const file of ['css/core/studio-legacy.css','css/pages/jobs-page.css','css/pages/commercial.css','css/pages/pos.css']){
  const ast=css.parse(fs.readFileSync(path.join(root,file),'utf8'));
  css.walk(ast,node=>{if(node.type!=='Rule')return;const selector=css.generate(node.prelude);if(!row.test(selector)||!/:hover|:focus-visible|:active/.test(selector))return;
   node.block.children.forEach(d=>{if(d.type==='Declaration')assert.equal(/^(background|background-color|transform|box-shadow|outline|outline-offset)$/.test(d.property),false,file+' must delegate row interaction to studio-rows.css: '+selector+' / '+d.property);});
  });
 }
 const rules=fs.readFileSync(path.join(root,'css/components/studio-rows.css'),'utf8');assert.match(rules,/--studio-row-highlight:#fff6df/);assert.match(rules,/prefers-reduced-motion:reduce/);assert.match(rules,/transform:none/);
});

test('Production filter and Overview styles each have one owning declaration',()=>{
 const ast=css.parse(fs.readFileSync(path.join(root,'css/components/studio-page.css'),'utf8'));
 const expected=new Map([
  ['.production-board-filters',['background','color','border','padding']],
  ['.production-board-filters>input[type=search]',['background','color','height','font-size']],
  ['.production-board-filters>select',['background','color','height','font-size']],
  ['.production-overview-entries .workload-indicator',['height','font-size','font-weight','padding']],
  ['.production-overview-entries .workload-indicator strong',['font-size','font-weight']]
 ]);
 const found=new Map([...expected.keys()].map(selector=>[selector,0]));
 css.walk(ast,node=>{
  if(node.type!=='Rule')return;
  const selector=css.generate(node.prelude);
  if(!expected.has(selector))return;
  found.set(selector,found.get(selector)+1);
  const declarations=new Map();
  node.block.children.forEach(d=>{
   if(d.type!=='Declaration'||!expected.get(selector).includes(d.property))return;
   declarations.set(d.property,(declarations.get(d.property)||0)+1);
  });
  for(const property of expected.get(selector))assert.equal(declarations.get(property),1,selector+' must own one '+property+' declaration');
 });
 for(const [selector,count] of found)assert.equal(count,1,'Expected one owning rule for '+selector);
});


test('POS, Messaging, Workspaces and Front Desk use their owning stylesheets',()=>{
 const legacy=fs.readFileSync(path.join(root,'css/core/studio-legacy.css'),'utf8');
 for(const selector of [
  /(^|\\n)\\s*\\.away-digest-list\\s*\\{/,
  /(^|\\n)\\s*\\.pos-(?:panel|history|catalog-item|grid)(?:\\s|\\{|$)/,
  /(^|\\n)\\s*\\.workspace-list-page \\.workspace-wow-main\\s*\\{/,
  /(^|\\n)\\s*\\.workspace-list-page \\.workspace-wow-stat b\\s*\\{/,
  /body\\[data-studio-view="frontdesk"\\] \\.fd-table-frame \\.fd-table\\s*\\{/
 ])assert.doesNotMatch(legacy,selector,'Remove the duplicate legacy rule and edit the owning page stylesheet.');
 const pos=fs.readFileSync(path.join(root,'css/pages/pos.css'),'utf8');
 const workspaces=fs.readFileSync(path.join(root,'css/pages/workspaces-grid.css'),'utf8');
 assert.match(pos,/\\.pos-panel/);assert.match(pos,/\\.pos-grid/);assert.match(pos,/\\.pos-catalog-item/);
 assert.match(workspaces,/\\.workspace-list-page \\.workspace-wow-main/);
 assert.match(fs.readFileSync(path.join(root,'css/components/messaging.css'),'utf8'),/\\.away-digest-list/);
 assert.match(fs.readFileSync(path.join(root,'css/pages/frontdesk-filters.css'),'utf8'),/fd-table-frame \\.fd-table/);
});
