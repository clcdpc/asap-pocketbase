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

const { duplicateContext } = require('../pb_hooks/lib/records.js');

class MockRecord {
  constructor(data) {
    this.data = data || {};
    this.id = this.data.id;
    this.created = this.data.createdDirectly;
  }
  get(key) {
    return this.data[key];
  }
}

const testCases = [
  {
    name: 'all fields present with get method',
    record: new MockRecord({
      id: 'rec123',
      created: '2023-01-01T00:00:00Z',
      status: 'suggestion',
      closeReason: '',
      title: 'The Great Gatsby',
      author: 'F. Scott Fitzgerald',
      format: 'book'
    }),
    matchType: 'bibid',
    expected: {
      id: 'rec123',
      created: '2023-01-01T00:00:00Z',
      status: 'suggestion',
      closeReason: '',
      title: 'The Great Gatsby',
      author: 'F. Scott Fitzgerald',
      format: 'book',
      matchType: 'bibid'
    }
  },
  {
    name: 'missing matchType uses default',
    record: new MockRecord({
      id: 'rec124',
      created: '2023-01-02T00:00:00Z',
      title: 'Moby Dick'
    }),
    matchType: undefined,
    expected: {
      id: 'rec124',
      created: '2023-01-02T00:00:00Z',
      status: '',
      closeReason: '',
      title: 'Moby Dick',
      author: '',
      format: '',
      matchType: 'title_format'
    }
  },
  {
    name: 'empty record',
    record: new MockRecord({}),
    matchType: 'identifier',
    expected: {
      id: '',
      created: '',
      status: '',
      closeReason: '',
      title: '',
      author: '',
      format: '',
      matchType: 'identifier'
    }
  },
  {
    name: 'created directly on object instead of via get',
    record: new MockRecord({
      id: 'rec125',
      createdDirectly: '2023-01-03T00:00:00Z'
    }),
    matchType: 'title_format',
    expected: {
      id: 'rec125',
      created: '2023-01-03T00:00:00Z',
      status: '',
      closeReason: '',
      title: '',
      author: '',
      format: '',
      matchType: 'title_format'
    }
  }
];

console.log('Running tests for duplicateContext in pb_hooks/lib/records.js...');

let passed = 0;
let failed = 0;

testCases.forEach((tc) => {
  try {
    const actual = duplicateContext(tc.record, tc.matchType);
    assert.deepStrictEqual(actual, tc.expected, `Test case "${tc.name}" failed`);
    console.log(`✅ Test case "${tc.name}" passed`);
    passed++;
  } catch (err) {
    console.error(`❌ ${err.message}`);
    failed++;
  }
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
