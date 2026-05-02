const assert = require('assert');
const path = require('path');
const { performance } = require('perf_hooks');

global.__hooks = path.resolve(__dirname, '../pb_hooks');
const orgs = require('../pb_hooks/lib/orgs.js');

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
    findRecordsByFilter: function(collection, filter, sort, limit, offset, params) {
      dbQueryCount++;
      if (collection === "polaris_organizations") {
        if (filter.includes("parentOrganizationId != ''")) {
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
        } else {
            // Mock batch fetch of parents
            // e.g. organizationId = {:p0} || organizationId = {:p1}
            const rows = [];
            if (params) {
                for (const key in params) {
                    const p = mockParents[params[key]];
                    if (p) {
                        rows.push({
                            id: p.id,
                            get: function(k) { return p[k]; }
                        });
                    }
                }
            }
            return rows;
        }
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
    save: function(record) {
      // Mock save
    }
  };
}

const app = createMockApp();

const fs = require('fs');
let orgsCode = fs.readFileSync('./pb_hooks/lib/orgs.js', 'utf8');

orgsCode = orgsCode.replace(
`function relinkParents(app) {
  var offset = 0;
  while (true) {
    var rows = app.findRecordsByFilter("polaris_organizations", "parentOrganizationId != ''", "", 200, offset);
    if (!rows.length) break;
    for (var i = 0; i < rows.length; i++) {
      var parent = findOrganization(app, rows[i].get("parentOrganizationId"));
      rows[i].set("parentOrganization", parent ? parent.id : "");
      app.save(rows[i]);
    }
    if (rows.length < 200) break;
    offset += 200;
  }
}`,
`function relinkParents(app) {
  var offset = 0;
  while (true) {
    var rows = app.findRecordsByFilter("polaris_organizations", "parentOrganizationId != ''", "", 200, offset);
    if (!rows.length) break;

    // Bulk fetch parents for this batch
    var parentIds = {};
    for (var i = 0; i < rows.length; i++) {
      var pid = normalizeOrgId(rows[i].get("parentOrganizationId"));
      if (pid) {
        parentIds[pid] = true;
      }
    }

    var parentCache = {};
    var uniqueParentIds = Object.keys(parentIds);
    if (uniqueParentIds.length > 0) {
      // Chunk the requests into batches of 100 records as per memory guidelines
      var chunkSize = 100;
      for (var i = 0; i < uniqueParentIds.length; i += chunkSize) {
        var chunk = uniqueParentIds.slice(i, i + chunkSize);
        var filterParts = [];
        var params = {};
        for (var j = 0; j < chunk.length; j++) {
          filterParts.push("organizationId = {:p" + j + "}");
          params["p" + j] = chunk[j];
        }

        try {
          var parents = app.findRecordsByFilter("polaris_organizations", filterParts.join(" || "), "", chunk.length, 0, params);
          for (var j = 0; j < parents.length; j++) {
            parentCache[parents[j].get("organizationId")] = parents[j].id;
          }
        } catch (err) {}
      }
    }

    for (var i = 0; i < rows.length; i++) {
      var pid = normalizeOrgId(rows[i].get("parentOrganizationId"));
      rows[i].set("parentOrganization", parentCache[pid] || "");
      app.save(rows[i]);
    }

    if (rows.length < 200) break;
    offset += 200;
  }
}`);

const codeToEval = orgsCode + `
  app.dbQueryCount = 0;
  const start = performance.now();
  relinkParents(app);
  const end = performance.now();
  console.log("DB Queries: " + dbQueryCount);
  console.log("Time: " + (end - start) + "ms");
`;

eval(codeToEval);
