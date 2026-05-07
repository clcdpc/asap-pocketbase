const assert = require('assert');
const path = require('path');

// Mock __hooks globally for the required modules
global.__hooks = path.resolve(__dirname, "../pb_hooks");

const Module = require('module');
const originalRequire = Module.prototype.require;

let routeUtilsMock = {
  body: (e) => e.requestBody || {}
};

Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/config.js")) {
    return {};
  }
  if (moduleName.includes("lib/format_rules.js")) {
    return {};
  }
  if (moduleName.includes("lib/mail.js")) {
    return {};
  }
  if (moduleName.includes("lib/orgs.js")) {
    return {};
  }
  if (moduleName.includes("lib/polaris.js")) {
    return {};
  }
  if (moduleName.includes("lib/records.js")) {
    return {};
  }
  if (moduleName.includes("lib/route_utils.js")) {
    return routeUtilsMock;
  }
  return originalRequire.apply(this, arguments);
};

const patronRoutes = require('../lib/patron_routes.js');

// Restore original require after importing the module under test
Module.prototype.require = originalRequire;

let passed = 0;
let failed = 0;

function runTest(name, testFn) {
  try {
    testFn();
    console.log(`✅ Test case "${name}" passed`);
    passed++;
  } catch (err) {
    console.error(`❌ Test case "${name}" failed`);
    console.error(`   ${err.stack || err.message || err}`);
    failed++;
  }
}

// Helper to mock the event object (e)
function createEventMock(requestBody) {
  let jsonStatus = null;
  let jsonPayload = null;

  const e = {
    requestBody: requestBody,
    json: (status, payload) => {
      jsonStatus = status;
      jsonPayload = payload;
      return 'response';
    }
  };

  return {
    e: e,
    getResponse: () => ({ status: jsonStatus, payload: jsonPayload })
  };
}

runTest('patronLogin returns 400 when missing both barcode and PIN', () => {
  const { e, getResponse } = createEventMock({});

  const result = patronRoutes.patronLogin(e);

  const { status, payload } = getResponse();
  assert.strictEqual(result, 'response');
  assert.strictEqual(status, 400);
  assert.deepStrictEqual(payload, { message: "Barcode and PIN are required" });
});

runTest('patronLogin returns 400 when missing PIN', () => {
  const { e, getResponse } = createEventMock({ barcode: '123456789' });

  const result = patronRoutes.patronLogin(e);

  const { status, payload } = getResponse();
  assert.strictEqual(result, 'response');
  assert.strictEqual(status, 400);
  assert.deepStrictEqual(payload, { message: "Barcode and PIN are required" });
});

runTest('patronLogin returns 400 when missing barcode', () => {
  const { e, getResponse } = createEventMock({ pin: '1234' });

  const result = patronRoutes.patronLogin(e);

  const { status, payload } = getResponse();
  assert.strictEqual(result, 'response');
  assert.strictEqual(status, 400);
  assert.deepStrictEqual(payload, { message: "Barcode and PIN are required" });
});

runTest('patronLogin returns 400 when barcode and PIN are empty strings', () => {
  const { e, getResponse } = createEventMock({ barcode: '', pin: '' });

  const result = patronRoutes.patronLogin(e);

  const { status, payload } = getResponse();
  assert.strictEqual(result, 'response');
  assert.strictEqual(status, 400);
  assert.deepStrictEqual(payload, { message: "Barcode and PIN are required" });
});

runTest('patronLogin returns 400 when barcode and PIN are just whitespace', () => {
  const { e, getResponse } = createEventMock({ barcode: '   ', pin: ' ' });

  const result = patronRoutes.patronLogin(e);

  const { status, payload } = getResponse();
  assert.strictEqual(result, 'response');
  assert.strictEqual(status, 400);
  assert.deepStrictEqual(payload, { message: "Barcode and PIN are required" });
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
