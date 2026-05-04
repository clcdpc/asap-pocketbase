const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";

class MockRecord {
  constructor(collection, initial = {}) {
    this.collection = collection;
    this.data = { ...initial };
    this.id = initial.id || "rec_" + Math.random().toString(16).slice(2);
  }
  get(key) {
    return this.data[key];
  }
  set(key, value) {
    this.data[key] = value;
  }
  email() {
    return this.data.email || "";
  }
  getBool(key) {
    return !!this.data[key];
  }
}

global.Record = MockRecord;

const records = require("../lib/records.js");

function makeApp(initialRequests) {
  const rows = {
    title_requests: initialRequests || [],
    workflow_tags: [],
    title_request_tags: [],
    title_request_events: [],
  };
  return {
    rows,
    findCollectionByNameOrId(name) {
      return { name };
    },
    findFirstRecordByData(collection, field, value) {
      const row = (rows[collection] || []).find(record => String(record.get(field) || "") === String(value || ""));
      if (!row) throw new Error("not found");
      return row;
    },
    findFirstRecordByFilter(collection, filter, params) {
      if (collection === "title_request_tags") {
        const row = rows.title_request_tags.find(record => record.get("titleRequest") === params.request && record.get("tag") === params.tag);
        if (!row) throw new Error("not found");
        return row;
      }
      throw new Error("not found");
    },
    findRecordsByFilter(collection, filter, sort, limit, offset, params) {
      if (collection !== "title_requests") return [];
      const requests = rows.title_requests;
      if (filter.includes("barcode = {:barcode} && identifier = {:identifier}")) {
        return requests.filter(record => record.get("barcode") === params.barcode && record.get("identifier") === params.identifier).slice(0, limit);
      }
      if (filter.includes("identifier = {:identifier} && barcode != {:barcode}")) {
        return requests.filter(record => record.get("identifier") === params.identifier && record.get("barcode") !== params.barcode && record.id !== params.id).slice(0, limit);
      }
      if (filter.includes("barcode = {:barcode} && ((title = {:title} && format = {:format})")) {
        return requests.filter(record => {
          if (record.get("barcode") !== params.barcode) return false;
          if (record.get("title") === params.title && record.get("format") === params.format) return true;
          return params.bibid && record.get("bibid") === params.bibid;
        }).slice(0, limit);
      }
      return [];
    },
    save(record) {
      const collection = record.collection && record.collection.name;
      if (!rows[collection]) rows[collection] = [];
      const existing = rows[collection].findIndex(row => row.id === record.id);
      if (existing >= 0) {
        rows[collection][existing] = record;
      } else {
        rows[collection].push(record);
      }
    },
    logger() {
      return { warn: () => {} };
    },
  };
}

function patron(barcode) {
  return new MockRecord({ name: "patron_users" }, {
    id: "patron_" + barcode,
    barcode,
    email: barcode + "@example.test",
  });
}

console.log("Running duplicate suggestion tests...");

{
  const existing = new MockRecord({ name: "title_requests" }, {
    barcode: "100",
    identifier: "978",
    title: "Existing",
    format: "book",
  });
  const app = makeApp([existing]);

  assert.throws(
    () => records.createSuggestion(app, patron("100"), { title: "New", identifier: "978", format: "book" }, { skipLimits: true }),
    err => err && err.code === 409 && err.message === "This patron already has a suggestion for this identifier number."
  );
}

{
  const existing = new MockRecord({ name: "title_requests" }, {
    barcode: "100",
    identifier: "978",
    title: "Existing",
    format: "book",
  });
  const app = makeApp([existing]);
  const created = records.createSuggestion(app, patron("200"), { title: "New", identifier: "978", format: "book" }, { skipLimits: true });

  assert.strictEqual(created.get("identifier"), "978");
  assert.strictEqual(app.rows.workflow_tags[0].get("code"), "Duplicate suggestion");
  assert.strictEqual(app.rows.title_request_tags[0].get("titleRequest"), created.id);
}

{
  const existing = new MockRecord({ name: "title_requests" }, {
    barcode: "100",
    identifier: "978",
    title: "Existing",
    format: "book",
  });
  const app = makeApp([existing]);
  let warnCalled = false;
  app.logger = () => ({
    warn: (msg, key1, val1, key2, val2) => {
      if (msg === "Cross-patron duplicate tagging failed") {
        warnCalled = true;
        assert.strictEqual(key1, "recordId");
        assert.strictEqual(key2, "error");
        assert(val2.includes("simulated save error"));
      }
    }
  });

  const originalSave = app.save;
  app.save = (record) => {
    if (record.collection && record.collection.name === "workflow_tags") {
      throw new Error("simulated save error");
    }
    originalSave(record);
  };

  const created = records.createSuggestion(app, patron("200"), { title: "New", identifier: "978", format: "book" }, { skipLimits: true });

  assert.strictEqual(warnCalled, true, "logger.warn should have been called");
  assert.strictEqual(created.get("identifier"), "978");
  assert.strictEqual(app.rows.workflow_tags.length, 0);
}

console.log("Duplicate suggestion tests passed.");
