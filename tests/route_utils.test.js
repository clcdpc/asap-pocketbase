const assert = require('assert');
const path = require('path');

// Set up globals for the environment
global.__hooks = path.resolve(__dirname, '../pb_hooks');
global.UnauthorizedError = class UnauthorizedError extends Error {
  constructor(message) {
    super(message);
    this.name = 'UnauthorizedError';
    this.status = 401;
  }
};

const routeUtils = require('../lib/route_utils.js');

console.log('Running tests for lib/route_utils.js...');

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

// --- Request Parsing Functions ---

test('body() returns request body', () => {
  const e = {
    requestInfo: () => ({ body: { foo: 'bar' } })
  };
  assert.deepStrictEqual(routeUtils.body(e), { foo: 'bar' });
});

test('body() returns empty object if no body', () => {
  const e = {
    requestInfo: () => ({})
  };
  assert.deepStrictEqual(routeUtils.body(e), {});
});

test('requestHeader() retrieves header case-insensitively from requestInfo', () => {
  const e = {
    requestInfo: () => ({
      headers: {
        'Content-Type': 'application/json',
        'content-type': 'application/json',
        get: function(n) { return this[n]; }
      }
    })
  };
  assert.strictEqual(routeUtils.requestHeader(e, 'Content-Type'), 'application/json');
  assert.strictEqual(routeUtils.requestHeader(e, 'content-type'), 'application/json');
});

test('requestHeader() retrieves header from e.request.header', () => {
  const e_dead = {
    requestInfo: () => ({ headers: null }),
    request: {
      header: {
        get: (n) => 'SHOULD NOT REACH'
      }
    }
  };
  assert.strictEqual(routeUtils.requestHeader(e_dead, 'X-Test'), '');
});

test('queryValue() retrieves value from requestInfo.query', () => {
  const e = {
    requestInfo: () => ({
      query: {
        foo: 'bar',
        get: function(n) { return this[n]; }
      }
    })
  };
  assert.strictEqual(routeUtils.queryValue(e, 'foo'), 'bar');
});

test('queryValue() falls back to parsing URL', () => {
  const e = {
    requestInfo: () => ({
      url: 'http://example.com?foo=baz'
    })
  };
  assert.strictEqual(routeUtils.queryValue(e, 'foo'), 'baz');
});

test('queryValueFromUrl() parses query parameters', () => {
  assert.strictEqual(routeUtils.queryValueFromUrl('http://example.com?a=1&b=2', 'a'), '1');
  assert.strictEqual(routeUtils.queryValueFromUrl('http://example.com?a=1&b=2', 'b'), '2');
  assert.strictEqual(routeUtils.queryValueFromUrl('http://example.com?a=hello+world', 'a'), 'hello world');
  assert.strictEqual(routeUtils.queryValueFromUrl('http://example.com', 'a'), '');
});

test('queryValueFromUrl() handles malformed URI strings securely', () => {
  // These would normally throw "URIError: URI malformed" with decodeURIComponent
  assert.strictEqual(routeUtils.queryValueFromUrl('http://example.com?%xyz=1', '%xyz'), '1');
  assert.strictEqual(routeUtils.queryValueFromUrl('http://example.com?a=%xyz', 'a'), '%xyz');
  assert.strictEqual(routeUtils.queryValueFromUrl('http://example.com?%=%', '%'), '%');
});

test('parseJsonObject() parses valid JSON strings', () => {
  assert.deepStrictEqual(routeUtils.parseJsonObject('{"a":1}'), { a: 1 });
  assert.deepStrictEqual(routeUtils.parseJsonObject('  {"a":1}  '), { a: 1 });
});

test('parseJsonObject() handles nested stringified JSON', () => {
  assert.deepStrictEqual(routeUtils.parseJsonObject(JSON.stringify('{"a":1}')), { a: 1 });
});

test('parseJsonObject() returns fallback for invalid input', () => {
  const fallback = { d: 1 };
  assert.deepStrictEqual(routeUtils.parseJsonObject('invalid', fallback), fallback);
  assert.deepStrictEqual(routeUtils.parseJsonObject(null, fallback), fallback);
  assert.deepStrictEqual(routeUtils.parseJsonObject('[]', fallback), fallback); // Not an object
});

// --- Auth & Access Functions ---

test('requireAuth() returns auth if valid', () => {
  const auth = { collection: () => ({ name: 'test' }) };
  const e = { requestInfo: () => ({ auth }) };
  assert.strictEqual(routeUtils.requireAuth(e, 'test'), auth);
});

test('requireAuth() throws if invalid', () => {
  const e = { requestInfo: () => ({ auth: null }) };
  assert.throws(() => routeUtils.requireAuth(e, 'test'), UnauthorizedError);
});

test('isSuperAdmin() identifies super_admin', () => {
  const staff = { get: (k) => k === 'role' ? 'super_admin' : '' };
  assert.strictEqual(routeUtils.isSuperAdmin(staff), true);
});

test('isAdminRole() identifies admin and super_admin', () => {
  const admin = { get: (k) => k === 'role' ? 'admin' : '' };
  const superAdmin = { get: (k) => k === 'role' ? 'super_admin' : '' };
  const other = { get: (k) => k === 'role' ? 'other' : '' };
  assert.strictEqual(routeUtils.isAdminRole(admin), true);
  assert.strictEqual(routeUtils.isAdminRole(superAdmin), true);
  assert.strictEqual(routeUtils.isAdminRole(other), false);
});

test('sameLibrary() checks library org ID', () => {
  const staff = {
    get: (k) => k === 'libraryOrgId' ? 'LIB1' : '',
  };
  assert.strictEqual(routeUtils.sameLibrary(staff, 'LIB1'), true);
  assert.strictEqual(routeUtils.sameLibrary(staff, 'LIB2'), false);

  const superStaff = {
    get: (k) => k === 'role' ? 'super_admin' : ''
  };
  assert.strictEqual(routeUtils.sameLibrary(superStaff, 'ANY'), true);
});

test('canAccessTitleRequest() uses sameLibrary logic', () => {
  const staff = { get: (k) => k === 'libraryOrgId' ? 'LIB1' : '' };
  const record = { get: (k) => k === 'libraryOrgId' ? 'LIB1' : '' };
  assert.strictEqual(routeUtils.canAccessTitleRequest(staff, record), true);
});

// --- Utility Functions ---

test('escapeHtml() escapes characters', () => {
  assert.strictEqual(routeUtils.escapeHtml('<script>alert("XSS")</script>'), '&lt;script&gt;alert(&quot;XSS&quot;)&lt;/script&gt;');
  assert.strictEqual(routeUtils.escapeHtml("It's & me"), 'It&#39;s &amp; me');
  assert.strictEqual(routeUtils.escapeHtml(null), '');
});

test('formatDuplicateDate() formats date string', () => {
  const input = '2023-01-01 12:00:00';
  const result = routeUtils.formatDuplicateDate(input);
  assert.ok(result.includes('January') && result.includes('2023'));
});

test('duplicateStatusKey() returns appropriate status key', () => {
  assert.strictEqual(routeUtils.duplicateStatusKey({ status: 'open' }), 'open');
  assert.strictEqual(routeUtils.duplicateStatusKey({ status: 'closed', closeReason: 'rejected' }), 'rejected');
  assert.strictEqual(routeUtils.duplicateStatusKey({}), 'suggestion');
});

test('appendQuery() appends parameters to URL', () => {
  assert.strictEqual(routeUtils.appendQuery('http://ex.com', { a: 1 }), 'http://ex.com?a=1');
  assert.strictEqual(routeUtils.appendQuery('http://ex.com?b=2', { a: 1 }), 'http://ex.com?b=2&a=1');
  assert.strictEqual(routeUtils.appendQuery('http://ex.com#hash', { a: 1 }), 'http://ex.com?a=1#hash');
});

test('duplicateMatchLabel() returns human label', () => {
  assert.strictEqual(routeUtils.duplicateMatchLabel('identifier'), 'identifier number');
  assert.strictEqual(routeUtils.duplicateMatchLabel('bibid'), 'catalog record');
  assert.strictEqual(routeUtils.duplicateMatchLabel('unknown'), 'suggestion');
});

test('firstValue() returns first non-null/undefined value', () => {
  const source = { a: 1, b: 2 };
  assert.strictEqual(routeUtils.firstValue(source, ['c', 'b', 'a'], 0), 2);
  assert.strictEqual(routeUtils.firstValue(source, ['x', 'y'], 10), 10);
});

test('boolValue() parses various boolean-like values', () => {
  assert.strictEqual(routeUtils.boolValue('true', false), true);
  assert.strictEqual(routeUtils.boolValue('1', false), true);
  assert.strictEqual(routeUtils.boolValue('on', false), true);
  assert.strictEqual(routeUtils.boolValue('yes', false), true);
  assert.strictEqual(routeUtils.boolValue('false', true), false);
  assert.strictEqual(routeUtils.boolValue('0', true), false);
  assert.strictEqual(routeUtils.boolValue('', true), true);
});

// --- Polaris & ISBN Functions ---

test('buildPolarisData() correctly maps fields', () => {
  const data = {
    host: 'host1',
    accessId: 'aid1',
    apiKey: 'key1'
  };
  const result = routeUtils.buildPolarisData(data);
  assert.strictEqual(result.host, 'host1');
  assert.strictEqual(result.accessId, 'aid1');
  assert.strictEqual(result.apiKey, 'key1');
  assert.strictEqual(result.autoPromote, undefined);
});

test('missingPolarisTestFields() identifies missing fields', () => {
  const data = { host: 'h' };
  const missing = routeUtils.missingPolarisTestFields(data);
  assert.ok(missing.includes('access ID'));
  assert.ok(missing.includes('API key'));
});

test('isIsbnCapableFormat() checks format rules', () => {
  const uiText = {
    formatRules: {
      book: { fields: { identifier: { mode: 'required' } } }
    }
  };
  assert.strictEqual(routeUtils.isIsbnCapableFormat('book', uiText), true);
});

test('applyIsbnCheckStatusForCreate() sets status', () => {
  const data = { format: 'book', isbn: '123' };
  const uiText = {
    formatRules: {
      book: { fields: { identifier: { mode: 'required' } } }
    }
  };
  routeUtils.applyIsbnCheckStatusForCreate(data, uiText);
  assert.strictEqual(data.isbnCheckStatus, 'pending');

  const data2 = { format: 'book' };
  routeUtils.applyIsbnCheckStatusForCreate(data2, uiText);
  assert.strictEqual(data2.isbnCheckStatus, 'skipped_no_isbn');
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
