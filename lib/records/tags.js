const helpers = require("./helpers.js");

function normalizeWorkflowTags(tags) {
  if (!Array.isArray(tags)) {
    return [];
  }
  let seen = {};
  let normalized = [];
  for (let i = 0; i < tags.length; i++) {
    let tag = normalizeWorkflowTagName(tags[i]);
    if (!tag || seen[tag]) {
      continue;
    }
    seen[tag] = true;
    normalized.push(tag);
  }
  return normalized;
}

function normalizeWorkflowTagName(tag) {
  let clean = String(tag || "").trim();
  if (clean === "dupe found in Polaris" || clean === "Dupe found in Polaris") {
    return "Identifier found";
  }
  if (clean === "ISBN not found in system") {
    return "Identifier number not found in system";
  }
  return clean;
}

function conflictingWorkflowTagNames(tag) {
  let cleanTag = normalizeWorkflowTagName(tag);
  if (cleanTag === "Identifier found") {
    return ["Identifier number not found in system"];
  }
  if (cleanTag === "Identifier number not found in system") {
    return ["Identifier found"];
  }
  return [];
}

function removeWorkflowTagForRequest(app, record, tag) {
  let cleanTag = normalizeWorkflowTagName(tag);
  if (!cleanTag || !record || !record.id) return false;
  let tagRecord = helpers.lookupByCode(app, "workflow_tags", cleanTag);
  if (!tagRecord) return false;
  let removed = false;
  try {
    let rows = app.findRecordsByFilter("title_request_tags", "titleRequest = {:request} && tag = {:tag}", "", 100, 0, { request: record.id, tag: tagRecord.id });
    for (let i = 0; i < rows.length; i++) {
      app.delete(rows[i]);
      removed = true;
    }
  } catch (err) {}
  return removed;
}

function addWorkflowTagForRequest(app, record, tag) {
  let cleanTag = normalizeWorkflowTagName(tag);
  if (!cleanTag) return false;
  let conflicts = conflictingWorkflowTagNames(cleanTag);
  for (let i = 0; i < conflicts.length; i++) {
    removeWorkflowTagForRequest(app, record, conflicts[i]);
  }
  let tagRecord = helpers.lookupByCode(app, "workflow_tags", cleanTag);
  if (!tagRecord) {
    tagRecord = new Record(app.findCollectionByNameOrId("workflow_tags"));
    tagRecord.set("code", cleanTag);
    tagRecord.set("label", cleanTag);
    app.save(tagRecord);
  }
  try {
    app.findFirstRecordByFilter("title_request_tags", "titleRequest = {:request} && tag = {:tag}", { request: record.id, tag: tagRecord.id });
    return false;
  } catch (err) {
    let join = new Record(app.findCollectionByNameOrId("title_request_tags"));
    join.set("titleRequest", record.id);
    join.set("tag", tagRecord.id);
    app.save(join);
    return true;
  }
}

function workflowTagsForRequest(app, record) {
  let tags = [];
  try {
    let rows = app.findRecordsByFilter("title_request_tags", "titleRequest = {:request}", "", 100, 0, { request: record.id });
    if (rows && rows.length > 0) {
      let params = {};
      let conditions = [];
      for (let i = 0; i < rows.length; i++) {
        let tagId = rows[i].get("tag");
        if (tagId) {
          let pKey = "p" + i;
          conditions.push("id = {:" + pKey + "}");
          params[pKey] = tagId;
        }
      }

      if (conditions.length > 0) {
        let batchFilter = conditions.join(" || ");
        let tagRecords = app.findRecordsByFilter("workflow_tags", batchFilter, "", conditions.length, 0, params);
        if (tagRecords) {
          for (let j = 0; j < tagRecords.length; j++) {
            tags.push(tagRecords[j].get("code") || tagRecords[j].get("label") || "");
          }
        }
      }
    }
  } catch (err) {}
  return normalizeWorkflowTags(tags);
}

module.exports = {
  normalizeWorkflowTags: normalizeWorkflowTags,
  normalizeWorkflowTagName: normalizeWorkflowTagName,
  conflictingWorkflowTagNames: conflictingWorkflowTagNames,
  removeWorkflowTagForRequest: removeWorkflowTagForRequest,
  addWorkflowTagForRequest: addWorkflowTagForRequest,
  workflowTagsForRequest: workflowTagsForRequest,
};
