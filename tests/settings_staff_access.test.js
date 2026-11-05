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
    organization: orgId === 'system' ? null : {
      id: Number(orgId), name: 'Library Two', abbreviation: 'TWO', active: true, version: `${orgId}-version`
    },
    stored: {
      systemSettings: { enabledLibraryOrgIds: [2, 3], libraryOrgIds: [2, 3] },
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
      formatRules: [],
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
    emails: { fromAddress: 'system@example.org', fromName: 'System', templates: [] },
    autoClaimStaff: []
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

function openRow(row) {
  assert.equal(row.hidden, false, 'Reveal an inactive account before opening its editor.');
  const disclosure = row.querySelector('.settings-staff-summary');
  if (disclosure.getAttribute('aria-expanded') === 'false') disclosure.click();
  return row;
}

function edit(control, value) {
  control.value = value;
  control.dispatchEvent(new window.Event(control.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true }));
}

async function setupController(settingsModule, frontendRoot, staff, fetchHandler, callbacks = {}) {
  const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
    url: 'http://localhost/staff/'
  });
  global.window = dom.window;
  global.document = dom.window.document;
  global.FormData = dom.window.FormData;
  global.Node = dom.window.Node;
  global.Event = dom.window.Event;
  require('./helpers/confirmation.cjs').answerConfirmations(window, () => true);
  global.fetch = fetchHandler;

  const controller = settingsModule.createSettingsController({
    root: document.getElementById('settings-view'),
    tab: document.getElementById('settings-view-tab'),
    announce: () => {},
    getStaff: () => staff,
    ...callbacks
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
      { ...staffUser(22), displayName: '', userPrincipalName: 'aaron@example.org' },
      staffUser(20, { displayName: 'Ada Admin', role: 'admin', version: 'user-version-20' })
    ];
    const requests = [];
    const postBodies = [];
    let patchBody;
    let reactivateBody;
    let deleteBody;
    const superStaff = {
      id: '1',
      role: 'super_admin',
      organizationId: 1
    };
    let revokeStaffRefresh = false;
    let holdRoster = false, releaseRoster, loseProfileResponse = false, failRoster = false;
    const committedMessages = [];
    let refreshedCount = 0;
    const auditNavigation = [];
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
          return response(200, { code: 'ok', data: [
            { id: 1, name: 'System', abbreviation: null, organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org-v1' },
            { id: 2, name: 'Library Two', abbreviation: 'TWO', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-v2' },
            { id: 3, name: 'Library Three', abbreviation: 'THREE', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-v3' }
          ] });
        }
        if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
        if (requestUrl === '/api/asap/staff/users' && (options.method || 'GET') === 'GET') {
          if (revokeStaffRefresh) return response(401, { code: 'staff_session_invalid' });
          if (failRoster) return response(503, { message: 'Roster unavailable' });
          if (holdRoster) return new Promise(resolve => { releaseRoster = () => resolve(response(200, { canAssignSuperAdmin: true, users })); });
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
          const existing = users.find(user => user.userPrincipalName.toLowerCase() === body.email.toLowerCase());
          const saved = existing && !existing.active
            ? { ...existing, role: body.role, organizationId: body.organizationId, active: true, version: `reactivated-${existing.id}` }
            : staffUser(postBodies.length === 1 ? '9223372036854775807' : 30 + postBodies.length, {
              userPrincipalName: body.email,
              displayName: null,
              notificationEmail: body.email,
              role: body.role,
              organizationId: body.organizationId,
              active: true,
              version: `created-${postBodies.length}`
            });
          users = [saved, ...users.filter(user => user.id !== saved.id)];
          return response(201, {
            user: saved,
            cleanup: { rulesDeactivated: 2, openTitleClaimsCleared: 3, openAdditionalCopyClaimsCleared: 4 }
          });
        }
        if (requestUrl === '/api/asap/staff/users/20' && options.method === 'PUT') {
          if (loseProfileResponse) throw new Error('Lost staff profile response');
          patchBody = JSON.parse(options.body);
          const saved = { ...users.find(user => user.id === '20'), userPrincipalName: patchBody.email,
            notificationEmail: patchBody.email, displayName: patchBody.displayName, role: patchBody.role,
            organizationId: patchBody.organizationId, version: `updated-${patchBody.role}` };
          users = users.map(user => user.id === '20' ? saved : user);
          return response(200, {
            user: saved,
            cleanup: { rulesDeactivated: 0, openTitleClaimsCleared: 0, openAdditionalCopyClaimsCleared: 0 }
          });
        }
        if (requestUrl === '/api/asap/staff/users/21/reactivate') {
          reactivateBody = JSON.parse(options.body);
          const saved = { ...users.find(user => user.id === '21'), active: true, version: 'reactivated-21' };
          users = users.map(user => user.id === '21' ? saved : user);
          return response(200, {
            user: saved,
            cleanup: { rulesDeactivated: 1, openTitleClaimsCleared: 0, openAdditionalCopyClaimsCleared: 0 }
          });
        }
        if (requestUrl === '/api/asap/staff/users/20' && options.method === 'DELETE') {
          deleteBody = JSON.parse(options.body);
          const saved = { ...users.find(user => user.id === '20'), active: false, version: 'inactive-20' };
          users = users.map(user => user.id === '20' ? saved : user);
          return response(200, {
            user: saved,
            cleanup: { rulesDeactivated: 5, openTitleClaimsCleared: 6, openAdditionalCopyClaimsCleared: 7 }
          });
        }
        throw new Error(`Unexpected request: ${requestUrl}`);
      },
      {
        onCommitted: message => committedMessages.push(message),
        onStaffAuditHistory: (...args) => auditNavigation.push(args),
        onRefreshed: () => { refreshedCount += 1; }
      }
    );

    assert.ok(!requests.some(url => url.includes('/api/asap/staff/users')));
    assert.deepStrictEqual([...document.querySelectorAll('#settings-scope option')].map(option => option.textContent),
      ['System level', 'Library Three', 'Library Two']);
    assert.deepStrictEqual([...document.querySelectorAll('#settings-organizations-list .settings-organization-name')].map(span => span.textContent),
      ['Library Three (THREE)', 'Library Two (TWO)']);
    assert.equal(document.getElementById('settings-message').textContent, '', 'A successful routine load clears its own loading status.');
    document.getElementById('settings-nav-staff').click();
    await waitFor(() => document.getElementById('settings-staff-users-list').textContent.includes('Ada Admin'));
    assert.ok(requests.includes('/api/asap/staff/users'));
    assert.equal(requests.some(url => url.includes('/api/asap/staff/audit')), false);
    assert.equal(document.getElementById('settings-staff-audit-list'), null);
    assert.equal(document.getElementById('staff-audit-refresh'), null);
    document.getElementById('staff-audit-history').click();
    assert.equal(auditNavigation[0][0], null, 'System Staff roster intentionally opens aggregate authorized history');
    assert.deepStrictEqual([...document.querySelectorAll('.settings-staff-heading strong')].map(item => item.textContent),
      ['aaron@example.org', 'Ada Admin', 'Inactive Selector'], 'Sort the actual visible fallback name, not empty displayName.');
    const inactiveFilter = document.getElementById('staff-show-inactive');
    assert.equal(inactiveFilter.checked, false);
    assert.deepEqual([...document.querySelectorAll('.settings-staff-row')].filter(row => !row.hidden).map(row => row.dataset.staffId), ['22', '20']);
    assert.equal(document.getElementById('staff-roster-counts').textContent, '2 active accounts · 1 inactive hidden');
    inactiveFilter.click();
    assert.equal(document.querySelector('[data-staff-id="21"]').hidden, false);
    assert.equal(document.getElementById('staff-roster-counts').textContent, '2 active accounts · 1 inactive account shown');
    inactiveFilter.click();
    assert.equal(controller.isDirty(), false, 'A roster display preference is never a Settings draft.');
    assert.deepStrictEqual([...document.querySelectorAll('#staff-add-organization option')].map(option => option.textContent),
      ['Library Three', 'Library Two']);
    assert.equal(document.getElementById('staff-access-status').textContent, '', 'Roster loading completion is quiet.');
    assert.strictEqual(document.querySelectorAll('#settings-staff .settings-override-control').length, 0);

    const createEmail = document.getElementById('staff-add-email');
    document.getElementById('staff-add-toggle').click();
    document.getElementById('staff-add-submit').click();
    assert.equal(document.getElementById('staff-access-status').textContent, 'Email address is required.');
    edit(createEmail, 'invalid-address');
    assert.equal(document.getElementById('staff-access-status').textContent, '', 'Nonempty partial input retires its obsolete required-field notice.');
    document.getElementById('staff-add-submit').click();
    assert.equal(createEmail.validity.valid, false);
    assert.equal(postBodies.length, 0, 'Native invalid-email validation prevents a write.');
    edit(createEmail, 'corrected@example.org');
    assert.equal(document.getElementById('staff-access-status').textContent, '', 'A valid correction clears only the field-owned notice.');
    edit(createEmail, '');
    document.getElementById('staff-add-submit').click();
    createEmail.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    assert.equal(document.getElementById('staff-add-form').hidden, true);
    assert.equal(document.getElementById('staff-access-status').textContent, '', 'Closing Add retires only its owned required notice.');
    document.getElementById('staff-add-toggle').click();
    edit(createEmail, 'retained.partial');
    createEmail.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    document.getElementById('staff-add-toggle').click();
    assert.equal(createEmail.value, 'retained.partial', 'Escape retains the creation draft.');
    edit(createEmail, ''); document.getElementById('staff-add-submit').click();
    document.getElementById('staff-add-cancel').click();
    assert.equal(document.getElementById('staff-access-status').textContent, '', 'Cancel retires the owned required notice.');
    document.getElementById('staff-add-toggle').click();
    assert.equal(createEmail.value, '', 'Cancel resets the creation draft.');
    document.getElementById('staff-add-submit').click();
    failRoster = true;
    document.getElementById('staff-refresh').click();
    await waitFor(() => document.getElementById('staff-access-status').textContent === 'Roster unavailable');
    edit(createEmail, 'corrected@example.org');
    assert.equal(document.getElementById('staff-access-status').textContent, 'Roster unavailable', 'Correcting the email preserves a newer server failure.');
    document.getElementById('staff-add-cancel').click();
    assert.equal(document.getElementById('staff-access-status').textContent, 'Roster unavailable', 'Closing Add preserves newer server feedback.');
    failRoster = false;
    document.getElementById('staff-refresh').click();
    await waitFor(() => !document.getElementById('staff-refresh').disabled && document.getElementById('staff-access-status').textContent === '');

    assert.strictEqual(document.getElementById('staff-add-tenant-id'), null);
    assert.strictEqual(document.getElementById('staff-add-object-id'), null);
    assert.strictEqual(document.getElementById('staff-add-display-name'), null);
    assert.strictEqual(document.getElementById('staff-add-notification-email'), null);
    assert.equal(document.getElementById('staff-add-email').maxLength, 320, 'Create email respects the stored column bound');
    const initialRow = [...document.querySelectorAll('.settings-staff-row')]
      .find(row => row.textContent.includes('Ada Admin'));
    assert.equal(controller.isDirty(), false, 'loading collapsed editors does not dirty Settings');
    assert.ok([...document.querySelectorAll('.settings-staff-editor')].every(editor => editor.hidden && editor.disabled));
    openRow(initialRow);
    const initialEmail = initialRow.querySelector('input[type="email"]');
    assert.equal(initialEmail.maxLength, 320, 'the unified row email uses the same column bound');
    const originalEmail = initialEmail.value;
    const rowButton = (owner, text) => [...owner.querySelectorAll('button')].find(item => item.textContent === text);
    const staffStatus = document.getElementById('staff-access-status');
    for (const action of ['Cancel', 'Escape']) {
      edit(initialEmail, ''); rowButton(initialRow, 'Save changes').click();
      assert.equal(staffStatus.textContent, 'Email address is required.');
      assert.equal(patchBody, undefined, 'A blank existing-row email cannot dispatch a write.');
      if (action === 'Cancel') rowButton(initialRow, 'Cancel').click();
      else initialEmail.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      assert.equal(staffStatus.textContent, '', `${action} retires only this row's required notice.`);
      if (action === 'Escape') {
        assert.equal(initialRow.querySelector('.settings-staff-editor').hidden, true);
        assert.equal(initialEmail.value, '', 'Escape retains the existing-row draft.');
        assert.equal(controller.isDirty(), true);
        openRow(initialRow); rowButton(initialRow, 'Cancel').click();
      }
      assert.equal(initialEmail.value, originalEmail);
      assert.equal(controller.isDirty(), false, 'Cancel restores the accepted row and clean controller.');
    }
    const noticePeer = document.querySelector('[data-staff-id="22"]'); openRow(noticePeer);
    edit(initialEmail, ''); rowButton(initialRow, 'Save changes').click();
    edit(noticePeer.querySelector('input[type="email"]'), ''); rowButton(noticePeer, 'Save changes').click();
    rowButton(initialRow, 'Cancel').click();
    assert.equal(staffStatus.textContent, 'Email address is required.', 'Resetting one row preserves another row\'s notice.');
    rowButton(noticePeer, 'Cancel').click(); assert.equal(staffStatus.textContent, '');
    inactiveFilter.click();
    const noticeInactive = document.querySelector('[data-staff-id="21"]'); openRow(noticeInactive);
    const inactiveEmail = noticeInactive.querySelector('input[type="email"]');
    edit(inactiveEmail, ''); rowButton(noticeInactive, 'Save changes').click();
    assert.equal(staffStatus.textContent, 'Email address is required.');
    inactiveFilter.click();
    assert.equal(noticeInactive.hidden, true); assert.equal(inactiveEmail.value, '');
    assert.equal(staffStatus.textContent, '', 'Filtering out a row retires its owned notice and retains its draft.');
    assert.equal(controller.isDirty(), true);
    inactiveFilter.click(); rowButton(noticeInactive, 'Cancel').click(); inactiveFilter.click();
    edit(initialEmail, ''); rowButton(initialRow, 'Save changes').click();
    failRoster = true; document.getElementById('staff-refresh').click();
    await waitFor(() => staffStatus.textContent === 'Roster unavailable');
    rowButton(initialRow, 'Cancel').click();
    assert.equal(staffStatus.textContent, 'Roster unavailable', 'Resetting the row preserves newer server feedback.');
    failRoster = false; document.getElementById('staff-refresh').click();
    await waitFor(() => !document.getElementById('staff-refresh').disabled && staffStatus.textContent === '');
    initialEmail.value = 'unsaved@example.org';
    assert.equal(controller.isDirty(), true, 'staff profile edits participate in departure and unload guards');
    const beforeCompetingAction = requests.length;
    const otherRow = document.querySelector('[data-staff-id="22"]');
    openRow(otherRow);
    edit(otherRow.querySelector('input[type="text"]'), 'Competing name');
    [...initialRow.querySelectorAll('button')].find(button => button.textContent === 'Deactivate').click();
    await flush();
    assert.equal(requests.length, beforeCompetingAction, 'lifecycle cannot consume another account draft');
    assert.equal(initialEmail.value, 'unsaved@example.org');
    [...otherRow.querySelectorAll('button')].find(button => button.textContent === 'Cancel').click();
    initialEmail.value = originalEmail;
    assert.equal(controller.isDirty(), false, 'returning a roster draft exactly to baseline makes it clean');

    holdRoster = true;
    document.getElementById('staff-refresh').click();
    await waitFor(() => releaseRoster);
    initialEmail.value = 'edited-during-read@example.org';
    releaseRoster(); holdRoster = false;
    await waitFor(() => document.getElementById('staff-access-status').textContent.includes('Edits changed'));
    assert.equal(initialEmail.isConnected, true, 'late roster read cannot replace edits made while it was pending');
    assert.equal(initialEmail.value, 'edited-during-read@example.org');
    controller.setStaff({ ...superStaff, version: 'new-preference-revision' });
    assert.equal(initialEmail.isConnected, true, 'same actor preference revision retains the staff draft owner');
    const beforeRejectedRefresh = requests.length;
    require('./helpers/confirmation.cjs').answerConfirmations(window, () => false);
    document.getElementById('staff-refresh').click();
    await flush();
    assert.equal(requests.length, beforeRejectedRefresh, 'declined roster discard performs no read or mutation');
    require('./helpers/confirmation.cjs').answerConfirmations(window, () => true);
    initialEmail.value = originalEmail;

    document.getElementById('staff-add-toggle').click();
    document.getElementById('staff-add-email').value = 'created@example.org';
    assert.equal(controller.isDirty(), true, 'new staff user is a separately owned draft');
    [...initialRow.querySelectorAll('button')].find(button => button.textContent === 'Deactivate').click();
    await flush();
    assert.equal(deleteBody, undefined, 'a lifecycle command cannot discard the new-user draft');
    document.getElementById('staff-add-role').value = 'staff';
    document.getElementById('staff-add-organization').value = '2';
    // JSDOM does not implement inert focus suppression. Match the browser here:
    // the early focus attempt while the mutation holds the form cannot work.
    const nativeFocus = window.HTMLElement.prototype.focus;
    window.HTMLElement.prototype.focus = function(options) {
      if (this.closest('#settings-form')?.inert) return;
      return nativeFocus.call(this, options);
    };
    document.getElementById('staff-add-email').focus();
    document.getElementById('staff-add-submit').click();
    await waitFor(() => postBodies.length === 1);
    assert.deepStrictEqual(postBodies[0], {
      email: 'created@example.org',
      role: 'staff',
      organizationId: 2
    });
    await waitFor(() => document.getElementById('staff-access-status').textContent.includes('2 auto-claim rules deactivated'));
    assert.equal(document.activeElement, document.getElementById('staff-add-toggle'),
      'Create returns focus to Add staff after its inert mutation owner releases the hidden form');
    window.HTMLElement.prototype.focus = nativeFocus;
    const largeCreatedRow = [...document.querySelectorAll('.settings-staff-row')]
      .find(row => row.dataset.staffId === '9223372036854775807');
    assert.ok(largeCreatedRow, 'A confirmed staff-create receipt preserves the exact Int64-max ID string.');
    assert.ok(largeCreatedRow.querySelector('.settings-cleanup-result'),
      'Cleanup evidence remains attached to the exact large returned staff identity.');

    let activeRow = [...document.querySelectorAll('.settings-staff-row')]
      .find(row => row.textContent.includes('Ada Admin'));
    edit(activeRow.querySelector('input[type="email"]'), 'ada.updated@example.org');
    const accessRole = activeRow.querySelector('select[aria-label^="Role"]');
    edit(accessRole, 'staff');
    [...activeRow.querySelectorAll('button')].find(button => button.textContent === 'Save changes').click();
    await waitFor(() => Boolean(patchBody));
    assert.strictEqual(patchBody.version, 'user-version-20');
    assert.strictEqual(patchBody.email, 'ada.updated@example.org');
    assert.strictEqual(patchBody.role, 'staff', 'profile and access drafts share one request');
    assert.equal(Object.hasOwn(patchBody, 'notificationEmail'), false, 'one email value owns both server properties');
    await waitFor(() => document.getElementById('staff-access-status').textContent.includes('Staff changes saved.'));
    assert.strictEqual(document.querySelector('[data-staff-id="20"]'), activeRow, 'accepted save retains the mounted editor');
    assert.strictEqual(activeRow.querySelector('input[type="email"]').value, 'ada.updated@example.org');

    activeRow = [...document.querySelectorAll('.settings-staff-row')].find(row => row.textContent.includes('Ada Admin'));
    [...activeRow.querySelectorAll('button')].find(button => button.textContent === 'Deactivate').click();
    await waitFor(() => Boolean(deleteBody));
    assert.strictEqual(deleteBody.version, 'updated-staff');
    await waitFor(() => document.getElementById('staff-access-status').textContent.includes('5 auto-claim rules deactivated'));

    assert.strictEqual([...activeRow.querySelectorAll('button')].some(button => button.textContent === 'Rebind identity'), false);

    const inactiveRow = [...document.querySelectorAll('.settings-staff-row')]
      .find(row => row.textContent.includes('Inactive Selector'));
    inactiveFilter.click();
    openRow(inactiveRow);
    [...inactiveRow.querySelectorAll('button')].find(button => button.textContent === 'Reactivate').click();
    await waitFor(() => Boolean(reactivateBody));
    assert.deepStrictEqual(reactivateBody, { version: 'user-version-21' });
    assert.equal(postBodies.length, 1, 'ordinary Reactivate uses accepted state, separate from single-email Create');
    await waitFor(() => document.getElementById('staff-access-status').textContent.includes('Staff user reactivated.'));

    const refreshedBeforeRevocation = refreshedCount;
    revokeStaffRefresh = true;
    const currentAdaRow = [...document.querySelectorAll('.settings-staff-row')]
      .find(row => row.textContent.includes('Ada Admin'));
    edit(currentAdaRow.querySelector('input[type="text"]'), 'Ada Admin Revised');
    [...currentAdaRow.querySelectorAll('button')]
      .find(button => button.textContent === 'Save changes').click();
    await waitFor(() => document.getElementById('staff-access-status').textContent
      .includes('Staff access could not be refreshed'));
    assert.match(committedMessages.at(-1), /Staff changes saved.*auto-claim rules deactivated/);
    assert.strictEqual(refreshedCount, refreshedBeforeRevocation,
      'failed follow-up refresh must retain the committed result');

    const beforeStaleRetry = requests.length;
    [...currentAdaRow.querySelectorAll('button')].find(button => button.textContent === 'Save changes').click();
    await flush();
    assert.equal(requests.length, beforeStaleRetry, 'confirmed staff commit with failed refresh cannot resubmit the stale roster');
    assert.equal(controller.hasUnconfirmedOutcome(), false, 'failed review does not make the confirmed commit uncertain');
    assert.equal(controller.inspectDeparture().blocked, false, 'a confirmed commit permits navigation');
    revokeStaffRefresh = false;
    document.getElementById('staff-refresh').click();
    await waitFor(() => !document.getElementById('staff-refresh').disabled &&
      document.getElementById('staff-access-status').textContent === '');
    loseProfileResponse = true; failRoster = true;
    controller.discardDraft();
    const uncertainRow = [...document.querySelectorAll('.settings-staff-row')].find(row => row.textContent.includes('Ada Admin'));
    openRow(uncertainRow);
    edit(uncertainRow.querySelector('input[type="email"]'), 'uncertain@example.org');
    [...uncertainRow.querySelectorAll('button')].find(button => button.textContent === 'Save changes').click();
    await waitFor(() => controller.hasUnconfirmedOutcome() && !controller.hasPendingMutation());
    const priorSettingsReads = requests.filter(url => url.includes('/settings?')).length;
    document.getElementById('settings-refresh').click();
    await waitFor(() => requests.filter(url => url.includes('/settings?')).length > priorSettingsReads);
    await flush(); await flush();
    assert.equal(controller.hasUnconfirmedOutcome(), true, 'main settings read cannot verify an uncertain staff command when roster review fails');
    failRoster = false;
    document.getElementById('staff-refresh').click();
    await waitFor(() => !controller.hasUnconfirmedOutcome());

    dom.window.close();

    // Exercise the mounted row owner with accepted mismatches, rejected atomic
    // saves, and external versions. These are UI contracts, not a SQL substitute.
    {
      let roster = [
        staffUser(51, { displayName: 'Zoe Adams', notificationEmail: 'separate@example.org' }),
        staffUser(52, { displayName: 'Amy Zimmer' }),
        staffUser('9223372036854775807', { displayName: 'Amy Zimmer', userPrincipalName: 'z@example.org' }),
        staffUser(54, { displayName: 'Aaron Aardvark', active: false })
      ];
      const writes = [], commits = [];
      let rejection = null;
      const mounted = await setupController(settingsModule, frontendRoot, superStaff, async (url, options = {}) => {
        if (url.endsWith('/session')) return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
        if (url.includes('/settings?')) {
          const data = settingsData('system'); data.stored.systemSettings.libraryOrgIds = [2, 3, 4];
          return response(200, data);
        }
        if (url.endsWith('/organizations')) return response(200, { code: 'ok', data: [
          { id: 1, name: 'System', abbreviation: null, parentOrganizationId: null, organizationCodeId: 1, isActive: true, version: 'o1' },
          { id: 2, name: 'Library Two', abbreviation: null, parentOrganizationId: 1, organizationCodeId: 2, isActive: true, version: 'o2' },
          { id: 3, name: 'Library Three', abbreviation: null, parentOrganizationId: 1, organizationCodeId: 2, isActive: true, version: 'o3' },
          { id: 4, name: 'Inactive library', abbreviation: null, parentOrganizationId: 1, organizationCodeId: 2, isActive: false, version: 'o4' }
        ] });
        if (url.includes('/patron-codes?')) return response(200, { code: 'ok', data: [] });
        if (url === '/api/asap/staff/users' && options.method === 'GET') return response(200, {
          canAssignSuperAdmin: true, users: roster.map(user => ({ ...user }))
        });
        if (options.method === 'PUT') {
          const body = JSON.parse(options.body); writes.push(body);
          if (rejection === 409) roster = roster.map(user => user.id === '51' ? {
            ...user, displayName: 'Externally Accepted', version: 'external-v2', organizationId: 4
          } : user);
          if (typeof rejection === 'number') return response(rejection, { message: `Rejected atomic save ${rejection}` });
          const saved = { ...roster.find(user => user.id === '51'), userPrincipalName: body.email,
            notificationEmail: rejection === 'partial' ? 'old@example.org' : body.email,
            displayName: body.displayName, role: body.role, organizationId: body.organizationId, version: `accepted-${writes.length}` };
          if (rejection !== 'partial') roster = roster.map(user => user.id === '51' ? saved : user);
          return response(200, { user: saved, cleanup: { rulesDeactivated: 0, openTitleClaimsCleared: 0, openAdditionalCopyClaimsCleared: 0 } });
        }
        throw new Error(`Unexpected mounted roster request: ${url}`);
      }, { onCommitted: message => commits.push(message) });
      document.getElementById('settings-nav-staff').click();
      await waitFor(() => document.querySelector('[data-staff-id="51"]'));
      const row = document.querySelector('[data-staff-id="51"]'), other = document.querySelector('[data-staff-id="52"]');
      const button = (owner, text) => [...owner.querySelectorAll('button')].find(item => item.textContent === text);
      const email = row.querySelector('input[type="email"]'), name = row.querySelector('input[type="text"]');
      const role = row.querySelector('select[aria-label^="Role"]'), library = row.querySelector('select[aria-label^="Library"]');
      const disclosure = row.querySelector('.settings-staff-summary'), editor = row.querySelector('fieldset');
      assert.deepEqual([...document.querySelectorAll('.settings-staff-row')].map(item => item.dataset.staffId),
        ['51', '52', '9223372036854775807', '54'], 'active first, final name component, email and opaque ID ties');
      assert.equal(mounted.controller.isDirty(), false, 'mismatched accepted emails do not dirty roster load');
      assert.equal(writes.length, 0);
      openRow(row); openRow(other);
      assert.equal(disclosure.getAttribute('aria-expanded'), 'true');
      assert.equal(email.value, 'staff51@example.org', 'authentication email owns the single editor field');
      assert.equal(button(row, 'Save changes').disabled, false, 'a pristine mismatch offers an intentional synchronization Save');
      assert.equal(row.querySelectorAll('input[type="email"]').length, 1);
      assert.equal(row.textContent.includes('Version '), false);
      assert.equal(row.textContent.includes('Used for sign-in and notifications.'), true);
      edit(email, 'invalid-email');
      assert.equal(email.checkValidity(), false);
      email.focus(); email.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      assert.equal(document.activeElement, disclosure);
      assert.equal(editor.hidden, true); assert.equal(editor.disabled, true);
      assert.equal(email.checkValidity(), true, 'collapsed invalid drafts cannot intercept global Settings native validation');
      assert.equal(row.querySelector('.settings-staff-unsaved').hidden, false);
      openRow(row);
      mounted.controller.activatePanel('patron');
      assert.equal(editor.hidden, false, 'tab navigation preserves this row expansion');
      assert.equal(editor.disabled, true, 'an editor hidden by its panel cannot intercept native validation');
      assert.equal(email.willValidate, false);
      const globalNote = document.getElementById('patron-login-note');
      edit(globalNote, 'Competing global Settings draft');
      document.getElementById('settings-save').click(); await flush();
      assert.match(document.getElementById('settings-message').textContent, /competing Settings draft/);
      assert.equal(writes.length, 0);
      assert.equal(email.value, 'invalid-email');
      edit(globalNote, '');
      mounted.controller.activatePanel('staff');
      assert.equal(editor.disabled, false);
      assert.equal(email.checkValidity(), false);
      button(row, 'Cancel').click();
      assert.equal(email.value, 'staff51@example.org');
      assert.equal(mounted.controller.isDirty(), false);
      assert.equal(writes.length, 0, 'Cancel never synchronizes the stored mismatch');
      button(row, 'Save changes').click();
      await waitFor(() => commits.length === 1 && !mounted.controller.hasPendingMutation()).catch(error => {
        throw new Error(`${error.message}: ${JSON.stringify({ writes, commits, status: document.getElementById('staff-access-status').textContent,
          message: document.getElementById('settings-message').textContent, email: email.value, role: role.value, library: library.value,
          saveDisabled: button(row, 'Save changes').disabled })}`);
      });
      assert.equal(roster[0].notificationEmail, 'staff51@example.org');
      assert.equal(button(row, 'Save changes').disabled, true);
      edit(name, 'Zoe Zulu'); edit(email, 'one@example.org'); edit(role, 'admin'); edit(library, '3');
      edit(other.querySelector('input[type="text"]'), 'Other draft');
      button(row, 'Deactivate').click(); await flush();
      assert.equal(writes.length, 1, 'lifecycle remains blocked by dirty roster drafts');
      disclosure.click(); openRow(row);
      assert.equal(name.value, 'Zoe Zulu');
      assert.equal(other.querySelector('input[type="text"]').value, 'Other draft');
      button(other, 'Cancel').click();
      button(row, 'Deactivate').click(); await flush();
      assert.equal(writes.length, 1, 'lifecycle does not save or discard profile/access drafts');
      rejection = 400;
      button(row, 'Save changes').click();
      await waitFor(() => writes.length === 2 && !mounted.controller.hasPendingMutation());
      assert.equal(name.value, 'Zoe Zulu'); assert.equal(role.value, 'admin'); assert.equal(library.value, '3');
      assert.equal(roster[0].displayName, 'Zoe Adams', 'failed combined save never presents partial profile success');
      assert.equal(commits.length, 1);
      rejection = 409;
      button(row, 'Save changes').click();
      await waitFor(() => !mounted.controller.hasPendingMutation() && row.textContent.includes('Saved values changed elsewhere'));
      assert.equal(name.value, 'Zoe Zulu'); assert.equal(email.value, 'one@example.org');
      assert.equal(button(row, 'Save changes').disabled, true, 'new external rowversion never silently authorizes a replay');
      button(row, 'Cancel').click();
      assert.equal(name.value, 'Externally Accepted'); assert.equal(library.value, '4', 'unchanged inactive Library remains an accepted option');
      assert.equal(mounted.controller.isDirty(), false);
      rejection = null;
      edit(name, 'Zoe Zulu');
      button(row, 'Save changes').focus(); button(row, 'Save changes').click();
      await waitFor(() => commits.length === 2 && !mounted.controller.hasPendingMutation());
      assert.equal(writes.at(-1).version, 'external-v2', 'Cancel intentionally accepts the reviewed server version');
      assert.equal(document.querySelector('[data-staff-id="51"]'), row, 'reorder retains editor and draft identity');
      assert.deepEqual([...document.querySelectorAll('.settings-staff-row')].map(item => item.dataset.staffId),
        ['52', '9223372036854775807', '51', '54']);
      assert.equal(document.activeElement, disclosure, 'disabled completed Save returns focus to its moved summary');
      rejection = 'partial'; edit(name, 'Unconfirmed draft'); button(row, 'Save changes').click();
      await waitFor(() => mounted.controller.hasUnconfirmedOutcome() && !mounted.controller.hasPendingMutation());
      assert.equal(commits.length, 2, 'a partial or mismatched unified receipt cannot announce complete success');
      assert.equal(name.value, 'Unconfirmed draft');
      document.getElementById('staff-refresh').click();
      await waitFor(() => !mounted.controller.hasUnconfirmedOutcome());
      assert.equal(name.value, 'Zoe Zulu', 'explicit authoritative review resolves the uncertain draft');
      mounted.controller.dispose(); mounted.dom.window.close();
    }

    {
      const actor = { id: '61', tenantId: 'roster-filter', authenticationEmail: 'ACTOR@EXAMPLE.ORG', role: 'super_admin', organizationId: 1 };
      let roster = [
        staffUser(61, { userPrincipalName: 'actor@example.org', notificationEmail: 'actor@example.org', displayName: 'Current Actor', role: 'super_admin', organizationId: 1 }),
        staffUser(62, { displayName: 'Same Name', organizationId: 3 }),
        staffUser(63, { displayName: 'Same Name', active: false, organizationId: 2 })
      ];
      const writes = [], reads = [];
      let holdRead = false, failRead = false, releaseRead, announcement = '', announcementGeneration = 0;
      const filtered = await setupController(settingsModule, frontendRoot, actor, async (url, options = {}) => {
        if (url.endsWith('/session')) return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
        if (url.includes('/settings?')) {
          const data = settingsData(new URL(url, 'http://localhost').searchParams.get('orgId'));
          data.stored.systemSettings.libraryOrgIds = [2, 3, 4];
          data.stored.systemSettings.enabledLibraryOrgIds = [2, 3, 4];
          return response(200, data);
        }
        if (url.endsWith('/organizations')) return response(200, { data: [
          { id: 1, name: 'System', parentOrganizationId: null, organizationCodeId: 1, isActive: true, version: 'o1' },
          { id: 2, name: 'Library Two', parentOrganizationId: 1, organizationCodeId: 2, isActive: true, version: 'o2' },
          { id: 3, name: 'Library Three', parentOrganizationId: 1, organizationCodeId: 2, isActive: true, version: 'o3' },
          { id: 4, name: 'Empty Library', parentOrganizationId: 1, organizationCodeId: 2, isActive: true, version: 'o4' }
        ] });
        if (url.includes('/patron-codes?')) return response(200, { data: [] });
        if (url.startsWith('/api/asap/staff/users') && options.method === 'GET') {
          reads.push(url);
          if (failRead) return response(503, { message: 'Roster refresh unavailable' });
          const scope = new URL(url, 'http://localhost').searchParams.get('orgId');
          const result = () => response(200, { canAssignSuperAdmin: true,
            users: roster.filter(user => !scope || String(user.organizationId) === scope).map(user => ({ ...user })) });
          if (holdRead) return new Promise(resolve => { releaseRead = () => resolve(result()); });
          return result();
        }
        if (['PUT', 'DELETE', 'POST'].includes(options.method)) {
          const id = url.match(/\/users\/(\d+)/)?.[1], body = JSON.parse(options.body);
          const target = roster.find(user => user.id === id);
          assert.ok(target, 'Only the requested existing account can mutate.');
          assert.equal(body.version, target.version, 'Every independent save retains its own accepted rowversion.');
          writes.push({ id, method: options.method, body });
          const saved = options.method === 'PUT' ? { ...target, userPrincipalName: body.email, notificationEmail: body.email,
            displayName: body.displayName, role: body.role, organizationId: body.organizationId }
            : { ...target, active: options.method !== 'DELETE' };
          saved.version = `saved-${writes.length}`;
          roster = roster.map(user => user.id === id ? saved : user);
          return response(200, { user: saved, cleanup: { rulesDeactivated: 0, openTitleClaimsCleared: 0, openAdditionalCopyClaimsCleared: 0 } });
        }
        throw new Error(`Unexpected filtered roster request: ${url}`);
      }, { announce(message) {
        announcement = message;
        const generation = ++announcementGeneration;
        return () => { if (announcementGeneration === generation) announcement = ''; };
      } });
      document.getElementById('workspace').hidden = false;
      document.getElementById('settings-view').hidden = false;
      document.getElementById('settings-nav-staff').click();
      const counts = document.getElementById('staff-roster-counts'), filter = document.getElementById('staff-show-inactive');
      await waitFor(() => counts.textContent === '2 active accounts · 1 inactive hidden');
      const button = (row, text) => [...row.querySelectorAll('button')].find(item => item.textContent === text);
      const row = id => document.querySelector(`[data-staff-id="${id}"]`);
      const inactive = row('63'), other = row('62'), self = row('61');
      assert.equal(filter.checked, false); assert.equal(inactive.hidden, true);
      assert.equal(filter.getAttribute('aria-describedby'), counts.id);
      assert.equal(counts.getAttribute('aria-live'), 'polite');
      assert.equal(filtered.controller.isDirty(), false);
      const beforeToggleReads = reads.length;
      filter.click(); openRow(inactive); openRow(other); openRow(self);
      assert.equal(counts.textContent, '2 active accounts · 1 inactive account shown');
      assert.equal(reads.length, beforeToggleReads, 'The display filter never fetches another roster.');
      assert.match(inactive.querySelector('.status-badge').textContent, /^Inactive$/);
      assert.ok(button(inactive, 'Reactivate'));
      const email = inactive.querySelector('input[type="email"]'), name = inactive.querySelector('input[type="text"]');
      edit(email, 'invalid-email'); edit(name, 'Hidden draft'); edit(inactive.querySelector('select[aria-label^="Role"]'), 'admin');
      name.focus(); name.setSelectionRange(1, 3);
      filter.checked = false; filter.dispatchEvent(new window.Event('change', { bubbles: true }));
      assert.equal(document.activeElement, filter, 'Focus leaves the editor before its inactive row is hidden.');
      assert.equal(inactive.hidden, true); assert.equal(inactive.querySelector('fieldset').hidden, false);
      assert.equal(inactive.querySelector('fieldset').disabled, true); assert.equal(email.willValidate, false);
      assert.equal(email.checkValidity(), true, 'A filtered invalid editor cannot intercept unrelated native Save.');
      assert.equal(filtered.controller.isDirty(), true);
      filtered.controller.activatePanel('patron');
      let submits = 0;
      document.getElementById('settings-form').addEventListener('submit', () => { submits += 1; });
      const note = document.getElementById('patron-login-note'), originalNote = note.value;
      edit(note, 'An unrelated Settings draft');
      document.getElementById('settings-save').click(); await flush();
      assert.equal(submits, 1, `Native form submission reaches the owning draft guard despite the filtered invalid input: ${JSON.stringify(
        [...document.getElementById('settings-form').elements].filter(control => control.willValidate && !control.checkValidity())
          .map(control => [control.id, control.getAttribute('aria-label'), control.value, control.validationMessage]))}`);
      assert.match(document.getElementById('settings-message').textContent, /competing Settings draft/);
      assert.equal(writes.length, 0);
      edit(note, originalNote); filtered.controller.activatePanel('staff');
      filter.click();
      assert.equal(row('63'), inactive); assert.equal(inactive.querySelector('input[type="email"]'), email);
      assert.equal(email.value, 'invalid-email'); assert.equal(name.value, 'Hidden draft');
      assert.deepEqual([name.selectionStart, name.selectionEnd], [1, 3]);
      assert.equal(inactive.querySelector('fieldset').disabled, false); assert.equal(email.checkValidity(), false);
      assert.equal(inactive.querySelector('select[aria-label^="Library"]').disabled, false);
      edit(email, 'hidden.saved@example.org');
      edit(other.querySelector('input[type="text"]'), 'Other independent draft');
      filter.click();
      button(other, 'Save changes').focus(); button(other, 'Save changes').click();
      await waitFor(() => writes.length === 1 && !filtered.controller.hasPendingMutation());
      assert.equal(writes[0].id, '62'); assert.equal(writes[0].body.version, 'v62');
      assert.equal(row('63'), inactive); assert.equal(email.value, 'hidden.saved@example.org');
      assert.equal(name.value, 'Hidden draft'); assert.equal(inactive.hidden, true);
      assert.equal(inactive.querySelector('.settings-staff-unsaved').hidden, false);
      assert.equal(document.activeElement, other.querySelector('.settings-staff-summary'));
      filter.click(); button(inactive, 'Save changes').click();
      await waitFor(() => writes.length === 2 && !filtered.controller.hasPendingMutation());
      assert.equal(writes[1].id, '63'); assert.equal(writes[1].body.version, 'v63');
      assert.equal(roster.find(user => user.id === '63').active, false);
      assert.equal(filtered.controller.isDirty(), false, 'Both independent roster drafts can save sequentially without Cancel.');
      assert.equal(document.getElementById('settings-message').textContent, ''); assert.equal(announcement, '');

      edit(other.querySelector('input[type="text"]'), 'Sibling survives actor metadata');
      edit(self.querySelector('input[type="text"]'), 'Actor metadata');
      button(self, 'Save changes').click();
      await waitFor(() => writes.length === 3 && !filtered.controller.hasPendingMutation());
      assert.equal(other.querySelector('input[type="text"]').value, 'Sibling survives actor metadata');
      for (const change of ['email', 'role']) {
        const input = self.querySelector(change === 'email' ? 'input[type="email"]' : 'select[aria-label^="Role"]');
        edit(input, change === 'email' ? 'replacement.actor@example.org' : 'admin');
        button(self, 'Save changes').click(); await flush();
        assert.equal(writes.length, 3, `${change}: an actor-context-changing save cannot retire another roster draft`);
        assert.match(document.getElementById('settings-message').textContent, /competing Settings draft/);
        button(self, 'Cancel').click();
      }
      edit(self.querySelector('input[type="email"]'), 'replacement.actor@example.org');
      button(self, 'Save changes').click(); await flush();
      document.getElementById('copy-patron-public-url').click(); await flush();
      const newerNotice = document.getElementById('settings-message').textContent;
      assert.match(newerNotice, /copied|could not be copied/);
      button(other, 'Cancel').click();
      assert.equal(document.getElementById('settings-message').textContent, newerNotice,
        'Recovering a blocked draft clears only its owned notice, preserving a newer unrelated notice.');
      assert.equal(announcement, newerNotice);
      button(self, 'Cancel').click();
      document.getElementById('staff-add-toggle').click(); edit(document.getElementById('staff-add-email'), 'create.draft@example.org');
      edit(other.querySelector('input[type="text"]'), 'Blocked by creation');
      button(other, 'Save changes').click(); await flush();
      assert.equal(writes.length, 3, 'A row PUT never preserves the separately owned creation draft.');
      document.getElementById('staff-add-cancel').click();
      assert.equal(document.getElementById('settings-message').textContent, ''); assert.equal(announcement, '');
      button(other, 'Deactivate').click(); await flush();
      assert.equal(writes.length, 3, 'Lifecycle still requires explicit resolution of all roster drafts.');
      button(other, 'Cancel').click();
      assert.equal(document.getElementById('settings-message').textContent, '');

      edit(name, 'Recoverable hidden draft');
      edit(other.querySelector('input[type="text"]'), 'Saved before roster read failed');
      filter.click(); failRead = true;
      button(other, 'Save changes').click();
      await waitFor(() => writes.length === 4 && !filtered.controller.hasPendingMutation());
      assert.equal(document.getElementById('settings-form').inert, true, 'A known commit still requires an authoritative roster review.');
      assert.equal(filtered.controller.isDirty(), true, 'A preserved sibling remains dirty during committed review.');
      assert.equal(filtered.controller.inspectDeparture().dirty, true, 'Departure cannot silently retire the preserved sibling.');
      assert.equal(name.value, 'Recoverable hidden draft'); assert.equal(inactive.hidden, true);
      let reviewConfirmations = 0;
      require('./helpers/confirmation.cjs').answerConfirmations(window, () => { reviewConfirmations += 1; return true; });
      failRead = false; document.getElementById('settings-refresh').click();
      await waitFor(() => !document.getElementById('settings-form').inert && !document.getElementById('staff-refresh').disabled);
      assert.equal(reviewConfirmations, 0, 'Reviewing the known commit preserves the sibling without demanding a discard.');
      assert.equal(name.value, 'Recoverable hidden draft'); assert.equal(inactive.hidden, true);
      assert.equal(filtered.controller.isDirty(), true);
      filter.click(); button(inactive, 'Save changes').click();
      await waitFor(() => writes.length === 5 && !filtered.controller.hasPendingMutation());
      assert.equal(writes.at(-1).body.version, 'saved-2', 'Preserved review does not silently advance the sibling version.');
      assert.equal(filtered.controller.isDirty(), false);

      holdRead = true; document.getElementById('staff-refresh').click();
      await waitFor(() => releaseRead);
      filter.click(); releaseRead(); holdRead = false;
      await waitFor(() => !document.getElementById('staff-refresh').disabled);
      assert.equal(document.getElementById('staff-access-status').textContent, '', 'Toggling visibility during a load does not stale its accepted roster.');
      assert.equal(filter.checked, false); assert.equal(counts.textContent, '2 active accounts · 1 inactive hidden');
      assert.equal(row('63'), inactive); assert.equal(inactive.hidden, true);
      button(other, 'Deactivate').focus(); button(other, 'Deactivate').click();
      await waitFor(() => writes.length === 6 && !filtered.controller.hasPendingMutation());
      assert.equal(counts.textContent, '1 active account · 2 inactive hidden'); assert.equal(other.hidden, true);
      assert.equal(document.activeElement, filter, 'Successful deactivation moves focus to the filter before hiding its row.');
      filter.click(); button(other, 'Reactivate').click();
      await waitFor(() => writes.length === 7 && !filtered.controller.hasPendingMutation());
      assert.equal(counts.textContent, '2 active accounts · 1 inactive account shown');
      assert.equal(writes.at(-1).body.version, 'saved-6');
      failRead = true; button(other, 'Deactivate').click();
      await waitFor(() => writes.length === 8 && !filtered.controller.hasPendingMutation());
      assert.equal(counts.textContent, '1 active account · 2 inactive accounts shown',
        'A failed follow-up GET cannot leave counts contradicting the confirmed deactivation receipt.');
      assert.equal(other.querySelector('.status-badge').textContent, 'Inactive');
      assert.match(document.getElementById('staff-access-status').textContent, /deactivated.*could not be refreshed/);
      failRead = false; document.getElementById('staff-refresh').click();
      await waitFor(() => !document.getElementById('settings-form').inert && !document.getElementById('staff-refresh').disabled);
      button(other, 'Reactivate').click();
      await waitFor(() => writes.length === 9 && !filtered.controller.hasPendingMutation());
      assert.equal(counts.textContent, '2 active accounts · 1 inactive account shown');
      filtered.controller.setStaff({ ...actor, version: 'preference-revision' });
      assert.equal(filter.checked, true, 'A same-session preference revision preserves the roster display filter.');

      let prepared = await filtered.controller.prepareScope('2'); prepared.accept(); await filtered.controller.activate('staff');
      await waitFor(() => counts.textContent === '0 active accounts · 1 inactive hidden');
      assert.equal(filter.checked, false, 'An accepted scope starts with active accounts only.');
      assert.equal(inactive.isConnected, false, 'The retired scope releases its mounted editor identity.');
      assert.match(document.querySelector('#settings-staff-users-list .settings-empty').textContent, /No active staff accounts for Library Two.*Show inactive accounts/);
      filter.click();
      assert.equal(counts.textContent, '0 active accounts · 1 inactive account shown');
      assert.equal(document.querySelector('#settings-staff-users-list .settings-empty'), null);
      assert.deepEqual([...document.querySelectorAll('.settings-staff-row')].map(item => item.dataset.staffId), ['63']);
      assert.equal(reads.at(-1), '/api/asap/staff/users?orgId=2');
      const leaving = row('63'); openRow(leaving);
      edit(leaving.querySelector('select[aria-label^="Library"]'), '3');
      button(leaving, 'Save changes').focus(); button(leaving, 'Save changes').click();
      await waitFor(() => writes.length === 10 && !filtered.controller.hasPendingMutation());
      assert.equal(row('63'), null); assert.equal(counts.textContent, '0 active accounts · 0 inactive accounts shown');
      assert.equal(document.activeElement, document.getElementById('staff-refresh'),
        'A saved move outside the selected scope returns focus to the surviving roster toolbar.');
      prepared = await filtered.controller.prepareScope('4'); prepared.accept(); await filtered.controller.activate('staff');
      await waitFor(() => counts.textContent === '0 active accounts · 0 inactive hidden');
      assert.equal(document.querySelector('#settings-staff-users-list .settings-empty').textContent, 'No staff users found for Empty Library.');
      filter.click();
      assert.equal(document.querySelector('#settings-staff-users-list .settings-empty').textContent, 'No staff users found for Empty Library.');
      assert.equal(filtered.controller.isDirty(), false); assert.equal(document.getElementById('settings-save').disabled, true);
      filtered.controller.signedOut();
      assert.equal(filter.checked, false); assert.equal(counts.textContent, '');
      assert.equal(document.querySelectorAll('.settings-staff-row').length, 0);
      filtered.controller.dispose(); filtered.dom.window.close();
    }

    for (const missingEvidence of ['id', 'version', 'cleanup', 'id-zero', 'id-leading-zero', 'id-overflow', 'id-number']) {
      const lifecycleRequests = [];
      const lifecycleCommits = [];
      let createBody;
      let rosterReads = 0;
      const lifecycle = await setupController(
        settingsModule,
        frontendRoot,
        superStaff,
        async (url, options = {}) => {
          const requestUrl = String(url);
          lifecycleRequests.push({ requestUrl, options });
          if (requestUrl.includes('/api/asap/staff/settings?orgId=')) return response(200, settingsData('system'));
          if (requestUrl.endsWith('/api/asap/staff/organizations')) return response(200, { code: 'ok', data: [
            { id: 1, name: 'System', abbreviation: null, organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org-v1' },
            { id: 2, name: 'Library Two', abbreviation: 'TWO', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-v2' },
            { id: 3, name: 'Library Three', abbreviation: 'THREE', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-v3' }
          ] });
          if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
          if (requestUrl === '/api/asap/staff/users' && options.method === 'GET') {
            rosterReads += 1;
            return response(200, { canAssignSuperAdmin: true, users: [] });
          }
          if (requestUrl === '/api/asap/staff/audit?limit=50') return response(200, { code: 'ok', data: [] });
          if (requestUrl === '/api/asap/staff/users' && options.method === 'POST') {
            createBody = JSON.parse(options.body);
            const user = staffUser(40, { userPrincipalName: createBody.email, version: 'created-v1' });
            if (missingEvidence === 'id') delete user.id;
            if (missingEvidence === 'id-zero') user.id = '0';
            if (missingEvidence === 'id-leading-zero') user.id = '040';
            if (missingEvidence === 'id-overflow') user.id = '9223372036854775808';
            if (missingEvidence === 'id-number') user.id = 40;
            if (missingEvidence === 'version') delete user.version;
            const envelope = { user,
              cleanup: { rulesDeactivated: 0, openTitleClaimsCleared: 0, openAdditionalCopyClaimsCleared: 0 } };
            if (missingEvidence === 'cleanup') delete envelope.cleanup;
            return response(201, envelope);
          }
          throw new Error(`Unexpected lifecycle request: ${requestUrl}`);
        },
        { onCommitted: message => lifecycleCommits.push(message) }
      );
      document.getElementById('settings-nav-staff').click();
      await waitFor(() => !document.getElementById('staff-refresh').disabled &&
        document.getElementById('staff-access-status').textContent === '');
      const email = `uncertain-${missingEvidence}@example.org`;
      document.getElementById('staff-add-toggle').click();
      document.getElementById('staff-add-email').value = email;
      const createRole = document.getElementById('staff-add-role');
      createRole.value = 'staff';
      createRole.dispatchEvent(new lifecycle.dom.window.Event('change', { bubbles: true }));
      const createOrganization = document.getElementById('staff-add-organization');
      assert.ok([...createOrganization.options].some(option => option.value === '2'),
        `${missingEvidence}: changing to a library staff role populates the Library Two choice`);
      createOrganization.value = '2';
      assert.equal(createOrganization.value, '2', `${missingEvidence}: Library Two remains selected for the real lifecycle request`);
      document.getElementById('staff-add-submit').click();
      await waitFor(() => createBody !== undefined, `${missingEvidence}: staff create reaches the real lifecycle POST`);
      await flush();
      assert.deepStrictEqual(createBody, { email, role: 'staff', organizationId: 2 });
      assert.equal(lifecycleCommits.length, 0, `${missingEvidence}: incomplete lifecycle evidence is not announced as committed`);
      assert.equal(lifecycle.controller.hasUnconfirmedOutcome(), true,
        `${missingEvidence}: missing lifecycle evidence requires authoritative roster review`);
      assert.equal(document.getElementById('staff-add-email').value, email,
        `${missingEvidence}: uncertain creation preserves its draft`);
      assert.equal(lifecycle.controller.inspectDeparture().blocked, true,
        `${missingEvidence}: the pending staff result guards departure`);
      assert.equal(rosterReads, 1, `${missingEvidence}: do not treat the incomplete response as a committed roster refresh`);
      lifecycle.controller.dispose();
      lifecycle.dom.window.close();
    }

    const malformedRosterReceipts = [
      ['PUT', 'missing-active', user => { delete user.active; }],
      ['PUT', 'string-active', user => { user.active = 'false'; }],
      ['PUT', 'opposite-active', user => { user.active = true; }],
      ['POST', 'missing-role', user => { delete user.role; }],
      ['POST', 'unknown-role', user => { user.role = 'unknown'; }],
      ['POST', 'missing-organization', user => { delete user.organizationId; }],
      ['POST', 'string-organization', user => { user.organizationId = '2'; }],
      ['POST', 'fractional-organization', user => { user.organizationId = 2.5; }],
      ['POST', 'out-of-range-organization', user => { user.organizationId = 2147483648; }],
      ['POST', 'staff-system-organization', user => { user.organizationId = 1; }],
      ['DELETE', 'super-admin-library-organization', user => { user.role = 'super_admin'; }],
      ['DELETE', 'missing-display-name', user => { delete user.displayName; }],
      ['POST', 'invalid-notification-email', user => { user.notificationEmail = {}; }]
    ];
    for (const [method, label, corrupt] of malformedRosterReceipts) {
      const accepted = [staffUser(71, { active: false }), staffUser(72)];
      const commits = [], projections = [];
      let mutation, rosterReads = 0, failedReviews = 0;
      const receipt = await setupController(settingsModule, frontendRoot, superStaff, async (url, options = {}) => {
        if (url.endsWith('/session')) return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
        if (url.includes('/settings?')) return response(200, settingsData('system'));
        if (url.endsWith('/organizations')) return response(200, { data: [
          { id: 1, name: 'System', organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'o1' },
          { id: 2, name: 'Library Two', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'o2' },
          { id: 3, name: 'Library Three', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'o3' }
        ] });
        if (url.includes('/patron-codes?')) return response(200, { data: [] });
        if (url === '/api/asap/staff/users' && options.method === 'GET') {
          rosterReads += 1;
          if (mutation) {
            failedReviews += 1;
            return response(503, { message: 'Authoritative roster review unavailable' });
          }
          return response(200, { canAssignSuperAdmin: true, users: accepted.map(user => ({ ...user })) });
        }
        const targetId = method === 'PUT' ? '71' : '72';
        const path = method === 'POST' ? '/api/asap/staff/users' : `/api/asap/staff/users/${targetId}`;
        if (url === path && options.method === method) {
          mutation = JSON.parse(options.body);
          const user = method === 'DELETE' ? { ...accepted[1], active: false, version: 'receipt-v2' }
            : { ...staffUser(method === 'POST' ? 73 : targetId), active: method === 'POST', version: 'receipt-v2',
              userPrincipalName: mutation.email, notificationEmail: mutation.email, displayName: mutation.displayName ?? null,
              role: mutation.role, organizationId: mutation.organizationId };
          corrupt(user);
          return response(method === 'POST' ? 201 : 200, { user,
            cleanup: { rulesDeactivated: 0, openTitleClaimsCleared: 0, openAdditionalCopyClaimsCleared: 0 } });
        }
        throw new Error(`Unexpected ${label} receipt request: ${url}`);
      }, { onCommitted: message => commits.push(message), onStaffAccessCommitted: owner => projections.push(owner) });
      receipt.controller.activatePanel('staff');
      await waitFor(() => rosterReads === 1 && !document.getElementById('staff-refresh').disabled);
      document.getElementById('staff-show-inactive').click();
      const inactive = document.querySelector('[data-staff-id="71"]');
      const other = document.querySelector('[data-staff-id="72"]');
      const counts = document.getElementById('staff-roster-counts');
      assert.equal(counts.textContent, '1 active account · 1 inactive account shown');
      if (method === 'PUT') {
        openRow(inactive); openRow(other);
        edit(inactive.querySelector('input[type="email"]'), 'raw-receipt-draft@example.org');
        edit(inactive.querySelector('input[type="text"]'), 'Raw inactive draft');
        edit(other.querySelector('input[type="text"]'), 'Preserved sibling draft');
        [...inactive.querySelectorAll('button')].find(button => button.textContent === 'Save changes').click();
      } else if (method === 'POST') {
        document.getElementById('staff-add-toggle').click();
        edit(document.getElementById('staff-add-email'), 'raw-create-draft@example.org');
        document.getElementById('staff-add-submit').click();
      } else {
        openRow(other);
        [...other.querySelectorAll('button')].find(button => button.textContent === 'Deactivate').click();
      }
      await waitFor(() => mutation && !receipt.controller.hasPendingMutation());
      const assertUnaccepted = () => {
        assert.equal(commits.length, 0, `${label}: malformed evidence cannot announce a committed operation`);
        assert.equal(projections.length, 0, `${label}: malformed evidence cannot invalidate accepted projections`);
        assert.equal(receipt.controller.hasUnconfirmedOutcome(), true, `${label}: authoritative review remains required`);
        assert.equal(receipt.controller.inspectDeparture().blocked, true);
        assert.equal(counts.textContent, '1 active account · 1 inactive account shown', `${label}: accepted counts remain unchanged`);
        assert.equal(inactive.querySelector('.status-badge').textContent, 'Inactive');
        assert.equal(other.querySelector('.status-badge').textContent, 'Active');
        assert.equal(document.querySelector('[data-staff-id="73"]'), null);
        if (method === 'PUT') {
          assert.equal(inactive.querySelector('input[type="email"]').value, 'raw-receipt-draft@example.org');
          assert.equal(inactive.querySelector('input[type="text"]').value, 'Raw inactive draft');
          assert.equal(other.querySelector('input[type="text"]').value, 'Preserved sibling draft');
        } else if (method === 'POST') {
          assert.equal(document.getElementById('staff-add-email').value, 'raw-create-draft@example.org');
        }
      };
      assert.equal(rosterReads, 1, `${label}: malformed evidence does not authorize a success follow-up GET`);
      assertUnaccepted();
      await receipt.controller.activate('staff');
      assert.ok(failedReviews > 0, `${label}: exercise a real failed authoritative review after the malformed receipt`);
      assertUnaccepted();
      receipt.controller.dispose(); receipt.dom.window.close();
    }

    for (const retired of ['owner', 'preserved']) {
      for (const newer of [false, true]) {
        let roster = [staffUser(81), staffUser(82)];
        let rosterReads = 0, announcement = '', announcementGeneration = 0;
        const feedback = await setupController(settingsModule, frontendRoot, superStaff, async (url, options = {}) => {
          if (url.includes('/settings?')) return response(200, settingsData('system'));
          if (url.endsWith('/organizations')) return response(200, { data: [
            { id: 1, name: 'System', organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'o1' },
            { id: 2, name: 'Library Two', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'o2' },
            { id: 3, name: 'Library Three', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'o3' }
          ] });
          if (url.includes('/patron-codes?')) return response(200, { data: [] });
          if (url === '/api/asap/staff/users' && options.method === 'GET') {
            rosterReads += 1;
            return response(200, { canAssignSuperAdmin: true, users: roster.map(user => ({ ...user })) });
          }
          throw new Error(`Unexpected retired feedback request: ${url}`);
        }, { announce(message) {
          announcement = message;
          const generation = ++announcementGeneration;
          return () => { if (generation === announcementGeneration) announcement = ''; };
        } });
        feedback.controller.activatePanel('staff');
        await waitFor(() => rosterReads === 1 && !document.getElementById('staff-refresh').disabled);
        const owner = document.querySelector('[data-staff-id="81"]'), sibling = document.querySelector('[data-staff-id="82"]');
        openRow(owner); openRow(sibling);
        edit(owner.querySelector('input[type="text"]'), 'Owner draft');
        edit(sibling.querySelector('input[type="text"]'), 'Preserved sibling draft');
        document.getElementById('staff-add-toggle').click();
        edit(document.getElementById('staff-add-email'), 'Competing-create@example.org');
        [...owner.querySelectorAll('button')].find(button => button.textContent === 'Save changes').click();
        await flush();
        assert.match(document.getElementById('settings-message').textContent, /competing Settings draft/);
        let newerNotice = '';
        if (newer) {
          document.getElementById('copy-patron-public-url').click(); await flush();
          newerNotice = document.getElementById('settings-message').textContent;
          assert.match(newerNotice, /copied|could not be copied/);
        }
        // An accepted roster can lose an account after an external scope or
        // eligibility change. Confirmed discard must retire its feedback owner.
        const retiredId = retired === 'owner' ? '81' : '82';
        roster = roster.filter(user => user.id !== retiredId);
        document.getElementById('staff-refresh').click();
        await waitFor(() => rosterReads === 2 && !document.getElementById('staff-refresh').disabled);
        assert.equal(document.querySelector(`[data-staff-id="${retiredId}"]`), null);
        assert.equal(document.getElementById('settings-message').textContent, newerNotice,
          `${retired}: retiring an action's draft handles clears only that action's owned warning`);
        assert.equal(announcement, newerNotice, `${retired}: the same ownership rule applies to shell feedback`);
        assert.equal(feedback.controller.isDirty(), false);
        feedback.controller.dispose(); feedback.dom.window.close();
      }
    }

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
        if (requestUrl.endsWith('/api/asap/staff/organizations')) return response(200, { code: 'ok', data: [
          { id: 2, name: 'Library Two', abbreviation: 'TWO', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-v2' }
        ] });
        if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
        if (requestUrl === '/api/asap/staff/users?orgId=2') return response(200, { canAssignSuperAdmin: false,
          users: [staffUser(22), staffUser(23, { active: false })] });
        if (requestUrl === '/api/asap/staff/audit?limit=50&organizationId=2') return response(200, { code: 'ok', data: [] });
        throw new Error(`Unexpected admin request: ${requestUrl}`);
      }
    );
    document.getElementById('settings-nav-staff').click();
    await waitFor(() => adminRequests.includes('/api/asap/staff/users?orgId=2'));
    assert.equal(adminRequests.some(url => url.includes('/api/asap/staff/audit')), false);
    assert.strictEqual(document.getElementById('staff-add-role').textContent.includes('Super admin'), false);
    assert.strictEqual(document.getElementById('staff-add-organization').disabled, true);
    assert.equal(document.getElementById('staff-roster-counts').textContent, '1 active account · 1 inactive hidden');
    document.getElementById('staff-show-inactive').click();
    const scopedInactive = document.querySelector('[data-staff-id="23"]');
    openRow(scopedInactive);
    assert.equal(scopedInactive.querySelector('select[aria-label^="Library"]').disabled, true);
    document.getElementById('staff-show-inactive').click();
    assert.equal(scopedInactive.querySelector('fieldset').disabled, true);
    document.getElementById('staff-show-inactive').click();
    assert.equal(scopedInactive.querySelector('fieldset').disabled, false);
    assert.equal(scopedInactive.querySelector('select[aria-label^="Library"]').disabled, true,
      'filter and fieldset state never remove a library administrator restriction');
    assert.deepEqual([...document.querySelectorAll('.settings-staff-row')].map(row => row.dataset.staffId), ['22', '23']);
    assert.equal(adminRequests.some(url => url === '/api/asap/staff/users'), false,
      'revealing inactive accounts never requests a broader roster');
    assert.strictEqual([...document.querySelectorAll('.settings-staff-row button')].some(button => button.textContent === 'Rebind identity'), false);
    admin.dom.window.close();

    {
      let currentOwner = superStaff;
      const pendingLibraryRosters = [];
      const capability = await setupController(settingsModule, frontendRoot, superStaff, async (url) => {
        if (url.includes('/settings?')) return response(200, settingsData(currentOwner.role === 'super_admin' ? 'system' : '2'));
        if (url.endsWith('/organizations')) return response(200, { code: 'ok', data: currentOwner.role === 'super_admin' ? [
          { id: 1, name: 'System', abbreviation: null, organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'o1' },
          { id: 2, name: 'Library Two', abbreviation: null, organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'o2' },
          { id: 3, name: 'Library Three', abbreviation: null, organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'o3' }
        ] : [
          { id: 2, name: 'Library Two', abbreviation: null, organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'o2' }
        ] });
        if (url.includes('/patron-codes?')) return response(200, { code: 'ok', data: [] });
        if (url === '/api/asap/staff/users') return response(200, { canAssignSuperAdmin: true, users: [staffUser(71)] });
        if (url === '/api/asap/staff/users?orgId=2') return new Promise(resolve => pendingLibraryRosters.push(resolve));
        throw new Error(`Unexpected capability owner request: ${url}`);
      });
      document.getElementById('settings-nav-staff').click();
      await waitFor(() => document.querySelector('[data-staff-id="71"]'));
      assert.equal(document.getElementById('staff-add-role').textContent.includes('Super admin'), true);
      document.getElementById('staff-show-inactive').click();
      currentOwner = adminStaff;
      capability.controller.setStaff(adminStaff);
      assert.equal(document.getElementById('staff-show-inactive').checked, false, 'a replacement actor starts with active accounts only');
      await capability.controller.activate('staff');
      await waitFor(() => pendingLibraryRosters.length > 0);
      document.getElementById('staff-add-toggle').click();
      assert.equal(document.getElementById('staff-add-form').hidden, false);
      assert.equal(document.getElementById('staff-add-role').textContent.includes('Super admin'), false,
        'the replacement library admin cannot inherit the prior roster capability while its GET is pending');
      assert.equal(document.getElementById('staff-add-organization').value, '2');
      assert.equal(document.getElementById('staff-add-organization').disabled, true);
      for (const complete of pendingLibraryRosters) complete(response(503, { message: 'Replacement roster unavailable' }));
      await waitFor(() => !document.getElementById('staff-refresh').disabled &&
        document.getElementById('staff-access-status').textContent.includes('Replacement roster unavailable'));
      assert.equal(document.getElementById('staff-add-role').textContent.includes('Super admin'), false,
        'a failed replacement roster GET cannot restore the retired super-admin capability');
      capability.controller.signedOut();
      assert.equal(document.getElementById('staff-add-role').textContent.includes('Super admin'), false);
      capability.controller.dispose(); capability.dom.window.close();
    }

    for (const action of ['metadata', 'role', 'deactivate', 'reactivate']) {
      const activeTargetId = '9223372036854775806';
      const inactiveTargetId = '9223372036854775805';
      const otherUserId = '9223372036854775807';
      const roster = [
        staffUser(activeTargetId, { displayName: 'Ada Admin', role: 'admin', version: 'target-v1' }),
        staffUser(inactiveTargetId, { displayName: 'Inactive Selector', active: false, version: 'inactive-v1' }),
        staffUser(otherUserId, { displayName: 'Unrelated User', version: 'other-v1' })
      ];
      const commits = [];
      let rosterReads = 0;
      let mutationRequest;
      const lifecycle = await setupController(settingsModule, frontendRoot, superStaff,
        async (url, options = {}) => {
          const requestUrl = String(url);
          if (requestUrl.includes('/api/asap/staff/settings?orgId=')) return response(200, settingsData('system'));
          if (requestUrl.endsWith('/api/asap/staff/organizations')) return response(200, { code: 'ok', data: [
            { id: 1, name: 'System', abbreviation: null, organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org-v1' },
            { id: 2, name: 'Library Two', abbreviation: 'TWO', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-v2' },
            { id: 3, name: 'Library Three', abbreviation: 'THREE', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-v3' }
          ] });
          if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
          if (requestUrl === '/api/asap/staff/audit?limit=50') return response(200, { code: 'ok', data: [] });
          if (requestUrl === '/api/asap/staff/users' && (options.method || 'GET') === 'GET') {
            rosterReads += 1;
            return response(200, { canAssignSuperAdmin: true, users: roster });
          }
          const user = staffUser(otherUserId, {
            displayName: 'Unrelated User',
            active: action !== 'deactivate',
            version: `wrong-target-${action}`
          });
          const cleanup = { rulesDeactivated: 0, openTitleClaimsCleared: 0, openAdditionalCopyClaimsCleared: 0 };
          const requestedId = action === 'reactivate' ? inactiveTargetId : activeTargetId;
          const expectedRequest = action === 'metadata'
            ? requestUrl === `/api/asap/staff/users/${requestedId}` && options.method === 'PUT'
            : action === 'role'
              ? requestUrl === `/api/asap/staff/users/${requestedId}` && options.method === 'PUT'
              : action === 'deactivate'
                ? requestUrl === `/api/asap/staff/users/${requestedId}` && options.method === 'DELETE'
                : requestUrl === `/api/asap/staff/users/${requestedId}/reactivate` && options.method === 'POST';
          if (expectedRequest) {
            mutationRequest = { requestUrl, options };
            return response(200, { user, cleanup });
          }
          throw new Error(`Unexpected ${action} request: ${requestUrl}`);
        }, { onCommitted: (...args) => commits.push(args) });

      document.getElementById('settings-nav-staff').click();
      await waitFor(() => !document.getElementById('staff-refresh').disabled &&
        document.getElementById('staff-access-status').textContent === '');
      const targetId = action === 'reactivate' ? inactiveTargetId : activeTargetId;
      if (action === 'reactivate') document.getElementById('staff-show-inactive').click();
      const targetRow = [...document.querySelectorAll('.settings-staff-row')]
        .find(row => row.dataset.staffId === targetId);
      assert.ok(targetRow, `${action}: the requested staff row is present`);
      openRow(targetRow);
      if (action === 'metadata') {
        const email = targetRow.querySelector('input[type="email"]');
        email.value = 'draft-kept@example.org';
        email.dispatchEvent(new lifecycle.dom.window.Event('input', { bubbles: true }));
        [...targetRow.querySelectorAll('button')].find(button => button.textContent === 'Save changes').click();
      } else if (action === 'role' || action === 'reactivate') {
        const role = targetRow.querySelector('select[aria-label^="Role"]');
        role.value = 'staff';
        role.dispatchEvent(new lifecycle.dom.window.Event('change', { bubbles: true }));
        [...targetRow.querySelectorAll('button')]
          .find(button => button.textContent === (action === 'role' ? 'Save changes' : 'Reactivate')).click();
      } else {
        [...targetRow.querySelectorAll('button')].find(button => button.textContent === 'Deactivate').click();
      }
      await waitFor(() => Boolean(mutationRequest));
      await flush(); await flush();
      assert.equal(rosterReads, 1, `${action}: wrong-user evidence cannot authorize the follow-up roster refresh`);
      assert.equal(commits.length, 0, `${action}: a response for another staff ID is not committed`);
      assert.equal(lifecycle.controller.hasUnconfirmedOutcome(), true,
        `${action}: wrong-user response requires authoritative roster review`);
      assert.equal(lifecycle.controller.inspectDeparture().blocked, true);
      const currentTargetRow = [...document.querySelectorAll('.settings-staff-row')]
        .find(row => row.dataset.staffId === targetId);
      if (action === 'metadata') {
        assert.equal(currentTargetRow.querySelector('input[type="email"]').value, 'draft-kept@example.org',
          'metadata draft remains available after a mismatched target receipt');
      } else if (action === 'role' || action === 'reactivate') {
        assert.equal(currentTargetRow.querySelector('select[aria-label^="Role"]').value,
          'staff', 'access values remain available after a mismatched target receipt');
      }
      const unrelatedRow = [...document.querySelectorAll('.settings-staff-row')]
        .find(row => row.dataset.staffId === otherUserId);
      assert.ok(unrelatedRow);
      assert.equal(unrelatedRow.querySelector('.settings-cleanup-result').hidden, true,
        `${action}: cleanup evidence is never presented for the unrelated returned user`);
      assert.equal(unrelatedRow.querySelector('.settings-cleanup-result').textContent, '');
      lifecycle.controller.dispose();
      lifecycle.dom.window.close();
    }

    for (const transfer of ['setStaff', 'signout']) {
      const actorA = { id: '41', tenantId: 'sync-a', authenticationEmail: 'a@example.org',
        role: 'super_admin', organizationId: 1 };
      const actorB = { id: '42', tenantId: 'sync-b', authenticationEmail: 'b@example.org',
        role: 'super_admin', organizationId: 1 };
      let currentOwner = actorA;
      const syncReleases = [];
      const receiptOwners = [];
      const catalogCallbackOwners = [];
      const acceptedCatalogOwners = [];
      const sync = await setupController(settingsModule, frontendRoot, actorA,
        async (url, options = {}) => {
          const requestUrl = String(url);
          if (requestUrl.endsWith('/api/asap/staff/session')) return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
          if (requestUrl.includes('/api/asap/staff/settings?orgId=')) return response(200, settingsData('system'));
          if (requestUrl.endsWith('/api/asap/staff/organizations')) return response(200, { code: 'ok', data: [
            { id: 1, name: 'System', abbreviation: null, organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org-v1' },
            { id: 2, name: 'Library Two', abbreviation: 'TWO', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-v2' },
            { id: 3, name: 'Library Three', abbreviation: 'THREE', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-v3' }
          ] });
          if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
          if (requestUrl.endsWith('/api/asap/staff/organizations/sync') && options.method === 'POST') {
            return new Promise(resolve => syncReleases.push(() => resolve(response(200, {
              code: 'synced', data: { received: 3, changed: 2 }
            }))));
          }
          throw new Error(`Unexpected organization-sync request: ${requestUrl}`);
        }, {
          onCommitted: (_message, owner) => receiptOwners.push(owner),
          onOrganizationCatalogCommitted: owner => {
            catalogCallbackOwners.push(owner);
            if (owner === currentOwner) acceptedCatalogOwners.push(owner);
          }
        });

      const button = document.getElementById('btn-sync-organizations');
      button.click();
      await waitFor(() => syncReleases.length === 1);
      assert.equal(button.disabled, true, 'the current owner’s sync is disabled while its request is pending');
      if (transfer === 'signout') {
        currentOwner = null;
        sync.controller.signedOut();
      }
      currentOwner = actorB;
      sync.controller.setStaff(actorB);
      await sync.controller.activate();
      const canStartReplacementSync = !button.disabled;
      if (canStartReplacementSync) {
        button.click();
        await waitFor(() => syncReleases.length === 2);
      }
      syncReleases[0]();
      for (let attempt = 0; attempt < 8; attempt++) await flush();
      const replacementSyncStillOwnsDisabledState = syncReleases.length === 2 && button.disabled;
      const replacementMessageBeforeItsResult = document.getElementById('organizations-sync-result').textContent;
      if (syncReleases.length === 2) {
        syncReleases[1]();
        await waitFor(() => document.getElementById('organizations-sync-result').textContent.includes('Synchronized 3 organizations'));
      }
      assert.equal(canStartReplacementSync, true,
        `${transfer}: the new owner can start its own sync without waiting for the retired request`);
      assert.equal(replacementSyncStillOwnsDisabledState, true,
        `${transfer}: the retired completion cannot unlock a newer pending sync`);
      assert.equal(replacementMessageBeforeItsResult, 'Synchronizing Polaris organizations...',
        `${transfer}: the retired result does not replace the new owner's pending feedback`);
      assert.deepStrictEqual(receiptOwners, [actorA, actorB], `${transfer}: each known response retains its captured owner`);
      assert.deepStrictEqual(catalogCallbackOwners, [actorA, actorB]);
      assert.deepStrictEqual(acceptedCatalogOwners, [actorB], `${transfer}: stale catalog callback cannot affect the replacement owner`);
      assert.equal(button.disabled, false, `${transfer}: the replacement sync releases its own busy state`);
      sync.controller.dispose();
      sync.dom.window.close();
    }

    console.log('Settings Staff Access roster, lifecycle, audit, and scope checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
