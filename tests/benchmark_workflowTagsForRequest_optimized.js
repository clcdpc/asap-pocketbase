const assert = require('assert');
const { performance } = require('perf_hooks');

// We need to mock the environment
global.__hooks = `${__dirname}/../pb_hooks`;

const records = require('../pb_hooks/lib/records.js');

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
// new logic
  var tags = [];
  try {
    var rows = mockApp.findRecordsByFilter("title_request_tags", "titleRequest = {:request}", "", 100, 0, { request: mockRecord.id });
    if (rows && rows.length > 0) {
        var tagIds = [];
        var params = {};
        var conditions = [];
        for (var i = 0; i < rows.length; i++) {
            var tagId = rows[i].get("tag");
            if (tagId) {
                var pKey = "p" + i;
                conditions.push("id = {:" + pKey + "}");
                params[pKey] = tagId;
            }
        }

        if (conditions.length > 0) {
            var batchFilter = conditions.join(" || ");
            var tagRecords = mockApp.findRecordsByFilter("workflow_tags", batchFilter, "", conditions.length, 0, params);
            if (tagRecords) {
                for (var j = 0; j < tagRecords.length; j++) {
                    tags.push(tagRecords[j].get("code") || tagRecords[j].get("label") || "");
                }
            }
        }
    }
  } catch (err) {}
const end = performance.now();

console.log(`Queries executed: ${queries}`);
console.log(`Time taken: ${(end - start).toFixed(2)} ms`);
console.log(`Tags: ${tags.length}`);
