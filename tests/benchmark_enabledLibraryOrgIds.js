const { performance } = require('perf_hooks');

let findRecordByIdCalls = 0;
let findRecordsByFilterCalls = 0;

const mockApp = {
  findRecordById: function(collection, id) {
    findRecordByIdCalls++;
    for(let i=0; i<50000; i++) {} // Simulate DB overhead
    return {
      get: function(k) { return k === "organizationId" ? id + "_org" : null; }
    };
  },
  findRecordsByFilter: function(collection, filter, sort, limit, offset, params) {
    findRecordsByFilterCalls++;
    for(let i=0; i<50000; i++) {} // Simulate DB overhead
    const results = [];
    for (let key in params) {
      const id = params[key];
      results.push({
        get: function(k) { return k === "organizationId" ? id + "_org" : null; }
      });
    }
    return results;
  }
};

const mockSys = {
  get: function(key) {
    if (key === "enabledLibraries") {
      const ids = [];
      for(let i=0; i<300; i++) ids.push(i.toString());
      return ids;
    }
    return null;
  }
};

function unoptimized(app) {
  var sys = mockSys;
  if (!sys) return "";
  var ids = [];
  var rels = sys.get("enabledLibraries") || [];
  if (!Array.isArray(rels)) rels = rels ? [rels] : [];
  rels.forEach(function (id) {
    try {
      var org = app.findRecordById("polaris_organizations", id);
      if (org.get("organizationId")) ids.push(String(org.get("organizationId")));
    } catch (err) {}
  });
  return ids.join(",");
}

function optimized(app) {
  var sys = mockSys;
  if (!sys) return "";
  var ids = [];
  var rels = sys.get("enabledLibraries") || [];
  if (!Array.isArray(rels)) rels = rels ? [rels] : [];
  if (rels.length === 0) return "";

  var chunkLimit = 100;
  for (var i = 0; i < rels.length; i += chunkLimit) {
    var chunk = rels.slice(i, i + chunkLimit);
    var filterParts = [];
    var params = {};
    for (var j = 0; j < chunk.length; j++) {
      var key = "p" + j;
      filterParts.push("id = {:" + key + "}");
      params[key] = chunk[j];
    }
    var filter = filterParts.join(" || ");
    try {
      var records = app.findRecordsByFilter("polaris_organizations", filter, "", chunk.length, 0, params);
      for (var k = 0; k < records.length; k++) {
        var org = records[k];
        if (org.get("organizationId")) ids.push(String(org.get("organizationId")));
      }
    } catch (err) {}
  }

  return ids.join(",");
}

console.log("Running N+1 Query Performance Benchmark for enabledLibraryOrgIds...");
const start1 = performance.now();
unoptimized(mockApp);
const end1 = performance.now();
const time1 = end1 - start1;

const start2 = performance.now();
optimized(mockApp);
const end2 = performance.now();
const time2 = end2 - start2;

console.log(`Unoptimized: ${time1.toFixed(2)} ms, ${findRecordByIdCalls} findRecordById calls`);
console.log(`Optimized: ${time2.toFixed(2)} ms, ${findRecordsByFilterCalls} findRecordsByFilter calls`);
console.log(`Improvement: ${(time1 / time2).toFixed(2)}x faster`);
