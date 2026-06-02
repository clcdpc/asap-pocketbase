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

function workflowTagsForRequests(app, titleRequests) {
  let workflowTagsCache = {};
  var requestIds = [];
  for (var i = 0; i < titleRequests.length; i++) {
    var id = titleRequests[i].id;
    if (id && workflowTagsCache[id] === undefined) {
      requestIds.push(id);
      workflowTagsCache[id] = [];
    }
  }

  if (!requestIds.length) {
    return workflowTagsCache;
  }

  var batchSize = 100;
  for (var b = 0; b < requestIds.length; b += batchSize) {
    var batchIds = requestIds.slice(b, b + batchSize);

    var filterParts = [];
    var batchParams = {};
    for (var j = 0; j < batchIds.length; j++) {
      var pKey = "r" + j;
      filterParts.push("titleRequest = {:" + pKey + "}");
      batchParams[pKey] = batchIds[j];
    }

    try {
      var joinRows = app.findRecordsByFilter("title_request_tags", filterParts.join(" || "), "", batchIds.length * 10, 0, batchParams);
      if (!joinRows || joinRows.length === 0) continue;

      var tagIds = [];
      var tagParams = {};
      var tagConditions = [];
      var joinMap = {};

      for (var k = 0; k < joinRows.length; k++) {
        var reqId = joinRows[k].get("titleRequest");
        var tId = joinRows[k].get("tag");
        if (reqId && tId) {
          if (!joinMap[tId]) {
            joinMap[tId] = [];
            var pTagKey = "t" + tagIds.length;
            tagConditions.push("id = {:" + pTagKey + "}");
            tagParams[pTagKey] = tId;
            tagIds.push(tId);
          }
          joinMap[tId].push(reqId);
        }
      }

      if (tagConditions.length > 0) {
        var tagBatchSize = 100;
        for (var tb = 0; tb < tagConditions.length; tb += tagBatchSize) {
           var tagBatchConditions = tagConditions.slice(tb, tb + tagBatchSize);
           var batchFilter = tagBatchConditions.join(" || ");
           var tagRecords = app.findRecordsByFilter("workflow_tags", batchFilter, "", tagBatchConditions.length, 0, tagParams);
           if (tagRecords) {
             for (var tr = 0; tr < tagRecords.length; tr++) {
               var tId = tagRecords[tr].id;
               var tCode = tagRecords[tr].get("code") || tagRecords[tr].get("label") || "";
               var linkedReqs = joinMap[tId] || [];
               for (var lr = 0; lr < linkedReqs.length; lr++) {
                  workflowTagsCache[linkedReqs[lr]].push(tCode);
               }
             }
           }
        }
      }
    } catch (err) {}
  }

  for (var reqId in workflowTagsCache) {
    if (workflowTagsCache.hasOwnProperty(reqId)) {
        workflowTagsCache[reqId] = normalizeWorkflowTags(workflowTagsCache[reqId]);
    }
  }

  return workflowTagsCache;
}

function workflowTagsForRequest(app, record) {
  if (!record || !record.id) return [];
  let cache = workflowTagsForRequests(app, [record]);
  return cache[record.id] || [];
}

module.exports = {
  normalizeWorkflowTags: normalizeWorkflowTags,
  normalizeWorkflowTagName: normalizeWorkflowTagName,
  conflictingWorkflowTagNames: conflictingWorkflowTagNames,
  removeWorkflowTagForRequest: removeWorkflowTagForRequest,
  addWorkflowTagForRequest: addWorkflowTagForRequest,
  workflowTagsForRequest: workflowTagsForRequest,
  workflowTagsForRequests: workflowTagsForRequests,
};
