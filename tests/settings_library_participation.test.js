const assert = require('node:assert/strict');
const { test } = require('node:test');
const { fixture } = require('./helpers/staff-controller.cjs');

const actor = { id: '20', tenantId: 'participation', authenticationEmail: 'admin@example.org',
  role: 'super_admin', organizationId: 1 };
const response = (status, body) => ({ ok: status < 400, status, statusText: 'Test response', json: async () => body });
const copy = value => JSON.parse(JSON.stringify(value));
const organization = (id, name, code, parent, active = false, abbreviation = null) => ({
  id, name, abbreviation, organizationCodeId: code, parentOrganizationId: parent,
  isActive: active, version: `org-${id}-v1`
});
const catalog = () => [
  organization(1, 'System', 1, null, true),
  organization(4, 'Columbus', 2, 1, true, 'COL'),
  organization(3, 'Bexley', 2, 1, false, 'BEX'),
  organization(2, 'Alexandria', 2, 1, true, 'ALEX'),
  organization(20, 'Zebra site', 3, 2, false, 'ZEB'),
  organization(21, 'Central organization', 3, 2, false, 'CENT'),
  organization(22, 'Indirect site', 3, 21, false, 'IND'),
  organization(23, 'Bexley site', 3, 3, false, 'SITE'),
  organization(24, 'Columbus site', 3, 4),
  organization(25, 'Alexandria in name only', 3, 3),
  organization(30, 'Missing parent', 3, 999),
  organization(31, 'Cycle A', 3, 32),
  organization(32, 'Cycle B', 3, 31),
  organization(33, 'Cycle descendant', 3, 31),
  organization(34, 'Self parent', 3, 34)
];
const flush = () => new Promise(resolve => setImmediate(resolve));
async function until(predicate) {
  for (let attempt = 0; attempt < 100 && !predicate(); attempt++) await flush();
  assert.ok(predicate(), 'Expected participation state');
}

async function participationFixture(journey, { staff = actor, organizations = catalog() } = {}) {
  await fixture(async ui => {
    const { createSettingsController } = await ui.load('settings');
    ui.get('#workspace').hidden = false;
    ui.get('#settings-view').hidden = false;
    const model = { catalog: copy(organizations), version: 1, loginNote: 'Saved note', mutate: null, sync: null };
    const requests = [], writes = [], catalogs = [], commits = [], refreshed = [], panels = [];
    global.fetch = async (path, init = {}) => {
      requests.push({ path, init });
      if (path.endsWith('/session')) return response(200, { authenticated: true, staff, antiforgeryToken: 'test' });
      if (path.endsWith('/organizations')) return response(200, { code: 'ok', data: copy(model.catalog) });
      if (path.includes('/patron-codes?')) return response(200, { data: [] });
      if (path.includes('/settings?')) {
        if (model.failSettings) return response(503, { message: 'Current settings are unavailable.' });
        const scope = new URL(path, 'https://localhost').searchParams.get('orgId');
        const empty = { exists: false, values: [] };
        const configured = { workflow: {}, patron: { loginNote: model.loginNote }, email: {},
          publicationOptions: empty, commonCreators: empty, allowedPatronCodeIds: empty,
          providers: [], formats: [], templates: [], branding: { hasLogo: false } };
        const libraries = model.catalog.filter(row => row.id > 1 && row.organizationCodeId === 2);
        return response(200, { orgId: scope, version: `settings-${model.version}`,
          organization: scope === 'system' ? null : { id: Number(scope), name: 'Scoped library', active: true },
          stored: { configuredSystem: configured, libraryOverride: scope === 'system' ? null : { ...configured, patron: {} },
            systemSettings: { enabledLibraryOrgIds: libraries.filter(row => row.isActive).map(row => row.id),
              libraryOrgIds: libraries.map(row => row.id) },
            polaris: {}, origins: [], formats: [], providers: [], formatRules: [], customFields: [],
            templates: [], autoClaimRules: [] },
          effective: { formats: [], customFields: [], externalSearchProviders: [], allowedPatronCodeIds: [],
            publicationOptions: [], commonCreators: [] }, ui_text: {}, emails: {}, workflow: {}, autoClaimStaff: [] });
      }
      const body = JSON.parse(init.body);
      writes.push({ path, body });
      if (path === '/api/asap/staff/settings') {
        model.loginNote = body.patron.loginNote;
        return response(200, { code: 'saved', data: { orgId: body.orgId, version: `settings-${++model.version}` } });
      }
      if (path.endsWith('/organizations/sync')) return model.sync
        ? model.sync() : response(200, { code: 'synced', data: { received: model.catalog.length, changed: 0 } });
      const action = path.match(/\/organizations\/(\d+)\/(activate|deactivate)$/);
      if (action) {
        if (model.mutate) return model.mutate(action, body);
        const row = model.catalog.find(row => row.id === Number(action[1]));
        assert.equal(body.version, row.version, 'Each action sends its accepted organization rowversion.');
        row.isActive = action[2] === 'activate';
        row.version += '-next';
        return response(200, { code: row.isActive ? 'activated' : 'deactivated',
          data: { organizationId: row.id, revokedPatronSessions: 0, version: row.version } });
      }
      throw new Error(`Unexpected participation path: ${path}`);
    };
    const controller = createSettingsController({ root: ui.get('#settings-view'), tab: ui.get('#settings-view-tab'),
      announce: () => {}, onPanelChange: panel => panels.push(panel),
      onCommitted: (...args) => commits.push(args), onOrganizationCatalogCommitted: (...args) => catalogs.push(args),
      onRefreshed: (...args) => refreshed.push(args) });
    try {
      controller.bind(); controller.setStaff(staff); await controller.activate();
      await journey({ ...ui, controller, model, requests, writes, catalogs, commits, refreshed, panels });
    } finally { controller.dispose(); }
  });
}

test('participation has one Polaris editor and reference groups follow authoritative ancestor chains', () =>
  participationFixture(async ({ get, dom, controller }) => {
    const document = dom.window.document;
    assert.equal(get('#settings-staff #settings-organizations-list'), null);
    assert.equal(get('#enabled-libraries-checkbox-container'), null);
    assert.equal(get('#settings-organizations-list').closest('[data-settings-panel-content]').id, 'settings-polaris');
    assert.match(get('#settings-organization-catalog').textContent, /does not activate or deactivate the organization in Polaris/);
    assert.match(get('#settings-reference-organizations-heading').parentElement.textContent, /not necessarily available pickup locations/);
    const ids = [...document.querySelectorAll('[data-organization-id]')].map(row => Number(row.dataset.organizationId));
    assert.equal(ids.length, catalog().length);
    assert.deepEqual([...ids].sort((a, b) => a - b), catalog().map(row => row.id).sort((a, b) => a - b),
      'Every synchronized record renders once, including malformed parent chains.');
    assert.deepEqual([...get('#settings-organizations-list').children].map(row => row.dataset.organizationId), ['2', '3', '4']);
    const groups = [...get('#settings-reference-organizations').children];
    assert.deepEqual(groups.map(group => group.querySelector('h4').textContent),
      ['Alexandria (ALEX)', 'Bexley (BEX)', 'Columbus (COL)', 'Other / unassociated organizations']);
    assert.deepEqual(groups.map(group => [...group.querySelectorAll('[data-organization-id]')].map(row => row.dataset.organizationId)),
      [['21', '22', '20'], ['25', '23'], ['24'], ['31', '32', '33', '30', '34', '1']]);
    assert.equal(get('[data-organization-id="22"] small').textContent, 'Within: Central organization (CENT)');
    assert.equal(get('[data-organization-id="20"] small'), null);
    assert.equal(get('[data-organization-id="3"] .status-badge').textContent, 'Inactive');
    assert.equal(get('#settings-reference-organizations').querySelectorAll('button').length, 0);
    assert.equal(get('#settings-organizations-list').querySelectorAll('button').length, 3);
    assert.ok([...get('#settings-reference-organizations').querySelectorAll('.status-badge')]
      .every(badge => badge.textContent === 'Reference only'));
    assert.equal(controller.isDirty(), false);
  }));

test('summary navigation preserves unrelated drafts and later settings saves cannot replace participation', () =>
  participationFixture(async ({ get, dom, controller, writes, catalogs, panels }) => {
    assert.equal(get('#organizations-status-message').textContent, '2 of 3 synchronized libraries active in ASAP; 1 inactive.');
    const note = get('#patron-login-note');
    note.value = 'Unrelated draft'; note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    const before = get('#settings-version').value;
    get('#manage-library-participation').click();
    assert.equal(get('#settings-polaris').hidden, false);
    assert.equal(dom.window.document.activeElement, get('#settings-organization-catalog'));
    assert.deepEqual(panels, ['polaris']);
    assert.equal(get('#settings-scope').value, 'system');
    assert.equal(note.value, 'Unrelated draft');
    assert.equal(get('#settings-version').value, before);
    assert.equal(controller.isDirty(), true);
    assert.equal(writes.length, 0, 'Navigation does not save or discard any draft.');
    assert.equal(Boolean(get('#confirmation-dialog')?.open), false);
    controller.discardDraft();
    get('[aria-label="Activate Bexley"]').click();
    await until(() => catalogs.length === 1 && !controller.hasPendingMutation());
    assert.match(get('#organizations-status-message').textContent, /^3 of 3/);
    note.value = 'Saved unrelated edit'; note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    get('#settings-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    await until(() => writes.length === 2 && !controller.hasPendingMutation());
    assert.equal(Object.hasOwn(writes[1].body.systemSettings, 'enabledLibraryOrgIds'), false);
    assert.equal(catalogs.length, 1, 'An unrelated settings save does not announce a participation mutation.');
    assert.equal(get('[data-organization-id="3"] .status-badge').textContent, 'Active');
    assert.equal(controller.isDirty(), false);
  }));

test('canonical actions retain confirmation, rowversion conflicts, explicit retry and immediate activation/deactivation', () =>
  participationFixture(async ({ get, dom, controller, writes, model, catalogs }) => {
    controller.activatePanel('polaris');
    require('./helpers/confirmation.cjs').answerConfirmations(dom.window, () => false);
    const canceledAction = get('[aria-label="Activate Bexley"]');
    canceledAction.focus(); canceledAction.click(); await flush(); await flush();
    assert.equal(writes.length, 0);
    assert.equal(dom.window.document.activeElement, canceledAction);
    require('./helpers/confirmation.cjs').answerConfirmations(dom.window, () => true);
    model.mutate = () => {
      model.catalog.find(row => row.id === 3).version = 'external-org-3';
      return response(409, { message: 'Participation changed elsewhere. Review current values.' });
    };
    get('[aria-label="Activate Bexley"]').click();
    await until(() => writes.length === 1 && !controller.hasPendingMutation());
    assert.deepEqual(writes[0].body, { version: 'org-3-v1' });
    assert.equal(catalogs.length, 0);
    assert.match(get('#settings-message').textContent, /changed elsewhere/);
    assert.equal(get('[data-organization-id="3"] .status-badge').textContent, 'Inactive');
    model.mutate = null;
    get('[aria-label="Activate Bexley"]').click();
    await until(() => catalogs.length === 1 && !controller.hasPendingMutation());
    assert.deepEqual(writes[1].body, { version: 'external-org-3' });
    assert.equal(get('[data-organization-id="3"] .status-badge').textContent, 'Active');
    get('[aria-label="Deactivate Bexley"]').click();
    await until(() => catalogs.length === 2 && !controller.hasPendingMutation());
    assert.deepEqual(writes[2].body, { version: 'external-org-3-next' });
    assert.equal(get('[data-organization-id="3"] .status-badge').textContent, 'Inactive');
    assert.equal(get('[data-library-organization-id="3"] [data-organization-id="23"]').dataset.organizationId, '23',
      'Deactivation never hides a library-associated reference group.');
  }));

test('failed sync keeps the current groups and authoritative review/success refresh regroup references', () =>
  participationFixture(async ({ get, controller, model, catalogs, refreshed }) => {
    controller.activatePanel('polaris');
    const before = get('#settings-reference-organizations').textContent;
    model.sync = () => response(502, { message: 'Deterministic provider unavailable.' });
    get('#btn-sync-organizations').click();
    await until(() => controller.hasUnconfirmedOutcome() && !controller.hasPendingMutation());
    assert.equal(get('#settings-reference-organizations').textContent, before);
    assert.equal(catalogs.length, 0);
    get('#settings-refresh').click();
    await until(() => !controller.hasUnconfirmedOutcome());
    assert.equal(refreshed.at(-1)[1].catalogChanged, true);
    model.sync = () => {
      model.catalog.find(row => row.id === 21).parentOrganizationId = 4;
      model.catalog.find(row => row.id === 4).isActive = false;
      return response(200, { code: 'synced', data: { received: model.catalog.length, changed: 2 } });
    };
    get('#btn-sync-organizations').click();
    await until(() => catalogs.length === 1 && !controller.hasPendingMutation());
    assert.equal(get('[data-library-organization-id="4"] [data-organization-id="22"]').dataset.organizationId, '22');
    assert.equal(get('[data-library-organization-id="2"] [data-organization-id="22"]'), null);
    assert.match(get('#organizations-status-message').textContent, /^1 of 3/);
    assert.match(get('#organizations-sync-result').textContent, /^Synchronized 15 organizations\.$/);
  }));

test('participation restores its updated action focus after success/conflict and respects newer focus', () =>
  participationFixture(async ({ get, dom, controller, writes, model, catalogs }) => {
    controller.activatePanel('polaris');
    const activate = get('[aria-label="Activate Bexley"]');
    activate.focus(); activate.click();
    await until(() => catalogs.length === 1 && !controller.hasPendingMutation());
    assert.equal(activate.isConnected, false);
    assert.equal(dom.window.document.activeElement, get('[aria-label="Deactivate Bexley"]'));
    model.mutate = () => {
      model.catalog.find(row => row.id === 3).version = 'external-focus-version';
      return response(409, { message: 'Participation changed elsewhere.' });
    };
    get('[aria-label="Deactivate Bexley"]').click();
    await until(() => writes.length === 2 && !controller.hasPendingMutation());
    assert.equal(dom.window.document.activeElement, get('[aria-label="Deactivate Bexley"]'),
      'Conflict review focuses the replacement action without replaying the change.');
    let complete;
    model.mutate = () => new Promise(resolve => { complete = resolve; });
    get('[aria-label="Deactivate Bexley"]').click(); await until(() => complete);
    get('#settings-refresh').focus();
    const row = model.catalog.find(row => row.id === 3);
    row.isActive = false; row.version = 'focus-deactivated';
    complete(response(200, { code: 'deactivated', data: { organizationId: 3, revokedPatronSessions: 0, version: row.version } }));
    await until(() => catalogs.length === 2 && !controller.hasPendingMutation());
    assert.equal(dom.window.document.activeElement, get('#settings-refresh'), 'A newer focus choice keeps ownership.');
  }));

test('participation focus falls back to Refresh for failed review and to the catalog for a removed action', () =>
  participationFixture(async ({ get, dom, controller, model, catalogs }) => {
    controller.activatePanel('polaris');
    model.mutate = () => {
      const row = model.catalog.find(row => row.id === 3);
      row.isActive = true; row.version = 'focus-activated'; model.failSettings = true;
      return response(200, { code: 'activated', data: { organizationId: 3, revokedPatronSessions: 0, version: row.version } });
    };
    const action = get('[aria-label="Activate Bexley"]');
    action.focus(); action.click();
    await until(() => catalogs.length === 1 && !controller.hasPendingMutation());
    assert.equal(get('#settings-form').inert, true);
    assert.equal(dom.window.document.activeElement, get('#settings-refresh'));
    model.failSettings = false; get('#settings-refresh').click();
    await until(() => get('#settings-form').inert === false);
    model.mutate = () => {
      model.catalog = model.catalog.filter(row => row.id !== 3);
      return response(200, { code: 'deactivated', data: { organizationId: 3, revokedPatronSessions: 0, version: 'removed-after-commit' } });
    };
    const deactivate = get('[aria-label="Deactivate Bexley"]');
    deactivate.focus(); deactivate.click();
    await until(() => catalogs.length === 2 && !controller.hasPendingMutation());
    assert.equal(get('[data-organization-id="3"]'), null);
    assert.equal(dom.window.document.activeElement, get('#settings-organization-catalog'));
  }));

test('library context disables participation/navigation and never switches scope or submits system keys', () =>
  participationFixture(async ({ get, dom, controller, writes, requests }) => {
    const target = await controller.prepareScope('3'); target.accept();
    controller.activatePanel('start');
    assert.equal(get('#manage-library-participation').matches(':disabled'), true);
    const before = requests.length;
    get('#manage-library-participation').click();
    assert.equal(get('#settings-scope').value, '3');
    assert.equal(get('#settings-start').hidden, false);
    assert.equal(requests.length, before);
    controller.activatePanel('polaris');
    for (const action of get('#settings-organizations-list').querySelectorAll('button')) {
      assert.equal(action.matches(':disabled'), true); action.click();
    }
    assert.equal(writes.length, 0);
    controller.activatePanel('patron');
    const note = get('#patron-login-note');
    note.closest('[data-setting-section]').querySelector('.settings-override-toggle').click();
    note.value = 'Library-only draft'; note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    get('#settings-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    await until(() => writes.length === 1 && !controller.hasPendingMutation());
    assert.equal(Object.hasOwn(writes[0].body, 'systemSettings'), false);
    assert.equal(Object.hasOwn(writes[0].body, 'polaris'), false);
  }));

test('late participation receipts cannot render over a replacement actor catalog', () =>
  participationFixture(async ({ get, dom, controller, model }) => {
    controller.activatePanel('polaris');
    let complete;
    model.mutate = () => new Promise(resolve => { complete = resolve; });
    get('[aria-label="Activate Bexley"]').focus(); get('[aria-label="Activate Bexley"]').click(); await until(() => complete);
    controller.setStaff({ ...actor, id: '21', authenticationEmail: 'replacement@example.org' });
    model.catalog = [organization(1, 'Replacement system', 1, null, true), organization(2, 'Replacement library', 2, 1, true)];
    await controller.activate('polaris');
    const summary = get('#organizations-status-message').textContent;
    const currentFocus = dom.window.document.activeElement;
    complete(response(200, { code: 'activated', data: { organizationId: 3, revokedPatronSessions: 0, version: 'late' } }));
    await flush(); await flush();
    assert.equal(get('#organizations-status-message').textContent, summary);
    assert.match(get('#settings-organizations-list').textContent, /Replacement library/);
    assert.doesNotMatch(get('#settings-organizations-list').textContent, /Bexley/);
    assert.equal(dom.window.document.activeElement, currentFocus, 'A stale receipt cannot take replacement-actor focus.');
  }));

test('empty eligible and reference groups explain their current state', async () => {
  await participationFixture(async ({ get }) => {
    assert.match(get('#settings-organizations-list').textContent, /No eligible library organizations/);
    assert.match(get('#organizations-status-message').textContent, /No eligible libraries are synchronized/);
  }, { organizations: [organization(1, 'System', 1, null, true)] });
  await participationFixture(async ({ get }) => {
    assert.equal(get('#settings-reference-organizations').textContent, 'No other synchronized organizations.');
    assert.equal(get('#settings-organizations-list').querySelector('button'), null);
  }, { staff: { ...actor, role: 'admin', organizationId: 2 }, organizations: [organization(2, 'Only library', 2, 1, true)] });
});
