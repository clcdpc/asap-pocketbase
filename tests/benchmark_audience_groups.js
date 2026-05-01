const { performance } = require('perf_hooks');

let queryCalls = 0;
const app = {
  findRecordsByFilter: function(col, filter, sort, limit, offset, params) {
    queryCalls++;
    if (col === "audience_groups") {
      let rows = [];
      for (let i = 0; i < 200; i++) {
        rows.push({ id: `ag_${i}`, get: function(key) { return key === "code" ? `group_${i}` : `Label ${i}`; } });
      }
      return rows;
    }
    return [];
  },
  findFirstRecordByFilter: function(col, filter, params) {
    queryCalls++;
    let sum = 0;
    for(let i=0; i<10000; i++) sum += i; // simulate overhead
    // simulate no match for worst case full iteration
    throw new Error("No record found");
  }
};

const keep = {}; // keeping none

function unoptimized() {
  queryCalls = 0;
  const start = performance.now();
  try {
    var rows = app.findRecordsByFilter("audience_groups", "filter", "", 200, 0, {});
    for (var i = 0; i < rows.length; i++) {
      var row = rows[i];
      if (!keep[String(row.get("code") || "")]) {
        try {
          app.findFirstRecordByFilter("title_requests", "audienceGroup = {:id}", { id: row.id });
          var err = new Error("Age group '" + row.get("label") + "' is currently in use by existing requests and cannot be deleted. You can disable it instead.");
          err.code = 400;
          throw err;
        } catch (findErr) {
          if (findErr.message && findErr.message.indexOf("in use") >= 0) {
            throw findErr;
          }
        }
      }
    }
  } catch (err) {
    if (err.message && err.message.indexOf("in use") >= 0) {
      throw err;
    }
  }
  const end = performance.now();
  return { time: end - start, calls: queryCalls };
}

function optimized() {
  queryCalls = 0;
  const start = performance.now();
  try {
    var rows = app.findRecordsByFilter("audience_groups", "filter", "", 200, 0, {});
    var toCheck = [];
    for (var i = 0; i < rows.length; i++) {
      var row = rows[i];
      if (!keep[String(row.get("code") || "")]) {
        toCheck.push(row);
      }
    }

    if (toCheck.length > 0) {
      var batchSize = 100;
      for (var j = 0; j < toCheck.length; j += batchSize) {
        var chunk = toCheck.slice(j, j + batchSize);
        var filterParts = [];
        var checkParams = {};
        for (var k = 0; k < chunk.length; k++) {
          filterParts.push("audienceGroup = {:p" + k + "}");
          checkParams["p" + k] = chunk[k].id;
        }
        var batchFilter = filterParts.join(" || ");
        try {
          var usedRequest = app.findFirstRecordByFilter("title_requests", batchFilter, checkParams);
          if (usedRequest) {
            var usedGroupId = usedRequest.get("audienceGroup");
            var usedLabel = "";
            for (var k = 0; k < chunk.length; k++) {
              if (chunk[k].id === usedGroupId) {
                usedLabel = chunk[k].get("label");
                break;
              }
            }
            var err = new Error("Age group '" + usedLabel + "' is currently in use by existing requests and cannot be deleted. You can disable it instead.");
            err.code = 400;
            throw err;
          }
        } catch (findErr) {
          if (findErr.message && findErr.message.indexOf("in use") >= 0) {
            throw findErr;
          }
        }
      }
    }
  } catch (err) {
    if (err.message && err.message.indexOf("in use") >= 0) {
      throw err;
    }
  }
  const end = performance.now();
  return { time: end - start, calls: queryCalls };
}

console.log("Running Audience Groups N+1 Query Benchmark...");
const before = unoptimized();
console.log(`Unoptimized: ${before.time.toFixed(2)} ms, ${before.calls} query calls`);

const after = optimized();
console.log(`Optimized: ${after.time.toFixed(2)} ms, ${after.calls} query calls`);

console.log(`Improvement: ${(before.time / after.time).toFixed(2)}x faster`);
