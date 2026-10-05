const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
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
      getFeatures: () => [{ key: 'request', inspectDeparture: () => ({ stamp, blocked, dirty,
        message: 'Pending', confirmMessage: 'Discard?' }) }],
      request: () => new Promise(resolve => { finish = resolve; }) });
    navigation.start();
    router.pushStage('suggestion', { scope: '2' }); router.remember();
    const accepted = router.snapshot().href;
    router.pushStage('suggestion', { scope: '3' });
    const moving = navigation.navigateFromUrl();
    assert.equal(disposed, 0);
    blocked = true; stamp += 1;
    finish({ data: [{ id: 3, isActive: true }] }); await moving;
    assert.equal(disposed, 0, 'new pending command invalidates earlier departure permission');
    assert.equal(presented, 0);
    await new Promise(resolve => setTimeout(resolve, 35));
    assert.equal(dom.window.location.href, accepted);
    blocked = false;
    router.pushStage('suggestion', { scope: '3' });
    const editingDuringValidation = navigation.navigateFromUrl();
    dirty = true;
    dom.window.confirm = () => false;
    finish({ data: [{ id: 3, isActive: true }] }); await editingDuringValidation;
    assert.equal(disposed, 0, 'a previously clean owner must obtain dirty departure permission even with the same stamp');
    await new Promise(resolve => setTimeout(resolve, 35));
    assert.equal(dom.window.location.href, accepted);
    navigation.dispose();
    dom.window.dispatchEvent(new dom.window.PopStateEvent('popstate'));
    assert.equal(presented, 0, 'disposed router cannot reactivate features');
  });
  console.log('Immutable routes, guarded async validation, history restoration and router disposal passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
