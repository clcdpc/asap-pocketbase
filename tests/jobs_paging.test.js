const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const env = {
  ASAP_TEST_QUEUE_PAGE_SIZE: "2",
  ASAP_TEST_QUEUE_MAX_PER_RUN: "3",
};
global.$os = {
  getenv(name) {
    return env[name] || "";
  }
};

const jobs = require("../lib/jobs.js");

class MockRecord {
  constructor(data) {
    this.id = data.id;
    this.data = Object.assign({}, data);
  }
  get(key) {
    return this.data[key];
  }
  set(key, value) {
    this.data[key] = value;
  }
}

function makeApp(records, calls) {
  return {
    findRecordsByFilter(collection, filter, sort, limit, offset, params) {
      assert.strictEqual(collection, "title_requests");
      assert.strictEqual(offset, 0);
      calls.push({ filter, sort, limit, params: Object.assign({}, params) });
      let matches = records.filter(record => record.get("status") === params.status);
      if (params.cursorValue) {
        matches = matches.filter(record => {
          const value = String(record.get("created") || "");
          return value > params.cursorValue || (value === params.cursorValue && record.id > params.cursorId);
        });
      }
      matches.sort((a, b) => {
        const created = String(a.get("created") || "").localeCompare(String(b.get("created") || ""));
        if (created !== 0) return created;
        return String(a.id).localeCompare(String(b.id));
      });
      return matches.slice(0, limit);
    },
    logger() {
      return {
        info() {},
      };
    },
  };
}

function runTests() {
  const rows = [
    new MockRecord({ id: "r5", status: "pending", created: "2026-01-05 00:00:00" }),
    new MockRecord({ id: "r1", status: "pending", created: "2026-01-01 00:00:00" }),
    new MockRecord({ id: "r3", status: "pending", created: "2026-01-03 00:00:00" }),
    new MockRecord({ id: "r2", status: "pending", created: "2026-01-02 00:00:00" }),
    new MockRecord({ id: "r4", status: "pending", created: "2026-01-04 00:00:00" }),
  ];
  const calls = [];
  const seen = [];
  const result = {};

  const stats = jobs._processPagedQueue(makeApp(rows, calls), result, {
    queueName: "test_queue",
    collection: "title_requests",
    filter: "status = {:status}",
    sortField: "created",
    params: { status: "pending" },
  }, function(record) {
    seen.push(record.id);
    record.set("status", "done");
  });

  assert.deepStrictEqual(seen, ["r1", "r2", "r3"]);
  assert.strictEqual(stats.scanned, 3);
  assert.strictEqual(stats.pages, 2);
  assert.strictEqual(stats.maxPerRunReached, true);
  assert.strictEqual(stats.moreRemain, true);
  assert.strictEqual(result.queueScanned, 3);
  assert.strictEqual(result.maxPerRunReached, true);
  assert.strictEqual(calls[0].limit, 2);
  assert.strictEqual(calls[1].params.cursorValue, "2026-01-02 00:00:00");
  assert.strictEqual(calls[1].params.cursorId, "r2");

  console.log("job paging tests passed.");
}

runTests();
