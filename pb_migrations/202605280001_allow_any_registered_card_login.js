/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

function rel(name, collection, options) {
  options = options || {};
  options.collectionId = collection.id;
  options.maxSelect = options.maxSelect || 1;
  return field(name, "relation", options);
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
  const workflowSettings = app.findCollectionByNameOrId("workflow_settings");
  addField(workflowSettings, field("allowAnyRegisteredCardLogin", "bool", { required: false }));
  app.save(workflowSettings);

  const patronUsers = app.findCollectionByNameOrId("patron_users");
  addField(patronUsers, field("experienceLibraryOrgId", "text", { max: 32 }));
  addField(patronUsers, field("experienceLibraryOrgName", "text", { max: 256 }));
  addField(patronUsers, field("patronHomeLibraryOrgId", "text", { max: 32 }));
  addField(patronUsers, field("patronHomeLibraryOrgName", "text", { max: 256 }));
  addField(patronUsers, field("effectiveLibraryOrgId", "text", { max: 32 }));
  addField(patronUsers, field("effectiveLibraryOrgName", "text", { max: 256 }));
  app.save(patronUsers);

  try {
    const contexts = new Collection({
      type: "base",
      name: "patron_session_contexts",
      listRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
      viewRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
      fields: [
        rel("patron", patronUsers, { required: true }),
        field("patronUserId", "text", { max: 64 }),
        field("experienceLibraryOrgId", "text", { max: 32 }),
        field("experienceLibraryOrgName", "text", { max: 256 }),
        field("effectiveLibraryOrgId", "text", { required: true, max: 32 }),
        field("effectiveLibraryOrgName", "text", { max: 256 }),
        field("patronHomeLibraryOrgId", "text", { max: 32 }),
        field("patronHomeLibraryOrgName", "text", { max: 256 }),
        field("expiresAt", "date"),
        field("created", "date"),
        field("updated", "date")
      ],
      indexes: [
        "CREATE INDEX idx_patron_session_contexts_patron_user ON patron_session_contexts (patronUserId)",
        "CREATE INDEX idx_patron_session_contexts_effective_library ON patron_session_contexts (effectiveLibraryOrgId)"
      ]
    });
    app.save(contexts);
  } catch (err) {}
}, (app) => {
  const workflowSettings = app.findCollectionByNameOrId("workflow_settings");
  removeField(workflowSettings, "allowAnyRegisteredCardLogin");
  app.save(workflowSettings);

  const patronUsers = app.findCollectionByNameOrId("patron_users");
  removeField(patronUsers, "experienceLibraryOrgId");
  removeField(patronUsers, "experienceLibraryOrgName");
  removeField(patronUsers, "patronHomeLibraryOrgId");
  removeField(patronUsers, "patronHomeLibraryOrgName");
  removeField(patronUsers, "effectiveLibraryOrgId");
  removeField(patronUsers, "effectiveLibraryOrgName");
  app.save(patronUsers);

  try {
    const contexts = app.findCollectionByNameOrId("patron_session_contexts");
    app.delete(contexts);
  } catch (err) {}
});
