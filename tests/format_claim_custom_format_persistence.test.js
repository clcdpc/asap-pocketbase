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
    collection() { return { name: 'staff_users' }; }
  };
}

global.Record = function MockRecord(collection) {
  return makeRecord('', { _collection: collection && collection.name ? collection.name : '' });
};

const staffRoutes = require('../lib/staff_routes.js');

console.log('Running format-claim custom format persistence tests...');

const org = makeRecord('org-rec-100', { organizationId: '100', displayName: 'Library 100' });
const staffTarget = makeRecord('staff-target', { role: 'admin', libraryOrgId: '100', active: true });
const savedRows = [];

const app = {
  findCollectionByNameOrId(name) { return { name }; },
  findFirstRecordByData(collectionName, field, value) {
    if (collectionName === 'polaris_organizations' && field === 'organizationId' && String(value) === '100') return org;
    throw new Error('not found');
  },
  findFirstRecordByFilter(collectionName, filter) {
    if (collectionName === 'workflow_settings' && filter.includes("scope = 'library'")) return makeRecord('wf-lib', {});
    if (collectionName === 'ui_settings' && filter.includes("scope = 'library'")) return makeRecord('ui-lib', {});
    throw new Error('not found');
  },
  findRecordsByFilter(collectionName) {
    if (collectionName === 'format_claim_rules') return [];
    return [];
  },
  findRecordById(collectionName, id) {
    if (collectionName === 'staff_users' && id === 'staff-target') return staffTarget;
    throw new Error('not found');
  },
  save(record) {
    if (record.get('format')) savedRows.push(record);
  },
  delete() {},
  logger() { return { error() {}, warn() {} }; }
};

const auth = makeRecord('admin-1', { role: 'admin', libraryOrgId: '100' });
auth.collection = () => ({ name: 'staff_users' });

const result = staffRoutes.updateLibrarySettings({
  app,
  requestInfo() {
    return {
      auth,
      body: {
        orgId: '100',
        workflow: {},
        ui_text: {},
        emails: {},
        formatClaimRules: [{ format: 'videogame', staffUserId: 'staff-target' }]
      }
    };
  },
  json(code, payload) { return { code, payload }; }
});

console.log('RESULT', result); assert.strictEqual(result.code, 200);
assert.ok(savedRows.length > 0, 'Expected a format claim rule row to be saved');
assert.strictEqual(savedRows[0].get('format'), 'videogame');

console.log('All format-claim custom format persistence tests passed!');
