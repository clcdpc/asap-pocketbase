const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

function response(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status >= 400 ? 'Request failed' : 'OK',
    json: async () => body
  };
}

function librarySettings(orgId, marker) {
  return {
    orgId,
    version: `settings-${orgId}-${marker}`,
    isOverride: true,
    uiText: { loginNote: marker },
    emails: {},
    systemSettings: {},
    polaris: {},
    workflow: { suggestionLimit: orgId },
    formats: [],
    providers: [],
    autoClaimRules: [],
    autoClaimStaff: [],
    templates: []
  };
}

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
  await Promise.resolve();
}

async function waitFor(predicate) {
  for (let attempt = 0; attempt < 60; attempt += 1) {
    await flush();
    if (predicate()) return;
  }
  assert.ok(predicate(), 'Timed out waiting for expected primary staff UI state');
}

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const primaryRoot = path.join(frontendRoot, 'staff');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-primary-entra-access-'));
  fs.cpSync(primaryRoot, path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  let dom;
  try {
    const index = fs.readFileSync(path.join(primaryRoot, 'index.html'), 'utf8');
    const eventsSource = fs.readFileSync(path.join(primaryRoot, 'js', 'app', 'events.js'), 'utf8');
    const runtimeSource = fs.readdirSync(path.join(primaryRoot, 'js'), { recursive: true })
      .filter(file => /\.(?:js|mjs)$/.test(String(file)))
      .map(file => fs.readFileSync(path.join(primaryRoot, 'js', file), 'utf8'))
      .join('\n');

    assert.match(index, /Sign in with Microsoft/);
    assert.match(index, /Authentication email \/ UPN/);
    assert.doesNotMatch(index, /DOMAIN\\username|Username \/ identity|Last Login|Emergency override password|Initial Setup/i);
    assert.doesNotMatch(runtimeSource, /overridePassword|polaris-override-pass|setup-form|setup-container/i);
    assert.doesNotMatch(runtimeSource, /authStore|\.collection\(|local password|emergency override login/i);
    assert.match(index, /Weekly summary email override/);
    assert.match(index, /Leave blank to send weekly summaries to your primary notification email/);
    assert.doesNotMatch(eventsSource, /notificationEmail\s*:/,
      'Profile self-service must not submit the primary notification email');

    dom = new JSDOM(index, { url: 'https://localhost/staff/' });
    global.window = dom.window;
    global.document = dom.window.document;
    global.CustomEvent = dom.window.CustomEvent;
    global.Event = dom.window.Event;
    global.FormData = dom.window.FormData;
    global.Node = dom.window.Node;
    global.Option = dom.window.Option;
    global.localStorage = dom.window.localStorage;
    global.navigator = dom.window.navigator;
    global.requestAnimationFrame = callback => callback();
    dom.window.HTMLDialogElement.prototype.showModal = function showModal() {
      this.setAttribute('open', '');
    };
    dom.window.HTMLDialogElement.prototype.close = function close() {
      this.removeAttribute('open');
    };

    let runtimeFailure = '';
    let startupSession = null;
    let delayLibraryTwoStaffUsers = false;
    let resolveLibraryTwoStaffUsers;
    let delayLibraryTwoOrganizations = false;
    let resolveLibraryTwoOrganizations;
    let useLibraryThreeOrganizations = false;
    let delayLibraryTwoSettings = false;
    let resolveLibraryTwoSettings;
    let delayAddStaff = false;
    let resolveAddStaff;
    let delayMetadataPatch = false;
    let resolveMetadataPatch;
    let mutationSessionResult = null;
    let sessionRequestCount = 0;
    let queueSessionResponses = false;
    const pendingSessionResponses = [];
    let signOutRequests = 0;
    let delaySignOut = false;
    let releaseSignOut;
    let staffUserReads = 0;
    let bootstrapMutations = 0;
    let users = [
      {
        id: '9007199254740993',
        userPrincipalName: 'active@example.org',
        displayName: 'Active Staff',
        notificationEmail: 'notify@example.org',
        role: 'admin',
        organizationId: 2,
        active: true,
        version: 'version-active'
      },
      {
        id: '9007199254740995',
        userPrincipalName: 'inactive@example.org',
        displayName: 'Inactive Staff',
        notificationEmail: 'inactive-notify@example.org',
        role: 'staff',
        organizationId: 2,
        active: false,
        version: 'version-inactive'
      }
    ];
    const writes = [];
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      const method = String(options.method || 'GET').toUpperCase();
      if (requestUrl === '/api/asap/config') {
        return response(200, {});
      }
      if (requestUrl === '/api/asap/staff/legacy/session') {
        sessionRequestCount += 1;
        if (queueSessionResponses) {
          return new Promise(resolve => {
            pendingSessionResponses.push((status, body) => resolve(response(status, body)));
          });
        }
        if (startupSession === 'invalid') {
          return response(401, { code: 'staff_session_invalid' });
        }
        if (startupSession === 'unavailable') {
          return response(200, {
            authenticated: true,
            accessAllowed: false,
            code: 'staff_library_inactive',
            antiforgeryToken: 'access-unavailable-token'
          });
        }
        if (startupSession === 'forbidden') {
          return response(403, { code: 'staff_scope_forbidden', accessAllowed: false });
        }
        if (startupSession === 'error') {
          return response(500, { message: 'Session service unavailable' });
        }
        if (mutationSessionResult) {
          if (mutationSessionResult.networkError) {
            throw new Error('Simulated session request failure');
          }
          return response(mutationSessionResult.status, mutationSessionResult.body);
        }
        throw new Error('Unexpected session request');
      }
      if (requestUrl.startsWith('/api/asap/staff/legacy/settings?')) {
        if (requestUrl.includes('orgId=2') && delayLibraryTwoSettings) {
          return new Promise(resolve => {
            resolveLibraryTwoSettings = () => resolve(response(200, librarySettings(2, 'stale-library-two-baseline')));
          });
        }
        if (requestUrl.includes('orgId=3')) {
          return response(200, librarySettings(3, 'current-library-three-baseline'));
        }
        return response(200, librarySettings(1, 'system-baseline'));
      }
      if (requestUrl === '/runtime-probe') {
        if (runtimeFailure === 'invalid') {
          return response(401, { code: 'staff_session_invalid' });
        }
        if (runtimeFailure === 'access') {
          return response(403, { code: 'staff_scope_forbidden', accessAllowed: false });
        }
        if (runtimeFailure === 'scoped') {
          return response(403, { code: 'staff_scope_forbidden' });
        }
        return response(200, { ok: true });
      }
      if (requestUrl.includes('/api/asap/staff/email-status')) {
        return response(200, { enabled: true });
      }
      if (requestUrl.includes('/api/asap/staff/legacy/title-requests')) {
        return response(200, { items: [], scope: { superAdmin: true, mode: 'library', libraryOrgId: '2', label: 'Library Two' }, availableLibraries: [] });
      }
      if (requestUrl.includes('/api/asap/staff/legacy/additional-copies')) {
        return response(200, { items: [], scope: { superAdmin: true, mode: 'library', libraryOrgId: '2', label: 'Library Two' }, availableLibraries: [] });
      }
      if (requestUrl.startsWith('/api/asap/staff/legacy/patron-codes')) {
        return response(200, []);
      }
      if (requestUrl === '/api/asap/staff/legacy/organizations') {
        if (delayLibraryTwoOrganizations) {
          return new Promise(resolve => {
            resolveLibraryTwoOrganizations = () => resolve(response(200,
              [{ id: 2, displayName: 'Stale Library Two', active: true }]));
          });
        }
        if (useLibraryThreeOrganizations) {
          return response(200, [{ id: 3, displayName: 'Current Library Three', active: true }]);
        }
        return response(200, [
            { id: 1, displayName: 'System', active: true },
            { id: 2, displayName: 'Library Two', active: true },
            { id: 3, displayName: 'Library Three', active: true }
          ]);
      }
      if (requestUrl.startsWith('/api/asap/staff/users') && method === 'GET') {
        staffUserReads += 1;
        if (requestUrl.includes('orgId=2') && delayLibraryTwoStaffUsers) {
          return new Promise(resolve => {
            resolveLibraryTwoStaffUsers = () => resolve(response(200, {
              canAssignSuperAdmin: true,
              users: [{
                id: '9007199254740202',
                userPrincipalName: 'stale-library-two@example.org',
                displayName: 'Stale Library Two',
                notificationEmail: 'stale-library-two@example.org',
                role: 'staff',
                organizationId: 2,
                active: true,
                version: 'stale-library-two-version'
              }]
            }));
          });
        }
        if (requestUrl.includes('orgId=3')) {
          return response(200, {
            canAssignSuperAdmin: false,
            users: [{
              id: '9007199254740303',
              userPrincipalName: 'current-library-three@example.org',
              displayName: 'Current Library Three',
              notificationEmail: 'current-library-three@example.org',
              role: 'staff',
              organizationId: 3,
              active: true,
              version: 'current-library-three-version'
            }]
          });
        }
        return response(200, { canAssignSuperAdmin: true, users });
      }
      if (requestUrl.startsWith('/api/asap/staff/users') && method !== 'GET') {
        const body = JSON.parse(options.body || '{}');
        writes.push({ url: requestUrl, method, body });
        if (delayAddStaff && requestUrl === '/api/asap/staff/users' && method === 'POST') {
          return new Promise(resolve => {
            resolveAddStaff = () => resolve(response(201, { user: { id: '9007199254740404' } }));
          });
        }
        if (delayMetadataPatch && method === 'PATCH') {
          return new Promise(resolve => {
            resolveMetadataPatch = () => resolve(response(200, {
              user: { id: '9007199254740993', version: 'stale-metadata-result' }
            }));
          });
        }
        let savedUser;
        if (method === 'PATCH') {
          const targetId = decodeURIComponent(requestUrl.match(/\/users\/([^/]+)$/)?.[1] || '');
          users = users.map(user => user.id === targetId
            ? { ...user, userPrincipalName: body.email, displayName: body.displayName, notificationEmail: body.notificationEmail }
            : user);
          savedUser = users.find(user => user.id === targetId);
        } else if (method === 'DELETE') {
          const targetId = decodeURIComponent(requestUrl.match(/\/users\/([^/]+)$/)?.[1] || '');
          users = users.map(user => user.id === targetId
            ? { ...user, active: false, version: 'version-deactivated' }
            : user);
          savedUser = users.find(user => user.id === targetId);
        } else if (method === 'POST' && /\/role$/.test(requestUrl)) {
          const targetId = decodeURIComponent(requestUrl.match(/\/users\/([^/]+)\/role$/)?.[1] || '');
          users = users.map(user => user.id === targetId
            ? { ...user, role: body.role, organizationId: body.organizationId, version: 'version-role-updated' }
            : user);
          savedUser = users.find(user => user.id === targetId);
        } else if (method === 'POST' && requestUrl === '/api/asap/staff/users') {
          const existing = users.find(user => user.userPrincipalName.toLowerCase() === String(body.email).toLowerCase());
          if (existing) {
            users = users.map(user => user.id === existing.id
              ? { ...user, active: true, role: body.role, organizationId: body.organizationId, version: 'version-reactivated' }
              : user);
            savedUser = users.find(user => user.id === existing.id);
          } else {
            savedUser = {
              id: (9007199254741000n + BigInt(users.length)).toString(),
              userPrincipalName: body.email,
              displayName: body.email,
              notificationEmail: body.email,
              role: body.role,
              organizationId: body.organizationId,
              active: true,
              version: 'version-created'
            };
            users = [...users, savedUser];
          }
        }
        return response(method === 'POST' && requestUrl === '/api/asap/staff/users' ? 201 : 200, {
          user: savedUser || users[0],
          cleanup: { rulesDeactivated: 1, openTitleClaimsCleared: 2, openAdditionalCopyClaimsCleared: 3 }
        });
      }
      if (requestUrl === '/api/asap/staff/sign-out' && method === 'POST') {
        signOutRequests += 1;
        if (delaySignOut) {
          return new Promise(resolve => {
            releaseSignOut = () => resolve(response(200, { signedOut: true }));
          });
        }
        return response(200, { signedOut: true });
      }
      if (requestUrl === '/bootstrap-mutation' && method === 'POST') {
        bootstrapMutations += 1;
        return response(200, { saved: true });
      }
      if (requestUrl === '/api/asap/staff/legacy/profile' && method === 'POST') {
        return response(200, { staff: { ...selfUser, displayName: 'Newest Profile' } });
      }
      throw new Error(`Unexpected request: ${method} ${requestUrl}`);
    };

    const state = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'state.js')).href);
    const auth = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'app', 'auth.js')).href);
    const http = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'http.js')).href);
    await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'app', 'events.js')).href);
    const loader = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'loader.js')).href);
    const libraryContext = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'library-context.js')).href);
    const staffAccess = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings-users.js')).href);

    state.setStaffSession({ authenticated: false, antiforgeryToken: 'anonymous-token' });
    auth.checkAuth();
    assert.strictEqual(document.getElementById('login-container').classList.contains('hidden'), false);
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true);
    assert.strictEqual(document.getElementById('login-status').classList.contains('hidden'), true);

    const activeSession = {
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'staff-token',
      staff: {
        id: '9007199254740001',
        userPrincipalName: 'signed.in@example.org',
        displayName: 'Signed In',
        notificationEmail: 'signed.in@example.org',
        role: 'staff',
        organizationId: 2,
        organizationName: 'Library Two',
        version: 'session-version'
      }
    };
    state.setStaffSession(activeSession);
    auth.checkAuth();
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), false);
    assert.strictEqual(document.getElementById('login-container').classList.contains('hidden'), true);

    runtimeFailure = 'invalid';
    await assert.rejects(http.authorizedJson('/runtime-probe'), error =>
      error.status === 401 && error.response.code === 'staff_session_invalid');
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true);
    assert.strictEqual(document.getElementById('login-container').classList.contains('hidden'), false);
    assert.match(document.getElementById('login-status').textContent, /session ended/i);
    const sessionEndedMessage = document.getElementById('login-status').textContent;

    state.setStaffSession(activeSession);
    auth.checkAuth();
    runtimeFailure = 'access';
    await assert.rejects(http.authorizedJson('/runtime-probe'), error =>
      error.status === 403 && error.response.accessAllowed === false);
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true);
    assert.match(document.getElementById('login-status').textContent, /signed in.*access is not currently available/i);
    assert.notStrictEqual(document.getElementById('login-status').textContent, sessionEndedMessage);
    assert.strictEqual(document.getElementById('login-sign-out-btn').classList.contains('hidden'), false);

    state.setStaffSession(activeSession);
    auth.checkAuth();
    startupSession = 'invalid';
    await loader.initStaffApp();
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true,
      'A startup session rejection must not leave a blank or stale workspace');
    assert.strictEqual(document.getElementById('login-container').classList.contains('hidden'), false);
    assert.match(document.getElementById('login-status').textContent, /session ended/i);

    startupSession = 'unavailable';
    await loader.initStaffApp();
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true);
    assert.strictEqual(document.getElementById('login-container').classList.contains('hidden'), false);
    assert.match(document.getElementById('login-status').textContent, /signed in.*access is not currently available/i);
    assert.strictEqual(document.getElementById('login-sign-out-btn').classList.contains('hidden'), false);

    state.setStaffSession(activeSession);
    auth.checkAuth();
    startupSession = 'forbidden';
    await loader.initStaffApp();
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true);
    assert.match(document.getElementById('login-status').textContent, /signed in.*access is not currently available/i);

    state.setStaffSession(activeSession);
    auth.checkAuth();
    startupSession = 'error';
    await assert.rejects(loader.initStaffApp(), error =>
      error.status === 500 && error.message === 'Session service unavailable');
    startupSession = null;

    state.setStaffSession(activeSession);
    auth.checkAuth();
    runtimeFailure = 'scoped';
    await assert.rejects(http.authorizedJson('/runtime-probe'), error => error.status === 403);
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), false,
      'An ordinary scoped denial must not masquerade as global access loss');

    state.setStaffSession({
      ...activeSession,
      staff: { ...activeSession.staff, role: 'super_admin', organizationId: 1, organizationName: 'System' }
    });
    runtimeFailure = '';
    await staffAccess.populateStaffLibraryOptions();
    await staffAccess.loadStaffUsers();

    const rows = [...document.querySelectorAll('#staff-users-table-body tr[data-staff-id]')];
    assert.strictEqual(rows.length, 2);
    assert.ok(rows.some(row => row.textContent.includes('Inactive')),
      'Inactive staff must remain visible and identifiable');
    assert.strictEqual(rows[0].getAttribute('data-staff-id'), '9007199254740993',
      'Bigint staff IDs must remain strings in the browser');
    assert.ok(rows[0].textContent.includes('version-active'));
    assert.strictEqual(document.getElementById('staff-add-library').value, '',
      'Creating normal staff from System context must begin without an implicitly selected library');
    assert.strictEqual(document.getElementById('staff-add-library').options[0].textContent, 'Select library');
    assert.strictEqual(rows[0].querySelector('.staff-library-select').value, '2',
      'An existing staff user must retain a valid persisted library');

    state.setCurrentLibraryContextOrgId('2');
    await staffAccess.populateStaffLibraryOptions();
    assert.strictEqual(document.getElementById('staff-add-library').value, '2',
      'A library-specific Settings context must select that library for Add Staff');

    state.setCurrentLibraryContextOrgId('system');
    await staffAccess.populateStaffLibraryOptions();
    assert.strictEqual(document.getElementById('staff-add-library').value, '',
      'Returning to System context must not retain the previous library-specific selection');
    assert.strictEqual(document.getElementById('staff-add-library').options[0].textContent, 'Select library');

    const postsBeforeMissingLibrary = writes.filter(write => write.method === 'POST').length;
    document.getElementById('staff-add-identity').value = 'missing-library@example.org';
    document.getElementById('btn-add-staff-user').click();
    await waitFor(() => document.getElementById('alert-dialog').hasAttribute('open'));
    assert.match(document.getElementById('alert-dialog-message').textContent, /select a library/i);
    assert.strictEqual(writes.filter(write => write.method === 'POST').length, postsBeforeMissingLibrary,
      'Add must not POST when no library was explicitly selected');
    document.getElementById('alert-dialog-ok').click();
    await flush();

    document.getElementById('staff-add-library').value = '2';
    await staffAccess.populateStaffLibraryOptions();
    assert.strictEqual(document.getElementById('staff-add-library').value, '2',
      'A deliberate Add Staff library draft must survive a refresh that remains in System context');

    staffAccess.renderStaffUsers([{
      id: '9007199254740997',
      userPrincipalName: 'super@example.org',
      displayName: 'Existing Super Admin',
      notificationEmail: 'super@example.org',
      role: 'super_admin',
      organizationId: 1,
      active: true,
      version: 'version-super'
    }]);
    const superAdminRow = document.querySelector('tr[data-staff-id="9007199254740997"]');
    const superAdminRole = superAdminRow.querySelector('.staff-role-select');
    superAdminRole.value = 'staff';
    superAdminRole.dispatchEvent(new Event('change'));
    assert.strictEqual(superAdminRow.querySelector('.staff-library-select').value, '',
      'Changing a super-admin to a normal role must not select the first library');
    superAdminRow.querySelector('.staff-role-save').click();
    await waitFor(() => document.getElementById('alert-dialog').hasAttribute('open'));
    assert.match(document.getElementById('alert-dialog-message').textContent, /select a library/i);
    assert.strictEqual(writes.filter(write => write.method === 'POST').length, postsBeforeMissingLibrary,
      'Changing a super-admin without choosing a library must not POST');
    document.getElementById('alert-dialog-ok').click();
    await staffAccess.loadStaffUsers();

    state.setCurrentLibraryContextOrgId('2');
    delayLibraryTwoStaffUsers = true;
    const staleLibraryTwoLoad = staffAccess.loadStaffUsers();
    await waitFor(() => typeof resolveLibraryTwoStaffUsers === 'function');
    state.setCurrentLibraryContextOrgId('3');
    const currentLibraryThreeLoad = staffAccess.loadStaffUsers();
    assert.strictEqual(document.querySelectorAll('#staff-users-table-body tr[data-staff-id]').length, 0,
      'Switching context must immediately remove mutation controls for the previous library');
    await currentLibraryThreeLoad;
    assert.strictEqual(document.querySelector('#staff-users-table-body tr[data-staff-id]')?.getAttribute('data-staff-id'),
      '9007199254740303');
    assert.match(document.getElementById('staff-users-msg').textContent, /Loaded 1 staff user/);
    assert.strictEqual(state.canAssignSuperAdmin, false);

    resolveLibraryTwoStaffUsers();
    await staleLibraryTwoLoad;
    assert.strictEqual(document.querySelector('#staff-users-table-body tr[data-staff-id]')?.getAttribute('data-staff-id'),
      '9007199254740303', 'The delayed library-two response must not replace library three');
    assert.match(document.getElementById('staff-users-msg').textContent, /Loaded 1 staff user/,
      'The delayed response must not overwrite the current load message');
    assert.strictEqual(state.canAssignSuperAdmin, false,
      'The delayed response must not overwrite the current authorization baseline');

    state.setCurrentLibraryContextOrgId('2');
    delayLibraryTwoStaffUsers = true;
    resolveLibraryTwoStaffUsers = undefined;
    const oldestLibraryTwoLoad = staffAccess.loadStaffUsers();
    await waitFor(() => typeof resolveLibraryTwoStaffUsers === 'function');
    state.setCurrentLibraryContextOrgId('3');
    await staffAccess.loadStaffUsers();
    state.setCurrentLibraryContextOrgId('2');
    delayLibraryTwoStaffUsers = false;
    await staffAccess.loadStaffUsers();
    assert.strictEqual(document.querySelectorAll('#staff-users-table-body tr[data-staff-id]').length, 2);
    resolveLibraryTwoStaffUsers();
    await oldestLibraryTwoLoad;
    assert.strictEqual(document.querySelectorAll('#staff-users-table-body tr[data-staff-id]').length, 2,
      'An older A response must not win after an A to B to A sequence');
    assert.doesNotMatch(document.getElementById('staff-users-table-body').textContent, /Stale Library Two/);

    delayLibraryTwoStaffUsers = true;
    resolveLibraryTwoStaffUsers = undefined;
    const olderSameContextLoad = staffAccess.loadStaffUsers();
    await waitFor(() => typeof resolveLibraryTwoStaffUsers === 'function');
    delayLibraryTwoStaffUsers = false;
    await staffAccess.loadStaffUsers();
    resolveLibraryTwoStaffUsers();
    await olderSameContextLoad;
    assert.strictEqual(document.querySelectorAll('#staff-users-table-body tr[data-staff-id]').length, 2,
      'An older same-context refresh must not replace the newer response');
    assert.match(document.getElementById('staff-users-msg').textContent, /Loaded 2 staff users/);

    state.setCurrentLibraryContextOrgId('2');
    delayLibraryTwoOrganizations = true;
    const staleLibraryTwoOrganizations = staffAccess.populateStaffLibraryOptions();
    await waitFor(() => typeof resolveLibraryTwoOrganizations === 'function');
    delayLibraryTwoOrganizations = false;
    useLibraryThreeOrganizations = true;
    state.setCurrentLibraryContextOrgId('3');
    await staffAccess.populateStaffLibraryOptions();
    assert.deepStrictEqual(
      [...document.getElementById('staff-add-library').options].map(option => option.value),
      ['3']
    );
    state.setCurrentLibraryContextOrgId('2');
    await staffAccess.populateStaffLibraryOptions();
    resolveLibraryTwoOrganizations();
    await staleLibraryTwoOrganizations;
    assert.deepStrictEqual(
      [...document.getElementById('staff-add-library').options].map(option => option.value),
      ['3'],
      'An older organization response must not win after an A to B to A sequence'
    );

    delayLibraryTwoSettings = true;
    state.setCurrentLibraryContextOrgId('2');
    const staleLibrarySettingsLoad = libraryContext.loadLibrarySettings('2');
    await waitFor(() => typeof resolveLibraryTwoSettings === 'function');
    const currentLibrarySettingsLoad = libraryContext.loadLibrarySettings('3');
    await currentLibrarySettingsLoad;
    const currentBaseline = state.initialSettingsSnapshot;
    assert.strictEqual(state.lastSavedLibrarySettingsOrgId, '3');
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.uiText.loginNote, 'current-library-three-baseline');
    assert.ok(currentBaseline, 'The current library settings load must capture its baseline');
    resolveLibraryTwoSettings();
    await staleLibrarySettingsLoad;
    assert.strictEqual(state.lastSavedLibrarySettingsOrgId, '3',
      'A delayed loadLibrarySettings response must not replace the current saved scope');
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.uiText.loginNote, 'current-library-three-baseline');
    assert.strictEqual(state.initialSettingsSnapshot, currentBaseline,
      'A delayed loadLibrarySettings response must not overwrite the current settings baseline');
    delayLibraryTwoSettings = false;

    delayLibraryTwoStaffUsers = false;
    useLibraryThreeOrganizations = false;
    state.setCurrentLibraryContextOrgId('system');
    await staffAccess.populateStaffLibraryOptions();
    await staffAccess.loadStaffUsers();

    document.getElementById('staff-add-identity').value = 'delayed-add@example.org';
    document.getElementById('staff-add-role').value = 'staff';
    document.getElementById('staff-add-library').value = '2';
    delayAddStaff = true;
    document.getElementById('btn-add-staff-user').click();
    await waitFor(() => typeof resolveAddStaff === 'function');
    state.setCurrentLibraryContextOrgId('3');
    await staffAccess.loadStaffUsers();
    document.getElementById('staff-add-identity').value = 'current-context-draft@example.org';
    resolveAddStaff();
    await waitFor(() => document.getElementById('btn-add-staff-user').disabled === false);
    assert.strictEqual(document.getElementById('staff-add-identity').value, 'current-context-draft@example.org',
      'A stale add-user completion must not clear the current context draft');
    assert.strictEqual(document.querySelector('#staff-users-table-body tr[data-staff-id]')?.getAttribute('data-staff-id'),
      '9007199254740303', 'A stale add-user completion must not refresh over the current context');
    assert.doesNotMatch(document.getElementById('staff-users-msg').textContent, /Staff user saved/);
    delayAddStaff = false;

    state.setCurrentLibraryContextOrgId('system');
    await staffAccess.populateStaffLibraryOptions();
    await staffAccess.loadStaffUsers();

    let activeRow = document.querySelector('tr[data-staff-id="9007199254740993"]');
    activeRow.querySelector('.staff-authentication-email').value = 'delayed-metadata@example.org';
    activeRow.querySelector('.staff-display-name').value = 'Delayed Metadata';
    activeRow.querySelector('.staff-notification-email').value = 'delayed-notify@example.org';
    const delayedMetadataButton = activeRow.querySelector('.staff-metadata-save');
    delayMetadataPatch = true;
    delayedMetadataButton.click();
    await waitFor(() => typeof resolveMetadataPatch === 'function');
    state.setCurrentLibraryContextOrgId('3');
    await staffAccess.loadStaffUsers();
    assert.strictEqual(state.canAssignSuperAdmin, false);
    assert.match(document.getElementById('staff-users-msg').textContent, /Loaded 1 staff user/);
    resolveMetadataPatch();
    await waitFor(() => delayedMetadataButton.disabled === false);
    assert.strictEqual(document.querySelector('#staff-users-table-body tr[data-staff-id]')?.getAttribute('data-staff-id'),
      '9007199254740303', 'A stale metadata completion must not replace current-context rows');
    assert.match(document.getElementById('staff-users-msg').textContent, /Loaded 1 staff user/,
      'A stale metadata completion must not replace the current load message');
    assert.doesNotMatch(document.getElementById('staff-users-msg').textContent, /Staff profile saved/);
    assert.strictEqual(state.canAssignSuperAdmin, false,
      'A stale metadata completion must not replace the current authorization baseline');
    delayMetadataPatch = false;

    state.setCurrentLibraryContextOrgId('system');
    await staffAccess.populateStaffLibraryOptions();
    await staffAccess.loadStaffUsers();

    document.getElementById('staff-add-identity').value = 'created@example.org';
    document.getElementById('staff-add-role').value = 'staff';
    document.getElementById('staff-add-library').value = '2';
    document.getElementById('btn-add-staff-user').click();
    await waitFor(() => writes.some(write => write.method === 'POST' && write.body.email === 'created@example.org'));
    assert.deepStrictEqual(
      writes.find(write => write.method === 'POST' && write.body.email === 'created@example.org').body,
      { email: 'created@example.org', role: 'staff', organizationId: 2 }
    );

    activeRow = document.querySelector('tr[data-staff-id="9007199254740993"]');
    activeRow.querySelector('.staff-authentication-email').value = 'updated@example.org';
    activeRow.querySelector('.staff-display-name').value = 'Updated Staff';
    activeRow.querySelector('.staff-notification-email').value = 'separate-notify@example.org';
    const otherUserMetadataButton = activeRow.querySelector('.staff-metadata-save');
    const sessionRequestsBeforeOtherUserMetadata = sessionRequestCount;
    otherUserMetadataButton.click();
    await waitFor(() => writes.some(write => write.method === 'PATCH' && write.body.email === 'updated@example.org'));
    await waitFor(() => otherUserMetadataButton.disabled === false);
    assert.strictEqual(sessionRequestCount, sessionRequestsBeforeOtherUserMetadata,
      'Mutating a different StaffUser must not revalidate the current session');
    assert.deepStrictEqual(writes.find(write =>
      write.method === 'PATCH' && write.body.email === 'updated@example.org').body, {
      version: 'version-active',
      email: 'updated@example.org',
      displayName: 'Updated Staff',
      notificationEmail: 'separate-notify@example.org'
    });

    await waitFor(() => document.querySelector('tr[data-staff-id="9007199254740993"]'));
    activeRow = document.querySelector('tr[data-staff-id="9007199254740993"]');
    activeRow.querySelector('.staff-user-deactivate').click();
    await flush();
    assert.match(document.getElementById('confirm-dialog-title').textContent, /Deactivate staff member/);
    document.getElementById('confirm-dialog-ok').click();
    await waitFor(() => writes.some(write => write.method === 'DELETE'));
    assert.deepStrictEqual(writes.find(write => write.method === 'DELETE').body, { version: 'version-active' });
    await waitFor(() => document.querySelector('tr[data-staff-id="9007199254740993"]')?.textContent.includes('Inactive'));

    const usersBeforeUnchangedReactivation = users.length;
    const inactiveRow = document.querySelector('tr[data-staff-id="9007199254740995"]');
    inactiveRow.querySelector('.staff-user-reactivate').click();
    await waitFor(() => writes.some(write => write.method === 'POST' && write.body.email === 'inactive@example.org'));
    assert.deepStrictEqual(
      writes.find(write => write.method === 'POST' && write.body.email === 'inactive@example.org').body,
      { email: 'inactive@example.org', role: 'staff', organizationId: 2, version: 'version-inactive' }
    );
    assert.strictEqual(users.length, usersBeforeUnchangedReactivation,
      'Unchanged-email reactivation must reactivate the existing StaffUser rather than create another');
    assert.strictEqual(users.find(user => user.id === '9007199254740995').active, true);

    users = [...users, {
      id: '9007199254740996',
      userPrincipalName: 'persisted-inactive@example.org',
      displayName: 'Persisted Inactive',
      notificationEmail: 'persisted-inactive@example.org',
      role: 'staff',
      organizationId: 2,
      active: false,
      version: 'version-persisted-inactive'
    }];
    const usersBeforeIdentitySequence = users.length;
    await staffAccess.loadStaffUsers();
    let identitySequenceRow = document.querySelector('tr[data-staff-id="9007199254740996"]');
    identitySequenceRow.querySelector('.staff-authentication-email').value = 'saved-inactive@example.org';
    const postsBeforeEditedReactivation = writes.filter(write => write.method === 'POST').length;
    identitySequenceRow.querySelector('.staff-user-reactivate').click();
    await waitFor(() => document.getElementById('alert-dialog').hasAttribute('open'));
    assert.match(document.getElementById('alert-dialog-message').textContent, /save profile changes first/i,
      'The UI must explain that identity edits have to be saved before reactivation');
    assert.strictEqual(writes.filter(write => write.method === 'POST').length, postsBeforeEditedReactivation,
      'Reactivate must not POST an unsaved edited identity');
    assert.strictEqual(writes.some(write => write.method === 'POST' && write.body.email === 'saved-inactive@example.org'), false);
    document.getElementById('alert-dialog-ok').click();
    await flush();

    identitySequenceRow.querySelector('.staff-metadata-save').click();
    await waitFor(() => writes.some(write =>
      write.method === 'PATCH' &&
      write.url.endsWith('/9007199254740996') &&
      write.body.email === 'saved-inactive@example.org'));
    await waitFor(() => document.querySelector('tr[data-staff-id="9007199254740996"]')?.
      querySelector('.staff-authentication-email')?.value === 'saved-inactive@example.org');
    identitySequenceRow = document.querySelector('tr[data-staff-id="9007199254740996"]');
    identitySequenceRow.querySelector('.staff-user-reactivate').click();
    await waitFor(() => writes.some(write =>
      write.method === 'POST' && write.body.email === 'saved-inactive@example.org'));
    assert.deepStrictEqual(
      writes.find(write => write.method === 'POST' && write.body.email === 'saved-inactive@example.org').body,
      { email: 'saved-inactive@example.org', role: 'staff', organizationId: 2, version: 'version-persisted-inactive' }
    );
    assert.strictEqual(users.length, usersBeforeIdentitySequence,
      'Saving and then reactivating the persisted identity must not create a duplicate StaffUser');
    assert.strictEqual(users.find(user => user.id === '9007199254740996').active, true);

    const selfId = '9007199254740998';
    const selfUser = {
      id: selfId,
      userPrincipalName: 'self@example.org',
      displayName: 'Original Self',
      notificationEmail: 'self-notify@example.org',
      role: 'super_admin',
      organizationId: 1,
      active: true,
      version: 'version-self'
    };
    users = [...users, selfUser];
    function restoreSelfWorkspace() {
      users = users.map(user => user.id === selfId ? { ...selfUser } : user);
      state.setStaffSession({
        authenticated: true,
        accessAllowed: true,
        antiforgeryToken: 'self-token-restored',
        staff: selfUser
      });
      state.setCurrentLibraryContextOrgId('system');
      state.setCurrentStatus('settings');
      auth.checkAuth();
      staffAccess.renderStaffUsers(users);
    }

    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'self-token',
      staff: selfUser
    });
    state.setCurrentLibraryContextOrgId('system');
    await staffAccess.populateStaffLibraryOptions();
    await staffAccess.loadStaffUsers();

    mutationSessionResult = {
      status: 200,
      body: {
        authenticated: true,
        accessAllowed: true,
        antiforgeryToken: 'self-token-refreshed',
        staff: {
          ...selfUser,
          displayName: 'Updated Current User',
          notificationEmail: 'updated-self-notify@example.org',
          version: 'version-self-profile'
        }
      }
    };
    const sessionRequestsBeforeSelfProfile = sessionRequestCount;
    let selfRow = document.querySelector(`tr[data-staff-id="${selfId}"]`);
    selfRow.querySelector('.staff-display-name').value = 'Updated Current User';
    selfRow.querySelector('.staff-notification-email').value = 'updated-self-notify@example.org';
    selfRow.querySelector('.staff-metadata-save').click();
    await waitFor(() => sessionRequestCount > sessionRequestsBeforeSelfProfile);
    assert.strictEqual(state.staffSession.staff.displayName, 'Updated Current User',
      'A successful mutation of the signed-in user must refresh authoritative session profile data');
    assert.match(document.getElementById('display-user').textContent, /Updated Current User/);

    await waitFor(() => document.querySelector(`tr[data-staff-id="${selfId}"]`));
    state.setCurrentStatus('settings');
    mutationSessionResult = {
      status: 200,
      body: {
        authenticated: true,
        accessAllowed: true,
        antiforgeryToken: 'self-token-demoted',
        staff: {
          ...selfUser,
          displayName: 'Updated Current User',
          role: 'staff',
          organizationId: 2,
          organizationName: 'Library Two',
          version: 'version-self-demoted'
        }
      }
    };
    const sessionRequestsBeforeSelfDemotion = sessionRequestCount;
    selfRow = document.querySelector(`tr[data-staff-id="${selfId}"]`);
    const selfRoleSelect = selfRow.querySelector('.staff-role-select');
    selfRoleSelect.value = 'staff';
    selfRoleSelect.dispatchEvent(new Event('change'));
    assert.strictEqual(selfRow.querySelector('.staff-library-select').value, '',
      'A self-demotion from super-admin must also require an explicit library');
    selfRow.querySelector('.staff-library-select').value = '2';
    selfRow.querySelector('.staff-role-save').click();
    await waitFor(() => sessionRequestCount > sessionRequestsBeforeSelfDemotion);
    assert.strictEqual(state.staffSession.staff.role, 'staff');
    assert.strictEqual(document.getElementById('nav-settings').classList.contains('hidden'), true,
      'Contracted self-access must immediately hide the Settings navigation');
    assert.strictEqual(document.getElementById('settings-form').classList.contains('hidden'), true,
      'Contracted self-access must immediately remove stale settings controls');
    assert.strictEqual(document.getElementById('settings-error').classList.contains('hidden'), false);

    restoreSelfWorkspace();
    mutationSessionResult = {
      status: 401,
      body: { code: 'staff_session_invalid' }
    };
    const sessionRequestsBeforeSelfDeactivation = sessionRequestCount;
    selfRow = document.querySelector(`tr[data-staff-id="${selfId}"]`);
    selfRow.querySelector('.staff-user-deactivate').click();
    await waitFor(() => document.getElementById('confirm-dialog').hasAttribute('open'));
    document.getElementById('confirm-dialog-ok').click();
    await waitFor(() => sessionRequestCount > sessionRequestsBeforeSelfDeactivation);
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true,
      'Self-deactivation session invalidation must remove the stale workspace');
    assert.match(document.getElementById('login-status').textContent, /session ended/i);

    restoreSelfWorkspace();
    mutationSessionResult = {
      status: 403,
      body: { code: 'staff_scope_forbidden', accessAllowed: false }
    };
    const sessionRequestsBeforeSelfAccessLoss = sessionRequestCount;
    selfRow = document.querySelector(`tr[data-staff-id="${selfId}"]`);
    selfRow.querySelector('.staff-display-name').value = 'Saved Before Access Loss';
    selfRow.querySelector('.staff-metadata-save').click();
    await waitFor(() => sessionRequestCount > sessionRequestsBeforeSelfAccessLoss);
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true);
    assert.match(document.getElementById('login-status').textContent, /signed in.*access is not currently available/i,
      'Self-mutation access loss must retain the existing access-unavailable UI');

    restoreSelfWorkspace();
    mutationSessionResult = {
      status: 500,
      body: { message: 'Session service unavailable' }
    };
    const selfPatchesBeforeServerFailure = writes.filter(write =>
      write.method === 'PATCH' && write.url.endsWith(`/${selfId}`)).length;
    const sessionRequestsBeforeServerFailure = sessionRequestCount;
    selfRow = document.querySelector(`tr[data-staff-id="${selfId}"]`);
    selfRow.querySelector('.staff-display-name').value = 'Saved Before Server Failure';
    selfRow.querySelector('.staff-metadata-save').click();
    await waitFor(() => /safely refreshed/i.test(document.getElementById('login-status').textContent));
    assert.strictEqual(sessionRequestCount, sessionRequestsBeforeServerFailure + 1,
      'A failed session refresh must not be retried implicitly');
    assert.strictEqual(writes.filter(write =>
      write.method === 'PATCH' && write.url.endsWith(`/${selfId}`)).length, selfPatchesBeforeServerFailure + 1,
      'A failed session refresh must not repeat the successful Staff Access mutation');
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true,
      'Unexpected session refresh failures must hide the stale privileged workspace');
    assert.match(document.getElementById('login-status').textContent,
      /change was saved.*could not be safely refreshed.*sign in again.*revalidate/i);
    assert.doesNotMatch(document.getElementById('staff-users-msg').textContent, /could not be saved|failed to/i,
      'A post-mutation session failure must not describe the saved mutation as failed');

    restoreSelfWorkspace();
    mutationSessionResult = { networkError: true };
    const selfPatchesBeforeNetworkFailure = writes.filter(write =>
      write.method === 'PATCH' && write.url.endsWith(`/${selfId}`)).length;
    const sessionRequestsBeforeNetworkFailure = sessionRequestCount;
    selfRow = document.querySelector(`tr[data-staff-id="${selfId}"]`);
    selfRow.querySelector('.staff-display-name').value = 'Saved Before Network Failure';
    selfRow.querySelector('.staff-metadata-save').click();
    await waitFor(() => /safely refreshed/i.test(document.getElementById('login-status').textContent));
    assert.strictEqual(sessionRequestCount, sessionRequestsBeforeNetworkFailure + 1);
    assert.strictEqual(writes.filter(write =>
      write.method === 'PATCH' && write.url.endsWith(`/${selfId}`)).length, selfPatchesBeforeNetworkFailure + 1);
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true);
    assert.match(document.getElementById('login-status').textContent,
      /change was saved.*could not be safely refreshed.*sign in again.*revalidate/i);

    restoreSelfWorkspace();
    mutationSessionResult = {
      status: 200,
      body: { authenticated: true, accessAllowed: true, staff: null }
    };
    selfRow = document.querySelector(`tr[data-staff-id="${selfId}"]`);
    selfRow.querySelector('.staff-display-name').value = 'Saved Before Malformed Session';
    selfRow.querySelector('.staff-metadata-save').click();
    await waitFor(() => /safely refreshed/i.test(document.getElementById('login-status').textContent));
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true);
    assert.match(document.getElementById('login-status').textContent,
      /change was saved.*could not be safely refreshed.*sign in again.*revalidate/i);

    restoreSelfWorkspace();
    mutationSessionResult = null;
    queueSessionResponses = true;
    const oldSession = { authenticated: true, accessAllowed: true,
      antiforgeryToken: 'old-session-token', staff: { ...selfUser, displayName: 'Old Session' } };
    selfRow = document.querySelector(`tr[data-staff-id="${selfId}"]`);
    selfRow.querySelector('.staff-display-name').value = 'Saved Before Sign-Out';
    selfRow.querySelector('.staff-metadata-save').click();
    await waitFor(() => pendingSessionResponses.length === 1);
    assert.strictEqual(state.staffSession.authenticated, false);
    assert.strictEqual(state.staffSession.staff, null);
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true);
    const revalidationAccessGeneration = state.staffAccessGeneration;
    const revalidationEpoch = state.staffSessionEpoch;
    const signOutRequestsBefore = signOutRequests;
    document.getElementById('login-sign-out-btn').click();
    await waitFor(() => signOutRequests === signOutRequestsBefore + 1);
    assert.strictEqual(state.staffAccessGeneration, revalidationAccessGeneration,
      'Signing out from an already false/null revalidation state does not change the access generation');
    assert.ok(state.staffSessionEpoch > revalidationEpoch,
      'The explicit sign-out still invalidates pending session responses');
    const staffReadsAfterSignOut = staffUserReads;
    pendingSessionResponses.shift()(200, oldSession);
    await flush();
    await flush();
    assert.strictEqual(state.staffSession.authenticated, false);
    assert.strictEqual(state.staffSession.staff, null);
    assert.strictEqual(document.getElementById('app-container').classList.contains('hidden'), true,
      'An old self-update session response must not reopen the signed-out workspace');
    assert.strictEqual(staffUserReads, staffReadsAfterSignOut,
      'Obsolete revalidation must not repopulate Staff Access');
    assert.doesNotMatch(document.getElementById('login-status').textContent, /could not be safely refreshed/i,
      'Superseded revalidation is not an error state');

    for (const staleStatus of [200, 401, 403]) {
      restoreSelfWorkspace();
      const older = http.loadStaffSession();
      await waitFor(() => pendingSessionResponses.length === 1);
      const newer = http.loadStaffSession();
      await waitFor(() => pendingSessionResponses.length === 2);
      const latestSession = { ...oldSession, staff: { ...selfUser, displayName: `Latest ${staleStatus}` } };
      pendingSessionResponses.splice(1, 1)[0](200, latestSession);
      await newer;
      pendingSessionResponses.shift()(staleStatus, staleStatus === 200 ? oldSession :
        staleStatus === 401 ? { code: 'staff_session_invalid' } :
          { code: 'staff_scope_forbidden', accessAllowed: false });
      await assert.rejects(older, error => error.name === 'AbortError');
      assert.strictEqual(state.staffSession.staff.displayName, `Latest ${staleStatus}`,
        'Neither an older success nor an older access error may replace a newer session');
      assert.strictEqual(state.staffSession.authenticated, true);
      assert.strictEqual(state.staffSession.accessAllowed, true);
    }

    restoreSelfWorkspace();
    const oldProfileSession = http.loadStaffSession();
    await waitFor(() => pendingSessionResponses.length === 1);
    document.getElementById('profile-btn').click();
    document.getElementById('profile-form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await waitFor(() => state.staffSession.staff?.displayName === 'Newest Profile');
    pendingSessionResponses.shift()(200, oldSession);
    await assert.rejects(oldProfileSession, error => error.name === 'AbortError');
    assert.strictEqual(state.staffSession.staff.displayName, 'Newest Profile',
      'An older session read must not overwrite the successful profile update');

    restoreSelfWorkspace();
    delaySignOut = true;
    const delayedSignOutBefore = signOutRequests;
    document.getElementById('logout-btn').click();
    await waitFor(() => signOutRequests === delayedSignOutBefore + 1 && !!releaseSignOut);
    assert.strictEqual(state.staffSession.authenticated, false);
    const replacement = { ...oldSession, antiforgeryToken: 'replacement-token',
      staff: { ...selfUser, id: 'replacement', displayName: 'Replacement Staff' } };
    state.setStaffSession(replacement);
    auth.checkAuth();
    releaseSignOut();
    await flush();
    assert.strictEqual(state.staffSession.staff.id, 'replacement',
      'An older sign-out completion must not erase a newer owned session');
    delaySignOut = false;
    releaseSignOut = null;

    restoreSelfWorkspace();
    state.staffSession.antiforgeryToken = '';
    const noTokenSignOutBefore = signOutRequests;
    document.getElementById('logout-btn').click();
    await waitFor(() => pendingSessionResponses.length === 1);
    assert.strictEqual(state.staffSession.authenticated, false);
    pendingSessionResponses.shift()(200, oldSession);
    await waitFor(() => signOutRequests === noTokenSignOutBefore + 1);
    assert.strictEqual(state.staffSession.authenticated, false,
      'The token-only sign-out bootstrap must not reinstall an authenticated session');

    restoreSelfWorkspace();
    state.staffSession.antiforgeryToken = '';
    const supersededSignOutBefore = signOutRequests;
    document.getElementById('logout-btn').click();
    await waitFor(() => pendingSessionResponses.length === 1);
    state.setStaffSession(replacement);
    pendingSessionResponses.shift()(200, oldSession);
    await flush();
    assert.strictEqual(state.staffSession.staff.id, 'replacement');
    assert.strictEqual(signOutRequests, supersededSignOutBefore,
      'A token bootstrap superseded by a newer session must not dispatch an old sign-out');

    restoreSelfWorkspace();
    state.staffSession.antiforgeryToken = '';
    const staleBootstrap = http.authorizedJson('/bootstrap-mutation', { method: 'POST' });
    await waitFor(() => pendingSessionResponses.length === 1);
    state.setStaffSession(replacement);
    pendingSessionResponses.shift()(200, oldSession);
    await assert.rejects(staleBootstrap, error => error.name === 'AbortError');
    assert.strictEqual(bootstrapMutations, 0,
      'A superseded antiforgery bootstrap must not dispatch its mutation');

    restoreSelfWorkspace();
    state.staffSession.antiforgeryToken = '';
    const changedBootstrap = http.authorizedJson('/bootstrap-mutation', { method: 'POST' });
    await waitFor(() => pendingSessionResponses.length === 1);
    pendingSessionResponses.shift()(200, replacement);
    await assert.rejects(changedBootstrap, error => error.name === 'AbortError');
    assert.strictEqual(bootstrapMutations, 0,
      'A bootstrap that discovers a different identity must not dispatch the old mutation');

    console.log('Primary staff Entra session, metadata, concurrency, and lifecycle UI checks passed');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
