const assert = require('assert');
const path = require('path');

global.__hooks = path.resolve(__dirname, '../pb_hooks');

function makeRecord(id, values) {
  return {
    id,
    values: Object.assign({}, values || {}),
    get(name) { return this.values[name]; },
    getBool(name) { return !!this.values[name]; },
    collection() { return { name: 'staff_users' }; }
  };
}

const config = require('../lib/config.js');
const staffRoutes = require('../lib/staff_routes.js');

const originalLibrarySettings = config.librarySettings;
const originalEmailStatus = config.emailStatus;

config.librarySettings = function () {
  return { emails: {}, ui_text: {}, workflow: {}, leapBibUrlPattern: '' };
};
config.emailStatus = function () { return {}; };

function makeEvent(app, auth) {
  return {
    app,
    requestInfo() {
      return {
        auth,
        query: { get: (key) => key === 'orgId' ? '100' : '' }
      };
    },
    json(code, payload) { return { code, payload }; }
  };
}

console.log('Running format-claim super-admin option tests...');

const app = {
  findRecordsByFilter(collectionName, filter) {
    if (collectionName === 'staff_users') {
      assert.ok(filter.includes("role = 'super_admin'"), 'Expected staff options filter to include super_admin users');
      return [
        makeRecord('lib-admin', { displayName: 'Lib Admin', username: 'libadmin', role: 'admin', libraryOrgId: '100', active: true }),
        makeRecord('super-admin', { displayName: 'Global Admin', username: 'global', role: 'super_admin', libraryOrgId: '', active: true })
      ];
    }
    if (collectionName === 'format_claim_rules') return [];
    return [];
  },
  logger() { return { error() {}, warn() {} }; }
};

const auth = makeRecord('staff-1', { role: 'admin', libraryOrgId: '100' });
auth.collection = () => ({ name: 'staff_users' });

const result = staffRoutes.getLibrarySettings(makeEvent(app, auth));
assert.strictEqual(result.code, 200);
assert.strictEqual(result.payload.formatClaimStaffOptions.length, 2);
assert.ok(result.payload.formatClaimStaffOptions.some((row) => row.id === 'super-admin'));

config.librarySettings = originalLibrarySettings;
config.emailStatus = originalEmailStatus;

console.log('All format-claim super-admin option tests passed!');
