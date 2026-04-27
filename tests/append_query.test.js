const assert = require('assert');

// We need to define __hooks globally before requiring polaris.js
// so that the require statement inside polaris.js works correctly
global.__hooks = require('path').resolve(__dirname, '../pb_hooks');
const polaris = require('../pb_hooks/lib/polaris.js');

console.log('Running tests for appendQuery...');

let passed = 0;
let failed = 0;

// Test 1: Empty query
try {
  const ep = { full: 'http://example.com', signature: 'http://example.com' };
  const result = polaris.appendQuery(ep, '');
  assert.strictEqual(result.full, 'http://example.com');
  assert.strictEqual(result.signature, 'http://example.com');
  assert.strictEqual(result, ep); // Should return the same object
  console.log('✅ Test case 1 (empty query) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 1 failed:', err.message);
  failed++;
}

// Test 2: Null/undefined query
try {
  const ep = { full: 'http://example.com', signature: 'http://example.com' };
  const result = polaris.appendQuery(ep, null);
  assert.strictEqual(result.full, 'http://example.com');
  assert.strictEqual(result.signature, 'http://example.com');
  assert.strictEqual(result, ep);
  console.log('✅ Test case 2 (null query) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 2 failed:', err.message);
  failed++;
}

// Test 3: Query starting with ?
try {
  const ep = { full: 'http://example.com', signature: 'http://example.com' };
  const result = polaris.appendQuery(ep, '?foo=bar');
  assert.strictEqual(result.full, 'http://example.com?foo=bar');
  assert.strictEqual(result.signature, 'http://example.com?foo=bar');
  assert.strictEqual(result, ep);
  console.log('✅ Test case 3 (query starting with ?) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 3 failed:', err.message);
  failed++;
}

// Test 4: Query not starting with ?
try {
  const ep = { full: 'http://example.com', signature: 'http://example.com' };
  const result = polaris.appendQuery(ep, 'foo=bar');
  assert.strictEqual(result.full, 'http://example.com?foo=bar');
  assert.strictEqual(result.signature, 'http://example.com?foo=bar');
  assert.strictEqual(result, ep);
  console.log('✅ Test case 4 (query not starting with ?) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 4 failed:', err.message);
  failed++;
}

// Test 5: Endpoint with existing path and parameters
try {
  const ep = { full: 'http://example.com/api?existing=1', signature: 'http://example.com/api?existing=1' };
  // Note: the current implementation of appendQuery just appends "?" if it doesn't start with "?".
  // So if we append "new=2", it will become "?new=2".
  // If the URL already has "?", this logic might result in "http://example.com/api?existing=1?new=2",
  // or the caller is expected to handle it. Let's just test its pure behavior.
  const result = polaris.appendQuery(ep, '&new=2');
  assert.strictEqual(result.full, 'http://example.com/api?existing=1?&new=2');
  assert.strictEqual(result.signature, 'http://example.com/api?existing=1?&new=2');
  console.log('✅ Test case 5 (existing parameters pure behavior) passed');
  passed++;
} catch (err) {
  console.error('❌ Test case 5 failed:', err.message);
  failed++;
}

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
