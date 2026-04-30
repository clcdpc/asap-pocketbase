/// <reference path="../pb_data/types.d.ts" />

function findWorkflowTag(app, code) {
  try {
    return app.findFirstRecordByData("workflow_tags", "code", code);
  } catch (err) {
    return null;
  }
}

function ensureWorkflowTag(app, code, label, description) {
  var record = findWorkflowTag(app, code);
  if (!record) {
    record = new Record(app.findCollectionByNameOrId("workflow_tags"));
    record.set("code", code);
  }
  record.set("label", label || code);
  record.set("description", description || "");
  app.save(record);
  return record;
}

function moveTagJoins(app, fromTag, toTag) {
  if (!fromTag || !toTag || fromTag.id === toTag.id) {
    return;
  }

  var joins = app.findRecordsByFilter("title_request_tags", "tag = {:tag}", "", 5000, 0, { tag: fromTag.id });
  joins.forEach(function (join) {
    var titleRequest = join.get("titleRequest");
    try {
      app.findFirstRecordByFilter("title_request_tags", "titleRequest = {:request} && tag = {:tag}", { request: titleRequest, tag: toTag.id });
      app.delete(join);
    } catch (err) {
      join.set("tag", toTag.id);
      app.save(join);
    }
  });
}

function mergeLegacyWorkflowTag(app, oldCode, newCode, newLabel, newDescription) {
  var oldTag = findWorkflowTag(app, oldCode);
  var newTag = findWorkflowTag(app, newCode);

  if (!oldTag) {
    ensureWorkflowTag(app, newCode, newLabel, newDescription);
    return;
  }

  if (!newTag) {
    oldTag.set("code", newCode);
    oldTag.set("label", newLabel || newCode);
    oldTag.set("description", newDescription || "");
    app.save(oldTag);
    return;
  }

  moveTagJoins(app, oldTag, newTag);
  app.delete(oldTag);
  ensureWorkflowTag(app, newCode, newLabel, newDescription);
}

migrate((app) => {
  mergeLegacyWorkflowTag(
    app,
    "dupe found in Polaris",
    "Identifier found",
    "Identifier found",
    "Identifier lookup found a Polaris bibliographic record."
  );
  mergeLegacyWorkflowTag(
    app,
    "ISBN not found in system",
    "Identifier number not found in system",
    "Identifier number not found in system",
    "Identifier lookup did not find a Polaris bibliographic record."
  );
}, (app) => {
  return null;
});
