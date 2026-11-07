const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const staff = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1 };
const pending = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { resolve, reject, promise }; };
const tick = () => new Promise(done => setTimeout(done, 25));
const response = (query = '', scope = '2', total = 0, title = 'Current <img src=x>') => ({ query, scope, total, filteredTotal: total, page: 1, pageSize: 12,
  libraries: [{ id: 2, name: 'Library A' }, { id: 3, name: 'Library B' }], groups: [{ key: 'title:closed', count: total }],
  items: total ? [{ type: 'title', id: '9007199254741467', status: 'closed', title, patron: 'Ada O’Brien', libraryOrgName: 'Library A' }] : [] });

async function setup(run) {
  await fixture(async ({ load, get, dom }) => {
    const { createSubmissionSearch } = await load('search');
    const { createSessionIdentity } = await load('session-identity');
    const identity = createSessionIdentity(); identity.accept(staff);
    const calls = [], opened = []; let all = 0, openCompletion = null;
    const search = createSubmissionSearch({ dialog: get('#search-dialog'), view: get('#search-view'), launcher: get('#search-asap'),
      detailLauncher: get('#detail-search-asap'), sessionIdentity: identity, getScope: () => '2', announce() {}, debounceMs: 10,
      openResult: async intent => { opened.push(intent); return openCompletion ? openCompletion.promise : true; }, openAll: async () => { all++; search.activate(); },
      request: (path, init) => { const next = pending(); calls.push({ path, init, ...next }); return next.promise; } });
    const input = get('#search-quick-input');
    const type = text => { input.value = text; input.dispatchEvent(new dom.window.Event('input', { bubbles: true })); };
    const key = (key, extra = {}) => input.dispatchEvent(new dom.window.KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...extra }));
    try { await run({ search, identity, calls, opened, get, dom, input, type, key, all: () => all, holdOpen: () => (openCompletion = pending()) }); }
    finally { search.dispose(); }
  });
}
(async () => {
  await setup(async ({ search, calls, get, input, type }) => {
    search.open(); assert.equal(document.activeElement, input);
    calls[0].resolve(response()); await tick();
    assert.match(get('#search-dialog [data-search="summary"]').textContent, /Enter a title/);
    type('old'); type('new'); await tick();
    assert.equal(calls.length, 2, 'debounce makes one latest request');
    assert.equal(calls[1].path, '/api/asap/staff/search'); assert.equal(calls[1].init.method, 'POST');
    assert.equal(calls[1].init.body.query, 'new');
    type('current'); await tick();
    assert.equal(calls[1].init.signal.aborted, true);
    calls[2].resolve(response('current', '2', 1)); await tick();
    calls[1].resolve(response('new', '2', 1, 'Stale')); await tick();
    assert.match(get('#search-suggestions').textContent, /Current <img src=x>/);
    assert.equal(get('#search-suggestions img'), null, 'runtime text never becomes markup');
    type('failure-old'); await tick(); type('success-new'); await tick();
    calls[4].resolve(response('success-new', '2', 1, 'Newest')); await tick();
    calls[3].reject(new Error('Old failure')); await tick();
    assert.match(get('#search-suggestions').textContent, /Newest/);
    assert.doesNotMatch(get('#search-dialog [data-search="summary"]').textContent, /unavailable/);
    type('failed'); await tick(); calls[5].reject(new Error('Current failure')); await tick();
    assert.match(get('#search-dialog [data-search="summary"]').textContent, /Search unavailable/);
    get('#search-dialog [data-search="retry"]').click();
    assert.equal(calls[6].init.body.query, 'failed'); assert.equal(calls[6].init.body.scope, '2');
    calls[6].resolve(response('failed')); await tick();
    assert.match(get('#search-dialog [data-search="summary"]').textContent, /No matching submissions/);
    type(Array(14).fill('word').join(' ')); await tick();
    assert.equal(calls.length, 7); assert.match(get('#search-dialog [data-search="summary"]').textContent, /12 words/);
  });
  await setup(async ({ search, calls, get, identity, type }) => {
    search.open(); calls[0].resolve(response()); await tick();
    type('private'); await tick();
    get('#search-quick-scope').value = '3'; get('#search-quick-scope').dispatchEvent(new window.Event('change'));
    assert.equal(calls[1].init.signal.aborted, true); assert.equal(get('#search-suggestions').textContent, '');
    calls[2].resolve(response('private', '3', 1, 'Scope B')); await tick();
    calls[1].resolve(response('private', '2', 1, 'Scope A')); await tick();
    assert.match(get('#search-suggestions').textContent, /Scope B/);
    type('closing'); await tick(); search.close();
    assert.equal(calls[3].init.signal.aborted, true);
    calls[3].resolve(response('closing', '3', 1, 'Closed stale')); await tick();
    assert.doesNotMatch(get('#search-suggestions').textContent, /Closed stale/);
    search.open(); identity.accept({ ...staff, id: '21' }); search.setStaff();
    calls[4].resolve(response('closing', '3', 1, 'Old account')); await tick();
    assert.equal(get('#search-quick-input').value, ''); assert.equal(get('#search-suggestions').textContent, '');
    search.signedOut(); identity.clear();
    assert.equal(get('#search-dialog').open, false); assert.equal(get('#search-results-input').value, '');
  });
  await setup(async ({ search, calls, get, dom, input, type, key, opened, all }) => {
    const shortcut = () => document.dispatchEvent(new dom.window.KeyboardEvent('keydown', { key: 'k', ctrlKey: true, bubbles: true, cancelable: true }));
    document.dispatchEvent(new dom.window.KeyboardEvent('keydown', { key: 'k', ctrlKey: true, isComposing: true, bubbles: true }));
    assert.equal(get('#search-dialog').open, false);
    shortcut(); calls[0].resolve(response()); await tick(); type('needle'); await tick();
    calls[1].resolve(response('needle', '2', 37)); await tick();
    key('ArrowDown'); assert.equal(input.getAttribute('aria-activedescendant'), 'search-option-0');
    key('Enter', { isComposing: true }); await tick(); assert.equal(opened.length, 0);
    get('#search-dialog [data-search="all"]').click(); await tick();
    assert.equal(all(), 1); assert.equal(calls[2].init.body.query, 'needle'); assert.equal(calls[2].init.body.scope, '2');
    calls[2].resolve({ ...response('needle', '2', 37), pageSize: 25 }); await tick();
    get('#search-view [data-search="next"]').click(); assert.equal(calls[3].init.body.page, 2);
    get('#search-result-filter').value = 'title:closed'; get('#search-result-filter').dispatchEvent(new window.Event('change'));
    assert.equal(calls[3].init.signal.aborted, true); assert.equal(calls[4].init.body.page, 1); assert.equal(calls[4].init.body.group, 'title:closed');
    calls[4].resolve({ ...response('needle', '2', 37), filteredTotal: 2, pageSize: 25 }); await tick();
    calls[3].resolve({ ...response('needle', '2', 37, 'Wrong page'), page: 2, pageSize: 25 }); await tick();
    assert.doesNotMatch(get('#search-view [data-search="list"]').textContent, /Wrong page/);
    search.open(); calls[5].resolve(response('needle', '2', 1)); await tick();
    key('ArrowUp'); key('Enter'); await tick();
    assert.equal(opened[0].id, '9007199254741467'); assert.equal(opened[0].scope, '2');
    assert.equal(get('#search-dialog').open, false); assert.equal(document.activeElement, get('#search-asap'));
    assert.equal(new URL(window.location.href).searchParams.has('query'), false);
    search.open(); calls[6].resolve(response()); await tick();
    key('Escape', { isComposing: true }); assert.equal(get('#search-dialog').open, true);
    key('Escape'); assert.equal(get('#search-dialog').open, false);
    assert.equal(document.activeElement, get('#search-asap'));
    get('#request-dialog').showModal(); shortcut(); assert.equal(get('#search-dialog').open, false, 'shortcut defers to competing modal');
  });
  await setup(async ({ search, calls, get, dom, opened, holdOpen }) => {
    search.activate(); calls[0].resolve(response()); await tick();
    const input = get('#search-results-input'); input.value = 'needle'; input.dispatchEvent(new dom.window.Event('input')); await tick();
    calls[1].resolve({ ...response('needle', '2', 37), pageSize: 25 }); await tick();
    const completion = holdOpen(); get('#search-view .search-result').click(); await tick();
    assert.equal(opened[0].isCurrent(), true);
    get('#search-results-scope').value = '3'; get('#search-results-scope').dispatchEvent(new dom.window.Event('change'));
    assert.equal(opened[0].signal.aborted, true); assert.equal(opened[0].isCurrent(), false);
    const count = calls.length; completion.resolve(false); await tick();
    assert.equal(calls.length, count, 'an obsolete detail rejection cannot restart a newer scoped search');
    calls[2].resolve({ ...response('needle', '3', 37), pageSize: 25 }); await tick();
    get('#search-view [data-search="next"]').click(); calls[3].resolve({ ...response('needle', '3', 37), page: 2, pageSize: 25 }); await tick();
    search.open(); assert.equal(calls[4].init.body.scope, '3', 'full results own their selected scope when reopening preview');
    calls[4].resolve(response('needle', '3', 37)); await tick();
    search.close(); assert.equal(calls[5].init.body.pageSize, 25, 'closing preview over full results restores full-page contract');
    assert.equal(calls[5].init.body.page, 2, 'preview does not replace a retained full-results page');
    assert.equal(calls[5].init.body.scope, '3', 'closing preview preserves explicit full-results scope');
    calls[5].resolve({ ...response('needle', '3', 37), page: 2, pageSize: 25 }); await tick();
  });
  await setup(async ({ search, calls, get, key, opened, type }) => {
    search.open(); calls[0].resolve(response()); await tick(); type('needle'); await tick();
    const value = response('needle', '2', 2);
    value.groups = [{ key: 'title:closed', count: 1 }, { key: 'copy:open', count: 1 }];
    value.items = [{ ...value.items[0], type: 'copy', status: 'open', title: 'Exact copy', patron: null }, value.items[0]];
    calls[1].resolve(value); await tick();
    assert.match(get('#search-option-0').textContent, /Exact copy/);
    key('ArrowDown'); key('Enter'); await tick();
    assert.equal(opened[0].type, 'copy', 'group presentation follows the server-ranked first result');
  });
  await fixture(async ({ load, get, dom }) => {
    const { createSubmissionSearch } = await load('search'), http = await load('http');
    const { createSessionIdentity } = await load('session-identity');
    const identity = createSessionIdentity(); identity.accept(staff);
    const originalFetch = global.fetch, delayed = pending();
    let currentStaff = staff, controller, invalidations = 0, posted = false;
    global.fetch = async (url, options = {}) => {
      if (url.endsWith('/session')) return { ok: true, status: 200, json: async () => ({ authenticated: true,
        accessAllowed: true, staff: currentStaff, antiforgeryToken: 'token' }) };
      assert.equal(url, '/api/asap/staff/search'); assert.equal(options.method, 'POST'); assert.equal(options.cache, 'no-store');
      const body = JSON.parse(options.body); if (!body.query) return { ok: true, status: 200, json: async () => response() };
      posted = true; await delayed.promise;
      return { ok: true, status: 200, json: async () => response(body.query, '2', 1, 'Old account private result') };
    };
    const unsubscribe = http.onSessionInvalid(() => { invalidations++; controller.signedOut(); identity.clear(); });
    try {
      await http.loadStaffSession();
      controller = createSubmissionSearch({ dialog: get('#search-dialog'), view: get('#search-view'), launcher: get('#search-asap'),
        sessionIdentity: identity, getScope: () => '2', openResult() {}, openAll() {}, announce() {}, debounceMs: 10 });
      controller.open(); await tick();
      const input = get('#search-quick-input'); input.value = 'private'; input.dispatchEvent(new dom.window.Event('input')); await tick();
      assert.equal(posted, true); currentStaff = { ...staff, id: '21' }; delayed.resolve(); await tick();
      assert.equal(invalidations, 1, 'read-only POST rechecks session after its response');
      assert.equal(identity.actor(), null); assert.equal(input.value, ''); assert.equal(get('#search-dialog').open, false);
      assert.doesNotMatch(get('#search-suggestions').textContent, /Old account/);
    } finally { controller?.dispose(); unsubscribe(); global.fetch = originalFetch; }
  });
  console.log('Submission search controller: debounce, states, literal DOM, stale query/scope/page/session, keyboard and full results passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
