const assert = require("assert");
const fs = require("fs");
const path = require("path");
global.__hooks = __dirname + "/../pb_hooks";

const staffRoutes = require("../lib/staff_routes.js");
const actions = require("../lib/staff/title_request_actions.js");

function assertFn(name) {
  assert.strictEqual(typeof staffRoutes[name], "function", name + " should be exported");
}

assertFn("staffTitleRequestAction");
assertFn("staffTitleRequestAdditionalCopyPreview");
assertFn("staffTitleRequestAdditionalCopyCreate");
assert.strictEqual(typeof actions.applyCatalogFoundWorkflow, "function", "applyCatalogFoundWorkflow should be exported from actions");
assert.strictEqual(typeof actions.autoClaimTitleRequestAction, "function", "autoClaimTitleRequestAction should be exported from actions");

const mainHook = fs.readFileSync(path.resolve(__dirname, "../pb_hooks/main.pb.js"), "utf8");
assert.ok(mainHook.includes('"/api/asap/staff/title-requests/{id}/additional-copy"'));
assert.ok(mainHook.includes(".staffTitleRequestAdditionalCopyPreview(e)"));
assert.ok(mainHook.includes(".staffTitleRequestAdditionalCopyCreate(e)"));

console.log("staff_title_request_action.test.js passed.");
