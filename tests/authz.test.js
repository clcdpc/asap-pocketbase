const assert = require('assert');
const authz = require('../lib/authz.js');

console.log('Running tests for lib/authz.js...');

let passed = 0;
let failed = 0;

function test(name, fn) {
  try {
    fn();
    console.log(`✅ ${name} passed`);
    passed++;
  } catch (err) {
    console.error(`❌ ${name} failed:`, err.message);
    failed++;
  }
}

function createMockStaff(role, libraryOrgId) {
  return {
    get: function(key) {
      if (key === 'role') return role;
      if (key === 'libraryOrgId') return libraryOrgId;
      return null;
    }
  };
}

test('sameLibrary returns true for super_admin regardless of libraryOrgId', () => {
  const staff = createMockStaff('super_admin', 'LIB1');
  assert.strictEqual(authz.sameLibrary(staff, 'LIB2'), true);
  assert.strictEqual(authz.sameLibrary(staff, ''), true);
  assert.strictEqual(authz.sameLibrary(staff, null), true);
});

test('sameLibrary returns true for matching libraryOrgId', () => {
  const staff = createMockStaff('admin', 'LIB1');
  assert.strictEqual(authz.sameLibrary(staff, 'LIB1'), true);
});

test('sameLibrary returns false for non-matching libraryOrgId', () => {
  const staff = createMockStaff('admin', 'LIB1');
  assert.strictEqual(authz.sameLibrary(staff, 'LIB2'), false);
});

test('sameLibrary trims whitespace before comparing', () => {
  const staff = createMockStaff('admin', ' LIB1 ');
  assert.strictEqual(authz.sameLibrary(staff, '  LIB1'), true);
});

test('sameLibrary returns false if staff libraryOrgId is empty', () => {
  const staff = createMockStaff('admin', '');
  assert.strictEqual(authz.sameLibrary(staff, 'LIB1'), false);
});

test('sameLibrary returns false if provided libraryOrgId is empty', () => {
  const staff = createMockStaff('admin', 'LIB1');
  assert.strictEqual(authz.sameLibrary(staff, ''), false);
  assert.strictEqual(authz.sameLibrary(staff, null), false);
  assert.strictEqual(authz.sameLibrary(staff, undefined), false);
});

test('sameLibrary returns false if both are empty (prevents false positives)', () => {
  const staff = createMockStaff('admin', '');
  assert.strictEqual(authz.sameLibrary(staff, ''), false);

  const staff2 = createMockStaff('admin', null);
  assert.strictEqual(authz.sameLibrary(staff2, null), false);
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);
if (failed > 0) {
  process.exit(1);
}
