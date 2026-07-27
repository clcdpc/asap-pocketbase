const assert = require("assert");
global.__hooks = __dirname + "/../pb_hooks";

const composeExports = require("../lib/staff/compose_exports.js").composeExports;

assert.throws(() => {
  composeExports([
    { name: "a", exports: { duplicate: 1 } },
    { name: "b", exports: { duplicate: 2 } },
  ]);
}, /Duplicate staff export 'duplicate'/);

const staffRoutes = require("../lib/staff_routes.js");
assert.strictEqual(typeof staffRoutes.staffLogin, "function");
assert.strictEqual(typeof staffRoutes.staffTitleRequestAction, "function");
assert.strictEqual(typeof staffRoutes.staffSyncPatronCodes, "function");
assert.strictEqual(typeof staffRoutes.TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE, "string");

console.log("staff_routes_export_guard.test.js passed.");
