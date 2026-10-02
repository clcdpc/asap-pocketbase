const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const actorA = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1, version: 'actor-v1' };
const actorB = { ...actorA, id: '21', authenticationEmail: 'b@example.org' };
const flush = () => new Promise(resolve => setImmediate(resolve));

(async () => {
  for (const outcome of ['deleted', 'outcome_unconfirmed']) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createBulkDeleteController } = await load('bulk-delete');
      const session = createSessionIdentity(); session.accept(actorA);
      const title = { id: '9223372036854775807', type: 'title_request', version: 'title-v1',
        status: 'closed', title: 'Title', libraryOrgId: 2, libraryOrgName: 'A' };
      const copy = { ...title, id: '9007199254740993', type: 'additional_copy', version: 'copy-v1' };
      let complete, reject; const receipts = [], deletes = [], reviews = [];
      const options = { root: get('#bulk-delete-dialog'), titleTrigger: get('#bulk-delete-closed'), copyTrigger: get('#bulk-delete-closed-copies'),
        sessionIdentity: session, getLibraries: () => [{ id: 2, name: 'A' }], beforeOpen: () => true, beforeExecute: () => true,
        captureReview: () => ({}), reviewClosed: async intent => { reviews.push(intent); return null; },
        onReceipt: (...args) => receipts.push(args), clearReceipt() {}, onSessionLost() {}, onAccessUnavailable() {},
        sessionRequest: async () => ({ authenticated: true, accessAllowed: true, staff: actorA }),
        request: async (path, init = {}) => {
          if (init.method === 'DELETE') { deletes.push({ path, init }); return new Promise((resolve, fail) => { complete = resolve; reject = fail; }); }
          return path.includes('additional-copies') ? { scope: '2', status: 'closed', items: [copy] } : { scope: '2', items: [title] };
        } };
      const first = createBulkDeleteController(options); first.open(get('#bulk-delete-closed'));
      get('#bulk-delete-scope').value = '2'; get('#bulk-delete-preview').click(); await flush(); await flush();
      assert.equal(get('#bulk-delete-items').children.length, 2);
      get('#bulk-delete-confirmation').value = 'DELETE';
      get('#bulk-delete-confirmation').dispatchEvent(new dom.window.Event('input'));
      get('#bulk-delete-execute').click(); await flush();
      assert.equal(deletes[0].path, '/api/asap/staff/requests/9223372036854775807');
      assert.equal(deletes[0].init.signal, undefined); assert.deepEqual(deletes[0].init.body, { version: 'title-v1', actorVersion: 'actor-v1' });
      assert.equal(first.close(), false); assert.equal(first.hasPendingMutation(), true);
      title.version = 'changed'; copy.id = 'changed'; actorA.version = 'actor-v2';
      first.signedOut(); session.clear();
      assert.match(receipts.at(-1)[0], /outcome unconfirmed.*not attempted/);
      first.dispose(); session.accept(actorB);
      const replacement = createBulkDeleteController({ ...options, sessionRequest: async () => ({ authenticated: true, accessAllowed: true, staff: actorB }) });
      replacement.open(); first.dispose(); assert.equal(get('#bulk-delete-dialog').open, true);
      if (outcome === 'deleted') complete({ deleted: true }); else reject(Object.assign(new Error('Lost response'), { status: 0 }));
      await flush(); await flush();
      assert.equal(deletes.length, 1, 'interruption never dispatches the later captured item');
      assert.equal(first.hasPendingMutation(), false);
      const receipt = receipts.at(-1);
      assert.equal(receipt[1].id, actorA.id); assert.match(receipt[0], new RegExp(outcome.replaceAll('_', ' ')));
      assert.match(receipt[0], /9007199254740993.*not attempted/); assert.equal(reviews.length, 0);
      assert.equal(get('#bulk-delete-results').children.length, 0, 'old ledger cannot repaint replacement UI');
      assert.equal(replacement.hasPendingMutation(), false); replacement.dispose();
      actorA.version = 'actor-v1';
    });
  }
  console.log('Bulk frozen identity, uncancellable sequential runner, interrupted ledger and recreate checks passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
