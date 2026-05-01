/// <reference path="../pb_data/types.d.ts" />

function upsertWorkflowTag(app, row) {
  var record;
  try {
    record = app.findFirstRecordByData("workflow_tags", "code", row.code);
  } catch (err) {
    record = new Record(app.findCollectionByNameOrId("workflow_tags"));
    record.set("code", row.code);
  }
  record.set("label", row.label || row.code);
  record.set("description", row.description || "");
  app.save(record);
}

migrate((app) => {
  upsertWorkflowTag(app, {
    code: "Duplicate suggestion",
    label: "Duplicate suggestion",
    description: "Another patron has a suggestion with the same identifier number."
  });
}, (app) => {
  return null;
});
