const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const organizationCatalog = [
  { id: 1, name: 'System', abbreviation: 'SYS', organizationCodeId: 1, parentOrganizationId: null, isActive: true, lastSyncedUtc: null, version: 'org-1' },
  { id: 2, name: 'Library Two', abbreviation: 'L2', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, lastSyncedUtc: null, version: 'org-2' },
  { id: 3, name: 'Library Three', abbreviation: 'L3', organizationCodeId: 2, parentOrganizationId: 1, isActive: false, lastSyncedUtc: null, version: 'org-3' },
  { id: 20, name: 'Branch Twenty', abbreviation: 'B20', organizationCodeId: 3, parentOrganizationId: 2, isActive: true, lastSyncedUtc: null, version: 'org-20' },
  { id: 22, name: 'Inactive Branch', abbreviation: 'B22', organizationCodeId: 3, parentOrganizationId: 2, isActive: false, lastSyncedUtc: null, version: 'org-22' },
  { id: 21, name: 'Unclassified Reference', abbreviation: null, organizationCodeId: null, parentOrganizationId: 2, isActive: true, lastSyncedUtc: null, version: 'org-21' }
];
(async () => {
  await fixture(async ({ load, dom }) => {
    const { createRouter, parseStaffRoute } = await load('router');
    const { createNavigationController } = await load('navigation');
    const { createSessionIdentity } = await load('session-identity');
    const id = '9223372036854775807';
    const route = parseStaffRoute(`https://localhost/staff/?stage=pending_hold&scope=2&request=${id}`);
    assert.ok(Object.isFrozen(route));
    assert.equal(route.requestId, id);
    const session = createSessionIdentity(); session.accept({ id: '20', role: 'super_admin', organizationId: 1 });
    let stamp = 0, blocked = false, dirty = false, finish, disposed = 0, presented = 0;
    const router = createRouter();
    const queue = { refresh: async () => true, activate() {}, deactivate() {}, setLibraries() {} };
    const navigation = createNavigationController({ router, sessionIdentity: session, announce() {},
      present() { presented += 1; }, closeTransient() { disposed += 1; }, onInvalidate() {}, onContextChanged() {},
      views: { queue, 'additional-copies': queue, settings: { currentScope: () => 'system' } },
      getFeatures: () => [{ key: 'request', inspectDeparture }],
      request: () => new Promise(resolve => { finish = resolve; }) });
    function inspectDeparture() { return { stamp, blocked, dirty, message: 'Pending', confirmMessage: 'Discard?' }; }
    navigation.start();
    router.pushStage('suggestion', { scope: '2' }); router.remember();
    const accepted = router.snapshot().href;
    router.pushStage('suggestion', { scope: '3' });
    const moving = navigation.navigateFromUrl();
    assert.equal(disposed, 0);
    blocked = true; stamp += 1;
    finish({ data: organizationCatalog }); await moving;
    assert.equal(disposed, 0, 'new pending command invalidates earlier departure permission');
    assert.equal(presented, 0);
    await new Promise(resolve => setTimeout(resolve, 35));
    assert.equal(dom.window.location.href, accepted);
    blocked = false;
    router.pushStage('suggestion', { scope: '3' });
    const editingDuringValidation = navigation.navigateFromUrl();
    dirty = true;
    dom.window.confirm = () => false;
    finish({ data: organizationCatalog }); await editingDuringValidation;
    assert.equal(disposed, 0, 'a previously clean owner must obtain dirty departure permission even with the same stamp');
    await new Promise(resolve => setTimeout(resolve, 35));
    assert.equal(dom.window.location.href, accepted);
    navigation.dispose();
    dom.window.dispatchEvent(new dom.window.PopStateEvent('popstate'));
    assert.equal(presented, 0, 'disposed router cannot reactivate features');
  });
  await fixture(async ({ load }) => {
    const { createRouter } = await load('router');
    const { createNavigationController } = await load('navigation');
    const { createSessionIdentity } = await load('session-identity');
    const session = createSessionIdentity(); session.accept({ id: '20', role: 'super_admin', organizationId: 1 });
    let queueLibraries = [], copyLibraries = [];
    const queue = { refresh: async () => true, activate() {}, deactivate() {},
      setLibraries(value) { queueLibraries = value; } };
    const copyQueue = { ...queue, setLibraries(value) { copyLibraries = value; } };
    const catalogReads = [];
    const router = createRouter();
    const navigation = createNavigationController({ router, sessionIdentity: session, announce() {},
      present() {}, closeTransient() {}, onInvalidate() {}, onContextChanged() {},
      views: { queue, 'additional-copies': copyQueue, settings: { currentScope: () => 'system' } },
      getFeatures: () => [],
      request: path => {
        assert.equal(path, '/api/asap/staff/organizations');
        if (catalogReads.length < 2) return new Promise(resolve => catalogReads.push(resolve));
        return Promise.resolve({ data: organizationCatalog });
      } });
    navigation.start();

    router.pushStage('pending_hold', { scope: '20' });
    const staleBranchDeepLink = navigation.navigateFromUrl();
    await new Promise(resolve => setImmediate(resolve));
    router.pushStage('pending_hold', { scope: '2' });
    const currentLibraryDeepLink = navigation.navigateFromUrl();
    await new Promise(resolve => setImmediate(resolve));
    catalogReads[0]({ data: organizationCatalog });
    assert.equal(await staleBranchDeepLink, false, 'a superseded branch deep-link cannot become accepted scope');
    catalogReads[1]({ data: organizationCatalog });
    assert.equal(await currentLibraryDeepLink, true);
    assert.equal(navigation.context().scope, '2');
    assert.deepEqual(queueLibraries.map(item => String(item.id)), ['2']);
    assert.deepEqual(copyLibraries.map(item => String(item.id)), ['2']);

    for (const invalidScope of ['3', '20', '21', '22']) {
      router.pushStage('pending_hold', { scope: invalidScope });
      assert.equal(await navigation.navigateFromUrl(), true);
      assert.equal(navigation.context().scope, 'all', `${invalidScope} is not an active code-2 library authority`);
      assert.deepEqual(queueLibraries.map(item => String(item.id)), ['2']);
      assert.deepEqual(copyLibraries.map(item => String(item.id)), ['2']);
    }

    navigation.dispose();
  });
  console.log('Immutable routes, guarded async validation, history restoration and router disposal passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
