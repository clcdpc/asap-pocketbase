const assert = require('assert');
const fs = require('fs');
const path = require('path');

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

assert.match(index, /Sign in with Microsoft/);
assert.match(index, /gridjs\.umd\.js/);
assert.match(workflow, /\/api\/asap\/config\?libraryOrgId=/, 'staff editor should load scoped public-form configuration');
assert.doesNotMatch(workflow, /Format code/, 'staff editor should present the configured format selector, not an internal code input');
assert.match(workflow, /canChangeWorkflowState/, 'workflow controls should honor backend operation capabilities');
assert.match(styles, /table\.gridjs-table\s*\{[^}]*min-width:\s*\d+px/s, 'queue table should retain readable mobile columns inside its scroll wrapper');
assert.match(app, /createWorkflowApp/);
assert.match(workflow, /requestId: String\(request\.id\)/,
  'Polaris lookup request IDs should stay strings');
assert.doesNotMatch(workflow, /requestId:\s*Number\(request\.id\)/,
  'Polaris lookup must not coerce a title-request identity to Number');
assert.match(http, /X-ASAP-Antiforgery/);
assert.doesNotMatch(workflow, /\blatestLoads\b/, 'reads belong to disposable controllers');
assert.match(workflow, /createSessionCoordinator/);
assert.match(workflow, /createStaffShell/);
assert.match(workflow, /requestedRequestIdFromUrl/);
assert.match(workflow, /requestedStatusFromUrl/);
assert.match(urlUtils, /searchParams\.get\('request'\)/);
assert.match(urlUtils, /searchParams\.set\('request', normalizedId\)/);
assert.match(urlUtils, /searchParams\.delete\('request'\)/);
assert.match(index, /data-view="additional-copies"/);
assert.match(index, /data-view="analytics"/);
assert.match(index, /id="analytics-container"/);
assert.match(workflow, /loadAnalytics/);
assert.match(workflow, /resetAnalytics/);
assert.match(analytics, /\/api\/asap\/staff\/analytics\?/);
assert.match(analytics, /reads\.begin\('analytics'\)/);
assert.match(analytics, /analyticsRange = 'lastMonth'/);
assert.match(index, /id="additional-copy-create-dialog"/);
assert.match(index, /id="additional-copy-reminder"/);
assert.match(urlUtils, /searchParams\.set\('stage', 'additional_copies'\)/);
assert.match(workflow, /\/api\/asap\/staff\/additional-copies\?scope=/);
assert.match(workflow, /title-requests\/\$\{parent\.request\.id\}\/additional-copy/);
assert.match(workflow, /title-requests\/\$\{record\.sourceId\}\/additional-copy/);
for (const operation of ['claim', 'unclaim', 'assign', 'close', 'reopen', 'delete']) {
  assert.ok(workflow.includes(`operation === '${operation}'`) || workflow.includes(`'${operation}'`),
    `${operation} should be wired through the AdditionalCopy UI`);
}
assert.match(workflow, /claimClearedReason/);
assert.match(workflow, /createElement/);
assert.match(workflow, /window\.requestAnimationFrame/);
assert.equal(
  [...workflow.matchAll(/\/api\/asap\/staff\/assignment-candidates\?libraryOrgId=/g)].length,
  2,
  'both assignment pickers should use the scoped candidate API'
);
assert.doesNotMatch(
  workflow,
  /authorizedJson\(`\/api\/asap\/staff\/users\?orgId=/,
  'workflow assignment must not depend on the admin-only Staff Access endpoint'
);
assert.match(workflow, /pickup-options/);
assert.match(workflow, /place-hold/);
assert.match(workflow, /retry-identifier-check/);
assert.match(workflow, /hold-operations\/\$\{operation\.id\}\/\$\{action\}/);
for (const resolutionField of [
  'requestVersion',
  'operationSpecificProofAttested',
  'proofSource',
  'causalConnection',
  'provenFinalHoldId',
  'executorExclusionAttested',
  'executorExclusionReference',
  'executorExclusionExplanation'
]) {
  assert.ok(workflow.includes(resolutionField), `${resolutionField} should be wired through operator resolution`);
}
assert.match(workflow, /serverFenced/);
assert.match(workflow, /'aria-label': 'Resolution'/);
assert.match(workflow, /'aria-label': 'Evidence type'/);
for (const preference of [
  'weeklyActionSummaryEnabled',
  'weeklyActionSummaryEmail',
  'purchaseReminderDefault',
  'additionalCopyReminderDefault',
  'defaultMineUnclaimedFilter'
]) {
  assert.ok(all.includes(preference), `${preference} should be wired through the staff profile UI`);
}

assert.ok(!/pocketbase/i.test(all), 'The .NET staff shell must not use PocketBase browser state');
assert.ok(!/\.innerHTML\s*=/.test(all), 'Runtime staff UI must use DOM APIs rather than innerHTML assignment');

const composition = fs.readFileSync(path.join(root, 'js', 'workflow.js'), 'utf8');
assert.doesNotMatch(composition, /\/api\/|history\.|JSON\.stringify|createDraftScope|\bstate\s*=/,
  'composition only constructs, wires and starts/disposes owners');
const owners = new Set(['analytics', 'bulk-delete', 'copy-creation', 'copy-detail', 'detail-host',
  'navigation', 'operations-controller', 'profile-controller', 'session', 'settings', 'shell',
  'suggestion-controller', 'title-detail', 'queues', 'research'].map(name => `${name}.js`));
const visited = new Set(), visiting = new Set();
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
    const sharedResearchHelpers = path.basename(dependency) === 'research.js' &&
      /^import\s*\{\s*(?:positivePolarisId|applyPolarisResultToControls)\s*\}\s*from/.test(match[0]);
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

console.log('Staff .NET frontend structure regression checks passed');
