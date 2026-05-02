const assert = require('assert');

// Mock __hooks globally for the required modules
global.__hooks = __dirname + "/../pb_hooks";

// Mock dependencies of records.js
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

const { hasStaffUsers } = require('../pb_hooks/lib/records.js');

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

// 1. Test normal case where countRecords > 0
runTest('countRecords returns > 0', () => {
  let countRecordsCalled = false;
  const app = {
    countRecords: (collection) => {
      assert.strictEqual(collection, "staff_users");
      countRecordsCalled = true;
      return 1;
    }
  };

  const result = hasStaffUsers(app);
  assert.strictEqual(countRecordsCalled, true);
  assert.strictEqual(result, true);
});

// 2. Test normal case where countRecords == 0
runTest('countRecords returns 0', () => {
  let countRecordsCalled = false;
  const app = {
    countRecords: (collection) => {
      assert.strictEqual(collection, "staff_users");
      countRecordsCalled = true;
      return 0;
    }
  };

  const result = hasStaffUsers(app);
  assert.strictEqual(countRecordsCalled, true);
  assert.strictEqual(result, false);
});

// 3. Test fallback case where countRecords throws, and findRecordsByFilter returns > 0
runTest('fallback: countRecords throws, findRecordsByFilter returns > 0', () => {
  let countRecordsCalled = false;
  let findRecordsCalled = false;

  const app = {
    countRecords: (collection) => {
      countRecordsCalled = true;
      throw new Error("Simulated countRecords error");
    },
    findRecordsByFilter: (collection, filter, sort, limit, offset) => {
      assert.strictEqual(collection, "staff_users");
      assert.strictEqual(filter, "id != ''");
      assert.strictEqual(sort, "");
      assert.strictEqual(limit, 1);
      assert.strictEqual(offset, 0);
      findRecordsCalled = true;
      return [{}]; // length > 0
    }
  };

  const result = hasStaffUsers(app);
  assert.strictEqual(countRecordsCalled, true);
  assert.strictEqual(findRecordsCalled, true);
  assert.strictEqual(result, true);
});

// 4. Test fallback case where countRecords throws, and findRecordsByFilter returns empty array
runTest('fallback: countRecords throws, findRecordsByFilter returns empty array', () => {
  let countRecordsCalled = false;
  let findRecordsCalled = false;

  const app = {
    countRecords: (collection) => {
      countRecordsCalled = true;
      throw new Error("Simulated countRecords error");
    },
    findRecordsByFilter: (collection, filter, sort, limit, offset) => {
      findRecordsCalled = true;
      return []; // length == 0
    }
  };

  const result = hasStaffUsers(app);
  assert.strictEqual(countRecordsCalled, true);
  assert.strictEqual(findRecordsCalled, true);
  assert.strictEqual(result, false);
});

// 5. Test fallback error case where both countRecords and findRecordsByFilter throw
runTest('fallback error: both throw', () => {
  let countRecordsCalled = false;
  let findRecordsCalled = false;
  let loggerErrorCalled = false;

  const app = {
    countRecords: (collection) => {
      countRecordsCalled = true;
      throw new Error("Simulated countRecords error");
    },
    findRecordsByFilter: (collection, filter, sort, limit, offset) => {
      findRecordsCalled = true;
      throw new Error("Simulated findRecordsByFilter error");
    },
    logger: () => ({
      error: (msg, key, val) => {
        assert.strictEqual(msg, "Staff user count failed");
        assert.strictEqual(key, "error");
        assert.strictEqual(val.includes("Simulated findRecordsByFilter error"), true);
        loggerErrorCalled = true;
      }
    })
  };

  const result = hasStaffUsers(app);
  assert.strictEqual(countRecordsCalled, true);
  assert.strictEqual(findRecordsCalled, true);
  assert.strictEqual(loggerErrorCalled, true);
  assert.strictEqual(result, false);
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
