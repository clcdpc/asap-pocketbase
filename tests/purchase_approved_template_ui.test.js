const assert = require("assert");
const fs = require("fs");
const path = require("path");

const html = fs.readFileSync(path.join(__dirname, "../pb_public/staff/index.html"), "utf8");
const state = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/state.js"), "utf8");
const templates = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/settings-templates.js"), "utf8");
const settings = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/settings.js"), "utf8");

console.log("Running purchase approved template UI tests...");

assert.ok(html.includes("Purchase approved"), "settings accordion should label the purchase approved template");
assert.ok(html.includes('id="email-purchase-approved-subject"'), "settings HTML should include purchase approved subject input");
assert.ok(html.includes('id="email-purchase-approved-body"'), "settings HTML should include purchase approved body textarea");
assert.ok(
  html.indexOf("email-submit-subject") < html.indexOf("email-purchase-approved-subject") &&
    html.indexOf("email-purchase-approved-subject") < html.indexOf("email-owned-subject"),
  "purchase approved template should appear between submission confirmation and already owned"
);

assert.ok(state.includes("'email-purchase-approved-subject'"), "template field tracking should include purchase approved subject");
assert.ok(state.includes("'email-purchase-approved-body'"), "template field tracking should include purchase approved body");
assert.ok(state.includes("purchase_approved"), "state defaults should include purchase_approved");
assert.ok(state.includes("awaiting ordering and cataloging"), "state default body should explain ordering and cataloging");

assert.ok(templates.includes("emails.purchase_approved"), "settings population should read purchase_approved template");
assert.ok(templates.includes("email-purchase-approved-subject"), "settings population should write purchase approved subject");
assert.ok(templates.includes("id: 'purchase-approved'"), "settings summaries should include purchase approved");
assert.ok(
  templates.includes("target.id === 'email-purchase-approved-subject'"),
  "input listener should refresh summaries for purchase approved subject"
);

assert.ok(settings.includes("purchase_approved"), "settings payload should serialize purchase_approved");
assert.ok(settings.includes("email-purchase-approved-subject"), "settings payload should read purchase approved subject");
assert.ok(settings.includes("email-purchase-approved-body"), "settings payload should read purchase approved body");

console.log("All purchase approved template UI tests passed!");
