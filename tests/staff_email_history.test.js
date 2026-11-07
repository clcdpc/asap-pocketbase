const assert = require('node:assert/strict');
const { fixture, emailHistoryPage } = require('./helpers/staff-controller.cjs');
const settle = async () => { for (let i = 0; i < 10; i++) await new Promise(resolve => setImmediate(resolve)); };
const staff = () => ({ id: '7', tenantId: 'tenant', authenticationEmail: 'admin@example.org', role: 'super_admin', organizationId: 1, version: 'v1' });
const libraries = [{ id: 2, name: 'Library Two' }, { id: 3, name: 'Library Three' }];
const row = id => ({ id, organizationId: 2, status: 'failed', deliveryClass: 'business_event',
  typeLabel: 'Business event <b>recorded</b>', detailLabel: '<img src=x onerror=alert(1)>', lastErrorCode: 'mail_not_configured',
  createdUtc: '2026-11-01T06:30:00.1234567Z', canRetry: true, version: 'AAAAAAAAAAE=' });
const page = (url, items, extra = {}) => emailHistoryPage({ items, libraries, ...extra }, url);

(async () => {
  const priorTimezone = process.env.TZ;
  process.env.TZ = 'America/New_York';
  try {
    await fixture(async ({ load }) => {
      const { emailHistoryDateBounds } = await load('email-history');
      assert.deepEqual(emailHistoryDateBounds('2026-03-08', '2026-03-08'), { fromUtc: '2026-03-08T05:00:00.000Z', beforeUtc: '2026-03-09T04:00:00.000Z' });
      assert.deepEqual(emailHistoryDateBounds('2026-11-01', '2026-11-01'), { fromUtc: '2026-11-01T04:00:00.000Z', beforeUtc: '2026-11-02T05:00:00.000Z' });
      assert.deepEqual(emailHistoryDateBounds('2026-01-01', '2026-01-02'), { fromUtc: '2026-01-01T05:00:00.000Z', beforeUtc: '2026-01-03T05:00:00.000Z' });
      assert.throws(() => emailHistoryDateBounds('2026-02-30', ''), /valid/);
      assert.throws(() => emailHistoryDateBounds('2026-11-02', '2026-11-01'), /before/);
      assert.throws(() => emailHistoryDateBounds('', '9999-12-31'), /supported range/);
    });
  } finally { if (priorTimezone === undefined) delete process.env.TZ; else process.env.TZ = priorTimezone; }

  await fixture(async ({ load, get }) => {
    assert.equal(get('#email-status option[value="failed"]').textContent, 'Failed or unconfirmed');
    const { createSessionIdentity } = await load('session-identity');
    const { createEmailHistory } = await load('email-history');
    const identity = createSessionIdentity(), owner = identity.accept(staff());
    const cancelled = { id: '9007199254740993', organizationId: 2, status: 'pending', attemptCount: 0,
      lastErrorCode: 'pre_send_cancelled', detailLabel: 'Pre-send cancelled; send not started',
      deliveryClass: 'business_event', typeLabel: 'Business event', canRetry: false, createdUtc: '2026-11-01T06:30:00Z' };
    const history = createEmailHistory({ root: get('#operations-view'), sessionIdentity: identity, request: async url => {
      assert.equal(new URL(url, 'https://localhost').searchParams.get('search'), 'pre_send_cancelled');
      return page(url, [cancelled]);
    } });
    history.setStaff(owner); history.activate(); history.setRoute({ search: 'pre_send_cancelled' }); await history.refresh();
    assert.match(get('#email-operations-table').textContent, /Queued.*Pre-send cancelled; send not started/);
    assert.match(get('#email-count').textContent, /1 matching operation/);
    assert.equal(get('#email-operations-table button'), null);
    history.dispose();
  });

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createEmailHistory } = await load('email-history');
    const identity = createSessionIdentity(), owner = identity.accept(staff());
    const reads = [], routes = [], retries = [];
    const rows = Array.from({ length: 60 }, (_, index) => row((9007199254740993n + BigInt(index)).toString()));
    const history = createEmailHistory({ root: get('#operations-view'), sessionIdentity: identity,
      onRouteChange: () => routes.push(history.route()), onRetry: value => retries.push(value), request: async (url, init) => {
        reads.push({ url: new URL(url, 'https://localhost'), init });
        const query = reads.at(-1).url.searchParams;
        if (query.get('search') === 'missing') return page(url, [], { page: 1 });
        if (query.get('search') === 'failure') throw new Error('History dependency failed');
        const accepted = Math.min(Number(query.get('page')), 3);
        return page(url, rows.slice((accepted - 1) * 25, accepted * 25), { count: rows.length, page: accepted });
      } });
    history.setStaff(owner); history.activate(); await history.refresh();
    assert.match(get('#email-count').textContent, /60 matching operations/);
    assert.match(get('#email-page').textContent, /Showing 1–25 of 60.*Page 1/);
    assert.equal(get('#email-operations-table').querySelectorAll('tbody tr').length, 25);
    assert.match(get('#email-operations-table').textContent, /9007199254740993/);
    assert.equal(get('#email-operations-table').querySelectorAll('img,b,script').length, 0);
    assert.deepEqual([...get('#email-operations-table').querySelectorAll('th')].map(node => node.textContent), ['Reference', 'Created', 'Status', 'Type / detail', 'Action']);
    get('#email-operations-table button').click(); assert.equal(retries[0].id, '9007199254740993');
    get('#email-next').click(); await settle();
    assert.equal(reads.at(-1).url.searchParams.get('page'), '2');
    assert.match(get('#email-page').textContent, /Showing 26–50 of 60/);
    const change = (field, value) => { get(`#email-${field === 'scope' ? 'view-scope' : field}`).value = value; get(`#email-${field === 'scope' ? 'view-scope' : field}`).dispatchEvent(new dom.window.Event(field === 'search' ? 'input' : 'change')); };
    for (const [field, value] of [['scope', 'system'], ['status', 'suppressed'], ['type', 'operational_test'], ['sort', 'oldest'], ['from', '2026-11-01'], ['through', '2026-11-01']]) {
      change(field, value); await settle();
      assert.equal(reads.at(-1).url.searchParams.get('page'), '1', field);
    }
    const query = reads.at(-1).url.searchParams;
    for (const [key, expected] of [['scope', 'system'], ['status', 'suppressed'], ['type', 'operational_test'], ['sort', 'oldest']]) assert.equal(query.get(key), expected);
    assert.ok(query.get('fromUtc')); assert.ok(query.get('beforeUtc'));
    assert.match(get('#email-timezone').textContent, /Through includes the whole local day/);
    change('search', 'missing'); await history.refresh();
    assert.match(get('#email-count').textContent, /0 matching operations/);
    assert.match(get('#email-operations-table').textContent, /No email operations match/);
    change('search', 'failure'); await history.refresh();
    assert.match(get('#email-history-status').textContent, /History dependency failed/);
    assert.equal(get('#email-count').textContent, ''); assert.equal(get('#email-next').disabled, true);
    get('#email-clear').click(); await settle();
    const cleared = reads.at(-1).url.searchParams;
    assert.equal(cleared.get('scope'), 'all'); assert.equal(cleared.get('sort'), 'newest'); assert.equal(cleared.get('page'), '1');
    for (const field of ['search', 'status', 'type', 'fromUtc', 'beforeUtc', 'reference']) assert.equal(cleared.has(field), false, field);
    history.setRoute({ scope: '2', page: '999', reference: '9223372036854775807' }); await history.refresh();
    assert.equal(reads.at(-1).url.searchParams.get('reference'), '9223372036854775807');
    assert.equal(history.route().page, 3, 'accepted server page clamping becomes current route state');
    assert.equal(routes.at(-1).page, 3);
    assert.equal(get('#email-reference-context').hidden, false);
    assert.match(get('#email-reference-value').textContent, /9223372036854775807/);
    get('#email-reference-clear').click(); await settle();
    assert.equal(reads.at(-1).url.searchParams.has('reference'), false); assert.equal(history.route().page, 1);
    assert.equal(dom.window.document.activeElement, get('#email-search'));
    const beforeInvalid = reads.length;
    history.setRoute({ from: '2026-11-02', through: '2026-11-01' }); assert.equal(await history.refresh(), false);
    assert.equal(reads.length, beforeInvalid, 'invalid local date bounds never reach the API');
    assert.match(get('#email-history-status').textContent, /From must be on or before Through/);
    assert.ok(reads.every(read => !read.init.method || read.init.method === 'GET'), 'history never requests sending, retries, or retirement');
    history.dispose();
  });

  for (const outcome of ['success', 'failure']) {
    await fixture(async ({ load, get, dom }) => {
      const { createSessionIdentity } = await load('session-identity');
      const { createEmailHistory } = await load('email-history');
      const identity = createSessionIdentity(), owner = identity.accept(staff());
      const pending = [];
      const history = createEmailHistory({ root: get('#operations-view'), sessionIdentity: identity,
        request: (url, init) => new Promise((resolve, reject) => pending.push({ url, init, resolve, reject })) });
      history.setStaff(owner); history.activate(); const old = history.refresh();
      get('#email-sort').value = 'oldest'; get('#email-sort').dispatchEvent(new dom.window.Event('change'));
      assert.equal(pending[0].init.signal.aborted, true);
      assert.equal(get('#email-count').textContent, '');
      assert.equal(get('#email-view-scope').options.length, 2, 'old library choices are absent while loading');
      pending[1].resolve(page(pending[1].url, [row('9007199254740995')])); await settle();
      if (outcome === 'failure') pending[0].reject(new Error('Stale query failed'));
      else pending[0].resolve(page(pending[0].url, [row('9007199254740993')]));
      await old;
      assert.match(get('#email-operations-table').textContent, /9007199254740995/);
      assert.doesNotMatch(get('#email-history-status').textContent, /Stale/);
      assert.doesNotMatch(get('#email-operations-table').textContent, /9007199254740993/);
      const previousEpoch = history.refresh();
      identity.clear(); history.setStaff(null); const replacement = identity.accept(staff()); history.setStaff(replacement);
      assert.equal(pending[2].init.signal.aborted, true);
      const current = history.refresh();
      pending[3].resolve(page(pending[3].url, [row('9223372036854775807')])); await current;
      pending[2].reject(new Error('Prior session failed')); await previousEpoch;
      assert.match(get('#email-operations-table').textContent, /9223372036854775807/);
      assert.doesNotMatch(get('#email-history-status').textContent, /Prior session/);
      history.dispose();
    });
  }

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createEmailHistory } = await load('email-history');
    const identity = createSessionIdentity(), owner = identity.accept(staff());
    let held = null, release;
    const history = createEmailHistory({ root: get('#operations-view'), sessionIdentity: identity, request: async url => {
      if (held) { const wait = held; held = null; await wait; }
      return page(url, [row('9007199254740993')], { count: 60 });
    } });
    history.setStaff(owner); history.activate(); await history.refresh();
    held = new Promise(resolve => { release = resolve; });
    get('#email-next').focus(); get('#email-next').click();
    assert.equal(dom.window.document.activeElement, get('#email-operations-table'));
    get('#email-search').focus(); release(); await settle();
    assert.equal(dom.window.document.activeElement, get('#email-search'), 'a delayed pager cannot take focus back from deliberate input');
    get('#email-prev').focus(); get('#email-prev').click(); await settle();
    assert.equal(get('#email-prev').disabled, true);
    assert.equal(dom.window.document.activeElement, get('#email-operations-table'), 'unavailable page boundary keeps stable results focus');
    get('#email-next').focus(); get('#email-next').click(); await settle();
    assert.equal(dom.window.document.activeElement, get('#email-next'), 'an available pager regains keyboard focus');
    history.dispose();
  });

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createEmailHistory } = await load('email-history');
    const identity = createSessionIdentity(), owner = identity.accept(staff());
    const pending = [];
    let first = true;
    const history = createEmailHistory({ root: get('#operations-view'), sessionIdentity: identity, request: async (url, init) => {
      if (first) { first = false; return page(url, [row('9007199254740993')], { count: 60 }); }
      return new Promise((resolve, reject) => pending.push({ url, init, resolve, reject }));
    } });
    history.setStaff(owner); history.activate(); await history.refresh();
    get('#email-next').click();
    assert.equal(new URL(pending[0].url, 'https://localhost').searchParams.get('page'), '2');
    get('#email-view-scope').value = 'system'; get('#email-view-scope').dispatchEvent(new dom.window.Event('change'));
    assert.equal(pending[0].init.signal.aborted, true);
    assert.equal(new URL(pending[1].url, 'https://localhost').searchParams.get('page'), '1');
    pending[1].resolve(page(pending[1].url, [row('9223372036854775807')])); await settle();
    pending[0].resolve(page(pending[0].url, [row('9007199254740995')], { count: 60 })); await settle();
    assert.equal(get('#email-view-scope').value, 'system');
    assert.match(get('#email-page').textContent, /Page 1/);
    assert.match(get('#email-count').textContent, /1 matching operation/);
    assert.doesNotMatch(get('#email-operations-table').textContent, /9007199254740995/);
    history.dispose();
  });

  await fixture(async ({ load, get }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createEmailHistory } = await load('email-history');
    const identity = createSessionIdentity(), owner = identity.accept(staff());
    const listed = row('9007199254740993'), retries = [];
    let exact = null;
    const history = createEmailHistory({ root: get('#operations-view'), sessionIdentity: identity,
      selectSnapshot: value => exact || value, onRetry: value => retries.push(value), request: async url => page(url, [listed]) });
    history.setStaff(owner); history.activate(); await history.refresh();
    const staleButton = get('#email-operations-table button'); assert.ok(staleButton);
    exact = { id: listed.id, organizationId: 2, status: 'sent', deliveryClass: 'operational_test', typeLabel: 'Operational test',
      createdUtc: listed.createdUtc, deliveryMode: 'postmark', version: 'AAAAAAAAAAI=', canRetry: false };
    staleButton.click();
    assert.equal(retries.length, 0, 'a newer exact poll revokes a previously rendered retry button at dispatch');
    assert.match(get('#email-operations-table').textContent, /Provider accepted/);
    assert.doesNotMatch(get('#email-operations-table').textContent, /mail_not_configured/, 'newer complete evidence does not retain old detail');
    assert.equal(get('#email-operations-table button'), null);
    history.dispose();
  });

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await load('operations-controller');
    const identity = createSessionIdentity(), owner = identity.accept(staff());
    const id = '9007199254740993', operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
    const key = `asap.staff.email-test.${encodeURIComponent(identity.actor().key)}.2`;
    dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, id, status: 'pending', pending: true }));
    const reads = [], posts = [];
    const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
      announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {}, request: async (path, init = {}) => {
        if (init.method === 'POST') { posts.push(path); throw new Error('History must never send.'); }
        reads.push(path); const url = new URL(path, 'https://localhost');
        if (url.pathname.endsWith('/organizations')) return { data: libraries.map(item => ({ ...item, organizationCodeId: 2, isActive: true })) };
        if (url.pathname.endsWith('/test-context')) return { code: 'ok', data: { organizationId: Number(url.searchParams.get('organizationId')),
          scopeName: 'Saved scope', recipient: 'admin@example.org', fromAddress: 'saved@example.org', deliveryMode: 'postmark', state: 'ready', configurationVersion: 'A'.repeat(64) } };
        if (url.pathname.endsWith(`/email-operations/${id}`)) return { code: 'ok', data: { id, organizationId: 2, deliveryClass: 'operational_test',
          status: 'failed', lastErrorCode: 'provider_timeout', version: 'AAAAAAAAAAI=' } };
        if (url.pathname.endsWith('/email-operations')) return page(path, []);
        return { items: [] };
      } });
    controller.setStaff(owner); controller.activate(); await controller.openEmailScope(2);
    assert.equal(get('#email-test-scope').value, '2');
    assert.match(get('#operations-email-test').textContent, new RegExp(id));
    assert.equal(get('#send-test-email').disabled, true);
    const exactReads = reads.filter(path => path.includes(`/email-operations/${id}?`)).length;
    get('#email-view-scope').value = 'system'; get('#email-view-scope').dispatchEvent(new dom.window.Event('change')); await settle();
    get('#email-status').value = 'sent'; get('#email-status').dispatchEvent(new dom.window.Event('change')); await settle();
    get('#email-clear').click(); await settle();
    assert.equal(get('#email-test-scope').value, '2', 'history changes and Clear preserve explicit saved test scope');
    assert.equal(controller.currentRoute().testScope, '2');
    assert.equal(reads.filter(path => path.includes(`/email-operations/${id}?`)).length, exactReads, 'history controls do not restart exact test observation');
    assert.equal(get('#send-test-email').disabled, true);
    assert.equal(get('#email-operations-table').textContent.includes(id), false, 'filtered-out recovery is never inserted into counted rows');
    await controller.refresh();
    assert.ok(reads.filter(path => path.includes(`/email-operations/${id}?`)).length > exactReads, 'the main Refresh reconciles the original test');
    assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).operationId, operationId);
    assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).unconfirmed, false);
    assert.equal(posts.length, 0);
    dom.window.sessionStorage.removeItem(key);
    const setItem = dom.window.Storage.prototype.setItem;
    try {
      dom.window.Storage.prototype.setItem = () => { throw new Error('Storage unavailable'); };
      get('#email-test-scope').value = '1'; get('#email-test-scope').dispatchEvent(new dom.window.Event('change'));
      assert.equal(get('#email-test-scope').value, '2', 'explicit test-scope departure cannot drop the only unresolved reference when retention fails');
      assert.equal(get('#send-test-email').disabled, true);
      assert.match(get('#operations-email-test').textContent, /recovery reference could not be retained/);
    } finally { dom.window.Storage.prototype.setItem = setItem; }
    controller.dispose();
  });

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await load('operations-controller');
    const identity = createSessionIdentity(), owner = identity.accept(staff());
    const id = '9007199254740993', operationId = '130e5f99-b59c-4e39-9718-bec1e7e5d94d';
    const key = `asap.staff.email-test.${encodeURIComponent(identity.actor().key)}.1`;
    dom.window.sessionStorage.setItem(key, JSON.stringify({ operationId, id, status: 'failed', lastErrorCode: 'mail_not_configured',
      pending: false, version: 'AAAAAAAAAAE=' }));
    let posts = 0;
    const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
      announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {}, request: async (path, init = {}) => {
        if (init.method === 'POST') { posts++; throw new Error('No dispatch allowed.'); }
        const url = new URL(path, 'https://localhost');
        if (url.pathname.endsWith('/organizations')) return { data: [] };
        if (url.pathname.endsWith('/test-context')) return { code: 'ok', data: { organizationId: 1, scopeName: 'System',
          deliveryMode: 'postmark', state: 'ready', configurationVersion: 'A'.repeat(64) } };
        if (url.pathname.endsWith(`/email-operations/${id}`)) throw Object.assign(new Error('Exact status unavailable'), { status: 503 });
        if (url.pathname.endsWith('/email-operations')) return page(path, [{ ...row(id), organizationId: 1, deliveryClass: 'operational_test' }]);
        return { items: [] };
      } });
    controller.setStaff(owner); controller.activate(); await controller.refresh();
    assert.equal(JSON.parse(dom.window.sessionStorage.getItem(key)).unconfirmed, true);
    assert.equal(get('#send-test-email').disabled, true);
    assert.equal(get('#email-operations-table button'), null, 'an unreadable exact lookup cannot restore remembered retry permission from a stale certified list row');
    assert.match(get('#email-count').textContent, /1 matching operation/);
    assert.equal(posts, 0);
    controller.dispose();
  });
  console.log('Email history server queries, local dates, exact references, paging, epochs, focus and independent test recovery passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
