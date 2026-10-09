const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
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
        const { createOperationsController } = await load('operations-controller');
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
        const controls = ['#run-workflow-now', '#run-weekly-now', '#force-weekly-now', '#send-test-email', '#operations-scope'];
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
      const { createOperationsController } = await load('operations-controller');
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
      assert.equal(get('#send-test-email').disabled, true, 'exact actor revision restores command guard');
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
      assert.equal(get('#send-test-email').disabled, false);
      second.dispose();
    });
  }
  for (const outcome of ['committed', 'uncertain']) {
    for (const replacement of ['newer', 'foreign']) {
      await fixture(async ({ load, get, dom }) => {
        const { createSessionIdentity } = await load('session-identity');
        const { createOperationsController } = await load('operations-controller');
        const session = createSessionIdentity(); session.accept(sameIdActor);
        let complete, reject;
        const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: session,
          announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {},
          request: () => new Promise((resolve, fail) => { complete = resolve; reject = fail; }) });
        controller.setStaff(session.preferences()); controller.activate();
        const saving = controller.run('/api/asap/staff/email-operations/test', 'Pending operation');
        const key = `asap.staff.operation.${sameIdActor.tenantId}.${sameIdActor.id}.${encodeURIComponent(session.actor().key)}`;
        const saved = JSON.parse(dom.window.sessionStorage.getItem(key));
        // Keeping operationId identical reproduces the old overly broad cleanup.
        const next = JSON.stringify({ ...saved, ...(replacement === 'foreign'
          ? { actorKey: 'another-actor' } : { message: 'Newer record' }) });
        dom.window.sessionStorage.setItem(key, next); controller.dispose();
        if (outcome === 'committed') complete({ code: 'queued' });
        else reject(new Error('Response lost'));
        await saving;
        assert.equal(dom.window.sessionStorage.getItem(key), next, `${outcome} cannot remove or rewrite a ${replacement} record`);
      });
    }
  }
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await load('operations-controller');
    const session = createSessionIdentity(); session.accept(sameIdActor);
    const pending = [];
    const options = { root: get('#operations-view'), sessionIdentity: session, announce() {},
      onScopeChange() {}, onReceipt() {}, clearReceipt() {}, request: async (path, init = {}) => {
        if (init.method === 'POST') return new Promise(resolve => pending.push(resolve));
        return path.endsWith('/organizations') ? { data: organizationCatalog } : { items: [] };
      } };
    const first = createOperationsController(options); first.setStaff(session.preferences()); first.activate();
    const saving = first.run('/api/asap/staff/email-operations/test', 'Reloaded operation'); first.dispose();
    const key = `asap.staff.operation.${sameIdActor.tenantId}.${sameIdActor.id}.${encodeURIComponent(session.actor().key)}`;
    const original = JSON.parse(dom.window.sessionStorage.getItem(key));
    const second = createOperationsController(options); second.setStaff(session.preferences()); second.activate(); await second.refresh();
    get('#operations-outcome button').click();
    const newerRaw = dom.window.sessionStorage.getItem(key), newer = JSON.parse(newerRaw);
    assert.equal(newer.operationId, original.operationId, 'reload retry preserves immutable command identity');
    assert.notEqual(newer.recordId, original.recordId, 'each dispatch owns distinct durable evidence');
    pending[0]({ code: 'queued' }); await saving;
    assert.equal(dom.window.sessionStorage.getItem(key), newerRaw, 'pre-reload completion cannot clear a newer retry of the same operation');
    pending[1]({ code: 'queued' }); await new Promise(resolve => setImmediate(resolve));
    assert.equal(dom.window.sessionStorage.getItem(key), null, 'new retry clears its own exact record');
    second.dispose();
  });
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await load('operations-controller');
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
    assert.deepEqual([...get('#operations-scope').options].map(option => option.value), ['all', '2', '3', '4'],
      'active code-2 libraries remain selectable while system, branch, inactive and unclassified rows stay excluded');
    assert.equal(get('#operations-outcome button'), null,
      'a retained command scoped to an active branch is not reviewable as a library operation');
    assert.equal(paths.some(path => path.includes('organizationId=20')), false,
      'branch scope cannot trigger exact command review reads');
    assert.equal(dom.window.sessionStorage.getItem(key), JSON.stringify(saved),
      'filtered presentation preserves the captured command without authorizing it');
    controller.dispose();
  });
  const status200Commands = [
    { selector: '#run-workflow-now', path: '/api/asap/staff/workflow/run-now', message: 'Workflow run' },
    { selector: '#run-weekly-now', path: '/api/asap/staff/workflow/weekly-summary/run-now?force=false', message: 'Weekly summary' },
    { selector: '#send-test-email', path: '/api/asap/staff/email-operations/test', message: 'Test email' },
    { retry: true, path: '/api/asap/staff/email-operations/71/retry', message: 'Email retry 71', body: { version: 'email-v1' } }
  ];
  for (const command of status200Commands) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createOperationsController } = await load('operations-controller');
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
        const row = [...get('#email-operations-table').querySelectorAll('tr')]
          .find(item => item.textContent.includes('71'));
        assert.ok(row, 'retry-email witness loads the captured failed row');
        row.querySelector('button').click();
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
  console.log('Operations controller: 32 fixtures passed, including four status-200 unknown command outcomes');
})().catch(error => { console.error(error); process.exitCode = 1; });
