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

async function until(predicate, message) {
  const deadline = Date.now() + 3000;
  while (!predicate() && Date.now() < deadline) {
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.ok(predicate(), message);
}

(async () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-staff-created-401-'));
  let dom;
  try {
    fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'), {
      url: 'https://localhost/staff/', pretendToBeVisual: true
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.URLSearchParams = dom.window.URLSearchParams;
    global.Node = dom.window.Node;
    global.HTMLElement = dom.window.HTMLElement;
    global.DOMParser = dom.window.DOMParser;
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    dom.window.gridjs = require(path.join(frontend, 'vendor/gridjs/6.2.0/gridjs.umd.js'));

    const requestId = '9007199254740993';
    let suggestionPosts = 0;
    let revokeOnRefresh = false;
    global.fetch = async (url, options = {}) => {
      if (url.endsWith('/session')) {
        if (revokeOnRefresh) return response(401, { code: 'staff_session_invalid' });
        return response(200, {
          authenticated: true, accessAllowed: true, antiforgeryToken: 'suggestion-af',
          staff: {
            tenantId: 'test-tenant', id: '20', role: 'super_admin', organizationId: 1, organizationName: 'System',
            displayName: 'Library staff', userPrincipalName: 'staff@example.org',
            defaultMineUnclaimedFilter: false
          }
        });
      }
      if (url.includes('/title-requests?')) {
        return response(200, { scope: 'all', organizations: [{ id: 2, name: 'Test Library' }], items: [] });
      }
      if (url.includes('/email-readiness')) return response(200, {});
      if (url.endsWith('/patron-lookup')) {
        return response(200, {
          status: 'verified', libraryOrgId: 2, libraryOrgName: 'Test Library',
          searchLibraryLimited: true, matches: [], patron: {
            patron: {
              barcode: '20000000000001', name: 'Alex Example', patronOrganizationId: 2,
              homeLibraryOrganizationId: 2, homeLibraryOrganizationName: 'Test Library'
            },
            email: 'alex@example.org', currentPreferredPickupBranchId: 101,
            currentPreferredPickupBranchName: 'Main Library',
            pickupBranches: [{ id: 101, label: 'Main Library' }],
            pickupWarning: null, pickupOptionsUnavailable: false, libraryOrgId: 2,
            libraryOrgName: 'Test Library', searchLibraryLimited: true
          }
        });
      }
      if (url.endsWith('/suggestion-configuration?libraryOrgId=2')) {
        return response(200, { libraryOrgId: 2, configuration: {
          availableFormats: ['book'], formatLabels: { book: 'Book' },
          formatRules: { book: { fields: { title: { mode: 'required', label: 'Title' } }, customFields: {} } },
          publicationOptions: [], additionalFieldDefinitions: []
        } });
      }
      if (url.endsWith('/suggestions') && options.method === 'POST') {
        suggestionPosts += 1;
        revokeOnRefresh = true;
        return response(201, {
          id: requestId, libraryOrgId: 2, notificationStatus: 'queued'
        });
      }
      throw new Error(`Unexpected request ${url}`);
    };

    const workflow = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    await workflow.createWorkflowApp().start();
    document.getElementById('new-suggestion').click();
    const scope = document.querySelector('#staff-suggestion-body select[aria-label="Servicing library"]');
    scope.value = '2';
    scope.dispatchEvent(new dom.window.Event('change'));
    document.querySelector('#staff-suggestion-body input[type="search"]').value = '20000000000001';
    document.getElementById('staff-suggestion-form').dispatchEvent(
      new dom.window.Event('submit', { cancelable: true }));
    await until(() => document.querySelector('.staff-suggestion-fields'), 'patron form must render');
    const format = document.querySelector('.staff-suggestion-fields select[aria-label="Material format"]');
    format.value = 'book';
    format.dispatchEvent(new dom.window.Event('change'));
    document.querySelector('.staff-suggestion-fields input[maxlength="500"]').value = 'Committed suggestion';
    document.getElementById('staff-suggestion-form').dispatchEvent(
      new dom.window.Event('submit', { cancelable: true }));

    await until(() => document.getElementById('workspace').hidden,
      'session revocation during follow-up refresh must hide the workspace');
    assert.equal(suggestionPosts, 1);
    assert.equal(document.getElementById('staff-suggestion-dialog').open, false);
    assert.match(document.getElementById('signed-out-message').textContent,
      /Suggestion 9007199254740993 created on behalf of the patron/);
    assert.match(document.getElementById('signed-out-message').textContent,
      /Confirmation email queued/);
    assert.match(document.getElementById('signed-out-message').textContent,
      /Sign in again to review the committed request/);
    assert.doesNotMatch(document.getElementById('signed-out-message').textContent,
      /Your staff session ended or no longer has access/);
    console.log('Committed staff suggestion survives an immediate refresh 401');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
