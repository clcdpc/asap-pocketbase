const assert = require("assert");
global.__hooks = __dirname + "/../pb_hooks";

const staffRoutes = require("../lib/staff_routes.js");
const actions = require("../lib/staff/title_request_actions.js");

function assertFn(name) {
  assert.strictEqual(typeof staffRoutes[name], "function", name + " should be exported");
}

assertFn("staffTitleRequestAction");
assert.strictEqual(typeof actions.applyCatalogFoundWorkflow, "function", "applyCatalogFoundWorkflow should be exported from actions");

console.log("staff_title_request_action.test.js passed.");
