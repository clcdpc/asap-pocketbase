const assert = require('assert');
const path = require('path');
global.__hooks = path.resolve(__dirname, '../pb_hooks');

const Module = require('module');
const originalRequire = Module.prototype.require;

let enabled = '10';
let workflowByOrg = {};
let sessionContexts = {};
let contextCounter = 0;
let patronAuthResult = {};
let createdOptions = null;
let weeklyLimitOrgId = null;
let pickupBuildOptionsSeen = [];

const mockRecord = values => ({
  id: values.id || 'patron-record',
  get: key => values[key] || '',
  getBool: key => !!values[key],
  email: () => values.email || '',
  newAuthToken: () => 'token',
});

Module.prototype.require = function(moduleName) {
  if (moduleName.includes('lib/config.js') || moduleName.endsWith('/config.js') || moduleName === '../config.js') {
    return {
      getSettings: () => ({ enabledLibraryOrgIds: enabled }),
      librarySettings: (app, orgId) => ({ ui_text: { systemNotEnabledMessage: '{{library}} does not currently participate in this suggestion service.', successTitle: `Success ${orgId}`, successMessage: `Created ${orgId}` } }),
      workflowSettings: (app, orgId) => workflowByOrg[String(orgId)] || { allowAnyRegisteredCardLogin: false, suggestionLimit: 5 },
      uiText: (app, orgId) => ({ successTitle: `Success ${orgId}`, successMessage: `Created ${orgId}` }),
      suggestionLimit: (app, orgId) => { weeklyLimitOrgId = orgId; return workflowByOrg[String(orgId)] || { suggestionLimit: 5, allowPatronAutoholdOptOut: true }; },
    };
  }
  if (moduleName.includes('lib/format_rules.js')) return { sanitizePatronSuggestion: data => data };
  if (moduleName.includes('lib/format_claim_rules.js')) return { applyFormatClaimRule: () => {} };
  if (moduleName.includes('lib/mail.js')) return { suggestionSubmitted: () => true };
  if (moduleName.includes('lib/orgs.js')) return { attachPatronScope: (app, patron) => patron, findOrganization: (app, id) => ({ get: key => key === 'displayName' ? `Library ${id}` : '' }) };
  if (moduleName.includes('lib/polaris.js')) return {
    adminStaffAuth: () => ({}),
    authenticatePatron: () => patronAuthResult,
    lookupPatron: () => ({ PatronID: 'p1', Barcode: 'b1', PatronOrgID: 'po1', LibraryOrgID: '20', LibraryOrgName: 'Home B', RequestPickupBranchID: '10', PreferredPickupBranchID: '10', PreferredPickupBranchName: 'Main' }),
    updatePatronPreferredPickupBranch: () => ({ success: true })
  };
  if (moduleName.includes('lib/polaris/pickup_preference_context.js')) return {
    buildPickupPreferenceContext: (app, staffAuth, patron, options) => {
      pickupBuildOptionsSeen.push(options || {});
      return {
      pickupBranches: [{ id: '10', label: 'Main' }],
      pickupBranchesRefreshedAt: '',
      currentPreferredPickupBranchId: '10',
      currentPreferredPickupBranchName: 'Main',
      selectedPickupBranchId: '10',
      selectedPickupBranchName: 'Main',
      currentPreferenceAllowed: true,
      pickupBranchWarning: ''
      };
    },
    buildAvailablePickupPreferenceContext: (app, staffAuth, patron, options) => {
      pickupBuildOptionsSeen.push(options || {});
      return {
      pickupBranches: [{ id: '10', label: 'Main' }],
      pickupBranchesRefreshedAt: '',
      currentPreferredPickupBranchId: '10',
      currentPreferredPickupBranchName: 'Main',
      selectedPickupBranchId: '10',
      selectedPickupBranchName: 'Main',
      currentPreferenceAllowed: true,
      pickupBranchWarning: ''
      };
    },
    validateSelectedPickupBranch: () => ({ id: '10', label: 'Main' }),
    currentPreferredId: () => '10'
  };
  if (moduleName.includes('lib/records.js')) return {
    upsertPatronUser: (app, patron) => mockRecord({
      id: 'patron-record',
      barcode: patron.Barcode,
      nameFirst: patron.NameFirst,
      nameLast: patron.NameLast,
      patronOrgId: patron.PatronOrgID,
      libraryOrgId: patron.LibraryOrgID,
      libraryOrgName: patron.LibraryOrgName,
    }),
    createSuggestion: (app, patron, data, options) => { createdOptions = options; return { id: 'req1' }; },
  };
  if (moduleName.includes('lib/patron_session_contexts.js')) return {
    createPatronSessionContext: (app, patronRecord, context) => {
      const id = 'ctx' + (++contextCounter);
      sessionContexts[id] = Object.assign({ id, patronUserId: patronRecord.id }, context);
      return { id };
    },
    getPatronSessionContext: (app, patronRecord, contextId) => {
      const context = sessionContexts[contextId];
      if (!context || context.patronUserId !== patronRecord.id) { const err = new Error('Bad context'); err.code = 403; throw err; }
      return context;
    },
  };
  if (moduleName.includes('lib/route_utils.js')) return { body: e => e.requestBody || {}, requireAuth: e => e.auth, applyIsbnCheckStatusForCreate: () => {}, runImmediateSubmissionIdentifierLookup: (e, record) => record };
  return originalRequire.apply(this, arguments);
};

const patronRoutes = require('../lib/patron_routes.js');
const suggestionRecords = require('../lib/records/suggestions.js');
Module.prototype.require = originalRequire;

function event(body, auth) {
  let response;
  return {
    e: { requestBody: body, auth, app: { logger: () => ({ error: () => {}, warn: () => {} }), findCollectionByNameOrId: () => ({}), save: record => { record.saved = true; } }, json: (status, payload) => (response = { status, payload }) },
    response: () => response,
  };
}

function reset() {
  enabled = '10'; workflowByOrg = {}; sessionContexts = {}; contextCounter = 0; createdOptions = null; weeklyLimitOrgId = null; pickupBuildOptionsSeen = [];
  patronAuthResult = { PatronID: 'p1', Barcode: 'b1', PatronOrgID: 'po1', LibraryOrgID: '20', LibraryOrgName: 'Home B', NameFirst: 'Pat', NameLast: 'Ron' };
}

reset();
enabled = '20';
let t = event({ barcode: 'b1', pin: '123' });
patronRoutes.patronLogin(t.e);
assert.strictEqual(t.response().status, 200, 'normal login without libraryOrgId succeeds');
assert.strictEqual(t.response().payload.effectiveLibraryOrgId, '20');
assert.strictEqual(t.response().payload.crossLibraryLogin, false);
assert.ok(t.response().payload.patronContextId, 'login returns patronContextId');

reset();
t = event({ barcode: 'b1', pin: '123', libraryOrgId: '10' });
patronRoutes.patronLogin(t.e);
assert.strictEqual(t.response().status, 403, 'default false rejects when patron home library is not eligible');

reset();
workflowByOrg['10'] = { allowAnyRegisteredCardLogin: true, suggestionLimit: 5, allowPatronAutoholdOptOut: true };
t = event({ barcode: 'b1', pin: '123', libraryOrgId: '10' });
patronRoutes.patronLogin(t.e);
assert.strictEqual(t.response().status, 200, 'enabled allows cross-library login');
assert.strictEqual(t.response().payload.effectiveLibraryOrgId, '10');
assert.strictEqual(t.response().payload.experienceLibraryOrgId, '10');
assert.strictEqual(t.response().payload.patronHomeLibraryOrgId, '20');
assert.strictEqual(t.response().payload.crossLibraryLogin, true);
assert.ok(t.response().payload.patronContextId, 'cross-library login returns patronContextId');

reset();
workflowByOrg['10'] = { allowAnyRegisteredCardLogin: true };
t = event({ barcode: 'b1', pin: '123' });
patronRoutes.patronLogin(t.e);
assert.strictEqual(t.response().status, 403, 'no library context does not activate setting');

reset();
enabled = '30';
workflowByOrg['10'] = { allowAnyRegisteredCardLogin: true };
t = event({ barcode: 'b1', pin: '123', libraryOrgId: '10' });
patronRoutes.patronLogin(t.e);
assert.strictEqual(t.response().status, 403, 'experience library must participate');

reset();
const auth = mockRecord({ id: 'patron-record', barcode: 'b1', nameFirst: 'Pat', nameLast: 'Ron', patronOrgId: 'po1', libraryOrgId: '20', libraryOrgName: 'Home B', effectiveLibraryOrgId: 'stale', effectiveLibraryOrgName: 'Stale Library' });
sessionContexts.ctxA = { id: 'ctxA', patronUserId: 'patron-record', effectiveLibraryOrgId: '10', effectiveLibraryOrgName: 'Library 10' };
t = event({ title: 'Test', format: 'book', patronContextId: 'ctxA', preferredPickupBranchId: '10' }, auth);
patronRoutes.createSuggestion(t.e);
assert.strictEqual(t.response().payload.successTitle, 'Success 10', 'success text uses effective library');
assert.strictEqual(!!(pickupBuildOptionsSeen[0] && pickupBuildOptionsSeen[0].forceRefresh), false, 'create should not force refresh on first validation pass');
assert.strictEqual(createdOptions.effectiveLibraryOrgId, '10', 'patron route passes effective library to suggestion creation');
sessionContexts.ctxB = { id: 'ctxB', patronUserId: 'patron-record', effectiveLibraryOrgId: '30', effectiveLibraryOrgName: 'Library 30' };
t = event({ title: 'Test', format: 'book', patronContextId: 'ctxB', preferredPickupBranchId: '10' }, auth);
patronRoutes.createSuggestion(t.e);
assert.strictEqual(createdOptions.effectiveLibraryOrgId, '30', 'second session context owns its own submission');
t = event({ title: 'Legacy', format: 'book', preferredPickupBranchId: '10' }, auth);
patronRoutes.createSuggestion(t.e);
assert.strictEqual(createdOptions.effectiveLibraryOrgId, '20', 'legacy fallback ignores stale patron effective library');

reset();
let savedRecords = [];
const app = {
  findCollectionByNameOrId: () => ({}),
  findRecordsByFilter: (collection, filter, sort, limit, offset, params) => savedRecords.filter(record => record.get('barcode') === params.barcode && (!params.libraryOrgId || record.get('libraryOrgId') === params.libraryOrgId)).slice(0, limit),
  save: record => { if (!savedRecords.includes(record)) savedRecords.push(record); },
};
global.Record = function() { this.values = {}; this.id = `r${savedRecords.length + 1}`; this.set = (k, v) => { this.values[k] = v; }; this.get = k => this.values[k] || ''; };
workflowByOrg['10'] = { suggestionLimit: 1, allowPatronAutoholdOptOut: true };
const patron = mockRecord({ barcode: 'b1', nameFirst: 'Pat', nameLast: 'Ron', patronOrgId: 'po1', libraryOrgId: '20', libraryOrgName: 'Home B', effectiveLibraryOrgId: 'stale', effectiveLibraryOrgName: 'Stale Library' });
const effectiveOptions = { effectiveLibraryOrgId: '10', effectiveLibraryOrgName: 'Library 10' };
const createdSuggestion = suggestionRecords.createSuggestion(app, patron, { title: 'First', format: 'book' }, effectiveOptions);
assert.strictEqual(createdSuggestion.get('libraryOrgId'), '10', 'suggestion belongs to effective library');
assert.strictEqual(createdSuggestion.get('libraryOrgName'), 'Library 10', 'suggestion library name is effective library name');
assert.strictEqual(createdSuggestion.get('patronOrgId'), 'po1', 'patron identity remains actual patron');
assert.strictEqual(createdSuggestion.get('nameFirst'), 'Pat', 'patron name remains actual patron');
assert.strictEqual(weeklyLimitOrgId, '10', 'weekly limit uses effective library');
assert.throws(() => suggestionRecords.createSuggestion(app, patron, { title: 'Second', format: 'book' }, effectiveOptions), err => err && err.code === 406, 'second suggestion is blocked by effective library weekly limit');

console.log('allowAnyRegisteredCardLogin tests passed.');
