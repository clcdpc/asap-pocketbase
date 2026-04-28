const { performance } = require('perf_hooks');

let polarisCalls = 0;
const polaris = {
  checkPatronCheckouts: function(staff, barcode) {
    polarisCalls++;
    let sum = 0;
    for(let i=0; i<50000; i++) sum += i; // Simulate external API call overhead
    return [{ BibID: "123" }, { BibID: "456" }];
  }
};

const records = [];
for (let i = 0; i < 2000; i++) {
  records.push({
    get: function(key) { return key === "barcode" ? (i % 5).toString() : "123"; }
  });
}

function unoptimized() {
  polarisCalls = 0;
  const start = performance.now();
  for (var i = 0; i < records.length; i++) {
    var record = records[i];
    var barcode = record.get("barcode");

    // Simulate original unoptimized code
    var checkouts = polaris.checkPatronCheckouts(null, barcode);
    var bibId = String(record.get("bibid") || "");
  }
  const end = performance.now();
  return { time: end - start, calls: polarisCalls };
}

function optimized() {
  polarisCalls = 0;
  const start = performance.now();
  var checkoutsCache = {};
  for (var i = 0; i < records.length; i++) {
    var record = records[i];
    var barcode = record.get("barcode");

    // Simulate current optimized code in jobs.js
    if (checkoutsCache[barcode] === undefined) {
      checkoutsCache[barcode] = polaris.checkPatronCheckouts(null, barcode);
    }
    var checkouts = checkoutsCache[barcode];
    var bibId = String(record.get("bibid") || "");
  }
  const end = performance.now();
  return { time: end - start, calls: polarisCalls };
}

console.log("Running N+1 Query Performance Benchmark for checkPatronCheckouts...");
const before = unoptimized();
console.log(`Unoptimized: ${before.time.toFixed(2)} ms, ${before.calls} API calls`);

const after = optimized();
console.log(`Optimized: ${after.time.toFixed(2)} ms, ${after.calls} API calls`);

console.log(`Improvement: ${(before.time / after.time).toFixed(2)}x faster`);
