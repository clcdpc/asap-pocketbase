const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { fixture } = require('./helpers/staff-controller.cjs');

const root = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend', 'staff');
const requiredFiles = [
  'index.html',
  'styles.css',
  'app.js',
  path.join('js', 'http.js'),
  path.join('js', 'workflow.js'),
  path.join('js', 'url-utils.js'),
  path.join('js', 'analytics.js')
];

for (const file of requiredFiles) {
  assert.ok(fs.existsSync(path.join(root, file)), `${file} should exist in tracked .NET frontend source`);
}

const index = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const app = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
const http = fs.readFileSync(path.join(root, 'js', 'http.js'), 'utf8');
const workflow = fs.readdirSync(path.join(root, 'js')).filter(name => name.endsWith('.js'))
  .map(name => fs.readFileSync(path.join(root, 'js', name), 'utf8')).join('\n');
const urlUtils = fs.readFileSync(path.join(root, 'js', 'url-utils.js'), 'utf8');
const analytics = fs.readFileSync(path.join(root, 'js', 'analytics.js'), 'utf8');
const styles = fs.readFileSync(path.join(root, 'styles.css'), 'utf8');
const all = `${index}\n${app}\n${http}\n${workflow}\n${urlUtils}\n${analytics}`;

// Positive workflow behavior is exercised through imported controllers and the
// real staff UI (see docs/implementation/test-infrastructure-code-health.md).
// Retain deliberate architecture/security prohibitions here.
assert.match(index, /Sign in with Microsoft/);
assert.match(index, /gridjs\.umd\.js/);
assert.doesNotMatch(workflow, /authorizedJson\(`\/api\/asap\/staff\/users\?orgId=/,
  'workflow assignment must not depend on the admin-only Staff Access endpoint');

assert.ok(!/pocketbase/i.test(all), 'The .NET staff shell must not use PocketBase browser state');
assert.ok(!/\.innerHTML\s*=/.test(all), 'Runtime staff UI must use DOM APIs rather than innerHTML assignment');

const composition = fs.readFileSync(path.join(root, 'js', 'workflow.js'), 'utf8');
assert.doesNotMatch(composition, /\/api\/|history\.|JSON\.stringify|createDraftScope|\bstate\s*=/,
  'composition only constructs, wires and starts/disposes owners');
const owners = new Set(['analytics', 'bulk-delete', 'copy-creation', 'copy-detail', 'detail-host',
  'navigation', 'operations-controller', 'profile-controller', 'session', 'settings', 'shell',
  'suggestion-controller', 'title-detail', 'queues', 'research'].map(name => `${name}.js`));
const visited = new Set(), visiting = new Set();
const statelessResearchHelpers = new Set(['positivePolarisId', 'applyPolarisResultToControls',
  'researchRecordUrl', 'researchRecordValue', 'safeMaterialIconUrl']);
function inspectImports(file) {
  assert.ok(!visiting.has(file), `ES module import cycle at ${file}`);
  if (visited.has(file)) return;
  visiting.add(file);
  const source = fs.readFileSync(file, 'utf8');
  for (const match of source.matchAll(/(?:import|export)\s+[^;]*?\sfrom\s*['"]([^'"]+)['"]/g)) {
    if (!match[1].startsWith('.')) continue;
    const dependency = path.resolve(path.dirname(file), match[1]);
    // Research exports stateless formatting/verification helpers alongside the
    // injected dialog service. Importing those helpers grants no peer lifetime.
    const importedNames = match[0].match(/^import\s*\{([^}]+)\}\s*from/)?.[1].split(',').map(name => name.trim());
    const sharedResearchHelpers = path.basename(dependency) === 'research.js' && importedNames?.length > 0 &&
      importedNames.every(name => statelessResearchHelpers.has(name));
    if (owners.has(path.basename(file)) && !sharedResearchHelpers) {
      assert.ok(!owners.has(path.basename(dependency)), `${file} must use injected ports instead of importing a peer owner`);
    }
    inspectImports(dependency);
  }
  visiting.delete(file); visited.add(file);
}
inspectImports(path.join(root, 'app.js'));
for (const name of fs.readdirSync(path.join(root, 'js')).filter(name => name.endsWith('.js'))) {
  if (name !== 'router.js') {
    assert.doesNotMatch(fs.readFileSync(path.join(root, 'js', name), 'utf8'), /\bhistory\s*(?:\.\s*(?:pushState|replaceState|go|back|forward|state)\b|\[)/,
      `${name} cannot manipulate browser history`);
  }
}

// Exercise both consumers of the assignment API instead of counting URL text.
(async () => {
  for (const copy of [false, true]) await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createDetailHost } = await load('detail-host');
    const { createTitleDetailController } = await load('title-detail');
    const { createCopyDetailController } = await load('copy-detail');
    const session = createSessionIdentity();
    session.accept({ id: '20', tenantId: 'tenant', authenticationEmail: 'staff@example.org', role: 'super_admin', organizationId: 1 });
    const host = createDetailHost({ root: get('#request-dialog') });
    const calls = [];
    const record = { id: '9223372036854775807', libraryOrgId: 2, version: 'v1', title: 'Scoped assignment',
      status: copy ? 'open' : 'suggestion', format: 'book', customFields: {}, capabilities: { canClaim: true, canAssign: true, allowedActions: [] } };
    const create = copy ? createCopyDetailController : createTitleDetailController;
    const controller = create({ host, sessionIdentity: session, announce() {},
      beforeOpen: () => ({ isCurrent: () => true }), beforeClose: () => true,
      isNavigationCurrent: () => true, getNavigationGeneration: () => 1,
      getScope: () => 'all', onAlign: async () => ({ scope: 'all', commit: () => 1 }),
      onOpened() {}, onClosed() {}, getFocusReturn: () => null,
      refreshQueue: async () => true, queueSequence: () => 1, rememberOpened() {}, forgetUnavailable() {},
      onReceipt() {}, clearReceipt() {}, polarisLookup: { close() {}, invalidate() {} },
      copyCreation: { close: () => true, invalidate() {}, hasPendingMutation: () => false },
      request: async (url, init = {}) => {
        calls.push({ url, init });
        if (url === '/api/asap/staff/assignment-candidates?libraryOrgId=2') return { candidates: [{ id: '9007199254740993', displayName: 'Eligible staff' }] };
        return record;
      } });
    try {
      await controller.open(record.id);
      [...get('#request-dialog').querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === 'Assign').click();
      const deadline = Date.now() + 3000;
      while (!get('.inline-form select') && Date.now() < deadline) await new Promise(resolve => setImmediate(resolve));
      const select = get('.inline-form select');
      assert.ok(select, 'Scoped assignment candidates render');
      assert.equal(select.querySelector('option[value="9007199254740993"]').textContent, 'Eligible staff');
      const requests = calls.filter(call => call.url.includes('assignment-candidates'));
      assert.equal(requests.length, 1);
      assert.equal(requests[0].url, '/api/asap/staff/assignment-candidates?libraryOrgId=2');
      assert.equal(requests[0].init.method, undefined);
      assert.equal(requests[0].init.body, undefined);
      assert.ok(requests[0].init.signal);
      assert.equal(calls.some(call => call.url.includes('/staff/users')), false);
    } finally { controller.dispose(); host.dispose(); }
  });
  console.log('Staff frontend ownership, safety and both scoped assignment consumers passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
