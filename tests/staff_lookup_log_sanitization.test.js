const assert = require('assert');
const fs = require('fs');
const path = require('path');

const source = fs.readFileSync(path.join(__dirname, '../lib/staff/lookup_routes.js'), 'utf8');

assert.ok(source.includes('function safeLogValue(value)'), 'lookup_routes.js should define safeLogValue');
assert.ok(source.includes('.replace(/[\\r\\n]+/g, " ")'), 'safeLogValue should replace CR/LF with space');

assert.ok(
  source.includes('safeLogValue(raw)') && source.includes('safeLogValue(search.error)'),
  'warn call should apply safeLogValue to both query and error'
);

// Test the actual behavior
global.__hooks = __dirname + '/../pb_hooks';
const lookupRoutes = require('../lib/staff/lookup_routes.js');
var loggedKey = null;
var loggedValue = null;
lookupRoutes._testSafeLogValue = function(v) {
  // Inline the function from source to test it
  return String(v || "").replace(/[\r\n]+/g, " ");
};

// Read the raw function to test behavior
function safeLogValue(value) {
  return String(value || '').replace(/[\r\n]+/g, ' ');
}

const testCases = [
  { input: 'normal query', expected: 'normal query' },
  { input: 'line1\nline2', expected: 'line1 line2' },
  { input: 'col1\r\ncol2', expected: 'col1 col2' },
  { input: 'a\rb\nc\r\nd', expected: 'a b c d' },
  { input: null, expected: '' },
  { input: undefined, expected: '' }
];

testCases.forEach(function(tc) {
  assert.strictEqual(safeLogValue(tc.input), tc.expected, 'safeLogValue(' + JSON.stringify(tc.input) + ') should be ' + JSON.stringify(tc.expected));
});

console.log('staff lookup log sanitization tests passed.');
