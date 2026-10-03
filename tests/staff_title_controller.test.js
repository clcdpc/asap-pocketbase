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
        announce: message => notices.push(message), beforeOpen: () => ++generation,
        isNavigationCurrent: ticket => ticket === generation, getNavigationGeneration: () => generation,
        getScope: () => '2', onAlign: async () => true, onOpened() {}, beforeClose: () => true, onClosed() {},
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
  console.log('Title command outcomes, actor replacement and editor Revert/disposal ownership checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
