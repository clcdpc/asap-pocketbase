const assert = require("assert");
const fs = require("fs");

const migration = fs.readFileSync("pb_migrations/202606080001_library_additional_fields.js", "utf8");
const initial = fs.readFileSync("pb_migrations/0000000000_initial.js", "utf8");
const patronOverridesMigration = fs.readFileSync("pb_migrations/202605010002_patron_settings_overrides.js", "utf8");

assert(migration.includes('app.findCollectionByNameOrId("patron_settings_overrides")'));
assert(migration.includes('field("additionalFieldDefinitions", "json")'));
assert(migration.includes('app.findCollectionByNameOrId("title_requests")'));
assert(migration.includes('field("customFields", "json")'));
assert(initial.includes('field("customFields", "json")'));
assert(patronOverridesMigration.includes('field("additionalFieldDefinitions", "json")'));
["select ", "update ", "alter " + "table"].forEach((forbidden) => {
  assert(!migration.toLowerCase().includes(forbidden));
});

console.log("custom field schema tests passed");
