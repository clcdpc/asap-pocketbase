const assert = require('assert');
const { performance } = require('perf_hooks');

global.__hooks = `${__dirname}/../pb_hooks`;
const records = require('../lib/records.js');

records.normalizeWorkflowTags = (tags) => tags;

let queries = 0;

const mockApp = {
  findRecordsByFilter: (collection, filter, sort, limit, offset, params) => {
    queries++;
    if (collection === 'title_request_tags') {
      return Array.from({ length: 50 }).map((_, i) => ({
        get: (field) => {
          if (field === 'tag') return `tag_${i}`;
          if (field === 'titleRequest') return `req_${i % 10}`;
        }
      }));
    }
    if (collection === 'workflow_tags') {
      return Array.from({ length: 50 }).map((_, i) => ({
        id: `tag_${i}`,
        get: (field) => {
          if (field === 'code') return `tag_${i}_code`;
        }
      }));
    }
    return [];
  },
  findRecordById: (collection, id) => {
    queries++;
    return {
      get: (field) => {
        if (field === 'code') return `${id}_code`;
      }
    };
  }
};

const mockRecords = Array.from({ length: 10 }).map((_, i) => ({ id: `req_${i}` }));

console.log("=== Old Implementation (looping) ===");
queries = 0;
const startOld = performance.now();
for (let i = 0; i < mockRecords.length; i++) {
  // simulate old behavior by not batching requests
  let req = mockRecords[i];
  records.workflowTagsForRequest(mockApp, req);
}
const endOld = performance.now();
console.log(`Queries executed: ${queries}`);
console.log(`Time taken: ${(endOld - startOld).toFixed(2)} ms`);


console.log("\n=== New Implementation (batch fetching) ===");
queries = 0;
const startNew = performance.now();
records.workflowTagsForRequests(mockApp, mockRecords);
const endNew = performance.now();
console.log(`Queries executed: ${queries}`);
console.log(`Time taken: ${(endNew - startNew).toFixed(2)} ms`);
