const assert = require("assert");
const path = require("path");
const Module = require("module");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const originalRequire = Module.prototype.require;
const sharedAuth = { AccessToken: "staff-token", AccessSecret: "staff-secret" };
let organizationAuth = null;
let patronCodeAuth = null;

Module.prototype.require = function(moduleName) {
  if (moduleName.includes("lib/route_utils.js")) {
    return { requireSuperAdminStaff: () => true };
  }
  if (moduleName.includes("lib/polaris.js")) {
    return { adminStaffAuth: () => sharedAuth };
  }
  if (moduleName.includes("lib/orgs.js")) {
    return {
      syncOrganizations: (app, auth) => {
        organizationAuth = auth;
        return { synced: 42 };
      }
    };
  }
  if (moduleName.includes("lib/patron_codes.js")) {
    return {
      syncPatronCodes: (app, auth) => {
        patronCodeAuth = auth;
        return { synced: 28 };
      }
    };
  }
  if (
    moduleName.includes("lib/config.js") ||
    moduleName.includes("lib/records.js") ||
    moduleName.includes("lib/mail.js") ||
    moduleName.includes("lib/additional_copies.js") ||
    moduleName.includes("lib/format_claim_rules.js") ||
    moduleName.includes("lib/staff/effective_library.js") ||
    moduleName.includes("lib/polaris/pickup_preference_context.js")
  ) {
    return {};
  }
  if (moduleName.includes("lib/staff/polaris_actor.js")) {
    return { resolvePolarisUpdateActor: () => ({}) };
  }
  return originalRequire.apply(this, arguments);
};

const adminRoutes = require("../lib/staff/admin_routes.js");
Module.prototype.require = originalRequire;

const response = adminRoutes.staffSyncOrganizations({
  app: {
    logger: () => ({ warn: () => {} })
  },
  json: (status, body) => ({ status, body })
});

assert.strictEqual(response.status, 200);
assert.strictEqual(response.body.synced, 42);
assert.strictEqual(response.body.patronCodesSynced, 28);
assert.strictEqual(response.body.patronCodesError, "");
assert.strictEqual(organizationAuth, sharedAuth);
assert.strictEqual(patronCodeAuth, sharedAuth);

console.log("staff_reference_data_sync.test.js passed.");
