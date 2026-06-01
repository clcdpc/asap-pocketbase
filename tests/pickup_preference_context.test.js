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

{
  let calls = [];
  cacheMock.getCachedPickupBranchesWithMeta = (app, staff, patronOrgId) => {
    calls.push(String(patronOrgId || ""));
    if (String(patronOrgId) === "9") {
      return { branches: [], refreshedAt: "2026-06-01T12:00:00.000Z" };
    }
    if (String(patronOrgId) === "8") {
      return {
        branches: [{ id: "12", label: "Fairfield County Bremen Branch" }],
        refreshedAt: "2026-06-01T13:00:00.000Z"
      };
    }
    return { branches: [], refreshedAt: "" };
  };

  const ctx = pickup.buildPickupPreferenceContext({}, {}, {
    PatronOrgID: "9",
    LibraryOrgID: "8",
    CurrentPreferredPickupBranchID: "12"
  });

  assert.deepStrictEqual(calls, ["9", "8"]);
  assert.strictEqual(ctx.selectedPickupBranchId, "12");
  assert.strictEqual(ctx.currentPreferenceAllowed, true);
  assert.strictEqual(ctx.pickupBranchWarning, "");
  assert.strictEqual(ctx.pickupBranchesRefreshedAt, "2026-06-01T13:00:00.000Z");
}

{
  let calls = [];
  cacheMock.getCachedPickupBranchesWithMeta = (app, staff, patronOrgId) => {
    calls.push(String(patronOrgId || ""));
    if (String(patronOrgId) === "9") {
      throw new Error("upstream org lookup failed");
    }
    if (String(patronOrgId) === "8") {
      return {
        branches: [{ id: "12", label: "Fairfield County Bremen Branch" }],
        refreshedAt: "2026-06-01T14:00:00.000Z"
      };
    }
    return { branches: [], refreshedAt: "" };
  };

  const ctx = pickup.buildPickupPreferenceContext({}, {}, {
    PatronOrgID: "9",
    LibraryOrgID: "8",
    CurrentPreferredPickupBranchID: "12"
  });

  assert.deepStrictEqual(calls, ["9", "8"]);
  assert.strictEqual(ctx.selectedPickupBranchId, "12");
  assert.strictEqual(ctx.currentPreferenceAllowed, true);
  assert.strictEqual(ctx.pickupBranchWarning, "");
  assert.strictEqual(ctx.pickupBranchesRefreshedAt, "2026-06-01T14:00:00.000Z");
}

{
  let calls = [];
  cacheMock.getCachedPickupBranchesWithMeta = (app, staff, patronOrgId) => {
    calls.push(String(patronOrgId || ""));
    if (String(patronOrgId) === "8") {
      return {
        branches: [{ id: "12", label: "Fairfield County Bremen Branch" }],
        refreshedAt: "2026-06-01T15:00:00.000Z"
      };
    }
    return { branches: [], refreshedAt: "" };
  };

  const ctx = pickup.buildPickupPreferenceContext({}, {}, {
    PatronOrgID: "po1",
    LibraryOrgID: "8",
    CurrentPreferredPickupBranchID: "12"
  });

  assert.deepStrictEqual(calls, ["8"]);
  assert.strictEqual(ctx.selectedPickupBranchId, "12");
  assert.strictEqual(ctx.currentPreferenceAllowed, true);
  assert.strictEqual(ctx.pickupBranchWarning, "");
}

console.log("pickup preference context tests passed.");
