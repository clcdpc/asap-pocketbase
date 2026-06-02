const assert = require("assert");
const path = require("path");
const { performance } = require("perf_hooks");

let mockApp = {
    findFirstRecordByFilter: function(collection, filter, params) {
        throw new Error("not found");
    },
    findRecordsByFilter: function(collection, filter, sort, limit, offset, params) {
        return [];
    }
};

const org = { id: "org1" };
const orgIdStr = "org1";

const filters = [
    ["workflow_settings", "scope = 'library' && libraryOrganization = {:org}"],
    ["ui_settings", "scope = 'library' && libraryOrganization = {:org}"],
    ["email_templates", "scope = 'library' && libraryOrganization = {:org}"],
    ["rejection_templates", "scope = 'library' && libraryOrganization = {:org}"],
    ["material_formats", "scope = 'library' && libraryOrganization = {:org}"],
    ["patron_settings_overrides", "orgId = {:orgId}"],
    ["patron_library_settings", "libraryOrganization = {:org}"]
];

const start1 = performance.now();
for (let i = 0; i < 10000; i++) {
    for (let j = 0; j < filters.length; j++) {
        try {
            mockApp.findFirstRecordByFilter(filters[j][0], filters[j][1], { org: org.id, orgId: orgIdStr });
        } catch (e) {}
    }
}
const end1 = performance.now();

const start2 = performance.now();
for (let i = 0; i < 10000; i++) {
    for (let j = 0; j < filters.length; j++) {
        let rows = mockApp.findRecordsByFilter(filters[j][0], filters[j][1], "", 1, 0, { org: org.id, orgId: orgIdStr });
        if (rows.length > 0) {}
    }
}
const end2 = performance.now();

console.log(`findFirst (with catch): ${(end1 - start1).toFixed(2)}ms`);
console.log(`findRecords (no catch): ${(end2 - start2).toFixed(2)}ms`);
