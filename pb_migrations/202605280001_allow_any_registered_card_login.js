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
});
