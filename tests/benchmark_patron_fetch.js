const { performance } = require('perf_hooks');

// Mock data
const PAGE_SIZE = 200;
const UNIQUE_PATRONS = 50; // Some overlap, but many unique

const mockTitleRequests = [];
for (let i = 0; i < PAGE_SIZE; i++) {
  mockTitleRequests.push({
    get: (key) => {
      if (key === "patron") return `patron_${i % UNIQUE_PATRONS}`;
      return null;
    }
  });
}

// Mock e.app
let dbCalls = 0;
const mockApp = {
  findRecordById: (collection, id) => {
    dbCalls++;
    // Simulate DB latency
    let sum = 0;
    for (let i = 0; i < 10000; i++) sum += i;
    return {
      get: (key) => key === "nameFirst" ? "First" : "Last",
      email: () => "email@example.com"
    };
  },
  findRecordsByFilter: (collection, filter, sort, limit, offset, params) => {
    dbCalls++;
    // Simulate DB latency
    let sum = 0;
    for (let i = 0; i < 20000; i++) sum += i;

    // For batch fetch simulation
    const results = [];
    Object.keys(params).forEach(key => {
      if (key.startsWith('p')) {
        results.push({
          id: params[key],
          get: (key) => key === "nameFirst" ? "First" : "Last",
          email: () => "email@example.com"
        });
      }
    });
    return results;
  }
};

const mockRecords = {
  titleRequestToJson: (req) => ({})
};

function unoptimized(page) {
  dbCalls = 0;
  const patronCache = {};
  const start = performance.now();

  for (let i = 0; i < page.length; i++) {
    const row = mockRecords.titleRequestToJson(page[i]);
    const patronId = page[i].get("patron");
    let patronRecord = null;

    if (patronId) {
      if (patronCache[patronId] !== undefined) {
        patronRecord = patronCache[patronId];
      } else {
        try {
          patronRecord = mockApp.findRecordById("patron_users", patronId);
        } catch (err) {
          patronRecord = null;
        }
        patronCache[patronId] = patronRecord;
      }
    }
    // ... rest of processing
  }

  const end = performance.now();
  return { time: end - start, calls: dbCalls };
}

function optimized(page) {
  dbCalls = 0;
  const patronCache = {};
  const start = performance.now();

  // 1. Collect unique missing patron IDs
  const missingPatronIds = [];
  const seenInPage = {};
  for (let i = 0; i < page.length; i++) {
    const pId = page[i].get("patron");
    if (pId && patronCache[pId] === undefined && !seenInPage[pId]) {
      missingPatronIds.push(pId);
      seenInPage[pId] = true;
    }
  }

  // 2. Batch fetch in chunks
  if (missingPatronIds.length > 0) {
    const batchSize = 100;
    for (let j = 0; j < missingPatronIds.length; j += batchSize) {
      const chunk = missingPatronIds.slice(j, j + batchSize);
      const filterParts = [];
      const batchParams = {};
      for (let k = 0; k < chunk.length; k++) {
        filterParts.push(`id = {:p${k}}`);
        batchParams[`p${k}`] = chunk[k];
      }
      const batchFilter = filterParts.join(" || ");
      const results = mockApp.findRecordsByFilter("patron_users", batchFilter, "", chunk.length, 0, batchParams);

      const foundIds = {};
      for (let k = 0; k < results.length; k++) {
        const r = results[k];
        patronCache[r.id] = r;
        foundIds[r.id] = true;
      }
      for (let k = 0; k < chunk.length; k++) {
        const id = chunk[k];
        if (!foundIds[id]) {
          patronCache[id] = null;
        }
      }
    }
  }

  // 3. Process loop (now hitting cache)
  for (let i = 0; i < page.length; i++) {
    const row = mockRecords.titleRequestToJson(page[i]);
    const patronId = page[i].get("patron");
    let patronRecord = null;

    if (patronId) {
      if (patronCache[patronId] !== undefined) {
        patronRecord = patronCache[patronId];
      } else {
        // Fallback (shouldn't happen with batch fetch)
        try {
          patronRecord = mockApp.findRecordById("patron_users", patronId);
        } catch (err) {
          patronRecord = null;
        }
        patronCache[patronId] = patronRecord;
      }
    }
    // ... rest of processing
  }

  const end = performance.now();
  return { time: end - start, calls: dbCalls };
}

console.log(`Running Patron Fetch Benchmark (Page Size: ${PAGE_SIZE}, Unique Patrons: ${UNIQUE_PATRONS})...`);

const before = unoptimized(mockTitleRequests);
console.log(`Unoptimized (N+1): ${before.time.toFixed(2)} ms, ${before.calls} DB calls`);

const after = optimized(mockTitleRequests);
console.log(`Optimized (Batch): ${after.time.toFixed(2)} ms, ${after.calls} DB calls`);

console.log(`Improvement: ${(before.time / after.time).toFixed(2)}x faster`);
console.log(`Call Reduction: ${before.calls} -> ${after.calls} calls (${((1 - after.calls/before.calls)*100).toFixed(1)}% reduction)`);
