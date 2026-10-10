const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const root = path.resolve(__dirname, '..');
const source = path.join(root, 'src', 'Asap.Web', 'Frontend');
const response = (status, body) => ({
  ok: status < 400,
  status,
  statusText: status < 400 ? 'OK' : 'Request failed',
  json: async () => body
});
const settle = async () => {
  for (let index = 0; index < 8; index += 1) await new Promise(resolve => setImmediate(resolve));
};

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((nextResolve, nextReject) => {
    resolve = nextResolve;
    reject = nextReject;
  });
  return { promise, resolve, reject };
}

function assertRequest(request, pathPart, expectedScope, expectedBody) {
  assert.ok(request, `request for ${pathPart} should exist`);
  const url = new URL(`https://localhost${request.url}`);
  assert.equal(url.pathname, pathPart);
  assert.equal(url.searchParams.get('organizationId'), expectedScope);
  assert.equal(request.options.method, 'POST');
  assert.deepEqual(request.options.body ? JSON.parse(request.options.body) : null, expectedBody);
}

function operationResult(url) {
  const parsed = new URL(url, 'https://localhost');
  const { pathname, searchParams } = parsed;
  const organizationId = Number(searchParams.get('organizationId') || 1);
  if (pathname === '/api/asap/staff/workflow/run-now' ||
      pathname === '/api/asap/staff/workflow/weekly-summary/run-now') {
    const data = { jobId: 'workflow-job-1', organizationId };
    if (searchParams.get('force') === 'true') data.manualRunId = searchParams.get('operationId');
    return response(202, { code: 'queued', ...data });
  }
  if (pathname === '/api/asap/staff/email-operations/test') {
    return response(202, { code: 'queued', data: {
      id: '9007199254740995', code: null, dispatchDelayed: false, version: 'email-test-v1'
    } });
  }
  const retry = pathname.match(/^\/api\/asap\/staff\/email-operations\/(\d+)\/retry$/);
  if (retry) {
    return response(202, { code: 'queued', data: {
      id: retry[1], dispatchDelayed: false, version: 'email-retry-v1'
    } });
  }
  throw new Error(`Unexpected operation POST ${url}`);
}

(async () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-operations-ui-'));
  let dom;
  let ordinaryDom;
  try {
    fs.cpSync(path.join(source, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(source, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    dom = new JSDOM(fs.readFileSync(path.join(source, 'staff', 'index.html'), 'utf8'), {
      url: 'https://localhost/staff/?stage=operations',
      pretendToBeVisual: true
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.URLSearchParams = dom.window.URLSearchParams;
    global.Node = dom.window.Node;

    const requests = [];
    const operationsReads = new Map();
    let session = {
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'operations-af',
      staff: {
        id: '7', tenantId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', objectId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
        displayName: 'Super Admin', userPrincipalName: 'admin@example.org', notificationEmail: 'admin@example.org',
        role: 'super_admin', organizationId: 1, organizationName: 'System', organizationIsActive: true,
        weeklyActionSummaryEnabled: true, weeklyActionSummaryEmail: null, purchaseReminderDefault: false,
        additionalCopyReminderDefault: false, defaultMineUnclaimedFilter: false, version: 'staff-version'
      }
    };
    const organizations = [
      { id: 1, name: 'System', abbreviation: 'SYS', organizationCodeId: 1, parentOrganizationId: null, isActive: true, lastSyncedUtc: null, version: 'org-1' },
      { id: 2, name: 'Library Two', abbreviation: 'L2', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, lastSyncedUtc: null, version: 'org-2' },
      { id: 3, name: 'Inactive Library', abbreviation: 'L3', organizationCodeId: 2, parentOrganizationId: 1, isActive: false, lastSyncedUtc: null, version: 'org-3' },
      { id: 20, name: 'Branch Twenty', abbreviation: 'B20', organizationCodeId: 3, parentOrganizationId: 2, isActive: true, lastSyncedUtc: null, version: 'org-20' },
      { id: 22, name: 'Inactive Branch', abbreviation: 'B22', organizationCodeId: 3, parentOrganizationId: 2, isActive: false, lastSyncedUtc: null, version: 'org-22' },
      { id: 21, name: 'Unclassified Reference', abbreviation: null, organizationCodeId: null, parentOrganizationId: 2, isActive: true, lastSyncedUtc: null, version: 'org-21' }
    ];
    const activeLibraries = [{ id: organizations[1].id, name: organizations[1].name }];
    const titleRequestPayload = scope => ({ scope, items: [], organizations: activeLibraries });
    const queuePayload = scope => ({
      scope,
      scopeOrganizationId: scope === 'all' ? 1 : Number(scope),
      organizations: activeLibraries,
      items: [{
        queueName: 'IdentifierProcessing', scopeOrganizationId: scope === 'all' ? 1 : Number(scope),
        cycleMaxId: 12, lastCreatedUtc: '2026-09-14T12:00:00Z', lastItemId: 11,
        lastOutcomeItemId: 11, lastOutcomeCode: 'processed', lastOutcomeUtc: '2026-09-14T12:01:00Z',
        updatedUtc: '2026-09-14T12:01:00Z', version: 'queue-version'
      }]
    });
    const emailPayload = {
      items: [
        { id: '70', status: 'failed', deliveryClass: 'operational_test', lastErrorCode: '<unsafe-error>', canRetry: false, suppressionReason: null, createdUtc: '2026-09-14T12:00:00Z', version: 'unknown-version' },
        { id: '71', status: 'failed', deliveryClass: 'business_event', lastErrorCode: 'mail_not_configured', suppressionReason: null, createdUtc: '2026-09-14T11:59:00Z', version: 'missing-proof-version' },
        { id: '9007199254740993', status: 'failed', deliveryClass: 'operational_test', lastErrorCode: 'mail_not_configured', canRetry: true, suppressionReason: null, createdUtc: '2026-09-14T11:58:00Z', version: 'email-version' },
        { id: '73', status: 'failed', deliveryClass: 'business_event', lastErrorCode: 'mail_not_configured', canRetry: false, suppressionReason: null, createdUtc: '2026-09-14T11:57:00Z', version: 'dispatch-marker-version' },
        { id: '42', status: 'sent', deliveryClass: 'business_event', lastErrorCode: null, suppressionReason: null, createdUtc: '2026-09-14T11:00:00Z', version: 'sent-version' }
      ]
    };

    global.fetch = async (url, options = {}) => {
      requests.push({ url, options });
      if (url.endsWith('/session')) return response(200, session);
      if (url.endsWith('/api/asap/staff/organizations')) return response(200, { code: 'ok', data: organizations });
      if (url.includes('/workflow/queues')) {
        const scope = new URL(`https://localhost${url}`).searchParams.get('organizationId') || 'all';
        const pending = operationsReads.get(`queue:${scope}`);
        if (pending) return pending.promise;
        return response(200, queuePayload(scope));
      }
      if (url.includes('/api/asap/staff/title-requests?')) {
        const scope = new URL(`https://localhost${url}`).searchParams.get('scope') || 'all';
        return response(200, titleRequestPayload(scope));
      }
      if (url.includes('/email-operations') && !url.includes('/retry') && !url.includes('/test')) {
        const scope = new URL(`https://localhost${url}`).searchParams.get('organizationId') || 'all';
        const pending = operationsReads.get(`email:${scope}`);
        if (pending) return pending.promise;
        return response(200, emailPayload);
      }
      if (url.includes('/api/asap/staff/email-readiness')) {
        return response(200, { state: 'non_delivery' });
      }
      if (url.includes('/api/asap/staff/additional-copies?')) {
        return response(200, { scope: 'all', status: 'open', items: [], availableLibraries: activeLibraries });
      }
      if (options.method === 'POST') return operationResult(url);
      throw new Error(`Unexpected request ${url}`);
    };

    const workflow = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    await workflow.createWorkflowApp().start();
    const operationsTab = document.getElementById('operations-view-tab');
    assert.equal(operationsTab.hidden, false, 'admins should see Operations');
    assert.equal(document.getElementById('operations-view').hidden, false);
    assert.equal(document.getElementById('operations-scope').value, 'all');
    assert.deepEqual([...document.getElementById('operations-scope').options].map(option => option.value), ['all', '2'],
      'the operational selector contains only active code-2 libraries');
    assert.equal(document.querySelectorAll('#email-operations-table tbody tr').length, 5);
    assert.equal(document.querySelector('#email-operations-table').textContent.includes('<unsafe-error>'), true);
    assert.equal(document.querySelector('#email-operations-table').querySelector('script'), null, 'runtime text must not become markup');
    assert.match(document.querySelector('#email-operations-table').textContent, /9007199254740993/);
    assert.match(document.getElementById('queue-progress-table').textContent, /12/);
    assert.match(document.getElementById('queue-progress-table').textContent, /11/);
    assert.match(document.getElementById('queue-progress-table').textContent, /processed/);

    const scope = document.getElementById('operations-scope');
    scope.value = '2';
    scope.dispatchEvent(new dom.window.Event('change'));
    await settle();
    document.getElementById('run-workflow-now').click();
    await settle();
    assertRequest(requests.find(item => item.url.includes('/workflow/run-now')), '/api/asap/staff/workflow/run-now', '2', null);

    scope.value = 'all';
    scope.dispatchEvent(new dom.window.Event('change'));
    await settle();
    document.getElementById('run-weekly-now').click();
    await settle();
    assertRequest(requests.find(item => item.url.includes('force=false')), '/api/asap/staff/workflow/weekly-summary/run-now', null, null);
    scope.value = '2';
    scope.dispatchEvent(new dom.window.Event('change'));
    await settle();
    document.getElementById('force-weekly-now').click();
    await settle();
    assertRequest(requests.find(item => item.url.includes('force=true')), '/api/asap/staff/workflow/weekly-summary/run-now', '2', null);
    document.getElementById('send-test-email').click();
    await settle();
    assertRequest(requests.find(item => item.url.includes('/email-operations/test')), '/api/asap/staff/email-operations/test', '2', null);

    const emailRows = [...document.querySelectorAll('#email-operations-table tbody tr')];
    const rowForId = id => emailRows.find(row => row.textContent.includes(id));
    assert.equal(rowForId('70').querySelector('button'), null, 'unknown failures are not retryable without explicit server proof');
    assert.equal(rowForId('71').querySelector('button'), null, 'missing canRetry proof is denied even for mail_not_configured');
    assert.ok(rowForId('9007199254740993').querySelector('button'), 'only the explicitly certified no-send row exposes Retry');
    assert.equal(rowForId('73').querySelector('button'), null, 'dispatch-start evidence overrides the no-send error label');
    assert.equal(rowForId('42').querySelector('button'), null, 'sent rows never expose Retry');
    const retry = rowForId('9007199254740993').querySelector('button');
    retry.click();
    await settle();
    const retryRequest = requests.find(item => item.url.includes('/email-operations/9007199254740993/retry'));
    assertRequest(retryRequest, '/api/asap/staff/email-operations/9007199254740993/retry', null, { version: 'email-version' });
    assert.equal(document.querySelectorAll('#email-operations-table button').length, 1,
      'only the explicitly certified row exposes Retry after list refresh');

    scope.value = '2';
    scope.dispatchEvent(new dom.window.Event('change'));
    await settle();
    document.querySelector('[data-view="queue"]').click();
    await settle();
    assert.equal(scope.value, '2', 'queue refresh must not overwrite Operations scope');
    assert.equal(document.getElementById('library-scope').value, 'all',
      'Operations loads must not overwrite the operational queue scope control');
    assert.deepEqual([...document.getElementById('library-scope').options].map(option => option.value), ['all', '2'],
      'the queue selector only presents active code-2 libraries');
    const readinessCount = requests.filter(item => item.url.includes('/email-readiness')).length;
    document.querySelector('[data-view="additional-copies"]').click();
    await settle();
    assert.equal(scope.value, '2', 'Additional-copy choice projections preserve the valid Operations library scope');
    assert.deepEqual([...scope.options].map(option => option.value), ['all', '2'],
      'the Additional-copy endpoint choice projection must retain its authorized library');
    document.querySelector('[data-view="queue"]').click();
    await settle();
    assert.equal(scope.value, '2', 'Title-request choice projections must not overwrite the independent Operations scope');
    const titleRequest = requests.filter(item => item.url.includes('/api/asap/staff/title-requests?')).at(-1);
    assert.ok(titleRequest, 'switching to the request queue must load the actual TitleRequestView endpoint');
    assert.equal(new URL(`https://localhost${titleRequest.url}`).searchParams.get('scope'), 'all',
      'the request queue keeps its independent All-libraries scope while Operations remains scoped to Library Two');
    assert.deepEqual([...document.getElementById('library-scope').options].map(option => option.value), ['all', '2'],
      'the TitleRequestView OrganizationChoice projection must retain its authorized library');
    assert.equal(requests.filter(item => item.url.includes('/email-readiness')).length, readinessCount,
      'switching between views with the same email scope must not start another readiness request');

    document.querySelector('[data-view="operations"]').click();
    await settle();
    const oldQueue = deferred();
    const oldEmail = deferred();
    operationsReads.set('queue:2', oldQueue);
    operationsReads.set('email:2', oldEmail);
    scope.dispatchEvent(new dom.window.Event('change'));
    await settle();
    scope.value = 'all';
    scope.dispatchEvent(new dom.window.Event('change'));
    await settle();
    oldQueue.resolve(response(200, queuePayload('2')));
    oldEmail.reject(new Error('stale old scope failure'));
    await settle();
    assert.doesNotMatch(document.getElementById('app-status').textContent, /stale old scope failure/);
    assert.equal(scope.value, 'all');

    ordinaryDom = new JSDOM(fs.readFileSync(path.join(source, 'staff', 'index.html'), 'utf8'), {
      url: 'https://localhost/staff/?stage=operations',
      pretendToBeVisual: true
    });
    global.window = ordinaryDom.window;
    global.document = ordinaryDom.window.document;
    global.FormData = ordinaryDom.window.FormData;
    global.URLSearchParams = ordinaryDom.window.URLSearchParams;
    global.Node = ordinaryDom.window.Node;
    session = { ...session, staff: { ...session.staff, role: 'staff', organizationId: 2 } };
    await workflow.createWorkflowApp().start();
    assert.equal(ordinaryDom.window.document.getElementById('operations-view-tab').hidden, true);
    assert.equal(ordinaryDom.window.document.getElementById('operations-view').hidden, true);
    console.log('Staff Operations jsdom behavior tests passed.');
  } finally {
    dom?.window.close();
    ordinaryDom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
