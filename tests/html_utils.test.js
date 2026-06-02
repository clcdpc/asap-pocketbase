const assert = require('assert');
const { escapeHtml } = require('../lib/html_utils.js');

console.log('Running tests for lib/html_utils.js...');

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

// 1. Basic strings
test('escapes basic html chars', () => {
  assert.strictEqual(
    escapeHtml('<div>"Hello" & \'World\'</div>'),
    '&lt;div&gt;&quot;Hello&quot; &amp; &#39;World&#39;&lt;/div&gt;'
  );
});

test('returns normal string without html', () => {
  assert.strictEqual(escapeHtml('Hello World'), 'Hello World');
});

test('returns empty string for empty string input', () => {
  assert.strictEqual(escapeHtml(''), '');
});

// 2. Edge cases (null/undefined)
test('returns empty string for null input', () => {
  assert.strictEqual(escapeHtml(null), '');
});

test('returns empty string for undefined input', () => {
  assert.strictEqual(escapeHtml(undefined), '');
});

// 3. Other non-string primitives
test('converts numbers to string', () => {
  assert.strictEqual(escapeHtml(123), '123');
  assert.strictEqual(escapeHtml(0), '0');
  assert.strictEqual(escapeHtml(NaN), 'NaN');
});

test('converts booleans to string', () => {
  assert.strictEqual(escapeHtml(true), 'true');
  assert.strictEqual(escapeHtml(false), 'false');
});

test('converts arrays to string', () => {
  assert.strictEqual(escapeHtml(['<', '>']), '&lt;,&gt;');
});

test('converts objects to string', () => {
  assert.strictEqual(escapeHtml({}), '[object Object]');
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
