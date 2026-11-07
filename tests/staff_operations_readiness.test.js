const assert = require('node:assert/strict');
const { fixture, emailHistoryPage } = require('./helpers/staff-controller.cjs');
const settle = async () => { for (let i = 0; i < 12; i++) await new Promise(resolve => setImmediate(resolve)); };
const staff = { id: '7', tenantId: 'tenant', authenticationEmail: 'admin@example.org', role: 'super_admin', organizationId: 1, version: 'v1' };
const libraries = [{ id: 2, name: 'Library Two', organizationCodeId: 2, isActive: true },
  { id: 3, name: 'Library Three', organizationCodeId: 2, isActive: true }];

(async () => {
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createStaffShell } = await load('shell');
    const { createOperationsController } = await load('operations-controller');
    const identity = createSessionIdentity(), owner = identity.accept(staff);
    const healthChecks = [], savedContextChecks = [], writes = [];
    const request = async (path, init = {}) => {
      const url = new URL(path, 'https://localhost');
      if (init.method === 'POST') { writes.push(path); throw new Error('Browsing must not dispatch.'); }
      if (url.pathname.endsWith('/email-readiness')) { healthChecks.push(url.searchParams.get('organizationId') || 'default'); return { state: 'ready' }; }
      if (url.pathname.endsWith('/test-context')) {
        const organizationId = Number(url.searchParams.get('organizationId')); savedContextChecks.push(organizationId);
        return { code: 'ok', data: { organizationId, scopeName: 'Saved context', deliveryMode: 'postmark', state: 'ready', configurationVersion: 'A'.repeat(64) } };
      }
      if (url.pathname.endsWith('/organizations')) return { data: libraries };
      if (url.pathname.endsWith('/email-operations')) return emailHistoryPage({ count: 60, libraries,
        items: [{ id: '9007199254740993', organizationId: 2, status: 'pending', deliveryClass: 'business_event', canRetry: false }] }, path);
      if (url.pathname.endsWith('/audit')) return { data: { items: [], count: 0, page: 1, pageSize: 25, libraries, actors: [] } };
      return { items: [] };
    };
    let operations;
    const shell = createStaffShell({ root: dom.window.document, sessionIdentity: identity,
      getContext: () => ({ activeView: 'operations', scope: 'all' }), settingsScope: () => 'system',
      operationsScope: () => operations.currentScope(), findTitle: () => null,
      openRecentTitle() {}, onViewIntent() {}, onSignOutIntent() {}, request });
    operations = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity, request,
      announce: shell.announce, onScopeChange: shell.refreshReadiness, onReceipt() {}, clearReceipt() {} });
    operations.setStaff(owner); shell.showWorkspace(owner);
    operations.activate({ section: 'workflow', runScope: '2', testScope: '3', email: { scope: '2' } });
    await operations.refresh(); await settle();
    assert.equal(healthChecks.at(-1), '2', 'an explicit restored Run context initializes Operations readiness');
    const healthBaseline = healthChecks.length, contextBaseline = savedContextChecks.length;
    const change = async (field, value) => {
      const control = get(`#email-${field === 'scope' ? 'view-scope' : field}`); control.value = value;
      control.dispatchEvent(new dom.window.Event(field === 'search' ? 'input' : 'change'));
      if (field === 'search') get('#email-filters').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
      await settle();
    };
    get('#operations-email-tab').click(); await settle();
    for (const [field, value] of [['scope', 'system'], ['search', 'safe detail'], ['status', 'pending'], ['type', 'business_event'],
      ['from', '2026-11-01'], ['through', '2026-11-02'], ['sort', 'oldest']]) await change(field, value);
    get('#email-next').click(); await settle(); get('#email-prev').click(); await settle();
    get('#email-clear').click(); await settle();
    operations.activate({ section: 'email', runScope: '2', testScope: '3', email: { scope: 'system', reference: '9007199254740993' } });
    await operations.refresh(); get('#email-reference-clear').click(); await settle();
    for (const section of ['audit', 'workflow', 'email']) { get(`#operations-${section}-tab`).click(); await settle(); }
    assert.equal(healthChecks.length, healthBaseline, 'history controls, reference, paging, Refresh and tab selection never reach provider readiness');
    assert.equal(savedContextChecks.length, contextBaseline, 'history does not invalidate or reload the saved test context');
    assert.equal(operations.currentScope(), '2', 'history and section presentation leave the accepted action readiness context intact');
    get('#operations-scope').value = '3'; get('#operations-scope').dispatchEvent(new dom.window.Event('change')); await settle();
    assert.equal(healthChecks.at(-1), '3');
    get('#email-test-scope').value = '2'; get('#email-test-scope').dispatchEvent(new dom.window.Event('change')); await settle();
    assert.equal(healthChecks.at(-1), '2', 'an explicit Test settings scope checks its own context');
    await operations.openEmailScope(3); await settle();
    assert.equal(healthChecks.at(-1), '3', 'the authorized Settings handoff checks its original settings context');
    assert.equal(healthChecks.length, healthBaseline + 3);
    assert.equal(writes.length, 0);
    operations.dispose(); shell.dispose();
  });

  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await load('operations-controller');
    const identity = createSessionIdentity(), owner = identity.accept(staff), checks = [];
    let operations;
    operations = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
      announce() {}, onScopeChange: () => checks.push(operations.currentScope()), onReceipt() {}, clearReceipt() {},
      request: async () => ({ items: [] }) });
    operations.setStaff(owner);
    operations.activate({ section: 'email', runScope: 'all', testScope: '3', email: { scope: 'system' } });
    assert.equal(operations.currentScope(), '3', 'initial explicit test action context takes precedence over the history view');
    assert.deepEqual(checks, ['3']);
    get('#operations-workflow-tab').click(); get('#operations-audit-tab').click();
    assert.deepEqual(checks, ['3'], 'tabs do not change the restored readiness context');
    operations.dispose();
  });
  console.log('Operations readiness composition: browsing makes no extra provider checks; explicit action contexts stay scoped');
})().catch(error => { console.error(error); process.exitCode = 1; });
