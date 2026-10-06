const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '../../Aegis.Studio.Web/wwwroot');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');

test('obsolete workspace sidebar presentation is not wired into the current shell', () => {
  const index = read('index.html');
  const app = read('app.js');
  const access = read('workspace-access.js');
  const outlook = read('workspace-outlook.js');

  for (const obsolete of [
    'id="workspaceList"',
    'id="allButton"',
    'class="workspace-toolbar"',
    'id="workspaceSearch"',
    'id="workspaceRefresh"'
  ]) {
    assert.equal(index.includes(obsolete), false, `index.html must not restore obsolete UI: ${obsolete}`);
  }

  assert.equal(app.includes('workspaceList'), false);
  assert.equal(app.includes('workspaceBandHue'), false);
  assert.equal(app.includes('allButton.onclick'), false);

  assert.equal(access.includes('workspaceList'), false);
  assert.equal(access.includes('originalLoadWorkspacesForAccess'), false);

  assert.equal(outlook.includes(".workspace-toolbar"), false);
  assert.equal(outlook.includes('legacyAllButton'), false);
  assert.equal(outlook.includes("getElementById('workspaceSearch')"), false);
  assert.equal(outlook.includes("getElementById('workspaceRefresh')"), false);
});
