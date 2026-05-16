const assert = require("assert");
global.__hooks = __dirname + "/../pb_hooks";

const staffRoutes = require("../lib/staff_routes.js");
const list = require("../lib/staff/title_request_list.js");

function assertFn(name) {
  assert.strictEqual(typeof staffRoutes[name], "function", name + " should be exported");
}

assertFn("staffTitleRequestsList");
assert.strictEqual(typeof list.titleRequestListScope, "function");
assert.strictEqual(typeof list.titleRequestListResponseScope, "function");

console.log("staff_title_request_list.test.js passed.");
