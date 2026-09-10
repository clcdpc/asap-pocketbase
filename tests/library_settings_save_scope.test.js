const assert = require('assert');
const path = require('path');

global.__hooks = path.resolve(__dirname, '../pb_hooks');

function makeRecord(id, values) {
  return {
    id,
    values: Object.assign({}, values || {}),
    get(name) { return this.values[name]; },
    getBool(name) { return !!this.values[name]; },
    set(name, value) { this.values[name] = value; },
    collection() { return { name: 'mock' }; }
  };
}

global.Record = function MockRecord(collection) {
  return makeRecord('', { _collection: collection && collection.name ? collection.name : '' });
};

const staffRoutes = require('../lib/staff_routes.js');

function createMockApp() {
  const org = makeRecord('org-rec-1', { organizationId: '100', displayName: 'Library 100' });
  const systemUi = makeRecord('ui-system', {
    scope: 'system',
    publicationOptions: JSON.stringify([{ id: 'new', label: 'New', enabled: true, sortOrder: 10 }]),

  });
  const libraryUi = makeRecord('ui-library', { scope: 'library', libraryOrganization: org.id });
  const workflowLibrary = makeRecord('wf-library', { scope: 'library', libraryOrganization: org.id });
  let patronOverride = null;
  const deleted = [];

  return {
    systemUi,
    workflowLibrary,
    getPatronOverride: () => patronOverride,
    deleted,
    app: {
      findCollectionByNameOrId(name) { return { name }; },
      findFirstRecordByData(collectionName, field, value) {
        if (collectionName === 'polaris_organizations' && field === 'organizationId' && String(value) === '100') return org;
        throw new Error('not found');
      },
      findFirstRecordByFilter(collectionName, filter, params) {
        if (collectionName === 'workflow_settings' && filter.includes("scope = 'library'")) return workflowLibrary;
        if (collectionName === 'ui_settings' && filter.includes("scope = 'library'")) return libraryUi;
        if (collectionName === 'ui_settings' && filter.includes("scope = 'system'")) return systemUi;
        if (collectionName === 'email_templates' && filter.includes("templateKey")) {
          return makeRecord('email-existing', {
            scope: 'library',
            libraryOrganization: org.id,
            templateKey: params.key
          });
        }
        if (collectionName === 'patron_settings_overrides' && patronOverride) return patronOverride;
        throw new Error('not found');
      },
      findRecordsByFilter(collectionName, filter, sort, limit, offset, params) {
        if (collectionName === 'email_templates' && filter.includes("templateKey = {:k")) {
            return [
                makeRecord('email-existing-1', { scope: 'library', libraryOrganization: org.id, templateKey: 'suggestion_submitted' }),
                makeRecord('email-existing-2', { scope: 'library', libraryOrganization: org.id, templateKey: 'purchase_approved' }),
                makeRecord('email-existing-3', { scope: 'library', libraryOrganization: org.id, templateKey: 'already_owned' }),
                makeRecord('email-existing-4', { scope: 'library', libraryOrganization: org.id, templateKey: 'rejected' }),
                makeRecord('email-existing-5', { scope: 'library', libraryOrganization: org.id, templateKey: 'hold_placed' })
            ];
        }
        return [];
      },
      findRecordById() { throw new Error('not found'); },
      save(record) {
        if ((record.get('orgId') || '') === '100') patronOverride = record;
      },
      delete(record) { deleted.push(record); },
      logger() { return { warn() {}, error() {}, info() {}, debug() {} }; }
    }
  };
}

function makeEvent(app, body, staff) {
  return {
    app,
    requestInfo() { return { body, auth: staff }; },
    json(code, payload) { return { code, payload }; }
  };
}

console.log('Running library settings save scope tests...');

const { app, systemUi, workflowLibrary, getPatronOverride, deleted } = createMockApp();
const staff = makeRecord('staff-1', { role: 'admin', libraryOrgId: '100' });
staff.collection = () => ({ name: 'staff_users' });

const result = staffRoutes.updateLibrarySettings(makeEvent(app, {
  orgId: '100',
  ui_text: {
    publicationOptions: [{ id: 'local', label: 'Local preorder', enabled: true, sortOrder: 10 }]
  },
  workflow: {
    additionalCopyTimeoutEnabled: true,
    additionalCopyTimeoutDays: 21,
    patronCodeEligibilityEnabled: true,
    allowedPatronCodeIds: '1,14,28',
    patronCodeEligibilityMessage: 'This card is not eligible.'
  },
  smtp: { host: 'should-not-save.example.org', port: 2525 },
  staffUrl: 'https://example.org/staff'
}, staff));

assert.strictEqual(result.code, 200);
const override = getPatronOverride();
assert.ok(override, 'Expected a patron_settings_overrides record to be saved');
assert.deepStrictEqual(override.get('publicationOptions'), [{ id: 'local', label: 'Local preorder', enabled: true, sortOrder: 10 }]);
assert.strictEqual(systemUi.get('publicationOptions'), JSON.stringify([{ id: 'new', label: 'New', enabled: true, sortOrder: 10 }]));
assert.strictEqual(workflowLibrary.get('additionalCopyTimeoutEnabled'), true);
assert.strictEqual(workflowLibrary.get('additionalCopyTimeoutDays'), 21);
assert.strictEqual(workflowLibrary.get('patronCodeEligibilityEnabled'), true);
assert.strictEqual(workflowLibrary.get('allowedPatronCodeIds'), '1,14,28');
assert.strictEqual(workflowLibrary.get('patronCodeEligibilityMessage'), 'This card is not eligible.');
assert.strictEqual(deleted.length, 5, 'Expected blank library email templates to clear template overrides');

console.log('All library settings save scope tests passed!');
