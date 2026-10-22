const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
const response = (status, body) => ({
  ok: status < 400, status, statusText: status < 400 ? 'OK' : 'Request failed',
  json: async () => body
});

async function until(predicate, message) {
  const deadline = Date.now() + 3000;
  while (!predicate() && Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.ok(predicate(), message);
}

async function verify(mode) {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), `asap-operations-401-${mode}-`));
  let dom;
  try {
    fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'), {
      url: 'https://localhost/staff/?stage=operations', pretendToBeVisual: true
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.URLSearchParams = dom.window.URLSearchParams;
    global.Node = dom.window.Node;
    dom.window.gridjs = require(path.join(frontend, 'vendor/gridjs/6.2.0/gridjs.umd.js'));

    let revokeOnRefresh = false;
    let posts = 0;
    global.fetch = async (url, options = {}) => {
      if (url.endsWith('/session')) {
        if (revokeOnRefresh) return response(401, { code: 'staff_session_invalid' });
        return response(200, {
          authenticated: true, accessAllowed: true, antiforgeryToken: 'operations-af',
          staff: {
            tenantId: 'test-tenant', id: '7', role: 'super_admin', organizationId: 1,
            organizationName: 'System', displayName: 'Super Admin', version: 'staff-version',
            defaultMineUnclaimedFilter: false
          }
        });
      }
      if (url.endsWith('/api/asap/staff/organizations')) {
        return response(200, [{ id: 2, name: 'Library Two' }]);
      }
      if (url.includes('/workflow/queues')) return response(200, { items: [] });
      if (url.includes('/email-operations') && options.method !== 'POST') {
        return response(200, { items: [{
          id: '9007199254740993', status: 'failed', deliveryClass: 'business_event',
          lastErrorCode: 'mail_not_configured', canRetry: true,
          createdUtc: '2026-09-14T12:00:00Z', version: 'email-version'
        }] });
      }
      if (url.includes('/email-readiness')) return response(200, {});
      if (options.method === 'POST') {
        posts += 1;
        revokeOnRefresh = true;
        const parsed = new URL(url, 'https://localhost');
        if (parsed.pathname === '/api/asap/staff/workflow/run-now') {
          return response(202, { code: 'queued', jobId: '9007199254740995', organizationId: 1 });
        }
        const retry = parsed.pathname.match(/^\/api\/asap\/staff\/email-operations\/(\d+)\/retry$/);
        if (retry) {
          return response(202, { code: 'queued', data: {
            id: retry[1], dispatchDelayed: false, version: 'email-version-v2'
          } });
        }
        throw new Error(`Unexpected operations POST ${url}`);
      }
      throw new Error(`Unexpected request ${url}`);
    };

    const workflow = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    await workflow.createWorkflowApp().start();
    if (mode === 'run') {
      document.getElementById('run-workflow-now').click();
    } else {
      document.querySelector('#email-operations-table button').click();
    }
    await until(() => document.getElementById('workspace').hidden,
      `${mode} post-commit 401 must hide workspace`);
    assert.equal(posts, 1);
    const message = document.getElementById('signed-out-message').textContent;
    assert.match(message, mode === 'run' ? /Workflow run queued/ :
      /Email retry 9007199254740993 queued/);
    assert.match(message, /Sign in again to review/);
    assert.doesNotMatch(message, /Your staff session ended or no longer has access/);
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
}

(async () => {
  await verify('run');
  await verify('retry');
  console.log('Committed Operations actions survive immediate refresh 401');
})().catch(error => { console.error(error); process.exitCode = 1; });
