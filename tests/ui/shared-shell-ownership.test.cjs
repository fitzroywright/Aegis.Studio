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
  assert.match(shell, /function renderLeftRail\(/);
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
