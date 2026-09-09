const assert = require("assert");
global.__hooks = __dirname + "/../pb_hooks";

const config = require("../lib/config.js");
const polaris = require("../lib/polaris.js");
const records = require("../lib/records.js");
const orgs = require("../lib/orgs.js");
const routeUtils = require("../lib/route_utils.js");
const publicJson = require("../lib/staff/public_json.js");
const originals = [];
function stub(target, name, fn) {
  originals.push([target, name, target[name]]);
  target[name] = fn;
}

let active = true;
let exists = true;
let enabled = "2";
let authResult = { AccessToken: "test-token", BranchID: 3, PolarisUserID: 42 };
let scope = { branchOrgId: "3", libraryOrgId: "2", libraryOrgName: "Library" };
let authCalls = [];
let saved = [];
let logs = [];
const polarisConfig = { staffDomain: "DEFAULT", overridePassword: "" };
const record = { getBool: () => active, get: key => key === "role" ? "staff" : "", newAuthToken: () => "local-token" };
const app = { logger: () => ({ warn: (...args) => logs.push(args) }) };
stub(config, "polaris", source => { assert.strictEqual(source, app); return polarisConfig; });
stub(config, "enabledLibraryOrgIds", () => enabled);
stub(records, "hasStaffUsers", () => true);
stub(records, "findStaffByIdentity", (source, key) => {
  assert.strictEqual(key, "westervillelibrary.org\\rsomerfeldt");
  return exists ? record : null;
});
stub(records, "upsertStaffUser", (...args) => { saved.push(args); return record; });
stub(polaris, "staffAuth", (...args) => {
  authCalls.push(args);
  if (authResult instanceof Error) throw authResult;
  return authResult;
});
stub(orgs, "resolveParentLibrary", (source, branch) => { assert.strictEqual(branch, "3"); return scope; });
stub(routeUtils, "body", e => e.body);
stub(publicJson, "staffPublicJson", () => ({ id: "staff-id" }));
const { staffLogin } = require("../lib/staff/auth_routes.js");
function login() {
  return staffLogin({ app, body: { username: "WESTERVILLELIBRARY.ORG\\rsomerfeldt", password: "test-password" }, json: (status, body) => ({ status, body }) });
}

try {
  assert.strictEqual(login().status, 200);
  assert.deepStrictEqual(authCalls[0], ["rsomerfeldt", "test-password", polarisConfig, "WESTERVILLELIBRARY.ORG"]);
  assert.strictEqual(saved.length, 1);
  authCalls = [];
  saved = [];
  exists = false;
  assert.strictEqual(login().status, 401);
  exists = true;
  active = false;
  assert.strictEqual(login().status, 401);
  assert.strictEqual(authCalls.length, 0, "Unprovisioned or inactive users must not authenticate");
  active = true;
  authResult = new Error("upstream failure containing test-password");
  assert.strictEqual(login().status, 502);
  authResult.papiErrorCode = -8001;
  const denied = login();
  assert.strictEqual(denied.status, 403);
  assert.strictEqual(denied.body.papiErrorCode, -8001);
  assert.match(denied.body.message, /Polaris user is not permitted/);
  authResult = null;
  assert.strictEqual(login().status, 502);
  authResult = {};
  assert.strictEqual(login().status, 502);
  authResult = { AccessToken: "test-token", BranchID: 3 };
  scope = null;
  assert.strictEqual(login().status, 403);
  scope = { branchOrgId: "3", libraryOrgId: "2" };
  enabled = "4";
  assert.strictEqual(login().status, 403);
  assert.strictEqual(saved.length, 0, "Failed logins must not update or create staff accounts");
  assert.ok(!JSON.stringify(logs).includes("test-password"));
  assert.ok(!JSON.stringify(logs).includes("test-token"));
  console.log("staff_login.test.js passed");
} finally {
  originals.reverse().forEach(([target, name, original]) => { target[name] = original; });
}
