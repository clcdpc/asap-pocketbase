const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { fixture } = require('./helpers/staff-controller.cjs');

const source = fs.readFileSync(path.join(__dirname, '../src/Asap.Web/Frontend/staff/js/suggestion-controller.js'), 'utf8');
// Deliberate ownership and retired-endpoint/runtime-HTML prohibitions.
assert.doesNotMatch(source, /titleQueue|titleDetail|navigation\.align/);
assert.doesNotMatch(source, /api\/asap\/staff\/catalog-search|\.innerHTML\s*=/);

const flush = () => new Promise(resolve => setImmediate(resolve));
async function until(predicate) {
  const deadline = Date.now() + 3000;
  while (!predicate() && Date.now() < deadline) await flush();
  assert.ok(predicate(), 'Suggestion stage completed');
}

(async () => {
  for (const id of ['9007199254740993', '9223372036854775807', 9007199254740992, '01', null]) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createSuggestionController } = await load('suggestion-controller');
      const actor = { id: '20', tenantId: 'a', authenticationEmail: 'staff@example.org', role: 'staff', organizationId: 2 };
      const identity = createSessionIdentity(); identity.accept(actor);
      const calls = [], opened = [], receipts = [];
      const controller = createSuggestionController({ root: get('#staff-suggestion-dialog'), trigger: get('#new-suggestion'),
        sessionIdentity: identity, getLibraries: () => [{ id: '2', name: 'Two' }], announce() {},
        polarisLookup: { close() {}, invalidate() {} }, beforeOpen: () => true, beforeClose: () => true,
        openCreatedTitle: async intent => { opened.push(intent); }, openExistingTitle() {}, clearReceipt() {},
        onReceipt: (...args) => receipts.push(args), request: async (url, init = {}) => {
          calls.push({ url, init });
          if (url === '/api/asap/staff/patron-lookup') return { status: 'verified', libraryOrgId: 2, patron: {
            patron: { barcode: '20000000000001', name: 'Patron' }, pickupBranches: [{ id: 101, label: 'Main' }], currentPreferredPickupBranchId: 101 } };
          if (url === '/api/asap/staff/suggestion-configuration?libraryOrgId=2') return { libraryOrgId: 2, configuration: { availableFormats: ['book'] } };
          assert.equal(url, '/api/asap/staff/suggestions');
          return { id, libraryOrgId: 2, notificationStatus: 'not_requested' };
        } });
      try {
        await controller.open(null, { barcode: '20000000000001' });
        await until(() => get('.staff-suggestion-fields input'));
        assert.equal(calls[0].url, '/api/asap/staff/patron-lookup');
        assert.equal(calls[0].init.method, 'POST');
        assert.deepEqual(calls[0].init.body, { query: null, barcode: '20000000000001', libraryOrgId: 2 });
        assert.ok(calls[0].init.signal, 'Lookup remains cancellable');
        assert.equal(calls[1].url, '/api/asap/staff/suggestion-configuration?libraryOrgId=2');
        assert.equal(calls[1].init.method, undefined);
        get('.staff-suggestion-fields input').value = 'Independent title';
        get('#staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
        await until(() => receipts.length === 1);
        const mutation = calls[2];
        assert.equal(calls.length, 3);
        assert.equal(mutation.url, '/api/asap/staff/suggestions');
        assert.equal(mutation.init.method, 'POST');
        assert.equal(mutation.init.signal, undefined, 'A dispatched write outlives disposable reads');
        assert.equal(mutation.init.body.libraryOrgId, 2);
        assert.equal(mutation.init.body.barcode, '20000000000001');
        assert.equal(mutation.init.body.title, 'Independent title');
        assert.equal(mutation.init.body.verifiedBibId, null, 'Unverified text cannot become a verified BIB');
        assert.equal(receipts[0][1].id, '20');
        if (typeof id === 'string' && id !== '01') {
          assert.equal(receipts[0][2].outcome, 'committed');
          assert.equal(opened.length, 1);
          assert.equal(opened[0].id, id, 'Created request IDs retain every digit');
          assert.equal(opened[0].libraryOrgId, '2');
        } else {
          assert.equal(receipts[0][2].outcome, 'uncertain');
          assert.deepEqual(opened, [], 'Malformed acknowledgements cannot navigate to an invented request');
          assert.match(get('#staff-suggestion-status').textContent, /unconfirmed/);
        }
      } finally { controller.dispose(); }
    });
  }
  console.log('Suggestion request methods, scope, body, mutation ownership and exact acknowledgement identities passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
