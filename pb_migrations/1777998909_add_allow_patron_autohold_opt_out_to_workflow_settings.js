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
    workflowSettings.fields.add(new Field(field("allowPatronAutoholdOptOut", "bool", { required: false })));
    app.save(workflowSettings);
  } catch (err) {}
}, (app) => {
  return null;
})
