const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const staff = { id: '20', tenantId: 'diagnostic-tenant', authenticationEmail: 'admin@example.org',
  role: 'super_admin', organizationId: 1, version: 'v1' };
const response = (status, body) => ({ ok: status < 400, status, statusText: 'Test response', json: async () => body });
const flush = () => new Promise(resolve => setImmediate(resolve));
async function until(predicate) {
  for (let count = 0; count < 60 && !predicate(); count++) await flush();
  assert.ok(predicate(), 'Expected diagnostic controller state');
}

async function diagnosticFixture(journey) {
  await fixture(async ui => {
    const { createSettingsController } = await ui.load('settings');
    let diagnostic = async () => response(200, { data: { connected: true, organizationCount: 2 } });
    let settingsReads = 0, diagnosticCalls = 0;
    const receipts = [], commits = [], notices = [], requests = [];
    global.fetch = async (path, init = {}) => {
      requests.push({ path, init });
      if (path.endsWith('/session')) return response(200, { authenticated: true, accessAllowed: true, staff, antiforgeryToken: 'test' });
      if (path === '/api/asap/staff/polaris/test') { diagnosticCalls++; return diagnostic(init); }
      if (path.includes('/settings?')) {
        settingsReads++;
        return response(200, { orgId: 'system', version: 'settings-v1',
          stored: { configuredSystem: { patron: { loginNote: 'Saved' } }, systemSettings: {}, polaris: {} },
          effective: {}, ui_text: { loginNote: 'Saved' }, emails: {}, workflow: {} });
      }
      if (path.endsWith('/organizations')) return response(200, { data: [{ id: 2, name: 'Library', active: true }] });
      if (path.includes('/patron-codes')) return response(200, { data: [] });
      throw new Error(`Unexpected diagnostic fixture path: ${path}`);
    };
    const controller = createSettingsController({ root: ui.get('#settings-view'), tab: ui.get('#settings-view-tab'),
      announce: message => notices.push(message), onCommitted: (...args) => commits.push(args), onUnconfirmed: (...args) => receipts.push(args) });
    try {
      controller.bind(); controller.setStaff(staff); await controller.activate('polaris');
      await journey({ ...ui, controller, receipts, commits, notices, requests,
        setDiagnostic: fn => { diagnostic = fn; }, calls: () => diagnosticCalls, reads: () => settingsReads });
    } finally { controller.dispose(); }
  });
}

(async () => {
  for (const outcome of ['connected', 'unavailable', 'transport']) {
    await diagnosticFixture(async ({ get, controller, setDiagnostic, receipts, commits, calls, requests }) => {
      setDiagnostic(async init => {
        assert.equal(init.method, 'POST');
        if (outcome === 'transport') throw new TypeError('Network offline');
        return outcome === 'unavailable' ? response(502, { code: 'polaris_unavailable', data: { connected: false } })
          : response(200, { data: { connected: true, organizationCount: 2 } });
      });
      get('#btn-test-polaris').click();
      await until(() => calls() === 1 && !get('#btn-test-polaris').disabled);
      assert.match(get('#polaris-test-result').textContent, outcome === 'connected' ? /Connected.*2 organizations/
        : outcome === 'unavailable' ? /Polaris is unavailable/ : /test could not be completed/);
      assert.equal(controller.hasPendingMutation(), false); assert.equal(controller.hasUnconfirmedOutcome(), false);
      assert.equal(controller.inspectDeparture().blocked, false); assert.equal(controller.inspectDeparture().dirty, false);
      assert.equal(get('#settings-save-title').textContent, 'No changes');
      assert.equal(commits.length, 0); assert.equal(receipts.length, 0);
      assert.equal(controller.suspend(), true, 'diagnostic failure does not guard departure');
      const priorReads = requests.length;
      await controller.activate('polaris');
      assert.equal(requests.length, priorReads, 'diagnostic did not set awaitingReload');
    });
  }
  await diagnosticFixture(async ({ get, dom, controller, setDiagnostic, calls, reads }) => {
    const note = get('#patron-login-note'); note.value = 'Unsaved';
    note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    window.confirm = () => false;
    get('#btn-test-polaris').click(); await flush();
    assert.equal(calls(), 0); assert.equal(reads(), 1); assert.equal(note.value, 'Unsaved');
    window.confirm = () => true;
    let complete, signal;
    setDiagnostic(init => { signal = init.signal; return new Promise(resolve => { complete = resolve; }); });
    get('#btn-test-polaris').click();
    await until(() => complete);
    assert.ok(signal, 'diagnostic has a cancellable read signal');
    assert.equal(reads(), 2); assert.equal(note.value, 'Saved', 'consent reloads saved configuration before testing');
    get('#btn-test-polaris').click(); await flush(); assert.equal(calls(), 1, 'test is single flight');
    controller.suspend(); assert.equal(signal.aborted, true);
    await controller.activate('polaris');
    get('#polaris-test-result').textContent = 'Replacement presentation';
    complete(response(200, { data: { connected: true, organizationCount: 99 } })); await flush(); await flush();
    assert.equal(get('#polaris-test-result').textContent, 'Replacement presentation', 'retired diagnostic cannot repaint after re-entry');
    assert.equal(controller.hasUnconfirmedOutcome(), false);
  });
  console.log('Polaris diagnostic success/failure, no mutation receipts, consent, single flight and cancellation passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
