const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const actor = id => ({ id, tenantId: `tenant-${id}`, authenticationEmail: `${id}@example.org`,
  organizationId: 1, role: 'super_admin', version: 'v1' });
(async () => {
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await load('operations-controller');
    const session = createSessionIdentity(); session.accept(actor('a'));
    const pending = [], receipts = [];
    const options = { root: get('#operations-view'), sessionIdentity: session, announce() {},
      onScopeChange() {}, clearReceipt() {}, onReceipt: (...value) => receipts.push(value),
      request: async (path, init = {}) => {
        if (init.method === 'POST') return new Promise(resolve => pending.push({ path, init, resolve }));
        if (path.endsWith('/organizations')) return { data: [{ id: 2, name: 'A', active: true }] };
        return { items: [] };
      } };
    const first = createOperationsController(options); first.setStaff(session.preferences()); first.activate();
    const savingA = first.run('/api/asap/staff/email-operations/test', 'A email');
    assert.equal(first.inspectDeparture().blocked, false, 'route may be left while attempt survives');
    assert.equal(pending[0].init.signal, undefined);
    first.deactivate(); first.dispose();
    session.clear(); session.accept(actor('b'));
    const second = createOperationsController(options); second.setStaff(session.preferences()); second.activate();
    assert.equal(get('#send-test-email').disabled, false, 'B cannot be blocked by A recovery');
    const savingB = second.run('/api/asap/staff/email-operations/test', 'B email');
    const bKey = `asap.staff.operation.tenant-b.b.${encodeURIComponent(session.actor().key)}`;
    const bRecord = window.sessionStorage.getItem(bKey);
    pending[0].resolve({ code: 'queued' }); await savingA;
    assert.equal(window.sessionStorage.getItem(bKey), bRecord, 'A cannot clear B captured recovery');
    assert.equal(get('#send-test-email').disabled, true, 'old actor cannot release new actor controls');
    assert.equal(receipts[0][2].outcome, 'committed');
    assert.equal(session.isCurrent(receipts[0][1]), false);
    pending[1].resolve({ code: 'queued' }); await savingB;
    assert.equal(window.sessionStorage.getItem(bKey), null);
    assert.equal(get('#send-test-email').disabled, false);
    second.dispose();
    window.sessionStorage.setItem(bKey, JSON.stringify({ operationId: 'forged', path: '/api/asap/staff/email-operations/test', scope: 'all', message: 'Invalid' }));
    const third = createOperationsController(options); third.setStaff(session.preferences());
    assert.equal(get('#send-test-email').disabled, false, 'malformed records cannot acquire a command guard');
    third.dispose();
  });
  console.log('Operations retained lifetime, captured actor cleanup and supported recovery contracts passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
