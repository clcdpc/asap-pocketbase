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

function renameWorkflowTag(app, oldCode, newCode, newLabel, newDescription) {
  var oldRecord;
  try {
    oldRecord = app.findFirstRecordByData("workflow_tags", "code", oldCode);
  } catch (err) {
    return;
  }

  try {
    app.findFirstRecordByData("workflow_tags", "code", newCode);
    return;
  } catch (err) {
    // The new taxonomy row does not exist yet, so reuse the old row and keep
    // existing title_request_tags joins attached to the renamed tag.
  }

  oldRecord.set("code", newCode);
  oldRecord.set("label", newLabel || newCode);
  oldRecord.set("description", newDescription || "");
  app.save(oldRecord);
}

migrate((app) => {
  renameWorkflowTag(
    app,
    "dupe found in Polaris",
    "Identifier found",
    "Identifier found",
    "Identifier lookup found a Polaris bibliographic record."
  );
  renameWorkflowTag(
    app,
    "ISBN not found in system",
    "Identifier number not found in system",
    "Identifier number not found in system",
    "Identifier lookup did not find a Polaris bibliographic record."
  );

  [
    { code: "Identifier found", label: "Identifier found", description: "Identifier lookup found a Polaris bibliographic record." },
    { code: "Multiple Polaris matches", label: "Multiple Polaris matches", description: "Identifier lookup found more than one Polaris bibliographic record." },
    { code: "Hold placed", label: "Hold placed", description: "Polaris hold placement succeeded." },
    { code: "Hold exists (same patron)", label: "Hold exists (same patron)", description: "Polaris reported an existing duplicate hold request for this patron." },
    { code: "Hold failed: patron", label: "Hold failed: patron", description: "Polaris hold placement failed because of patron data." },
    { code: "Hold failed: bib", label: "Hold failed: bib", description: "Polaris hold placement failed because of bibliographic record data." },
    { code: "Hold failed: pickup", label: "Hold failed: pickup", description: "Polaris hold placement failed because of pickup settings." },
    { code: "Hold failed: workstation", label: "Hold failed: workstation", description: "Polaris hold placement failed because of workstation settings." },
    { code: "Hold failed: org", label: "Hold failed: org", description: "Polaris hold placement failed because of organization settings." },
    { code: "Hold failed", label: "Hold failed", description: "Polaris hold placement failed." },
    { code: "Identifier number not found in system", label: "Identifier number not found in system", description: "Identifier lookup did not find a Polaris bibliographic record." }
  ].forEach(function (row) {
    upsertWorkflowTag(app, row);
  });
}, (app) => {
  return null;
});
