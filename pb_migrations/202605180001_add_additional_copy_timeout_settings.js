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
    workflowSettings.fields.add(new Field(field("additionalCopyTimeoutEnabled", "bool", { required: false })));
  } catch (err) {}
  try {
    workflowSettings.fields.add(new Field(field("additionalCopyTimeoutDays", "number", { onlyInt: true, required: false })));
  } catch (err) {}
  app.save(workflowSettings);

  const records = app.findRecordsByFilter("workflow_settings", "id != ''");
  records.forEach(record => {
    if (record.get("additionalCopyTimeoutDays") === undefined || record.get("additionalCopyTimeoutDays") === null || record.get("additionalCopyTimeoutDays") === "") {
      record.set("additionalCopyTimeoutDays", 14);
    }
    app.save(record);
  });
}, (app) => {
  const workflowSettings = app.findCollectionByNameOrId("workflow_settings");

  try {
    workflowSettings.fields.removeByName("additionalCopyTimeoutEnabled");
  } catch (err) {}
  try {
    workflowSettings.fields.removeByName("additionalCopyTimeoutDays");
  } catch (err) {}
  app.save(workflowSettings);
});
