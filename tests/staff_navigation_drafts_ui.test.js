const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
const id = '9007199254740993';
const response = (status, body) => ({ ok: status < 400, status,
  statusText: status < 400 ? 'OK' : 'Request failed', json: async () => body });
const settle = async () => { await new Promise(resolve => setTimeout(resolve, 45)); };
async function until(predicate, message) {
  const deadline = Date.now() + 2500;
  while (!predicate() && Date.now() < deadline) await settle();
  assert.ok(predicate(), message);
}

async function fixture(route, journey, options = {}) {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-navigation-drafts-'));
  let dom;
  try {
    fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff/index.html'), 'utf8'), {
      url: `https://localhost/staff/${route}`, pretendToBeVisual: true
    });
    Object.assign(global, { window: dom.window, document: dom.window.document,
      Node: dom.window.Node, HTMLElement: dom.window.HTMLElement, DOMParser: dom.window.DOMParser,
      FormData: dom.window.FormData, URLSearchParams: dom.window.URLSearchParams });
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    const gridPath = path.join(frontend, 'vendor/gridjs/6.2.0/gridjs.umd.js');
    delete require.cache[require.resolve(gridPath)];
    dom.window.gridjs = require(gridPath);
    const staff = { id: '20', role: options.role || 'super_admin', organizationId: options.role === 'staff' ? 2 : 1,
      organizationName: 'System', displayName: 'Staff', version: 'actor-v1' };
    const organizations = options.organizations ||
      [{ id: 2, name: 'Library A', active: true }, { id: 3, name: 'Library B', active: true }];
    if (options.retainedOperation) {
      dom.window.sessionStorage.setItem('asap.staff.operation..20', JSON.stringify(options.retainedOperation));
    }
    let request = { id, type: 'title_request', version: 'v1', title: 'Saved title', author: null,
      identifier: '9780000000001', bibid: 9001, bibidStaffVerified: options.verified !== false,
      libraryOrgId: options.requestLibrary || 2, libraryOrgName: 'Library A',
      status: options.status || 'suggestion', format: 'book', formatLabel: 'Book', autohold: true, notes: '', customFields: {},
      holdOperation: options.holdOperation || null,
      claimedByStaffUserId: null, workflowTags: [], capabilities: {
        canEditIdentifier: true, canChangeBib: true, canChangeWorkflowState: true, canClaim: true,
        allowedActions: ['edit', 'purchase', 'alreadyOwn', 'catalogFound', 'reject', 'silentClose'] } };
    const calls = [];
    const confirms = [];
    let discard = false;
    let operationResponse = null;
    global.fetch = async (url, init = {}) => {
      calls.push({ url, init });
      const parsed = new URL(url, dom.window.location.href);
      const pathname = parsed.pathname;
      if (pathname.endsWith('/session')) return response(200, { authenticated: true, accessAllowed: true, staff, antiforgeryToken: 'token' });
      if (pathname.endsWith('/email-readiness')) return response(200, {});
      if (pathname.endsWith('/organizations')) return response(200, { code: 'ok', data: organizations });
      if (pathname.endsWith('/settings') && init.method !== 'POST') return response(200, {
        orgId: parsed.searchParams.get('orgId'), version: 'settings-v1',
        stored: { configuredSystem: { patron: { loginNote: 'Saved login note' } },
          systemSettings: {}, workflow: {}, patron: {}, email: {}, formats: [], templates: [] },
        effective: {}, ui_text: { loginNote: 'Saved login note' }, workflow: {}, emails: {}
      });
      if (pathname.endsWith('/patron-codes')) return response(200, { data: [] });
      if (pathname.endsWith('/title-requests') || pathname.endsWith('/additional-copies')) {
        const scope = parsed.searchParams.get('scope') || 'all';
        return response(200, { scope, status: parsed.searchParams.get('status') || 'open',
          organizations, availableLibraries: organizations,
          items: pathname.endsWith('/title-requests') ? [request] : [] });
      }
      if (pathname.endsWith(`/title-requests/${id}`)) {
        const scope = parsed.searchParams.get('scope');
        if (scope && scope !== 'all' && scope !== String(request.libraryOrgId)) return response(404, {});
        return response(200, request);
      }
      if (pathname.endsWith('/config') || pathname.endsWith('/suggestion-configuration')) {
        const configuration = { availableFormats: ['book'], formatLabels: { book: 'Book' },
          publicationOptions: ['published'], additionalFieldDefinitions: [], formatRules: {} };
        return response(200, pathname.endsWith('/config') ? configuration :
          { libraryOrgId: Number(parsed.searchParams.get('libraryOrgId')), configuration });
      }
      if (pathname.endsWith('/research-configuration')) return response(200, {});
      if (pathname.endsWith('/bib-lookup')) return response(200, { bibId: 9001, title: request.title, author: null });
      if (pathname.endsWith('/patron-lookup')) return response(200, {
        status: 'verified', libraryOrgId: 3, libraryOrgName: 'Library B',
        patron: { patron: { barcode: '20000000000001', name: 'Patron' },
          pickupBranches: [{ id: 101, label: 'Main' }], currentPreferredPickupBranchId: 101,
          preferredPickupBranchId: 101 } });
      if (pathname.endsWith('/suggestions')) return operationResponse ? operationResponse() : response(409, {
        code: 'duplicate_open_request', duplicate: { id, matchType: 'title_format' } });
      if (pathname.endsWith('/workflow/queues') || pathname.endsWith('/email-operations')) return response(200, { items: [] });
      if (init.method === 'POST') {
        if (operationResponse) return operationResponse();
        request = { ...request, version: 'v2', title: JSON.parse(init.body || '{}').title || request.title };
        return response(200, { committed: true, request, finalStatus: request.status });
      }
      throw new Error(`Unexpected request: ${url}`);
    };
    dom.window.confirm = message => { confirms.push(message); return discard; };
    const module = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    await module.createWorkflowApp().start();
    await settle();
    const get = selector => document.querySelector(selector);
    const edit = (selector, value) => { const control = get(selector); control.value = value;
      control.dispatchEvent(new dom.window.Event('input', { bubbles: true })); };
    const back = async () => { dom.window.history.back(); await settle(); await settle(); };
    const forward = async () => { dom.window.history.forward(); await settle(); await settle(); };
    await journey({ dom, get, edit, back, forward, calls, confirms,
      allowDiscard: () => { discard = true; },
      readRequest: () => request,
      setOperation: fn => { operationResponse = fn; },
      params: () => new URL(dom.window.location.href).searchParams,
      open: async () => { await until(() => get('.grid-open'), 'queue opener'); get('.grid-open').click();
        await until(() => get('#request-dialog').open, 'detail opened'); } });
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
}

const cases = [];
const test = (name, body) => cases.push({ name, body });
for (const exit of ['close', 'escape', 'top-level', 'back', 'sign-out']) {
  test(`dirty request blocks ${exit}`, () => fixture('?stage=suggestion&scope=2', async ui => {
    await ui.open();
    ui.edit('.edit-form input', 'Unsaved title');
    const activeUrl = ui.dom.window.location.href;
    if (exit === 'close') ui.get('#close-request').click();
    if (exit === 'escape') ui.get('#request-dialog').dispatchEvent(new ui.dom.window.Event('cancel', { cancelable: true }));
    if (exit === 'top-level') ui.get('[data-view="profile"]').click();
    if (exit === 'back') await ui.back();
    if (exit === 'sign-out') ui.get('#sign-out').click();
    await settle();
    assert.equal(ui.get('#request-dialog').open, true);
    assert.equal(ui.get('.edit-form input').value, 'Unsaved title');
    assert.equal(ui.dom.window.location.href, activeUrl);
    assert.equal(ui.confirms.length, 1);
    assert.equal(ui.calls.filter(call => call.init.method === 'POST').length, 0);
    const unload = new ui.dom.window.Event('beforeunload', { cancelable: true });
    ui.dom.window.dispatchEvent(unload);
    assert.equal(unload.defaultPrevented, true);
    ui.allowDiscard();
    ui.get('#close-request').click();
    await settle();
    assert.equal(ui.get('#request-dialog').open, false);
    const cleanUnload = new ui.dom.window.Event('beforeunload', { cancelable: true });
    ui.dom.window.dispatchEvent(cleanUnload);
    assert.equal(cleanUnload.defaultPrevented, false);
  }));
}

test('dirty Settings sign-out requires discard and preserves the draft when declined', () =>
  fixture('?stage=settings#settings-patron', async ui => {
    await until(() => !ui.get('#settings-form').hidden, 'settings editor loaded');
    ui.edit('#patron-login-note', 'Unsaved login note');
    ui.get('#sign-out').click(); await settle();
    assert.equal(ui.get('#workspace').hidden, false);
    assert.equal(ui.get('#patron-login-note').value, 'Unsaved login note');
    assert.equal(ui.params().get('stage'), 'settings');
    assert.equal(ui.confirms.length, 1);
    assert.ok(!ui.calls.some(call => call.url.endsWith('/sign-out')));
    ui.allowDiscard(); ui.get('#sign-out').click();
    await until(() => ui.get('#workspace').hidden, 'confirmed discard permits sign-out');
  }));

test('pending and uncertain Settings mutations block sign-out until authoritative reload', () =>
  fixture('?stage=settings#settings-patron', async ui => {
    await until(() => !ui.get('#settings-form').hidden, 'settings editor loaded');
    ui.edit('#patron-login-note', 'Unsaved login note');
    let reject;
    ui.setOperation(() => new Promise((resolve, fail) => { reject = fail; }));
    ui.get('#settings-form').dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true }));
    await until(() => reject, 'settings save dispatched');
    ui.get('#sign-out').click(); await settle();
    assert.match(ui.get('#app-status').textContent, /Wait for the settings change/);
    reject(new Error('Connection lost')); await settle();
    ui.get('#sign-out').click(); await settle();
    assert.match(ui.get('#app-status').textContent, /uncertain change/);
    assert.equal(ui.confirms.length, 0);
    assert.ok(!ui.calls.some(call => call.url.endsWith('/sign-out')));
    ui.allowDiscard();
    ui.get('#settings-refresh').click();
    await until(() => ui.get('#patron-login-note').value === 'Saved login note', 'authoritative settings reload');
    ui.setOperation(null); ui.get('#sign-out').click();
    await until(() => ui.get('#workspace').hidden, 'reviewed settings permit sign-out');
  }));
test('dirty request blocks claim and unrelated workflow actions', () => fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
  ui.edit('.edit-form input', 'Unsaved title');
  for (const label of ['Claim', 'Already own', 'Ready for hold', 'Close silently']) {
    [...document.querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === label)?.click();
    await settle();
  }
  assert.equal(ui.calls.filter(call => call.init.method === 'POST').length, 0);
  assert.equal(ui.get('.edit-form input').value, 'Unsaved title');
  assert.match(ui.get('#app-status').textContent, /save|revert/i);
}));
test('hold recovery guards editor drafts and refreshes authoritatively after its result', () =>
  fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    const reconcile = () => [...document.querySelectorAll('.hold-operation button')]
      .find(button => button.textContent === 'Reconcile provider state');
    ui.allowDiscard();
    ui.edit('.edit-form input', 'Unsaved recovery draft');
    reconcile().click(); await settle();
    assert.equal(ui.calls.filter(call => call.init.method === 'POST').length, 0);
    assert.equal(ui.get('.edit-form input').value, 'Unsaved recovery draft');
    [...document.querySelectorAll('.edit-form button')].find(button => button.textContent === 'Revert changes').click();
    let resolve;
    ui.setOperation(() => new Promise(done => { resolve = done; }));
    reconcile().click(); await until(() => resolve, 'hold recovery dispatched');
    assert.equal(ui.get('.edit-form input').disabled, true);
    ui.get('[data-view="profile"]').click();
    assert.equal(ui.get('#request-dialog').open, true);
    resolve(response(200, { committed: true, code: 'updated', operationId: '71', finalStatus: 'pending_hold' }));
    await until(() => /reconciliation recorded/.test(ui.get('#app-status').textContent), 'hold recovery result announced');
    assert.ok(ui.calls.filter(call => new URL(call.url, 'https://localhost').pathname.endsWith(`/title-requests/${id}`)).length >= 2,
      'The authoritative recovery must refresh its detail despite the navigation guard');
    assert.equal(ui.get('.edit-form input').disabled, false);
    assert.equal(ui.get('.edit-form input').value, 'Saved title');
  }, { status: 'pending_hold', holdOperation: { id: '71', version: 'operation-v1', state: 'outcome_unknown',
    phase: 'acquired', attemptNumber: 1, canReconcile: true } }));
for (const action of ['Already own', 'Ready for hold', 'Purchase']) {
  test(`persisted verified BIB permits ${action} after reopen`, () => fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
    ui.allowDiscard();
    [...document.querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === action).click();
    if (action === 'Purchase') ui.get('.action-choice button[type="submit"]').click();
    await until(() => ui.calls.some(call => call.url.endsWith('/action') && call.init.method === 'POST'), 'persisted verification preflight');
    assert.equal(ui.calls.some(call => call.url.includes('/polaris/bibs')), false);
  }, { status: action === 'Ready for hold' ? 'outstanding_purchase' : 'suggestion' }));
}
test('changed BIB or identifier cannot inherit authoritative verification', () => fixture(`?request=${id}`, async ui => {
  ui.allowDiscard();
  ui.edit('.edit-form input[inputmode="numeric"]', '9002');
  [...document.querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === 'Already own').click();
  await settle();
  assert.equal(ui.calls.filter(call => call.init.method === 'POST').length, 0);
  ui.edit('.edit-form input[inputmode="numeric"]', '9001');
  ui.edit('.edit-form input[maxlength="100"]', '9780000000002');
  [...document.querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === 'Already own').click();
  await settle();
  assert.equal(ui.calls.filter(call => call.init.method === 'POST').length, 0);
}));
test('explicit revert keeps the detail and clears only the discarded draft', () => fixture(`?request=${id}`, async ui => {
  ui.edit('.edit-form input', 'Unsaved title');
  [...document.querySelectorAll('.edit-form button')].find(button => button.textContent === 'Revert changes').click();
  assert.equal(ui.get('.edit-form input').value, 'Unsaved title', 'declined revert retains draft');
  ui.allowDiscard();
  [...document.querySelectorAll('.edit-form button')].find(button => button.textContent === 'Revert changes').click();
  assert.equal(ui.get('.edit-form input').value, 'Saved title');
  assert.equal(ui.get('#request-dialog').open, true);
  const unload = new ui.dom.window.Event('beforeunload', { cancelable: true });
  ui.dom.window.dispatchEvent(unload);
  assert.equal(unload.defaultPrevented, false);
  assert.equal(ui.calls.filter(call => call.init.method === 'POST').length, 0);
}));
test('empty authoritative publication is clean even with configured choices', () => fixture(`?request=${id}`, async ui => {
  assert.equal(ui.get('.edit-form select[aria-label="Publication timing"]').value, '');
  const unload = new ui.dom.window.Event('beforeunload', { cancelable: true });
  ui.dom.window.dispatchEvent(unload);
  assert.equal(unload.defaultPrevented, false);
  ui.get('#close-request').click(); await settle();
  assert.equal(ui.confirms.length, 0);
}));
test('save protects fields while pending and clears draft after authoritative success', () => fixture(`?request=${id}`, async ui => {
  let resolve;
  ui.edit('.edit-form input', 'Saved browser draft');
  ui.setOperation(() => new Promise(done => { resolve = done; }));
  ui.get('.edit-form').dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true }));
  await until(() => resolve, 'save dispatched');
  assert.equal(ui.get('.edit-form input').disabled, true);
  ui.get('#close-request').click();
  assert.equal(ui.get('#request-dialog').open, true);
  assert.equal(ui.confirms.length, 0);
  resolve(response(200, { committed: true, request: { ...ui.readRequest(), version: 'v2', title: 'Saved browser draft' } }));
  await until(() => ui.get('.edit-form input').value === 'Saved browser draft' && !ui.get('.edit-form input').disabled, 'saved detail rendered');
  const unload = new ui.dom.window.Event('beforeunload', { cancelable: true });
  ui.dom.window.dispatchEvent(unload);
  assert.equal(unload.defaultPrevented, false);
  ui.get('#close-request').click(); await settle();
  assert.equal(ui.confirms.length, 0);
}));
test('new authoritative version cannot inherit ephemeral verification', () => fixture(`?request=${id}`, async ui => {
  [...document.querySelectorAll('.edit-form button')].find(button => button.textContent === 'Search Polaris catalog').click();
  ui.get('#polaris-form').dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true }));
  await until(() => ui.get('#polaris-results button'), 'Polaris result');
  ui.get('#polaris-results button').click();
  await until(() => !ui.get('#polaris-dialog').open, 'BIB selected for v1');
  ui.setOperation(() => response(200, { committed: true, request: { ...ui.readRequest(), version: 'v2', bibidStaffVerified: false } }));
  ui.get('.edit-form').dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true }));
  await until(() => /Request changes saved/.test(ui.get('#app-status').textContent), 'v2 authoritative response');
  assert.equal(ui.get('.polaris-selection-context').textContent, '');
  const posts = ui.calls.filter(call => call.url.endsWith('/action')).length;
  ui.allowDiscard();
  [...document.querySelectorAll('.action-bar button')].find(button => button.textContent === 'Already own').click(); await settle();
  assert.equal(ui.calls.filter(call => call.url.endsWith('/action')).length, posts);
  assert.match(ui.get('#app-status').textContent, /Search Polaris/);
}, { verified: false }));
test('open close back does not reopen the closed request', () => fixture('?stage=profile', async ui => {
  ui.get('[data-view="queue"]').click(); await settle();
  for (let cycle = 0; cycle < 3; cycle += 1) {
    await ui.open(); ui.get('#close-request').click(); await settle(); await settle();
    assert.equal(ui.params().has('request'), false);
  }
  await ui.back();
  assert.equal(ui.get('#request-dialog').open, false);
  assert.equal(ui.params().get('stage'), 'profile');
  assert.equal(ui.get('#profile-view').hidden, false);
}));
test('direct detail close replaces its route', () => fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
  const length = ui.dom.window.history.length;
  ui.get('#close-request').click(); await settle();
  assert.equal(ui.params().has('request'), false);
  assert.equal(ui.dom.window.history.length, length);
}));
test('profile direct route and back forward round trip', () => fixture('?stage=profile', async ui => {
  assert.equal(ui.get('#profile-view').hidden, false);
  ui.get('[data-view="queue"]').click(); await settle();
  await ui.back();
  assert.equal(ui.get('#profile-view').hidden, false);
  await ui.forward();
  assert.equal(ui.get('#queue-view').hidden, false);
  ui.get('[data-view="profile"]').click(); await settle();
  assert.equal(ui.params().get('stage'), 'profile');
}));
test('additional copy closed and scope direct route and history', () => fixture('?stage=additional_copies&scope=3&copyStatus=closed', async ui => {
  assert.equal(ui.get('#additional-copy-library-scope').value, '3');
  assert.equal(ui.get('[data-copy-status="closed"]').getAttribute('aria-selected'), 'true');
  ui.get('[data-copy-status="open"]').click(); await settle();
  ui.get('#additional-copy-library-scope').value = '2';
  ui.get('#additional-copy-library-scope').dispatchEvent(new ui.dom.window.Event('change')); await settle();
  assert.equal(ui.params().get('scope'), '2');
  await ui.back();
  assert.equal(ui.get('#additional-copy-library-scope').value, '3');
  await ui.back();
  assert.equal(ui.get('[data-copy-status="closed"]').getAttribute('aria-selected'), 'true');
  await ui.forward();
  assert.equal(ui.get('[data-copy-status="open"]').getAttribute('aria-selected'), 'true');
}));
for (const scope of ['1', '99', 'bad']) {
  test(`invalid operational scope ${scope} canonicalizes`, () => fixture(`?stage=suggestion&scope=${scope}`, async ui => {
    assert.equal(ui.params().get('scope'), 'all');
    assert.equal(ui.get('#library-scope').value, 'all');
  }));
}
test('ordinary staff cannot route to another library', () => fixture('?stage=suggestion&scope=3', async ui => {
  assert.equal(ui.params().get('scope'), '2');
  assert.ok(ui.calls.filter(call => call.url.includes('/title-requests?')).every(call => call.url.includes('scope=2')));
}, { role: 'staff' }));
test('inactive operational scope canonicalizes to the authorized default', () =>
  fixture('?stage=suggestion&scope=3', async ui => {
    assert.equal(ui.params().get('scope'), 'all');
    assert.equal(ui.get('#library-scope').value, 'all');
  }, { organizations: [{ id: 2, name: 'Library A', active: true }, { id: 3, name: 'Inactive library', active: false }] }));
test('invalid additional-copy subroute canonicalizes to Open', () =>
  fixture('?stage=additional_copies&scope=2&copyStatus=bad', async ui => {
    assert.equal(ui.params().get('copyStatus'), 'open');
    assert.equal(ui.get('[data-copy-status="open"]').getAttribute('aria-selected'), 'true');
  }));
test('Operations keeps its own session scope and clears unrelated queue route parameters', () =>
  fixture('?stage=operations&scope=3&copyStatus=closed', async ui => {
    assert.equal(ui.params().get('scope'), null);
    assert.equal(ui.params().get('copyStatus'), null);
    assert.equal(ui.get('#operations-scope').value, 'all');
    const entries = ui.dom.window.history.length;
    const scope = ui.get('#operations-scope');
    scope.value = '3'; scope.dispatchEvent(new ui.dom.window.Event('change'));
    await settle();
    assert.equal(ui.dom.window.history.length, entries);
    assert.equal(ui.params().get('stage'), 'operations');
    assert.equal(ui.params().get('scope'), null);
  }));
for (const exit of ['close', 'escape', 'search-Escape', 'top-level', 'back', 'sign-out']) {
  test(`new suggestion protects patron lookup draft on ${exit}`, () => fixture('?stage=suggestion&scope=2', async ui => {
    ui.get('[data-view="profile"]').click(); await settle();
    ui.get('[data-view="queue"]').click(); await settle();
    ui.get('#new-suggestion').click();
    ui.edit('[aria-label="Patron barcode or name"]', 'Patron name');
    const activeUrl = ui.dom.window.location.href;
    if (exit === 'close') ui.get('#close-staff-suggestion').click();
    if (exit === 'escape') ui.get('#staff-suggestion-dialog').dispatchEvent(new ui.dom.window.Event('cancel', { cancelable: true }));
    if (exit === 'search-Escape') {
      const escape = new ui.dom.window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
      ui.get('[aria-label="Patron barcode or name"]').dispatchEvent(escape);
      assert.equal(escape.defaultPrevented, true, 'Native search clearing must wait for the draft decision');
    }
    if (exit === 'top-level') ui.get('[data-view="profile"]').click();
    if (exit === 'back') await ui.back();
    if (exit === 'sign-out') ui.get('#sign-out').click();
    await settle();
    assert.equal(ui.get('#staff-suggestion-dialog').open, true);
    assert.equal(ui.get('[aria-label="Patron barcode or name"]').value, 'Patron name');
    assert.equal(ui.dom.window.location.href, activeUrl);
    assert.equal(ui.confirms.length, 1);
  }));
}
test('operations guard different mutations while pending and uncertain', () => fixture('?stage=operations', async ui => {
  let reject;
  ui.setOperation(() => new Promise((resolve, fail) => { reject = fail; }));
  ui.get('#force-weekly-now').click();
  await until(() => reject, 'operation dispatched');
  ui.get('#force-weekly-now').click(); ui.get('#send-test-email').click();
  await settle();
  assert.equal(ui.calls.filter(call => call.init.method === 'POST').length, 1);
  reject(new Error('Network disconnected')); await settle();
  ui.get('#send-test-email').click(); await settle();
  assert.equal(ui.calls.filter(call => call.init.method === 'POST').length, 1);
  assert.match(ui.get('#app-status').textContent, /uncertain|unconfirmed/i);
}));
test('cross-library duplicate aligns the authorized detail and parent route', () => fixture('?stage=suggestion&scope=2', async ui => {
  ui.get('#new-suggestion').click();
  const scope = ui.get('[aria-label="Servicing library"]');
  scope.value = '3'; scope.dispatchEvent(new ui.dom.window.Event('change', { bubbles: true }));
  ui.edit('[aria-label="Patron barcode or name"]', '20000000000001');
  ui.get('#staff-suggestion-form').dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true }));
  await until(() => ui.get('.staff-suggestion-fields'), 'verified suggestion editor');
  ui.edit('.staff-suggestion-fields input[maxlength="500"]', 'Duplicate draft');
  ui.get('#staff-suggestion-form').dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true }));
  await until(() => ui.get('.staff-suggestion-conflict button'), 'duplicate conflict');
  ui.allowDiscard();
  ui.get('.staff-suggestion-conflict button').click();
  await until(() => ui.get('#request-dialog').open, 'cross-library duplicate detail');
  assert.equal(ui.get('#staff-suggestion-dialog').open, false);
  assert.equal(ui.get('#library-scope').value, '3');
  assert.equal(ui.params().get('scope'), '3');
  assert.equal(ui.params().get('request'), id);
  assert.ok(ui.calls.some(call => call.url.includes(`/title-requests/${id}?scope=all`)));
}, { requestLibrary: 3 }));

test('pending and uncertain suggestion creation protect its submitted fields and navigation', () =>
  fixture('?stage=suggestion&scope=2', async ui => {
    ui.get('#new-suggestion').click();
    const scope = ui.get('[aria-label="Servicing library"]');
    scope.value = '3'; scope.dispatchEvent(new ui.dom.window.Event('change', { bubbles: true }));
    ui.edit('[aria-label="Patron barcode or name"]', '20000000000001');
    ui.get('#staff-suggestion-form').dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true }));
    await until(() => ui.get('.staff-suggestion-fields'), 'verified suggestion editor');
    ui.edit('.staff-suggestion-fields input[maxlength="500"]', 'Submitted draft');
    let reject;
    ui.setOperation(() => new Promise((resolve, fail) => { reject = fail; }));
    ui.get('#staff-suggestion-form').dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true }));
    await until(() => reject, 'suggestion create dispatched');
    assert.equal(ui.get('#staff-suggestion-form').inert, true);
    ui.get('#close-staff-suggestion').click(); ui.get('#sign-out').click();
    assert.equal(ui.get('#staff-suggestion-dialog').open, true);
    assert.equal(ui.confirms.length, 0);
    reject(new Error('Connection lost')); await settle();
    ui.get('[data-view="profile"]').click();
    assert.equal(ui.get('#staff-suggestion-dialog').open, true);
    assert.equal(ui.get('#staff-suggestion-form').inert, true);
    ui.get('#staff-suggestion-form').dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true }));
    await settle();
    assert.equal(ui.calls.filter(call => call.url.endsWith('/suggestions')).length, 1);
    assert.ok(!ui.calls.some(call => call.url.endsWith('/sign-out')));
  }));
for (const status of [202, 400]) {
  test(`authoritative operations ${status} restores controls`, () => fixture('?stage=operations', async ui => {
    let resolve;
    ui.setOperation(() => new Promise(done => { resolve = done; }));
    ui.get('#run-workflow-now').click();
    await until(() => resolve, 'manual operation started');
    assert.equal(ui.get('#send-test-email').disabled, true);
    resolve(response(status, { code: status === 202 ? 'queued' : 'invalid_operation' }));
    await until(() => !ui.get('#send-test-email').disabled, 'authoritative outcome releases guard');
    assert.equal(ui.calls.filter(call => call.init.method === 'POST').length, 1);
  }));
}
test('uncertain forced operation requires review and reuses its identity', () => fixture('?stage=operations', async ui => {
  ui.setOperation(() => { throw new Error('Connection lost'); });
  ui.get('#force-weekly-now').click(); await settle();
  const first = ui.calls.find(call => call.init.method === 'POST');
  assert.equal(ui.get('#operations-outcome button'), null);
  ui.get('[data-view="profile"]').click(); await settle();
  ui.get('[data-view="operations"]').click(); await settle();
  assert.equal(ui.get('#send-test-email').disabled, true);
  await until(() => ui.get('#operations-outcome button'), 'review enables only the same operation retry');
  ui.setOperation(() => response(202, { code: 'queued' }));
  ui.get('#operations-outcome button').click();
  await until(() => !ui.get('#send-test-email').disabled, 'safe retry confirmed');
  const posts = ui.calls.filter(call => call.init.method === 'POST');
  assert.equal(posts.length, 2);
  assert.equal(new URL(posts[1].url, 'https://localhost').searchParams.get('operationId'),
    new URL(first.url, 'https://localhost').searchParams.get('operationId'));
}));

test('reload preserves an unresolved operation and retries only its recorded identity', () =>
  fixture('?stage=operations', async ui => {
    assert.equal(ui.calls.filter(call => call.init.method === 'POST').length, 0);
    assert.equal(ui.get('#send-test-email').disabled, true);
    assert.equal(ui.get('#force-weekly-now').disabled, true);
    await until(() => ui.get('#operations-outcome button'), 'current Operations reviewed after reload');
    ui.setOperation(() => response(202, { code: 'queued' }));
    ui.get('#operations-outcome button').click();
    await until(() => !ui.get('#send-test-email').disabled, 'recorded operation resolved');
    const posts = ui.calls.filter(call => call.init.method === 'POST');
    assert.equal(posts.length, 1);
    const query = new URL(posts[0].url, 'https://localhost').searchParams;
    assert.equal(query.get('organizationId'), '3');
    assert.equal(query.get('operationId'), '33333333-3333-4333-8333-333333333333');
    assert.equal(ui.dom.window.sessionStorage.getItem('asap.staff.operation..20'), null);
  }, { retainedOperation: { path: '/api/asap/staff/workflow/weekly-summary/run-now?force=true',
    message: 'Forced weekly summary', scope: '3', operationId: '33333333-3333-4333-8333-333333333333' } }));

(async () => {
  let failed = 0;
  for (const item of cases) {
    try { await item.body(); console.log(`PASS ${item.name}`); }
    catch (error) { failed += 1; console.error(`FAIL ${item.name}: ${error.message}`); }
  }
  assert.equal(failed, 0, `${failed}/${cases.length} navigation/draft journeys failed`);
  console.log(`${cases.length} staff navigation/draft journeys passed.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
