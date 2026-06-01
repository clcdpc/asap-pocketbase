const assert = require("assert");
const path = require("path");
const Module = require("module");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const originalRequire = Module.prototype.require;

let cacheMock = {
  getCachedPickupBranchesWithMeta: () => ({
    branches: [{ id: "10", label: "Main" }],
    refreshedAt: "2026-06-01T12:00:00.000Z"
  })
};

Module.prototype.require = function(moduleName) {
  if (moduleName.endsWith("/pickup_branch_cache.js")) return cacheMock;
  return originalRequire.apply(this, arguments);
};

const pickup = require("../lib/polaris/pickup_preference_context.js");
Module.prototype.require = originalRequire;

{
  const ctx = pickup.buildPickupPreferenceContext({}, {}, {
    PatronOrgID: "2",
    CurrentPreferredPickupBranchID: "",
    RequestPickupBranchID: "",
    PreferredPickupBranchID: "2"
  });
  assert.strictEqual(ctx.currentPreferredPickupBranchId, "2");
  assert.strictEqual(ctx.selectedPickupBranchId, "");
  assert.strictEqual(ctx.currentPreferenceAllowed, false);
}

{
  const ctx = pickup.buildPickupPreferenceContext({}, {}, {
    PatronOrgID: "2",
    CurrentPreferredPickupBranchID: "10",
    RequestPickupBranchID: "",
    PreferredPickupBranchID: ""
  });
  assert.strictEqual(ctx.currentPreferredPickupBranchId, "10");
  assert.strictEqual(ctx.selectedPickupBranchId, "10");
  assert.strictEqual(ctx.pickupBranchesRefreshedAt, "2026-06-01T12:00:00.000Z");
}

console.log("pickup preference context tests passed.");
