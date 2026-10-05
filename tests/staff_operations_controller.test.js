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
        if (path.endsWith('/organizations')) return { data: [{ id: 2, name: 'A', isActive: true }] };
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
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await load('operations-controller');
    const session = createSessionIdentity(); session.accept(actor('a'));
    const reads = [], notices = [], receipts = [];
    const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: session,
      announce: message => notices.push(message), onScopeChange() {}, clearReceipt() {}, onReceipt: (...args) => receipts.push(args),
      request: async (path, init = {}) => init.method === 'POST' ? { code: 'queued' }
        : new Promise(resolve => reads.push(resolve)) });
    controller.setStaff(session.preferences()); controller.activate();
    const running = controller.run('/api/asap/staff/email-operations/test', 'A email');
    await new Promise(resolve => setImmediate(resolve));
    assert.ok(reads.length, 'committed attempt begins its disposable review reads');
    session.clear(); controller.signedOut(); session.accept(actor('b')); controller.setStaff(session.preferences()); controller.activate();
    const before = notices.length;
    for (const resolve of reads) resolve({ items: [] }); await running;
    assert.equal(receipts[0][2].outcome, 'committed');
    assert.equal(notices.length, before, 'retired post-commit review cannot announce over a replacement actor');
    controller.dispose();
  });
  for (const scenario of ['inactive', 'active', 'exact review unavailable', 'preferences', 'replacement', 'email body']) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createOperationsController } = await load('operations-controller');
      const identity = createSessionIdentity(); identity.accept(actor('a'));
      const paths = [], posts = [], notices = [];
      let uncertain = true, retired = false;
      const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
        announce: message => notices.push(message), onScopeChange() {}, onReceipt() {}, clearReceipt() {},
        request: async (path, init = {}) => {
          if (init.method === 'POST') {
            posts.push({ path, init });
            if (uncertain) throw new Error('Response lost');
            return { code: 'queued' };
          }
          paths.push(path);
          if (path.endsWith('/organizations')) return { data: [{ id: 3, name: 'C', isActive: !retired || scenario !== 'inactive' }] };
          if (retired && scenario === 'exact review unavailable' && path.endsWith('organizationId=3')) throw new Error('Exact review unavailable');
          return { items: [] };
        } });
      controller.setStaff(identity.preferences()); controller.activate();
      controller.setLibraries([{ id: 3, name: 'C' }]);
      get('#operations-scope').value = '3'; get('#operations-scope').dispatchEvent(new dom.window.Event('change'));
      await new Promise(resolve => setImmediate(resolve));
      await controller.run(scenario === 'email body' ? '/api/asap/staff/email-operations/71/retry'
        : '/api/asap/staff/workflow/weekly-summary/run-now?force=true', 'Retained operation', null,
        scenario === 'email body' ? { version: 'email-v1' } : undefined);
      const key = `asap.staff.operation.tenant-a.a.${encodeURIComponent(identity.actor().key)}`;
      const captured = JSON.parse(window.sessionStorage.getItem(key));
      assert.equal(captured.scope, '3'); assert.equal(posts[0].init.signal, undefined);
      retired = true; controller.retireCatalog(); assert.equal(controller.currentScope(), 'all');
      paths.length = 0;
      await controller.refresh();
      const retry = [...get('#operations-outcome').querySelectorAll('button')].find(button => button.textContent.includes('Retry same'));
      const retryable = !['inactive', 'exact review unavailable'].includes(scenario);
      assert.equal(Boolean(retry), retryable, `${scenario}: broad presentation cannot provide exact command evidence`);
      assert.equal(JSON.parse(window.sessionStorage.getItem(key)).scope, '3');
      if (scenario === 'inactive') assert.equal(paths.some(path => path.includes('organizationId=3')), false);
      else assert.equal(paths.filter(path => path.includes('organizationId=3')).length, 2, 'exact authority must be reviewed separately');
      if (!retry) { controller.dispose(); return; }
      if (scenario === 'replacement') {
        identity.clear(); identity.accept(actor('b')); controller.setStaff(identity.preferences());
        retry.click(); await new Promise(resolve => setImmediate(resolve));
        assert.equal(posts.length, 1, 'old evidence cannot authorize another actor');
      } else {
        if (scenario === 'preferences') {
          const preferences = identity.updatePreferences({ ...actor('a'), version: 'v2', displayName: 'New preference' }, identity.preferences());
          controller.setStaff(preferences);
        }
        uncertain = false; retry.click(); await new Promise(resolve => setImmediate(resolve));
        assert.equal(posts.length, 2);
        assert.equal(posts[1].path, posts[0].path, 'exact scope/path/operationId survive projection and preference replacement');
        assert.deepEqual(posts[1].init.body, posts[0].init.body);
      }
      controller.dispose();
    });
  }
  console.log('Operations retained lifetime, captured actor cleanup and six exact-authority recovery cases passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
