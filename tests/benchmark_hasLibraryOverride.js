const assert = require("assert");
const path = require("path");
const { performance } = require("perf_hooks");

global.__hooks = path.resolve(__dirname, "../pb_hooks");
global.Record = function Record() {
  return {};
};
const settingsRoutes = require("../lib/staff/settings_routes.js");

let dbCalls = 0;

const mockApp = {
    findFirstRecordByFilter: function(collection, filter, params) {
        dbCalls++;
        // simulate some DB time
        let start = performance.now();
        while(performance.now() - start < 0.1) {}

        throw new Error("Not found");
    },
    findRecordsByFilter: function(collection, filter, sort, limit, offset, params) {
        dbCalls++;
        // simulate some DB time
        let start = performance.now();
        while(performance.now() - start < 0.1) {}

        return [];
    }
};

const mockConfig = require("../lib/config.js");
// override findOrganization
mockConfig.findOrganization = function(app, orgId) {
    return { id: orgId };
};

const ITERATIONS = 100;
console.log("Measuring baseline for hasLibraryOverride (with exceptions)...");

dbCalls = 0;
const start1 = performance.now();
for (let i = 0; i < ITERATIONS; i++) {
    settingsRoutes.hasLibraryOverride(mockApp, "org-1");
}
const end1 = performance.now();

console.log(`Baseline Execution Time: ${(end1 - start1).toFixed(2)}ms`);
console.log(`Total DB Calls: ${dbCalls}`);

// Now create an optimized version inline to test
function optimizedHasLibraryOverride(app, orgId) {
  var org = mockConfig.findOrganization(app, orgId);
  if (!org) return false;
  var filters = [
    ["workflow_settings", "scope = 'library' && libraryOrganization = {:org}"],
    ["ui_settings", "scope = 'library' && libraryOrganization = {:org}"],
    ["email_templates", "scope = 'library' && libraryOrganization = {:org}"],
    ["rejection_templates", "scope = 'library' && libraryOrganization = {:org}"],
    ["material_formats", "scope = 'library' && libraryOrganization = {:org}"],
    ["patron_settings_overrides", "orgId = {:orgId}"],
    ["patron_library_settings", "libraryOrganization = {:org}"]
  ];
  for (var i = 0; i < filters.length; i++) {
    var records = app.findRecordsByFilter(filters[i][0], filters[i][1], "", 1, 0, { org: org.id, orgId: String(orgId || "").trim() });
    if (records && records.length > 0) {
      return true;
    }
  }
  return false;
}

console.log("\nMeasuring optimized version (without exceptions)...");

dbCalls = 0;
const start2 = performance.now();
for (let i = 0; i < ITERATIONS; i++) {
    optimizedHasLibraryOverride(mockApp, "org-1");
}
const end2 = performance.now();

console.log(`Optimized Execution Time: ${(end2 - start2).toFixed(2)}ms`);
console.log(`Total DB Calls: ${dbCalls}`);
