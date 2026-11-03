const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const settle = async () => { for (let index = 0; index < 10; index += 1) await new Promise(resolve => setImmediate(resolve)); };
const actor = id => ({ id, tenantId: `tenant-${id}`, authenticationEmail: `${id}@example.org`,
  organizationId: 1, role: 'super_admin', version: 'v1' });
const organizationCatalog = [
  { id: 1, name: 'System', abbreviation: 'SYS', organizationCodeId: 1, parentOrganizationId: null, isActive: true, lastSyncedUtc: null, version: 'org-1' },
  { id: 2, name: 'Library Two', abbreviation: 'L2', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, lastSyncedUtc: null, version: 'org-2' },
  { id: 3, name: 'Library Three', abbreviation: 'L3', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, lastSyncedUtc: null, version: 'org-3' },
  { id: 4, name: 'Library Four', abbreviation: 'L4', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, lastSyncedUtc: null, version: 'org-4' },
  { id: 20, name: 'Branch Twenty', abbreviation: 'B20', organizationCodeId: 3, parentOrganizationId: 2, isActive: true, lastSyncedUtc: null, version: 'org-20' },
  { id: 22, name: 'Inactive Branch', abbreviation: 'B22', organizationCodeId: 3, parentOrganizationId: 2, isActive: false, lastSyncedUtc: null, version: 'org-22' },
  { id: 21, name: 'Unclassified Reference', abbreviation: null, organizationCodeId: null, parentOrganizationId: 2, isActive: true, lastSyncedUtc: null, version: 'org-21' }
];
function committedOperationResult(path) {
  const url = new URL(path, 'https://localhost');
  if (url.pathname.endsWith('/email-operations/test')) {
    return { code: 'queued', data: { id: '9007199254740993', code: null,
      dispatchDelayed: false, version: 'email-test-v1' } };
  }
  if (url.pathname.endsWith('/retry')) {
    const id = url.pathname.split('/').at(-2);
    return { code: 'queued', data: { id, dispatchDelayed: false, version: 'email-retry-v1' } };
  }
  const data = { jobId: 'hangfire-job-1', organizationId: Number(url.searchParams.get('organizationId') || 1) };
  if (url.searchParams.get('force') === 'true') data.manualRunId = url.searchParams.get('operationId');
  return { code: 'queued', ...data };
}
// These fixtures exercise the generic operation journal. Saved email readiness
// is an independent read, covered with blocked/dirty/mode races in staff_email_test.
async function loadOperationsController(load) {
  const actual = await load('operations-controller');
  return { createOperationsController(options) {
    return actual.createOperationsController({ ...options, request: (path, init) => {
      if (path.includes('/email-operations/test-context')) {
        const organizationId = Number(new URL(path, 'https://localhost').searchParams.get('organizationId'));
        return Promise.resolve({ code: 'ok', data: { organizationId, scopeName: 'Saved fixture',
          fromAddress: 'saved@example.org', recipient: 'primary@example.org', deliveryMode: 'postmark',
          state: 'ready', configurationVersion: 'A'.repeat(64) } });
      }
      return options.request(path, init);
    } });
  } };
}
(async () => {
  for (const scope of ['all', '3']) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createOperationsController } = await loadOperationsController(load);
      const identity = createSessionIdentity(), owner = identity.accept(actor('weekly-gestures'));
      const posts = [], receipts = [];
      const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
        announce() {}, onScopeChange() {}, clearReceipt() {}, onReceipt: message => receipts.push(message),
        request: async (path, init = {}) => {
          if (init.method === 'POST') { posts.push(path); return committedOperationResult(path); }
          return path.endsWith('/organizations') ? { data: organizationCatalog } : { items: [] };
        } });
      controller.setStaff(owner); controller.activate(); await controller.refresh();
      get('#operations-scope').value = scope;
      get('#operations-scope').dispatchEvent(new dom.window.Event('change')); await settle();
      const run = get('#run-weekly-now');
      assert.equal(get('#force-weekly-now'), null);
      assert.equal(run.textContent.trim(), 'Run weekly summary');
      const click = detail => run.dispatchEvent(new dom.window.MouseEvent('click', { bubbles: true, detail }));
      click(1);
      assert.equal(run.disabled, true, 'the journal locks synchronously before the POST');
      await settle();
      assert.equal(run.disabled, false, 'the first acknowledgement has already completed');
      click(2); await settle();
      assert.equal(posts.length, 1, 'the second click of the same native double-click is not another deliberate run');
      for (const key of ['Enter', ' ']) {
        const repeat = new dom.window.KeyboardEvent('keydown', { key, repeat: true, bubbles: true, cancelable: true });
        run.dispatchEvent(repeat);
        assert.equal(repeat.defaultPrevented, true, 'held activation keys cannot synthesize fresh clicks after a fast acknowledgement');
        const deliberate = new dom.window.KeyboardEvent('keydown', { key, repeat: false, bubbles: true, cancelable: true });
        run.dispatchEvent(deliberate);
        assert.equal(deliberate.defaultPrevented, false, 'a new key gesture keeps native button activation');
      }
      click(1); await settle();
      // Keyboard/accessibility activation has click detail zero and remains a
      // deliberate gesture; native key synthesis itself is covered by Chromium.
      click(0); await settle();
      assert.equal(posts.length, 3);
      const urls = posts.map(path => new URL(path, 'https://localhost'));
      assert.ok(urls.every(url => url.searchParams.get('force') === 'true'));
      assert.ok(urls.every(url => url.searchParams.get('organizationId') === (scope === 'all' ? null : scope)));
      assert.equal(new Set(urls.map(url => url.searchParams.get('operationId'))).size, 3,
        'each subsequent deliberate gesture owns a fresh durable identity');
      assert.ok(receipts.every(message => /Weekly summary Run .* queued\./.test(message)));
      assert.ok(receipts.every(message => !/forced|sent|delivered/i.test(message)));
      controller.dispose();
    });
  }

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await loadOperationsController(load);
    const identity = createSessionIdentity(), owner = identity.accept(actor('weekly-lost-ack'));
    const posts = [];
    let rejectFirst;
    const options = { root: get('#operations-view'), sessionIdentity: identity,
      announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {},
      request: async (path, init = {}) => {
        if (init.method === 'POST') {
          posts.push(path);
          if (posts.length === 1) return new Promise((resolve, reject) => { rejectFirst = reject; });
          return committedOperationResult(path);
        }
        return path.endsWith('/organizations') ? { data: organizationCatalog } : { items: [] };
      } };
    const first = createOperationsController(options);
    first.setStaff(owner); first.activate(); await first.refresh();
    get('#operations-scope').value = '3';
    get('#operations-scope').dispatchEvent(new dom.window.Event('change')); await settle();
    get('#run-weekly-now').click(); get('#run-weekly-now').click();
    assert.equal(posts.length, 1, 'pending repeated activation sends one request');
    assert.equal(get('#operations-scope').disabled, true);
    assert.equal(get('#send-test-email').disabled, true);
    rejectFirst(new Error('Acknowledgement lost')); await settle();
    assert.equal(get('#run-weekly-now').disabled, true);
    assert.equal(get('#operations-outcome button'), null, 'uncertain retry waits for explicit review');
    const storageKey = `asap.staff.operation.${owner.tenantId}.${owner.id}.${encodeURIComponent(identity.actor().key)}`;
    const original = JSON.parse(dom.window.sessionStorage.getItem(storageKey));
    first.dispose();
    const restored = createOperationsController(options);
    restored.setStaff(owner); restored.activate(); await restored.refresh();
    assert.equal(posts.length, 1, 'reload and refresh are read-only');
    assert.equal(get('#operations-scope').value, 'all', 'visible scope does not replace the captured retry scope');
    get('#operations-outcome button').click(); await settle();
    assert.equal(posts.length, 2);
    assert.equal(posts[1], posts[0], 'retry keeps the exact original UUID, forced path and library scope');
    assert.equal(dom.window.sessionStorage.getItem(storageKey), null);
    get('#run-weekly-now').click(); await settle();
    assert.equal(posts.length, 3);
    const next = new URL(posts[2], 'https://localhost');
    assert.notEqual(next.searchParams.get('operationId'), original.operationId);
    assert.equal(next.searchParams.get('organizationId'), null, 'new work uses the currently selected scope');
    restored.dispose();
  });

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await loadOperationsController(load);
    const session = createSessionIdentity();
    const owner = session.accept(actor('read-dto'));
    const timestamp = '2026-03-08T07:01:00.1234567Z';
    const invalid = '<img src=x onerror="throw Error(1)">';
    // Parse the actual wire shape before rendering so opaque SQL identities never
    // pass through JavaScript numeric literals or Number conversion.
    const queues = JSON.parse(`{"items":[
      {"queueName":"Active","cycleMaxId":"9007199254740995","lastItemId":"9007199254740993","lastOutcomeItemId":"9223372036854775807","lastCreatedUtc":"${timestamp}","updatedUtc":"${timestamp}"},
      {"queueName":"Empty","cycleMaxId":"0","lastItemId":"0","lastCreatedUtc":"${timestamp}","updatedUtc":null},
      {"queueName":"Idle","cycleMaxId":null,"lastItemId":null,"lastCreatedUtc":null,"updatedUtc":null},
      {"queueName":"Completed","cycleMaxId":null,"lastItemId":null,"lastCreatedUtc":null,"lastOutcomeCode":"cycle_complete","updatedUtc":null},
      {"queueName":"Maximum","cycleMaxId":"9223372036854775807","lastItemId":"9223372036854775807","lastCreatedUtc":"${timestamp}","updatedUtc":"${timestamp}"}
    ]}`);
    queues.items.push({ queueName: 'Invalid', cycleMaxId: null, lastCreatedUtc: invalid,
      lastItemId: '9007199254740993', updatedUtc: invalid });
    const requests = [];
    const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: session,
      announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {},
      request: async path => {
        requests.push(path);
        if (path.endsWith('/organizations')) return { data: organizationCatalog };
        if (path.includes('/workflow/queues')) return queues;
        return { items: [
          { id: '1', createdUtc: timestamp, status: 'sent' },
          { id: '2', createdUtc: null, status: 'pending' },
          { id: '3', createdUtc: invalid, status: 'failed' }
        ] };
      } });
    controller.setStaff(owner); controller.activate();
    const assertProjection = () => {
      const rows = [...get('#queue-progress-table').querySelectorAll('tbody tr')];
      const cells = name => [...rows.find(row => row.cells[0].textContent === name).cells].map(cell => cell.textContent);
      assert.equal(cells('Active')[1], '9007199254740995');
      assert.equal(cells('Active')[2], 'Active');
      assert.equal(cells('Active')[3], `${new Date(timestamp).toLocaleString()} / 9007199254740993`);
      assert.equal(cells('Maximum')[1], '9223372036854775807');
      assert.equal(cells('Maximum')[3], `${new Date(timestamp).toLocaleString()} / 9223372036854775807`);
      assert.equal(cells('Empty')[1], '0');
      assert.equal(cells('Empty')[2], 'Empty', 'canonical string zero retains the empty-cycle sentinel');
      assert.equal(cells('Empty')[3], `${new Date(timestamp).toLocaleString()} / 0`);
      assert.equal(cells('Idle')[1], 'None');
      assert.equal(cells('Idle')[2], 'Idle');
      assert.equal(cells('Idle')[3], 'None');
      assert.equal(cells('Completed')[2], 'Completed');
      const times = [...get('#email-operations-table').querySelectorAll('time')];
      assert.equal(times[0].textContent, new Date(timestamp).toLocaleString());
      assert.equal(times[0].getAttribute('datetime'), timestamp);
      assert.equal(times[1].textContent, 'Not recorded');
      assert.equal(times[1].hasAttribute('datetime'), false);
      assert.equal(times[2].textContent, invalid);
      assert.equal(times[2].children.length, 0);
      assert.equal(cells('Invalid')[3], `${invalid} / 9007199254740993`);
      const updated = rows[0].querySelector('time');
      assert.equal(updated.textContent, new Date(timestamp).toLocaleString());
      assert.equal(updated.getAttribute('datetime'), timestamp);
      assert.equal(get('#queue-progress-table').querySelector('img'), null);
      assert.equal(get('#email-operations-table').querySelector('img'), null);
      assert.doesNotMatch(get('#operations-view').textContent, /Invalid Date/);
    };
    assert.equal(await controller.refresh(), true);
    assertProjection();
    get('#operations-scope').value = '2';
    get('#operations-scope').dispatchEvent(new dom.window.Event('change'));
    assert.equal(await controller.refresh({ silent: true }), true);
    assert.ok(requests.some(path => path.includes('/workflow/queues?organizationId=2')));
    assertProjection();
    controller.dispose();
  });

  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await loadOperationsController(load);
    const session = createSessionIdentity(); session.accept(actor('a'));
    const pending = [], receipts = [];
    const options = { root: get('#operations-view'), sessionIdentity: session, announce() {},
      onScopeChange() {}, clearReceipt() {}, onReceipt: (...value) => receipts.push(value),
      request: async (path, init = {}) => {
        if (init.method === 'POST') return new Promise(resolve => pending.push({ path, init, resolve }));
        if (path.endsWith('/organizations')) return { data: organizationCatalog };
        return { items: [] };
      } };
    const first = createOperationsController(options); first.setStaff(session.preferences()); first.activate();
    const savingA = first.run('/api/asap/staff/email-operations/test', 'A email');
    assert.equal(first.inspectDeparture().blocked, false, 'route may be left while attempt survives');
    assert.equal(pending[0].init.signal, undefined);
    first.deactivate(); first.dispose();
    session.clear(); session.accept(actor('b'));
    const second = createOperationsController(options); second.setStaff(session.preferences()); second.activate();
    assert.equal(get('#run-workflow-now').disabled, false, 'B cannot be blocked by A recovery');
    const savingB = second.run('/api/asap/staff/email-operations/test', 'B email');
    const bKey = `asap.staff.operation.tenant-b.b.${encodeURIComponent(session.actor().key)}`;
    const bRecord = window.sessionStorage.getItem(bKey);
    pending[0].resolve(committedOperationResult(pending[0].path)); await savingA;
    assert.equal(window.sessionStorage.getItem(bKey), bRecord, 'A cannot clear B captured recovery');
    assert.equal(get('#run-workflow-now').disabled, true, 'old actor cannot release new actor controls');
    assert.equal(receipts[0][2].outcome, 'committed');
    assert.equal(session.isCurrent(receipts[0][1]), false);
    pending[1].resolve(committedOperationResult(pending[1].path)); await savingB;
    assert.equal(window.sessionStorage.getItem(bKey), null);
    assert.equal(get('#run-workflow-now').disabled, false);
    second.dispose();
    window.sessionStorage.setItem(bKey, JSON.stringify({ operationId: 'forged', path: '/api/asap/staff/email-operations/test', scope: 'all', message: 'Invalid' }));
    const third = createOperationsController(options); third.setStaff(session.preferences());
    assert.equal(get('#run-workflow-now').disabled, false, 'malformed records cannot acquire a command guard');
    third.dispose();
  });
  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await loadOperationsController(load);
    const session = createSessionIdentity(); session.accept(actor('a'));
    const reads = [], notices = [], receipts = [];
    const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: session,
      announce: message => notices.push(message), onScopeChange() {}, clearReceipt() {}, onReceipt: (...args) => receipts.push(args),
      request: async (path, init = {}) => init.method === 'POST' ? committedOperationResult(path)
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
      const { createOperationsController } = await loadOperationsController(load);
      const identity = createSessionIdentity(); identity.accept(actor('a'));
      const paths = [], posts = [], notices = [];
      let uncertain = true, retired = false;
      const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
        announce: message => notices.push(message), onScopeChange() {}, onReceipt() {}, clearReceipt() {},
        request: async (path, init = {}) => {
          if (init.method === 'POST') {
            posts.push({ path, init });
            if (uncertain) throw new Error('Response lost');
              return committedOperationResult(path);
          }
          paths.push(path);
          if (path.endsWith('/organizations')) return { data: organizationCatalog.map(item => item.id === 3
            ? { ...item, isActive: !retired || scenario !== 'inactive' } : item) };
          if (retired && scenario === 'exact review unavailable' && path.endsWith('organizationId=3')) throw new Error('Exact review unavailable');
          return { items: [] };
        } });
      controller.setStaff(identity.preferences()); controller.activate();
      // Queue projections supply OrganizationChoice{id,name}, not organization summaries.
      controller.setLibraries([{ id: 3, name: 'Library Three' }]);
      assert.deepEqual([...get('#operations-scope').options].map(option => option.value), ['all', '3'],
        'trusted queue choices retain the selectable library scope');
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
  const sameIdActor = { ...actor('20'), organizationId: 3 };
  for (const change of [{ authenticationEmail: 'replacement@example.org' }, { role: 'admin' }, { organizationId: 4 }]) {
    for (const kind of ['legacy', 'modern foreign', 'missing evidence at modern key', 'foreign evidence at modern key']) {
      await fixture(async ({ load, get, dom }) => {
        const { createSessionIdentity, actorKey } = await load('session-identity');
        const { createOperationsController } = await loadOperationsController(load);
        const session = createSessionIdentity(); session.accept(sameIdActor);
        const posts = [];
        const options = { root: get('#operations-view'), sessionIdentity: session, announce() {},
          onScopeChange() {}, clearReceipt() {}, onReceipt() {},
          request: async (path, init = {}) => {
            if (init.method === 'POST') { posts.push({ path, init }); throw new Error('Response lost'); }
            if (path.endsWith('/organizations')) return { data: organizationCatalog };
            return { items: [] };
          } };
        const first = createOperationsController(options); first.setStaff(session.preferences()); first.activate();
        await first.run('/api/asap/staff/workflow/weekly-summary/run-now?force=true', 'Captured operation'); first.dispose();
        const oldKey = `asap.staff.operation.${sameIdActor.tenantId}.${sameIdActor.id}.${encodeURIComponent(actorKey(sameIdActor))}`;
        const oldRaw = dom.window.sessionStorage.getItem(oldKey);
        const replacement = { ...sameIdActor, ...change }; session.accept(replacement);
        let testedKey = oldKey, testedRaw = oldRaw;
        if (kind !== 'modern foreign') {
          testedKey = kind === 'legacy' ? `asap.staff.operation.${sameIdActor.tenantId}.${sameIdActor.id}`
            : `asap.staff.operation.${replacement.tenantId}.${replacement.id}.${encodeURIComponent(actorKey(replacement))}`;
          const legacy = JSON.parse(oldRaw);
          if (kind !== 'foreign evidence at modern key') delete legacy.actorKey;
          testedRaw = JSON.stringify(legacy); dom.window.sessionStorage.setItem(testedKey, testedRaw);
        }
        const second = createOperationsController(options); second.setStaff(session.preferences()); second.activate();
        assert.equal(await second.refresh(), true);
        const controls = ['#run-workflow-now', '#run-weekly-now', '#send-test-email', '#operations-scope'];
        for (const selector of controls) assert.equal(get(selector).disabled, false, `${kind} cannot block ${selector}`);
        assert.equal(await second.refresh(), true);
        assert.equal(get('#operations-outcome').hidden, true, 'foreign recovery cannot become current/reviewed');
        assert.equal(get('#operations-outcome button'), null, 'foreign or unverifiable evidence cannot expose Retry');
        assert.equal(posts.length, 1, 'review never dispatches a foreign retained command');
        second.signedOut(); second.dispose();
        assert.equal(dom.window.sessionStorage.getItem(testedKey), testedRaw, 'replacement actor cannot clear unverifiable or foreign evidence');
        assert.equal(dom.window.sessionStorage.getItem(oldKey), oldRaw);
      });
    }
  }
  for (const path of ['/api/asap/staff/workflow/weekly-summary/run-now?force=true', '/api/asap/staff/email-operations/71/retry']) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity, actorKey } = await load('session-identity');
      const { createOperationsController } = await loadOperationsController(load);
      const session = createSessionIdentity(); const owner = session.accept(sameIdActor);
      const posts = [], reads = [];
      const options = { root: get('#operations-view'), sessionIdentity: session, announce() {},
        onScopeChange() {}, clearReceipt() {}, onReceipt() {},
        request: async (requestPath, init = {}) => {
          if (init.method === 'POST') { posts.push({ path: requestPath, init }); throw new Error('Response lost'); }
          reads.push(requestPath);
          return requestPath.endsWith('/organizations') ? { data: organizationCatalog } : { items: [] };
        } };
      const first = createOperationsController(options); first.setStaff(owner); first.activate();
      // The queue/copy caller passes its restricted {id,name} projection.
      first.setLibraries([{ id: 3, name: 'Library Three' }]);
      assert.deepEqual([...get('#operations-scope').options].map(option => option.value), ['all', '3'],
        'trusted choices preserve the captured library scope for exact review');
      get('#operations-scope').value = '3'; get('#operations-scope').dispatchEvent(new dom.window.Event('change'));
      await new Promise(resolve => setImmediate(resolve));
      const body = path.endsWith('/retry') ? { version: 'captured-v1' } : undefined;
      await first.run(path, 'Captured operation', null, body); first.dispose();
      const key = `asap.staff.operation.${sameIdActor.tenantId}.${sameIdActor.id}.${encodeURIComponent(actorKey(sameIdActor))}`;
      const raw = dom.window.sessionStorage.getItem(key), captured = JSON.parse(raw);
      const revised = session.updatePreferences({ ...sameIdActor, version: 'v2', displayName: 'Revised',
        weeklyActionSummaryEnabled: true, purchaseReminderDefault: true }, owner);
      const second = createOperationsController(options); second.setStaff(revised); second.activate();
      assert.equal(get('#run-workflow-now').disabled, true, 'exact actor revision restores command guard');
      assert.equal(get('#operations-outcome button'), null, 'restoration does not restore review authority');
      reads.length = 0; await second.refresh();
      assert.equal(reads.filter(value => value.endsWith('organizationId=3')).length, 2, 'captured scope reviewed independently of all projection');
      assert.equal(dom.window.sessionStorage.getItem(key), raw, 'review cannot rewrite captured identity');
      const retry = get('#operations-outcome button'); assert.ok(retry);
      retry.click(); await new Promise(resolve => setImmediate(resolve));
      assert.equal(posts[1].path, posts[0].path); assert.deepEqual(posts[1].init.body, posts[0].init.body);
      assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).operationId, captured.operationId);
      await second.refresh(); const oldRetry = get('#operations-outcome button'); assert.ok(oldRetry);
      session.accept({ ...revised, authenticationEmail: 'replacement@example.org' }); second.setStaff(session.preferences());
      oldRetry.click(); await new Promise(resolve => setImmediate(resolve));
      assert.equal(posts.length, 2, 'actor replacement cannot reuse exact captured review');
      assert.equal(get('#run-workflow-now').disabled, false);
      second.dispose();
    });
  }
  for (const outcome of ['committed', 'uncertain']) {
    for (const replacement of ['newer', 'foreign']) {
      await fixture(async ({ load, get, dom }) => {
        const { createSessionIdentity } = await load('session-identity');
        const { createOperationsController } = await loadOperationsController(load);
        const session = createSessionIdentity(); session.accept(sameIdActor);
        let complete, reject;
        const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: session,
          announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {},
          request: path => new Promise((resolve, fail) => {
            complete = () => resolve(committedOperationResult(path)); reject = fail;
          }) });
        controller.setStaff(session.preferences()); controller.activate();
        const saving = controller.run('/api/asap/staff/email-operations/test', 'Pending operation');
        const key = `asap.staff.operation.${sameIdActor.tenantId}.${sameIdActor.id}.${encodeURIComponent(session.actor().key)}`;
        const saved = JSON.parse(dom.window.sessionStorage.getItem(key));
        // Keeping operationId identical reproduces the old overly broad cleanup.
        const next = JSON.stringify({ ...saved, ...(replacement === 'foreign'
          ? { actorKey: 'another-actor' } : { message: 'Newer record' }) });
        dom.window.sessionStorage.setItem(key, next); controller.dispose();
        if (outcome === 'committed') complete();
        else reject(new Error('Response lost'));
        await saving;
        assert.equal(dom.window.sessionStorage.getItem(key), next, `${outcome} cannot remove or rewrite a ${replacement} record`);
      });
    }
  }
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await loadOperationsController(load);
    const session = createSessionIdentity(); session.accept(sameIdActor);
    const pending = [];
    const options = { root: get('#operations-view'), sessionIdentity: session, announce() {},
      onScopeChange() {}, onReceipt() {}, clearReceipt() {}, request: async (path, init = {}) => {
        if (init.method === 'POST') return new Promise(resolve => pending.push({ path, resolve }));
        return path.endsWith('/organizations') ? { data: organizationCatalog } : { items: [] };
      } };
    const first = createOperationsController(options); first.setStaff(session.preferences()); first.activate();
    const saving = first.run('/api/asap/staff/workflow/weekly-summary/run-now?force=true', 'Reloaded operation'); first.dispose();
    const key = `asap.staff.operation.${sameIdActor.tenantId}.${sameIdActor.id}.${encodeURIComponent(session.actor().key)}`;
    const original = JSON.parse(dom.window.sessionStorage.getItem(key));
    const second = createOperationsController(options); second.setStaff(session.preferences()); second.activate(); await second.refresh();
    get('#operations-outcome button').click();
    const newerRaw = dom.window.sessionStorage.getItem(key), newer = JSON.parse(newerRaw);
    assert.equal(newer.operationId, original.operationId, 'reload retry preserves immutable command identity');
    assert.notEqual(newer.recordId, original.recordId, 'each dispatch owns distinct durable evidence');
    pending[0].resolve(committedOperationResult(pending[0].path)); await saving;
    assert.equal(dom.window.sessionStorage.getItem(key), newerRaw, 'pre-reload completion cannot clear a newer retry of the same operation');
    pending[1].resolve(committedOperationResult(pending[1].path)); await new Promise(resolve => setImmediate(resolve));
    assert.equal(dom.window.sessionStorage.getItem(key), null, 'new retry clears its own exact record');
    second.dispose();
  });
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await loadOperationsController(load);
    const identity = createSessionIdentity(); identity.accept(actor('catalog-scope'));
    const paths = [];
    const key = `asap.staff.operation.${identity.actor().tenantId}.${identity.actor().id}.${encodeURIComponent(identity.actor().key)}`;
    const saved = {
      path: '/api/asap/staff/workflow/run-now',
      message: 'Retained branch workflow',
      scope: '20',
      operationId: '74c093a8-6d8b-4f8d-b38a-65521235ee4a',
      actorKey: identity.actor().key,
      body: null
    };
    dom.window.sessionStorage.setItem(key, JSON.stringify(saved));
    const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
      announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {},
      request: async path => {
        paths.push(path);
        return path.endsWith('/organizations') ? { data: organizationCatalog } : { items: [] };
      } });
    controller.setStaff(identity.preferences()); controller.activate();
    assert.equal(await controller.refresh(), true);
    assert.deepEqual([...get('#operations-scope').options].map(option => option.value), ['all', '4', '3', '2'],
      'active code-2 libraries remain selectable while system, branch, inactive and unclassified rows stay excluded');
    assert.equal(get('#operations-outcome button'), null,
      'a retained command scoped to an active branch is not reviewable as a library operation');
    assert.equal(paths.some(path => path.includes('organizationId=20')), false,
      'branch scope cannot trigger exact command review reads');
    assert.equal(dom.window.sessionStorage.getItem(key), JSON.stringify(saved),
      'filtered presentation preserves the captured command without authorizing it');
    controller.dispose();
  });
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await loadOperationsController(load);
    const identity = createSessionIdentity(), owner = identity.accept(actor('legacy-test'));
    const operationId = 'db540bca-9621-42b7-a8f1-4b58bf7f99a9';
    const key = `asap.staff.operation.${owner.tenantId}.${owner.id}.${encodeURIComponent(identity.actor().key)}`;
    dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, actorKey: identity.actor().key,
      path: '/api/asap/staff/email-operations/test', scope: '3', message: 'Original test email' }));
    let posts = 0, observed = 0;
    const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
      announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {}, request: async (path, init = {}) => {
        if (init.method === 'POST') { posts += 1; throw new Error('Legacy recovery must never POST.'); }
        if (path.endsWith('/organizations')) return { data: organizationCatalog };
        if (path.includes('/email-operations/test?')) {
          observed += 1;
          assert.equal(new URL(path, 'https://localhost').searchParams.get('operationId'), operationId);
          return { code: 'ok', data: { id: '9223372036854775807', organizationId: 3, status: 'sent', deliveryMode: 'postmark' } };
        }
        return { items: [] };
      } });
    controller.setStaff(owner); controller.activate(); await controller.refresh();
    assert.equal(posts, 0);
    assert.equal(observed, 1, 'legacy recovery resolves its original click beyond the list projection');
    assert.equal(dom.window.sessionStorage.getItem(key), null);
    const recovered = JSON.parse(dom.window.sessionStorage.getItem(`asap.staff.email-test.${encodeURIComponent(identity.actor().key)}.3`));
    assert.equal(recovered.operationId, operationId);
    assert.equal(recovered.id, '9223372036854775807');
    assert.equal(recovered.pending, false);
    controller.dispose();
  });
  const status200Commands = [
    { selector: '#run-workflow-now', path: '/api/asap/staff/workflow/run-now', message: 'Workflow run' },
    { selector: '#run-weekly-now', path: '/api/asap/staff/workflow/weekly-summary/run-now?force=true', message: 'Weekly summary' },
    // The shared email-test controller owns the new visible action; this entry
    // retains the generic legacy acknowledgement/recovery contract.
    { legacyTest: true, path: '/api/asap/staff/email-operations/test', message: 'Test email' },
    { retry: true, path: '/api/asap/staff/email-operations/71/retry', message: 'Email retry 71', body: { version: 'email-v1' } }
  ];
  for (const command of status200Commands) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createOperationsController } = await loadOperationsController(load);
      const identity = createSessionIdentity();
      const ownerA = identity.accept(actor('a'));
      const posts = [], receipts = [];
      const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
        announce() {}, onScopeChange() {}, clearReceipt() {}, onReceipt: (...args) => receipts.push(args),
        request: async (path, init = {}) => {
          if (init.method === 'POST') {
            posts.push({ path, init });
            throw Object.assign(new Error('HTTP 200 operation result is unknown.'), {
              status: 200, outcomeUnknown: true
            });
          }
          if (path.endsWith('/organizations')) return { data: organizationCatalog };
          if (path.includes('/email-operations')) return { items: [
            { id: '71', status: 'failed', deliveryClass: 'operational_test', version: 'email-v1',
              lastErrorCode: 'mail_not_configured', canRetry: true }
          ] };
          return { items: [] };
        } });
      controller.setStaff(ownerA);
      controller.setLibraries([{ id: 3, name: 'Library Three' }]);
      controller.activate();
      get('#operations-scope').value = '3';
      get('#operations-scope').dispatchEvent(new dom.window.Event('change'));
      await controller.refresh();

      if (command.retry) {
        get('#email-view-scope').value = '3';
        get('#email-view-scope').dispatchEvent(new dom.window.Event('change'));
        await controller.refresh();
        const row = [...get('#email-operations-table').querySelectorAll('tr')]
          .find(item => item.textContent.includes('71'));
        assert.ok(row, 'retry-email witness loads the captured failed row');
        row.querySelector('button').click();
      } else if (command.legacyTest) {
        void controller.run(command.path, command.message);
      } else {
        get(command.selector).click();
      }
      for (let attempt = 0; attempt < 20 && posts.length === 0; attempt++) {
        await new Promise(resolve => setImmediate(resolve));
      }
      for (let attempt = 0; attempt < 5; attempt++) await new Promise(resolve => setImmediate(resolve));

      assert.equal(posts.length, 1, `${command.message}: one uncertain POST is captured and never replayed`);
      assert.ok(posts[0].path.startsWith(command.path), `${command.message}: dispatch uses its real command path`);
      assert.equal(posts[0].init.body?.version, command.body?.version);
      assert.equal(receipts.length, 1, `${command.message}: a status-200 unknown result creates one owner-bound receipt`);
      assert.equal(receipts[0][1].id, ownerA.id);
      assert.equal(receipts[0][2].outcome, 'uncertain');
      assert.equal(receipts[0][2].scope, '3', 'receipt retains the scope captured at dispatch');

      const keyA = `asap.staff.operation.${ownerA.tenantId}.${ownerA.id}.${encodeURIComponent(identity.actor().key)}`;
      const rawA = dom.window.sessionStorage.getItem(keyA);
      assert.ok(rawA, `${command.message}: uncertain dispatch evidence remains stored`);
      const savedA = JSON.parse(rawA);
      assert.equal(savedA.actorKey, identity.actor().key);
      assert.equal(savedA.scope, '3');
      assert.equal(savedA.path, command.path);
      assert.equal(savedA.body?.version, command.body?.version);

      identity.clear();
      const ownerB = identity.accept(actor('b'));
      controller.setStaff(ownerB);
      const keyB = `asap.staff.operation.${ownerB.tenantId}.${ownerB.id}.${encodeURIComponent(identity.actor().key)}`;
      assert.equal(dom.window.sessionStorage.getItem(keyB), null, 'replacement actor cannot adopt actor A command evidence');
      assert.equal(dom.window.sessionStorage.getItem(keyA), rawA, 'actor B cannot clear actor A uncertain evidence');
      assert.equal(posts.length, 1, 'actor replacement does not retry the uncertain command');
      controller.dispose();
    });
  }
  const invalidAcknowledgements = [
    { name: 'workflow result without a job id', path: '/api/asap/staff/workflow/run-now',
      result: { code: 'queued', organizationId: 1 } },
    { name: 'workflow result for a different scope', path: '/api/asap/staff/workflow/run-now',
      result: { code: 'queued', jobId: 'job-1', organizationId: 2 } },
    { name: 'forced weekly result for a different operation id', path: '/api/asap/staff/workflow/weekly-summary/run-now?force=true',
      result: { code: 'queued', jobId: 'job-1', organizationId: 1,
        manualRunId: '00000000-0000-4000-8000-000000000000' } },
    { name: 'workflow cannot return the email-only suppressed code', path: '/api/asap/staff/workflow/run-now',
      result: { code: 'suppressed', jobId: 'job-1', organizationId: 1 } },
    { name: 'email test result missing its version', path: '/api/asap/staff/email-operations/test',
      result: { code: 'queued', data: { id: '9007199254740993', dispatchDelayed: false } } },
    { name: 'email replay missing its prior status', path: '/api/asap/staff/email-operations/test',
      result: { code: 'queued', data: { id: '9007199254740993', version: 'email-v2', replayed: true } } },
    { name: 'email retry result with a different bigint id', path: '/api/asap/staff/email-operations/9007199254740993/retry',
      body: { version: 'email-v1' },
      result: { code: 'queued', data: { id: '9007199254740992', version: 'email-v2', dispatchDelayed: false } } },
    { name: 'email test result with a zero bigint id', path: '/api/asap/staff/email-operations/test',
      result: { code: 'queued', data: { id: '0', code: null, dispatchDelayed: false, version: 'email-v2' } } },
    { name: 'email test result with a noncanonical bigint id', path: '/api/asap/staff/email-operations/test',
      result: { code: 'queued', data: { id: '01', code: null, dispatchDelayed: false, version: 'email-v2' } } },
    { name: 'email test result with an overflowing bigint id', path: '/api/asap/staff/email-operations/test',
      result: { code: 'queued', data: { id: '9223372036854775808', code: null, dispatchDelayed: false, version: 'email-v2' } } },
    { name: 'email test result with a numeric id', path: '/api/asap/staff/email-operations/test',
      result: { code: 'queued', data: { id: 42, code: null, dispatchDelayed: false, version: 'email-v2' } } },
    { name: 'email retry with an overflowing path and matching result id',
      path: '/api/asap/staff/email-operations/9223372036854775808/retry', body: { version: 'email-v1' },
      result: { code: 'queued', data: { id: '9223372036854775808', version: 'email-v2', dispatchDelayed: false } } }
  ];
  for (const invalid of invalidAcknowledgements) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createOperationsController } = await loadOperationsController(load);
      const identity = createSessionIdentity();
      const owner = identity.accept(actor('shape'));
      const posts = [], receipts = [];
      const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
        announce() {}, onScopeChange() {}, clearReceipt() {}, onReceipt: (...args) => receipts.push(args),
        request: async (path, init = {}) => {
          if (init.method === 'POST') { posts.push({ path, init }); return invalid.result; }
          if (path.endsWith('/organizations')) return { data: organizationCatalog };
          if (path.includes('/email-operations')) return { items: [
            { id: '9007199254740993', status: 'failed', deliveryClass: 'business_event',
              lastErrorCode: 'mail_not_configured', canRetry: true, version: 'email-v1' }
          ] };
          return { items: [] };
        } });
      controller.setStaff(owner); controller.activate();
      await controller.run(invalid.path, invalid.name, null, invalid.body);
      assert.equal(posts.length, 1, `${invalid.name}: exactly one dispatch is made`);
      assert.equal(receipts.length, 1, `${invalid.name}: malformed 2xx retains a single owner-bound receipt`);
      assert.equal(receipts[0][1].id, owner.id);
      assert.equal(receipts[0][2].outcome, 'uncertain', `${invalid.name}: malformed 2xx is not confirmed`);
      assert.equal(get('#operations-outcome').hidden, false);
      assert.equal(get('#operations-outcome button'), null, `${invalid.name}: no retry before authoritative review`);
      for (const selector of ['#run-workflow-now', '#run-weekly-now', '#send-test-email']) {
        assert.equal(get(selector).disabled, true, `${invalid.name}: controls remain guarded`);
      }
      const key = `asap.staff.operation.${owner.tenantId}.${owner.id}.${encodeURIComponent(identity.actor().key)}`;
      const raw = dom.window.sessionStorage.getItem(key);
      assert.ok(raw, `${invalid.name}: operation identity remains durably reviewable`);
      const saved = JSON.parse(raw);
      assert.equal(saved.path, invalid.path);
      assert.equal(saved.body?.version, invalid.body?.version);
      if (invalid.path.includes('force=true')) {
        assert.equal(new URL(posts[0].path, 'https://localhost').searchParams.get('operationId'), saved.operationId,
          'the forced result is checked against the exact persisted operation identity');
      }
      if (invalid.path.endsWith('/retry')) {
        assert.equal(posts[0].path, invalid.path);
        assert.equal(posts[0].init.body.version, 'email-v1');
      }
      await controller.run(invalid.path, invalid.name, null, invalid.body);
      assert.equal(posts.length, 1, `${invalid.name}: a malformed response cannot cause a blind second POST`);
      assert.equal(dom.window.sessionStorage.getItem(key), raw,
        `${invalid.name}: malformed response preserves exact captured identity`);
      controller.dispose();
    });
  }
  for (const result of [
    { code: 'suppressed', data: { id: '9007199254740993', code: 'mail_not_configured',
      dispatchDelayed: false, version: 'email-suppressed-v1' } },
    { code: 'queued', data: { id: '9007199254740993', code: null, version: 'email-replay-v1',
      replayed: true, status: 'pending' } },
    { code: 'queued', data: { id: '9223372036854775807', code: null, version: 'email-max-id-v1',
      replayed: true, status: 'pending' } }
  ]) {
    await fixture(async ({ load, get }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createOperationsController } = await loadOperationsController(load);
      const identity = createSessionIdentity(); const owner = identity.accept(actor('shape-positive'));
      const receipts = [];
      const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
        announce() {}, onScopeChange() {}, clearReceipt() {}, onReceipt: (...args) => receipts.push(args),
        request: async (path, init = {}) => {
          if (init.method === 'POST') return result;
          if (path.endsWith('/organizations')) return { data: organizationCatalog };
          return { items: [] };
        } });
      controller.setStaff(owner); controller.activate();
      await controller.run('/api/asap/staff/email-operations/test', 'Test email');
      assert.equal(receipts.length, 1);
      assert.equal(receipts[0][2].outcome, 'committed', 'canonical suppression and replay envelopes remain valid outcomes');
      if (result.code === 'suppressed') assert.match(receipts[0][0], /suppressed; no email was sent/i);
      else assert.match(receipts[0][0], new RegExp(`already recorded as pending\\. Review email operation ${result.data.id}`, 'i'));
      controller.dispose();
    });
  }
  for (const recovery of [
    { organizationId: 2, id: '9007199254740993' },
    { organizationId: 2, id: null },
    { organizationId: 1, id: null }
  ]) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createOperationsController } = await loadOperationsController(load);
      const identity = createSessionIdentity(), owner = identity.accept(actor('remembered-test'));
      const operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
      const key = `asap.staff.email-test.${encodeURIComponent(identity.actor().key)}.${recovery.organizationId}`;
      dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, id: recovery.id, pending: true }));
      const calls = [];
      let status = 'failed', pause = false, resolveOld, oldStarted;
      const started = new Promise(resolve => { oldStarted = resolve; });
      const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
        announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {}, request: async (path, init = {}) => {
          calls.push({ path, init });
          if (path.endsWith('/organizations')) return { data: organizationCatalog };
          if (/\/email-operations\/(test|9007199254740993)\?/.test(path)) {
            if (pause) { pause = false; oldStarted(); return new Promise(resolve => { resolveOld = resolve; }); }
            return { code: 'ok', data: { id: '9007199254740993', organizationId: recovery.organizationId,
              status, lastErrorCode: 'provider_timeout', deliveryMode: 'postmark' } };
          }
          return { items: [] }; // The bounded log deliberately contains no matching row.
        } });
      controller.setStaff(owner); controller.activate();
      await controller.openEmailScope(recovery.organizationId);
      assert.equal(get('#send-test-email').disabled, true, 'an empty visible log cannot resolve an uncertain exact test');
      assert.equal(get('#email-operations-table tbody tr').cells[0].textContent, '9007199254740993',
        'the authorized exact test is represented even when the bounded log omits it');
      assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).pending, true);
      const exact = calls.filter(call => /\/email-operations\/(test|9007199254740993)\?/.test(call.path));
      assert.ok(exact.length > 0);
      assert.ok(exact[0].path.includes(`organizationId=${recovery.organizationId}`));
      assert.ok(exact[0].path.includes(recovery.id || `operationId=${operationId}`));
      if (recovery.organizationId === 1) {
        assert.equal(controller.currentScope(), '1');
        assert.ok(calls.some(call => call.path === '/api/asap/staff/email-operations?organizationId=1'), 'the explicit System view includes System tests');
      }
      pause = true;
      const oldRefresh = controller.refresh(); await started;
      status = 'sent';
      await controller.refresh();
      assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).pending, false);
      resolveOld({ code: 'ok', data: { id: '9007199254740993', organizationId: recovery.organizationId,
        status: 'failed', lastErrorCode: 'provider_timeout', deliveryMode: 'postmark' } });
      await oldRefresh;
      assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).pending, false, 'a stale status reply cannot replace a newer sent result');
      assert.match(get('#email-operations-table tbody tr').textContent, /Provider accepted/,
        'a superseded exact read cannot replace the current visible test row');
      assert.equal(get('#send-test-email').disabled, false);
      assert.equal(get('#operations-email-test [data-email-test="result"]').hidden, true);
      const beforeMainRefresh = calls.length;
      get('#refresh-operations').click();
      for (let index = 0; index < 10; index += 1) await new Promise(resolve => setImmediate(resolve));
      assert.ok(calls.length > beforeMainRefresh, 'the main refresh control reloads operational data');
      assert.equal(calls.filter(call => call.init.method === 'POST').length, 0, 'refresh never sends, retries, or retires a test');
      controller.dispose();
    });
  }
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await loadOperationsController(load);
    const identity = createSessionIdentity();
    let owner = identity.accept(actor('visible-A')), resolveOld;
    const paused = new Promise(resolve => { resolveOld = resolve; });
    const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
      announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {}, request: async path => {
        if (path.endsWith('/organizations')) return { data: organizationCatalog };
        const scope = new URL(path, 'https://localhost').searchParams.get('organizationId') || 'all';
        const name = `${identity.preferences().id}-${scope}`;
        if (path.includes('/workflow/queues')) {
          if (scope === '2') return paused;
          return { items: [{ queueName: `queue-${name}` }] };
        }
        return { items: [{ id: '1', status: 'failed', lastErrorCode: `email-${name}` }] };
      } });
    controller.setStaff(owner); controller.activate(); await controller.refresh();
    assert.match(get('#queue-progress-table').textContent, /queue-visible-A-all/);
    assert.match(get('#email-operations-table').textContent, /email-visible-A-all/);
    get('#email-view-scope').value = '2';
    get('#email-view-scope').dispatchEvent(new dom.window.Event('change'));
    assert.doesNotMatch(get('#queue-progress-table').textContent, /visible-A/, 'scope changes remove old queue rows before awaiting reads');
    assert.doesNotMatch(get('#email-operations-table').textContent, /visible-A/, 'scope changes remove old email rows before awaiting reads');
    get('#email-view-scope').value = '3';
    get('#email-view-scope').dispatchEvent(new dom.window.Event('change'));
    await controller.refresh();
    assert.match(get('#email-operations-table').textContent, /email-visible-A-3/);
    resolveOld({ items: [{ queueName: 'stale-library-2' }] });
    for (let index = 0; index < 10; index += 1) await new Promise(resolve => setImmediate(resolve));
    assert.doesNotMatch(get('#operations-view').textContent, /stale-library-2/);
    owner = identity.accept(actor('visible-B')); controller.setStaff(owner);
    assert.doesNotMatch(get('#queue-progress-table').textContent, /visible-A/, 'account changes remove old queue data synchronously');
    assert.doesNotMatch(get('#email-operations-table').textContent, /visible-A/, 'account changes remove old email data synchronously');
    await controller.refresh();
    assert.match(get('#email-operations-table').textContent, /email-visible-B-all/);
    controller.deactivate();
    assert.doesNotMatch(get('#email-operations-table').textContent, /visible-B/, 'leaving the screen retires its rendered context');
    controller.dispose();
  });
  for (const failure of ['queues', 'email-operations']) {
    for (const knownReference of [false, true]) {
      await fixture(async ({ load, get, dom }) => {
        const { createSessionIdentity } = await load('session-identity');
        const { createOperationsController } = await loadOperationsController(load);
        const identity = createSessionIdentity(), owner = identity.accept(actor('independent-recovery'));
        const operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
        const key = `asap.staff.email-test.${encodeURIComponent(identity.actor().key)}.1`;
        dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, pending: true,
          id: knownReference ? '9007199254740993' : null }));
        let recover = false;
        const calls = [], announcements = [];
        const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
          announce: (...args) => announcements.push(args), onScopeChange() {}, onReceipt() {}, clearReceipt() {},
          request: async (path, init = {}) => {
            calls.push({ path, init });
            if (/\/email-operations\/(test|9007199254740993)\?/.test(path)) {
              if (!recover) throw new Error('Exact lookup was temporarily unavailable');
              return { code: 'ok', data: { id: '9007199254740993', organizationId: 1,
                status: 'sent', deliveryMode: 'postmark' } };
            }
            if (path.endsWith(`/${failure}`)) throw new Error(`${failure} is unavailable`);
            if (path.endsWith('/organizations')) return { data: organizationCatalog };
            return { items: [] };
          } });
        controller.setStaff(owner); controller.activate(); await settle();
        assert.equal(get('#send-test-email').disabled, true);
        assert.match(get('#operations-email-test [data-email-test="result"]').textContent, /unconfirmed/);
        recover = true;
        assert.equal(await controller.refresh(), false, 'table failures remain failures after independent exact reconciliation');
        const stored = JSON.parse(dom.window.sessionStorage.getItem(key));
        assert.equal(stored.operationId, operationId);
        assert.equal(stored.id, '9007199254740993');
        assert.equal(stored.pending, false, 'a recovered exact read resolves the original intent despite unrelated table failure');
        assert.equal(get('#send-test-email').disabled, false);
        assert.equal(get('#operations-email-test [data-email-test="result"]').hidden, true);
        assert.ok(announcements.some(([message, tone]) => message === `${failure} is unavailable` && tone === 'error'));
        const lookups = calls.filter(call => /\/email-operations\/(test|9007199254740993)\?/.test(call.path));
        assert.equal(lookups.length, 2);
        assert.ok(lookups.every(call => call.path.includes(knownReference ? '/9007199254740993?' : `operationId=${operationId}`)));
        assert.equal(calls.filter(call => call.init.method === 'POST').length, 0, 'refresh never resends, retries, or retires');
        assert.equal(dom.window.sessionStorage.getItem(`${key}.retirement-references`), null);
        controller.dispose();
      });
    }
  }
  for (const readiness of ['blocked', 'unavailable']) {
    for (const refreshPending of [false, true]) {
      await fixture(async ({ load, get, dom }) => {
        const originalNow = Date.now;
        let now = originalNow();
        Date.now = () => now;
        try {
          const { createSessionIdentity } = await load('session-identity');
          const { createOperationsController } = await load('operations-controller');
          const identity = createSessionIdentity(), owner = identity.accept(actor('retirement-focus'));
          const key = `asap.staff.email-test.${encodeURIComponent(identity.actor().key)}.1`;
          const operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
          dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, pending: true, id: null }));
          let holdTables = false, releaseTables;
          const held = new Promise(resolve => { releaseTables = resolve; });
          const calls = [];
          const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
            announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {}, request: async (path, init = {}) => {
              calls.push({ path, init });
              if (path.includes('/test-context')) {
                if (readiness === 'unavailable') throw new Error('Saved context is temporarily unavailable');
                return { code: 'ok', data: { organizationId: 1, scopeName: 'System', deliveryMode: 'postmark',
                  state: 'blocked', blockingReason: 'mail_not_configured', configurationVersion: 'A'.repeat(64) } };
              }
              if (path.includes('/email-operations/test?')) throw Object.assign(new Error('Not recorded'), { status: 404, response: { code: 'not_found' } });
              if (path.endsWith('/organizations')) return { data: organizationCatalog };
              if (holdTables && path.includes('/workflow/queues')) return held;
              return { items: [] };
            } });
          get('#operations-view').hidden = false;
          controller.setStaff(owner); controller.activate(); await settle();
          now += 60001; await controller.refresh();
          const retire = get('#operations-email-test [data-email-test="retire"]');
          assert.equal(retire.hidden, false);
          assert.equal(get('#send-test-email').disabled, true);
          let loading;
          if (refreshPending) { holdTables = true; loading = controller.refresh(); await settle(); }
          retire.focus(); retire.click(); await settle();
          assert.equal(retire.hidden, true);
          const focus = get(refreshPending ? '#email-operations-table' : '#refresh-operations');
          assert.equal(dom.window.document.activeElement, focus, `${readiness}: retirement focuses an enabled visible recovery control`);
          assert.equal(Boolean(focus.disabled), false);
          const result = get('#operations-email-test [data-email-test="result"]');
          assert.equal(result.hidden, false);
          assert.equal(result.getAttribute('aria-live'), 'polite');
          assert.match(result.textContent, /Retired the unrecorded client request/);
          assert.match(result.textContent, readiness === 'blocked' ? /Configure the System Postmark credential/ : /Saved test-email context is unavailable/);
          const stored = JSON.parse(dom.window.sessionStorage.getItem(key));
          assert.equal(stored.operationId, operationId); assert.equal(stored.retired, true);
          assert.equal(calls.filter(call => call.init.method === 'POST').length, 0);
          if (loading) { releaseTables({ items: [] }); await loading; }
          controller.dispose();
        } finally { Date.now = originalNow; }
      });
    }
  }
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await loadOperationsController(load);
    const identity = createSessionIdentity(), owner = identity.accept(actor('visible-current-test'));
    const currentId = '9007199254740993', createdUtc = '2026-11-03T13:00:00Z';
    const failures = Array.from({ length: 500 }, (_, index) => ({ id: String(1000 + index),
      organizationId: 1, status: 'failed', deliveryClass: 'business_event', lastErrorCode: 'retained_failure',
      createdUtc: '2026-11-02T13:00:00Z', canRetry: false }));
    const calls = [];
    let queued = false, included = false, status = 'pending';
    const exact = () => ({ id: currentId, organizationId: 1, status, deliveryClass: 'operational_test',
      createdUtc, deliveryMode: 'postmark', lastErrorCode: status === 'failed' ? 'mail_not_configured' : null,
      canRetry: true }); // Exact-read responses never provide retry authority.
    const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
      announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {}, request: async (path, init = {}) => {
        calls.push({ path, init });
        const url = new URL(path, 'https://localhost');
        if (init.method === 'POST') { queued = true; return committedOperationResult(path); }
        if (url.pathname.endsWith('/organizations')) return { data: organizationCatalog };
        if (url.pathname.endsWith(`/email-operations/${currentId}`)) return { code: 'ok', data: exact() };
        if (url.pathname.endsWith('/email-operations')) {
          const show = queued && included && !url.searchParams.has('organizationId') && identity.preferences() === owner;
          return { items: show ? [{ ...exact(), version: 'fresh-listed-version', canRetry: status === 'failed' }, ...failures.slice(0, 499)] : failures };
        }
        return { items: [] };
      } });
    controller.setStaff(owner); controller.activate(); await controller.refresh();
    assert.equal(get('#email-operations-table tbody').rows.length, 500);
    const beforeSend = calls.length;
    get('#send-test-email').click(); await settle();
    const rows = () => [...get('#email-operations-table tbody').rows];
    assert.equal(rows().length, 501, 'one exact current test supplements the bounded failed-first log');
    assert.equal(rows()[0].cells[0].textContent, currentId, 'the newly queued current test appears first without another click');
    assert.match(rows()[0].textContent, /Queued.*operational_test/);
    assert.equal(rows()[0].querySelector('time').getAttribute('datetime'), createdUtc);
    assert.equal(rows()[0].querySelector('button'), null);
    assert.deepEqual(rows().slice(1).map(row => row.cells[0].textContent), failures.map(row => row.id), 'normal failed-first row order is retained');
    assert.ok(calls.slice(beforeSend).some(call => call.init.method !== 'POST' && new URL(call.path, 'https://localhost').pathname.endsWith('/email-operations')));
    included = true; await controller.refresh();
    assert.equal(rows().length, 500);
    assert.equal(rows().filter(row => row.cells[0].textContent === currentId).length, 1, 'a reference already in the fresh list is not duplicated');
    status = 'failed'; included = false; await controller.refresh();
    assert.equal(rows()[0].querySelector('button'), null, 'exact-only failure evidence cannot invent a retry snapshot');
    included = true; await controller.refresh();
    assert.equal(rows().filter(row => row.cells[0].textContent === currentId).length, 1);
    assert.equal(rows()[0].querySelector('button').textContent.trim(), 'Retry', 'fresh listed retry proof stays on its complete listed row');
    assert.equal(calls.filter(call => call.init.method === 'POST').length, 1, 'submission and follow-up reads create one test request');
    get('#email-view-scope').value = '2'; get('#email-view-scope').dispatchEvent(new dom.window.Event('change'));
    assert.doesNotMatch(get('#email-operations-table').textContent, new RegExp(currentId), 'scope transitions immediately retire the exact row');
    await controller.refresh();
    assert.doesNotMatch(get('#email-operations-table').textContent, new RegExp(currentId));
    await controller.openEmailScope(1);
    assert.match(get('#email-operations-table').textContent, new RegExp(currentId));
    const replacement = identity.accept(actor('different-current-test-owner')); controller.setStaff(replacement);
    assert.doesNotMatch(get('#email-operations-table').textContent, new RegExp(currentId), 'account transitions retire the prior exact row');
    await controller.refresh();
    assert.doesNotMatch(get('#email-operations-table').textContent, new RegExp(currentId), 'a later load cannot reuse another account’s exact result');
    controller.dispose();
  });
  for (const update of ['poll', 'replacement', 'audit-poll', 'audit-failure']) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createOperationsController } = await loadOperationsController(load);
      const identity = createSessionIdentity(), owner = identity.accept(actor('latest-visible-test'));
      const key = `asap.staff.email-test.${encodeURIComponent(identity.actor().key)}.1`;
      const id = '9007199254740993', operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
      dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, id, pending: true, status: 'pending' }));
      let timerId = 0, tick = 0;
      const timers = new Map();
      dom.window.setTimeout = (callback, delay = 0) => {
        const token = ++timerId; timers.set(token, { callback, at: tick + delay }); return token;
      };
      dom.window.clearTimeout = token => timers.delete(token);
      const advance = milliseconds => {
        tick += milliseconds;
        for (const [token, timer] of [...timers]) {
          if (timer.at <= tick) { timers.delete(token); timer.callback(); }
        }
      };
      let releaseTable, status = 'pending', observations = 0;
      const table = new Promise(resolve => { releaseTable = resolve; });
      const calls = [];
      const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
        announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {}, request: async (path, init = {}) => {
          calls.push({ path, init });
          if (path.endsWith('/organizations')) return { data: organizationCatalog };
          if (path.endsWith('/email-operations')) return update.startsWith('audit-') ? { items: [] } : table;
          if (path.includes('/staff/audit?')) {
            await table;
            if (update === 'audit-failure') throw new Error('Audit read failed');
            return { code: 'ok', data: { items: [], count: 0, page: 1, pageSize: 25, actors: [], libraries: [] } };
          }
          if (path.includes(`/email-operations/${id}?`)) {
            observations += 1;
            return { code: 'ok', data: { id, organizationId: 1, status, deliveryMode: 'postmark',
              deliveryClass: 'operational_test', createdUtc: '2026-11-03T13:00:00Z' } };
          }
          return { items: [] };
        } });
      controller.setStaff(owner); controller.activate(update.startsWith('audit-') ? { section: 'audit' } : undefined); await settle();
      const loading = controller.refresh(); await settle();
      assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).status, 'pending');
      const beforePoll = observations;
      if (update !== 'replacement') {
        status = 'sent'; advance(2000); await settle();
        assert.equal(observations, beforePoll + 1, 'useful automatic observation remains enabled while table reads are slow');
        assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).status, 'sent');
      } else {
        dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId: '2d52da70-c805-4e99-ac52-b2f30cab4120',
          id: '9007199254740995', pending: true, status: 'pending' }));
      }
      releaseTable({ items: [] }); await loading;
      if (update !== 'replacement') {
        const row = get('#email-operations-table tbody tr');
        assert.equal(row.cells[0].textContent, id);
        assert.match(row.textContent, /Provider accepted/);
        assert.doesNotMatch(row.textContent, /Queued/);
        assert.equal(row.querySelector('button'), null);
      } else {
        assert.doesNotMatch(get('#email-operations-table').textContent, new RegExp(id), 'a storage-replaced operation cannot fall back to the captured old row');
        assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).operationId, '2d52da70-c805-4e99-ac52-b2f30cab4120');
      }
      assert.equal(calls.filter(call => call.init.method === 'POST').length, 0, 'polling and table rendering are read-only');
      controller.dispose();
    });
  }
  console.log('Operations controller: canonical command outcomes and exact read-only test recovery passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
