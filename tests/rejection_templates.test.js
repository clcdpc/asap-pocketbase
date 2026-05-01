const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");
global.Record = function Record() {
  return record("saved_tpl_456", {});
};

const routes = require("../pb_hooks/lib/routes.js");

function record(id, fields) {
  return {
    id,
    get: function (key) {
      return fields[key];
    },
    set: function (key, value) {
      fields[key] = value;
    },
  };
}

function appWithTemplate(options) {
  const template = record(options.templateId || "tpl_123", {
    scope: "system",
    enabled: true,
  });
  const deleted = [];
  return {
    deleted,
    findRecordById: function () {
      throw new Error("not found");
    },
    findCollectionByNameOrId: function () {
      return {};
    },
    findRecordsByFilter: function (collection) {
      if (collection === "rejection_templates") return [template];
      return [];
    },
    findFirstRecordByFilter: function (collection) {
      if (collection === "workflow_settings" && options.usedByAutoReject) {
        return record("workflow_123", {
          outstandingTimeoutRejectionTemplate: template.id,
        });
      }
      throw new Error("not found");
    },
    save: function () {},
    delete: function (row) {
      deleted.push(row.id);
    },
  };
}

let passed = 0;
let failed = 0;

function test(name, fn) {
  try {
    fn();
    console.log(`PASS ${name}`);
    passed++;
  } catch (err) {
    console.error(`FAIL ${name}`);
    console.error(err && err.stack ? err.stack : err);
    failed++;
  }
}

test("deletes an unused rejection template", function () {
  const app = appWithTemplate({ usedByAutoReject: false });
  routes.saveRejectionTemplates(app, "system", "", []);
  assert.deepStrictEqual(app.deleted, ["tpl_123"]);
});

test("prevents deleting a rejection template used by auto-reject", function () {
  const app = appWithTemplate({ usedByAutoReject: true });
  assert.throws(
    function () {
      routes.saveRejectionTemplates(app, "system", "", []);
    },
    function (err) {
      assert.strictEqual(err.code, routes.TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE);
      assert.strictEqual(err.message, routes.TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE);
      return true;
    }
  );
  assert.deepStrictEqual(app.deleted, []);
});

test("keeps a newly created rejection template after PocketBase assigns an id", function () {
  const saved = [];
  const deleted = [];
  const app = {
    deleted,
    findRecordById: function () {
      throw new Error("not found");
    },
    findCollectionByNameOrId: function () {
      return {};
    },
    findRecordsByFilter: function (collection) {
      if (collection === "rejection_templates") return saved;
      return [];
    },
    findFirstRecordByFilter: function () {
      throw new Error("not found");
    },
    save: function (row) {
      saved.push(row);
    },
    delete: function (row) {
      deleted.push(row.id);
    },
  };
  routes.saveRejectionTemplates(app, "system", "", [
    { id: "client_temp_id", name: "New", subject: "Subject", body: "Body" },
  ]);
  assert.deepStrictEqual(saved.map((row) => row.id), ["saved_tpl_456"]);
  assert.deepStrictEqual(deleted, []);
});

test("clears auto-reject custom template when standard template is selected", function () {
  const workflow = record("workflow_123", {
    scope: "system",
    outstandingTimeoutRejectionTemplate: "tpl_custom",
  });
  const app = {
    findCollectionByNameOrId: function () {
      return {};
    },
    findFirstRecordByFilter: function (collection) {
      if (collection === "workflow_settings") return workflow;
      throw new Error("not found");
    },
    save: function () {},
  };
  routes.saveWorkflowSettings(app, "system", "", {
    outstandingTimeoutEnabled: true,
    outstandingTimeoutSendEmail: true,
    outstandingTimeoutRejectionTemplateId: "",
  });
  assert.strictEqual(workflow.get("outstandingTimeoutRejectionTemplate"), "");
});

test("deletes former auto-reject template after workflow switches to standard", function () {
  const workflow = record("workflow_123", {
    scope: "system",
    outstandingTimeoutRejectionTemplate: "tpl_custom",
  });
  const template = record("tpl_custom", {
    scope: "system",
    enabled: true,
  });
  const deleted = [];
  const app = {
    deleted,
    findCollectionByNameOrId: function () {
      return {};
    },
    findFirstRecordByFilter: function (collection) {
      if (collection === "workflow_settings") {
        if (workflow.get("outstandingTimeoutRejectionTemplate")) return workflow;
        throw new Error("not found");
      }
      throw new Error("not found");
    },
    findRecordsByFilter: function (collection) {
      if (collection === "rejection_templates") return [template];
      return [];
    },
    save: function () {},
    delete: function (row) {
      deleted.push(row.id);
    },
  };
  routes.saveWorkflowSettings(app, "system", "", {
    outstandingTimeoutEnabled: true,
    outstandingTimeoutSendEmail: true,
    outstandingTimeoutRejectionTemplateId: "",
  });
  routes.saveRejectionTemplates(app, "system", "", []);
  assert.strictEqual(workflow.get("outstandingTimeoutRejectionTemplate"), "");
  assert.deepStrictEqual(deleted, ["tpl_custom"]);
});

console.log(`Tests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
