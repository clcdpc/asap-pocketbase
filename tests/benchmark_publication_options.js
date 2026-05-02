const { performance } = require('perf_hooks');

let queryCalls = 0;
const app = {
  findFirstRecordByFilter: function(col, filter, params) {
    queryCalls++;
    let sum = 0;
    for(let i=0; i<10000; i++) sum += i; // simulate overhead
    // simulate no match
    throw new Error("No record found");
  }
};

const oldOptions = [];
for (let i = 0; i < 200; i++) {
  oldOptions.push({ label: `Option ${i}` });
}
const keep = {};

function unoptimized() {
  queryCalls = 0;
  const start = performance.now();
  try {
    oldOptions.forEach(function (opt) {
      var optLabel = String(opt && typeof opt === "object" ? opt.label || "" : opt || "").trim();
      if (!optLabel) return;
      if (!keep[optLabel.toLowerCase()]) {
        try {
          app.findFirstRecordByFilter("title_requests", "publication = {:label}", { label: optLabel });
          var err = new Error("Publication timing '" + optLabel + "' is currently in use by existing requests and cannot be deleted. You can disable it instead.");
          err.code = 400;
          throw err;
        } catch (findErr) {
          if (findErr.message && findErr.message.indexOf("in use") >= 0) {
            throw findErr;
          }
        }
      }
    });
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
    var toCheck = [];
    oldOptions.forEach(function (opt) {
      var optLabel = String(opt && typeof opt === "object" ? opt.label || "" : opt || "").trim();
      if (!optLabel) return;
      if (!keep[optLabel.toLowerCase()]) {
        toCheck.push(optLabel);
      }
    });

    if (toCheck.length > 0) {
      var batchSize = 100;
      for (var j = 0; j < toCheck.length; j += batchSize) {
        var chunk = toCheck.slice(j, j + batchSize);
        var filterParts = [];
        var checkParams = {};
        for (var k = 0; k < chunk.length; k++) {
          filterParts.push("publication = {:p" + k + "}");
          checkParams["p" + k] = chunk[k];
        }
        var batchFilter = filterParts.join(" || ");
        try {
          var usedRequest = app.findFirstRecordByFilter("title_requests", batchFilter, checkParams);
          if (usedRequest) {
            var usedLabel = usedRequest.get("publication");
            // Find exact case of the original option, or just use the DB value
            var err = new Error("Publication timing '" + usedLabel + "' is currently in use by existing requests and cannot be deleted. You can disable it instead.");
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

console.log("Running Publication Options N+1 Query Benchmark...");
const before = unoptimized();
console.log(`Unoptimized: ${before.time.toFixed(2)} ms, ${before.calls} query calls`);

const after = optimized();
console.log(`Optimized: ${after.time.toFixed(2)} ms, ${after.calls} query calls`);

console.log(`Improvement: ${(before.time / after.time).toFixed(2)}x faster`);
