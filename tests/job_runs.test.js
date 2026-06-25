const assert = require('assert');

// Mock __hooks globally if needed
global.__hooks = __dirname + "/../pb_hooks";

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

const { startJobRun, finishJobRun } = require('../lib/job_runs.js');

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

console.log('Running tests for job_runs in lib/job_runs.js...');

runTest('startJobRun - success', () => {
  let savedRecord = null;
  const mockApp = {
    findCollectionByNameOrId: (name) => {
      assert.strictEqual(name, "job_runs");
      return { name };
    },
    save: (record) => {
      savedRecord = record;
    }
  };

  const record = startJobRun(mockApp, 'my_test_job');

  assert.ok(record);
  assert.strictEqual(record, savedRecord);
  assert.strictEqual(record.data.jobName, 'my_test_job');
  assert.strictEqual(record.data.status, 'running');
  assert.ok(record.data.startedAt);
});

runTest('startJobRun - handles error and returns null', () => {
  const mockApp = {
    findCollectionByNameOrId: () => {
      throw new Error('Database error');
    }
  };

  const record = startJobRun(mockApp, 'my_test_job');
  assert.strictEqual(record, null);
});

runTest('finishJobRun - success', () => {
  let savedRecord = null;
  const mockApp = {
    save: (record) => {
      savedRecord = record;
    }
  };
  const mockRecord = new MockRecord({ name: 'job_runs' });

  finishJobRun(mockApp, mockRecord, 'completed', { count: 5 }, 'No errors');

  assert.strictEqual(savedRecord, mockRecord);
  assert.strictEqual(mockRecord.data.status, 'completed');
  assert.ok(mockRecord.data.finishedAt);
  assert.deepStrictEqual(mockRecord.data.summary, { count: 5 });
  assert.strictEqual(mockRecord.data.error, 'No errors');
});

runTest('finishJobRun - missing summary and error', () => {
  let savedRecord = null;
  const mockApp = {
    save: (record) => {
      savedRecord = record;
    }
  };
  const mockRecord = new MockRecord({ name: 'job_runs' });

  finishJobRun(mockApp, mockRecord, 'failed');

  assert.strictEqual(savedRecord, mockRecord);
  assert.strictEqual(mockRecord.data.status, 'failed');
  assert.ok(mockRecord.data.finishedAt);
  assert.deepStrictEqual(mockRecord.data.summary, {});
  assert.strictEqual(mockRecord.data.error, '');
});

runTest('finishJobRun - handles missing record', () => {
  let saved = false;
  const mockApp = {
    save: () => {
      saved = true;
    }
  };

  finishJobRun(mockApp, null, 'completed');
  assert.strictEqual(saved, false);
});

runTest('finishJobRun - catches error silently', () => {
  const mockApp = {
    save: () => {
      throw new Error('Save error');
    }
  };
  const mockRecord = new MockRecord({ name: 'job_runs' });

  // Should not throw
  finishJobRun(mockApp, mockRecord, 'completed');
  assert.strictEqual(mockRecord.data.status, 'completed');
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
