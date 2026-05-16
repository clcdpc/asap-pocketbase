const assert = require("assert");
global.__hooks = __dirname + "/../pb_hooks";

const staffRoutes = require("../lib/staff_routes.js");

function assertFn(name) {
  assert.strictEqual(typeof staffRoutes[name], "function", name + " should be exported");
}

assertFn("staffLogin");
assertFn("staffUsersList");
assertFn("staffUserCreate");
assertFn("staffUserRoleUpdate");
assertFn("staffUserDelete");
assertFn("staffProfileUpdate");

console.log("staff_auth_users.test.js passed.");
