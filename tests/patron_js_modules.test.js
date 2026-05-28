const assert = require('assert');
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..');
const patronDir = path.join(root, 'pb_public', 'patron');
const jsDir = path.join(patronDir, 'js');

const expectedModules = [
  'state.js',
  'dom.js',
  'html.js',
  'api.js',
  'config.js',
  'form-rules.js',
  'form-ui.js',
  'steps.js',
  'auth.js',
  'submit.js',
  'bootstrap.js'
];

for (const filename of expectedModules) {
  assert.ok(fs.existsSync(path.join(jsDir, filename)), `${filename} should exist`);
}

const appJs = fs.readFileSync(path.join(patronDir, 'app.js'), 'utf8').trim();
assert.match(appJs, /import\s+\{\s*initPatronApp\s*\}\s+from\s+['"]\.\/js\/bootstrap\.js['"]/);
assert.match(appJs, /initPatronApp\(\);?$/);
assert.ok(!appJs.includes('document.getElementById'), 'entrypoint should not contain DOM business logic');
assert.ok(appJs.split(/\r?\n/).length <= 5, 'entrypoint should remain small');

const indexHtml = fs.readFileSync(path.join(patronDir, 'index.html'), 'utf8');
assert.match(indexHtml, /<script\s+src="app\.js\?v=[^"]+"\s+type="module"><\/script>/);


const authSource = fs.readFileSync(path.join(jsDir, 'auth.js'), 'utf8');
assert.ok(authSource.includes('result.effectiveLibraryOrgId || (result.record && result.record.libraryOrgId)'), 'patron auth should store effective library context before falling back to patron home library');
assert.ok(authSource.includes('storePatronContextId(result.patronContextId'), 'patron auth should store the session-bound patron context id after login');
assert.ok(authSource.includes("storePatronContextId('')"), 'patron logout should clear the session-bound patron context id');


const submitSource = fs.readFileSync(path.join(jsDir, 'submit.js'), 'utf8');
assert.ok(submitSource.includes('data.patronContextId = contextId'), 'patron submissions should include the session-bound patron context id');

const formRules = fs.readFileSync(path.join(jsDir, 'form-rules.js'), 'utf8');
assert.ok(!/\b(document|window|fetch|localStorage)\b/.test(formRules), 'form-rules.js should stay pure');

for (const filename of expectedModules) {
  const source = fs.readFileSync(path.join(jsDir, filename), 'utf8');
  assert.ok(!/\brequire\s*\(/.test(source), `${filename} should not use CommonJS require`);
  assert.ok(!/\bmodule\.exports\b/.test(source), `${filename} should not use CommonJS exports`);
}

console.log('Patron JS module structure regression checks passed');
