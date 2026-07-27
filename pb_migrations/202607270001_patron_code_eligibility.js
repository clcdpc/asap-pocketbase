/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

function addField(collection, spec) {
  try {
    collection.fields.add(new Field(spec));
  } catch (err) {}
}

function removeField(collection, name) {
  try {
    collection.fields.removeByName(name);
  } catch (err) {}
}

migrate((app) => {
  const patronUsers = app.findCollectionByNameOrId("patron_users");
  addField(patronUsers, field("patronCodeId", "text", { max: 32 }));
  addField(patronUsers, field("patronCodeDescription", "text", { max: 256 }));
  app.save(patronUsers);

  const titleRequests = app.findCollectionByNameOrId("title_requests");
  addField(titleRequests, field("patronCodeId", "text", { max: 32 }));
  addField(titleRequests, field("patronCodeDescription", "text", { max: 256 }));
  app.save(titleRequests);

  const workflowSettings = app.findCollectionByNameOrId("workflow_settings");
  addField(workflowSettings, field("patronCodeEligibilityEnabled", "bool"));
  addField(workflowSettings, field("allowedPatronCodeIds", "text", { max: 2048 }));
  addField(workflowSettings, field("patronCodeEligibilityMessage", "text", { max: 1000 }));
  app.save(workflowSettings);

  const systemSettings = app.findCollectionByNameOrId("system_settings");
  addField(systemSettings, field("patronCodesSyncStatus", "select", { maxSelect: 1, values: ["not_loaded", "loading", "loaded", "error"] }));
  addField(systemSettings, field("patronCodesLastSynced", "date"));
  addField(systemSettings, field("patronCodesSyncMessage", "text"));
  addField(systemSettings, field("patronCodesSyncError", "text"));
  app.save(systemSettings);

  try {
    const patronCodes = new Collection({
      type: "base",
      name: "polaris_patron_codes",
      listRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || @request.auth.role = 'admin')",
      viewRule: "@request.auth.collectionName = 'staff_users' && (@request.auth.role = 'super_admin' || @request.auth.role = 'admin')",
      fields: [
        field("patronCodeId", "text", { required: true, max: 32 }),
        field("description", "text", { max: 256 }),
        field("raw", "json"),
        field("lastSynced", "date")
      ],
      indexes: ["CREATE UNIQUE INDEX idx_polaris_patron_codes_id ON polaris_patron_codes (patronCodeId)"]
    });
    app.save(patronCodes);
  } catch (err) {}
}, (app) => {
  const patronUsers = app.findCollectionByNameOrId("patron_users");
  removeField(patronUsers, "patronCodeId");
  removeField(patronUsers, "patronCodeDescription");
  app.save(patronUsers);

  const titleRequests = app.findCollectionByNameOrId("title_requests");
  removeField(titleRequests, "patronCodeId");
  removeField(titleRequests, "patronCodeDescription");
  app.save(titleRequests);

  const workflowSettings = app.findCollectionByNameOrId("workflow_settings");
  removeField(workflowSettings, "patronCodeEligibilityEnabled");
  removeField(workflowSettings, "allowedPatronCodeIds");
  removeField(workflowSettings, "patronCodeEligibilityMessage");
  app.save(workflowSettings);

  const systemSettings = app.findCollectionByNameOrId("system_settings");
  removeField(systemSettings, "patronCodesSyncStatus");
  removeField(systemSettings, "patronCodesLastSynced");
  removeField(systemSettings, "patronCodesSyncMessage");
  removeField(systemSettings, "patronCodesSyncError");
  app.save(systemSettings);

  try {
    app.delete(app.findCollectionByNameOrId("polaris_patron_codes"));
  } catch (err) {}
});
