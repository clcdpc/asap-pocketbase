const assert = require('node:assert/strict');
const path = require('node:path');
const { test } = require('node:test');
const { fixture } = require('./helpers/staff-controller.cjs');
const stages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed'];
const choices = ['all', 'mine_unclaimed', 'mine', 'unclaimed'];
const staff = { id: '20', tenantId: 'tenant', authenticationEmail: 'first@example.org', role: 'super_admin', organizationId: 1 };

test('Both actual queue projections retain one choice across statuses, scopes, refreshes, recovery and hostile actor replacement', async () => {
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createClaimsFilter } = await load('claims-filter');
    const { createTitleQueue, createCopyQueue } = await load('queues');
    const identity = createSessionIdentity(); identity.accept({ ...staff, defaultMineUnclaimedFilter: true });
    const claimsFilter = createClaimsFilter({ sessionIdentity: identity });
    global.requestAnimationFrame = dom.window.requestAnimationFrame.bind(dom.window);
    global.cancelAnimationFrame = dom.window.cancelAnimationFrame.bind(dom.window);
    dom.window.gridjs = require(path.resolve(__dirname, '../src/Asap.Web/Frontend/vendor/gridjs/6.2.0/gridjs.umd.js'));
    let context = { scope: '2', status: 'suggestion', additionalCopyStatus: 'open', activeView: 'queue' };
    const data = [2, 3].flatMap(libraryOrgId => [...stages, 'open', 'copy-closed'].flatMap((stage, index) =>
      ['20', '21', null].map((claimedByStaffUserId, claim) => ({ id: `${libraryOrgId}${index}${claim}`,
        title: `${libraryOrgId} ${stage} ${claim}`, bibid: 7, status: stage === 'copy-closed' ? 'closed' : stage,
        copy: stage === 'open' || stage === 'copy-closed', libraryOrgId, libraryOrgName: `Library ${libraryOrgId}`,
        claimedByStaffUserId, claimedByDisplayName: claimedByStaffUserId, workflowTags: [] }))));
    let hold = false, pending = [], review = null, acknowledgements = 0;
    const request = copy => async () => {
      const result = { scope: context.scope, status: context.additionalCopyStatus, organizations: [], availableLibraries: [],
        items: data.filter(item => item.copy === copy && String(item.libraryOrgId) === context.scope &&
          (!copy || item.status === context.additionalCopyStatus)) };
      return hold ? new Promise(resolve => pending.push({ resolve, result })) : result;
    };
    const common = { sessionIdentity: identity, claimsFilter, getContext: () => context,
      announce() {}, onOpen() {}, onScopeIntent() {}, onStatusIntent() {}, onScopeAccepted() {}, onLibraries() {}, onRefreshed() {}, onRendered() {} };
    const title = createTitleQueue({ ...common, root: get('#queue-view'), request: request(false) });
    const copy = createCopyQueue({ ...common, root: get('#additional-copy-view'), request: request(true),
      recovery: { current: () => review, begin: () => review ? {} : null, loaded: () => Boolean(review),
        acknowledge() { acknowledgements++; review.reviewed = true; return true; } } });
    const selected = expected => {
      assert.equal(get('#claim-filter').value, expected); assert.equal(get('#additional-copy-claim-filter').value, expected);
    };
    const matches = item => {
      const value = claimsFilter.value(), mine = item.claimedByStaffUserId === identity.preferences().id, unclaimed = !item.claimedByStaffUserId;
      return value === 'all' || value === 'mine' && mine || value === 'unclaimed' && unclaimed || value === 'mine_unclaimed' && (mine || unclaimed);
    };
    const rows = async copyQueue => {
      const selector = copyQueue ? '#additional-copy-grid .additional-copy-open' : '#request-grid .request-title-link';
      const expected = data.filter(item => item.copy === copyQueue && String(item.libraryOrgId) === context.scope &&
        item.status === (copyQueue ? context.additionalCopyStatus : context.status) && matches(item)).map(item => item.id).sort();
      const actual = () => [...document.querySelectorAll(selector)].map(node => node.dataset[copyQueue ? 'copyId' : 'requestId']).sort();
      for (let attempt = 0; attempt < 300; attempt++) {
        if (JSON.stringify(actual()) === JSON.stringify(expected)) { assert.deepEqual(actual(), expected); return; }
        await new Promise(resolve => setTimeout(resolve, 15));
      }
      assert.deepEqual(actual(), expected, `${copyQueue ? 'Copies' : 'Requests'} exact matching rows for ${claimsFilter.value()}`);
    };
    const change = next => {
      const previous = context; context = { ...context, ...next };
      title.contextChanged(context, previous); copy.contextChanged(context, previous);
    };
    const choose = (copyQueue, value) => {
      const input = get(copyQueue ? '#additional-copy-claim-filter' : '#claim-filter');
      input.value = value; input.dispatchEvent(new dom.window.Event('change')); selected(value);
    };
    const setStaff = () => { title.setStaff(identity.preferences()); copy.setStaff(identity.preferences()); };
    try {
      setStaff(); await title.refresh(); await copy.refresh(); selected('mine_unclaimed'); await rows(false); await rows(true);
      identity.updatePreferences({ ...identity.preferences(), defaultMineUnclaimedFilter: false }, identity.preferences());
      title.preferencesChanged(); copy.preferencesChanged(); selected('all'); await rows(false); await rows(true);
      for (const value of choices) {
        choose(false, value);
        for (const status of stages) { change({ status }); title.render(); selected(value); await rows(false); }
        for (const additionalCopyStatus of ['open', 'closed']) {
          change({ activeView: 'additional-copies', additionalCopyStatus }); copy.activate({ previous: 'queue' }); await copy.refresh();
          selected(value); await rows(true); choose(true, value);
        }
        change({ activeView: 'queue' }); title.activate({ previous: 'additional-copies' }); title.render(); await rows(false);
        assert.deepEqual([...document.querySelectorAll('#status-tabs .status-count')].map(node => node.textContent), stages.map(() => '(3)'));
      }
      choose(true, 'mine');
      change({ activeView: 'profile' }); title.deactivate(); copy.deactivate();
      identity.updatePreferences({ ...identity.preferences(), defaultMineUnclaimedFilter: true }, identity.preferences());
      title.preferencesChanged(); copy.preferencesChanged();
      change({ activeView: 'queue' }); title.activate({ previous: 'profile' }); await title.refresh(); selected('mine'); await rows(false);
      change({ scope: '3', additionalCopyStatus: 'open' }); await title.refresh(); await copy.refresh(); selected('mine'); await rows(false); await rows(true);
      review = { libraryOrgId: 3, bibid: 7, reviewReady: true, reviewed: false };
      get('#additional-copy-search').value = 'previous search';
      await copy.refresh(); selected('mine'); await rows(true);
      assert.equal(get('#additional-copy-search').value, '');
      assert.equal(get('#additional-copy-create-review').hidden, false); assert.equal(acknowledgements, 0, 'Refresh never acknowledges uncertain creation.');
      assert.match(get('#additional-copy-create-review-summary').textContent, /hidden by the current filters.*All claims/);
      get('#additional-copy-create-review-done').click(); assert.equal(acknowledgements, 1); review = null;
      const oldOwner = identity.preferences(), oldControls = [get('#claim-filter'), get('#additional-copy-claim-filter')];
      hold = true; const oldReads = [title.refresh(), copy.refresh()];
      title.signedOut(); copy.signedOut(); identity.clear();
      identity.accept({ ...staff, id: '21', authenticationEmail: 'second@example.org', defaultMineUnclaimedFilter: false });
      setStaff(); selected('all');
      for (const control of oldControls) {
        assert.equal(control.isConnected, false); control.value = 'unclaimed'; control.dispatchEvent(new dom.window.Event('change'));
      }
      assert.equal(claimsFilter.select('unclaimed', oldOwner), false);
      for (const read of pending) read.resolve(read.result);
      assert.deepEqual(await Promise.all(oldReads), [false, false]);
      assert.equal(title.find(data[0].id), undefined); assert.equal(copy.isReady(context), false);
      hold = false; pending = []; await title.refresh(); await copy.refresh(); choose(true, 'mine'); await rows(false); await rows(true);
      const beforeAuthority = [get('#claim-filter'), get('#additional-copy-claim-filter')];
      identity.accept({ ...identity.preferences(), role: 'admin', organizationId: 3 });
      title.render(); copy.render();
      assert.equal(get('#request-grid .request-title-link'), null); assert.equal(get('#additional-copy-grid .additional-copy-open'), null);
      setStaff(); selected('mine');
      for (const control of beforeAuthority) { assert.equal(control.isConnected, false); control.value = 'all'; control.dispatchEvent(new dom.window.Event('change')); }
      await title.refresh(); await copy.refresh(); selected('mine'); await rows(false); await rows(true);
    } finally { title.dispose(); copy.dispose(); }
  });
});
