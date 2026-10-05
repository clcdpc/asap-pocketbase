const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');

(async () => {
  let cases = 0;
  for (const scenario of ['clean', 'dirty', 'failed target', 'new draft', 'changed draft', 'actor', 'context', 'owner', 'once', 'blocked', 'disposed', 'preferences', 'superseded']) {
    await fixture(async ({ load, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createNavigationController } = await load('navigation');
      const identity = createSessionIdentity();
      identity.accept({ id: '20', tenantId: 'a', role: 'super_admin', organizationId: 1 });
      let dirty = ['dirty', 'failed target', 'changed draft'].includes(scenario), stamp = 1, blocked = false;
      let owner = {}, confirms = 0, discards = 0;
      const feature = { key: 'source', inspectDeparture: () => ({ dirty, stamp, blocked, owner, confirmMessage: 'Discard?' }),
        discardDeparture() { discards++; dirty = false; } };
      dom.window.confirm = () => { confirms++; return true; };
      const navigation = createNavigationController({ sessionIdentity: identity, router: { busy: () => false, dispose() {} },
        getFeatures: () => [feature, { key: 'other', inspectDeparture: otherInspection, discardDeparture() { discards++; } }],
        views: {}, announce() {}, present() {}, closeTransient() {}, onInvalidate() {}, onContextChanged() {} });
      function otherInspection() { return { dirty: false, blocked: false }; }
      const departure = navigation.prepareDeparture();
      assert.ok(departure);
      assert.equal(discards, 0, 'preparation never discards');
      assert.equal(confirms, dirty ? 1 : 0);
      if (scenario === 'failed target') {
        await assert.rejects(async () => { throw new Error('Target unavailable'); });
        assert.equal(dirty, true); assert.equal(discards, 0);
      } else {
        if (scenario === 'new draft') dirty = true;
        if (scenario === 'changed draft') stamp++;
        if (scenario === 'actor') identity.accept({ id: '21', tenantId: 'b', role: 'super_admin', organizationId: 1 });
        if (scenario === 'context') navigation.align({ scope: '3' });
        if (scenario === 'owner') owner = {};
        if (scenario === 'blocked') blocked = true;
        if (scenario === 'disposed') navigation.dispose();
        if (scenario === 'superseded') navigation.prepareDeparture();
        if (scenario === 'preferences') identity.updatePreferences({ ...identity.preferences(), version: 'v2' }, identity.preferences());
        const rejected = ['actor', 'context', 'owner', 'blocked', 'disposed', 'superseded'].includes(scenario);
        assert.equal(departure.revalidate(), !rejected);
        assert.equal(departure.commit(), !rejected);
        assert.equal(discards, rejected ? 0 : 2);
        assert.equal(departure.commit(), false, 'a second commit is harmless');
        assert.equal(discards, rejected ? 0 : 2);
        if (['new draft', 'changed draft'].includes(scenario)) assert.equal(confirms, scenario === 'new draft' ? 1 : 2);
      }
      navigation.dispose(); cases++;
    });
  }
  console.log(`${cases} prepared-departure contract cases passed`);
})().catch(error => { console.error(error); process.exitCode = 1; });
