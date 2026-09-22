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
      if (requestUrl === '/api/asap/staff/session') {
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
        throw new Error('Unexpected session request');
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
      if (requestUrl.includes('/api/asap/staff/title-requests')) {
        return response(200, { items: [], scope: '2', availableLibraries: [] });
      }
      if (requestUrl.includes('/api/asap/staff/additional-copies')) {
        return response(200, { items: [], scope: '2', availableLibraries: [] });
      }
      if (requestUrl === '/api/asap/staff/organizations') {
        return response(200, {
          code: 'ok',
          data: [
            { id: 1, displayName: 'System', active: true },
            { id: 2, displayName: 'Library Two', active: true },
            { id: 3, displayName: 'Library Three', active: true }
          ]
        });
      }
      if (requestUrl === '/api/asap/staff/users' && method === 'GET') {
        return response(200, { canAssignSuperAdmin: true, users });
      }
      if (requestUrl.startsWith('/api/asap/staff/users') && method !== 'GET') {
        const body = JSON.parse(options.body || '{}');
        writes.push({ url: requestUrl, method, body });
        if (method === 'PATCH') {
          users = users.map(user => user.id === '9007199254740993'
            ? { ...user, userPrincipalName: body.email, displayName: body.displayName, notificationEmail: body.notificationEmail }
            : user);
        } else if (method === 'DELETE') {
          users = users.map(user => user.id === '9007199254740993'
            ? { ...user, active: false, version: 'version-deactivated' }
            : user);
        } else if (method === 'POST' && body.email === 'inactive@example.org') {
          users = users.map(user => user.id === '9007199254740995'
            ? { ...user, active: true, version: 'version-reactivated' }
            : user);
        }
        return response(method === 'POST' && requestUrl === '/api/asap/staff/users' ? 201 : 200, {
          user: users[0],
          cleanup: { rulesDeactivated: 1, openTitleClaimsCleared: 2, openAdditionalCopyClaimsCleared: 3 }
        });
      }
      throw new Error(`Unexpected request: ${method} ${requestUrl}`);
    };

    const state = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'state.js')).href);
    const auth = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'app', 'auth.js')).href);
    const http = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'http.js')).href);
    await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'app', 'events.js')).href);
    const loader = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'loader.js')).href);
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

    document.getElementById('staff-add-identity').value = 'created@example.org';
    document.getElementById('staff-add-role').value = 'staff';
    document.getElementById('staff-add-library').value = '2';
    document.getElementById('btn-add-staff-user').click();
    await waitFor(() => writes.some(write => write.method === 'POST' && write.body.email === 'created@example.org'));
    assert.deepStrictEqual(
      writes.find(write => write.method === 'POST' && write.body.email === 'created@example.org').body,
      { email: 'created@example.org', role: 'staff', organizationId: 2 }
    );

    let activeRow = document.querySelector('tr[data-staff-id="9007199254740993"]');
    activeRow.querySelector('.staff-authentication-email').value = 'updated@example.org';
    activeRow.querySelector('.staff-display-name').value = 'Updated Staff';
    activeRow.querySelector('.staff-notification-email').value = 'separate-notify@example.org';
    activeRow.querySelector('.staff-metadata-save').click();
    await waitFor(() => writes.some(write => write.method === 'PATCH'));
    assert.deepStrictEqual(writes.find(write => write.method === 'PATCH').body, {
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

    const inactiveRow = document.querySelector('tr[data-staff-id="9007199254740995"]');
    inactiveRow.querySelector('.staff-user-reactivate').click();
    await waitFor(() => writes.some(write => write.method === 'POST' && write.body.email === 'inactive@example.org'));
    assert.deepStrictEqual(
      writes.find(write => write.method === 'POST' && write.body.email === 'inactive@example.org').body,
      { email: 'inactive@example.org', role: 'staff', organizationId: 2 }
    );

    console.log('Primary staff Entra session, metadata, concurrency, and lifecycle UI checks passed');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
