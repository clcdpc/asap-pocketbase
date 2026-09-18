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
  const actualBody = request.options.body ? JSON.parse(request.options.body) : null;
  if (typeof expectedBody === 'function') expectedBody(actualBody);
  else assert.deepEqual(actualBody, expectedBody);
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
    const organizations = [{ id: 2, name: 'Library Two' }, { id: 3, name: 'Library Three' }];
    const queuePayload = scope => ({
      scope,
      scopeOrganizationId: scope === 'all' ? 1 : Number(scope),
      organizations,
      items: [{
        queueName: 'IdentifierProcessing', scopeOrganizationId: scope === 'all' ? 1 : Number(scope),
        cycleMaxId: 12, lastCreatedUtc: '2026-09-14T12:00:00Z', lastItemId: 11,
        lastOutcomeItemId: 11, lastOutcomeCode: 'processed', lastOutcomeUtc: '2026-09-14T12:01:00Z',
        updatedUtc: '2026-09-14T12:01:00Z', version: 'queue-version'
      }]
    });
    const emailPayload = {
      items: [
        { id: 41, status: 'failed', deliveryClass: 'operational_test', lastErrorCode: '<unsafe-error>', suppressionReason: null, createdUtc: '2026-09-14T12:00:00Z', version: 'email-version' },
        { id: 42, status: 'sent', deliveryClass: 'business_event', lastErrorCode: null, suppressionReason: null, createdUtc: '2026-09-14T11:00:00Z', version: 'sent-version' }
      ]
    };

    global.fetch = async (url, options = {}) => {
      requests.push({ url, options });
      if (url.endsWith('/session')) return response(200, session);
      if (url.endsWith('/api/asap/staff/organizations')) return response(200, organizations.map(item => ({
        id: item.id,
        displayName: item.name,
        abbreviation: null,
        active: true,
        version: 'organization-version'
      })));
      if (url.includes('/workflow/queues')) {
        const scope = new URL(`https://localhost${url}`).searchParams.get('organizationId') || 'all';
        const pending = operationsReads.get(`queue:${scope}`);
        if (pending) return pending.promise;
        return response(200, queuePayload(scope));
      }
      if (url.includes('/email-operations/') && !url.includes('/retry')) {
        return response(200, { code: 'ok', data: {
          id: 99,
          status: 'sent',
          deliveryMode: 'capture',
          providerMessageId: 'file:test-message',
          version: 'status-version'
        } });
      }
      if (url.includes('/email-operations') && !url.includes('/retry') && !url.includes('/test')) {
        const scope = new URL(`https://localhost${url}`).searchParams.get('organizationId') || 'all';
        const pending = operationsReads.get(`email:${scope}`);
        if (pending) return pending.promise;
        return response(200, emailPayload);
      }
      if (options.method === 'POST') return response(202, { code: 'queued', manualRunId: url.includes('force=true') ? 'forced-run-1' : undefined });
      throw new Error(`Unexpected request ${url}`);
    };

    const workflow = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    await workflow.createWorkflowApp().start();
    const operationsTab = document.getElementById('operations-view-tab');
    assert.equal(operationsTab.hidden, false, 'admins should see Operations');
    assert.equal(document.getElementById('operations-view').hidden, false);
    assert.equal(document.getElementById('operations-scope').value, 'all');
    assert.equal(document.querySelectorAll('#email-operations-table tbody tr').length, 2);
    assert.equal(document.querySelector('#email-operations-table').textContent.includes('<unsafe-error>'), true);
    assert.equal(document.querySelector('#email-operations-table').querySelector('script'), null, 'runtime text must not become markup');
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
    scope.value = '3';
    scope.dispatchEvent(new dom.window.Event('change'));
    await settle();
    document.getElementById('force-weekly-now').click();
    await settle();
    assertRequest(requests.find(item => item.url.includes('force=true')), '/api/asap/staff/workflow/weekly-summary/run-now', '3', null);
    document.getElementById('send-test-email').click();
    await settle();
    const testEmailRequest = requests.find(item => item.url.includes('/email-operations/test'));
    assertRequest(
      testEmailRequest,
      '/api/asap/staff/email-operations/test',
      '3',
      body => assert.match(body.requestId, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i));

    const retry = document.querySelector('#email-operations-table button');
    assert.ok(retry, 'failed rows should expose Retry');
    retry.click();
    await settle();
    const retryRequest = requests.find(item => item.url.includes('/email-operations/41/retry'));
    assertRequest(retryRequest, '/api/asap/staff/email-operations/41/retry', null, { version: 'email-version' });
    assert.equal(document.querySelectorAll('#email-operations-table button').length, 1, 'sent rows must not expose Retry');

    scope.value = '2';
    scope.dispatchEvent(new dom.window.Event('change'));
    await settle();
    document.querySelector('[data-view="queue"]').click();
    await settle();
    assert.equal(scope.value, '2', 'queue refresh must not overwrite Operations scope');

    document.querySelector('[data-view="operations"]').click();
    await settle();
    const oldQueue = deferred();
    const oldEmail = deferred();
    operationsReads.set('queue:2', oldQueue);
    operationsReads.set('email:2', oldEmail);
    scope.dispatchEvent(new dom.window.Event('change'));
    await settle();
    scope.value = '3';
    scope.dispatchEvent(new dom.window.Event('change'));
    await settle();
    oldQueue.resolve(response(200, queuePayload('2')));
    oldEmail.reject(new Error('stale old scope failure'));
    await settle();
    assert.doesNotMatch(document.getElementById('app-status').textContent, /stale old scope failure/);
    assert.equal(scope.value, '3');

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
