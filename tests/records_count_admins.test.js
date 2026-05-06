const assert = require('assert');

global.__hooks = __dirname + "/../pb_hooks";

const Module = require('module');
const originalRequire = Module.prototype.require;
Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/config.js")) {
    return {};
  }
  if (moduleName.includes("lib/identity.js")) {
    return {};
  }
  return originalRequire.apply(this, arguments);
};

const { countAdminUsers, countSuperAdminUsers } = require('../lib/records.js');

let passed = 0;
let failed = 0;

function runTest(name, testFn) {
  try {
    testFn();
    console.log(`✅ Test case "${name}" passed`);
    passed++;
  } catch (err) {
    console.error(`❌ Test case "${name}" failed`);
    console.error(`   ${err.stack || err.message || err}`);
    failed++;
  }
}

// 1. countAdminUsers returns correct count and checks args
runTest('countAdminUsers returns > 0', () => {
  let findRecordsCalled = false;
  const app = {
    findRecordsByFilter: (collection, filter, sort, limit, offset) => {
      assert.strictEqual(collection, "staff_users");
      assert.strictEqual(filter, "role = 'admin'");
      assert.strictEqual(sort, "");
      assert.strictEqual(limit, 2);
      assert.strictEqual(offset, 0);
      findRecordsCalled = true;
      return [{}, {}]; // length 2
    }
  };

  const result = countAdminUsers(app);
  assert.strictEqual(findRecordsCalled, true);
  assert.strictEqual(result, 2);
});

// 2. countAdminUsers returns 0
runTest('countAdminUsers returns 0', () => {
  let findRecordsCalled = false;
  const app = {
    findRecordsByFilter: (collection, filter, sort, limit, offset) => {
      findRecordsCalled = true;
      return []; // length 0
    }
  };

  const result = countAdminUsers(app);
  assert.strictEqual(findRecordsCalled, true);
  assert.strictEqual(result, 0);
});

// 3. countSuperAdminUsers returns correct count and checks args
runTest('countSuperAdminUsers returns > 0', () => {
  let findRecordsCalled = false;
  const app = {
    findRecordsByFilter: (collection, filter, sort, limit, offset) => {
      assert.strictEqual(collection, "staff_users");
      assert.strictEqual(filter, "role = 'super_admin'");
      assert.strictEqual(sort, "");
      assert.strictEqual(limit, 2);
      assert.strictEqual(offset, 0);
      findRecordsCalled = true;
      return [{}]; // length 1
    }
  };

  const result = countSuperAdminUsers(app);
  assert.strictEqual(findRecordsCalled, true);
  assert.strictEqual(result, 1);
});

// 4. countSuperAdminUsers returns 0
runTest('countSuperAdminUsers returns 0', () => {
  let findRecordsCalled = false;
  const app = {
    findRecordsByFilter: (collection, filter, sort, limit, offset) => {
      findRecordsCalled = true;
      return []; // length 0
    }
  };

  const result = countSuperAdminUsers(app);
  assert.strictEqual(findRecordsCalled, true);
  assert.strictEqual(result, 0);
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
