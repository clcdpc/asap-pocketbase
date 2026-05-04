const assert = require('assert');
const path = require('path');
const { performance } = require('perf_hooks');

global.__hooks = path.resolve(__dirname, '../pb_hooks');
const orgs = require('../lib/orgs.js');

// Mock data
const mockParents = {};
for (let i = 1; i <= 10; i++) {
  mockParents[`p${i}`] = { id: `id_p${i}`, organizationId: `p${i}` };
}

const numRows = 2000;
let dbQueryCount = 0;

function createMockApp() {
  dbQueryCount = 0;
  return {
    findRecordsByFilter: function(collection, filter, sort, limit, offset) {
      if (collection === "polaris_organizations" && filter === "parentOrganizationId != ''") {
        if (offset >= numRows) return [];

        const rows = [];
        const itemsToReturn = Math.min(limit, numRows - offset);
        for (let i = 0; i < itemsToReturn; i++) {
          const parentId = `p${(i % 10) + 1}`;
          const r = {
            _data: { parentOrganizationId: parentId },
            get: function(key) { return this._data[key]; },
            set: function(key, val) { this._data[key] = val; }
          };
          rows.push(r);
        }
        return rows;
      }
      return [];
    },
    findFirstRecordByData: function(collection, field, value) {
      dbQueryCount++;
      if (collection === "polaris_organizations" && field === "organizationId") {
        const p = mockParents[value];
        if (p) {
          return {
             id: p.id,
             get: function(key) { return p[key]; }
          };
        }
        throw new Error("Not found");
      }
      throw new Error("Not found");
    },
    findRecordById: function(collection, id) {
        dbQueryCount++;
        return { set: function() {}, id: id};
    },
    save: function(record) {
      // Mock save
    }
  };
}

const app = createMockApp();

const start = performance.now();
orgs.syncOrganizations = function() { return { synced: 0 } }; // override sync to test relink Parents only. Actually we can just call relinkParents directly if it was exported, but we'll mock and replace the function for testing.

// Since relinkParents is NOT exported, we need to read it and eval it
const fs = require('fs');
const orgsCode = fs.readFileSync('./lib/orgs.js', 'utf8');

// replace findOrganization mock count tracking inside the eval scope
const testContext = {
    app: app,
    normalizeOrgId: orgs.normalizeOrgId,
    dbQueryCount: 0
};

const codeToEval = orgsCode + `
  app.dbQueryCount = 0;
  const start = performance.now();
  relinkParents(app);
  const end = performance.now();
  console.log("DB Queries: " + dbQueryCount);
  console.log("Time: " + (end - start) + "ms");
`;

eval(codeToEval);
