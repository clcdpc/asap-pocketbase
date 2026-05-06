const assert = require('assert');

// Mock __hooks globally for the required modules
global.__hooks = __dirname + "/../pb_hooks";

// Use standard Node testing mechanism by simply exporting test runner functions
// like other test files in this repository.

// Mock dependencies of setup_routes.js
const Module = require('module');
const originalRequire = Module.prototype.require;

let configMock = {
  polaris: () => ({ host: '', accessId: '', apiKey: '' })
};

let recordsMock = {
  hasStaffUsers: () => false
};

Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/config.js")) {
    return configMock;
  }
  if (moduleName.includes("lib/identity.js")) {
    return {};
  }
  if (moduleName.includes("lib/orgs.js")) {
    return {};
  }
  if (moduleName.includes("lib/polaris.js")) {
    return {};
  }
  if (moduleName.includes("lib/records.js")) {
    return recordsMock;
  }
  if (moduleName.includes("lib/route_utils.js")) {
    return {};
  }
  return originalRequire.apply(this, arguments);
};

const { setupStatus } = require('../lib/setup_routes.js');

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

// 1. Test scenario where there are no staff users and Polaris is not configured.
runTest('no staff users, Polaris not configured', () => {
  configMock.polaris = () => ({ host: '', accessId: '', apiKey: '' });
  recordsMock.hasStaffUsers = (app) => {
    assert.strictEqual(app, 'mock_app');
    return false;
  };

  let jsonStatus = null;
  let jsonPayload = null;

  const e = {
    app: 'mock_app',
    json: (status, payload) => {
      jsonStatus = status;
      jsonPayload = payload;
      return 'response';
    }
  };

  const result = setupStatus(e);

  assert.strictEqual(result, 'response');
  assert.strictEqual(jsonStatus, 200);
  assert.deepStrictEqual(jsonPayload, {
    setupRequired: true,
    hasStaffUsers: false,
    polarisConfigured: false
  });
});

// 2. Test scenario where there are staff users and Polaris is configured.
runTest('staff users exist, Polaris is configured', () => {
  configMock.polaris = () => ({ host: 'http://example.com', accessId: 'id', apiKey: 'key' });
  recordsMock.hasStaffUsers = (app) => {
    assert.strictEqual(app, 'mock_app');
    return true;
  };

  let jsonStatus = null;
  let jsonPayload = null;

  const e = {
    app: 'mock_app',
    json: (status, payload) => {
      jsonStatus = status;
      jsonPayload = payload;
      return 'response';
    }
  };

  const result = setupStatus(e);

  assert.strictEqual(result, 'response');
  assert.strictEqual(jsonStatus, 200);
  assert.deepStrictEqual(jsonPayload, {
    setupRequired: false,
    hasStaffUsers: true,
    polarisConfigured: true
  });
});

// 3. Test scenario with partial Polaris configuration (e.g. missing apiKey).
runTest('staff users exist, Polaris partially configured (missing apiKey)', () => {
  configMock.polaris = () => ({ host: 'http://example.com', accessId: 'id', apiKey: '' });
  recordsMock.hasStaffUsers = (app) => {
    assert.strictEqual(app, 'mock_app');
    return true;
  };

  let jsonStatus = null;
  let jsonPayload = null;

  const e = {
    app: 'mock_app',
    json: (status, payload) => {
      jsonStatus = status;
      jsonPayload = payload;
      return 'response';
    }
  };

  const result = setupStatus(e);

  assert.strictEqual(result, 'response');
  assert.strictEqual(jsonStatus, 200);
  assert.deepStrictEqual(jsonPayload, {
    setupRequired: false,
    hasStaffUsers: true,
    polarisConfigured: false
  });
});

console.log(`\nTests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
