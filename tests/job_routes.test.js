const assert = require('assert');
const path = require('path');
const Module = require('module');

// Set up hooks mock
global.__hooks = path.resolve(__dirname, '../pb_hooks');

const originalRequire = Module.prototype.require;

function runTest(name, testFn) {
  try {
    testFn();
    console.log(`✅ ${name}`);
  } catch (err) {
    console.error(`❌ ${name}`);
    console.error(err.stack);
    throw err;
  }
}

// 1. Mock require to intercept jobs.js and polaris.js and route_utils.js
Module.prototype.require = function (moduleName) {
  if (moduleName.includes('lib/jobs.js')) {
    return {
      processOutstandingPurchases: function(app, auth, result) {
        if (app.throwError) {
          throw new Error('processOutstandingPurchases mocked error');
        }
        result.promoted = 1;
      }
    };
  }
  if (moduleName.includes('lib/polaris.js')) {
    return {
      adminStaffAuth: function() {
        return { isStaff: true };
      }
    };
  }
  if (moduleName.includes('lib/route_utils.js')) {
    return {
      requireSuperAdminStaff: function(e) {
        if (e.notAdmin) return false;
        return true;
      }
    };
  }
  return originalRequire.apply(this, arguments);
};

const jobRoutes = require('../lib/job_routes.js');

Module.prototype.require = originalRequire;

function createMockE() {
  let jsonCode = null;
  let jsonBody = null;
  return {
    json: function(code, body) {
      jsonCode = code;
      jsonBody = body;
      return { code, body };
    },
    app: { throwError: false }
  };
}

runTest("staffRunPromoterCheck returns 403 if not super admin", () => {
  const e = createMockE();
  e.notAdmin = true;

  const res = jobRoutes.staffRunPromoterCheck(e);

  assert.strictEqual(res.code, 403);
  assert.strictEqual(res.body.message, "Super admin access required");
});

runTest("staffRunPromoterCheck returns 200 on success", () => {
  const e = createMockE();

  const res = jobRoutes.staffRunPromoterCheck(e);

  assert.strictEqual(res.code, 200);
  assert.strictEqual(res.body.promoted, 1);
});

runTest("staffRunPromoterCheck returns 400 on error from jobs", () => {
  const e = createMockE();
  e.app.throwError = true;

  const res = jobRoutes.staffRunPromoterCheck(e);

  assert.strictEqual(res.code, 400);
  assert.strictEqual(res.body.message, "processOutstandingPurchases mocked error");
});

console.log('All tests passed.');
