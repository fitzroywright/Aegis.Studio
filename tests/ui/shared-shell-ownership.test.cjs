const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../../Aegis.Studio.Web/wwwroot');
const css = fs.readFileSync(path.join(root, 'studio.css'), 'utf8');
const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const shell = fs.readFileSync(path.join(root, 'studio-shell-nav.js'), 'utf8');

test('Studio loads the canonical stylesheet', () => {
  assert.match(html, /href="\/studio\.css\?/);
  assert.doesNotMatch(html, /studio-legacy\.css/);
  assert.equal(fs.existsSync(path.join(root, 'css/core/studio-legacy.css')), false);
});
test('Today and other pages do not fork breadcrumb styles', () => {
  assert.doesNotMatch(css, /body\[data-studio-view="today"\] \.topbar #studioBreadcrumb \.breadcrumb-label/);
  assert.doesNotMatch(css, /body:not\(\[data-studio-view="today"\]\) \.topbar #studioBreadcrumb \.breadcrumb-label/);
  assert.match(css, /body \.topbar #studioBreadcrumb \.breadcrumb-label::after/);
});
test('shared shell owns the workspace selector', () => {
  assert.match(shell, /function renderPageMenu\(/);
  assert.match(shell, /function renderFooterWorkspaceMenu\(/);
  const footer = css.match(/#studioShellFooter #studioFooterWorkspaces \{([^}]+)\}/);
  assert.ok(footer);
  assert.doesNotMatch(footer[1], /!important/);
});


test('shared topbar has one authoritative top-level declaration', () => {
  const rules = css.match(/^body \.topbar \{/gm) || [];
  assert.equal(rules.length, 1);
  assert.match(css, /body \.topbar \{[^}]*background: #172d4a/s);
});

test('shell CSS contains no malformed empty declarations', () => {
  assert.doesNotMatch(css, /\{\s*;+(?:\s*;)*\s*\}/);
});


test('hidden page surfaces remain hidden regardless of page display rules', () => {
  assert.match(css, /\.hidden \{\s*display: none !important;/);
  assert.match(css, /\[hidden\] \{\s*display: none !important;/);
  const exceptions = [...css.matchAll(/!important\b/g)];
  assert.equal(exceptions.length, 2);
});

test('Today, Front Desk and Job Detail use the one shared sessions drawer', () => {
  const nav = fs.readFileSync(path.join(root, 'studio-navigation.js'), 'utf8');
  const front = fs.readFileSync(path.join(root, 'frontdesk.js'), 'utf8');
  assert.doesNotMatch(nav, /Today's Sessions|data-mw-region="schedule"/);
  assert.doesNotMatch(front, /Today's Sessions|fd-secondary mw-list-right/);
  for (const page of ['photographer-schedule.js', 'tasks-page.js']) {
    const source = fs.readFileSync(path.join(root, page), 'utf8');
    assert.doesNotMatch(source, /Today[’']s Sessions|ps-todays-sessions|data-mw-region="schedule"/);
  }
  assert.match(shell, /sessionsDrawer.id = 'studioSessionsDrawer'/);
  assert.match(shell, /const sessions = sharedSessions;/);
  assert.doesNotMatch(shell, /sharedSessions\.filter\(item => String\(item\.jobId\)/);
  assert.match(shell, /loadSharedSessions\(\)/);
});
test('shared shell and Front Desk scripts are syntactically valid', () => {
  const vm = require('node:vm');
  for (const name of ['studio-shell-nav.js', 'studio-navigation.js', 'frontdesk.js']) {
    assert.doesNotThrow(() => new vm.Script(fs.readFileSync(path.join(root, name), 'utf8'), {filename:name}));
  }
});


test('shell height has one owner and reserves the full footer below every page',()=>{
 const rules=[...css.replace(/\/\*[\s\S]*?\*\//g,'').matchAll(/([^{}]+)\{([^{}]*)\}/g)].map(([,selector,body])=>({selector:selector.trim(),properties:Object.fromEntries([...body.matchAll(/([\w-]+)\s*:\s*([^;]+);/g)].map(([,property,value])=>[property,value.trim()]))}));
 const heights=rules.filter(rule=>/(?:^| )\.shell$/.test(rule.selector)).flatMap(({selector,properties})=>Object.entries(properties).filter(([name])=>['height','min-height','max-height'].includes(name)).map(([property,value])=>({selector,property,value})));
 assert.deepEqual(heights,[{selector:'body .shell',property:'height',value:'calc(100dvh - var(--studio-banner-height))'},{selector:'body .shell',property:'min-height',value:'0'}]);
 const properties=rules.find(rule=>rule.selector==='body .shell'&&rule.properties.height).properties;
 assert.equal(properties['grid-template-rows'],'minmax(0,1fr) var(--studio-footer-height)');
 assert.equal(properties['grid-template-columns'],'minmax(0,1fr)');
 assert.equal(properties['box-sizing'],'border-box');assert.equal(properties.overflow,'hidden');
});


test('footer participates in the reserved shell row instead of covering page content',()=>{
 const footer=css.match(/body \.shell>\.studio-shell-footer \{([^}]+)\}/)[1];
 assert.match(footer,/position: relative;/);
 assert.match(footer,/grid-column: 1 \/ -1;/);
 assert.match(footer,/grid-row: 2;/);
 assert.match(footer,/height: var\(--studio-footer-height\);/);
 assert.doesNotMatch(footer,/position: fixed|(?:^|;)\s*(?:bottom|inset-inline):/);
});

