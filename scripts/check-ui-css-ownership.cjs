#!/usr/bin/env node
// Guard against reintroducing legacy CSS, cascading override patches, and competing shell ownership.
// Baseline is intentionally limited to newly changed lines while the existing CSS is consolidated.
const { execFileSync } = require('node:child_process');
const base = process.env.UI_CSS_BASE || 'origin/main';
function git(...args) { return execFileSync('git', args, { encoding: 'utf8' }); }
let diff;
try { diff = git('diff', '--unified=0', '--no-ext-diff', base + '...HEAD', '--', 'Aegis.Studio.Web/wwwroot'); }
catch (error) { console.error('Cannot determine CSS changes against ' + base + ': ' + error.message); process.exit(2); }
const failures = [];
let file = '';
let added = [];
let removedImportant = 0;
let addedImportant = 0;
const touchedCss = new Set();
function check() {
  if (!file || !added.length) { added = []; return; }
  const css = file.endsWith('.css');
  if (css) touchedCss.add(file);
  if (css && file.endsWith('/studio-legacy.css')) failures.push(file + ': legacy stylesheet is frozen; migrate ownership instead of editing it.');
  if (css && added.some(line => /!important\b/i.test(line))) failures.push(file + ': new !important declarations are prohibited.');
  if (css && added.some(line => /(?:override|temporary fix|hotfix|css patch)/i.test(line))) failures.push(file + ': override patches are prohibited.');
  if (/\.(html|js|css)$/.test(file) && added.some(line => /studio-legacy\.css/i.test(line))) failures.push(file + ': new legacy CSS references are prohibited.');
  added = [];
}
for (const line of diff.split('\n')) {
  if (line.startsWith('diff --git ')) { check(); file = line.match(/^diff --git a\/.* b\/(.*)$/)?.[1] || ''; }
  else if (line.startsWith('+') && !line.startsWith('+++')) added.push(line.slice(1));
  else if (line.startsWith('-') && !line.startsWith('---') && file.endsWith('.css')) removedImportant += (line.match(/!important\\b/gi) || []).length;
}
check();
if (addedImportant > removedImportant) failures.push('CSS introduces ' + (addedImportant - removedImportant) + ' net new !important declarations.');
// Parse rules with their at-rule context and compare against the base revision.
// Existing debt is grandfathered; a PR must never increase duplicate selector/property
// ownership or introduce page-specific rules that reposition shared-shell controls.
function declarations(source) {
  const result = new Map();
  const cleaned = source.replace(/\\/\\*[\\s\\S]*?\\*\\//g, '');
  const stack = [];
  let start = 0;
  for (let i = 0; i < cleaned.length; i++) {
    if (cleaned[i] === '{') {
      const header = cleaned.slice(start, i).trim();
      stack.push({header, start:i+1});
      start = i+1;
    } else if (cleaned[i] === '}') {
      const block = stack.pop();
      if (block && !block.header.startsWith('@') && !block.header.startsWith('--')) {
        const context = stack.filter(x => x.header.startsWith('@')).map(x => x.header).join('|');
        const body = cleaned.slice(block.start, i);
        for (const selector of block.header.split(',')) {
          const normalized = selector.trim().replace(/\\s+/g,' ');
          for (const match of body.matchAll(/(?:^|;)\\s*([\\w-]+)\\s*:\\s*([^;{}]+)/g)) {
            const key = context+'|'+normalized+'|'+match[1].toLowerCase();
            result.set(key,(result.get(key)||0)+1);
          }
        }
      }
      start = i+1;
    }
  }
  return result;
}
for (const path of touchedCss) {
  let current, previous;
  try {
    current = git('show','HEAD:'+path);
    previous = git('show',base+':'+path);
  } catch (error) {
    failures.push(path+': cannot compare CSS ownership to base: '+error.message);
    continue;
  }
  const after = declarations(current), before = declarations(previous);
  for (const [key,count] of after) {
    const old = before.get(key)||0;
    if (count > 1 && count > Math.max(1,old)) failures.push(path+': competing declarations for '+key);
  }
  const shellSelector = /(?:\\.topbar|#studioShellFooter|#studioContextRail|#studioBreadcrumb|#studioSessionsDrawer|#studioAttentionDrawer)/;
  const reposition = /\\|(?:margin-left|margin-right|position|left|right|top|bottom|transform|display|height|gap)$/;
  for (const [key,count] of after) {
    if (before.has(key)) continue;
    const parts=key.split('|'), selector=parts[parts.length-2], property=parts[parts.length-1];
    if (/body\\[(?:data-studio-view|data-view)=/.test(selector) && shellSelector.test(selector) && /^(margin-left|margin-right|position|left|right|top|bottom|transform|display|height|gap)$/.test(property))
      failures.push(path+': page-specific shared-shell layout rule prohibited: '+key);
    if (shellSelector.test(selector) && /!important/.test(key)) failures.push(path+': shared-shell important rule prohibited: '+key);
  }
}
if (failures.length) { console.error('Studio UI CSS ownership violations:\n' + failures.map(x => ' - ' + x).join('\n')); process.exit(1); }
console.log('Studio UI CSS ownership guard passed (changed lines only).');
