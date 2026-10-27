const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const actor = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1 };
const settle = async () => { for (let index = 0; index < 6; index++) await new Promise(resolve => setImmediate(resolve)); };
const verified = barcode => ({ status: 'verified', libraryOrgId: 2, libraryOrgName: 'Servicing library', searchLibraryLimited: false,
  patron: { patron: { barcode, name: 'Current patron', patronOrganizationId: 301, homeLibraryOrganizationId: 3,
    homeLibraryOrganizationName: 'Home library' }, patronOrganizationName: 'Registered branch',
  email: 'current@example.org', libraryOrgId: 2, libraryOrgName: 'Servicing library', searchLibraryLimited: false,
  currentPreferredPickupState: 'invalid', currentPreferredPickupBranchId: 999, pickupBranches: [{ id: 101, label: 'Eligible branch' }],
  pickupBranchesRefreshedAt: '2026-10-19T15:00:00Z', pickupWarning: 'Current preference is not eligible.' } });

(async () => {
  for (const selected of [null, 101]) {
    await fixture(async ({ load }) => {
      const { createRequestWorkflows } = await load('request-workflows');
      const root = document.createElement('section'); document.body.append(root);
      const workflows = createRequestWorkflows({ context: {
        root, actor: { id: '20', role: 'staff' },
        request: { id: '9007199254740993', version: 'v1', status: 'pending_hold', capabilities: { canChangeWorkflowState: true } },
        scope: { register: () => ({}), release() {} }, isCurrent: () => true, isPending: () => false
      }, editor: {}, announce() {}, request: async () => ({ version: 'v1', selectedPickupBranchId: selected,
        currentPreferredPickupBranchId: selected, pickupBranches: [{ id: 101, label: 'Live pickup' }],
        pickupBranchWarning: selected ? null : 'Current preference is absent.' }) });
      root.append(workflows.buildActions());
      [...root.querySelectorAll('button')].find(button => button.textContent === 'Pickup').click();
      await settle();
      const select = root.querySelector('[aria-label="Preferred pickup branch"]');
      assert.equal(select.value, selected ? '101' : '');
      assert.equal(select.required, true);
      assert.equal(select.checkValidity(), Boolean(selected));
      for (const id of select.getAttribute('aria-describedby').split(' ')) assert.ok(document.getElementById(id));
      assert.match(root.querySelector('#request-pickup-help').textContent, /Every unfilled request uses the live default/);
      assert.equal(root.querySelector('#request-pickup-warning').hidden, Boolean(selected));
      workflows.dispose();
    });
  }
  for (const [scope, expected] of [['2', '2'], ['all', ''], ['system', ''], ['1', ''], ['3', '']]) {
    await fixture(async ({ load, get }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createSuggestionController } = await load('suggestion-controller');
      const identity = createSessionIdentity(); identity.accept(actor);
      let calls = 0;
      const controller = createSuggestionController({ root: get('#staff-suggestion-dialog'), trigger: get('#new-suggestion'),
        sessionIdentity: identity, polarisLookup: { close() {}, invalidate() {} }, announce() {},
        getScope: () => scope, getLibraries: () => [{ id: '2', name: 'Servicing library' }],
        beforeOpen: () => true, beforeClose: () => true, onReceipt() {}, clearReceipt() {},
        request: async () => { calls++; throw Error('Global open must await a patron query'); } });
      await controller.open();
      assert.equal(get('[aria-label="Servicing library"]').value, expected, scope);
      assert.equal(calls, 0);
      controller.dispose();
    });
  }
  for (const scope of ['2', 'all']) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createSuggestionController } = await load('suggestion-controller');
      const identity = createSessionIdentity(); identity.accept(actor);
      const calls = [];
      const target = document.createElement('button'); target.textContent = 'Current queue quick action'; document.body.append(target);
      const controller = createSuggestionController({ root: get('#staff-suggestion-dialog'), trigger: get('#new-suggestion'),
        sessionIdentity: identity, polarisLookup: { close() {}, invalidate() {} }, announce() {},
        getScope: () => scope, getLibraries: () => [{ id: 2, name: 'Servicing library' }],
        beforeOpen: () => true, beforeClose: () => true, onReceipt() {}, clearReceipt() {},
        request: async (path, options) => {
          calls.push({ path, body: options?.body });
          return path.includes('patron-lookup') ? verified('CURRENT-BARCODE') :
            { libraryOrgId: 2, configuration: { availableFormats: ['book'] } };
        } });
      await controller.open(null, { barcode: 'OLD-BARCODE', email: 'old@example.org', preferredPickupBranchId: 500,
        focusReturn: { isCurrent: () => true, target: () => target } });
      if (scope === 'all') {
        assert.equal(calls.length, 0, 'All libraries requires an explicit target before any seeded lookup');
        assert.equal(get('#staff-suggestion-body input[type=search]').value, 'OLD-BARCODE');
        get('[aria-label="Servicing library"]').value = '2';
        get('[aria-label="Servicing library"]').dispatchEvent(new dom.window.Event('change'));
      }
      await settle();
      assert.equal(calls.length, 2);
      assert.deepEqual(calls[0].body, { query: null, barcode: 'OLD-BARCODE', libraryOrgId: 2 });
      const card = get('.staff-patron-card');
      for (const text of ['CURRENT-BARCODE', 'current@example.org', 'Registered branch (301)', 'Home library (3)',
        'Servicing library (2)', 'other participating home libraries', 'Invalid preference']) assert.ok(card.textContent.includes(text), text);
      assert.ok(!card.textContent.includes('old@example.org'));
      const pickup = get('[aria-label="Preferred pickup location"]');
      assert.equal(pickup.value, '', 'An invalid live preference cannot silently preselect another branch');
      assert.equal(pickup.required, true);
      for (const id of pickup.getAttribute('aria-describedby').split(' ')) assert.ok(document.getElementById(id), id);
      assert.equal(get('#staff-suggestion-pickup-warning').textContent, 'Current preference is not eligible.');
      assert.equal(get('#staff-suggestion-pickup-warning').hidden, false);
      assert.equal(get('#staff-suggestion-pickup-refreshed time').getAttribute('datetime'), '2026-10-19T15:00:00Z');
      assert.ok(get('#staff-suggestion-pickup-help').textContent.includes('Unfilled requests use the live preference'));
      assert.equal(get('.staff-suggestion-fields input[maxlength="500"]').value, '');
      assert.equal(controller.isDirty(), false);
      assert.equal(await controller.close(), true);
      assert.equal(document.activeElement, target);
      controller.dispose();
    });
  }
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createSuggestionController } = await load('suggestion-controller');
    const identity = createSessionIdentity(); identity.accept(actor);
    const pending = new Map(), calls = [];
    const controller = createSuggestionController({ root: get('#staff-suggestion-dialog'), trigger: get('#new-suggestion'),
      sessionIdentity: identity, polarisLookup: { close() {}, invalidate() {} }, announce() {},
      getScope: () => '2', getLibraries: () => [{ id: 2, name: 'Servicing library' }],
      beforeOpen: () => true, beforeClose: () => true, onReceipt() {}, clearReceipt() {},
      request: async (path, options) => {
        calls.push(path);
        if (path.includes('patron-lookup')) return new Promise(resolve => pending.set(options.body.barcode, resolve));
        return { libraryOrgId: 2, configuration: { availableFormats: ['book'] } };
      } });
    await controller.open(null, { barcode: 'PATRON-A' });
    await controller.open(null, { barcode: 'PATRON-B' });
    pending.get('PATRON-B')(verified('PATRON-B')); await settle();
    pending.get('PATRON-A')(verified('PATRON-A')); await settle();
    assert.ok(get('.staff-patron-card').textContent.includes('PATRON-B'));
    assert.ok(!get('.staff-patron-card').textContent.includes('PATRON-A'));
    assert.equal(calls.filter(path => path.includes('suggestion-configuration')).length, 1);
    controller.dispose();
  });
  console.log('Specific queue scope, explicit All scope, authoritative barcode-only quick-create, context, focus and stale ownership passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
