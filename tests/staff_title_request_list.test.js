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

function mockRecord(id, values) {
  return {
    id,
    get(key) {
      return values[key] || "";
    },
    getBool(key) {
      return !!values[key];
    },
    email() {
      return values.email || "";
    }
  };
}

const titleRequest = mockRecord("req1", {
  patron: "patron1",
  barcode: "2900",
  nameFirst: "Pat",
  nameLast: "Ron",
  title: "Title",
  status: "suggestion"
});
const patronRecord = mockRecord("patron1", {
  polarisPatronId: "123456",
  nameFirst: "Pat",
  nameLast: "Ron"
});
const row = list.buildStaffTitleRequestRow(
  {},
  titleRequest,
  { patron1: patronRecord },
  {},
  { req1: [] },
  {}
);
assert.strictEqual(row.polarisPatronId, "123456");

console.log("staff_title_request_list.test.js passed.");
