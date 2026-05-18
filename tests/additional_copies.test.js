const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";

class MockRecord {
  constructor(collectionOrData, maybeData) {
    this.collection = collectionOrData && collectionOrData.name ? collectionOrData.name : "";
    this.data = maybeData || (collectionOrData && !collectionOrData.name ? collectionOrData : {});
    this.id = this.data.id || "";
  }
  get(key) {
    return this.data[key];
  }
  getBool(key) {
    return !!this.data[key];
  }
  set(key, value) {
    this.data[key] = value;
  }
}

global.Record = MockRecord;

const additionalCopies = require("../lib/additional_copies.js");

function makeStaff(data) {
  return new MockRecord(Object.assign({
    id: "staff1",
    username: "selector",
    role: "staff",
    libraryOrgId: "10",
    libraryOrgName: "Main"
  }, data || {}));
}

function makeApp() {
  const saved = [];
  const source = new MockRecord({
    id: "req1",
    libraryOrgId: "10",
    libraryOrgName: "Main",
    title: "Source Title",
    author: "Source Author",
    format: "book",
    identifier: "9781",
    publication: "Already published",
    status: "pending_hold"
  });
  const taskRows = [
    new MockRecord({ id: "task1", status: "open", libraryOrgId: "10", bibid: "111", title: "Mine", created: "2026-05-05" }),
    new MockRecord({ id: "task1b", status: "open", libraryOrgId: "10", bibid: "111", title: "Mine 2", created: "2026-05-05" }),
    new MockRecord({ id: "task2", status: "open", libraryOrgId: "20", bibid: "222", title: "Other", created: "2026-05-06" }),
    new MockRecord({ id: "task3", status: "closed", libraryOrgId: "10", bibid: "111", title: "Closed", created: "2026-05-07" }),
  ];
  return {
    saved,
    source,
    findCollectionByNameOrId(name) {
      return { name };
    },
    findFirstRecordByData(collection, field, value) {
      if (collection === "polaris_organizations" && field === "organizationId" && value === "10") {
        return new MockRecord({ id: "orgpb1", organizationId: "10" });
      }
      throw new Error("not found");
    },
    findRecordById(collection, id) {
      if (collection === "title_requests" && id === "req1") return source;
      throw new Error("not found");
    },
    findRecordsByFilter(collection, filter, sort, limit, offset, params) {
      assert.strictEqual(collection, "additional_copy_requests");
      let rows = taskRows.filter(row => row.get("status") === params.status);
      if (params.libraryOrgId) rows = rows.filter(row => row.get("libraryOrgId") === params.libraryOrgId);
      if (params.bibid) rows = rows.filter(row => row.get("bibid") === params.bibid);
      return rows.slice(offset, offset + limit);
    },
    save(record) {
      if (!record.id) record.id = "saved" + (saved.length + 1);
      saved.push(record);
    }
  };
}

const app = makeApp();
const task = additionalCopies.createFromTitleRequest(app, app.source, makeStaff(), {
  bibid: "999",
  selectedPolarisTitle: "Catalog Title",
  selectedPolarisAuthor: "Catalog Author",
  selectedPolarisPublication: "2022"
});

assert.strictEqual(task.get("sourceTitleRequest"), "req1");
assert.strictEqual(task.get("libraryOrgId"), "10");
assert.strictEqual(task.get("bibid"), "999");
assert.strictEqual(task.get("title"), "Catalog Title");
assert.strictEqual(task.get("publication"), "Already published");
assert.strictEqual(task.get("status"), "open");
assert.match(task.get("notes"), /Created from request req1 by selector/);
assert.strictEqual(app.source.get("status"), "pending_hold");

// Tracer Bullet: Assert created & updated timestamps exist on task creation
assert.ok(task.get("created"), "task.created should be populated on creation");
assert.ok(task.get("updated"), "task.updated should be populated on creation");
const createdTime = new Date(task.get("created")).getTime();
const updatedTime = new Date(task.get("updated")).getTime();
assert.ok(!isNaN(createdTime), "task.created should be a valid date");
assert.ok(!isNaN(updatedTime), "task.updated should be a valid date");
assert.strictEqual(task.get("created"), task.get("updated"), "created and updated should be equal on creation");

const taskJson = additionalCopies.toJson(task, app);
assert.strictEqual(taskJson.created, task.get("created"));
assert.strictEqual(taskJson.updated, task.get("updated"));


const libraryList = additionalCopies.listForStaff(app, makeStaff(), { status: "open" });
assert.deepStrictEqual(libraryList.items.map(item => item.id), ["task1", "task1b"]);

const superList = additionalCopies.listForStaff(app, makeStaff({ role: "super_admin", libraryOrgId: "" }), { status: "open", scope: "all" });
assert.deepStrictEqual(superList.items.map(item => item.id), ["task1", "task1b", "task2"]);

assert.strictEqual(additionalCopies.countOpenForLibraryBib(app, "10", "111"), 2);
assert.strictEqual(additionalCopies.countOpenForLibraryBib(app, "20", "111"), 0);

const closeTarget = new MockRecord({ id: "task4", status: "open", libraryOrgId: "10", created: "2026-05-01T00:00:00.000Z", updated: "2026-05-01T00:00:00.000Z" });
additionalCopies.closeTask(app, closeTarget, makeStaff());
assert.strictEqual(closeTarget.get("status"), "closed");
assert.strictEqual(closeTarget.get("closedByUsername"), "selector");
assert.ok(closeTarget.get("closedAt"));
assert.ok(closeTarget.get("updated"), "updated should be set on closeTask");
assert.notStrictEqual(closeTarget.get("updated"), "2026-05-01T00:00:00.000Z", "updated should change on closeTask");
assert.strictEqual(app.source.get("status"), "pending_hold");

// Test 3: reopenTask updates the `updated` timestamp
closeTarget.set("updated", "2026-05-01T00:00:00.000Z");
additionalCopies.reopenTask(app, closeTarget, makeStaff());
assert.strictEqual(closeTarget.get("status"), "open");
assert.ok(closeTarget.get("updated"), "updated should be set on reopenTask");
assert.notStrictEqual(closeTarget.get("updated"), "2026-05-01T00:00:00.000Z", "updated should change on reopenTask");

// Test 4: claimTask updates the `updated` timestamp
const claimTarget = new MockRecord({ id: "task5", status: "open", libraryOrgId: "10", created: "2026-05-01T00:00:00.000Z", updated: "2026-05-01T00:00:00.000Z" });
global.DateTime = class {
  constructor() {
    return new Date().toISOString();
  }
};
additionalCopies.claimTask(app, claimTarget, makeStaff());
assert.ok(claimTarget.get("updated"), "updated should be set on claimTask");
assert.notStrictEqual(claimTarget.get("updated"), "2026-05-01T00:00:00.000Z", "updated should change on claimTask");

// Test 5: unclaimTask updates the `updated` timestamp
claimTarget.set("updated", "2026-05-01T00:00:00.000Z");
additionalCopies.unclaimTask(app, claimTarget);
assert.ok(claimTarget.get("updated"), "updated should be set on unclaimTask");
assert.notStrictEqual(claimTarget.get("updated"), "2026-05-01T00:00:00.000Z", "updated should change on unclaimTask");

console.log("Additional-copy helper tests passed.");

