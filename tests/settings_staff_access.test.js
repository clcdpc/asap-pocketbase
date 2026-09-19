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
    statusText: 'OK',
    json: async () => body
  };
}

function settingsData(orgId = 'system') {
  const emptySet = { exists: false, values: [] };
  return {
    orgId,
    version: `${orgId}-version`,
    organization: orgId === 'system' ? null : { id: Number(orgId), name: 'Library Two', active: true },
    stored: {
      systemSettings: {},
      polaris: {},
      configuredSystem: {
        workflow: {},
        patron: {},
        email: { fromAddress: 'system@example.org', fromName: 'System' },
        publicationOptions: emptySet,
        commonCreators: emptySet,
        allowedPatronCodeIds: emptySet,
        providers: [],
        formats: [],
        templates: [],
        branding: { hasLogo: false, altText: 'System alt' }
      },
      libraryOverride: null,
      workflow: {},
      patron: {},
      email: {},
      origins: [],
      publicationOptions: [],
      commonCreators: [],
      allowedPatronCodeIds: [],
      providers: [],
      formats: [],
      customFields: [],
      templates: [],
      autoClaimRules: [],
      branding: { hasLogo: false, altText: null }
    },
    effective: {
      allowedPatronCodeIds: [],
      publicationOptions: [],
      commonCreators: [],
      externalSearchProviders: [],
      formats: [],
      customFields: [],
      email: { fromAddress: 'system@example.org', fromName: 'System' },
      logoAltText: 'System alt'
    },
    workflow: {},
    ui_text: {},
    emails: { fromAddress: 'system@example.org', fromName: 'System', templates: [] }
  };
}

function staffUser(id, overrides = {}) {
  return {
    id: String(id),
    userPrincipalName: overrides.userPrincipalName || `staff${id}@example.org`,
    displayName: overrides.displayName || `Staff ${id}`,
    notificationEmail: overrides.notificationEmail || `staff${id}@example.org`,
    role: overrides.role || 'staff',
    organizationId: overrides.organizationId || 2,
    active: overrides.active !== false,
    version: overrides.version || `v${id}`
  };
}

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
  await Promise.resolve();
}

async function waitFor(predicate) {
  for (let attempt = 0; attempt < 40; attempt += 1) {
    await flush();
    if (predicate()) return;
  }
  assert.ok(predicate(), 'Timed out waiting for expected UI state');
}

async function setupController(settingsModule, frontendRoot, staff, fetchHandler) {
  const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
    url: 'http://localhost/staff/'
  });
  global.window = dom.window;
  global.document = dom.window.document;
  global.FormData = dom.window.FormData;
  global.Node = dom.window.Node;
  global.Event = dom.window.Event;
  window.confirm = () => true;
  global.fetch = fetchHandler;

  const controller = settingsModule.createSettingsController({
    root: document.getElementById('settings-view'),
    tab: document.getElementById('settings-view-tab'),
    announce: () => {},
    getStaff: () => staff
  });
  controller.bind();
  controller.setStaff(staff);
  await controller.activate();
  return { dom, controller };
}

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-staff-access-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const settingsModule = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings.js')).href);
    let users = [
      staffUser(21, { displayName: 'Inactive Selector', active: false, version: 'user-version-21' }),
      staffUser(20, { displayName: 'Ada Admin', role: 'admin', version: 'user-version-20' })
    ];
    const requests = [];
    const postBodies = [];
    let patchBody;
    let roleBody;
    let deleteBody;
    const superStaff = {
      id: '1',
      role: 'super_admin',
      organizationId: 1
    };
    const { dom, controller } = await setupController(
      settingsModule,
      frontendRoot,
      superStaff,
      async (url, options = {}) => {
        const requestUrl = String(url);
        requests.push(requestUrl);
        if (requestUrl.endsWith('/api/asap/staff/session')) {
          return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
        }
        if (requestUrl.includes('/api/asap/staff/settings?orgId=')) return response(200, settingsData('system'));
        if (requestUrl.endsWith('/api/asap/staff/organizations')) {
          return response(200, [
            { id: 1, name: 'System', active: true, version: 'org-v1' },
            { id: 5, name: 'Zeta Library', active: true, version: 'org-v5' },
            { id: 4, name: null, active: true, version: 'org-v4' },
            { id: 3, name: 'alpha library', active: true, version: 'org-v3' },
            { id: 2, name: 'Alpha Library', active: true, version: 'org-v2' }
          ]);
        }
        if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
        if (requestUrl === '/api/asap/staff/users' && (options.method || 'GET') === 'GET') {
          return response(200, { canAssignSuperAdmin: true, users });
        }
        if (requestUrl === '/api/asap/staff/audit?limit=50') {
          return response(200, { code: 'ok', data: [
            {
              id: '101',
              actorStaffUserId: '1',
              actorName: 'System Admin',
              organizationId: 2,
              action: 'staff_created',
              targetType: 'StaffUser',
              targetId: '20',
              detailsJson: '{"role":"admin"}',
              createdUtc: '2026-09-14T12:00:00Z'
            }
          ] });
        }
        if (requestUrl === '/api/asap/staff/users' && options.method === 'POST') {
          const body = JSON.parse(options.body);
          postBodies.push(body);
          const saved = staffUser(30 + postBodies.length, {
            userPrincipalName: body.email,
            displayName: null,
            notificationEmail: body.email,
            role: body.role,
            organizationId: body.organizationId,
            active: true,
            version: `created-${postBodies.length}`
          });
          users = [saved, ...users.map(user => user.userPrincipalName.toLowerCase() === body.email.toLowerCase() ? { ...user, active: true } : user)];
          return response(201, {
            user: saved,
            cleanup: { rulesDeactivated: 2, openTitleClaimsCleared: 3, openAdditionalCopyClaimsCleared: 4 }
          });
        }
        if (requestUrl === '/api/asap/staff/users/20' && options.method === 'PATCH') {
          patchBody = JSON.parse(options.body);
          return response(200, {
            user: { ...users.find(user => user.id === '20'), userPrincipalName: patchBody.email, version: 'patched-20' },
            cleanup: { rulesDeactivated: 0, openTitleClaimsCleared: 0, openAdditionalCopyClaimsCleared: 0 }
          });
        }
        if (requestUrl === '/api/asap/staff/users/20/role') {
          roleBody = JSON.parse(options.body);
          return response(200, {
            user: { ...users.find(user => user.id === '20'), role: roleBody.role, organizationId: roleBody.organizationId, version: 'role-20' },
            cleanup: { rulesDeactivated: 1, openTitleClaimsCleared: 0, openAdditionalCopyClaimsCleared: 0 }
          });
        }
        if (requestUrl === '/api/asap/staff/users/20' && options.method === 'DELETE') {
          deleteBody = JSON.parse(options.body);
          return response(200, {
            user: { ...users.find(user => user.id === '20'), active: false, version: 'inactive-20' },
            cleanup: { rulesDeactivated: 5, openTitleClaimsCleared: 6, openAdditionalCopyClaimsCleared: 7 }
          });
        }
        throw new Error(`Unexpected request: ${requestUrl}`);
      }
    );

    assert.ok(!requests.some(url => url.includes('/api/asap/staff/users')));
    document.getElementById('settings-nav-staff').click();
    await waitFor(() => document.getElementById('settings-staff-users-list').textContent.includes('Ada Admin'));
    assert.ok(requests.includes('/api/asap/staff/users'));
    assert.ok(requests.includes('/api/asap/staff/audit?limit=50'));
    assert.ok(document.getElementById('settings-staff-audit-list').textContent.includes('staff_created'));
    assert.strictEqual(document.querySelectorAll('#settings-staff .settings-override-control').length, 0);
    assert.deepStrictEqual(
      [...document.getElementById('settings-scope').options].map(option => [option.value, option.textContent]),
      [
        ['system', 'System level'],
        ['2', 'Alpha Library'],
        ['3', 'alpha library'],
        ['4', 'Library 4'],
        ['5', 'Zeta Library']
      ]
    );
    assert.deepStrictEqual(
      [...document.querySelectorAll('#enabled-libraries-checkbox-container .settings-list-row span')]
        .map(item => item.textContent),
      ['Alpha Library', 'alpha library', 'Library 4', 'Zeta Library']
    );
    assert.deepStrictEqual(
      [...document.querySelectorAll('#settings-organizations-list .settings-organization-name')]
        .map(item => item.textContent),
      ['Alpha Library', 'alpha library', 'Library 4', 'System', 'Zeta Library']
    );
    assert.deepStrictEqual(
      [...document.getElementById('staff-add-organization').options].map(option => [option.value, option.textContent]),
      [['2', 'Alpha Library'], ['3', 'alpha library'], ['4', 'Library 4'], ['5', 'Zeta Library']]
    );
    assert.deepStrictEqual(
      [...document.querySelectorAll('#settings-staff-users-list .settings-staff-heading strong')]
        .map(item => item.textContent),
      ['Ada Admin', 'Inactive Selector']
    );

    assert.strictEqual(document.getElementById('staff-add-tenant-id'), null);
    assert.strictEqual(document.getElementById('staff-add-object-id'), null);
    assert.strictEqual(document.getElementById('staff-add-display-name'), null);
    assert.strictEqual(document.getElementById('staff-add-notification-email'), null);
    document.getElementById('staff-add-email').value = 'created@example.org';
    document.getElementById('staff-add-role').value = 'staff';
    document.getElementById('staff-add-organization').value = '2';
    document.getElementById('staff-add-submit').click();
    await waitFor(() => postBodies.length === 1);
    assert.deepStrictEqual(postBodies[0], {
      email: 'created@example.org',
      role: 'staff',
      organizationId: 2
    });
    await waitFor(() => document.getElementById('staff-access-status').textContent.includes('2 auto-claim rules deactivated'));

    const activeRow = [...document.querySelectorAll('.settings-staff-row')]
      .find(row => row.textContent.includes('Ada Admin'));
    activeRow.querySelector('input[type="email"]').value = 'ada.updated@example.org';
    [...activeRow.querySelectorAll('button')].find(button => button.textContent === 'Save profile').click();
    await waitFor(() => Boolean(patchBody));
    assert.strictEqual(patchBody.version, 'user-version-20');
    assert.strictEqual(patchBody.email, 'ada.updated@example.org');

    activeRow.querySelector('select[aria-label^="Role"]').value = 'staff';
    [...activeRow.querySelectorAll('button')].find(button => button.textContent === 'Update access').click();
    await waitFor(() => Boolean(roleBody));
    assert.deepStrictEqual(roleBody, { version: 'user-version-20', role: 'staff', organizationId: 2 });

    [...activeRow.querySelectorAll('button')].find(button => button.textContent === 'Deactivate').click();
    await waitFor(() => Boolean(deleteBody));
    assert.strictEqual(deleteBody.version, 'user-version-20');
    await waitFor(() => document.getElementById('staff-access-status').textContent.includes('5 auto-claim rules deactivated'));

    assert.strictEqual([...activeRow.querySelectorAll('button')].some(button => button.textContent === 'Rebind identity'), false);

    const inactiveRow = [...document.querySelectorAll('.settings-staff-row')]
      .find(row => row.textContent.includes('Inactive Selector'));
    [...inactiveRow.querySelectorAll('button')].find(button => button.textContent === 'Reactivate').click();
    await waitFor(() => postBodies.length === 2);
    assert.strictEqual(postBodies[1].email, 'staff21@example.org');

    dom.window.close();

    const adminRequests = [];
    const adminStaff = {
      id: '2',
      role: 'admin',
      organizationId: 2
    };
    const admin = await setupController(
      settingsModule,
      frontendRoot,
      adminStaff,
      async (url) => {
        const requestUrl = String(url);
        adminRequests.push(requestUrl);
        if (requestUrl.includes('/api/asap/staff/settings?orgId=')) return response(200, settingsData('2'));
        if (requestUrl.endsWith('/api/asap/staff/organizations')) return response(200, [{ id: 2, name: 'Library Two', active: true, version: 'org-v2' }]);
        if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
        if (requestUrl === '/api/asap/staff/users?orgId=2') return response(200, { canAssignSuperAdmin: false, users: [staffUser(22)] });
        if (requestUrl === '/api/asap/staff/audit?limit=50&organizationId=2') return response(200, { code: 'ok', data: [] });
        throw new Error(`Unexpected admin request: ${requestUrl}`);
      }
    );
    document.getElementById('settings-nav-staff').click();
    await waitFor(() => adminRequests.includes('/api/asap/staff/users?orgId=2'));
    assert.ok(adminRequests.includes('/api/asap/staff/audit?limit=50&organizationId=2'));
    assert.strictEqual(document.getElementById('staff-add-role').textContent.includes('Super admin'), false);
    assert.strictEqual(document.getElementById('staff-add-organization').disabled, true);
    assert.strictEqual([...document.querySelectorAll('.settings-staff-row button')].some(button => button.textContent === 'Rebind identity'), false);
    admin.dom.window.close();

    console.log('Settings Staff Access roster, lifecycle, audit, and scope checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
