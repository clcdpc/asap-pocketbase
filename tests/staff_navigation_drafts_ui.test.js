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
  let dom, app;
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
    let staff = { id: '20', role: options.role || 'super_admin', organizationId: options.role === 'staff' ? 2 : 1,
      organizationName: 'System', displayName: 'Staff', version: 'actor-v1', ...options.staff };
    const organizations = options.organizations ||
      [{ id: 2, name: 'Library A', active: true }, { id: 3, name: 'Library B', active: true }];
    if (options.retainedOperation) {
      dom.window.sessionStorage.setItem('asap.staff.operation..20', JSON.stringify(options.retainedOperation));
    }
    for (const [key, value] of options.storage || []) dom.window.sessionStorage.setItem(key, value);
    let request = { id, type: 'title_request', version: 'v1', title: 'Saved title', author: null,
      identifier: '9780000000001', bibid: 9001, bibidStaffVerified: options.verified !== false,
      libraryOrgId: options.requestLibrary || 2, libraryOrgName: 'Library A',
      status: options.status || 'suggestion', format: 'book', formatLabel: 'Book', autohold: true, notes: '', customFields: {},
      holdOperation: options.holdOperation || null,
      claimedByStaffUserId: null, workflowTags: [], capabilities: {
        canEditIdentifier: true, canChangeBib: true, canChangeWorkflowState: true, canClaim: true,
        allowedActions: ['edit', 'purchase', 'alreadyOwn', 'catalogFound', 'reject', 'silentClose'] },
      ...options.request };
    let copyRequest = { id: '71', type: 'additional_copy', version: 'copy-v1', status: 'open',
      libraryOrgId: 2, libraryOrgName: 'Library A', title: 'Additional copy', bibid: 9001,
      claimedByStaffUserId: null, capabilities: { canClaim: true, canAssign: true, canClose: true },
      ...options.copyRequest };
    const calls = [];
    const confirms = [];
    let discard = false;
    let operationResponse = null;
    let apiResponse = null;
    global.fetch = async (url, init = {}) => {
      calls.push({ url, init });
      const parsed = new URL(url, dom.window.location.href);
      const pathname = parsed.pathname;
      if (apiResponse) {
        const result = await apiResponse({ url, init, parsed, pathname, staff, request });
        if (result !== undefined) return result;
      }
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
          items: pathname.endsWith('/title-requests') ? options.titleItems || [request]
            : options.copyItems || (options.copyRequest ? [copyRequest] : []) });
      }
      if (pathname.endsWith(`/title-requests/${id}`)) {
        const scope = parsed.searchParams.get('scope');
        if (scope && scope !== 'all' && scope !== String(request.libraryOrgId)) return response(404, {});
        return response(200, request);
      }
      if (pathname.endsWith(`/additional-copies/${copyRequest.id}`)) return response(200, copyRequest);
      if (pathname.endsWith('/config') || pathname.endsWith('/suggestion-configuration')) {
        const configuration = { availableFormats: ['book'], formatLabels: { book: 'Book' },
          publicationOptions: ['published'], additionalFieldDefinitions: [], formatRules: {} };
        return response(200, pathname.endsWith('/config') ? configuration :
          { libraryOrgId: Number(parsed.searchParams.get('libraryOrgId')), configuration });
      }
      if (pathname.endsWith('/research-configuration')) return response(200, {});
      if (pathname.endsWith('/bib-lookup')) return response(200, { bibId: 9001, title: request.title, author: null });
      if (pathname.endsWith('/additional-copy') && init.method === 'GET') return response(200, {
        version: request.version, bibid: request.bibid, openCount: 0, emailPurchaseReminderDefault: false });
      if (pathname.endsWith('/assignment-candidates')) return response(200, {
        candidates: [{ id: '20', displayName: 'Staff A' }, { id: '21', displayName: 'Staff B' }] });
      if (pathname.endsWith('/pickup-options')) return response(200, {
        version: request.version, selectedPickupBranchId: 101, currentPreferredPickupBranchId: 101,
        pickupBranches: [{ id: 101, label: 'Main' }, { id: 102, label: 'Branch' }] });
      if (pathname.endsWith('/rejection-templates')) return response(200, {
        items: [{ id: '1', name: 'Default' }, { id: '2', name: 'Other' }], defaultTemplateId: '1' });
      if (pathname.endsWith('/patron-lookup')) return response(200, {
        status: 'verified', libraryOrgId: 3, libraryOrgName: 'Library B',
        patron: { patron: { barcode: '20000000000001', name: 'Patron' },
          pickupBranches: [{ id: 101, label: 'Main' }], currentPreferredPickupBranchId: 101,
          preferredPickupBranchId: 101 } });
      if (pathname.endsWith('/suggestions')) return operationResponse ? operationResponse() : response(409, {
        code: 'duplicate_open_request', duplicate: { id, matchType: 'title_format' } });
      if (pathname.endsWith('/workflow/queues') || pathname.endsWith('/email-operations')) return response(200, { items: [] });
      if (init.method === 'DELETE') return operationResponse ? operationResponse() : response(200, { deleted: true });
      if (init.method === 'POST') {
        if (operationResponse) return operationResponse();
        if (pathname.startsWith('/api/asap/staff/additional-copies/')) {
          copyRequest = { ...copyRequest, version: 'copy-v2' };
          return response(200, { committed: true, request: copyRequest, finalStatus: copyRequest.status });
        }
        request = { ...request, version: 'v2', title: JSON.parse(init.body || '{}').title || request.title };
        return response(200, { committed: true, request, finalStatus: request.status });
      }
      throw new Error(`Unexpected request: ${url}`);
    };
    dom.window.confirm = message => { confirms.push(message); return discard; };
    const module = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    app = module.createWorkflowApp();
    await app.start();
    await settle();
    const get = selector => document.querySelector(selector);
    const edit = (selector, value) => { const control = get(selector); control.value = value;
      control.dispatchEvent(new dom.window.Event('input', { bubbles: true })); };
    const back = async () => { dom.window.history.back(); await settle(); await settle(); };
    const forward = async () => { dom.window.history.forward(); await settle(); await settle(); };
    await journey({ dom, get, edit, back, forward, calls, confirms,
      allowDiscard: () => { discard = true; },
      readRequest: () => request,
      readCopyRequest: () => copyRequest,
      setOperation: fn => { operationResponse = fn; },
      setApi: fn => { apiResponse = fn; },
      readStaff: () => staff,
      setStaff: value => { staff = { ...staff, ...value }; },
      params: () => new URL(dom.window.location.href).searchParams,
      open: async () => { await until(() => get('.grid-open'), 'queue opener'); get('.grid-open').click();
        await until(() => get('#request-dialog').open, 'detail opened'); } });
  } finally {
    app?.dispose();
    if (dom && options.storage) {
      options.storage.clear();
      for (let index = 0; index < dom.window.sessionStorage.length; index += 1) {
        const key = dom.window.sessionStorage.key(index);
        options.storage.set(key, dom.window.sessionStorage.getItem(key));
      }
    }
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
}

const cases = [];
const test = (name, body) => cases.push({ name, body });
test('committed title status synchronizes detail queue and accepted URL', () => fixture(`?stage=suggestion&scope=2&request=${id}&marker=keep#anchor`, async ui => {
  await until(() => ui.get('#request-dialog').open, 'detail open');
  ui.setOperation(() => response(200, { committed: true,
    request: { ...ui.readRequest(), version: 'v2', status: 'pending_hold' }, finalStatus: 'pending_hold' }));
  ui.allowDiscard();
  [...document.querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === 'Already own').click();
  await until(() => ui.get('.detail-meta .status-badge').textContent.toLowerCase() === 'pending hold', 'committed detail status');
  assert.equal(ui.params().get('stage'), 'pending_hold');
  assert.equal(ui.get('[data-status="pending_hold"]').getAttribute('aria-selected'), 'true');
  assert.equal(ui.params().get('request'), id);
  assert.equal(ui.params().get('marker'), 'keep');
  assert.equal(ui.dom.window.location.hash, '#anchor');
}));
test('committed status during target validation retains the accepted detail entry', () =>
  fixture('?stage=suggestion&scope=2', async ui => {
    await ui.open();
    let validateScope;
    ui.setApi(({ pathname }) => pathname.endsWith('/organizations')
      ? new Promise(done => { validateScope = done; }) : undefined);
    ui.dom.window.history.back();
    await until(() => validateScope, 'target validation pending');
    ui.setOperation(() => response(200, { committed: true,
      request: { ...ui.readRequest(), version: 'v2', status: 'pending_hold' }, finalStatus: 'pending_hold' }));
    ui.allowDiscard();
    [...document.querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === 'Already own').click();
    await until(() => ui.get('.detail-meta .status-badge').textContent === 'Pending hold', 'committed source detail');
    validateScope(response(200, { data: [{ id: 2, name: 'Library A', active: true }] }));
    await until(() => ui.params().get('request') === id && ui.params().get('stage') === 'pending_hold', 'updated accepted source restored');
    assert.equal(ui.get('#request-dialog').open, true);
    assert.equal(ui.get('[data-status="pending_hold"]').getAttribute('aria-selected'), 'true');
  }));
test('authoritative deep-link title stage follows current server detail', () =>
  fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
    await until(() => ui.get('#request-dialog').open, 'authoritative detail opened');
    assert.equal(ui.params().get('stage'), 'pending_hold');
    assert.equal(ui.get('[data-status="pending_hold"]').getAttribute('aria-selected'), 'true');
  }, { status: 'pending_hold' }));
test('committed copy status synchronizes detail tab and accepted URL', () =>
  fixture('?stage=additional_copies&scope=2&request=71', async ui => {
    await until(() => ui.get('#request-dialog').open, 'copy detail open');
    ui.setOperation(() => response(200, { committed: true,
      request: { ...ui.readCopyRequest(), version: 'copy-v2', status: 'closed' }, finalStatus: 'closed' }));
    ui.allowDiscard();
    [...document.querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === 'Close task').click();
    await until(() => ui.get('.detail-meta .status-badge').textContent === 'Closed', 'committed copy status');
    assert.equal(ui.params().get('copyStatus'), 'closed');
    assert.equal(ui.params().get('request'), '71');
    assert.equal(ui.get('[data-copy-status="closed"]').getAttribute('aria-selected'), 'true');
  }, { copyRequest: { id: '71' } }));
for (const [stage, view, refresh, endpoint, scopeControl] of [
  ['suggestion', 'queue', '#refresh-queue', '/title-requests', '#library-scope'],
  ['additional_copies', 'additional-copies', '#refresh-additional-copies', '/additional-copies', '#additional-copy-library-scope']
]) {
  test(`authoritative ${view} scope synchronizes accepted URL without losing request identity`, () =>
    fixture(`?stage=${stage}&scope=2&marker=keep#anchor`, async ui => {
      ui.setApi(({ pathname }) => pathname.endsWith(endpoint) ? response(200, { scope: 'all', status: 'open',
        organizations: [{ id: 2, name: 'Library A', active: true }], availableLibraries: [{ id: 2, name: 'Library A', active: true }], items: [] }) : undefined);
      ui.get(refresh).click();
      await until(() => ui.get(scopeControl).value === 'all', 'accepted authoritative scope');
      assert.equal(ui.params().get('scope'), 'all');
      assert.equal(ui.params().get('marker'), 'keep');
      assert.equal(ui.dom.window.location.hash, '#anchor');
    }));
}
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

function protectedUnload(ui) {
  const event = new ui.dom.window.Event('beforeunload', { cancelable: true });
  ui.dom.window.dispatchEvent(event);
  return event.defaultPrevented;
}

const profileStaff = { notificationEmail: 'primary@example.org', weeklyActionSummaryEmail: 'weekly@example.org',
  weeklyActionSummaryEnabled: true, purchaseReminderDefault: false,
  additionalCopyReminderDefault: true, defaultMineUnclaimedFilter: false };

for (const exit of ['top-level', 'back', 'forward', 'sign-out']) {
  test(`Profile draft protects ${exit} and accepted discard resets the form`, () =>
    fixture('?stage=suggestion&scope=2', async ui => {
      ui.get('[data-view="profile"]').click(); await settle();
      if (exit === 'forward') {
        ui.get('[data-view="queue"]').click(); await settle(); await ui.back();
      }
      ui.edit('#weekly-email', 'draft@example.org');
      const currentUrl = ui.dom.window.location.href;
      assert.equal(protectedUnload(ui), true);
      const leave = async () => {
        if (exit === 'top-level') ui.get('[data-view="queue"]').click();
        if (exit === 'back') await ui.back();
        if (exit === 'forward') await ui.forward();
        if (exit === 'sign-out') ui.get('#sign-out').click();
        await settle();
      };
      await leave();
      assert.equal(ui.dom.window.location.href, currentUrl);
      assert.equal(ui.get('#profile-view').hidden, false);
      assert.equal(ui.get('#weekly-email').value, 'draft@example.org');
      assert.equal(ui.confirms.length, 1);
      assert.ok(!ui.calls.some(call => call.url.endsWith('/sign-out')));
      ui.allowDiscard(); await leave();
      assert.equal(protectedUnload(ui), false);
      if (exit === 'sign-out') {
        assert.equal(ui.get('#workspace').hidden, true);
      } else {
        assert.equal(ui.params().get('stage'), 'suggestion');
        ui.get('[data-view="profile"]').click(); await settle();
        assert.equal(ui.get('#weekly-email').value, 'weekly@example.org');
      }
    }, { staff: profileStaff }));
}

for (const selector of ['#weekly-email', '#weekly-enabled', '#purchase-default', '#additional-copy-default', '#mine-default']) {
  test(`returning Profile ${selector} to its baseline clears the draft`, () =>
    fixture('?stage=profile', async ui => {
      const control = ui.get(selector);
      const original = control.type === 'checkbox' ? control.checked : control.value;
      if (control.type === 'checkbox') control.checked = !original;
      else ui.edit(selector, 'other@example.org');
      control.dispatchEvent(new ui.dom.window.Event('change', { bubbles: true }));
      assert.equal(protectedUnload(ui), true);
      if (control.type === 'checkbox') control.checked = original;
      else ui.edit(selector, original);
      control.dispatchEvent(new ui.dom.window.Event('change', { bubbles: true }));
      assert.equal(protectedUnload(ui), false);
      ui.get('[data-view="queue"]').click(); await settle();
      assert.equal(ui.confirms.length, 0);
      assert.equal(ui.get('#profile-view').hidden, true);
    }, { staff: profileStaff }));
}

const submitProfile = ui => ui.get('#profile-form').dispatchEvent(
  new ui.dom.window.Event('submit', { cancelable: true }));

test('Profile save is single-flight, guards navigation, and establishes the refreshed staff baseline', () =>
  fixture('?stage=suggestion&scope=2', async ui => {
    ui.get('[data-view="profile"]').click(); await settle();
    ui.edit('#weekly-email', 'saved@example.org');
    let resolve;
    ui.setApi(({ pathname }) => pathname.endsWith('/profile')
      ? new Promise(done => { resolve = done; }) : undefined);
    submitProfile(ui); submitProfile(ui);
    await until(() => resolve, 'profile mutation dispatched');
    assert.equal(ui.calls.filter(call => call.url.endsWith('/profile')).length, 1);
    assert.ok([...ui.get('#profile-form').querySelectorAll('input, button[type="submit"]')].every(control => control.disabled));
    assert.equal(protectedUnload(ui), true);
    const currentUrl = ui.dom.window.location.href;
    await ui.back(); ui.get('[data-view="queue"]').click(); ui.get('#sign-out').click(); await settle();
    assert.equal(ui.dom.window.location.href, currentUrl);
    assert.equal(ui.confirms.length, 0);
    assert.equal(ui.get('#weekly-email').value, 'saved@example.org');
    // A concurrent preference change in the session must win over the older POST result.
    const posted = { ...ui.readStaff(), version: 'actor-v2', weeklyActionSummaryEmail: 'saved@example.org' };
    ui.setStaff({ ...posted, version: 'actor-v3', purchaseReminderDefault: true });
    resolve(response(200, { staff: posted }));
    await until(() => /Profile saved\./.test(ui.get('#app-status').textContent), 'profile saved');
    assert.equal(ui.get('#purchase-default').checked, true);
    assert.equal(protectedUnload(ui), false);
    assert.equal(ui.get('#weekly-email').disabled, false);
    ui.get('[data-view="queue"]').click(); await settle();
    assert.equal(ui.confirms.length, 0);
  }, { staff: profileStaff }));

test('definite Profile failure preserves the editable draft and does not announce a save', () =>
  fixture('?stage=profile', async ui => {
    ui.edit('#weekly-email', 'draft@example.org');
    ui.setApi(({ pathname }) => pathname.endsWith('/profile')
      ? response(400, { message: 'Invalid email' }) : undefined);
    submitProfile(ui); await settle();
    assert.equal(ui.get('#weekly-email').value, 'draft@example.org');
    assert.equal(ui.get('#weekly-email').disabled, false);
    assert.equal(protectedUnload(ui), true);
    assert.match(ui.get('#app-status').textContent, /Invalid email/);
    ui.get('[data-view="queue"]').click(); await settle();
    assert.equal(ui.params().get('stage'), 'profile');
  }, { staff: profileStaff }));

test('stale Profile save prefers authoritative staff values and the current rowversion without claiming success', () =>
  fixture('?stage=profile', async ui => {
    ui.edit('#weekly-email', 'discarded@example.org');
    ui.setApi(({ pathname }) => {
      if (!pathname.endsWith('/profile')) return undefined;
      ui.setStaff({ version: 'actor-v3', weeklyActionSummaryEmail: 'authoritative@example.org' });
      return response(409, { code: 'stale_version', message: 'Profile changed. Review current values.' });
    });
    submitProfile(ui); await until(() => ui.get('#weekly-email').value === 'authoritative@example.org', 'conflict refreshed');
    assert.match(ui.get('#app-status').textContent, /Profile changed/);
    assert.equal(protectedUnload(ui), false, 'authoritative conflict refresh replaces the rejected draft');
    ui.setApi(({ pathname, init }) => pathname.endsWith('/profile')
      ? response(400, { message: 'Stopped after version inspection' }) : undefined);
    submitProfile(ui); await settle();
    const posts = ui.calls.filter(call => call.url.endsWith('/profile'));
    assert.equal(JSON.parse(posts[1].init.body).version, 'actor-v3');
  }, { staff: profileStaff }));

test('uncertain Profile save cannot retry or discard until explicit authoritative review', () =>
  fixture('?stage=profile', async ui => {
    ui.edit('#weekly-email', 'committed@example.org');
    ui.setApi(({ pathname }) => {
      if (!pathname.endsWith('/profile')) return undefined;
      ui.setStaff({ version: 'actor-v2', weeklyActionSummaryEmail: 'committed@example.org' });
      throw new Error('Response lost after commit');
    });
    submitProfile(ui); await until(() => !ui.get('#profile-refresh').hidden, 'uncertain profile recovery');
    assert.match(ui.get('#app-status').textContent, /could not be confirmed/);
    assert.equal(protectedUnload(ui), true);
    submitProfile(ui); ui.get('[data-view="queue"]').click(); await settle();
    assert.equal(ui.calls.filter(call => call.url.endsWith('/profile')).length, 1);
    assert.equal(ui.params().get('stage'), 'profile');
    ui.get('#profile-refresh').click(); await settle();
    assert.equal(ui.get('#profile-refresh').hidden, false, 'declined reload retains unresolved state');
    ui.allowDiscard(); ui.get('#profile-refresh').click();
    await until(() => ui.get('#profile-refresh').hidden, 'authoritative profile reviewed');
    assert.equal(ui.get('#weekly-email').value, 'committed@example.org');
    assert.equal(protectedUnload(ui), false);
  }, { staff: profileStaff }));

async function previewBulk(ui, scope = '2') {
  ui.get('#bulk-delete-closed').click();
  ui.get('#bulk-delete-scope').value = scope;
  ui.get('#bulk-delete-preview').click();
  await until(() => ui.get('#bulk-delete-items li'), 'bulk population previewed');
  ui.edit('#bulk-delete-confirmation', 'DELETE');
}

test('active bulk deletion rejects Back, top-level navigation, close, Sign out, and beforeunload', () =>
  fixture('?stage=profile', async ui => {
    ui.get('[data-view="queue"]').click();
    ui.get('[data-status="closed"]').click(); await settle();
    await previewBulk(ui);
    let resolve;
    ui.setApi(({ init }) => init.method === 'DELETE' ? new Promise(done => { resolve = done; }) : undefined);
    ui.get('#bulk-delete-execute').click(); await until(() => resolve, 'delete dispatched');
    const currentUrl = ui.dom.window.location.href;
    assert.equal(protectedUnload(ui), true);
    await ui.back();
    ui.get('[data-view="profile"]').click(); ui.get('#sign-out').click(); ui.get('#bulk-delete-close').click();
    await settle();
    assert.equal(ui.dom.window.location.href, currentUrl);
    assert.equal(ui.get('#queue-view').hidden, false);
    assert.equal(ui.get('#bulk-delete-dialog').open, true);
    assert.ok(!ui.calls.some(call => call.url.endsWith('/sign-out')));
    resolve(response(200, { deleted: true }));
    await until(() => /Both Closed views were refreshed/.test(ui.get('#bulk-delete-summary').textContent), 'batch completed');
    assert.match(ui.get('#bulk-delete-results').textContent, /Confirmed deleted: 1 of 1/);
    assert.equal(protectedUnload(ui), false);
  }, { status: 'closed' }));

for (const exit of ['back', 'top-level']) {
  test(`non-submitting Bulk Delete preview closes cleanly on ${exit}`, () =>
    fixture('?stage=profile', async ui => {
      ui.get('[data-view="queue"]').click(); ui.get('[data-status="closed"]').click(); await settle();
      await previewBulk(ui);
      if (exit === 'back') await ui.back();
      else ui.get('[data-view="profile"]').click();
      await settle();
      assert.equal(ui.get('#bulk-delete-dialog').open, false);
      assert.equal(ui.get('#bulk-delete-items').childElementCount, 0);
      assert.equal(ui.get('#bulk-delete-confirmation').value, '');
      ui.get('#bulk-delete-execute').dispatchEvent(new ui.dom.window.Event('click'));
      assert.equal(ui.calls.filter(call => call.init.method === 'DELETE').length, 0);
      assert.equal(protectedUnload(ui), false);
    }, { status: 'closed' }));
}

test('bulk completion aligns the selected scope with its URL and refreshes both Closed lists', () =>
  fixture('?stage=closed&scope=2', async ui => {
    await previewBulk(ui, '3');
    ui.get('#bulk-delete-execute').click();
    await until(() => /Both Closed views were refreshed/.test(ui.get('#bulk-delete-summary').textContent), 'closed refresh finished');
    assert.equal(ui.params().get('scope'), '3');
    assert.equal(ui.get('#library-scope').value, '3');
    ui.get('#bulk-delete-close').click(); ui.get('[data-view="additional-copies"]').click(); await settle();
    assert.equal(ui.params().get('copyStatus'), 'closed');
    assert.ok(ui.calls.some(call => call.url.includes('/additional-copies?scope=3&status=closed')));
  }, { status: 'closed', requestLibrary: 3 }));

test('completed bulk deletion cannot overwrite an unrelated route during its refresh', () =>
  fixture('?stage=closed&scope=2', async ui => {
    await previewBulk(ui);
    let resolveRefresh;
    ui.setApi(({ pathname, init }) => pathname.endsWith('/title-requests') && init.method === 'GET'
      ? new Promise(done => { resolveRefresh = done; }) : undefined);
    ui.get('#bulk-delete-execute').click(); await until(() => resolveRefresh, 'completion refresh pending');
    ui.get('[data-view="profile"]').click(); await settle();
    const currentUrl = ui.dom.window.location.href;
    assert.equal(ui.get('#bulk-delete-dialog').open, false);
    resolveRefresh(response(200, { scope: '2', items: [], organizations: [] })); await settle();
    assert.equal(ui.params().get('stage'), 'profile');
    assert.equal(ui.dom.window.location.href, currentUrl);
    assert.equal(document.activeElement, ui.get('#profile-title'));
    assert.equal(ui.get('#bulk-delete-results').childElementCount, 0);
  }, { status: 'closed' }));

for (const loss of ['session', 'access']) {
  test(`bulk ${loss} loss preserves attempted and not-attempted ledger without retry`, () =>
    fixture('?stage=closed&scope=2', async ui => {
      await previewBulk(ui);
      ui.setApi(({ init }) => init.method === 'DELETE'
        ? response(loss === 'session' ? 401 : 403, { code: 'staff_session_invalid', accessAllowed: false }) : undefined);
      ui.get('#bulk-delete-execute').click();
      await until(() => ui.get('#workspace').hidden, 'access loss signed out');
      const ledger = ui.get('#signed-out-message').textContent;
      assert.match(ledger, /Attempted: 1/);
      assert.match(ledger, /forbidden\/out of scope/);
      assert.match(ledger, /not attempted/);
      assert.equal(ui.calls.filter(call => call.init.method === 'DELETE').length, 1);
      assert.equal(ui.get('#bulk-delete-dialog').open, false);
    }, { status: 'closed', copyItems: [{ id: '9007199254740994', type: 'additional_copy',
      version: 'copy-v1', status: 'closed', libraryOrgId: 2, libraryOrgName: 'Library A', title: 'Copy' }] }));
}

const actorTenant = '11111111-1111-4111-8111-111111111111';
const actorA = { id: '20', tenantId: actorTenant };
const copyStorageKey = actor => `asap.staff.unconfirmedCopyCreation.${actor.tenantId}.${actor.id}`;
const submitCopy = ui => ui.get('#additional-copy-create-form').dispatchEvent(
  new ui.dom.window.Event('submit', { cancelable: true }));
async function openCopyPreview(ui) {
  [...document.querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === 'Additional copy').click();
  await until(() => ui.get('#additional-copy-create-dialog').open, 'additional-copy preview opened');
}

for (const exit of ['back', 'top-level', 'parent-close']) {
  test(`Additional Copy child and parent discard their transient context on ${exit}`, () =>
    fixture('?stage=pending_hold&scope=2', async ui => {
      await ui.open(); await openCopyPreview(ui);
      const form = ui.get('#additional-copy-create-form');
      if (exit === 'back') await ui.back();
      if (exit === 'top-level') ui.get('[data-view="profile"]').click();
      if (exit === 'parent-close') ui.get('#close-request').click();
      await settle();
      assert.equal(ui.get('#additional-copy-create-dialog').open, false);
      assert.equal(ui.get('#request-dialog').open, false);
      assert.equal(ui.get('#request-dialog-body').childElementCount, 0);
      assert.equal(ui.get('#additional-copy-create-summary').textContent, '');
      assert.equal(form.querySelector('button[type="submit"]').disabled, true);
      submitCopy(ui); await settle();
      assert.ok(!ui.calls.some(call => call.init.method === 'POST' && call.url.endsWith('/additional-copy')));
      assert.equal(ui.params().has('request'), false);
    }, { status: 'pending_hold', staff: actorA }));
}

test('closing only the Additional Copy preview returns focus to its live parent action', () =>
  fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    await openCopyPreview(ui);
    ui.get('#cancel-additional-copy').click(); await settle();
    assert.equal(ui.get('#request-dialog').open, true);
    assert.equal(ui.get('#additional-copy-create-dialog').open, false);
    assert.equal(document.activeElement.textContent.trim(), 'Additional copy');
  }, { status: 'pending_hold', staff: actorA }));

test('Additional Copy submission blocks departure; unresolved creation safely closes on navigation and survives reload', async () => {
  const storage = new Map();
  await fixture('?stage=pending_hold&scope=2', async ui => {
    await ui.open(); await openCopyPreview(ui);
    let reject;
    ui.setApi(({ pathname, init }) => pathname.endsWith('/additional-copy') && init.method === 'POST'
      ? new Promise((done, fail) => { reject = fail; }) : undefined);
    submitCopy(ui); await until(() => reject, 'copy submission pending');
    const currentUrl = ui.dom.window.location.href;
    await ui.back(); ui.get('[data-view="profile"]').click(); ui.get('#cancel-additional-copy').click(); await settle();
    assert.equal(ui.dom.window.location.href, currentUrl);
    assert.equal(ui.get('#additional-copy-create-dialog').open, true);
    reject(new Error('Creation response lost'));
    await until(() => /could not be confirmed/.test(ui.get('#app-status').textContent), 'uncertain creation recorded');
    ui.get('[data-view="profile"]').click(); await settle();
    assert.equal(ui.get('#additional-copy-create-dialog').open, false);
    assert.equal(ui.get('#request-dialog').open, false);
    assert.ok(ui.dom.window.sessionStorage.getItem(copyStorageKey(actorA)));
    submitCopy(ui); await settle();
    assert.equal(ui.calls.filter(call => call.init.method === 'POST' && call.url.endsWith('/additional-copy')).length, 1);
  }, { status: 'pending_hold', staff: actorA, storage });
  await fixture('?stage=additional_copies&scope=2', async ui => {
    assert.equal(ui.get('#additional-copy-create-review').hidden, false);
    assert.match(ui.get('#additional-copy-create-review-summary').textContent, /BIB 9001/);
    const saved = JSON.parse(ui.dom.window.sessionStorage.getItem(copyStorageKey(actorA)));
    const { recordId, emailPurchaseReminder, ...source } = saved;
    assert.deepEqual(source, { libraryOrgId: 2, bibid: 9001, sourceId: id, version: 'v1' });
    assert.equal(emailPurchaseReminder, false);
    assert.match(recordId, /^[0-9a-f-]{36}$/i);
  }, { staff: actorA, storage });
});

test('Additional Copy recovery survives A sign-out, cannot affect or be cleared by B, and restores when A returns', async () => {
  const storage = new Map();
  await fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    await openCopyPreview(ui);
    ui.setApi(({ pathname, init }) => {
      if (pathname.endsWith('/additional-copy') && init.method === 'POST') throw new Error('Response lost');
    });
    submitCopy(ui); await until(() => /could not be confirmed/.test(ui.get('#app-status').textContent), 'A unresolved creation');
    ui.get('#sign-out').click(); await until(() => ui.get('#workspace').hidden, 'A signed out');
    assert.ok(ui.dom.window.sessionStorage.getItem(copyStorageKey(actorA)));
  }, { status: 'pending_hold', staff: actorA, storage });
  const original = storage.get(copyStorageKey(actorA));
  await fixture(`?stage=pending_hold&scope=3&request=${id}`, async ui => {
    assert.equal(ui.get('#additional-copy-create-review').hidden, true);
    ui.get('#additional-copy-create-review-done').click();
    await openCopyPreview(ui);
    ui.setApi(({ pathname, init }) => pathname.endsWith('/additional-copy') && init.method === 'POST'
      ? response(200, { committed: true, additionalCopyRequestId: '71', finalStatus: 'open',
        additionalCopyRequest: { id: '71', status: 'open', version: 'copy-v1' } }) : undefined);
    submitCopy(ui); await until(() => !ui.get('#additional-copy-create-dialog').open, 'B ordinary creation completed');
    assert.equal(ui.calls.filter(call => call.init.method === 'POST' && call.url.endsWith('/additional-copy')).length, 1);
    assert.equal(ui.dom.window.sessionStorage.getItem(copyStorageKey(actorA)), original);
    assert.equal(ui.dom.window.sessionStorage.getItem(copyStorageKey({ ...actorA, id: '21' })), null);
  }, { status: 'pending_hold', staff: { ...actorA, id: '21' }, requestLibrary: 3, storage });
  await fixture('?stage=additional_copies&scope=2', async ui => {
    assert.equal(ui.get('#additional-copy-create-review').hidden, false);
    assert.equal(ui.dom.window.sessionStorage.getItem(copyStorageKey(actorA)), original);
  }, { staff: actorA, storage });
});

test('Additional Copy recovery is isolated across tenants even with the same StaffUser ID', () =>
  fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    assert.equal(ui.get('#additional-copy-create-review').hidden, true);
    await openCopyPreview(ui);
    assert.ok(ui.dom.window.sessionStorage.getItem(copyStorageKey(actorA)));
  }, { status: 'pending_hold', staff: { ...actorA, tenantId: '22222222-2222-4222-8222-222222222222' },
    storage: new Map([[copyStorageKey(actorA), JSON.stringify({ libraryOrgId: 2, bibid: 9001, sourceId: id, version: 'v1' })]]) }));

for (const saved of ['{broken', '{}', JSON.stringify({ libraryOrgId: 2, bibid: 9001, sourceId: '0', version: 'v1' }),
  JSON.stringify({ libraryOrgId: 2, bibid: 9001, sourceId: id, version: '' })]) {
  test(`malformed Additional Copy recovery is ignored (${saved})`, () =>
    fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
      assert.equal(ui.get('#additional-copy-create-review').hidden, true);
      await openCopyPreview(ui);
    }, { status: 'pending_hold', staff: actorA, storage: new Map([[copyStorageKey(actorA), saved]]) }));
}

test('obsolete global Additional Copy marker is removed without assigning it to the signing-in actor', () =>
  fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    await openCopyPreview(ui);
    assert.equal(ui.dom.window.sessionStorage.getItem('asap.staff.unconfirmedCopyCreation'), null);
    assert.equal(ui.dom.window.sessionStorage.getItem(copyStorageKey(actorA)), null);
  }, { status: 'pending_hold', staff: actorA, storage: new Map([['asap.staff.unconfirmedCopyCreation',
    JSON.stringify({ libraryOrgId: 2, bibid: 9001, sourceId: id, version: 'v1' })]]) }));

for (const kind of ['action-choice', 'assignment', 'pickup', 'hold-resolution']) {
  test(`${kind} draft is guarded and accepted navigation removes the form and its stale submit`, () =>
    fixture(`?stage=${kind === 'pickup' ? 'pending_hold' : 'suggestion'}&scope=2&request=${id}`, async ui => {
      let form;
      if (kind === 'action-choice') {
        [...document.querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === 'Reject').click();
        await until(() => ui.get('.action-choice select')?.options.length === 3, 'action choices loaded');
        form = ui.get('.action-choice'); ui.edit('.action-choice select', '2');
      }
      if (kind === 'assignment') {
        [...document.querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === 'Assign').click();
        await until(() => ui.get('.inline-form'), 'assignment loaded');
        form = ui.get('.inline-form'); ui.edit('.inline-form select', '21');
      }
      if (kind === 'pickup') {
        [...document.querySelectorAll('.action-bar button')].find(button => button.textContent.trim() === 'Pickup').click();
        await until(() => ui.get('.inline-form'), 'pickup loaded');
        form = ui.get('.inline-form'); ui.edit('.inline-form select', '102');
      }
      if (kind === 'hold-resolution') {
        form = ui.get('.resolution-form'); ui.edit('.resolution-form textarea[required]', 'Unsaved evidence');
      }
      const currentUrl = ui.dom.window.location.href;
      ui.get('[data-view="profile"]').click(); await settle();
      assert.equal(ui.dom.window.location.href, currentUrl);
      assert.equal(ui.get('#request-dialog').open, true);
      assert.equal(protectedUnload(ui), true);
      ui.allowDiscard(); ui.get('[data-view="profile"]').click(); await settle();
      assert.equal(ui.params().get('stage'), 'profile');
      assert.equal(ui.get('#request-dialog-body').childElementCount, 0);
      assert.equal(form.isConnected, false);
      const before = ui.calls.filter(call => call.init.method === 'POST').length;
      form.dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true })); await settle();
      assert.equal(ui.calls.filter(call => call.init.method === 'POST').length, before);
    }, { status: kind === 'pickup' ? 'pending_hold' : 'suggestion', holdOperation: kind === 'hold-resolution'
      ? { id: '71', version: 'op-v1', state: 'outcome_unknown', phase: 'acquired', canResolveNotPerformed: true }
      : null }));
}

const actionButton = (label, root = document.querySelector('.action-bar')) => {
  const button = [...root.querySelectorAll('button')].find(item => item.textContent.trim() === label);
  assert.ok(button, `${label} is available`);
  return button;
};
const requestMutations = ui => ui.calls.filter(call => ['POST', 'DELETE'].includes(call.init.method) &&
  !call.url.endsWith('/pickup-options'));
const submitForm = (ui, form) => form.dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true }));
async function openInlineDraft(ui, action, label) {
  actionButton(action).click();
  await until(() => ui.get(`select[aria-label="${label}"]`), `${action} form loaded`);
  return ui.get(`select[aria-label="${label}"]`).closest('form');
}
const assignmentLabel = 'Assign to staff member';
const pickupLabel = 'Preferred pickup branch';
const copyAssignmentLabel = 'Assign additional-copy task';
const blockedDraftMessage = /finish or cancel the current request changes/i;

for (const action of ['Claim', 'Unclaim', 'Clear claim']) {
  test(`dirty title assignment blocks ${action} without a confirmation`, () =>
    fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
      const form = await openInlineDraft(ui, 'Assign', assignmentLabel);
      ui.edit(`select[aria-label="${assignmentLabel}"]`, '21');
      ui.allowDiscard(); actionButton(action).click(); await settle();
      assert.equal(requestMutations(ui).length, 0);
      assert.equal(form.isConnected, true);
      assert.equal(form.querySelector('select').value, '21');
      assert.equal(ui.confirms.length, 0);
      assert.match(ui.get('#app-status').textContent, blockedDraftMessage);
    }, { request: { claimedByStaffUserId: action === 'Claim' ? null : action === 'Unclaim' ? '20' : '22' } }));
}

test('dirty title assignment submits only its own draft and the authoritative render starts clean', () =>
  fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
    const form = await openInlineDraft(ui, 'Assign', assignmentLabel);
    ui.edit(`select[aria-label="${assignmentLabel}"]`, '21');
    submitForm(ui, form);
    await until(() => !form.isConnected, 'authoritative assignment render');
    assert.deepEqual(JSON.parse(requestMutations(ui)[0].init.body), { version: 'v1', assigneeId: '21' });
    assert.equal(requestMutations(ui).length, 1);
    assert.equal(protectedUnload(ui), false);
    const reopened = await openInlineDraft(ui, 'Assign', assignmentLabel);
    assert.equal(reopened.querySelector('select').value, '20');
    assert.equal(protectedUnload(ui), false);
    submitForm(ui, form); await settle();
    assert.equal(requestMutations(ui).length, 1, 'the consumed form cannot submit again');
  }));

test('editor Save and assignment submission each preserve the competing dirty draft', () =>
  fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
    const form = await openInlineDraft(ui, 'Assign', assignmentLabel);
    ui.edit(`select[aria-label="${assignmentLabel}"]`, '21');
    ui.edit('.edit-form input', 'Unsaved title');
    submitForm(ui, ui.get('.edit-form')); await settle();
    assert.match(ui.get('#app-status').textContent, blockedDraftMessage);
    submitForm(ui, form); await settle();
    assert.match(ui.get('#app-status').textContent, /save or revert/i);
    assert.equal(requestMutations(ui).length, 0);
    assert.equal(form.querySelector('select').value, '21');
    assert.equal(ui.get('.edit-form input').value, 'Unsaved title');
    ui.edit('.edit-form input', 'Saved title');
    submitForm(ui, form);
    await until(() => !form.isConnected, 'assignment consumes its draft after editor returns to baseline');
    assert.equal(requestMutations(ui).length, 1);
  }));

for (const finish of ['baseline', 'cancel', 'clean']) {
  test(`${finish} title assignment permits Claim`, () =>
    fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
      const opener = actionButton('Assign');
      const form = await openInlineDraft(ui, 'Assign', assignmentLabel);
      if (finish !== 'clean') ui.edit(`select[aria-label="${assignmentLabel}"]`, '21');
      if (finish === 'baseline') ui.edit(`select[aria-label="${assignmentLabel}"]`, '20');
      if (finish === 'cancel') {
        actionButton('Cancel', form).click();
        assert.equal(form.isConnected, false);
        assert.equal(document.activeElement, opener);
      }
      assert.equal(protectedUnload(ui), false);
      actionButton('Claim').click();
      await until(() => requestMutations(ui).length === 1, 'clean assignment permits Claim');
      assert.ok(requestMutations(ui)[0].url.endsWith('/claim'));
      assert.equal(ui.confirms.length, 0);
    }));
}

test('assignment submission cannot consume a second dirty assignment form', () =>
  fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
    const first = await openInlineDraft(ui, 'Assign', assignmentLabel);
    first.querySelector('select').value = '21';
    actionButton('Assign').click();
    await until(() => document.querySelectorAll('.inline-form').length === 2, 'second assignment loaded');
    const second = ui.get('.inline-form');
    second.querySelector('select').value = '21';
    submitForm(ui, first); await settle();
    assert.equal(requestMutations(ui).length, 0);
    assert.equal(first.isConnected, true);
    assert.equal(second.isConnected, true);
    assert.match(ui.get('#app-status').textContent, blockedDraftMessage);
    actionButton('Cancel', second).click();
    assert.equal(protectedUnload(ui), true, 'cancelling one form preserves the other draft');
    submitForm(ui, first);
    await until(() => !first.isConnected, 'only remaining draft is consumed');
    assert.equal(requestMutations(ui).length, 1);
  }));

test('dirty pickup blocks Claim, Place hold and Additional Copy preview', () =>
  fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    const form = await openInlineDraft(ui, 'Pickup', pickupLabel);
    ui.edit(`select[aria-label="${pickupLabel}"]`, '102');
    ui.allowDiscard();
    for (const action of ['Claim', 'Place hold', 'Additional copy']) {
      actionButton(action).click(); await settle();
      assert.match(ui.get('#app-status').textContent, blockedDraftMessage);
    }
    assert.equal(requestMutations(ui).length, 0);
    assert.equal(ui.confirms.length, 0);
    assert.equal(form.querySelector('select').value, '102');
    assert.equal(ui.get('#additional-copy-create-dialog').open, false);
  }, { status: 'pending_hold', request: { capabilities: { canChangeWorkflowState: true, canPlaceHold: true } } }));

test('dirty pickup submits its own branch choice with the observed preference and version', () =>
  fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    const form = await openInlineDraft(ui, 'Pickup', pickupLabel);
    ui.edit(`select[aria-label="${pickupLabel}"]`, '102');
    submitForm(ui, form);
    await until(() => !form.isConnected, 'pickup authoritative result');
    assert.deepEqual(JSON.parse(requestMutations(ui)[0].init.body), { version: 'v1', preferredPickupBranchId: 102,
      currentPreferredPickupBranchIdAtLoad: 101, currentPreferredPickupBranchObservedAtLoad: true });
    assert.equal(protectedUnload(ui), false);
  }, { status: 'pending_hold' }));

test('Cancel pickup discards only pickup and returns focus without another confirmation', () =>
  fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    const opener = actionButton('Pickup');
    const form = await openInlineDraft(ui, 'Pickup', pickupLabel);
    ui.edit(`select[aria-label="${pickupLabel}"]`, '102');
    ui.edit('.edit-form input', 'Retained title');
    actionButton('Cancel', form).click();
    assert.equal(form.isConnected, false);
    assert.equal(document.activeElement, opener);
    assert.equal(ui.confirms.length, 0);
    assert.equal(ui.get('.edit-form input').value, 'Retained title');
    assert.equal(protectedUnload(ui), true);
    submitForm(ui, ui.get('.edit-form'));
    await until(() => requestMutations(ui).length === 1, 'editor Save after explicit pickup Cancel');
  }, { status: 'pending_hold' }));

for (const action of ['Purchase', 'Reject']) {
  test(`dirty rejection choice blocks replacement by ${action}, Claim and Close silently`, () =>
    fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
      const form = await openInlineDraft(ui, 'Reject', 'Rejection template');
      await until(() => form.querySelector('select').options.length === 3, 'rejection templates loaded');
      ui.edit('.action-choice select', '2'); ui.allowDiscard();
      for (const competing of [action, 'Claim', 'Close silently']) {
        actionButton(competing).click(); await settle();
        assert.equal(ui.get('.action-choice'), form);
        assert.equal(form.querySelector('select').value, '2');
        assert.match(ui.get('#app-status').textContent, blockedDraftMessage);
      }
      assert.equal(requestMutations(ui).length, 0);
      assert.equal(ui.confirms.length, 0);
    }));
}

test('dirty rejection choice submits its selected template and clears only after confirmed success', () =>
  fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
    const form = await openInlineDraft(ui, 'Reject', 'Rejection template');
    await until(() => form.querySelector('select').options.length === 3, 'rejection templates loaded');
    ui.edit('.action-choice select', '2');
    submitForm(ui, form); await settle();
    assert.equal(requestMutations(ui).length, 0, 'declined confirmation preserves the choice');
    assert.equal(form.querySelector('select').value, '2');
    ui.allowDiscard(); submitForm(ui, form);
    await until(() => !form.isConnected, 'rejection authoritative result');
    assert.deepEqual(JSON.parse(requestMutations(ui)[0].init.body), { version: 'v1', action: 'reject', rejectionTemplateId: '2' });
    assert.equal(protectedUnload(ui), false);
  }));

test('returning a rejection choice to baseline permits another action-choice', () =>
  fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
    const form = await openInlineDraft(ui, 'Reject', 'Rejection template');
    await until(() => form.querySelector('select').options.length === 3, 'rejection templates loaded');
    ui.edit('.action-choice select', '2'); ui.edit('.action-choice select', '1');
    assert.equal(protectedUnload(ui), false);
    actionButton('Purchase').click();
    assert.equal(form.isConnected, false);
    assert.equal(ui.get('.action-choice').getAttribute('aria-label'), 'purchase options');
    assert.equal(ui.confirms.length, 0);
  }));

test('dirty purchase reminder choice blocks competing actions and submits its own decision', () =>
  fixture(`?stage=suggestion&scope=2&request=${id}`, async ui => {
    actionButton('Purchase').click();
    const form = ui.get('.action-choice');
    form.querySelector('input').checked = true;
    for (const action of ['Reject', 'Claim']) {
      actionButton(action).click(); await settle();
      assert.equal(ui.get('.action-choice'), form);
      assert.equal(form.querySelector('input').checked, true);
    }
    assert.equal(requestMutations(ui).length, 0);
    ui.allowDiscard(); submitForm(ui, form);
    await until(() => !form.isConnected, 'purchase authoritative result');
    assert.deepEqual(JSON.parse(requestMutations(ui)[0].init.body), { version: 'v1', action: 'purchase', emailPurchaseReminder: true });
    assert.equal(protectedUnload(ui), false);
  }, { request: { bibid: null } }));

const resolutionOperation = { id: '71', version: 'op-v1', state: 'outcome_unknown', phase: 'create_started',
  attemptNumber: 2, executionEpoch: 3, patronBarcodeSnapshotMasked: '***0001', bibIdSnapshot: 9001,
  canReconcile: true, canResolveNotPerformed: true };
function fillResolution(ui) {
  const form = ui.get('.resolution-form');
  for (const label of form.querySelectorAll('label')) {
    const control = label.querySelector('input, textarea');
    if (!control || control.disabled) continue;
    if (control.type === 'checkbox') control.checked = true;
    else control.value = `Evidence for ${label.querySelector('span').textContent}`;
    control.dispatchEvent(new ui.dom.window.Event('input', { bubbles: true }));
  }
  return form;
}

test('dirty hold-resolution evidence blocks Claim and reconciliation without losing attestations', () =>
  fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    const form = fillResolution(ui);
    const values = [...form.querySelectorAll('input, textarea')].map(control => [control.value, control.checked]);
    ui.allowDiscard(); actionButton('Claim').click();
    actionButton('Reconcile provider state', ui.get('.hold-operation')).click(); await settle();
    assert.equal(requestMutations(ui).length, 0);
    assert.equal(ui.confirms.length, 0);
    assert.deepEqual([...form.querySelectorAll('input, textarea')].map(control => [control.value, control.checked]), values);
    assert.match(ui.get('#app-status').textContent, blockedDraftMessage);
  }, { status: 'pending_hold', holdOperation: resolutionOperation }));

test('dirty hold-resolution submits its own evidence and refreshes the authoritative request', () =>
  fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    const form = fillResolution(ui);
    let resolved = false;
    ui.setApi(({ pathname, init, request }) => {
      if (pathname.endsWith('/hold-operations/71/resolve') && init.method === 'POST') {
        resolved = true;
        return response(200, { committed: true, code: 'resolved', operationId: '71', finalStatus: 'pending_hold' });
      }
      if (resolved && pathname.endsWith(`/title-requests/${id}`)) return response(200,
        { ...request, version: 'v2', holdOperation: null });
    });
    ui.allowDiscard(); submitForm(ui, form);
    await until(() => !form.isConnected, 'resolution authoritative refresh');
    assert.equal(requestMutations(ui).length, 1);
    const body = JSON.parse(requestMutations(ui)[0].init.body);
    assert.equal(body.version, 'op-v1');
    assert.equal(body.requestVersion, 'v1');
    assert.equal(body.outcome, 'not_performed');
    assert.equal(body.reason, 'Evidence for Reason');
    assert.equal(body.evidenceReference, 'Evidence for Evidence reference');
    assert.equal(body.proofSource, 'Evidence for Evidence provenance');
    assert.equal(body.causalConnection, 'Evidence for Connection to this exact attempt');
    assert.equal(body.operationSpecificProofAttested, true);
    assert.equal(body.originalExecutorExcluded, true);
    assert.equal(body.executorExclusionAttested, true);
    assert.equal(protectedUnload(ui), false);
    assert.match(ui.get('#app-status').textContent, /resolved as confirmed|resolved as not performed/);
  }, { status: 'pending_hold', holdOperation: resolutionOperation }));

test('hold-resolution Revert restores its baseline without discarding a competing assignment', () =>
  fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    const form = fillResolution(ui);
    const assignment = await openInlineDraft(ui, 'Assign', assignmentLabel);
    ui.edit(`select[aria-label="${assignmentLabel}"]`, '21');
    ui.allowDiscard(); submitForm(ui, form); await settle();
    assert.equal(requestMutations(ui).length, 0, 'resolution cannot consume assignment');
    assert.match(ui.get('#app-status').textContent, blockedDraftMessage);
    actionButton('Revert resolution changes', form).click();
    assert.ok([...form.querySelectorAll('input, textarea')].every(control => control.type === 'checkbox' ? !control.checked : !control.value));
    assert.equal(document.activeElement, form.querySelector('select'));
    assert.equal(assignment.querySelector('select').value, '21');
    assert.equal(ui.confirms.length, 0);
    actionButton('Cancel', assignment).click();
    assert.equal(protectedUnload(ui), false);
    actionButton('Claim').click();
    await until(() => requestMutations(ui).length === 1, 'clean recovery permits Claim');
  }, { status: 'pending_hold', holdOperation: resolutionOperation }));

for (const action of ['Claim', 'Unclaim', 'Clear claim', 'Close task']) {
  test(`dirty Additional Copy assignment blocks ${action}`, () =>
    fixture('?stage=additional_copies&scope=2&request=71', async ui => {
      const form = await openInlineDraft(ui, 'Assign', copyAssignmentLabel);
      ui.edit(`select[aria-label="${copyAssignmentLabel}"]`, '21'); ui.allowDiscard();
      actionButton(action).click(); await settle();
      assert.equal(requestMutations(ui).length, 0);
      assert.equal(form.querySelector('select').value, '21');
      assert.equal(ui.confirms.length, 0);
      assert.match(ui.get('#app-status').textContent, blockedDraftMessage);
    }, { copyRequest: { claimedByStaffUserId: action === 'Unclaim' ? '20' : action === 'Clear claim' ? '22' : null,
      capabilities: { canClaim: true, canUnclaim: true, canClearClaim: true, canAssign: true, canClose: true } } }));
}

test('dirty Additional Copy assignment submits its own draft and protects its fields while pending', () =>
  fixture('?stage=additional_copies&scope=2&request=71', async ui => {
    const form = await openInlineDraft(ui, 'Assign', copyAssignmentLabel);
    ui.edit(`select[aria-label="${copyAssignmentLabel}"]`, '21');
    let resolve;
    ui.setOperation(() => new Promise(done => { resolve = done; }));
    submitForm(ui, form); await until(() => resolve, 'copy assignment dispatched');
    assert.equal(form.querySelector('select').disabled, true);
    actionButton('Claim').click(); ui.get('[data-view="profile"]').click();
    assert.equal(requestMutations(ui).length, 1);
    assert.equal(ui.get('#request-dialog').open, true);
    assert.deepEqual(JSON.parse(requestMutations(ui)[0].init.body), { version: 'copy-v1', assigneeId: '21' });
    resolve(response(200, { committed: true, request: { ...ui.readCopyRequest(), version: 'copy-v2' }, finalStatus: 'open' }));
    await until(() => !form.isConnected, 'copy assignment authoritative render');
    assert.equal(protectedUnload(ui), false);
    ui.setOperation(null);
    const reopened = await openInlineDraft(ui, 'Assign', copyAssignmentLabel);
    assert.equal(reopened.querySelector('select').value, '20');
    assert.equal(protectedUnload(ui), false);
  }, { copyRequest: {} }));

test('clean Additional Copy assignment permits Claim; Cancel clears only a dirty assignment', () =>
  fixture('?stage=additional_copies&scope=2&request=71', async ui => {
    const form = await openInlineDraft(ui, 'Assign', copyAssignmentLabel);
    assert.equal(protectedUnload(ui), false);
    actionButton('Claim').click(); await until(() => !form.isConnected, 'clean form does not block Claim');
    const opener = actionButton('Assign');
    const next = await openInlineDraft(ui, 'Assign', copyAssignmentLabel);
    ui.edit(`select[aria-label="${copyAssignmentLabel}"]`, '21');
    actionButton('Cancel', next).click();
    assert.equal(document.activeElement, opener);
    assert.equal(protectedUnload(ui), false);
    actionButton('Close task').click(); await settle();
    assert.equal(ui.confirms.length, 1, 'the task close keeps its existing confirmation');
  }, { copyRequest: {} }));

for (const exit of ['back', 'forward', 'close', 'sign-out']) {
  test(`Additional Copy assignment protects ${exit} and beforeunload`, () =>
    fixture('?stage=additional_copies&scope=2&request=71', async ui => {
      if (exit === 'forward') {
        ui.dom.window.history.pushState({ marker: 'next' }, '', '?stage=profile');
        await ui.back();
      }
      if (exit === 'back') ui.dom.window.history.pushState({ marker: 'detail' }, '', ui.dom.window.location.href);
      const form = await openInlineDraft(ui, 'Assign', copyAssignmentLabel);
      ui.edit(`select[aria-label="${copyAssignmentLabel}"]`, '21');
      const url = ui.dom.window.location.href;
      if (exit === 'back') await ui.back();
      if (exit === 'forward') await ui.forward();
      if (exit === 'close') ui.get('#close-request').click();
      if (exit === 'sign-out') ui.get('#sign-out').click();
      await settle();
      assert.equal(ui.dom.window.location.href, url);
      assert.equal(form.isConnected, true);
      assert.equal(form.querySelector('select').value, '21');
      assert.equal(protectedUnload(ui), true);
      assert.equal(requestMutations(ui).length, 0);
      assert.equal(ui.confirms.length, 1);
      ui.allowDiscard(); ui.get('[data-view="profile"]').click(); await settle();
      assert.equal(form.isConnected, false);
      submitForm(ui, form); await settle();
      assert.equal(requestMutations(ui).length, 0);
      assert.equal(protectedUnload(ui), false);
    }, { copyRequest: {} }));
}

test('Additional Copy preview rechecks inline drafts after its asynchronous load', () =>
  fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    const form = await openInlineDraft(ui, 'Assign', assignmentLabel);
    let resolvePreview;
    ui.setApi(({ pathname, init }) => pathname.endsWith('/additional-copy') && init.method === 'GET'
      ? new Promise(done => { resolvePreview = done; }) : undefined);
    actionButton('Additional copy').click(); await until(() => resolvePreview, 'preview load pending');
    ui.edit(`select[aria-label="${assignmentLabel}"]`, '21');
    resolvePreview(response(200, { version: 'v1', bibid: 9001, openCount: 0 })); await settle();
    assert.equal(ui.get('#additional-copy-create-dialog').open, false);
    assert.equal(form.querySelector('select').value, '21');
    assert.match(ui.get('#app-status').textContent, blockedDraftMessage);
    assert.equal(requestMutations(ui).length, 0);
  }, { status: 'pending_hold' }));

test('Additional Copy creation rechecks competing parent drafts before recording an attempt', () =>
  fixture(`?stage=pending_hold&scope=2&request=${id}`, async ui => {
    const form = await openInlineDraft(ui, 'Assign', assignmentLabel);
    await openCopyPreview(ui);
    ui.edit(`select[aria-label="${assignmentLabel}"]`, '21');
    submitCopy(ui); await settle();
    assert.equal(requestMutations(ui).length, 0);
    assert.equal(ui.dom.window.sessionStorage.getItem(copyStorageKey(actorA)), null);
    assert.equal(form.querySelector('select').value, '21');
    assert.equal(ui.get('#additional-copy-create-dialog').open, true);
    assert.match(ui.get('#app-status').textContent, blockedDraftMessage);
  }, { status: 'pending_hold', staff: actorA }));

for (const kind of ['assignment', 'pickup', 'rejection', 'copy-assignment']) {
  test(`stale ${kind} load cannot register against an authoritative replacement version`, () =>
    fixture(kind === 'copy-assignment' ? '?stage=additional_copies&scope=2&request=71'
      : `?stage=${kind === 'pickup' ? 'pending_hold' : 'suggestion'}&scope=2&request=${id}`, async ui => {
      let resolveLoad;
      ui.setApi(({ pathname }) => pathname.endsWith(kind === 'pickup' ? '/pickup-options'
        : kind === 'rejection' ? '/rejection-templates' : '/assignment-candidates')
        ? new Promise(done => { resolveLoad = done; }) : undefined);
      actionButton(kind === 'pickup' ? 'Pickup' : kind === 'rejection' ? 'Reject' : 'Assign').click();
      await until(() => resolveLoad, 'inline load pending');
      const titleReads = ui.calls.filter(call => call.url.includes(`/title-requests/${id}?`)).length;
      actionButton('Claim').click();
      await until(() => /claimed/i.test(ui.get('#app-status').textContent), 'authoritative Claim rendered');
      resolveLoad(response(200, kind === 'pickup' ? { version: 'v1', selectedPickupBranchId: 101,
        pickupBranches: [{ id: 101, label: 'Main' }, { id: 102, label: 'Branch' }] }
        : kind === 'rejection' ? { items: [{ id: '1', name: 'Default' }], defaultTemplateId: '1' }
          : { candidates: [{ id: '20', displayName: 'Staff A' }, { id: '21', displayName: 'Staff B' }] }));
      await settle();
      assert.equal(ui.get('.inline-form'), null);
      assert.equal(ui.get('.action-choice'), null);
      assert.equal(protectedUnload(ui), false);
      assert.equal(requestMutations(ui).length, 1);
      if (kind !== 'copy-assignment') assert.equal(ui.calls.filter(call => call.url.includes(`/title-requests/${id}?`)).length, titleReads);
    }, { status: kind === 'pickup' ? 'pending_hold' : 'suggestion', copyRequest: kind === 'copy-assignment' ? {} : undefined }));
}

test('Recent Requests honors the dirty Profile discard boundary', () =>
  fixture('?stage=suggestion&scope=2', async ui => {
    await ui.open(); ui.get('#close-request').click(); await settle();
    ui.get('[data-view="profile"]').click(); await settle();
    ui.edit('#weekly-email', 'draft@example.org');
    const currentUrl = ui.dom.window.location.href;
    ui.get('#recent-request-list button').click(); await settle();
    assert.equal(ui.dom.window.location.href, currentUrl);
    assert.equal(ui.get('#request-dialog').open, false);
    assert.equal(ui.get('#weekly-email').value, 'draft@example.org');
    ui.allowDiscard(); ui.get('#recent-request-list button').click();
    await until(() => ui.get('#request-dialog').open, 'confirmed Profile discard opens recent request');
    assert.equal(ui.get('#weekly-email').value, 'weekly@example.org');
    assert.equal(ui.params().get('request'), id);
  }, { staff: { ...actorA, ...profileStaff, authenticationEmail: 'staff@example.org' } }));

test('bulk access loss from a concurrent read retains the pending ledger until DELETE confirms, without attempting later items', () =>
  fixture('?stage=profile', async ui => {
    ui.get('[data-view="queue"]').click(); ui.get('[data-status="closed"]').click(); await settle();
    await previewBulk(ui);
    let resolveDelete;
    ui.setApi(({ pathname, init }) => {
      if (init.method === 'DELETE') return new Promise(done => { resolveDelete = done; });
      if (pathname.endsWith('/title-requests')) return response(401, { code: 'staff_session_invalid' });
    });
    ui.get('#bulk-delete-execute').click(); await until(() => resolveDelete, 'destructive request pending');
    const currentUrl = ui.dom.window.location.href;
    ui.get('#refresh-queue').click(); await until(() => ui.get('#workspace').hidden, 'concurrent read lost access');
    assert.match(ui.get('#signed-out-message').textContent, /outcome unconfirmed/);
    assert.match(ui.get('#signed-out-message').textContent, /not attempted/);
    assert.equal(protectedUnload(ui), true);
    await ui.back(); assert.equal(ui.dom.window.location.href, currentUrl);
    resolveDelete(response(200, { deleted: true }));
    await until(() => /Confirmed deleted: 1 of 2/.test(ui.get('#signed-out-message').textContent), 'late DELETE result retained');
    assert.match(ui.get('#signed-out-message').textContent, /Attempted: 1/);
    assert.match(ui.get('#signed-out-message').textContent, /not attempted/);
    assert.equal(ui.calls.filter(call => call.init.method === 'DELETE').length, 1);
    assert.equal(protectedUnload(ui), false);
  }, { status: 'closed', copyItems: [{ id: '71', type: 'additional_copy', status: 'closed', version: 'copy-v1',
    libraryOrgId: 2, libraryOrgName: 'Library A', title: 'Not attempted copy' }] }));

test('route validation retains its source and rechecks a draft created while target scope loads', () =>
  fixture('?stage=suggestion&scope=2', async ui => {
    await ui.open();
    const accepted = ui.dom.window.location.href;
    let validateScope;
    ui.setApi(({ pathname }) => pathname.endsWith('/organizations')
      ? new Promise(done => { validateScope = done; }) : undefined);
    ui.dom.window.history.back();
    await until(() => validateScope, 'target validation pending');
    assert.equal(ui.get('#request-dialog').open, true, 'source is mounted until validation and final admission');
    ui.edit('.edit-form input', 'New draft during target validation');
    validateScope(response(200, { data: [{ id: 2, name: 'Library A', active: true }] }));
    await until(() => ui.dom.window.location.href === accepted, 'rejected target restores accepted history entry');
    assert.equal(ui.get('#request-dialog').open, true);
    assert.equal(ui.get('.edit-form input').value, 'New draft during target validation');
    assert.equal(ui.confirms.length, 1);
    assert.equal(requestMutations(ui).length, 0);
  }));

for (const owner of ['request editor', 'Staff Suggestion']) {
  test(`programmatic Polaris changes to an already-dirty ${owner} require fresh navigation consent`, () =>
    fixture('?stage=suggestion&scope=2', async ui => {
      const isRequest = owner === 'request editor';
      if (isRequest) await ui.open();
      else {
        const scope = ui.get('#library-scope');
        scope.value = '3'; scope.dispatchEvent(new ui.dom.window.Event('change', { bubbles: true }));
        await until(() => ui.params().get('scope') === '3', 'source library selected');
        ui.get('#new-suggestion').click();
        const servicing = ui.get('[aria-label="Servicing library"]');
        servicing.value = '3'; servicing.dispatchEvent(new ui.dom.window.Event('change', { bubbles: true }));
        ui.edit('[aria-label="Patron barcode or name"]', '20000000000001');
        ui.get('#staff-suggestion-form').dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true }));
        await until(() => ui.get('.staff-suggestion-fields'), 'suggestion form ready');
      }
      const titleSelector = isRequest ? '.edit-form input' : '.staff-suggestion-fields input[maxlength="500"]';
      ui.edit(titleSelector, 'Already dirty');
      const accepted = ui.dom.window.location.href;
      let validateScope;
      const selected = { bibId: 9002, title: 'New Polaris title', author: 'New author', identifier: '9780000000002' };
      ui.setApi(({ pathname, init }) => {
        if (pathname.endsWith('/organizations')) return new Promise(done => { validateScope = done; });
        if (pathname.endsWith('/bib-lookup')) return response(200,
          JSON.parse(init.body).mode === 'bib' ? selected : { results: [selected], totalMatches: 1 });
      });
      ui.dom.window.confirm = message => { ui.confirms.push(message); return ui.confirms.length === 1; };
      ui.dom.window.history.back();
      await until(() => validateScope, 'route validation pending after first consent');
      assert.equal(ui.confirms.length, 1);
      const form = ui.get(isRequest ? '.edit-form' : '.staff-suggestion-fields');
      [...form.querySelectorAll('button')].find(button => button.textContent.trim() === 'Search Polaris catalog').click();
      ui.get('#polaris-form').dispatchEvent(new ui.dom.window.Event('submit', { cancelable: true }));
      await until(() => ui.get('#polaris-results button'), 'real Polaris search result');
      ui.get('#polaris-results button').click();
      await until(() => !ui.get('#polaris-dialog').open, 'programmatic Polaris selection applied');
      const newerTitle = ui.get(titleSelector).value;
      assert.match(newerTitle, /New Polaris title/);
      validateScope(response(200, { data: [{ id: 2, name: 'Library A', active: true }] }));
      await until(() => ui.dom.window.location.href === accepted, 'declined fresh consent restores source history');
      assert.equal(ui.confirms.length, 2, 'first consent cannot authorize discarding newer programmatic values');
      assert.equal(ui.get(isRequest ? '#request-dialog' : '#staff-suggestion-dialog').open, true);
      assert.equal(ui.get(titleSelector).value, newerTitle);
      assert.equal(protectedUnload(ui), true);
      assert.ok(!ui.calls.some(call => /\/(action|assign|sign-out)$/.test(call.url)));
      assert.ok(!ui.calls.some(call => call.url.endsWith('/suggestions')));
    }));
}

test('Analytics view round trip preserves scope/range and rejects the prior activation response', () =>
  fixture('?stage=suggestion&scope=2', async ui => {
    const pending = [];
    const analyticsData = (scope, range, label) => ({
      scope: { mode: scope === 'all' ? 'all' : 'library', libraryOrgId: scope === 'all' ? null : Number(scope), label, superAdmin: true },
      dateRange: { key: range, start: '2026-09-01', end: '2026-09-30' },
      availableLibraries: [{ orgId: 2, name: 'Library A' }], summary: {}, stageCounts: {},
      aging: {}, exceptions: {}, closedReasons: []
    });
    ui.setApi(({ pathname, parsed, init }) => pathname.endsWith('/analytics')
      ? new Promise(resolve => pending.push({ parsed, init, resolve })) : undefined);
    ui.get('[data-view="analytics"]').click();
    await until(() => pending.length === 1, 'initial Analytics request');
    pending[0].resolve(response(200, analyticsData('all', 'lastMonth', 'All libraries')));
    await until(() => ui.get('#analytics-scope'), 'Analytics controls');
    ui.get('#analytics-scope').value = '2';
    ui.get('#analytics-scope').dispatchEvent(new ui.dom.window.Event('change'));
    await until(() => pending.length === 2, 'library-scoped request');
    pending[1].resolve(response(200, analyticsData('2', 'lastMonth', 'Library A')));
    await until(() => ui.get('#analytics-date-range'), 'scoped controls');
    ui.get('#analytics-date-range').value = 'last90';
    ui.get('#analytics-date-range').dispatchEvent(new ui.dom.window.Event('change'));
    await until(() => pending.length === 3, 'selected range request');
    ui.get('[data-view="profile"]').click();
    assert.equal(pending[2].init.signal.aborted, true, 'leaving aborts visible reads');
    ui.get('[data-view="analytics"]').click();
    await until(() => pending.length === 4, 'first reactivated request');
    assert.equal(pending[3].parsed.searchParams.get('scope'), '2');
    assert.equal(pending[3].parsed.searchParams.get('range'), 'last90');
    pending[3].resolve(response(200, analyticsData('2', 'last90', 'Current activation')));
    await until(() => ui.get('#analytics-scope'), 'reactivated controls');
    assert.equal(ui.get('#analytics-scope').value, '2');
    assert.equal(ui.get('#analytics-date-range').value, 'last90');
    const focused = document.activeElement;
    pending[2].resolve(response(200, analyticsData('all', 'lastMonth', 'Stale activation')));
    await settle();
    assert.match(ui.get('#analytics-container').textContent, /Current activation/);
    assert.doesNotMatch(ui.get('#analytics-container').textContent, /Stale activation/);
    assert.equal(document.activeElement, focused);
  }));

test('programmatic hold-resolution Revert invalidates consent while a competing editor draft remains', () =>
  fixture('?stage=suggestion&scope=2', async ui => {
    await ui.open();
    ui.edit('.edit-form input', 'Competing editor draft');
    ui.edit('.resolution-form textarea[required]', 'Unsaved resolution reason');
    const accepted = ui.dom.window.location.href;
    let validateScope;
    ui.setApi(({ pathname }) => pathname.endsWith('/organizations')
      ? new Promise(resolve => { validateScope = resolve; }) : undefined);
    ui.dom.window.confirm = message => { ui.confirms.push(message); return ui.confirms.length === 1; };
    ui.dom.window.history.back();
    await until(() => validateScope, 'route validation after initial request consent');
    [...ui.get('.resolution-form').querySelectorAll('button')].find(button => button.textContent === 'Revert resolution changes').click();
    assert.equal(ui.get('.resolution-form textarea[required]').value, '');
    validateScope(response(200, { data: [{ id: 2, name: 'Library A', active: true }] }));
    await until(() => ui.dom.window.location.href === accepted, 'fresh consent rejects discarding competing editor');
    assert.equal(ui.confirms.length, 2);
    assert.equal(ui.get('.edit-form input').value, 'Competing editor draft');
    assert.equal(protectedUnload(ui), true);
  }, { holdOperation: { id: '81', version: 'hold-v1', state: 'unknown', phase: 'acquired',
    attemptNumber: 1, canResolveNotPerformed: true } }));

for (const settingsScope of ['2', 'system']) {
  test(`authoritative ${settingsScope} Settings configuration refresh invalidates cached Title form configuration`, () =>
    fixture('?stage=suggestion&scope=2', async ui => {
      let configurationReads = 0;
      ui.setApi(({ pathname }) => {
        if (pathname === '/api/asap/config') {
          configurationReads++;
          return response(200, { availableFormats: ['book'], formatLabels: { book: configurationReads === 1 ? 'Configuration A' : 'Configuration B' } });
        }
      });
      await ui.open();
      assert.match(ui.get('[aria-label="Format"]').textContent, /Configuration A/);
      ui.get('#close-request').click();
      ui.dom.window.history.pushState({}, '', `?stage=settings&settingsScope=${settingsScope}`);
      ui.dom.window.dispatchEvent(new ui.dom.window.PopStateEvent('popstate'));
      await until(() => !ui.get('#settings-view').hidden && !ui.get('#settings-form').hidden, 'authoritative configuration refresh');
      ui.get('[data-view="queue"]').click();
      await ui.open();
      assert.equal(configurationReads, 2, 'reopen fetches fresh public form configuration');
      assert.match(ui.get('[aria-label="Format"]').textContent, /Configuration B/);
    }));
}

test('staff roster review preserves the cached Title configuration', () =>
  fixture('?stage=settings&settingsScope=2', async ui => {
    let configurationReads = 0, rosterReads = 0;
    ui.setApi(({ pathname }) => {
      if (pathname === '/api/asap/config') {
        configurationReads++;
        return response(200, { availableFormats: ['book'], formatLabels: { book: 'Cached configuration' } });
      }
      if (pathname.endsWith('/users')) { rosterReads++; return response(200, { data: [] }); }
      if (pathname.endsWith('/audit')) return response(200, { data: [] });
    });
    ui.get('[data-view="queue"]').click(); await settle(); await ui.open();
    ui.get('#close-request').click(); await settle();
    ui.get('[data-view="settings"]').click(); await settle();
    ui.get('[data-settings-panel="staff"]').click();
    await until(() => rosterReads === 1 && /Staff access loaded/.test(ui.get('#staff-access-status').textContent), 'authoritative roster reviewed');
    ui.get('[data-view="queue"]').click(); await settle();
    await ui.open();
    assert.equal(configurationReads, 1, 'staff access review does not invalidate public form configuration');
    assert.match(ui.get('[aria-label="Format"]').textContent, /Cached configuration/);
  }));

test('programmatic Settings domain changes advance navigation consent without dirtying baseline population', () =>
  fixture('?stage=suggestion&scope=2', async ui => {
    ui.get('[data-view="settings"]').click();
    await until(() => !ui.get('#settings-form').hidden, 'clean Settings baseline');
    ui.get('[data-settings-panel="patron"]').click();
    assert.equal(protectedUnload(ui), false);
    ui.edit('#patron-login-note', 'Already dirty Settings');
    const accepted = ui.dom.window.location.href;
    let validateScope;
    ui.setApi(({ pathname }) => pathname.endsWith('/organizations')
      ? new Promise(resolve => { validateScope = resolve; }) : undefined);
    ui.dom.window.confirm = message => { ui.confirms.push(message); return ui.confirms.length === 1; };
    ui.dom.window.history.go(-2);
    await until(() => validateScope, 'route validation pending after Settings consent');
    ui.get('#add-publication-option').click();
    validateScope(response(200, { data: [{ id: 2, name: 'Library A', active: true }] }));
    await until(() => ui.dom.window.location.href === accepted, 'new domain value retains the Settings source');
    assert.equal(ui.confirms.length, 2);
    assert.equal(ui.get('#patron-login-note').value, 'Already dirty Settings');
    assert.equal(protectedUnload(ui), true);
  }));

for (const review of ['unauthenticated', 'active', 'unavailable']) {
  test(`lost Sign Out response uses the HTTP session boundary when review is ${review}`, () =>
    fixture('?stage=suggestion&scope=2', async ui => {
      let reviewing = false, reviewReads = 0;
      ui.setApi(({ pathname }) => {
        if (pathname.endsWith('/sign-out')) { reviewing = true; throw new Error('Response lost after cookie clear'); }
        if (reviewing && pathname.endsWith('/session')) {
          reviewReads++;
          if (review === 'unavailable') throw new Error('Session unavailable');
          return response(200, review === 'unauthenticated' ? { authenticated: false, accessAllowed: false }
            : { authenticated: true, accessAllowed: true, staff: ui.readStaff(), antiforgeryToken: 'fresh-token' });
        }
      });
      ui.get('#sign-out').click();
      await until(() => reviewReads && (review === 'active' ? /session is still active/.test(ui.get('#app-status').textContent)
        : ui.get('#workspace').hidden), 'authoritative Sign Out review completed');
      assert.equal(ui.get('#workspace').hidden, review !== 'active');
      if (review === 'unavailable') assert.match(ui.get('#signed-out-message').textContent, /Sign out result could not be confirmed/i);
      if (review === 'unauthenticated') assert.match(ui.get('#signed-out-message').textContent, /session ended|signed out/i);
      assert.doesNotMatch(ui.get('#signed-out-message').textContent, /did not complete/);
    }, { staff: { tenantId: 'audit-tenant', authenticationEmail: 'staff@example.org' } }));
}

test('same-actor Profile revision preserves an Operations attempt started before preference refresh', () =>
  fixture('?stage=operations', async ui => {
    let completeOperation;
    ui.setApi(({ pathname, init }) => {
      if (pathname.endsWith('/workflow/run-now')) return new Promise(done => { completeOperation = done; });
      if (pathname.endsWith('/profile')) {
        ui.setStaff({ version: 'actor-v2', weeklyActionSummaryEmail: 'updated@example.org' });
        return response(200, { staff: ui.readStaff() });
      }
    });
    ui.get('#run-workflow-now').click(); await until(() => completeOperation, 'operation pending');
    ui.get('[data-view="profile"]').click(); await settle();
    ui.edit('#weekly-email', 'updated@example.org'); submitProfile(ui);
    await until(() => /Profile saved\./.test(ui.get('#app-status').textContent), 'preferences refreshed');
    completeOperation(response(202, { code: 'queued' })); await settle();
    assert.equal(ui.params().get('stage'), 'profile');
    assert.equal(ui.get('#weekly-email').value, 'updated@example.org');
    ui.get('[data-view="operations"]').click(); await settle();
    assert.equal(ui.get('#run-workflow-now').disabled, false);
    assert.equal(ui.dom.window.sessionStorage.getItem('asap.staff.operation..20'), null);
  }, { staff: profileStaff }));

// #353 pins current behavior before extraction. The two explicitly labelled
// limitations below are improved by the owning draft/controller phases.
test('characterization: declined editor Revert preserves both editor and inline draft', () =>
  fixture(`?request=${id}`, async ui => {
    const assignment = await openInlineDraft(ui, 'Assign', assignmentLabel);
    ui.edit(`select[aria-label="${assignmentLabel}"]`, '21');
    ui.edit('.edit-form input', 'Editor draft');
    actionButton('Revert changes', ui.get('.edit-form')).click();
    assert.equal(ui.get('.edit-form input').value, 'Editor draft');
    assert.equal(assignment.querySelector('select').value, '21');
    assert.equal(requestMutations(ui).length, 0);
    assert.equal(protectedUnload(ui), true);
  }));

test('editor Revert preserves competing inline UI and its draft registration', () =>
  fixture(`?request=${id}`, async ui => {
    const assignment = await openInlineDraft(ui, 'Assign', assignmentLabel);
    ui.edit(`select[aria-label="${assignmentLabel}"]`, '21');
    ui.edit('.edit-form input', 'Editor draft');
    ui.allowDiscard(); actionButton('Revert changes', ui.get('.edit-form')).click();
    assert.equal(assignment.isConnected, true);
    assert.equal(ui.get('.edit-form input').value, 'Saved title');
    assert.equal(protectedUnload(ui), true);
    actionButton('Claim').click(); await settle();
    assert.equal(requestMutations(ui).length, 0);
    assert.equal(assignment.querySelector('select').value, '21');
    submitForm(ui, assignment); await until(() => !assignment.isConnected, 'retained inline draft submits');
    assert.equal(requestMutations(ui).length, 1);
  }));

test('Additional Copy reminder owns a guarded draft and explicit Cancel lifetime', () =>
  fixture(`?stage=pending_hold&request=${id}`, async ui => {
    const opener = actionButton('Additional copy'); opener.click();
    await until(() => ui.get('#additional-copy-create-dialog').open, 'copy preview');
    ui.get('#additional-copy-reminder').checked = true;
    ui.get('#additional-copy-reminder').dispatchEvent(new ui.dom.window.Event('change', { bubbles: true }));
    assert.equal(protectedUnload(ui), true, 'changed reminder is an owned draft');
    ui.get('#cancel-additional-copy').click();
    assert.equal(ui.get('#additional-copy-create-dialog').open, false);
    assert.equal(ui.get('#additional-copy-reminder').checked, false);
    assert.equal(protectedUnload(ui), false, 'Cancel releases only the reminder registration');
    assert.equal(document.activeElement, opener);
    assert.equal(requestMutations(ui).length, 0);
  }, { status: 'pending_hold' }));

test('Cancel Additional Copy reminder preserves a competing parent editor draft', () =>
  fixture(`?stage=pending_hold&request=${id}`, async ui => {
    await openCopyPreview(ui);
    ui.edit('.edit-form input', 'Parent draft');
    ui.get('#additional-copy-reminder').checked = true;
    ui.get('#cancel-additional-copy').click();
    assert.equal(ui.get('.edit-form input').value, 'Parent draft');
    assert.equal(protectedUnload(ui), true);
    assert.equal(requestMutations(ui).length, 0);
  }, { status: 'pending_hold' }));

test('Additional Copy preview cannot attach a newer source version to a stale parent', () =>
  fixture(`?stage=pending_hold&request=${id}`, async ui => {
    ui.setApi(({ pathname, init }) => pathname.endsWith('/additional-copy') && init.method === 'GET'
      ? response(200, { version: 'v2', bibid: 9001, openCount: 0 }) : undefined);
    actionButton('Additional copy').click();
    await until(() => /request changed.*Reload/i.test(ui.get('#app-status').textContent), 'preview detects invalidated parent');
    assert.equal(ui.get('#additional-copy-create-dialog').open, false);
    assert.equal(requestMutations(ui).length, 0);
    assert.equal(ui.dom.window.sessionStorage.getItem(copyStorageKey(actorA)), null);
  }, { status: 'pending_hold', staff: actorA }));

test('authoritative parent replacement disposes clean Copy child and its stale submit', () =>
  fixture(`?stage=pending_hold&request=${id}`, async ui => {
    await openCopyPreview(ui);
    actionButton('Claim').click();
    await until(() => !ui.get('#additional-copy-create-dialog').open, 'parent replacement disposes child');
    assert.equal(protectedUnload(ui), false);
    submitCopy(ui); await settle();
    assert.equal(ui.calls.filter(call => call.init.method === 'POST' && call.url.endsWith('/additional-copy')).length, 0);
  }, { status: 'pending_hold', staff: actorA }));

test('characterization: definitive inline failure preserves values and competing registration', () =>
  fixture(`?request=${id}`, async ui => {
    const assignment = await openInlineDraft(ui, 'Assign', assignmentLabel);
    ui.edit(`select[aria-label="${assignmentLabel}"]`, '21');
    ui.setOperation(() => response(400, { message: 'Assignment rejected' }));
    submitForm(ui, assignment);
    await until(() => /Assignment rejected/.test(ui.get('#app-status').textContent), 'definite failure');
    assert.equal(assignment.querySelector('select').value, '21');
    assert.equal(assignment.querySelector('select').disabled, false);
    assert.equal(protectedUnload(ui), true);
    actionButton('Claim').click(); await settle();
    assert.equal(requestMutations(ui).length, 1);
    assert.match(ui.get('#app-status').textContent, blockedDraftMessage);
  }));

test('characterization: clean open assignment is disposed on departure and cannot submit', () =>
  fixture(`?request=${id}`, async ui => {
    const assignment = await openInlineDraft(ui, 'Assign', assignmentLabel);
    ui.get('[data-view="profile"]').click(); await settle();
    assert.equal(ui.confirms.length, 0);
    assert.equal(assignment.isConnected, false);
    submitForm(ui, assignment); await settle();
    assert.equal(requestMutations(ui).length, 0);
    assert.equal(ui.get('#profile-view').hidden, false);
  }));

for (const view of ['additional-copies', 'settings']) {
  test(`characterization: Recent Requests from ${view} converges on title route`, () =>
    fixture('?stage=suggestion&scope=2', async ui => {
      await ui.open(); ui.get('#close-request').click(); await settle();
      ui.get(`[data-view="${view}"]`).click(); await settle();
      ui.get('#recent-request-list button').click();
      await until(() => ui.get('#request-dialog').open, 'recent detail');
      assert.equal(ui.params().get('request'), id);
      assert.equal(ui.params().get('stage'), 'suggestion');
      assert.equal(ui.get('#queue-view').hidden, false);
      assert.equal(ui.get('#additional-copy-view').hidden, true);
      assert.equal(ui.get('#settings-view').hidden, true);
    }, { staff: { ...actorA, authenticationEmail: 'staff@example.org' } }));
}

test('Suggestion lookup returning exactly to baseline releases its draft guard', () =>
  fixture('?stage=suggestion&scope=2', async ui => {
    ui.get('#new-suggestion').click();
    ui.edit('input[aria-label="Patron barcode or name"]', 'Unsaved patron');
    assert.equal(protectedUnload(ui), true);
    ui.edit('input[aria-label="Patron barcode or name"]', '');
    assert.equal(protectedUnload(ui), false);
    ui.get('#close-staff-suggestion').click(); await settle();
    assert.equal(ui.get('#staff-suggestion-dialog').open, false);
    assert.equal(ui.confirms.length, 0);
  }));

for (const [parameter, value] of [['stage', 'new'], ['status', 'submitted']]) {
  test(`legacy Title detail preserves the supported ${parameter}=${value} alias and unrelated URL context`, () =>
    fixture(`?${parameter}=${value}&request=${id}&marker=legacy#details`, async ui => {
      assert.equal(ui.params().get(parameter), value);
      assert.equal(ui.params().get('marker'), 'legacy');
      assert.equal(ui.dom.window.location.hash, '#details');
      assert.equal(ui.get('#request-dialog').open, true);
      assert.equal(ui.get('#queue-view').hidden, false);
    }));
}

(async () => {
  let failed = 0;
  const selected = cases.filter(item => !process.argv[2] || item.name.includes(process.argv[2]));
  assert.ok(selected.length, 'the requested journey filter must discover tests');
  for (const item of selected) {
    try { await item.body(); console.log(`PASS ${item.name}`); }
    catch (error) { failed += 1; console.error(`FAIL ${item.name}: ${error.message}`); }
  }
  assert.equal(failed, 0, `${failed}/${selected.length} navigation/draft journeys failed`);
  console.log(`${selected.length} staff navigation/draft journeys passed.`);
})().catch(error => { console.error(error); process.exitCode = 1; });
