const assert = require('assert');
const path = require('path');
const httpUtils = require('../lib/http_utils.js');

console.log('Running tests for lib/http_utils.js...');

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

// --- queryValue() Tests ---

test('queryValue() retrieves value using query.get() function', () => {
  const e = {
    requestInfo: () => ({
      query: {
        get: (n) => n === 'foo' ? 'bar' : undefined
      }
    })
  };
  assert.strictEqual(httpUtils.queryValue(e, 'foo'), 'bar');
});

test('queryValue() retrieves value using direct query array/object access', () => {
  const e = {
    requestInfo: () => ({
      query: {
        foo: 'bar'
      }
    })
  };
  assert.strictEqual(httpUtils.queryValue(e, 'foo'), 'bar');
});

test('queryValue() ignores null/undefined values from query.get() and query object', () => {
  const e = {
    requestInfo: () => ({
      query: {
        get: () => null,
        foo: undefined
      }
    })
  };
  assert.strictEqual(httpUtils.queryValue(e, 'foo'), '');
});

test('queryValue() falls back to parsing info.url', () => {
  const e = {
    requestInfo: () => ({
      url: 'http://example.com?foo=baz'
    })
  };
  assert.strictEqual(httpUtils.queryValue(e, 'foo'), 'baz');
});

test('queryValue() falls back to parsing e.request.url', () => {
  const e = {
    requestInfo: () => ({}),
    request: {
      url: 'http://example.com?foo=baz2'
    }
  };
  assert.strictEqual(httpUtils.queryValue(e, 'foo'), 'baz2');
});

test('queryValue() falls back to parsing e.request.URL', () => {
  const e = {
    requestInfo: () => ({}),
    request: {
      URL: 'http://example.com?foo=baz3'
    }
  };
  assert.strictEqual(httpUtils.queryValue(e, 'foo'), 'baz3');
});

test('queryValue() handles e.requestInfo throwing an error gracefully', () => {
  const e = {
    requestInfo: () => { throw new Error('Simulated error'); },
    request: {
      url: 'http://example.com?foo=fallback'
    }
  };
  assert.strictEqual(httpUtils.queryValue(e, 'foo'), 'fallback');
});

test('queryValue() handles e.request errors gracefully', () => {
    const e = {
      requestInfo: () => ({}),
      get request() {
          throw new Error('Simulated request error')
      }
    };
    assert.strictEqual(httpUtils.queryValue(e, 'foo'), '');
});

test('queryValue() returns empty string when not found', () => {
  const e = {
    requestInfo: () => ({
      query: { foo: 'bar' },
      url: 'http://example.com?foo=bar'
    })
  };
  assert.strictEqual(httpUtils.queryValue(e, 'baz'), '');
});


console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
