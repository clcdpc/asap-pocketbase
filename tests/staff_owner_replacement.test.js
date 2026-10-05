const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');

(async () => {
  for (const copy of [false, true]) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createDetailHost } = await load('detail-host');
      const module = await load(copy ? 'copy-detail' : 'title-detail');
      const identity = createSessionIdentity(); identity.accept({ id: '20', tenantId: 'a', role: 'super_admin', organizationId: 1 });
      const frame = createDetailHost({ root: get('#request-dialog') });
      let retired = 0, generation = 0, rejectTarget, finishTarget, finishCandidates;
      const host = { ...frame, acquire(owner) {
        return frame.acquire({ ...owner, dispose(options) { retired++; owner.dispose(options); } });
      } };
      const request = id => ({ id, title: `Request ${id}`, status: copy ? 'open' : 'suggestion', version: 'v1',
        libraryOrgId: 2, format: 'book', customFields: {}, capabilities: { canAssign: true, canEditIdentifier: true, canChangeBib: true, allowedActions: [] } });
      const options = { host, sessionIdentity: identity, announce() {},
        beforeOpen: () => ({ isCurrent: () => true }), onAlign: async () => ({ scope: 'all', commit: () => ++generation }),
        isNavigationCurrent: ticket => ticket === generation, getNavigationGeneration: () => generation,
        getScope: () => 'all', onOpened() {}, onClosed() {}, beforeClose: () => true, getFocusReturn: () => null,
        refreshQueue: async () => true, queueSequence: () => 1, rememberOpened() {}, forgetUnavailable() {}, onReceipt() {}, clearReceipt() {},
        polarisLookup: { invalidate() {}, close() {} }, copyCreation: { invalidate() {}, close: () => true, hasPendingMutation: () => false },
        request: async path => {
          if (path.includes('/assignment-candidates?')) return new Promise(resolve => { finishCandidates = resolve; });
          if (/\/(title-requests|additional-copies)\/92/.test(path)) return new Promise((resolve, reject) => { finishTarget = resolve; rejectTarget = reject; });
          if (/\/(title-requests|additional-copies)\/91/.test(path)) return request('91');
          return {};
        } };
      const controller = (copy ? module.createCopyDetailController : module.createTitleDetailController)(options);
      await controller.open('91');
      const source = get('#request-dialog-body').firstElementChild;
      const sourceField = copy ? null : source.querySelector('.edit-form input'), baseline = sourceField?.value;
      if (sourceField) { sourceField.value = 'Exact source draft'; sourceField.dispatchEvent(new dom.window.Event('input', { bubbles: true })); }
      const failing = controller.open('92'); await new Promise(resolve => setImmediate(resolve));
      assert.equal(get('#request-dialog-body').firstElementChild, source, 'the old lease stays mounted throughout target loading');
      assert.equal(retired, 0);
      rejectTarget(Object.assign(new Error('Target unavailable'), { status: 500 }));
      assert.equal(await failing, false);
      assert.equal(get('#request-dialog-body').firstElementChild, source); assert.equal(retired, 0);
      if (sourceField) { assert.equal(sourceField.value, 'Exact source draft'); assert.equal(controller.isDirty(), true); }
      if (sourceField) { sourceField.value = baseline; sourceField.dispatchEvent(new dom.window.Event('input', { bubbles: true })); }
      [...source.querySelectorAll('button')].find(button => button.textContent.trim() === 'Assign').click();
      await new Promise(resolve => setImmediate(resolve)); assert.ok(finishCandidates);
      const succeeding = controller.open('92'); await new Promise(resolve => setImmediate(resolve));
      finishTarget(request('92')); assert.equal(await succeeding, true);
      assert.equal(retired, 1, 'the successful transfer retires the old lease exactly once');
      const replacement = get('#request-dialog-body').firstElementChild;
      assert.notEqual(replacement, source); assert.equal(source.isConnected, false);
      get('#close-request').focus();
      finishCandidates({ candidates: [{ id: '21', displayName: 'Late candidate' }] });
      await new Promise(resolve => setImmediate(resolve));
      assert.equal(replacement.querySelector('.inline-form'), null, 'late reads cannot register a form on the replacement');
      assert.equal(document.activeElement, get('#close-request'), 'late reads cannot focus over the replacement');
      if (sourceField) sourceField.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      assert.equal(controller.isDirty(), false, 'old source callbacks cannot touch replacement drafts');
      controller.dispose(); frame.dispose();
    });
  }
  console.log('Title and Copy transactional replacement, failed targets, exact retirement and late read/event/focus contracts passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
