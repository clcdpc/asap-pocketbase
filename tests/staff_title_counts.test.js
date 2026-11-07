const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const stages = ['suggestion', 'outstanding_purchase', 'pending_hold', 'hold_placed', 'closed'];
const labels = ['Suggestions', 'Purchases', 'Pending holds', 'Placed holds', 'Closed'];
const staff = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1 };
const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { resolve, promise }; };
const records = totals => totals.flatMap((total, index) => Array.from({ length: total }, (_, row) =>
  ({ id: `${index}-${row}`, title: `Request ${index}-${row}`, status: stages[index], workflowTags: [] })));

(async () => {
  await fixture(async ({ load, get, dom }) => {
    const { createTitleQueue } = await load('queues');
    const { createSessionIdentity } = await load('session-identity');
    const { createClaimsFilter } = await load('claims-filter');
    const identity = createSessionIdentity(); identity.accept(staff);
    let context = { activeView: 'queue', scope: '2', status: 'suggestion' };
    const calls = [], notices = [];
    dom.window.gridjs = { h() {}, Grid: class {
      constructor(options) {
        let state = { sort: null };
        this.config = { ...options, pipeline: { process: async () => null },
          store: { getState: () => state, dispatch: update => { state = update(state); } } };
      }
      render() {} destroy() {}
    } };
    const options = { root: get('#queue-view'), sessionIdentity: identity, claimsFilter: createClaimsFilter({ sessionIdentity: identity }), getContext: () => context,
      announce: message => notices.push(message), onOpen() {}, onScopeIntent() {}, onStatusIntent() {},
      onScopeAccepted: scope => changeScope(scope), onLibraries() {}, onRefreshed() {}, onRendered() {},
      request: (url, init) => { const pending = deferred(); calls.push({ url, init, ...pending }); return pending.promise; } };
    let queue = createTitleQueue(options);
    const changeScope = scope => { const previous = context; context = { ...context, scope }; queue.contextChanged(context, previous); };
    const tabs = () => [...get('#status-tabs').querySelectorAll('[data-status]')];
    const counts = expected => {
      assert.deepEqual(tabs().map(tab => tab.getAttribute('aria-label')), labels.map((label, i) => `${label} (${expected[i]})`));
      assert.deepEqual(tabs().map(tab => tab.querySelector('.status-count').textContent), expected.map(total => `(${total})`));
      assert.equal(get('#status-count-state').textContent, '');
    };
    const unavailable = (loading = false) => {
      assert.deepEqual(tabs().map(tab => tab.getAttribute('aria-label')),
        labels.map(label => `${label} (count ${loading ? 'loading' : 'unavailable'})`));
      assert.deepEqual(tabs().map(tab => tab.querySelector('.status-count').textContent), stages.map(() => loading ? '(…)' : '(—)'));
      assert.match(get('#status-count-state').textContent, loading ? /Counts loading/ : /Counts unavailable/);
    };
    const reply = (totals, scope = context.scope, call = calls.at(-1)) => call.resolve({ scope, items: records(totals), organizations: [] });
    try {
      queue.setStaff(identity.preferences()); unavailable();
      const initial = queue.refresh(); unavailable(true);
      reply([31, 2, 1, 1, 0]); assert.equal(await initial, true); counts([31, 2, 1, 1, 0]);
      assert.equal(tabs()[4].querySelector('.status-count').classList.contains('quiet'), true);
      assert.equal(tabs()[0].querySelector('.status-count').classList.contains('quiet'), false);
      assert.match(get('#status-count-help').textContent, /all requests in the selected library scope/);

      get('#refresh-queue').click(); unavailable(true);
      calls.at(-1).resolve(Promise.reject(new Error('Fixture unavailable')));
      await new Promise(resolve => setImmediate(resolve)); unavailable();
      assert.ok(notices.includes('Fixture unavailable'));
      const retry = queue.refresh(); unavailable(true); reply([0, 0, 0, 0, 0]);
      assert.equal(await retry, true); counts([0, 0, 0, 0, 0]);

      for (const result of [null, {}, { scope: '2' }, { scope: '2', items: null },
        { scope: '2', items: [{ status: 'unknown' }] }, { scope: '2', items: [null] }, { scope: 'system', items: [] }]) {
        const failed = queue.refresh({ silent: true }); unavailable(true); calls.at(-1).resolve(result);
        assert.equal(await failed, false); unavailable();
      }
      const cancelled = queue.refresh(); const cancelledCall = calls.at(-1);
      queue.deactivate(); unavailable(); assert.equal(cancelledCall.init.signal.aborted, true);
      cancelledCall.resolve({ scope: '2', items: [] }); assert.equal(await cancelled, false); unavailable();
      const entry = queue.refreshOnEntry(); reply([3, 0, 0, 0, 0]); assert.equal(await entry, true); counts([3, 0, 0, 0, 0]);

      const obsolete = queue.refresh(); const oldCall = calls.at(-1);
      changeScope('3'); unavailable();
      const selected = queue.refresh(); unavailable(true); reply([0, 4, 0, 0, 2]);
      assert.equal(await selected, true); counts([0, 4, 0, 0, 2]);
      reply([99, 99, 99, 99, 99], '2', oldCall); assert.equal(await obsolete, false); counts([0, 4, 0, 0, 2]);
      queue.markStale('2'); counts([0, 4, 0, 0, 2]);
      queue.markStale('3'); unavailable();
      const all = (changeScope('all'), queue.refresh()); reply([31, 6, 1, 1, 2]);
      assert.equal(await all, true); counts([31, 6, 1, 1, 2]);
      changeScope('2');
      const normalized = queue.refresh(); reply([31, 6, 1, 1, 2], 'all');
      assert.equal(await normalized, true); assert.equal(context.scope, 'all'); counts([31, 6, 1, 1, 2]);

      const preparation = queue.prepare({ ...context, scope: '2' });
      counts([31, 6, 1, 1, 2]); reply([7, 0, 0, 0, 0], '2');
      const prepared = await preparation; assert.ok(prepared.isCurrent()); counts([31, 6, 1, 1, 2]);
      // Navigation validates a prepared target, then invalidates source reads,
      // aligns the committed context and accepts the already reviewed target.
      queue.invalidate(); changeScope('2'); unavailable(); prepared.accept(); counts([7, 0, 0, 0, 0]);
      const malformed = queue.prepare({ ...context }); calls.at(-1).resolve({ scope: '2' });
      await assert.rejects(malformed, /Requests could not be loaded/); counts([7, 0, 0, 0, 0]);

      const actorRead = queue.refresh(); const actorCall = calls.at(-1);
      const replacement = { ...staff, id: '21', role: 'staff', organizationId: 3 };
      identity.accept(replacement); queue.setStaff(identity.preferences()); unavailable();
      prepared.accept(); unavailable();
      reply([90, 0, 0, 0, 0], '2', actorCall); assert.equal(await actorRead, false); unavailable();
      const foreign = queue.refresh(); reply([0, 0, 0, 0, 0], 'all');
      assert.equal(await foreign, false); unavailable();
      const own = queue.refresh(); assert.match(calls.at(-1).url, /scope=3$/);
      reply([0, 0, 5, 0, 0], '3'); assert.equal(await own, true); counts([0, 0, 5, 0, 0]);
      assert.equal(context.scope, '3', 'The server-resolved library is the accepted context.');
      const signoutRead = queue.refresh(); const signoutCall = calls.at(-1);
      queue.signedOut(); identity.clear(); unavailable();
      reply([80, 0, 0, 0, 0], '3', signoutCall); assert.equal(await signoutRead, false); unavailable();
      identity.accept(replacement); queue.setStaff(identity.preferences()); unavailable();
      const reentry = queue.refreshOnEntry(); reply([0, 0, 0, 0, 0]); assert.equal(await reentry, true); counts([0, 0, 0, 0, 0]);

      queue.dispose(); queue = createTitleQueue(options); queue.setStaff(identity.preferences()); unavailable();
      assert.ok(tabs().every(tab => tab.querySelectorAll('.status-count').length === 1), 'Owner replacement keeps one label/count.');
    } finally { queue.dispose(); }
  });
  console.log('Title tab count loading/failure/retry, malformed results, cancellation, prepared targets, scope and identity ownership passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
