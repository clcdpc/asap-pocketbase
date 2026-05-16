const assert = require("assert");
global.__hooks = __dirname + "/../pb_hooks";

const settingsRoutes = require("../lib/staff/settings_routes.js");
const staffRoutes = require("../lib/staff_routes.js");

assert.deepStrictEqual(Object.keys(settingsRoutes).sort(), [
  "formatClaimRulesForLibrary",
  "formatClaimStaffOptions",
  "getLibraryOverridesSummary",
  "getLibrarySettings",
  "hasLibraryOverride",
  "normalizeRelationId",
  "organizationSyncStatus",
  "staffEmailStatus",
  "updateLibrarySettings",
  "workflowWithEnabled",
].sort());

assert.strictEqual(typeof staffRoutes.staffTitleRequestAction, "function");
assert.strictEqual("staffTitleRequestAction" in settingsRoutes, false);
assert.strictEqual("staffTestPolaris" in settingsRoutes, false);
assert.strictEqual("staffSyncOrganizations" in settingsRoutes, false);

console.log("staff_settings_routes_exports.test.js passed.");
