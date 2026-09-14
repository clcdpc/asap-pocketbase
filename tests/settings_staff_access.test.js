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
    tenantId: overrides.tenantId || '11111111-1111-1111-1111-111111111111',
    objectId: overrides.objectId || `22222222-2222-2222-2222-2222222222${String(id).padStart(2, '0')}`,
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
      staffUser(20, { displayName: 'Ada Admin', role: 'admin', version: 'user-version-20' }),
      staffUser(21, { displayName: 'Inactive Selector', active: false, version: 'user-version-21' })
    ];
    const requests = [];
    const postBodies = [];
    let patchBody;
    let roleBody;
    let deleteBody;
    let rebindBody;
    const superStaff = {
      id: '1',
      tenantId: '11111111-1111-1111-1111-111111111111',
      objectId: '22222222-2222-2222-2222-222222222201',
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
            { id: 2, name: 'Library Two', active: true, version: 'org-v2' },
            { id: 3, name: 'Library Three', active: true, version: 'org-v3' }
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
            tenantId: body.tenantId,
            objectId: body.objectId,
            userPrincipalName: body.userPrincipalName,
            displayName: body.displayName,
            notificationEmail: body.notificationEmail,
            role: body.role,
            organizationId: body.organizationId,
            active: true,
            version: `created-${postBodies.length}`
          });
          users = [saved, ...users.map(user => user.objectId === body.objectId ? { ...user, active: true } : user)];
          return response(201, {
            user: saved,
            cleanup: { rulesDeactivated: 2, openTitleClaimsCleared: 3, openAdditionalCopyClaimsCleared: 4 }
          });
        }
        if (requestUrl === '/api/asap/staff/users/20' && options.method === 'PATCH') {
          patchBody = JSON.parse(options.body);
          return response(200, {
            user: { ...users[0], userPrincipalName: patchBody.userPrincipalName, version: 'patched-20' },
            cleanup: { rulesDeactivated: 0, openTitleClaimsCleared: 0, openAdditionalCopyClaimsCleared: 0 }
          });
        }
        if (requestUrl === '/api/asap/staff/users/20/role') {
          roleBody = JSON.parse(options.body);
          return response(200, {
            user: { ...users[0], role: roleBody.role, organizationId: roleBody.organizationId, version: 'role-20' },
            cleanup: { rulesDeactivated: 1, openTitleClaimsCleared: 0, openAdditionalCopyClaimsCleared: 0 }
          });
        }
        if (requestUrl === '/api/asap/staff/users/20' && options.method === 'DELETE') {
          deleteBody = JSON.parse(options.body);
          return response(200, {
            user: { ...users[0], active: false, version: 'inactive-20' },
            cleanup: { rulesDeactivated: 5, openTitleClaimsCleared: 6, openAdditionalCopyClaimsCleared: 7 }
          });
        }
        if (requestUrl === '/api/asap/staff/users/20/rebind') {
          rebindBody = JSON.parse(options.body);
          return response(200, {
            user: { ...users[0], tenantId: rebindBody.tenantId, objectId: rebindBody.objectId, version: 'rebound-20' },
            cleanup: { rulesDeactivated: 0, openTitleClaimsCleared: 0, openAdditionalCopyClaimsCleared: 0 }
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

    document.getElementById('staff-add-display-name').value = 'Unsaved Staff Draft';
    document.getElementById('staff-add-display-name').dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.strictEqual(controller.isDirty(), false);

    document.getElementById('staff-add-tenant-id').value = '11111111-1111-1111-1111-111111111111';
    document.getElementById('staff-add-object-id').value = '33333333-3333-3333-3333-333333333333';
    document.getElementById('staff-add-upn').value = 'created@example.org';
    document.getElementById('staff-add-display-name').value = 'Created Staff';
    document.getElementById('staff-add-notification-email').value = 'created-notify@example.org';
    document.getElementById('staff-add-role').value = 'staff';
    document.getElementById('staff-add-organization').value = '2';
    document.getElementById('staff-add-submit').click();
    await waitFor(() => postBodies.length === 1);
    assert.deepStrictEqual(postBodies[0], {
      tenantId: '11111111-1111-1111-1111-111111111111',
      objectId: '33333333-3333-3333-3333-333333333333',
      userPrincipalName: 'created@example.org',
      displayName: 'Created Staff',
      notificationEmail: 'created-notify@example.org',
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
    assert.strictEqual(patchBody.userPrincipalName, 'ada.updated@example.org');

    activeRow.querySelector('select[aria-label^="Role"]').value = 'staff';
    [...activeRow.querySelectorAll('button')].find(button => button.textContent === 'Update access').click();
    await waitFor(() => Boolean(roleBody));
    assert.deepStrictEqual(roleBody, { version: 'user-version-20', role: 'staff', organizationId: 2 });

    [...activeRow.querySelectorAll('button')].find(button => button.textContent === 'Deactivate').click();
    await waitFor(() => Boolean(deleteBody));
    assert.strictEqual(deleteBody.version, 'user-version-20');
    await waitFor(() => document.getElementById('staff-access-status').textContent.includes('5 auto-claim rules deactivated'));

    const prompts = [
      '11111111-1111-1111-1111-111111111111',
      '44444444-4444-4444-4444-444444444444',
      'Correct imported identity'
    ];
    window.prompt = () => prompts.shift();
    [...activeRow.querySelectorAll('button')].find(button => button.textContent === 'Rebind identity').click();
    await waitFor(() => Boolean(rebindBody));
    assert.strictEqual(rebindBody.version, 'user-version-20');
    assert.strictEqual(rebindBody.confirmed, true);
    assert.strictEqual(rebindBody.reason, 'Correct imported identity');

    const inactiveRow = [...document.querySelectorAll('.settings-staff-row')]
      .find(row => row.textContent.includes('Inactive Selector'));
    [...inactiveRow.querySelectorAll('button')].find(button => button.textContent === 'Reactivate').click();
    await waitFor(() => postBodies.length === 2);
    assert.strictEqual(postBodies[1].objectId, users.find(user => user.displayName === 'Inactive Selector')?.objectId);

    dom.window.close();

    const adminRequests = [];
    const adminStaff = {
      id: '2',
      tenantId: '11111111-1111-1111-1111-111111111111',
      objectId: '22222222-2222-2222-2222-222222222202',
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
    admin.dom.window.close();

    console.log('Settings Staff Access roster, lifecycle, audit, and scope checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
