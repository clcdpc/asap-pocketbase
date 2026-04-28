const { performance } = require('perf_hooks');

let searchBibCalls = 0;
const polaris = {
  searchBib: function(staff, identifier) {
    searchBibCalls++;
    let sum = 0;
    for(let i=0; i<50000; i++) sum += i; // Simulate external API call overhead
    return "BIB" + identifier;
  }
};

// Create records with duplicate identifiers
const records = [];
for (let i = 0; i < 100; i++) {
  records.push({
    get: function(key) {
      if (key === "bibid") return "";
      if (key === "identifier") return "ISBN-" + (i % 5).toString();
      return null;
    }
  });
}

function unoptimized() {
  searchBibCalls = 0;
  const start = performance.now();
  for (var i = 0; i < records.length; i++) {
    var record = records[i];
    var bibId = String(record.get("bibid") || "").trim();
    if (!bibId) {
      bibId = polaris.searchBib(null, record.get("identifier"));
    }
  }
  const end = performance.now();
  return { time: end - start, calls: searchBibCalls };
}

function optimized() {
  searchBibCalls = 0;
  const start = performance.now();
  var bibCache = {};
  for (var i = 0; i < records.length; i++) {
    var record = records[i];
    var bibId = String(record.get("bibid") || "").trim();
    if (!bibId) {
      var identifier = String(record.get("identifier") || "").trim();
      if (bibCache[identifier] === undefined) {
        bibCache[identifier] = polaris.searchBib(null, identifier);
      }
      bibId = bibCache[identifier];
    }
  }
  const end = performance.now();
  return { time: end - start, calls: searchBibCalls };
}

console.log("Running N+1 Query Performance Benchmark for searchBib...");
const before = unoptimized();
console.log(`Unoptimized: ${before.time.toFixed(2)} ms, ${before.calls} API calls`);

const after = optimized();
console.log(`Optimized: ${after.time.toFixed(2)} ms, ${after.calls} API calls`);

console.log(`Improvement: ${(before.time / after.time).toFixed(2)}x faster`);
