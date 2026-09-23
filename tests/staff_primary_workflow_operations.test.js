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
    let completeRun;
    global.fetch = (url, options = {}) => {
      calls.push({ url: String(url), options });
      if (String(url).includes('/workflow/run-now')) {
        return new Promise(resolve => { completeRun = resolve; });
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
      staff: { role: 'super_admin', organizationId: 1 }
    });
    gridData.updateWorkflowScopeControl({
      scope: { superAdmin: true, mode: 'library', libraryOrgId: '2', label: 'Library Two' },
      availableLibraries: [{ orgId: '2', name: 'Library Two' }, { orgId: '3', name: 'Library Three' }]
    }, {
      setCurrentWorkflowOrgScopeId: state.setCurrentWorkflowOrgScopeId,
      staffGridFilterBar: document.getElementById('staff-grid-filter-bar')
    });
    assert.equal(document.getElementById('workflow-library-scope').value, '2');
    assert.equal(state.currentWorkflowOrgScopeId, '2');
    state.setCurrentLibraryContextOrgId('3');
    gridData.updateAdminActions('pending_hold', { workflowSettings: { autoPromote: true } });
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
    completeRun(response(202, { code: 'queued', jobId: 'job-2', organizationId: 2 }));
    await settle();
    assert.match(document.getElementById('job-msg').textContent, /queued.*Library 2/i,
      'late feedback must describe the submitted scope and queue acceptance');
    assert.equal(button.disabled, false);

    gridData.updateAdminActions('closed', { workflowSettings: {} });
    assert.equal(button.classList.contains('hidden'), false);
    assert.equal(document.querySelectorAll('#admin-actions-bar button:not(.hidden)').length, 1);
    assert.equal(document.getElementById('bulk-delete-closed-dialog'), null);

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
    assert.equal(new URL(calls[2].url, 'https://localhost').searchParams.has('organizationId'), false,
      'ordinary admin requests must leave scope enforcement to the backend');
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

    button.click();
    await settle();
    assert.equal(calls.length, 5);
    completeRun(response(401, { code: 'staff_session_invalid' }));
    await settle();
    assert.equal(state.staffSession.authenticated, false);
    assert.equal(state.staffSession.staff, null);
    assert.doesNotMatch(document.getElementById('job-msg').textContent, /queued/i);
    assert.equal(button.disabled, false);

    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'workflow-test-token',
      staff: { id: '7', role: 'super_admin', organizationId: 1 }
    });
    state.setCurrentWorkflowOrgScopeId('2');
    button.click();
    await settle();
    assert.equal(calls.length, 6);
    state.setStaffSession({ authenticated: false, antiforgeryToken: 'workflow-test-token' });
    completeRun(response(202, { code: 'queued', jobId: 'late-job', organizationId: 2 }));
    await settle();
    assert.equal(document.getElementById('job-msg').textContent, '',
      'a response after sign-out must not leave pending privileged feedback behind');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
  console.log('Primary staff workflow operation tests passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
