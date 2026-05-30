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
  const otherOrg = makeRecord('org-rec-2', { organizationId: '200', displayName: 'Library 200' });
  const deleted = [];

  return {
    org,
    otherOrg,
    deleted,
    app: {
      findCollectionByNameOrId(name) { return { name }; },
      findFirstRecordByData(collectionName, field, value) {
        if (collectionName === 'polaris_organizations' && field === 'organizationId') {
          if (String(value) === '100') return org;
          if (String(value) === '200') return otherOrg;
        }
        throw new Error('not found');
      },
      findFirstRecordByFilter(collectionName, filter, params) {
        throw new Error('not found');
      },
      findRecordsByFilter(collectionName, filter, sort, limit, offset, params) {
        const mockRows = [];
        if (collectionName === 'format_claim_rules') {
          if (params.libraryOrgId === '100') {
            mockRows.push(makeRecord('rule-1', { libraryOrgId: '100', format: 'book' }));
          }
        } else if (['workflow_settings', 'ui_settings', 'email_templates', 'rejection_templates', 'material_formats'].indexOf(collectionName) >= 0) {
          if (params.org === 'org-rec-1') {
            mockRows.push(makeRecord(collectionName + '-1', { scope: 'library', libraryOrganization: 'org-rec-1' }));
          }
        } else if (collectionName === 'patron_settings_overrides') {
          if (params.orgId === '100') {
            mockRows.push(makeRecord('patron-override-1', { orgId: '100' }));
          }
        } else if (collectionName === 'patron_library_settings') {
          if (params.org === 'org-rec-1') {
            mockRows.push(makeRecord('patron-lib-1', { libraryOrganization: 'org-rec-1' }));
          }
        } else if (collectionName === 'library_settings') {
          if (params.org === 'org-rec-1') {
            mockRows.push(makeRecord('lib-settings-1', { libraryOrganization: 'org-rec-1' }));
          }
        }
        return mockRows;
      },
      findRecordById() { throw new Error('not found'); },
      save(record) {},
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

console.log('Running library settings reset tests...');

// 1. Non-admin cannot reset
{
  const { app } = createMockApp();
  const staff = makeRecord('staff-non-admin', { role: 'staff', libraryOrgId: '100' });
  staff.collection = () => ({ name: 'staff_users' });
  const result = staffRoutes.updateLibrarySettings(makeEvent(app, { orgId: '100', action: 'reset' }, staff));
  assert.strictEqual(result.code, 403, 'Non-admin should be denied');
}

// 2. Library admin can reset own library
{
  const { app, deleted } = createMockApp();
  const staff = makeRecord('staff-admin', { role: 'admin', libraryOrgId: '100' });
  staff.collection = () => ({ name: 'staff_users' });
  const result = staffRoutes.updateLibrarySettings(makeEvent(app, { orgId: '100', action: 'reset' }, staff));
  assert.strictEqual(result.code, 200, 'Library admin should be able to reset own library');
  assert.ok(deleted.length > 0, 'Expected records to be deleted');
  
  // Verify that a record from format_claim_rules was deleted
  const hasFormatClaimRuleDeleted = deleted.some(r => r.id === 'rule-1');
  assert.ok(hasFormatClaimRuleDeleted, 'Expected format_claim_rules for the library to be deleted');
  
  // Verify workflow_settings deleted
  assert.ok(deleted.some(r => r.id === 'workflow_settings-1'), 'Expected workflow_settings to be deleted');
  // Verify ui_settings deleted
  assert.ok(deleted.some(r => r.id === 'ui_settings-1'), 'Expected ui_settings to be deleted');
  // Verify email_templates deleted
  assert.ok(deleted.some(r => r.id === 'email_templates-1'), 'Expected email_templates to be deleted');
  // Verify rejection_templates deleted
  assert.ok(deleted.some(r => r.id === 'rejection_templates-1'), 'Expected rejection_templates to be deleted');
  // Verify material_formats deleted
  assert.ok(deleted.some(r => r.id === 'material_formats-1'), 'Expected material_formats to be deleted');
  // Verify patron_settings_overrides deleted
  assert.ok(deleted.some(r => r.id === 'patron-override-1'), 'Expected patron_settings_overrides to be deleted');
  // Verify patron_library_settings deleted
  assert.ok(deleted.some(r => r.id === 'patron-lib-1'), 'Expected patron_library_settings to be deleted');
  // Verify library_settings deleted
  assert.ok(deleted.some(r => r.id === 'lib-settings-1'), 'Expected library_settings to be deleted');
}

// 3. Library admin cannot reset another library
{
  const { app, deleted } = createMockApp();
  const staff = makeRecord('staff-admin', { role: 'admin', libraryOrgId: '100' });
  staff.collection = () => ({ name: 'staff_users' });
  const result = staffRoutes.updateLibrarySettings(makeEvent(app, { orgId: '200', action: 'reset' }, staff));
  assert.strictEqual(result.code, 403, 'Library admin cannot reset another library');
  assert.strictEqual(deleted.length, 0, 'No records should be deleted');
}

// 4. Super admin can reset selected library
{
  const { app, deleted } = createMockApp();
  const staff = makeRecord('super-admin', { role: 'super_admin', libraryOrgId: 'system' });
  staff.collection = () => ({ name: 'staff_users' });
  const result = staffRoutes.updateLibrarySettings(makeEvent(app, { orgId: '100', action: 'reset' }, staff));
  assert.strictEqual(result.code, 200, 'Super admin can reset selected library');
  assert.ok(deleted.length > 0, 'Expected records to be deleted');
}

console.log('All library settings reset tests passed!');
