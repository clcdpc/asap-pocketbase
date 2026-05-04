const assert = require('assert');
const { performance } = require('perf_hooks');

// We need to mock the environment
global.__hooks = `${__dirname}/../pb_hooks`;

const records = require('../lib/records.js');

// Mock normalizeWorkflowTags
records.normalizeWorkflowTags = (tags) => tags;

let queries = 0;

const mockApp = {
  findRecordsByFilter: (collection, filter, sort, limit, offset, params) => {
    queries++;
    if (collection === 'title_request_tags') {
      return Array.from({ length: 50 }).map((_, i) => ({
        get: (field) => {
          if (field === 'tag') return `tag_${i}`;
        }
      }));
    }
    if (collection === 'workflow_tags') {
      // Mock for batch fetch
      return Array.from({ length: 50 }).map((_, i) => ({
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

const mockRecord = {
  id: 'req_123'
};

const start = performance.now();
const tags = records.workflowTagsForRequest(mockApp, mockRecord);
const end = performance.now();

console.log(`Queries executed: ${queries}`);
console.log(`Time taken: ${(end - start).toFixed(2)} ms`);
console.log(`Tags: ${tags.length}`);
