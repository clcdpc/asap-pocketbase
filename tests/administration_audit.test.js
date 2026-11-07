const assert = require('node:assert/strict');
const { fixture, emailHistoryPage } = require('./helpers/staff-controller.cjs');
const settle = async () => { for (let i = 0; i < 8; i++) await new Promise(resolve => setImmediate(resolve)); };
const staff = (id = 'admin', role = 'super_admin', organizationId = 1) => ({ id, role, organizationId,
  tenantId: 'tenant', authenticationEmail: `${id}@example.org`, version: 'v1' });
const row = id => ({ id, actorStaffUserId: '9007199254740993', actorName: '<script>recorded actor</script>',
  organizationId: 2, scopeName: '<b>Library</b>', action: 'staff_created', summary: 'Staff account added', category: 'staff',
  targetType: 'StaffUser', targetId: '47', recordedTarget: '<img src=x onerror=alert(1)>',
  createdUtc: '2026-11-01T06:30:00Z', details: [{ label: 'role', value: '<b>admin</b>' }] });
const response = (rows = [row('9007199254740995')], extra = {}) => ({ code: 'ok', data: { items: rows,
  page: 1, pageSize: 25, count: rows.length, actors: [{ id: '9007199254740993', name: '<script>recorded actor</script>' }],
  libraries: [{ id: 2, name: 'Library Two' }, { id: 3, name: 'Library Three' }], ...extra } });

(async () => {
  const priorTimezone = process.env.TZ; process.env.TZ = 'America/New_York';
  try {
    await fixture(async ({ load }) => {
      const { auditDateBounds } = await load('administration-audit');
      assert.deepEqual(auditDateBounds('2026-03-08', '2026-03-08'), { fromUtc: '2026-03-08T05:00:00.000Z', beforeUtc: '2026-03-09T04:00:00.000Z' });
      assert.deepEqual(auditDateBounds('2026-11-01', '2026-11-01'), { fromUtc: '2026-11-01T04:00:00.000Z', beforeUtc: '2026-11-02T05:00:00.000Z' });
      assert.throws(() => auditDateBounds('2026-02-30', ''), /valid/);
      assert.throws(() => auditDateBounds('2026-11-02', '2026-11-01'), /before/);
    });
  } finally { if (priorTimezone === undefined) delete process.env.TZ; else process.env.TZ = priorTimezone; }

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createAdministrationAudit } = await load('administration-audit');
    const identity = createSessionIdentity(), owner = identity.accept(staff());
    const reads = [], routes = [];
    const audit = createAdministrationAudit({ root: get('#operations-view'), sessionIdentity: identity,
      onRouteChange: () => routes.push(audit.route()), request: async url => {
        reads.push(new URL(url, 'https://localhost'));
        const query = reads.at(-1).searchParams;
        if (query.get('search') === 'missing') return response([]);
        if (query.get('search') === 'failure') throw new Error('Audit dependency failed');
        return response([row(query.get('page') === '2' ? '9007199254740994' : '9007199254740995')],
          { count: 60, page: Number(query.get('page')), actorsTruncated: true });
      } });
    audit.setStaff(owner); audit.activate(); await audit.refresh();
    assert.deepEqual([...get('#audit-results').querySelectorAll('th')].map(node => node.textContent), ['When', 'Performed by', 'Library / scope', 'Change']);
    assert.match(get('#audit-results').textContent, /recorded actor/);
    assert.equal(get('#audit-results').querySelectorAll('img,script,b').length, 0, 'all recorded fields are text');
    assert.match(get('#audit-results').textContent, /9007199254740995/);
    assert.match(get('#audit-count').textContent, /60 matching entries/);
    const details = get('#audit-results details'); details.open = true;
    details.querySelector('summary').dispatchEvent(new dom.window.KeyboardEvent('keydown', { bubbles: true, key: 'Escape' }));
    assert.equal(details.open, false); assert.equal(dom.window.document.activeElement, details.querySelector('summary'));
    get('#audit-next').click(); await settle();
    assert.equal(reads.at(-1).searchParams.get('page'), '2');
    assert.match(get('#audit-results').textContent, /9007199254740994/);
    assert.match(get('#audit-page').textContent, /Showing 26–50 of 60/);
    const change = (id, value) => { get(`#audit-${id}`).value = value; get(`#audit-${id}`).dispatchEvent(new dom.window.Event('change')); };
    change('category', 'staff'); await settle(); change('scope', 'system'); await settle();
    change('actor', '9007199254740993'); await settle(); change('sort', 'oldest'); await settle();
    change('from', '2026-11-01'); await settle(); change('through', '2026-11-01'); await settle();
    const query = reads.at(-1).searchParams;
    assert.equal(query.get('scope'), 'system'); assert.equal(query.get('category'), 'staff');
    assert.equal(query.get('actorId'), '9007199254740993'); assert.equal(query.get('sort'), 'oldest'); assert.equal(query.get('page'), '1');
    assert.ok(query.get('fromUtc')); assert.ok(query.get('beforeUtc'));
    get('#audit-search').value = 'missing'; get('#audit-filters').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    // Submit uses the committed search filter; input first owns the search value.
    get('#audit-search').dispatchEvent(new dom.window.Event('input'));
    await audit.refresh(); assert.match(get('#audit-results').textContent, /No audit entries/);
    get('#audit-search').value = 'failure'; get('#audit-search').dispatchEvent(new dom.window.Event('input')); await audit.refresh();
    assert.match(get('#audit-status').textContent, /dependency failed/); assert.equal(get('#audit-next').disabled, true);
    get('#audit-clear').click(); await settle();
    assert.equal(reads.at(-1).searchParams.get('scope'), 'all'); assert.equal(reads.at(-1).searchParams.get('category'), null);
    assert.equal(reads.at(-1).searchParams.get('actorId'), null); assert.equal(reads.at(-1).searchParams.get('fromUtc'), null);
    assert.equal(get('#audit-sort').value, 'newest'); assert.ok(routes.length >= 8);
    audit.dispose();
  });

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createAdministrationAudit } = await load('administration-audit');
    const identity = createSessionIdentity(), owner = identity.accept(staff());
    let resolveFirst, rejectSecond;
    const pending = [], audit = createAdministrationAudit({ root: get('#operations-view'), sessionIdentity: identity,
      request: (url, init) => new Promise((resolve, reject) => pending.push({ url, init, resolve, reject })) });
    audit.setStaff(owner); audit.activate(); const old = audit.refresh();
    resolveFirst = pending[0].resolve;
    get('#audit-scope').value = 'system'; get('#audit-scope').dispatchEvent(new dom.window.Event('change'));
    assert.equal(pending[0].init.signal.aborted, true); assert.equal(get('#audit-results').textContent.includes('recorded actor'), false);
    rejectSecond = pending[1].reject;
    const replacement = identity.accept(staff('other', 'admin', 3)); audit.setStaff(replacement);
    assert.equal(get('#audit-actor').options.length, 1); assert.equal(get('#audit-scope').options.length, 1);
    assert.equal(get('#audit-scope').value, '3'); assert.equal(get('#audit-scope').disabled, true);
    resolveFirst(response()); rejectSecond(new Error('Other scope failed')); await old; await settle();
    assert.equal(get('#audit-results').textContent.includes('recorded actor'), false);
    assert.equal(get('#audit-status').textContent, '', 'a late result/error cannot announce across an identity change');
    audit.setRoute({ scope: 'all', actor: '', category: 'staff' }); const own = audit.refresh();
    assert.equal(new URL(pending[2].url, 'https://localhost').searchParams.get('scope'), '3', 'URL values cannot broaden ordinary-admin scope');
    pending[2].resolve(response([], { libraries: [{ id: 3, name: 'Own library' }] })); await own;
    audit.dispose();
  });

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await load('operations-controller');
    const identity = createSessionIdentity(), owner = identity.accept(staff());
    const routes = [], reads = [];
    const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
      announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {}, onRouteChange: route => routes.push(route),
      request: async url => { reads.push(url);
        if (url.includes('/staff/audit')) return response();
        if (url.endsWith('/organizations')) return { data: [{ id: 2, name: 'Library', organizationCodeId: 2, isActive: true }] };
        if (new URL(url, 'https://localhost').pathname.endsWith('/email-operations')) return emailHistoryPage({ items: [] }, url);
        return { items: [] };
      } });
    controller.setStaff(owner); controller.activate(); await controller.refresh();
    get('#operations-workflow-tab').dispatchEvent(new dom.window.KeyboardEvent('keydown', { key: 'End', bubbles: true, cancelable: true })); await settle();
    assert.equal(get('#operations-audit-tab').getAttribute('aria-selected'), 'true');
    assert.equal(dom.window.document.activeElement, get('#operations-audit-tab'));
    assert.equal(get('#operations-workflow-panel').hidden, true); assert.equal(get('#operations-audit-panel').hidden, false);
    await controller.openEmailScope(1); assert.equal(get('#email-view-scope').value, 'system');
    assert.equal(get('#operations-scope').value, 'all', 'Email view scope does not redirect a workflow command');
    assert.equal(get('#operations-email-tab').getAttribute('aria-selected'), 'true');
    assert.ok(reads.some(url => new URL(url, 'https://localhost').searchParams.get('scope') === 'system'));
    await controller.openStaffAuditScope(null); assert.equal(get('#audit-scope').value, 'all'); assert.equal(get('#audit-category').value, 'staff');
    assert.equal(dom.window.document.activeElement, get('#audit-search'));
    assert.equal(routes.at(-1).audit.scope, 'all');
    controller.dispose();
  });
  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createAdministrationAudit } = await load('administration-audit');
    const identity = createSessionIdentity(), owner = identity.accept(staff());
    let pending = null, release;
    let revealedBoundaries = 0;
    get('#audit-results').scrollIntoView = () => { revealedBoundaries++; };
    const audit = createAdministrationAudit({ root: get('#operations-view'), sessionIdentity: identity,
      request: async url => {
        const page = Number(new URL(url, 'https://localhost').searchParams.get('page'));
        if (pending) { const wait = pending; pending = null; await wait; }
        return response([row('9007199254740995')], { page, count: 60 });
      } });
    audit.setStaff(owner); audit.activate(); await audit.refresh();
    pending = new Promise(resolve => { release = resolve; });
    get('#audit-next').focus(); get('#audit-next').click();
    assert.equal(dom.window.document.activeElement, get('#audit-results'), 'paging has a stable loading focus target');
    get('#audit-search').focus(); release(); await settle();
    assert.equal(dom.window.document.activeElement, get('#audit-search'), 'a late page cannot steal deliberate focus');
    get('#audit-next').focus(); get('#audit-next').click(); await settle();
    assert.equal(get('#audit-next').disabled, true);
    assert.equal(dom.window.document.activeElement, get('#audit-results'), 'last-page boundary retains results focus');
    get('#audit-prev').focus(); get('#audit-prev').click(); await settle();
    assert.equal(dom.window.document.activeElement, get('#audit-prev'), 'available Previous regains keyboard focus');
    get('#audit-prev').click(); await settle();
    assert.equal(get('#audit-prev').disabled, true);
    assert.equal(dom.window.document.activeElement, get('#audit-results'), 'first-page boundary retains results focus');
    assert.equal(revealedBoundaries, 2, 'unavailable boundaries reveal their current focused results');
    get('#audit-search').focus(); await audit.refresh();
    assert.equal(dom.window.document.activeElement, get('#audit-search'), 'a background audit read does not take focus');
    audit.dispose();
  });

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createOperationsController } = await load('operations-controller');
    const identity = createSessionIdentity(), owner = identity.accept(staff());
    let pending = null, release, fail = false;
    const controller = createOperationsController({ root: get('#operations-view'), sessionIdentity: identity,
      announce() {}, onScopeChange() {}, onReceipt() {}, clearReceipt() {}, request: async url => {
        if (url.includes('/workflow/queues')) {
          if (pending) { const wait = pending; pending = null; await wait; }
          if (fail) throw new Error('Queue unavailable');
        }
        if (new URL(url, 'https://localhost').pathname.endsWith('/email-operations')) return emailHistoryPage({ items: [] }, url);
        return url.endsWith('/organizations') ? { data: [] } : { items: [] };
      } });
    controller.setStaff(owner); controller.activate(); await controller.refresh();
    pending = new Promise(resolve => { release = resolve; });
    get('#refresh-operations').focus(); get('#refresh-operations').click();
    assert.equal(get('#refresh-operations').disabled, true);
    assert.equal(dom.window.document.activeElement, get('#operations-title'));
    release(); await settle();
    assert.equal(dom.window.document.activeElement, get('#refresh-operations'));
    pending = new Promise(resolve => { release = resolve; });
    get('#refresh-operations').click(); get('#operations-workflow-tab').focus();
    release(); await settle();
    assert.equal(dom.window.document.activeElement, get('#operations-workflow-tab'));
    get('#refresh-operations').focus(); fail = true; get('#refresh-operations').click(); await settle();
    assert.equal(dom.window.document.activeElement, get('#refresh-operations'), 'failed reads retain refresh focus');
    get('#operations-workflow-tab').focus(); await controller.refresh();
    assert.equal(dom.window.document.activeElement, get('#operations-workflow-tab'), 'background read does not take focus');
    controller.dispose();
  });
  console.log('administration_audit.test.js passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
