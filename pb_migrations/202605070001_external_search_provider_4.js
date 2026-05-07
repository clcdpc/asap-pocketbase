/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

migrate((app) => {
  const workflowSettings = app.findCollectionByNameOrId("workflow_settings");

  try {
    workflowSettings.fields.add(new Field(field("externalSearch4Enabled", "bool", { required: false })));
    workflowSettings.fields.add(new Field(field("externalSearch4Label", "text", { required: false })));
    workflowSettings.fields.add(new Field(field("externalSearch4UrlTemplate", "text", { required: false })));
    app.save(workflowSettings);
  } catch (err) {}

  const records = app.findRecordsByFilter("workflow_settings", "id != ''");
  records.forEach(record => {
    record.set("externalSearch4Enabled", false);
    record.set("externalSearch4Label", "");
    record.set("externalSearch4UrlTemplate", "");
    app.save(record);
  });
}, (app) => {
  const workflowSettings = app.findCollectionByNameOrId("workflow_settings");

  try {
    workflowSettings.fields.removeByName("externalSearch4Enabled");
    workflowSettings.fields.removeByName("externalSearch4Label");
    workflowSettings.fields.removeByName("externalSearch4UrlTemplate");
    app.save(workflowSettings);
  } catch (err) {}
});
