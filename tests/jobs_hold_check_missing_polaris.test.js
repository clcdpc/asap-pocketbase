const assert = require("assert");
const path = require("path");
const Module = require("module");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const originalRequire = Module.prototype.require;
const calls = {
  outstandingTimeout: 0,
  pendingHoldTimeout: 0,
  holdPickupTimeout: 0,
  additionalCopyTimeout: 0,
  isbnChecks: 0,
  promoter: 0,
  pendingHolds: 0,
  checkedOut: 0,
  loggedErrors: [],
};

Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/polaris.js")) {
    return {
      adminStaffAuth() {
        throw new Error("Missing Polaris configuration");
      },
    };
  }
  if (moduleName === "./jobs/timeouts.js") {
    return {
      processOutstandingTimeout() { calls.outstandingTimeout++; },
      processPendingHoldTimeout() { calls.pendingHoldTimeout++; },
      processHoldPickupTimeout() { calls.holdPickupTimeout++; },
      processAdditionalCopyTimeout() { calls.additionalCopyTimeout++; },
    };
  }
  if (moduleName === "./jobs/isbn_checks.js") {
    return { processPendingIsbnChecks() { calls.isbnChecks++; } };
  }
  if (moduleName === "./jobs/purchase_promoter.js") {
    return { processOutstandingPurchases() { calls.promoter++; } };
  }
  if (moduleName === "./jobs/hold_placement.js") {
    return { classifyPolarisHoldResult() {}, processPendingHolds() { calls.pendingHolds++; } };
  }
  if (moduleName === "./jobs/fulfillment_tracker.js") {
    return { processCheckedOut() { calls.checkedOut++; } };
  }
  if (moduleName === "./jobs/weekly_summary.js") {
    return { buildWeeklyStaffActionSummary() {}, runWeeklyStaffActionSummary() {} };
  }
  return originalRequire.apply(this, arguments);
};

delete require.cache[require.resolve("../lib/jobs.js")];
const jobs = require("../lib/jobs.js");
Module.prototype.require = originalRequire;

const app = {
  logger() {
    return {
      error(message, key, value) {
        calls.loggedErrors.push({ message, key, value });
      },
      info() {},
    };
  },
};

const result = jobs.runScheduledHoldCheck(app);

assert.strictEqual(calls.outstandingTimeout, 1);
assert.strictEqual(calls.pendingHoldTimeout, 1);
assert.strictEqual(calls.holdPickupTimeout, 1);
assert.strictEqual(calls.additionalCopyTimeout, 1);
assert.strictEqual(calls.isbnChecks, 0);
assert.strictEqual(calls.promoter, 0);
assert.strictEqual(calls.pendingHolds, 0);
assert.strictEqual(calls.checkedOut, 0);
assert.strictEqual(result.polarisSkipped, true);
assert.strictEqual(result.error, "Missing Polaris configuration");
assert.strictEqual(result.errors, 1);
assert.strictEqual(calls.loggedErrors[0].message, "ASAP scheduled check skipped Polaris-dependent steps");

console.log("Hold check missing Polaris configuration test passed.");
