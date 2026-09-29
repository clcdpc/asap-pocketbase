const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
const response = (status, body) => ({
  ok: status < 400,
  status,
  statusText: status < 400 ? 'OK' : 'Request failed',
  json: async () => body
});
const settle = async () => {
  for (let i = 0; i < 8; i += 1) await new Promise(resolve => setImmediate(resolve));
};
function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
async function until(predicate, message) {
  const deadline = Date.now() + 3000;
  while (!predicate() && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 10));
  assert.ok(predicate(), message);
}

(async () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-stale-nav-'));
  let dom;
  try {
    fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'), {
      url: 'https://localhost/staff/?stage=additional_copies', pretendToBeVisual: true
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Node = dom.window.Node;
    global.HTMLElement = dom.window.HTMLElement;
    global.FormData = dom.window.FormData;
    global.URLSearchParams = dom.window.URLSearchParams;
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    dom.window.gridjs = require(path.join(frontend, 'vendor/gridjs/6.2.0/gridjs.umd.js'));

    const firstId = '9007199254740993';
    const secondId = '9007199254740994';
    const staff = {
      id: '20', tenantId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
      authenticationEmail: 'staff@example.org', role: 'staff', organizationId: 2,
      organizationName: 'Library', displayName: 'Staff',
      defaultMineUnclaimedFilter: false, version: 'staff-v1'
    };
    const request = id => ({
      id, type: 'title_request', version: 'v1', title: id === firstId ? 'First title' : 'Second title',
      libraryOrgId: 2, libraryOrgName: 'Library', barcode: '20000000000001',
      status: 'suggestion', format: 'book', formatLabel: 'Book', autohold: true,
      workflowTags: [], activity: [], capabilities: { canEditIdentifier: true, canChangeBib: true, canChangeWorkflowState: true },
      created: '2026-01-01T00:00:00Z', updated: '2026-01-01T00:00:00Z',
      phaseEnteredAt: '2026-01-01T00:00:00Z'
    });
    let staleCopyRead = null;
    let staleCopyFetchStarted = false;
    let staleRecentRead = null;
    global.fetch = async (url) => {
      if (url.endsWith('/session')) return response(200, { authenticated: true, accessAllowed: true, antiforgeryToken: 'token', staff });
      if (url.includes('/additional-copies?')) {
        const status = new URL(url, 'https://localhost').searchParams.get('status');
        if (status === 'open' && staleCopyRead) {
          staleCopyFetchStarted = true;
          return staleCopyRead.promise;
        }
        return response(200, { scope: '2', status, items: [], availableLibraries: [] });
      }
      if (url.includes('/title-requests?')) return response(200, { scope: '2', items: [request(firstId), request(secondId)], organizations: [] });
      if (url.includes(`/title-requests/${firstId}`)) {
        if (staleRecentRead) return staleRecentRead.promise;
        return response(200, request(firstId));
      }
      if (url.includes(`/title-requests/${secondId}`)) return response(200, request(secondId));
      if (url.includes('/api/asap/config?')) return response(200, { availableFormats: ['book'], formatLabels: { book: 'Book' }, publicationOptions: [] });
      if (url.includes('/research-configuration')) return response(200, { externalSearchProviders: [] });
      if (url.includes('/email-readiness')) return response(200, { state: 'non_delivery' });
      throw new Error(`Unexpected request ${url}`);
    };

    const workflow = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    await workflow.createWorkflowApp().start();
    const copyTabs = [...document.querySelectorAll('[data-copy-status]')];
    staleCopyRead = deferred();
    document.getElementById('refresh-additional-copies').click();
    await until(() => staleCopyFetchStarted, 'old Open fetch starts');
    copyTabs.find(tab => tab.dataset.copyStatus === 'closed').click();
    await until(() => copyTabs.find(tab => tab.dataset.copyStatus === 'closed').getAttribute('aria-selected') === 'true',
      'Closed tab becomes current');
    staleCopyRead.reject(new Error('stale Open copy failure'));
    await settle();
    assert.doesNotMatch(document.getElementById('app-status').textContent, /stale Open copy failure/,
      'old additional-copy error must not announce in Closed view');
    staleCopyRead = null;

    document.querySelector('[data-view="queue"]').click();
    await until(() => document.querySelector(`[aria-label="Open request ${firstId}"]`), 'title queue renders');
    document.querySelector(`[aria-label="Open request ${firstId}"]`).click();
    await until(() => document.getElementById('request-dialog-title').textContent === 'First title', 'first detail opens');
    document.getElementById('close-request').click();
    await until(() => document.querySelector('#recent-request-list button'), 'recent item recorded');
    staleRecentRead = deferred();
    document.querySelector('#recent-request-list button').click();
    await until(() => document.getElementById('app-status').textContent.includes('Loading request details'),
      'old recent detail starts');
    document.querySelector(`[aria-label="Open request ${secondId}"]`).click();
    await until(() => document.getElementById('request-dialog-title').textContent === 'Second title',
      'newer detail takes ownership');
    staleRecentRead.resolve(response(404, { code: 'not_found', message: 'Old request unavailable' }));
    await settle();
    assert.match(document.getElementById('recent-request-list').textContent, new RegExp(firstId),
      'stale 404 must not forget a recent item after another detail takes ownership');
    assert.equal(document.getElementById('request-dialog-title').textContent, 'Second title');
    console.log('Staff stale additional-copy error and recent-detail 404 UI checks passed');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
