const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const actorA = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 2, version: 'actor-v1' };
const actorB = { ...actorA, id: '21', authenticationEmail: 'b@example.org' };
const snapshot = { id: '9223372036854775807', version: 'v1', title: 'A', libraryOrgId: 2, status: 'suggestion',
  format: 'book', customFields: {}, capabilities: { canEditIdentifier: true, canChangeBib: true, canChangeWorkflowState: true, allowedActions: [] } };

(async () => {
  for (const operation of ['claim', 'delete', 'reconcile']) {
    await fixture(async ({ load, get }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createDetailHost } = await load('detail-host');
      const { createTitleDetailController } = await load('title-detail');
      const session = createSessionIdentity(); session.accept(actorA);
      const host = createDetailHost({ root: get('#request-dialog') });
      let generation = 0, complete; const posted = [], receipts = [], notices = [];
      const source = operation === 'delete' ? { ...snapshot, status: 'closed' }
        : operation === 'reconcile' ? { ...snapshot, status: 'pending_hold', holdOperation: { id: '71', version: 'op-v1', canReconcile: true } }
          : snapshot;
      const options = { host, sessionIdentity: session, polarisLookup: { close() {}, invalidate() {} },
        copyCreation: { close: () => true, invalidate() {}, hasPendingMutation: () => false },
        announce: message => notices.push(message), beforeOpen: () => ({ isCurrent: () => true }),
        isNavigationCurrent: ticket => ticket === generation, getNavigationGeneration: () => generation,
        getScope: () => '2', onAlign: async () => ({ scope: '2', commit: () => ++generation }), onOpened() {}, beforeClose: () => true, onClosed() {},
        getFocusReturn: () => null, refreshQueue: async () => true, queueSequence: () => 1,
        rememberOpened() {}, forgetUnavailable() {}, onReceipt: (...args) => receipts.push(args), clearReceipt() {},
        request: async (path, init = {}) => {
          if (init.method) { posted.push({ path, init }); return new Promise(resolve => { complete = resolve; }); }
          return path.includes('/title-requests/') ? source : {};
        } };
      const first = createTitleDetailController(options); await first.open(source.id);
      if (operation === 'delete') session.updatePreferences({ ...actorA, version: 'actor-v2' }, session.preferences());
      const label = operation === 'claim' ? 'Claim' : operation === 'delete' ? 'Permanently delete request' : 'Reconcile provider state';
      const control = [...get('#request-dialog').querySelectorAll('button')].find(button => button.textContent.trim() === label);
      control.click(); await new Promise(resolve => setImmediate(resolve));
      assert.ok(complete, `${operation} dispatched`);
      assert.equal(posted[0].init.signal, undefined, `${operation} cannot use read cancellation`);
      if (operation === 'delete') assert.equal(posted[0].init.body.actorVersion, 'actor-v2', 'capture current actor rowversion at dispatch after a same-actor preference revision');
      assert.equal(first.hasPendingMutation(), true);
      session.clear(); first.signedOut(); session.accept(actorB);
      const replacement = createTitleDetailController({ ...options, request: async path => path.includes('/title-requests/') ? { ...source, title: 'B' } : {} });
      await replacement.open(source.id);
      complete(operation === 'delete' ? { deleted: true } : operation === 'reconcile'
        ? { committed: true, code: 'updated', operationId: '71', finalStatus: 'pending_hold' }
        : { committed: true, request: { ...source, version: 'v2' } });
      await new Promise(resolve => setImmediate(resolve));
      assert.equal(receipts[0][2].outcome, 'committed', 'retired command records outcome before presentation checks');
      assert.equal(receipts[0][1].id, actorA.id, 'receipt stays bound to captured actor');
      assert.equal(get('#request-dialog-title').textContent, 'B');
      assert.equal(notices.some(message => /Request claimed|permanently deleted|reconciliation recorded/.test(message)), false);
      first.dispose(); replacement.dispose(); host.dispose();
    });
  }
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createDetailHost } = await load('detail-host');
    const { createTitleDetailController } = await load('title-detail');
    const session = createSessionIdentity();
    const owner = session.accept(actorA);
    const host = createDetailHost({ root: get('#request-dialog') });
    const source = { ...snapshot, bibid: 9001, status: 'pending_hold', holdOperation: {
      id: '71', version: 'operation-v1', state: 'outcome_unknown', phase: 'acquired', attemptNumber: 1,
      canResolveNotPerformed: true
    } };
    const posts = [], receipts = [], notices = [];
    const controller = createTitleDetailController({ host, sessionIdentity: session,
      polarisLookup: { close() {}, invalidate() {} },
      copyCreation: { close: () => true, invalidate() {}, hasPendingMutation: () => false },
      announce: message => notices.push(message), beforeOpen: () => ({ isCurrent: () => true }),
      isNavigationCurrent: () => true, getNavigationGeneration: () => 1, getScope: () => '2',
      onAlign: async () => ({ scope: '2', commit: () => 1 }), onOpened() {}, beforeClose: () => true, onClosed() {},
      getFocusReturn: () => null, refreshQueue: async () => true, queueSequence: () => 1,
      rememberOpened() {}, forgetUnavailable() {}, onReceipt: (...args) => receipts.push(args), clearReceipt() {},
      request: async (path, init = {}) => {
        if (init.method === 'POST') {
          posts.push({ path, init });
          throw Object.assign(new Error('HTTP 200 hold-resolution result is unknown.'), {
            status: 200, outcomeUnknown: true
          });
        }
        return path.includes('/title-requests/') ? source
          : { availableFormats: ['book'], formatLabels: { book: 'Book' }, publicationOptions: [], formatRules: {} };
      } });
    await controller.open(source.id);
    const reason = get('.resolution-form textarea[required]');
    assert.ok(reason, 'the acquired uncertain hold has a resolution form');
    reason.value = 'Operator confirms the acquired attempt was never dispatched.';
    get('.resolution-form button[type="submit"]').click();
    for (let attempt = 0; attempt < 10 && posts.length === 0; attempt++) {
      await new Promise(resolve => setImmediate(resolve));
    }
    for (let attempt = 0; attempt < 3; attempt++) await new Promise(resolve => setImmediate(resolve));
    assert.equal(posts.length, 1, 'hold resolution dispatches once and is never replayed automatically');
    assert.equal(posts[0].path, '/api/asap/staff/hold-operations/71/resolve');
    assert.equal(posts[0].init.body.outcome, 'not_performed');
    assert.equal(posts[0].init.body.version, 'operation-v1');
    assert.equal(receipts.length, 1, 'status-200 unknown resolution produces one recovery receipt');
    assert.equal(receipts[0][1].id, owner.id, 'recovery receipt remains with the dispatching staff actor');
    assert.equal(receipts[0][2].outcome, 'uncertain');
    assert.match(notices.join(' '), /hold recovery outcome could not be confirmed/i);
    controller.dispose(); host.dispose();
  });
  await fixture(async ({ load, get, dom }) => {
    const { createDraftScope } = await load('draft-scope');
    const { createRequestEditor } = await load('request-editor');
    const root = get('#request-dialog-body'), drafts = createDraftScope();
    let live = true, lookups = 0, mutations = 0;
    const context = Object.freeze({ request: snapshot, actor: actorA, root, scope: drafts,
      isCurrent: () => live, isPending: () => false,
      admit: declaration => drafts.admit(declaration).allowed,
      confirm: () => true, mutate: async () => { mutations++; } });
    const editor = createRequestEditor({ context, configuration: {}, announce() {},
      polarisLookup: { open() { lookups++; }, close() {}, invalidate() {} }, request: async () => ({}) });
    const oldForm = editor.build(); root.replaceChildren(oldForm);
    const oldTitle = oldForm.querySelector('input');
    oldTitle.value = 'Unsaved'; oldTitle.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.equal(drafts.isDirty(), true);
    [...oldForm.querySelectorAll('button')].find(button => button.textContent.includes('Revert changes')).click();
    assert.equal(drafts.isDirty(), false, 'Revert replaces only its own editor draft');
    oldTitle.value = 'Stale callback'; oldTitle.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    oldForm.dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    [...oldForm.querySelectorAll('button')].find(button => button.textContent.includes('Search Polaris')).click();
    assert.equal(drafts.isDirty(), false, 'retired editor events cannot mutate replacement dirty state');
    assert.equal(mutations, 0); assert.equal(lookups, 0);
    live = false; editor.dispose(); drafts.dispose();
  });
  for (const pendingRefresh of [false, true]) {
    await fixture(async ({ load, get }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createDetailHost } = await load('detail-host');
      const { createTitleDetailController } = await load('title-detail');
      const session = createSessionIdentity(); session.accept(actorA);
      const host = createDetailHost({ root: get('#request-dialog') });
      let generation = 0, revision = 'A', releaseConfiguration;
      const configurationReads = new Map();
      const controller = createTitleDetailController({ host, sessionIdentity: session,
        polarisLookup: { close() {}, invalidate() {} }, copyCreation: { close: () => true, invalidate() {}, hasPendingMutation: () => false },
        announce() {}, beforeOpen: () => ({ isCurrent: () => true }), isNavigationCurrent: ticket => ticket === generation,
        getNavigationGeneration: () => generation, getScope: () => 'all', onAlign: async () => ({ scope: 'all', commit: () => ++generation }), onOpened() {},
        beforeClose: () => true, onClosed() {}, getFocusReturn: () => null, refreshQueue: async () => true,
        queueSequence: () => 1, rememberOpened() {}, forgetUnavailable() {}, onReceipt() {}, clearReceipt() {},
        request: async path => {
          if (path.startsWith('/api/asap/config?')) {
            const key = new URL(path, 'https://localhost').searchParams.get('libraryOrgId');
            const count = (configurationReads.get(key) || 0) + 1; configurationReads.set(key, count);
            if (pendingRefresh && count === 1) return new Promise(resolve => { releaseConfiguration = resolve; });
            return { availableFormats: ['book'], formatLabels: { book: `Configuration ${revision}` } };
          }
          if (path.includes('/title-requests/')) return { ...snapshot, id: path.includes('/71?') ? '71' : snapshot.id,
            libraryOrgId: path.includes('/71?') ? 3 : 2 };
          return {};
        } });
      try {
        if (pendingRefresh) {
          const opening = controller.open(snapshot.id);
          await new Promise(resolve => setImmediate(resolve));
          assert.ok(releaseConfiguration);
          revision = 'B'; controller.invalidateConfiguration('system');
          releaseConfiguration({ availableFormats: ['book'], formatLabels: { book: 'Stale configuration A' } });
          await opening;
          assert.equal(configurationReads.get('2'), 2, 'invalidated in-flight configuration must be refetched');
          assert.match(get('[aria-label="Format"]').textContent, /Configuration B/);
          await controller.open(snapshot.id);
          assert.equal(configurationReads.get('2'), 2, 'stale completion cannot repopulate the cache');
        } else {
          await controller.open(snapshot.id); await controller.open('71');
          revision = 'B'; controller.invalidateConfiguration('2');
          await controller.open('71');
          assert.equal(configurationReads.get('3'), 1, 'library invalidation preserves unrelated cached configuration');
          assert.match(get('[aria-label="Format"]').textContent, /Configuration A/);
          await controller.open(snapshot.id);
          assert.equal(configurationReads.get('2'), 2);
          assert.match(get('[aria-label="Format"]').textContent, /Configuration B/);
          controller.invalidateConfiguration('system');
          await controller.open('71'); await controller.open(snapshot.id);
          assert.equal(configurationReads.get('3'), 2);
          assert.equal(configurationReads.get('2'), 3);
        }
      } finally { controller.dispose(); host.dispose(); }
    });
  }
  console.log('Title command outcomes, actor replacement, scoped configuration invalidation and editor Revert/disposal checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
