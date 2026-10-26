#!/usr/bin/env node
// Guard against reintroducing legacy CSS, cascading override patches, and competing shell ownership.
// Baseline is intentionally limited to newly changed lines while the existing CSS is consolidated.
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');
const base = process.env.UI_CSS_BASE || 'origin/main';
function git(...args) { return execFileSync('git', args, { encoding: 'utf8' }); }
let diff;
try { diff = git('diff', '--unified=0', '--no-ext-diff', base + '...HEAD', '--', 'Aegis.Studio.Web/wwwroot'); }
catch (error) { console.error('Cannot determine CSS changes against ' + base + ': ' + error.message); process.exit(2); }
const failures = [];
let file = '';
let added = [];
function check() {
  if (!file || !added.length) { added = []; return; }
  const css = file.endsWith('.css');
  if (css && file.endsWith('/studio-legacy.css')) failures.push(file + ': legacy stylesheet is frozen; migrate ownership instead of editing it.');
  if (css && /(^|\\/)studio\\.css$/.test(file)) failures.push(file + ': monolithic studio.css is frozen; edit the owning component stylesheet.');
  if (css && added.some(line => /!important\\b/i.test(line))) failures.push(file + ': new !important declarations are prohibited.');
  if (css && added.some(line => /(?:override|temporary fix|hotfix|css patch)/i.test(line))) failures.push(file + ': override patches are prohibited.');
  if (/\\.(html|js)$/.test(file) && added.some(line => /studio-legacy\\.css/i.test(line))) failures.push(file + ': new legacy CSS references are prohibited.');
  added = [];
}
for (const line of diff.split('\\n')) {
  if (line.startsWith('diff --git ')) { check(); file = line.match(/^diff --git a\\/.* b\\/(.*)$/)?.[1] || ''; }
  else if (line.startsWith('+') && !line.startsWith('+++')) added.push(line.slice(1));
}
check();
if (failures.length) { console.error('Studio UI CSS ownership violations:\\n' + failures.map(x => ' - ' + x).join('\\n')); process.exit(1); }
console.log('Studio UI CSS ownership guard passed (changed lines only).');
