const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";

const formatClaimRules = require("../lib/format_claim_rules.js");

class MockRecord {
  constructor(collectionName, data) {
    this.collectionName = collectionName;
    this.data = Object.assign({}, data || {});
    this.id = this.data.id || collectionName + "_id";
  }
  get(key) {
    return this.data[key];
  }
  set(key, value) {
    this.data[key] = value;
  }
  getBool(key) {
    return !!this.data[key];
  }
}

function request(data) {
  return new MockRecord("title_requests", Object.assign({
    id: "req1",
    libraryOrgId: "10",
    format: "dvd"
  }, data || {}));
}

function staff(data) {
  return new MockRecord("staff_users", Object.assign({
    id: "staff1",
    username: "jane",
    displayName: "Jane",
    libraryOrgId: "10",
    role: "staff",
    active: true
  }, data || {}));
}

function rule(data) {
  return new MockRecord("format_claim_rules", Object.assign({
    id: "rule1",
    libraryOrgId: "10",
    format: "dvd",
    staffUserId: "staff1",
    active: true
  }, data || {}));
}

function appWith(options) {
  options = options || {};
  const saved = [];
  return {
    saved,
    findFirstRecordByFilter(collection, filter, params) {
      if (collection === "format_claim_rules" && options.rule && params.libraryOrgId === options.rule.get("libraryOrgId") && params.format === options.rule.get("format")) {
        return options.rule;
      }
      throw new Error("not found");
    },
    findRecordById(collection, id) {
      if (collection === "staff_users" && options.staff && id === options.staff.id) return options.staff;
      if (collection === "title_requests" && options.request && id === options.request.id) return options.request;
      throw new Error("not found");
    },
    findCollectionByNameOrId() {
      throw new Error("events not available in unit test");
    },
    save(record) {
      saved.push(record);
    },
    logger() {
      return { warn() {}, error() {} };
    }
  };
}

{
  const req = request();
  const jane = staff();
  const app = appWith({ request: req, staff: jane, rule: rule() });
  const result = formatClaimRules.applyFormatClaimRule(app, req, { trigger: "submission" });
  assert.strictEqual(result.action, "assigned");
  assert.strictEqual(req.get("claimedByStaffUserId"), "staff1");
  assert.strictEqual(req.get("claimedByDisplayName"), "Jane");
  assert.strictEqual(req.get("claimType"), "automatic_format_rule");
  assert.strictEqual(req.get("claimRuleId"), "rule1");
}

{
  const req = request({ claimedByStaffUserId: "manual1", claimedByDisplayName: "Manual Mary", claimType: "manual" });
  const jane = staff();
  const app = appWith({ request: req, staff: jane, rule: rule() });
  const result = formatClaimRules.applyFormatClaimRule(app, req, { trigger: "format_changed" });
  assert.strictEqual(result.changed, false);
  assert.strictEqual(result.reason, "manual_claim");
  assert.strictEqual(req.get("claimedByStaffUserId"), "manual1");
  assert.strictEqual(req.get("claimType"), "manual");
}

{
  const req = request({ format: "book", claimedByStaffUserId: "staff1", claimedByDisplayName: "Jane", claimType: "automatic_format_rule", claimRuleId: "rule1" });
  const app = appWith({ request: req });
  const result = formatClaimRules.applyFormatClaimRule(app, req, { trigger: "format_changed", previousFormat: "dvd" });
  assert.strictEqual(result.action, "cleared");
  assert.strictEqual(req.get("claimedByStaffUserId"), "");
  assert.strictEqual(req.get("claimType"), "");
  assert.strictEqual(req.get("claimRuleId"), "");
}

{
  const req = request({ libraryOrgId: "20" });
  const jane = staff({ libraryOrgId: "10" });
  const app = appWith({ request: req, staff: jane, rule: rule({ libraryOrgId: "20" }) });
  const result = formatClaimRules.applyFormatClaimRule(app, req, { trigger: "submission" });
  assert.strictEqual(result.changed, false);
  assert.strictEqual(result.reason, "invalid_staff");
  assert.strictEqual(req.get("claimedByStaffUserId") || "", "");
}

console.log("Format auto-claim rule tests passed.");
