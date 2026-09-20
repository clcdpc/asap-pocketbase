const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const source = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
const response = (status, body) => ({
  ok: status < 400,
  status,
  statusText: status < 400 ? 'OK' : 'Request failed',
  json: async () => body
});
const settle = async () => {
  for (let index = 0; index < 8; index += 1) await new Promise(resolve => setImmediate(resolve));
};
async function until(predicate, message) {
  const deadline = Date.now() + 3000;
  while (!predicate() && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 10));
  assert.ok(predicate(), message);
}

(async () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-staff-suggestion-ui-'));
  let dom;
  try {
    fs.cpSync(path.join(source, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(source, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    dom = new JSDOM(fs.readFileSync(path.join(source, 'staff', 'index.html'), 'utf8'), {
      url: 'https://localhost/staff/',
      pretendToBeVisual: true
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.Node = dom.window.Node;
    global.HTMLElement = dom.window.HTMLElement;
    global.requestAnimationFrame = dom.window.requestAnimationFrame.bind(dom.window);
    global.cancelAnimationFrame = dom.window.cancelAnimationFrame.bind(dom.window);
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    dom.window.gridjs = require(path.join(source, 'vendor/gridjs/6.2.0/gridjs.umd.js'));

    const requests = [];
    const staff = {
      id: '20',
      role: 'staff',
      organizationId: 2,
      organizationName: 'Test Library',
      displayName: 'Library staff',
      userPrincipalName: 'staff@example.org',
      defaultMineUnclaimedFilter: false
    };
    const configuration = {
      availableFormats: ['book'],
      formatLabels: { book: 'Book' },
      formatRules: {
        book: {
          messageBehavior: 'none',
          fields: {
            title: { mode: 'required', label: 'Title' },
            author: { mode: 'optional', label: 'Author' },
            identifier: { mode: 'optional', label: 'Identifier' },
            publication: { mode: 'optional', label: 'Publication' }
          },
          customFields: {}
        }
      },
      publicationOptions: ['not_published'],
      additionalFieldDefinitions: []
    };
    let lookupCount = 0;
    global.fetch = async (url, options = {}) => {
      requests.push({ url, options });
      if (url.endsWith('/session')) return response(200, { authenticated: true, accessAllowed: true, antiforgeryToken: 'suggestion-af', staff });
      if (url.includes('/title-requests?')) return response(200, { items: [], scope: '2', organizations: [] });
      if (url.startsWith('/api/asap/config?')) return response(200, configuration);
      if (url.endsWith('/patron-lookup')) {
        const body = JSON.parse(options.body);
        lookupCount += 1;
        if (!body.barcode) {
          return response(200, {
            status: 'multiple_matches',
            libraryOrgId: 2,
            libraryOrgName: 'Test Library',
            searchLibraryLimited: true,
            matches: [
              { barcode: '29001234567890', name: 'Jane Doe', homeLibraryOrganizationId: 2, homeLibraryOrganizationName: 'Test Library' },
              { barcode: '29001234567891', name: 'Jane Roe', homeLibraryOrganizationId: 2, homeLibraryOrganizationName: 'Test Library' }
            ]
          });
        }
        return response(200, {
          status: 'verified',
          libraryOrgId: 2,
          libraryOrgName: 'Test Library',
          searchLibraryLimited: true,
          matches: [],
          patron: {
            patron: {
              barcode: body.barcode,
              name: 'Jane Doe',
              patronOrganizationId: 2,
              homeLibraryOrganizationId: 2,
              homeLibraryOrganizationName: 'Test Library'
            },
            email: 'jane@example.org',
            currentPreferredPickupBranchId: 101,
            currentPreferredPickupBranchName: 'Main Library',
            pickupBranches: [{ id: 101, label: 'Main Library' }],
            pickupBranchesRefreshedAt: '2026-09-20T12:00:00Z',
            pickupWarning: null,
            pickupOptionsUnavailable: false,
            libraryOrgId: 2,
            libraryOrgName: 'Test Library',
            searchLibraryLimited: true
          }
        });
      }
      if (url.endsWith('/catalog-search')) {
        return response(200, {
          results: [{ bibId: 9001, title: 'Catalog-selected title', author: 'Catalog author', identifier: '9780000000001' }]
        });
      }
      if (url.endsWith('/title-requests') && options.method === 'POST') {
        const body = JSON.parse(options.body);
        assert.equal(body.barcode, '29001234567890');
        assert.equal(body.currentPreferredPickupBranchIdAtLoad, 101);
        assert.equal(body.emailPatronConfirmation, false);
        assert.equal(body.preferredPickupBranchId, 101);
        return response(201, { id: 41, successTitle: 'Created', successMessage: 'Created' });
      }
      throw new Error(`Unexpected request ${url}`);
    };

    const workflow = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    await workflow.createWorkflowApp().start();
    document.getElementById('new-suggestion').click();
    await until(() => document.getElementById('staff-suggestion-dialog').open, 'new suggestion dialog must open');
    const query = document.querySelector('#staff-suggestion-body input[type="search"]');
    query.value = 'Jane';
    document.getElementById('staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await until(() => document.querySelectorAll('.staff-suggestion-candidate').length === 2, 'multiple patron matches must be shown');
    document.querySelector('.staff-suggestion-candidate').click();
    await until(() => document.querySelector('#staff-suggestion-body input[maxlength="500"]'), 'authoritative patron details must load after candidate selection');
    document.getElementById('staff-catalog-query').value = 'catalog title';
    document.getElementById('staff-catalog-search-button').click();
    await until(() => document.querySelector('.staff-catalog-result'), 'catalog search results must be shown');
    document.querySelector('.staff-catalog-result').click();
    assert.equal(document.querySelector('.staff-suggestion-fields input[maxlength="500"]').value, 'Catalog-selected title');
    const title = document.querySelector('.staff-suggestion-fields input[required]');
    title.value = 'A staff-created suggestion';
    document.getElementById('staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await until(() => document.getElementById('staff-suggestion-dialog').open === false, 'successful staff suggestion must close the dialog');
    assert.equal(lookupCount, 2, 'candidate selection must perform a second authoritative lookup');
    assert.match(document.getElementById('app-status').textContent, /created on behalf/i);
    assert.ok(requests.some(request => request.url.endsWith('/title-requests') && request.options.method === 'POST'));
    console.log('Staff-assisted suggestion jsdom journey passed');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
