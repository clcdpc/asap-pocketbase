const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");
global.Record = function Record() {
  return record("saved_tpl_456", {});
};

const routes = require("../lib/staff_routes.js");
const emailConfig = require("../lib/config/emails.js");

function record(id, fields) {
  return {
    id,
    get: function (key) {
      return fields[key];
    },
    getBool: function (key) {
      return !!fields[key];
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
      if (collection === "workflow_settings" && options.usedByAutoReject) {
        return [record("workflow_123", {
          outstandingTimeoutRejectionTemplate: template.id,
        })];
      }
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
      assert.strictEqual(err.code, routes.TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE || 'TEMPLATE_IN_USE_BY_AUTO_REJECT');
      assert.strictEqual(err.message, "This template can’t be deleted because it’s currently used by the auto-reject email. Assign a different template or disable auto-reject before deleting.");
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

test("library deletion of an inherited rejection template persists as an override", function () {
  const org = record("org_rec_100", { organizationId: "100" });
  const systemTemplate = record("tpl_system", {
    scope: "system",
    name: "System reason",
    subject: "System subject",
    body: "System body",
    enabled: true,
    sortOrder: 1,
  });
  const rows = [systemTemplate];
  const saved = [];
  const app = {
    findCollectionByNameOrId: function (name) {
      return { name };
    },
    findFirstRecordByData: function (collection, field, value) {
      if (collection === "polaris_organizations" && field === "organizationId" && String(value) === "100") return org;
      throw new Error("not found");
    },
    findRecordsByFilter: function (collection, filter, sort, limit, offset, params) {
      if (collection === "rejection_templates") {
        if (filter.indexOf("scope = 'system'") >= 0) {
          return rows.filter(function (row) {
            return row.get("scope") === "system" && row.getBool("enabled") !== false;
          });
        }
        if (filter.indexOf("scope = 'library'") >= 0 && params && params.org === org.id) {
          return rows.filter(function (row) {
            return row.get("scope") === "library" && row.get("libraryOrganization") === org.id && (filter.indexOf("enabled = true") < 0 || row.getBool("enabled") !== false);
          });
        }
      }
      if (collection === "email_templates") return [];
      if (collection === "workflow_settings") return [];
      return [];
    },
    findFirstRecordByFilter: function () {
      throw new Error("not found");
    },
    findRecordById: function () {
      throw new Error("not found");
    },
    save: function (row) {
      if (!row.id) row.id = "library_hidden_tpl";
      if (rows.indexOf(row) === -1) rows.push(row);
      saved.push(row);
    },
    delete: function (row) {
      const index = rows.indexOf(row);
      if (index >= 0) rows.splice(index, 1);
    },
  };

  routes.saveRejectionTemplates(app, "library", "100", []);

  assert.strictEqual(saved.length, 1, "Expected a library override marker to be saved");
  assert.strictEqual(saved[0].get("scope"), "library");
  assert.strictEqual(saved[0].get("libraryOrganization"), org.id);
  assert.strictEqual(saved[0].get("sourceTemplateId"), "tpl_system");
  assert.strictEqual(saved[0].getBool("enabled"), false);
  assert.deepStrictEqual(emailConfig.emailsFor(app, "100").rejection_templates, []);
});

console.log(`Tests finished: ${passed} passed, ${failed} failed.`);

if (failed > 0) {
  process.exit(1);
}
