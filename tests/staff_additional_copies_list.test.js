const assert = require("assert");
global.__hooks = __dirname + "/../pb_hooks";

const staffRoutes = require("../lib/staff_routes.js");

function assertFn(name) {
  assert.strictEqual(typeof staffRoutes[name], "function", name + " should be exported");
}

assertFn("staffAdditionalCopiesList");

assertFn("staffAdditionalCopyClose");
assertFn("staffAdditionalCopyReopen");
assertFn("staffAdditionalCopyClaim");
assertFn("staffAdditionalCopyUnclaim");

console.log("staff_additional_copies_list.test.js passed.");
