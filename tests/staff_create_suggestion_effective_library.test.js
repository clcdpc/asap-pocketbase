const assert = require('assert');
const path = require('path');
global.__hooks = path.resolve(__dirname, '../pb_hooks');

const Module = require('module');
const originalRequire = Module.prototype.require;

let staffUser;
let body;
let polarisLookupCalls = 0;
let createdOptions = null;
let isbnUiText = null;
let enabledLibraries = '10,20';
let pickupContextCalls = [];
let polarisUpdateCalls = 0;
let configuredPolarisUserId = '999';

function record(values) {
  return {
    id: values.id || 'rec1',
    get: key => values[key] || '',
    set: (key, value) => { values[key] = value; },
  };
}

Module.prototype.require = function(moduleName) {
  if (moduleName.includes('lib/route_utils.js')) return {
    requireAuth: () => staffUser,
    body: () => body,
    applyIsbnCheckStatusForCreate: (data, uiText) => { isbnUiText = uiText; data.isbnCheckStatus = data.identifier ? 'pending' : 'skipped_no_isbn'; },
    runImmediateSubmissionIdentifierLookup: (e, rec) => rec,
  };
  if (moduleName.includes('lib/polaris.js')) return {
    adminStaffAuth: () => ({}),
    lookupPatron: () => { polarisLookupCalls++; return { PatronID: 'p1', Barcode: body.barcode, PatronOrgID: 'po1', LibraryOrgID: '20', LibraryOrgName: 'Library 20', NameFirst: 'Pat', NameLast: 'Ron', EmailAddress: 'p@example.test' }; },
    updatePatronPreferredPickupBranch: () => { polarisUpdateCalls++; return { success: true }; },
  };
  if (moduleName.includes('lib/config.js')) return {
    getSettings: () => ({ enabledLibraryOrgIds: enabledLibraries }),
    workflowSettings: () => ({ allowAnyRegisteredCardLogin: true }),
    uiText: (app, orgId) => ({ orgId, formatRules: { book: { identifierMode: orgId === '10' ? 'required' : 'hidden' } } }),
    polaris: () => ({ userId: configuredPolarisUserId }),
  };
  if (moduleName.includes('lib/records.js')) return {
    upsertPatronUser: () => record({ id: 'patron1', barcode: body.barcode, nameFirst: 'Pat', nameLast: 'Ron', patronOrgId: 'po1', libraryOrgId: '20', libraryOrgName: 'Library 20' }),
    createSuggestion: (app, patron, data, options) => { createdOptions = options; return record({ id: 'req1', notes: '', email: 'p@example.test' }); },
    formatDate: () => '2026-05-28',
    appendSystemNote: () => {},
  };
  if (moduleName.includes('lib/orgs.js')) return { attachPatronScope: (app, patron) => patron, findOrganization: (app, id) => ({ get: () => `Library ${id}` }) };
  if (moduleName.includes('lib/mail.js')) return { suggestionSubmitted: () => true };
  if (moduleName.includes('lib/additional_copies.js')) return {};
  if (moduleName.includes('lib/format_claim_rules.js')) return { applyFormatClaimRule: () => {} };
  if (moduleName.includes('lib/polaris/pickup_preference_context.js')) return {
    buildPickupPreferenceContext: (app, staffAuth, patronData, options) => {
      pickupContextCalls.push(options || {});
      return {
      pickupBranches: [{ id: '20', label: 'Library 20' }],
      selectedPickupBranchId: '20'
      };
    },
    validateSelectedPickupBranch: () => ({ id: '20', label: 'Library 20' }),
    currentPreferredId: () => '20'
  };
  return originalRequire.apply(this, arguments);
};

const adminRoutes = require('../lib/staff/admin_routes.js');
Module.prototype.require = originalRequire;

function event() {
  let response = null;
  return {
    e: { app: { save: () => {}, logger: () => ({ error: () => {} }) }, json: (status, payload) => (response = { status, payload }) },
    response: () => response,
  };
}

staffUser = { get: key => ({ role: 'super_admin', libraryOrgId: '', username: 'root' })[key] || '' };
body = { barcode: 'b1', title: 'Title', format: 'book', identifier: '978' };
polarisLookupCalls = 0;
pickupContextCalls = [];
let t = event();
adminRoutes.staffCreateSuggestion(t.e);
assert.strictEqual(t.response().status, 400);
assert.strictEqual(t.response().payload.message, 'Select a library before creating a staff suggestion.');
assert.strictEqual(polarisLookupCalls, 0);

staffUser = { get: key => ({ role: 'super_admin', libraryOrgId: '', username: 'root' })[key] || '' };
body = { barcode: 'b1', title: 'Title', format: 'book', identifier: '978', libraryOrgId: '10', preferredPickupBranchId: '20' };
pickupContextCalls = [];
t = event();
adminRoutes.staffCreateSuggestion(t.e);
assert.strictEqual(t.response().status, 201);
assert.strictEqual(!!(pickupContextCalls[0] && pickupContextCalls[0].forceRefresh), false);
assert.strictEqual(createdOptions.effectiveLibraryOrgId, '10');
assert.strictEqual(isbnUiText.orgId, '10');
assert.strictEqual(body.isbnCheckStatus, 'pending');

staffUser = { get: key => ({ role: 'admin', libraryOrgId: '20', libraryOrgName: 'Library 20', username: 'admin', polarisUserId: '77' })[key] || '' };
body = { barcode: 'b2', title: 'Title', format: 'book', identifier: '978', preferredPickupBranchId: '20' };
t = event();
adminRoutes.staffCreateSuggestion(t.e);
assert.strictEqual(t.response().status, 201);
assert.strictEqual(createdOptions.effectiveLibraryOrgId, '20');
assert.strictEqual(isbnUiText.orgId, '20');

staffUser = { get: key => ({ role: 'super_admin', libraryOrgId: '', username: 'root' })[key] || '' };
body = { barcode: 'b1', title: 'Title', format: 'book', libraryOrgId: '99', preferredPickupBranchId: '20' };
enabledLibraries = '10,20';
t = event();
adminRoutes.staffCreateSuggestion(t.e);
assert.strictEqual(t.response().status, 403);
assert.strictEqual(t.response().payload.message, 'Selected library does not currently participate in ASAP.');

staffUser = { get: key => ({ role: 'super_admin', libraryOrgId: '', username: 'root' })[key] || '' };
body = { barcode: 'b1', title: 'Title', format: 'book', identifier: '978', libraryOrgId: '10', preferredPickupBranchId: '20' };
enabledLibraries = '10,20';
configuredPolarisUserId = '';
polarisUpdateCalls = 0;
t = event();
adminRoutes.staffCreateSuggestion(t.e);
assert.strictEqual(t.response().status, 403);
assert.strictEqual(t.response().payload.message, 'Configured Polaris system user ID is missing. Add a Polaris user ID to your staff account or configure the system user ID.');
assert.strictEqual(polarisUpdateCalls, 0);

console.log('staff create suggestion effective library tests passed.');
