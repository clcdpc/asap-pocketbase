const assert = require('assert');
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

// --- requestHeader() Tests ---

test('requestHeader() gets header from headers object mapping correctly', () => {
  const e = {
    requestInfo: () => ({
      headers: {
        'Test-Header': 'test-value',
        'test-header2': 'test-value2'
      }
    })
  };
  assert.strictEqual(httpUtils.requestHeader(e, 'Test-Header'), 'test-value');
  assert.strictEqual(httpUtils.requestHeader(e, 'test-header2'), 'test-value2');
  assert.strictEqual(httpUtils.requestHeader(e, 'Non-Existent'), '');
});

test('requestHeader() gets header using headers.get() method if present', () => {
  const e = {
    requestInfo: () => ({
      headers: {
        get: (name) => {
          if (name === 'Test-Header' || name === 'test-header') return 'test-value';
          return '';
        }
      }
    })
  };
  assert.strictEqual(httpUtils.requestHeader(e, 'Test-Header'), 'test-value');
});

test('requestHeader() fallback: uses e.request.header.get when requestInfo throws and headers is null', () => {
  const e = {
    requestInfo: () => { throw new Error('Boom'); },
    request: {
      header: {
        get: (name) => name === 'test-header' ? 'test-value' : ''
      }
    }
  };
  assert.strictEqual(httpUtils.requestHeader(e, 'test-header'), 'test-value');
});

test('requestHeader() fallback: uses e.request.headers.get when requestInfo is missing', () => {
  const e = {
    request: {
      headers: {
        get: (name) => name === 'test-header' ? 'test-value' : ''
      }
    }
  };
  assert.strictEqual(httpUtils.requestHeader(e, 'test-header'), 'test-value');
});

test('requestHeader() handles missing requestInfo and no request gracefully', () => {
  const e = {};
  assert.strictEqual(httpUtils.requestHeader(e, 'Test-Header'), '');
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
