const assert = require('assert');

// Mock __hooks globally
global.__hooks = __dirname + "/../pb_hooks";

// Intercept requires
const Module = require('module');
const originalRequire = Module.prototype.require;

Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/config.js")) return {};
  if (moduleName.includes("lib/identity.js")) return {};
  return originalRequire.apply(this, arguments);
};

class MockRecord {
  constructor(collection) {
    this.collection = collection;
    this.data = {};
  }
  set(key, value) {
    this.data[key] = value;
  }
}

global.Record = MockRecord;

const { recordEvent } = require('../lib/records.js');

// Restore original require to avoid polluting the global environment
Module.prototype.require = originalRequire;

let passed = 0;
let failed = 0;

function runTest(name, testFn) {
  try {
    testFn();
    console.log(`✅ Test passed: ${name}`);
    passed++;
  } catch (err) {
    console.error(`❌ Test failed: ${name}`);
    console.error(err);
    failed++;
  }
}

console.log('Running tests for recordEvent in pb_hooks/lib/records.js...');

runTest('recordEvent success', () => {
  let savedEvent = null;
  const mockApp = {
    findCollectionByNameOrId: (name) => {
      assert.strictEqual(name, "title_request_events");
      return { name };
    },
    save: (record) => {
      savedEvent = record;
    }
  };

  const mockRecord = { id: 'req_123' };
  recordEvent(mockApp, mockRecord, 'status_change', 'Status updated', { actorName: 'John Doe' });

  assert.ok(savedEvent);
  assert.strictEqual(savedEvent.data.titleRequest, 'req_123');
  assert.strictEqual(savedEvent.data.eventType, 'status_change');
  assert.strictEqual(savedEvent.data.actorName, 'John Doe');
  assert.strictEqual(savedEvent.data.actorType, 'staff');
  assert.strictEqual(savedEvent.data.message, 'Status updated');
});

runTest('recordEvent error handling - logs warning', () => {
  let loggerCalled = false;
  const mockApp = {
    findCollectionByNameOrId: () => {
      throw new Error('Simulated DB error');
    },
    logger: () => ({
      warn: (msg, key1, val1, key2, val2) => {
        loggerCalled = true;
        assert.strictEqual(msg, 'Failed to record title request event');
        assert.strictEqual(key1, 'recordId');
        assert.strictEqual(val1, 'req_456');
        assert.strictEqual(key2, 'error');
        assert.strictEqual(val2, 'Error: Simulated DB error');
      }
    })
  };

  const mockRecord = { id: 'req_456' };
  // Should not throw unhandled exception
  recordEvent(mockApp, mockRecord, 'status_change', 'Status updated');

  assert.strictEqual(loggerCalled, true);
});

runTest('recordEvent error handling - logger also throws', () => {
  const mockApp = {
    findCollectionByNameOrId: () => {
      throw new Error('Simulated DB error');
    },
    logger: () => ({
      warn: () => {
        throw new Error('Simulated logger error');
      }
    })
  };

  const mockRecord = { id: 'req_789' };
  // Should not throw unhandled exception
  recordEvent(mockApp, mockRecord, 'status_change', 'Status updated');
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}