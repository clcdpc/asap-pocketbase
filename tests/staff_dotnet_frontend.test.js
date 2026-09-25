const assert = require('assert');
const fs = require('fs');
const path = require('path');

const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
const legacyRoot = path.join(frontend, 'staff');
const nextRoot = path.join(frontend, 'staff-next');

const legacyFiles = [
  'index.html',
  'styles.css',
  'app.js',
  path.join('js', 'state.js'),
  path.join('js', 'http.js'),
  path.join('js', 'grid.js'),
  path.join('js', 'grid-data.js'),
  path.join('js', 'grid-actions.js'),
  path.join('js', 'modals.js'),
  path.join('js', 'settings.js'),
  path.join('js', 'app', 'auth.js'),
  path.join('js', 'app', 'url-utils.js')
];
for (const file of legacyFiles) {
  assert.ok(fs.existsSync(path.join(legacyRoot, file)), `${file} should remain in the restored legacy module tree`);
}

for (const file of ['index.html', 'styles.css', 'app.js', path.join('js', 'workflow.js'), path.join('js', 'settings.js')]) {
  assert.ok(fs.existsSync(path.join(nextRoot, file)), `${file} should remain available in staff-next`);
}

const legacyIndex = fs.readFileSync(path.join(legacyRoot, 'index.html'), 'utf8');
const legacyHttp = fs.readFileSync(path.join(legacyRoot, 'js', 'http.js'), 'utf8');
const legacyEvents = fs.readFileSync(path.join(legacyRoot, 'js', 'app', 'events.js'), 'utf8');
const legacyState = fs.readFileSync(path.join(legacyRoot, 'js', 'state.js'), 'utf8');
const legacyGrid = fs.readFileSync(path.join(legacyRoot, 'js', 'grid-data.js'), 'utf8');
const nextIndex = fs.readFileSync(path.join(nextRoot, 'index.html'), 'utf8');
const nextApp = fs.readFileSync(path.join(nextRoot, 'app.js'), 'utf8');
const nextSettings = fs.readFileSync(path.join(nextRoot, 'js', 'settings.js'), 'utf8');

for (const asset of [
  '/vendor/bootstrap/4.1.3/css/bootstrap.min.css',
  '/vendor/font-awesome/4.7.0/css/font-awesome.css',
  '/vendor/gridjs/6.2.0/theme/mermaid.min.css',
  '/vendor/gridjs/6.2.0/gridjs.umd.js'
]) {
  assert.ok(legacyIndex.includes(asset), `legacy staff should load local ${asset}`);
}
assert.doesNotMatch(legacyIndex, /(?:src|href)="https?:\/\//i, 'legacy staff must not request production CDN assets');
assert.match(legacyIndex, /Sign in with Microsoft/);
assert.match(legacyIndex, /id="status-tabs"/);
for (const status of ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'additional_copies', 'closed', 'analytics', 'settings']) {
  assert.ok(legacyIndex.includes(`data-status="${status}"`), `legacy status tab ${status} should remain`);
}
for (const settingsTarget of ['start', 'polaris', 'smtp', 'staff', 'workflow', 'patron', 'templates']) {
  assert.ok(legacyIndex.includes(`data-settings-target="${settingsTarget}"`), `legacy settings section ${settingsTarget} should remain`);
}
assert.match(legacyIndex, /id="editModal"/);
assert.match(legacyIndex, /id="profile-dialog"/);
assert.match(legacyGrid, /createLatestLoad/);
assert.match(legacyGrid, /requestedRequestIdFromUrl/);
assert.match(legacyHttp, /X-ASAP-Antiforgery/);
assert.match(legacyHttp, /asap:stale-write/);
assert.doesNotMatch(legacyHttp, /titleRequestVersions|additionalCopyVersions/);
assert.match(legacyHttp, /\/api\/asap\/staff\/legacy\/session/);
assert.match(legacyEvents, /\/api\/asap\/staff\/sign-in\?returnUrl=/);
assert.match(legacyEvents, /\/api\/asap\/staff\/sign-out/);
assert.match(legacyState, /staffSession/);

const runtimeFiles = [];
function collect(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) collect(full);
    else if (/\.(?:html|js|mjs)$/.test(entry.name)) runtimeFiles.push(full);
  }
}
collect(legacyRoot);
const legacyRuntime = runtimeFiles.map(file => fs.readFileSync(file, 'utf8')).join('\n');
assert.doesNotMatch(legacyRuntime, /PocketBase|authStore|pocketbase\.umd|\.collection\(/i);
for (const retiredRoute of [
  '/api/asap/jobs/hold-check',
  '/api/asap/jobs/promoter-check',
  '/api/asap/staff/material-types/sync',
  '/api/asap/staff/requests/delete-closed',
  '/api/asap/staff/test-polaris',
  '/api/asap/staff/test-smtp'
]) {
  assert.ok(!legacyRuntime.includes(retiredRoute), `legacy staff should not call retired route ${retiredRoute}`);
}
assert.match(legacyRuntime, /\/api\/asap\/staff\/patron-lookup/);
assert.match(legacyRuntime, /\/api\/asap\/staff\/suggestions/);
assert.match(legacyRuntime, /\/api\/asap\/staff\/workflow\/run-now/);
assert.match(legacyRuntime, /\/api\/asap\/staff\/email-operations\/test/);
assert.match(legacyIndex, /Email \/ Postmark/);
assert.match(legacyIndex, /id="postmark-token"/);
assert.match(legacyIndex, /id="postmark-clear-token"/);
assert.doesNotMatch(legacyIndex, /id="smtp-(?:host|port|username|password|tls)"/);
const serializeSettings = fs.readFileSync(path.join(legacyRoot, 'js/settings/serialize-save.js'), 'utf8');
assert.match(serializeSettings, /postmarkToken: getFieldValue\('postmark-token'\)/);
assert.match(serializeSettings, /clearPostmarkToken: getFieldChecked\('postmark-clear-token'\)/);
assert.doesNotMatch(serializeSettings, /payload\.smtp|smtp-host|smtp-port/);
const saveSettings = fs.readFileSync(path.join(legacyRoot, 'js/settings/save-controller.js'), 'utf8');
assert.match(saveSettings, /version: lastSavedLibrarySettingsOrgId === currentLibraryContextOrgId/);
const auth = fs.readFileSync(path.join(legacyRoot, 'js/app/auth.js'), 'utf8');
assert.match(auth, /\/api\/asap\/staff\/email-status/);
assert.doesNotMatch(auth, /enabled: true/);
assert.match(auth, /guard\.isCurrent\(\)/);

assert.match(nextApp, /createWorkflowApp/);
assert.match(nextSettings, /\/api\/asap\/staff\/settings/);
assert.doesNotMatch(nextSettings, /\/api\/asap\/staff\/legacy\/settings/);
assert.match(nextIndex, /href="\/staff-next\/"/);
assert.match(nextIndex, /returnUrl=%2Fstaff-next%2F/);

console.log('Legacy primary and retained staff-next frontend contract checks passed');
