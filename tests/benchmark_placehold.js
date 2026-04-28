const { performance } = require('perf_hooks');

// Mock PocketBase app
global.$app = {
  findRecordById: function(collection, id) {
    // Simulate DB query delay
    let sum = 0;
    for(let i=0; i<10000; i++) sum += i;
    return {
      get: function(key) { return key === "polaris" ? {} : null; }
    };
  }
};

global.__hooks = require('path').resolve(__dirname, '../pb_hooks');

const crypto = require(`${__hooks}/lib/crypto.js`);
global.crypto = crypto;

const config = require(`${__hooks}/lib/config.js`);
const polaris = require(`${__hooks}/lib/polaris.js`);

let dbQueries = 0;
const originalFind = global.$app.findRecordById;
global.$app.findRecordById = function(collection, id) {
  dbQueries++;
  return originalFind(collection, id);
};

// Replace buildXml just to make the test work without XML failures
polaris._buildXmlOriginal = polaris.buildXml;
polaris.buildXml = function() { return "<test></test>"; }

// We mock endpoint to not actually execute a request, but we want to measure cfg() calls
const originalSend = polaris._sendOriginal || null; // it's hidden, let's just mock what we need

function runUnoptimized() {
  dbQueries = 0;
  const start = performance.now();
  for (let i = 0; i < 50; i++) {
     try {
       // We expect this to fail because we're not fully mocking the HTTP client, but it will trigger the N+1 configuration lookups before failing
       polaris.placeHold(null, "123", "456");
     } catch (e) {
       // Ignore HTTP errors or missing deps, we just want the config hits
     }
  }
  const end = performance.now();
  return { time: end - start, queries: dbQueries };
}

function runOptimized() {
  dbQueries = 0;
  const start = performance.now();
  const cachedConfig = config.polaris();
  for (let i = 0; i < 50; i++) {
     try {
       polaris.placeHold(null, "123", "456", cachedConfig);
     } catch (e) {
     }
  }
  const end = performance.now();
  return { time: end - start, queries: dbQueries };
}

console.log("Benchmarking config loading in placeHold loop...");
const before = runUnoptimized();
console.log(`Unoptimized: ${before.time.toFixed(2)} ms, ${before.queries} DB queries`);

const after = runOptimized();
console.log(`Optimized: ${after.time.toFixed(2)} ms, ${after.queries} DB queries`);
console.log(`Improvement: ${(before.time / after.time).toFixed(2)}x faster`);
