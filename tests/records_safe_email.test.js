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

const { safeEmail } = require('../lib/records.js');

const testCases = [
  {
    name: 'valid email',
    input: 'test@example.com',
    expected: 'test@example.com'
  },
  {
    name: 'email with leading/trailing whitespace',
    input: '  test@example.com  ',
    expected: 'test@example.com'
  },
  {
    name: 'string without @ symbol',
    input: 'notanemail',
    expected: ''
  },
  {
    name: 'string with @ at the beginning',
    input: '@example.com',
    expected: ''
  },
  {
    name: 'string with @ at the end',
    input: 'user@',
    expected: 'user@' // indexOf("@") > 0 is true
  },
  {
    name: 'empty string',
    input: '',
    expected: ''
  },
  {
    name: 'null input',
    input: null,
    expected: ''
  },
  {
    name: 'undefined input',
    input: undefined,
    expected: ''
  },
  {
    name: 'number input',
    input: 123,
    expected: ''
  },
  {
    name: 'object input',
    input: { email: 'test@example.com' },
    expected: '' // String({email: '...'}) is "[object Object]", no @ at index > 0? Wait, "[object Object]".indexOf("@") is -1
  },
  {
    name: 'valid email but not at index > 0 (wait, already covered by @example.com)',
    input: 'a@b',
    expected: 'a@b'
  }
];

console.log('Running tests for safeEmail in pb_hooks/lib/records.js...');

let passed = 0;
let failed = 0;

testCases.forEach((tc) => {
  const actual = safeEmail(tc.input);

  try {
    assert.strictEqual(actual, tc.expected, `Test case "${tc.name}" failed: input="${tc.input}"`);
    console.log(`✅ Test case "${tc.name}" passed`);
    passed++;
  } catch (err) {
    console.error(`❌ ${err.message}`);
    console.error(`   Expected: "${tc.expected}"`);
    console.error(`   Actual:   "${actual}"`);
    failed++;
  }
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
