const assert = require("assert");
global.__hooks = __dirname + "/../pb_hooks";

const settingsUi = require("../lib/staff/settings_ui.js");
const tags = require("../lib/records/tags.js");

function rec(id, values) {
  return { id, get(key) { return values && values[key] || ""; } };
}

(function materialFormatsUseBulkDeleteWhenAvailable() {
  const stale = [rec("fmt1", { code: "dvd" }), rec("fmt2", { code: "cd" })];
  const calls = [];
  const app = {
    findRecordsByFilter(collection) {
      assert.strictEqual(collection, "material_formats");
      return stale;
    },
    deleteRecords(collection, ids) {
      calls.push({ collection, ids });
    },
    delete() {
      throw new Error("saveMaterialFormats should not delete stale formats one at a time when bulk delete is available");
    }
  };

  settingsUi.saveMaterialFormats(app, "system", "", { formatLabels: {} });
  assert.deepStrictEqual(calls, [{ collection: "material_formats", ids: ["fmt1", "fmt2"] }]);
})();

(function removeWorkflowTagUsesBulkDeleteWhenAvailable() {
  const joins = [rec("join1", {}), rec("join2", {})];
  const calls = [];
  const app = {
    findFirstRecordByData(collection, field, value) {
      assert.strictEqual(collection, "workflow_tags");
      assert.strictEqual(field, "code");
      assert.strictEqual(value, "Needs review");
      return rec("tag1", { code: "Needs review" });
    },
    findRecordsByFilter(collection) {
      assert.strictEqual(collection, "title_request_tags");
      return joins;
    },
    deleteRecords(collection, ids) {
      calls.push({ collection, ids });
    },
    delete() {
      throw new Error("removeWorkflowTagForRequest should not delete join rows one at a time when bulk delete is available");
    }
  };

  const removed = tags.removeWorkflowTagForRequest(app, { id: "req1" }, "Needs review");
  assert.strictEqual(removed, true);
  assert.deepStrictEqual(calls, [{ collection: "title_request_tags", ids: ["join1", "join2"] }]);
})();

(function titleRequestListPreloadDeduplicatesWorkflowTagLookups() {
  const list = require("../lib/staff/title_request_list.js");
  const workflowTagCalls = [];
  const app = {
    findRecordsByFilter(collection, filter, sort, limit, offset, params) {
      if (collection === "title_request_tags") {
        return [
          rec("join1", { titleRequest: "req1", tag: "tag1" }),
          rec("join2", { titleRequest: "req2", tag: "tag1" })
        ];
      }
      if (collection === "workflow_tags") {
        workflowTagCalls.push({ filter, limit, params });
        return [rec("tag1", { code: "Needs review" })];
      }
      return [];
    }
  };
  const cache = {};
  list.preloadWorkflowTagsForRequests(app, [{ id: "req1" }, { id: "req2" }], cache);
  assert.deepStrictEqual(cache, { req1: ["Needs review"], req2: ["Needs review"] });
  assert.strictEqual(workflowTagCalls.length, 1);
  assert.strictEqual(workflowTagCalls[0].limit, 1);
  assert.deepStrictEqual(workflowTagCalls[0].params, { t0: "tag1" });
})();

console.log("bulk_delete_paths.test.js passed.");
