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
    workflowSettings.fields.add(new Field(field("externalSearchLabel", "text", { required: false })));
    workflowSettings.fields.add(new Field(field("externalSearchUrlTemplate", "text", { required: false })));
    app.save(workflowSettings);
  } catch (err) {}
}, (app) => {
  const workflowSettings = app.findCollectionByNameOrId("workflow_settings");
  try {
    workflowSettings.fields.removeByName("externalSearchLabel");
    workflowSettings.fields.removeByName("externalSearchUrlTemplate");
    app.save(workflowSettings);
  } catch (err) {}
});
