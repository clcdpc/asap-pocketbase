const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const response = (status, body) => ({
  ok: status < 400,
  status,
  statusText: status < 400 ? 'OK' : 'Request failed',
  json: async () => body
});

async function settle() {
  for (let attempt = 0; attempt < 8; attempt += 1) {
    await new Promise(resolve => setImmediate(resolve));
  }
}

(async () => {
  const root = path.resolve(__dirname, '..');
  const frontend = path.join(root, 'src', 'Asap.Web', 'Frontend');
  const staff = path.join(frontend, 'staff');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-primary-workflow-'));
  let dom;
  try {
    fs.cpSync(staff, path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    dom = new JSDOM(fs.readFileSync(path.join(staff, 'index.html'), 'utf8'), {
      url: 'https://localhost/staff/?stage=pending_hold'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.CustomEvent = dom.window.CustomEvent;
    global.Event = dom.window.Event;
    global.FormData = dom.window.FormData;
    global.Node = dom.window.Node;
    global.Option = dom.window.Option;
    global.localStorage = dom.window.localStorage;
    global.navigator = dom.window.navigator;
    global.requestAnimationFrame = callback => callback();

    const calls = [];
    const sessionResponses = [];
    let sessionFetchCount = 0;
    let completeRun;
    let failRun;
    global.fetch = (url, options = {}) => {
      if (String(url).includes('/workflow/run-now')) {
        calls.push({ url: String(url), options });
        return new Promise((resolve, reject) => {
          completeRun = resolve;
          failRun = reject;
        });
      }
      if (String(url) === '/api/asap/staff/legacy/session') {
        sessionFetchCount += 1;
        const current = sessionResponses.shift() || {
          authenticated: true, accessAllowed: true, antiforgeryToken: 'workflow-test-token',
          staff: { ...state.staffSession.staff }
        };
        return Promise.resolve(response(200, current));
      }
      throw new Error(`Unexpected request: ${url}`);
    };

    const state = await import(pathToFileURL(path.join(temporary, 'staff/js/state.js')).href);
    const gridData = await import(pathToFileURL(path.join(temporary, 'staff/js/grid-data.js')).href);
    await import(pathToFileURL(path.join(temporary, 'staff/js/settings-polaris.js')).href);

    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'workflow-test-token',
      staff: { id: '7', role: 'super_admin', organizationId: 1 }
    });
    const gridContext = {
      currentStatus: 'pending_hold',
      get currentWorkflowOrgScopeId() { return state.currentWorkflowOrgScopeId; },
      setCurrentWorkflowOrgScopeId: state.setCurrentWorkflowOrgScopeId,
      staffGridFilterBar: document.getElementById('staff-grid-filter-bar'),
      workflowSettings: {}
    };
    gridData.updateAdminActions('pending_hold', gridContext);
    assert.equal(document.getElementById('btn-run-workflow-now').classList.contains('hidden'), true,
      'super-admin action must wait until the visible workflow scope has loaded');
    gridData.updateWorkflowScopeControl({
      scope: { superAdmin: true, mode: 'library', libraryOrgId: '2', label: 'Library Two' },
      availableLibraries: [{ orgId: '2', name: 'Library Two' }, { orgId: '3', name: 'Library Three' }]
    }, gridContext);
    assert.equal(document.getElementById('workflow-library-scope').value, '2');
    assert.equal(state.currentWorkflowOrgScopeId, '2');
    assert.equal(document.getElementById('btn-run-workflow-now').classList.contains('hidden'), false,
      'rendering the authoritative scope should reveal the workflow action');
    state.setCurrentLibraryContextOrgId('3');
    gridData.updateAdminActions('pending_hold', gridContext);
    assert.equal(document.querySelectorAll('#admin-actions-bar button:not(.hidden)').length, 1,
      'one truthful unified workflow control should be available');
    assert.ok(document.getElementById('btn-run-workflow-now'));
    assert.equal(document.getElementById('btn-run-promoter-check'), null);
    assert.equal(document.getElementById('btn-run-hold-check'), null);
    assert.equal(document.getElementById('btn-delete-closed-requests'), null,
      'the mixed Closed view has no truthful bulk delete backend contract');

    const button = document.getElementById('btn-run-workflow-now');
    button.click();
    button.click();
    await settle();
    assert.equal(calls.length, 1, 'pending click must not enqueue a duplicate workflow');
    assert.equal(button.disabled, true);
    const runUrl = new URL(calls[0].url, 'https://localhost');
    assert.equal(runUrl.pathname, '/api/asap/staff/workflow/run-now');
    assert.equal(runUrl.searchParams.get('organizationId'), '2',
      'workflow scope must win over Settings scope');
    assert.equal(calls[0].options.method, 'POST');
    assert.equal(calls[0].options.headers['X-ASAP-Antiforgery'], 'workflow-test-token');
    state.setCurrentLibraryContextOrgId('system');
    state.setCurrentWorkflowOrgScopeId('all');
    document.getElementById('workflow-library-scope').value = 'all';
    state.staffSession.staff = {
      ...state.staffSession.staff,
      displayName: 'Updated profile'
    };
    completeRun(response(202, { code: 'queued', jobId: 'job-2', organizationId: 2 }));
    await settle();
    assert.match(document.getElementById('job-msg').textContent, /queued.*Library 2/i,
      'profile replacement must retain truthful feedback for the submitted queue request');
    assert.equal(button.disabled, false);

    gridData.updateAdminActions('closed', gridContext);
    assert.equal(button.classList.contains('hidden'), false);
    assert.equal(document.querySelectorAll('#admin-actions-bar button:not(.hidden)').length, 1);
    assert.equal(document.getElementById('bulk-delete-closed-dialog'), null);

    state.setCurrentWorkflowOrgScopeId('3');
    button.click();
    await settle();
    assert.equal(calls.length, 1,
      'a super-admin run must not use browser state that disagrees with the visible scope selector');
    state.setCurrentWorkflowOrgScopeId('all');
    button.click();
    await settle();
    assert.equal(calls.length, 2);
    assert.equal(new URL(calls[1].url, 'https://localhost').searchParams.has('organizationId'), false,
      'all-library super-admin execution uses the backend omitted-scope contract');
    completeRun(response(202, { code: 'queued', organizationId: 1 }));
    await settle();
    assert.match(document.getElementById('job-msg').textContent, /queued.*all libraries/i);
    assert.equal(button.disabled, false);

    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'workflow-test-token',
      staff: { role: 'admin', organizationId: 2 }
    });
    state.setCurrentLibraryContextOrgId('3');
    state.setCurrentWorkflowOrgScopeId('3');
    gridData.updateAdminActions('suggestion', { workflowSettings: { autoPromote: false } });
    assert.equal(button.classList.contains('hidden'), false,
      'ordinary admins should be able to run their authorized library');
    button.click();
    await settle();
    assert.equal(calls.length, 3);
    assert.equal(new URL(calls[2].url, 'https://localhost').searchParams.get('organizationId'), '2',
      'ordinary admin requests must pin the visible own library if the server role changes');
    completeRun(response(202, { code: 'queued', organizationId: 2 }));
    await settle();
    assert.match(document.getElementById('job-msg').textContent, /queued/i);

    button.click();
    await settle();
    assert.equal(calls.length, 4);
    completeRun(response(403, { code: 'staff_scope_forbidden', message: 'Scope denied' }));
    await settle();
    assert.doesNotMatch(document.getElementById('job-msg').textContent, /queued/i);
    assert.equal(button.disabled, false);
    assert.equal(sessionFetchCount, 1, 'scope rejection revalidates the current staff before enabling retry');

    button.click();
    await settle();
    assert.equal(calls.filter(call => call.url.includes('/workflow/run-now')).length, 5);
    sessionResponses.push({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'workflow-test-token',
      staff: { id: '7', role: 'viewer', organizationId: 2 }
    });
    completeRun(response(403, { code: 'staff_scope_forbidden', message: 'Scope denied after role change' }));
    await settle();
    assert.equal(button.disabled, true, 'a server-side role change must lock stale privileged controls');
    assert.match(document.getElementById('job-msg').textContent, /scope changed.*reload/i);
    delete button.dataset.reloadMessage;
    button.disabled = false; // Model a new page load with the original authorized staff.
    state.setStaffSession({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'workflow-test-token',
      staff: { id: '7', role: 'admin', organizationId: 2 }
    });

    button.click();
    await settle();
    assert.equal(calls.filter(call => call.url.includes('/workflow/run-now')).length, 6);
    completeRun(response(400, { code: 'antiforgery_failed', message: 'Invalid request token', operationPhase: 'rejected' }));
    await settle();
    assert.doesNotMatch(document.getElementById('job-msg').textContent, /may have been queued/i);
    assert.equal(button.disabled, false, 'known antiforgery rejection happens before enqueue');

    button.click();
    await settle();
    assert.equal(calls.length, 7);
    completeRun(response(401, { code: 'staff_session_invalid' }));
    await settle();
    assert.equal(state.staffSession.authenticated, false);
    assert.equal(state.staffSession.staff, null);
    assert.doesNotMatch(document.getElementById('job-msg').textContent, /queued/i);
    assert.equal(button.disabled, true, 'session loss cannot leave privileged control enabled');

    delete button.dataset.reloadMessage;
    button.disabled = false; // Simulate a browser reload after signing in again.
    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'workflow-test-token',
      staff: { id: '7', role: 'super_admin', organizationId: 1 }
    });
    state.setCurrentWorkflowOrgScopeId('2');
    document.getElementById('workflow-library-scope').value = '2';
    button.click();
    await settle();
    assert.equal(calls.length, 8);
    state.setStaffSession({ authenticated: false, antiforgeryToken: 'workflow-test-token' });
    completeRun(response(202, { code: 'queued', jobId: 'late-job', organizationId: 2 }));
    await settle();
    assert.equal(document.getElementById('job-msg').textContent, '',
      'a response after sign-out must not leave pending privileged feedback behind');
    assert.equal(button.disabled, true, 'a different session must not inherit an enabled run control');

    delete button.dataset.reloadMessage;
    button.disabled = false; // Simulate the browser reload required for a new staff session.
    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'workflow-test-token',
      staff: { id: '7', role: 'super_admin', organizationId: 1 }
    });
    state.setCurrentWorkflowOrgScopeId('all');
    document.getElementById('workflow-library-scope').value = 'all';
    button.click();
    await settle();
    assert.equal(calls.length, 9);
    completeRun(response(202, { code: 'queued', jobId: 'downgraded-job', organizationId: 2 }));
    await settle();
    assert.match(document.getElementById('job-msg').textContent, /queued for Library 2/i,
      'a changed server role must report the effective queued library');
    assert.match(document.getElementById('job-msg').textContent, /reload/i);
    assert.equal(button.disabled, true,
      'stale access must require a reload before another manual run');
    gridData.clearJobMessage();
    gridData.updateAdminActions('closed', gridContext);
    assert.match(document.getElementById('job-msg').textContent, /reload/i,
      'tab navigation must preserve the reason the workflow action is disabled');

    delete button.dataset.reloadMessage;
    button.disabled = false;
    state.setCurrentWorkflowOrgScopeId('2');
    document.getElementById('workflow-library-scope').value = '2';
    button.click();
    await settle();
    assert.equal(calls.length, 10);
    failRun(new TypeError('Failed to fetch'));
    await settle();
    assert.match(document.getElementById('job-msg').textContent, /may have been queued.*reload/i,
      'a lost response must not claim a rejected or completed workflow run');
    assert.equal(button.disabled, true, 'an uncertain queue outcome must block an accidental repeat');
    button.click();
    await settle();
    assert.equal(calls.length, 10);
    gridData.clearJobMessage();
    gridData.updateAdminActions('closed', gridContext);
    assert.match(document.getElementById('job-msg').textContent, /may have been queued.*reload/i,
      'tab navigation must preserve the unconfirmed outcome warning');

    const acceptedGatewayJobs = [];
    const gatewayWarnings = [];
    for (const [status, serverReality] of [
      [502, 'enqueued'], [502, 'not enqueued'], [504, 'enqueued'], [500, 'not enqueued'], [503, 'enqueued']
    ]) {
      // A new browser load is the only way to begin another run after uncertainty.
      delete button.dataset.reloadMessage;
      button.disabled = false;
      const previousCount = calls.length;
      button.click();
      await settle();
      assert.equal(calls.length, previousCount + 1);
      if (serverReality === 'enqueued') acceptedGatewayJobs.push(previousCount + 1);
      completeRun(response(status, { message: 'Gateway unavailable' }));
      await settle();
      assert.match(document.getElementById('job-msg').textContent, /Library 2.*may have been queued.*reload/i,
        `${status} must not claim the ${serverReality} reality is known`);
      if (status === 502) gatewayWarnings.push(document.getElementById('job-msg').textContent);
      assert.equal(button.disabled, true, `${status} must lock repeat submission`);
      button.click();
      await settle();
      assert.equal(calls.length, previousCount + 1, `${status} must not enqueue a duplicate`);
      gridData.clearJobMessage();
      gridData.updateAdminActions('closed', gridContext);
      assert.match(document.getElementById('job-msg').textContent, /may have been queued.*reload/i);
      assert.equal(calls.length, previousCount + 1, 'tab navigation must not retry');
    }
    assert.equal(acceptedGatewayJobs.length, 3);
    assert.equal(gatewayWarnings[0], gatewayWarnings[1],
      'identical gateway responses must show identical behavior whether the server enqueued or not');

    delete button.dataset.reloadMessage;
    button.disabled = false;
    const countBeforeReplacement = calls.length;
    button.click();
    await settle();
    state.setStaffSession({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'new-staff-token',
      staff: { id: '8', role: 'super_admin', organizationId: 1 }
    });
    completeRun(response(502, { message: 'Gateway response after session replacement' }));
    await settle();
    assert.match(document.getElementById('job-msg').textContent, /Library 2.*may have been queued.*reload/i);
    assert.equal(button.disabled, true, 'a new active staff session cannot clear the ambiguous run lock');
    button.click();
    await settle();
    assert.equal(calls.length, countBeforeReplacement + 1);

    delete button.dataset.reloadMessage;
    button.disabled = false;
    state.setStaffSession({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'workflow-test-token',
      staff: { id: '7', role: 'super_admin', organizationId: 1 }
    });
    const beforeRelogin = calls.length;
    button.click();
    await settle();
    state.setStaffSession({ authenticated: false, antiforgeryToken: 'workflow-test-token' });
    state.setStaffSession({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'workflow-test-token',
      staff: { id: '7', role: 'super_admin', organizationId: 1 }
    });
    completeRun(response(202, { code: 'queued', jobId: 'old-session-job', organizationId: 2 }));
    await settle();
    assert.match(document.getElementById('job-msg').textContent, /changed staff session.*reload/i);
    assert.equal(button.disabled, true, 'sign-out and same-staff sign-in cannot reuse a pending run control');
    button.click();
    await settle();
    assert.equal(calls.length, beforeRelogin + 1);
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
  console.log('Primary staff workflow operation tests passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
