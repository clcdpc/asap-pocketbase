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
    findRecordsByFilter: function(collection, filter, sort, limit, offset, params) {
        dbCalls++;
        // simulate some DB time
        let start = performance.now();
        while(performance.now() - start < 0.1) {}

        return [];
    },
    db: function() {
        return {
            newQuery: function(sql) {
                return {
                    bind: function(params) {},
                    all: function(result) {
                        dbCalls++;
                        let start = performance.now();
                        // Assume a slightly longer DB time for the single query
                        while(performance.now() - start < 0.2) {}
                        return;
                    }
                };
            }
        };
    }
};

const mockConfig = require("../lib/config.js");
// override findOrganization
mockConfig.findOrganization = function(app, orgId) {
    return { id: orgId };
};

const ITERATIONS = 100;
console.log("Measuring db query version...");

dbCalls = 0;
const start1 = performance.now();
for (let i = 0; i < ITERATIONS; i++) {
    settingsRoutes.hasLibraryOverride(mockApp, "org-1");
}
const end1 = performance.now();

console.log(`Execution Time: ${(end1 - start1).toFixed(2)}ms`);
console.log(`Total DB Calls: ${dbCalls}`);
