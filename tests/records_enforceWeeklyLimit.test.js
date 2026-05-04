const assert = require('assert');

// Mock __hooks globally
global.__hooks = __dirname + "/../pb_hooks";

// Intercept requires
const Module = require('module');
const originalRequire = Module.prototype.require;
let mockSuggestionLimit = {};

Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/config.js")) {
    return {
      suggestionLimit: function(app, libraryOrgId) {
        return mockSuggestionLimit;
      }
    };
  }
  if (moduleName.includes("lib/identity.js")) {
    return {};
  }
  return originalRequire.apply(this, arguments);
};

const { enforceWeeklyLimit } = require('../lib/records.js');

let mockFindRecordsByFilterResult = [];
let appCallArgs = [];

const mockApp = {
  findRecordsByFilter: function(collection, filter, sort, limit, offset, params) {
    appCallArgs.push({ collection, filter, sort, limit, offset, params });
    return mockFindRecordsByFilterResult;
  }
};

let passed = 0;
let failed = 0;

function runTest(name, setup, run, verify) {
  try {
    mockFindRecordsByFilterResult = [];
    appCallArgs = [];
    mockSuggestionLimit = {};
    setup();
    run();
    verify();
    console.log(`✅ Test passed: ${name}`);
    passed++;
  } catch (err) {
    console.error(`❌ Test failed: ${name}`);
    console.error(err);
    failed++;
  }
}

// Test 1: No limit reached
runTest('Under limit (0 records)',
  () => {
    mockFindRecordsByFilterResult = [];
  },
  () => {
    enforceWeeklyLimit(mockApp, '12345', 'org1');
  },
  () => {
    assert.strictEqual(appCallArgs.length, 1);
    assert.strictEqual(appCallArgs[0].collection, 'title_requests');
    assert.strictEqual(appCallArgs[0].limit, 5); // default limit
  }
);

// Test 2: Limit reached
runTest('Limit reached (default 5)',
  () => {
    // Return 5 records, oldest created 2 days ago
    const oldestDate = new Date();
    oldestDate.setDate(oldestDate.getDate() - 2);

    mockFindRecordsByFilterResult = [
      { get: () => new Date().toISOString() },
      { get: () => new Date().toISOString() },
      { get: () => new Date().toISOString() },
      { get: () => new Date().toISOString() },
      { get: () => oldestDate.toISOString() }
    ];
  },
  () => {
    let thrown = false;
    try {
      enforceWeeklyLimit(mockApp, '12345', 'org1');
    } catch (err) {
      thrown = true;
      assert.strictEqual(err.code, 406);
      assert.ok(err.message.includes('Weekly suggestion limit reached'));
    }
    if (!thrown) throw new Error("Expected error to be thrown");
  },
  () => {}
);

let test3OldestDate;
// Test 3: Custom limit and message from config
runTest('Custom limit and message',
  () => {
    mockSuggestionLimit = { limit: 2, message: "Custom limit! {{next_available_date}}" };
    test3OldestDate = new Date();
    test3OldestDate.setDate(test3OldestDate.getDate() - 2);

    mockFindRecordsByFilterResult = [
      { get: () => new Date().toISOString() },
      { get: () => test3OldestDate.toISOString() }
    ];
  },
  () => {
    let thrown = false;
    try {
      enforceWeeklyLimit(mockApp, '12345', 'org1');
    } catch (err) {
      thrown = true;
      assert.strictEqual(err.code, 406);
      assert.ok(err.message.includes('Custom limit!'));

      const expectedNextAvailable = new Date(test3OldestDate.getTime() + (7 * 24 * 60 * 60 * 1000));
      const expectedDateStr = expectedNextAvailable.toLocaleDateString("en-US", {
        weekday: 'long',
        year: 'numeric',
        month: 'long',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit'
      });
      assert.ok(err.message.includes(expectedDateStr));
    }
    if (!thrown) throw new Error("Expected error to be thrown");
  },
  () => {
    assert.strictEqual(appCallArgs[0].limit, 2);
  }
);

// Test 4: Suggestion limit object instead of property on config
let test4OldestDate;
runTest('Config returns suggestionLimit property',
  () => {
    mockSuggestionLimit = { suggestionLimit: 3, suggestionLimitMessage: "Limit message! {{next_available_date}}" };
    test4OldestDate = new Date();
    test4OldestDate.setDate(test4OldestDate.getDate() - 5);

    mockFindRecordsByFilterResult = [
      { get: () => new Date().toISOString() },
      { get: () => new Date().toISOString() },
      { get: () => test4OldestDate.toISOString() }
    ];
  },
  () => {
    let thrown = false;
    try {
      enforceWeeklyLimit(mockApp, '12345', 'org1');
    } catch (err) {
      thrown = true;
      assert.strictEqual(err.code, 406);
      assert.ok(err.message.includes('Limit message!'));
    }
    if (!thrown) throw new Error("Expected error to be thrown");
  },
  () => {
    assert.strictEqual(appCallArgs[0].limit, 3);
  }
);


// Test 5: Fallback created access
let test5OldestDate;
runTest('Fallback property created on record',
  () => {
    mockSuggestionLimit = { limit: 1 };
    test5OldestDate = new Date();
    test5OldestDate.setDate(test5OldestDate.getDate() - 1);

    mockFindRecordsByFilterResult = [
      { get: () => null, created: test5OldestDate.toISOString() }
    ];
  },
  () => {
    let thrown = false;
    try {
      enforceWeeklyLimit(mockApp, '12345', 'org1');
    } catch (err) {
      thrown = true;
      assert.strictEqual(err.code, 406);
    }
    if (!thrown) throw new Error("Expected error to be thrown");
  },
  () => {
    assert.strictEqual(appCallArgs[0].limit, 1);
  }
);

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);
if (failed > 0) process.exit(1);
