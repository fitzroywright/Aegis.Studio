// Match the embedded static asset exposed by Studio, without copying its source.
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '../../Aegis.Studio.Web/wwwroot');
module.exports = function readStudioScript(name) {
  const file = name.replace(/^\//, '');
  const source = file === 'roster-editor.js'
    ? path.resolve(root, '../../Aegis.Studio.PublicSite/public/roster-editor.js')
    : path.join(root, file);
  return fs.readFileSync(source, 'utf8');
};
