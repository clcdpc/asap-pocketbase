const { performance } = require('perf_hooks');

let searchBibCalls = 0;
const polaris = {
  searchBib: function(staff, identifier) {
    searchBibCalls++;
    let sum = 0;
    for(let i=0; i<50000; i++) sum += i; // Simulate API call overhead
    return "BIB_" + identifier;
  }
};

const records = [];
// 100 items but only 10 unique identifiers
for (let i = 0; i < 100; i++) {
  records.push({
    get: function(key) { return key === "identifier" ? "ISBN_" + (i % 10) : null; },
    set: function() {}
  });
}

function unoptimized() {
  searchBibCalls = 0;
  const start = performance.now();
  for (var i = 0; i < records.length; i++) {
    var record = records[i];
    var identifier = record.get("identifier");

    if (!identifier) continue;

    var bibId = polaris.searchBib("staff", identifier);
    if (bibId) {
      record.set("bibid", bibId);
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
    var identifier = record.get("identifier");

    if (!identifier) continue;

    if (bibCache[identifier] === undefined) {
      bibCache[identifier] = polaris.searchBib("staff", identifier);
    }
    var bibId = bibCache[identifier];

    if (bibId) {
      record.set("bibid", bibId);
    }
  }
  const end = performance.now();
  return { time: end - start, calls: searchBibCalls };
}

console.log("Running N+1 Query searchBib Benchmark...");
const before = unoptimized();
console.log(`Unoptimized: ${before.time.toFixed(2)} ms, ${before.calls} API calls`);

const after = optimized();
console.log(`Optimized: ${after.time.toFixed(2)} ms, ${after.calls} API calls`);

console.log(`Improvement: ${(before.time / after.time).toFixed(2)}x faster`);
