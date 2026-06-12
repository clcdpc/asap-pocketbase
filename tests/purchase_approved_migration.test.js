const assert = require("assert");
const fs = require("fs");
const path = require("path");

const migrationPath = path.join(__dirname, "../pb_migrations/202606120001_purchase_approved_email_template.js");
const initial = fs.readFileSync(path.join(__dirname, "../pb_migrations/0000000000_initial.js"), "utf8");
const uiText = fs.readFileSync(path.join(__dirname, "../lib/config/ui_text.js"), "utf8");
const settings = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/settings.js"), "utf8");

console.log("Running purchase approved migration tests...");

assert.ok(fs.existsSync(migrationPath), "migration file should exist");
const migration = fs.readFileSync(migrationPath, "utf8");

assert.ok(migration.includes("purchase_approved"), "migration should create purchase_approved template");
assert.ok(migration.includes("OLD_HOLD_PLACED_BODY"), "migration should guard hold wording updates by exact old default");
assert.ok(migration.includes("OLD_SUGGESTION_FORM_NOTE"), "migration should guard submission note updates by exact old default");
assert.ok(migration.includes("awaiting ordering and cataloging"), "migration should include ordering and cataloging wording");

assert.ok(initial.includes('templateKey: "purchase_approved"'), "initial migration should seed purchase_approved");
assert.ok(initial.includes("awaiting ordering and cataloging"), "initial migration should seed updated wording");
assert.ok(
  !initial.includes("Good news. The library plans to add {{title}}"),
  "initial hold placed seed should not include purchase-decision wording"
);

assert.ok(uiText.includes("awaiting ordering and cataloging"), "runtime UI text fallback should use updated submission note");
assert.ok(settings.includes("awaiting ordering and cataloging"), "settings fallback should use updated submission note");

console.log("All purchase approved migration tests passed!");
