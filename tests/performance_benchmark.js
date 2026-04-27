const { performance } = require('perf_hooks');

let configCalls = 0;
const config = {
  outstandingTimeout: function(app, orgId) {
    configCalls++;
    let sum = 0;
    for(let i=0; i<50000; i++) sum += i; // Simulate DB query overhead
    return { enabled: true, days: 30 };
  }
};

const records = [];
for (let i = 0; i < 2000; i++) {
  records.push({
    get: function(key) { return key === "libraryOrgId" ? (i % 5).toString() : null; }
  });
}

function unoptimized() {
  configCalls = 0;
  const start = performance.now();
  for (var i = 0; i < records.length; i++) {
    var record = records[i];
    var orgId = record.get("libraryOrgId");

    // Simulate original unoptimized code
    var cfg = config.outstandingTimeout(null, orgId);
    if (!cfg.enabled) continue;
  }
  const end = performance.now();
  return { time: end - start, calls: configCalls };
}

function optimized() {
  configCalls = 0;
  const start = performance.now();
  var cfgCache = {};
  for (var i = 0; i < records.length; i++) {
    var record = records[i];
    var orgId = record.get("libraryOrgId");

    // Simulate current optimized code in jobs.js
    if (cfgCache[orgId] === undefined) {
      cfgCache[orgId] = config.outstandingTimeout(null, orgId);
    }
    var cfg = cfgCache[orgId];
    if (!cfg.enabled) continue;
  }
  const end = performance.now();
  return { time: end - start, calls: configCalls };
}

console.log("Running N+1 Query Performance Benchmark...");
const before = unoptimized();
console.log(`Unoptimized: ${before.time.toFixed(2)} ms, ${before.calls} config calls`);

const after = optimized();
console.log(`Optimized: ${after.time.toFixed(2)} ms, ${after.calls} config calls`);

console.log(`Improvement: ${(before.time / after.time).toFixed(2)}x faster`);
