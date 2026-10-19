const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const root = path.join(__dirname, '..');
const frontend = path.join(root, 'src', 'Asap.Web', 'Frontend');
const response = (status, body) => ({
  ok: status < 400,
  status,
  statusText: status < 400 ? 'OK' : 'Request failed',
  json: async () => body
});
const settle = async () => {
  for (let index = 0; index < 10; index += 1) await new Promise(resolve => setImmediate(resolve));
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
    fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'), {
      url: 'https://localhost/staff/',
      pretendToBeVisual: true
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.URLSearchParams = dom.window.URLSearchParams;
    global.Node = dom.window.Node;
    global.HTMLElement = dom.window.HTMLElement;
    global.DOMParser = dom.window.DOMParser;
    dom.window.confirm = () => true;
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    dom.window.gridjs = require(path.join(frontend, 'vendor/gridjs/6.2.0/gridjs.umd.js'));

    const requests = [];
    const staff = {
      id: '20',
      role: 'super_admin',
      organizationId: 1,
      organizationName: 'System',
      displayName: 'Library staff',
      userPrincipalName: 'staff@example.org',
      defaultMineUnclaimedFilter: false
    };
    const organizations = [{ id: 2, name: 'Test Library' }];
    const configuration = {
      availableFormats: ['book', 'comic', 'ebook'],
      formatLabels: { book: 'Book', comic: 'Comic', ebook: 'eBook' },
      formatRules: {
        book: {
          messageBehavior: 'none',
          fields: {
            title: { mode: 'required', label: 'Title' },
            author: { mode: 'optional', label: 'Author' },
            identifier: { mode: 'optional', label: 'Identifier' },
            publication: { mode: 'optional', label: 'Publication timing' }
          },
          customFields: { audience: { mode: 'required', label: 'Who is it for?' } }
        },
        comic: {
          messageBehavior: 'none',
          fields: {
            title: { mode: 'required', label: 'Comic title' },
            author: { mode: 'hidden', label: 'Author' },
            identifier: { mode: 'hidden', label: 'Identifier' },
            publication: { mode: 'optional', label: 'Publication timing' }
          },
          customFields: {}
        },
        ebook: {
          messageBehavior: 'ebookMessage',
          fields: {
            title: { mode: 'required', label: 'eBook title' },
            author: { mode: 'required', label: 'eBook author' },
            identifier: { mode: 'optional', label: 'Identifier' },
            publication: { mode: 'optional', label: 'Publication timing' }
          },
          customFields: { audience: { mode: 'required', label: 'Who is it for?' } }
        }
      },
      publicationOptions: ['not_published', 'published'],
      additionalFieldDefinitions: [{
        key: 'audience', type: 'select', label: 'Audience', helpText: 'Choose an audience.',
        options: [{ id: 'adult', label: 'Adult', enabled: true }]
      }],
      allowPatronAutoholdOptOut: false,
      ebookMessage: '<p>Use the library eBook collection.</p><p><a href="https://example.org/help" target="_blank" rel="noreferrer">Learn more</a></p>',
      eaudiobookMessage: ''
    };
    const createdRequest = {
      id: '9007199254740993', version: 'request-version', title: 'Staff title', author: 'Author',
      barcode: '20000000000001', nameFirst: 'Alex', nameLast: 'Example', email: 'alex@example.org',
      libraryOrgId: 2, libraryOrgName: 'Test Library', status: 'suggestion', format: 'book', formatLabel: 'Book',
      identifier: null, bibid: null, publication: 'not_published', preferredPickupBranchId: 101,
      preferredPickupBranchName: 'Main Library', workflowTags: [], capabilities: {}
    };
    let created = false;
    let suggestionRequests = 0;
    let duplicateNextSuggestion = false;
    let partialDuplicateNextSuggestion = false;
    let expireNextSuggestion = false;
    let malformedNextSuggestion = false;
    let currentPickupAtLookup = 101;
    let staleLookupResolve = null;
    global.fetch = async (url, options = {}) => {
      requests.push({ url, options });
      if (url.endsWith('/session')) {
        return response(200, { authenticated: true, accessAllowed: true, antiforgeryToken: 'suggestion-af', staff });
      }
      if (url.includes('/title-requests?')) {
        return response(200, {
          scope: 'all', organizations, items: created ? [createdRequest] : []
        });
      }
      if (url.endsWith('/suggestion-configuration?libraryOrgId=2')) {
        return response(200, { libraryOrgId: 2, libraryOrgName: 'Test Library', configuration });
      }
      if (url.endsWith('/api/asap/config?libraryOrgId=2')) return response(200, configuration);
      if (url.endsWith('/patron-lookup')) {
        const body = JSON.parse(options.body);
        if (!body.barcode && body.query === 'old') {
          return new Promise(resolve => {
            staleLookupResolve = () => resolve(response(200, {
              status: 'verified', libraryOrgId: 2, libraryOrgName: 'Test Library',
              searchLibraryLimited: true, matches: [], patron: {
                patron: {
                  barcode: '30000000000001', name: 'Stale Patron', patronOrganizationId: 2,
                  homeLibraryOrganizationId: 2, homeLibraryOrganizationName: 'Test Library'
                },
                email: 'stale@example.org', currentPreferredPickupBranchId: 101,
                currentPreferredPickupBranchName: 'Main Library',
                pickupBranches: [{ id: 101, label: 'Main Library' }],
                pickupWarning: null, pickupOptionsUnavailable: false, libraryOrgId: 2,
                libraryOrgName: 'Test Library', searchLibraryLimited: true
              }
            }));
          });
        }
        if (!body.barcode) {
          return response(200, {
            status: 'multiple_matches', libraryOrgId: 2, libraryOrgName: 'Test Library',
            searchLibraryLimited: true, matches: [
              { barcode: '20000000000001', name: 'Alex Example', homeLibraryOrganizationId: 2, homeLibraryOrganizationName: 'Test Library' },
              { barcode: '20000000000002', name: 'Avery Example', homeLibraryOrganizationId: 2, homeLibraryOrganizationName: 'Test Library' }
            ]
          });
        }
        return response(200, {
          status: 'verified', libraryOrgId: 2, libraryOrgName: 'Test Library', searchLibraryLimited: true,
          matches: [], patron: {
            patron: {
              barcode: body.barcode, name: 'Alex Example', patronOrganizationId: 2,
              homeLibraryOrganizationId: 2, homeLibraryOrganizationName: 'Test Library'
            },
            email: 'alex@example.org', currentPreferredPickupBranchId: currentPickupAtLookup,
            currentPreferredPickupBranchName: 'Main Library',
            pickupBranches: [{ id: 101, label: 'Main Library' }, { id: 102, label: 'North Branch' }],
            pickupWarning: null, pickupOptionsUnavailable: false, libraryOrgId: 2,
            libraryOrgName: 'Test Library', searchLibraryLimited: true
          }
        });
      }
      if (url.endsWith('/bib-lookup')) {
        return response(200, {
          status: 'found', bibId: 9001, title: 'Catalog title', author: 'Catalog author',
          identifier: '9780000000001', publication: '2026', format: 'Book'
        });
      }
      if (url.endsWith('/suggestions') || (url.endsWith('/title-requests') && options.method === 'POST')) {
        suggestionRequests += 1;
        if (expireNextSuggestion) {
          expireNextSuggestion = false;
          return response(401, { code: 'staff_session_invalid' });
        }
        const body = JSON.parse(options.body);
        assert.equal(body.libraryOrgId, 2);
        assert.equal(body.barcode, '20000000000001');
        assert.equal(body.currentPreferredPickupBranchObservedAtLoad, true);
        assert.equal(body.currentPreferredPickupBranchIdAtLoad, currentPickupAtLookup);
        assert.equal(body.customFields.audience, 'adult');
        assert.equal(body.verifiedBibId, null);
        assert.equal(body.emailPatronConfirmation, false);
        if (!created) assert.equal(body.autohold, false);
        if (malformedNextSuggestion) {
          malformedNextSuggestion = false;
          return {
            ...response(201, null),
            json: async () => { throw new SyntaxError('Malformed accepted response'); }
          };
        }
        if (duplicateNextSuggestion) {
          duplicateNextSuggestion = false;
          return response(409, {
            message: 'This patron already has a suggestion for this catalog BIB.',
            duplicate: { id: '9007199254740995', matchType: 'bibid' }
          });
        }
        if (partialDuplicateNextSuggestion) {
          partialDuplicateNextSuggestion = false;
          return response(409, {
            code: 'request_not_created_pickup_changed',
            pickupPreferenceChanged: true,
            message: 'The suggestion was not created, but the patron\'s preferred pickup location was changed successfully. An existing suggestion was found.',
            conflictTitle: 'Already Submitted',
            conflictMessage: 'An existing suggestion was found.',
            duplicate: { id: '9007199254740993', matchType: 'title_format', requestUrl: '/staff/?request=9007199254740993' }
          });
        }
        created = true;
        return response(201, {
          id: '9007199254740993', successTitle: 'Created', successMessage: 'Created',
          notificationStatus: 'not_requested', libraryOrgId: 2,
          requestUrl: '/staff/?request=9007199254740993'
        });
      }
      if (new URL(url, 'http://localhost').pathname.endsWith('/title-requests/9007199254740993')) return response(200, createdRequest);
      if (url.includes('/research-configuration')) return response(200, { externalSearchProviders: [] });
      throw new Error(`Unexpected request ${url}`);
    };

    const workflow = await import(pathToFileURL(path.join(temporary, 'staff/js/workflow.js')).href);
    let workflowApp = workflow.createWorkflowApp();
    await workflowApp.start();
    document.getElementById('new-suggestion').click();
    await until(() => document.getElementById('staff-suggestion-dialog').open, 'new suggestion dialog must open');
    const scope = document.querySelector('#staff-suggestion-body select[aria-label="Servicing library"]');
    assert.equal(scope.value, '', 'super-admin flow must require an explicit target library');
    scope.value = '2';
    scope.dispatchEvent(new dom.window.Event('change'));
    const query = document.querySelector('#staff-suggestion-body input[type="search"]');
    query.value = 'old';
    document.getElementById('staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await until(() => staleLookupResolve, 'first lookup must be in flight');
    query.value = 'Alex';
    query.dispatchEvent(new dom.window.Event('input'));
    document.getElementById('staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await until(() => document.querySelectorAll('.staff-suggestion-candidate').length === 2, 'latest lookup must render current matches');
    staleLookupResolve();
    await settle();
    assert.equal(document.querySelectorAll('.staff-suggestion-candidate').length, 2,
      'stale lookup completion must not replace the current matches');
    query.value = 'Alex';
    document.querySelector('.staff-suggestion-candidate').click();
    await until(() => document.querySelector('.staff-suggestion-fields input[maxlength="500"]'), 'verified patron form must render');
    assert.ok([...document.querySelectorAll('#staff-suggestion-body label')]
      .some(label => label.textContent.includes('Who is it for?')),
    'format-specific custom-field label overrides must be rendered');
    const format = document.querySelector('#staff-suggestion-body select[aria-label="Material format"]');
    format.value = 'comic';
    format.dispatchEvent(new dom.window.Event('change'));
    assert.equal(document.querySelector('#staff-suggestion-body input[aria-label="Author"]'), null,
      'hidden format fields must be removed from the active editor');
    assert.ok([...document.querySelectorAll('#staff-suggestion-body label')]
      .some(label => label.textContent.includes('Comic title')));
    assert.equal([...document.querySelectorAll('#staff-suggestion-body select')]
      .some(control => control.getAttribute('aria-label')?.startsWith('Audience')), false,
    'hidden custom fields must not remain visible after a format change');
    format.value = 'book';
    format.dispatchEvent(new dom.window.Event('change'));
    assert.ok([...document.querySelectorAll('#staff-suggestion-body label')]
      .some(label => label.textContent.includes('Who is it for?')));
    format.value = 'ebook';
    format.dispatchEvent(new dom.window.Event('change'));
    const notice = document.querySelector('.staff-format-notice');
    assert.equal(notice.textContent.includes('<p>'), false);
    assert.equal(notice.textContent.includes('<a'), false);
    assert.equal(notice.querySelector('p').textContent, 'Use the library eBook collection.');
    assert.equal(notice.querySelector('a').getAttribute('href'), 'https://example.org/help');
    assert.equal(notice.querySelector('a').textContent, 'Learn more');
    assert.equal(document.querySelector('#staff-suggestion-actions button[type="submit"]').disabled, false,
      'informational eBook format must remain available for staff creation');
    assert.equal(document.querySelector('.staff-suggestion-fields input[maxlength="500"]').required, true);
    configuration.ebookMessage = '<script>window.staffMessageScriptRan = true</script><a href="javascript:alert(1)">JS</a><a href="data:text/html,unsafe">Data</a><a href="vbscript:msgbox(1)">VB</a><span onclick="window.staffMessageEventRan = true">Safe text</span>';
    format.dispatchEvent(new dom.window.Event('change'));
    assert.equal(notice.querySelector('script'), null);
    assert.equal(dom.window.staffMessageScriptRan, undefined);
    assert.equal(notice.querySelectorAll('a').length, 3);
    assert.ok([...notice.querySelectorAll('a')].every(anchor => !anchor.hasAttribute('href')));
    assert.equal(notice.querySelector('span').hasAttribute('onclick'), false);
    configuration.ebookMessage = '';
    format.dispatchEvent(new dom.window.Event('change'));
    assert.equal(notice.hidden, true);
    assert.equal(notice.childNodes.length, 0);
    format.value = 'book';
    format.dispatchEvent(new dom.window.Event('change'));
    assert.ok([...document.querySelectorAll('#staff-suggestion-body button')]
      .some(button => button.textContent.includes('Search Polaris catalog')));
    const autohold = [...document.querySelectorAll('.staff-suggestion-fields label')]
      .find(label => label.textContent.includes('Automatically place hold'))?.querySelector('input');
    assert.ok(autohold);
    assert.equal(autohold.disabled, false, 'staff AutoHold stays enabled when patron opt-out is disabled');
    autohold.checked = false;
    document.querySelector('.staff-suggestion-catalog button').click();
    await until(() => document.getElementById('polaris-dialog').open, 'staff form must reuse the Task 01 Polaris dialog');
    document.getElementById('close-polaris').click();
    const title = document.querySelector('.staff-suggestion-fields input[maxlength="500"]');
    title.value = 'Staff title';
    const audience = [...document.querySelectorAll('.staff-suggestion-fields select')]
      .find(control => control.getAttribute('aria-label')?.startsWith('Who is it for?'));
    assert.ok(audience, 'configured custom field must be visible');
    audience.value = 'adult';
    document.getElementById('staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    document.getElementById('staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await until(() => suggestionRequests === 1, 'double submit must make one request');
    await until(() => !document.getElementById('staff-suggestion-dialog').open, 'successful creation must close the dialog');
    assert.match(document.getElementById('app-status').textContent, /9007199254740993/);
    assert.match(document.getElementById('app-status').textContent, /No confirmation email was requested/);
    assert.equal(requests.filter(item => item.url.endsWith('/catalog-search')).length, 0);

    document.getElementById('new-suggestion').click();
    currentPickupAtLookup = null;
    await until(() => document.getElementById('staff-suggestion-dialog').open, 'session-expiry suggestion dialog must open');
    const expiryScope = document.querySelector('#staff-suggestion-body select[aria-label="Servicing library"]');
    expiryScope.value = '2';
    expiryScope.dispatchEvent(new dom.window.Event('change'));
    const expiryQuery = document.querySelector('#staff-suggestion-body input[type="search"]');
    expiryQuery.value = '20000000000001';
    document.getElementById('staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await until(() => document.querySelectorAll('.staff-suggestion-candidate').length === 2,
      'session-expiry lookup must render the current patron candidates');
    document.querySelector('.staff-suggestion-candidate').click();
    await until(() => document.querySelector('.staff-suggestion-fields'), 'session-expiry form must render before submit');
    const pickup = document.querySelector('.staff-suggestion-fields select[aria-label="Preferred pickup location"]');
    pickup.value = '101';
    const expiryTitle = document.querySelector('.staff-suggestion-fields input[maxlength="500"]');
    expiryTitle.value = 'Session expiry suggestion';
    const expiryAudience = [...document.querySelectorAll('.staff-suggestion-fields select')]
      .find(control => control.getAttribute('aria-label')?.startsWith('Who is it for?'));
    expiryAudience.value = 'adult';
    duplicateNextSuggestion = true;
    document.getElementById('staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await until(() => document.querySelector('.staff-suggestion-conflict'), 'BIB duplicate must render a conflict');
    assert.match(document.querySelector('.staff-suggestion-conflict').textContent, /catalog BIB/);
    partialDuplicateNextSuggestion = true;
    document.getElementById('staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await until(() => document.querySelector('.staff-suggestion-conflict')?.textContent.includes('Pickup changed'),
      'post-pickup duplicate must render the partial warning');
    assert.match(document.getElementById('staff-suggestion-status').textContent, /not created.*changed successfully/);
    assert.match(document.querySelector('.staff-suggestion-conflict').textContent, /9007199254740993/);
    document.querySelector('.staff-suggestion-conflict button').click();
    await until(() => requests.some(item =>
      new URL(item.url, 'http://localhost').pathname.endsWith('/title-requests/9007199254740993')),
      'exact bigint duplicate action must open the existing request');

    document.getElementById('new-suggestion').click();
    const sessionScope = document.querySelector('#staff-suggestion-body select[aria-label="Servicing library"]');
    sessionScope.value = '2';
    sessionScope.dispatchEvent(new dom.window.Event('change'));
    const sessionQuery = document.querySelector('#staff-suggestion-body input[type="search"]');
    sessionQuery.value = '20000000000001';
    document.getElementById('staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await until(() => document.querySelectorAll('.staff-suggestion-candidate').length === 2,
      'session-expiry lookup must render patron candidates');
    document.querySelector('.staff-suggestion-candidate').click();
    await until(() => document.querySelector('.staff-suggestion-fields'), 'session-expiry form must render');
    document.querySelector('.staff-suggestion-fields select[aria-label="Preferred pickup location"]').value = '101';
    document.querySelector('.staff-suggestion-fields input[maxlength="500"]').value = 'Session expiry suggestion';
    [...document.querySelectorAll('.staff-suggestion-fields select')]
      .find(control => control.getAttribute('aria-label')?.startsWith('Who is it for?')).value = 'adult';
    expireNextSuggestion = true;
    document.getElementById('staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await until(() => document.getElementById('workspace').hidden, 'session expiry must hide the staff workspace');
    assert.equal(document.getElementById('staff-suggestion-dialog').open, false,
      'session expiry must close an in-flight suggestion dialog');
    assert.equal(document.getElementById('signed-out').hidden, false);
    assert.equal(document.getElementById('signed-out-message').textContent,
      'Your staff session ended or no longer has access. Sign in again.');

    workflowApp.dispose();
    dom.window.close();
    dom = new JSDOM(fs.readFileSync(path.join(temporary, 'staff/index.html'), 'utf8'), {
      url: 'https://localhost/staff/',
      pretendToBeVisual: true
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.URLSearchParams = dom.window.URLSearchParams;
    global.Node = dom.window.Node;
    global.HTMLElement = dom.window.HTMLElement;
    global.DOMParser = dom.window.DOMParser;
    dom.window.confirm = () => true;
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    dom.window.gridjs = require(path.join(frontend, 'vendor/gridjs/6.2.0/gridjs.umd.js'));
    currentPickupAtLookup = 101;
    malformedNextSuggestion = true;
    const attemptsBeforeUnconfirmed = suggestionRequests;
    workflowApp = workflow.createWorkflowApp();
    await workflowApp.start();

    document.getElementById('new-suggestion').click();
    await until(() => document.getElementById('staff-suggestion-dialog').open, 'uncertain suggestion dialog must open in a fresh page context');
    const uncertainScope = document.querySelector('#staff-suggestion-body select[aria-label="Servicing library"]');
    uncertainScope.value = '2';
    uncertainScope.dispatchEvent(new dom.window.Event('change'));
    const uncertainQuery = document.querySelector('#staff-suggestion-body input[type="search"]');
    uncertainQuery.value = '20000000000001';
    document.getElementById('staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await until(() => document.querySelectorAll('.staff-suggestion-candidate').length === 2,
      'uncertain suggestion lookup must render current patron candidates');
    document.querySelector('.staff-suggestion-candidate').click();
    await until(() => document.querySelector('.staff-suggestion-fields'), 'uncertain suggestion form must render');
    document.querySelector('.staff-suggestion-fields select[aria-label="Preferred pickup location"]').value = '101';
    document.querySelector('.staff-suggestion-fields input[maxlength="500"]').value = 'Unconfirmed suggestion';
    [...document.querySelectorAll('.staff-suggestion-fields select')]
      .find(control => control.getAttribute('aria-label')?.startsWith('Who is it for?')).value = 'adult';
    const detailReadsBeforeUnconfirmed = requests.filter(item =>
      new URL(item.url, 'http://localhost').pathname.endsWith('/title-requests/9007199254740993')).length;
    const uncertainForm = document.getElementById('staff-suggestion-form');
    uncertainForm.dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await until(() => suggestionRequests === attemptsBeforeUnconfirmed + 1,
      'HTTP 201 malformed JSON must reach exactly one real suggestion POST');
    await until(() => /unconfirmed/i.test(document.getElementById('staff-suggestion-status').textContent),
      'malformed accepted response must be presented as an unconfirmed attempt');
    const uncertainSubmit = document.querySelector('#staff-suggestion-actions button[type="submit"]');
    assert.equal(uncertainForm.inert, true, 'the submitted form stays locked until the outcome is reviewed');
    assert.equal(uncertainSubmit.disabled, true, 'the uncertain attempt cannot be submitted again immediately');
    uncertainForm.dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
    await settle();
    assert.equal(suggestionRequests, attemptsBeforeUnconfirmed + 1,
      'an uncertain 201 response cannot trigger a duplicate POST');
    assert.equal(document.getElementById('staff-suggestion-dialog').open, true);
    assert.equal(document.getElementById('signed-out').hidden, true);
    assert.equal(requests.filter(item =>
      new URL(item.url, 'http://localhost').pathname.endsWith('/title-requests/9007199254740993')).length,
    detailReadsBeforeUnconfirmed, 'an unconfirmed attempt is not navigated as a created request');
    workflowApp.dispose();
    console.log('Staff suggestion UI workflow, explicit scope, Polaris reuse, and double-submit checks passed');
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
