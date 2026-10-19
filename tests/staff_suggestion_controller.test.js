const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const actorA = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1 };
const actorB = { ...actorA, id: '21', authenticationEmail: 'b@example.org' };
const flush = () => new Promise(resolve => setImmediate(resolve));

(async () => {
  for (const outcome of ['committed', 'uncertain', 'accepted_unknown', 'pickup_changed']) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createSuggestionController } = await load('suggestion-controller');
      const session = createSessionIdentity(); session.accept(actorA);
      let complete, reject; const receipts = [], posted = [], intents = [];
      const root = get('#staff-suggestion-dialog');
      const options = { root, trigger: get('#new-suggestion'), sessionIdentity: session,
        polarisLookup: { close() {}, invalidate() {} }, announce() {}, getLibraries: () => [{ id: '2', name: 'A' }],
        beforeOpen: () => true, beforeClose: () => true, openCreatedTitle: async intent => { intents.push(intent); return null; },
        openExistingTitle: async intent => { intents.push(intent); }, onReceipt: (...args) => receipts.push(args), clearReceipt() {},
        request: async (path, init = {}) => {
          if (path.endsWith('/patron-lookup')) return { status: 'verified', libraryOrgId: 2, patron: {
            patron: { barcode: '20000000000001', name: 'Patron' }, pickupBranches: [{ id: 101, label: 'Main' }], currentPreferredPickupBranchId: 101 } };
          if (path.includes('/suggestion-configuration')) return { libraryOrgId: 2, configuration: { availableFormats: ['book'] } };
          posted.push(init); return new Promise((resolve, fail) => { complete = resolve; reject = fail; });
        } };
      const first = createSuggestionController(options); first.open();
      const scope = root.querySelector('select'); scope.value = '2'; scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      root.querySelector('input[type="search"]').value = 'Patron';
      const form = get('#staff-suggestion-form'); form.dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
      await flush(); await flush();
      const title = root.querySelector('.staff-suggestion-fields input');
      assert.equal(first.isDirty(), false, 'verified patron establishes a clean configured form baseline');
      title.value = 'Draft'; title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      assert.equal(first.isDirty(), true);
      title.value = ''; title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      assert.equal(first.isDirty(), false, 'returning to configured baseline releases creation draft');
      title.value = 'Submitted title';
      title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      form.dispatchEvent(new dom.window.Event('submit', { cancelable: true })); await flush();
      assert.ok(complete, `creation dispatch: ${[...form.querySelectorAll(':invalid')].map(node => `${node.tagName}:${node.getAttribute('aria-label') || node.type}`).join(', ')}; ${get('#staff-suggestion-status').textContent}`); assert.equal(posted[0].signal, undefined);
      assert.equal(posted[0].body.libraryOrgId, 2); assert.equal(first.inspectDeparture().blocked, true);
      session.clear(); first.dispose(); session.accept(actorB);
      const replacement = createSuggestionController(options); replacement.open();
      first.dispose();
      assert.equal(root.open, true, 'repeated retired disposal cannot close a recreated dialog');
      if (outcome === 'committed') complete({ id: '9223372036854775807', libraryOrgId: 2, notificationStatus: 'queued' });
      else reject(Object.assign(new Error(outcome === 'pickup_changed' ? 'Pickup changed; request not created.' : 'Response lost'),
        outcome === 'pickup_changed' ? { status: 401, response: { code: 'request_not_created_pickup_changed', pickupPreferenceChanged: true } }
          : outcome === 'accepted_unknown' ? { status: 200, outcomeUnknown: true } : { status: 0 }));
      await flush();
      assert.equal(receipts.length, 1, `${outcome}: the captured actor receives one terminal receipt before presentation checks`);
      assert.equal(receipts[0][2].outcome, outcome === 'accepted_unknown' ? 'uncertain' : outcome,
        'record outcome before retired UI checks');
      assert.equal(receipts[0][1].id, actorA.id);
      if (outcome === 'committed') assert.equal(receipts[0][2].body.libraryOrgId, 2,
        'the committed stale receipt retains its captured numeric serving library');
      assert.equal(intents.length, 0, 'old actor cannot navigate the replacement');
      assert.equal(root.open, true); assert.equal(root.querySelector('input[type="search"]').value, '');
      assert.equal(replacement.inspectDeparture().blocked, false);
      replacement.dispose();
    });
  }

  for (const acknowledgement of [
    { name: 'missing-library', make: id => ({ id, notificationStatus: 'queued' }) },
    { name: 'wrong-library', make: id => ({ id, libraryOrgId: 3, notificationStatus: 'queued' }) },
    { name: 'string-library', make: id => ({ id, libraryOrgId: '2', notificationStatus: 'queued' }) },
    { name: 'nonnumeric-library', make: id => ({ id, libraryOrgId: 'not-a-library', notificationStatus: 'queued' }) }
  ]) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createSuggestionController } = await load('suggestion-controller');
      const session = createSessionIdentity(); session.accept(actorA);
      let complete;
      const receipts = [], posted = [], intents = [];
      const root = get('#staff-suggestion-dialog');
      const controller = createSuggestionController({
        root,
        trigger: get('#new-suggestion'),
        sessionIdentity: session,
        polarisLookup: { close() {}, invalidate() {} },
        announce() {},
        getLibraries: () => [{ id: '2', name: 'A' }, { id: '3', name: 'B' }],
        beforeOpen: () => true,
        beforeClose: () => true,
        openCreatedTitle: async intent => { intents.push(intent); return null; },
        openExistingTitle: async intent => { intents.push(intent); },
        onReceipt: (...args) => receipts.push(args),
        clearReceipt() {},
        request: async (path, init = {}) => {
          if (path.endsWith('/patron-lookup')) return { status: 'verified', libraryOrgId: 2, patron: {
            patron: { barcode: '20000000000001', name: 'Patron' },
            pickupBranches: [{ id: 101, label: 'Main' }], currentPreferredPickupBranchId: 101
          } };
          if (path.includes('/suggestion-configuration')) {
            return { libraryOrgId: 2, configuration: { availableFormats: ['book'] } };
          }
          posted.push(init);
          return new Promise(resolve => { complete = resolve; });
        }
      });

      controller.open();
      const scope = root.querySelector('select');
      scope.value = '2';
      scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      root.querySelector('input[type="search"]').value = 'Patron';
      const form = get('#staff-suggestion-form');
      form.dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
      await flush(); await flush();
      const title = root.querySelector('.staff-suggestion-fields input');
      title.value = 'Title for library two';
      title.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      form.dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
      await flush();
      assert.equal(posted.length, 1, `${acknowledgement.name}: one creation request was dispatched`);
      assert.equal(posted[0].body.libraryOrgId, 2, `${acknowledgement.name}: captured scope is the native selected ID`);

      complete(acknowledgement.make('9223372036854775807'));
      await flush();
      assert.equal(receipts.length, 1, `${acknowledgement.name}: preserve one terminal owner receipt`);
      assert.equal(receipts[0][1].id, actorA.id);
      assert.equal(receipts[0][2].outcome, 'uncertain',
        `${acknowledgement.name}: missing or mismatched serving scope cannot confirm creation`);
      assert.equal(receipts[0][2].body.libraryOrgId, 2);
      assert.equal(intents.length, 0, `${acknowledgement.name}: never open details using unconfirmed scope evidence`);
      assert.equal(posted.length, 1, `${acknowledgement.name}: do not blindly replay the uncertain creation`);
      assert.equal(controller.inspectDeparture().blocked, true,
        `${acknowledgement.name}: retain the uncertainty guard for authoritative review`);
      controller.dispose();
    });
  }
  console.log('Suggestion captured scope, uncancellable create, retired outcomes and actor-safe navigation checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
