const assert = require("assert");

const defaults = require("../lib/config/defaults.js");

console.log("Running email template default tests...");

const templates = defaults.defaultEmailTemplates();

assert.ok(templates.purchase_approved, "purchase_approved default template should exist");
assert.strictEqual(templates.purchase_approved.subject, "Purchase approved: {{title}}");
assert.ok(
  templates.purchase_approved.body.includes("awaiting ordering and cataloging"),
  "purchase_approved body should explain ordering and cataloging"
);
assert.ok(
  !templates.hold_placed.body.includes("approved your suggestion for purchase"),
  "hold_placed default should not describe purchase approval"
);

console.log("All email template default tests passed!");
