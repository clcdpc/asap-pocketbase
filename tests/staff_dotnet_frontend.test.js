const assert = require('assert');
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend', 'staff');
const requiredFiles = [
  'index.html',
  'styles.css',
  'app.js',
  path.join('js', 'http.js'),
  path.join('js', 'workflow.js')
];

for (const file of requiredFiles) {
  assert.ok(fs.existsSync(path.join(root, file)), `${file} should exist in tracked .NET frontend source`);
}

const index = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
const app = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
const http = fs.readFileSync(path.join(root, 'js', 'http.js'), 'utf8');
const workflow = fs.readFileSync(path.join(root, 'js', 'workflow.js'), 'utf8');
const styles = fs.readFileSync(path.join(root, 'styles.css'), 'utf8');
const all = `${index}\n${app}\n${http}\n${workflow}`;

assert.match(index, /Sign in with Microsoft/);
assert.match(index, /gridjs\.umd\.js/);
assert.match(workflow, /\/api\/asap\/config\?libraryOrgId=/, 'staff editor should load scoped public-form configuration');
assert.doesNotMatch(workflow, /Format code/, 'staff editor should present the configured format selector, not an internal code input');
assert.match(workflow, /canChangeWorkflowState/, 'workflow controls should honor backend operation capabilities');
assert.match(styles, /table\.gridjs-table\s*\{[^}]*min-width:\s*\d+px/s, 'queue table should retain readable mobile columns inside its scroll wrapper');
assert.match(app, /createWorkflowApp/);
assert.match(http, /X-ASAP-Antiforgery/);
assert.match(http, /createLatestLoad/);
assert.match(workflow, /searchParams\.get\('request'\)/);
assert.match(workflow, /searchParams\.set\('request', id\)/);
assert.match(workflow, /searchParams\.delete\('request'\)/);
assert.match(index, /data-view="additional-copies"/);
assert.match(index, /id="additional-copy-create-dialog"/);
assert.match(index, /id="additional-copy-reminder"/);
assert.match(workflow, /searchParams\.set\('stage', 'additional_copies'\)/);
assert.match(workflow, /\/api\/asap\/staff\/additional-copies\?scope=/);
assert.match(workflow, /title-requests\/\$\{request\.id\}\/additional-copy/);
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

console.log('Staff .NET frontend structure regression checks passed');
