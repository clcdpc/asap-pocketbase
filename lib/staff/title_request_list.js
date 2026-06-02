const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const additionalCopies = require(`${__hooks}/../lib/additional_copies.js`);

function staffTitleRequestsList(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var selectedScope = String(routeUtils.queryValue(e, "scope") || routeUtils.queryValue(e, "orgId") || "").trim();
  var result = [];
  var patronCache = {};
  var pickupBranchNameCache = {};
  var workflowTagsCache = {};
  var phaseEntryCache = {};
  var limit = 200;
  var offset = 0;
  var scope = titleRequestListScope(e.app, staff, selectedScope);

  if (!scope.canList) {
    return e.json(200, { items: [] });
  }

  while (true) {
    var page = fetchTitleRequestPage(e.app, scope, limit, offset);
    if (!page.length) {
      break;
    }

    preloadPatronsForTitleRequests(e.app, page, patronCache);
    preloadWorkflowTagsForRequests(e.app, page, workflowTagsCache);
    preloadPhaseEntryTimesForRequests(e.app, page, phaseEntryCache);

    for (var i = 0; i < page.length; i++) {
      result.push(buildStaffTitleRequestRow(e.app, page[i], patronCache, pickupBranchNameCache, workflowTagsCache, phaseEntryCache));
    }
    if (page.length < limit) {
      break;
    }
    offset += limit;
  }

  sortTitleRequestRowsByPhaseEntry(result);

  return e.json(200, {
    items: result,
    scope: titleRequestListResponseScope(e.app, staff, scope),
    availableLibraries: routeUtils.isSuperAdmin(staff) ? orgs.analyticsLibraryOptions(e.app) : []
  });
}

function titleRequestListScope(app, staff, selectedOrgId) {
  var isSuper = routeUtils.isSuperAdmin(staff);
  var staffLibraryOrgId = String(staff.get("libraryOrgId") || "").trim();
  var cleanSelected = String(selectedOrgId || "").trim();

  if (isSuper && (cleanSelected === "all" || cleanSelected === "system" || !cleanSelected)) {
    return {
      canList: true,
      mode: "all",
      libraryOrgId: "",
      filter: "id != ''",
      params: {}
    };
  }

  var libraryOrgId = isSuper ? cleanSelected : staffLibraryOrgId;
  if (!libraryOrgId) {
    return {
      canList: false,
      mode: "library",
      libraryOrgId: "",
      filter: "",
      params: {}
    };
  }

  return {
    canList: true,
    mode: "library",
    libraryOrgId: libraryOrgId,
    filter: "libraryOrgId = {:libraryOrgId}",
    params: { libraryOrgId: libraryOrgId }
  };
}

function fetchTitleRequestPage(app, scope, limit, offset) {
  return app.findRecordsByFilter("title_requests", scope.filter, "-created", limit, offset, scope.params);
}

function preloadPatronsForTitleRequests(app, titleRequests, patronCache) {
  var missingPatronIds = collectMissingPatronIds(titleRequests, patronCache);
  if (!missingPatronIds.length) {
    return;
  }

  var batchSize = 100;
  for (var i = 0; i < missingPatronIds.length; i += batchSize) {
    cachePatronBatch(app, missingPatronIds.slice(i, i + batchSize), patronCache);
  }
}


function preloadWorkflowTagsForRequests(app, titleRequests, workflowTagsCache) {
  var requestIds = [];
  for (var i = 0; i < titleRequests.length; i++) {
    var id = titleRequests[i].id;
    if (id && workflowTagsCache[id] === undefined) {
      requestIds.push(id);
      workflowTagsCache[id] = []; // initialize
    }
  }

  if (!requestIds.length) {
    return;
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

    var joinRows = app.findRecordsByFilter("title_request_tags", filterParts.join(" || "), "", batchIds.length * 10, 0, batchParams);
    if (!joinRows || joinRows.length === 0) continue;

    var tagIds = [];
    var tagParams = {};
    var tagConditions = [];
    var joinMap = {}; // mapping tagId -> list of requestIds

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
      // Due to potential large number of tags, we batch them as well, 100 is limit
      // title_request_tags gives us max 10 tags per request, if 100 requests, up to 1000 tags.
      // Need to chunk tagIds to 100
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
  }
}

function statusIdByCodeMap(app) {
  var map = {};
  try {
    var rows = app.findRecordsByFilter("request_statuses", "id != ''", "", 100, 0);
    for (var i = 0; i < rows.length; i++) {
      var code = records.normalizeStatus(rows[i].get("code") || "");
      if (code) {
        map[code] = rows[i].id;
      }
    }
  } catch (err) {}
  return map;
}

function requestPhaseEntryFallback(record) {
  var status = records.normalizeStatus(record.get("status") || "");
  if (status === records.STATUS.SUGGESTION) {
    return record.get("created") || record.created || "";
  }
  return record.get("updated") || record.updated || record.get("created") || record.created || "";
}

function preloadPhaseEntryTimesForRequests(app, titleRequests, phaseEntryCache) {
  try {
    var requestIds = [];
    var targetStatusRefByRequest = {};
    var statusIds = null;

    for (var i = 0; i < titleRequests.length; i++) {
      var record = titleRequests[i];
      if (!record || !record.id || phaseEntryCache[record.id] !== undefined) continue;

      phaseEntryCache[record.id] = requestPhaseEntryFallback(record);
      requestIds.push(record.id);

      var statusRef = String(record.get("statusRef") || "").trim();
      if (!statusRef) {
        if (statusIds === null) {
          statusIds = statusIdByCodeMap(app);
        }
        statusRef = statusIds[records.normalizeStatus(record.get("status") || "")] || "";
      }
      targetStatusRefByRequest[record.id] = statusRef;
    }

    if (!requestIds.length) {
      return;
    }

    var batchSize = 100;
    for (var b = 0; b < requestIds.length; b += batchSize) {
      preloadPhaseEntryTimesBatch(app, requestIds.slice(b, b + batchSize), targetStatusRefByRequest, phaseEntryCache);
    }
  } catch (err) {
    try {
      app.logger().warn("Phase-entry preload failed; using fallback request timestamps", "error", String(err));
    } catch (logErr) {}
  }
}

function preloadPhaseEntryTimesBatch(app, requestIds, targetStatusRefByRequest, phaseEntryCache) {
  var filterParts = [];
  var params = {};
  var unresolved = {};
  for (var i = 0; i < requestIds.length; i++) {
    var key = "r" + i;
    filterParts.push("titleRequest = {:" + key + "}");
    params[key] = requestIds[i];
    unresolved[requestIds[i]] = !!targetStatusRefByRequest[requestIds[i]];
  }

  var offset = 0;
  var limit = 200;
  var maxPages = 10;
  for (var pageIndex = 0; pageIndex < maxPages; pageIndex++) {
    var events = app.findRecordsByFilter("title_request_events", filterParts.join(" || "), "-created", limit, offset, params);
    if (!events || !events.length) {
      break;
    }

    for (var j = 0; j < events.length; j++) {
      var event = events[j];
      var requestId = String(event.get("titleRequest") || "").trim();
      if (!unresolved[requestId]) continue;

      var targetStatusRef = targetStatusRefByRequest[requestId];
      var eventStatusRef = String(event.get("toStatus") || "").trim();
      if (targetStatusRef && eventStatusRef === targetStatusRef) {
        phaseEntryCache[requestId] = event.get("created") || event.created || phaseEntryCache[requestId] || "";
        unresolved[requestId] = false;
      }
    }

    var anyUnresolved = false;
    for (var id in unresolved) {
      if (Object.prototype.hasOwnProperty.call(unresolved, id) && unresolved[id]) {
        anyUnresolved = true;
        break;
      }
    }
    if (!anyUnresolved || events.length < limit) {
      break;
    }
    offset += limit;
  }
}

function collectMissingPatronIds(titleRequests, patronCache) {
  var missingPatronIds = [];
  var seenInPage = {};

  for (var i = 0; i < titleRequests.length; i++) {
    var patronId = titleRequestPatronId(titleRequests[i]);
    if (patronId && patronCache[patronId] === undefined && !seenInPage[patronId]) {
      missingPatronIds.push(patronId);
      seenInPage[patronId] = true;
    }
  }

  return missingPatronIds;
}

function cachePatronBatch(app, patronIds, patronCache) {
  var results = app.findRecordsByIds("patron_users", patronIds);
  var foundIds = {};
  for (var j = 0; j < results.length; j++) {
    var record = results[j];
    patronCache[record.id] = record;
    foundIds[record.id] = true;
  }

  for (var k = 0; k < patronIds.length; k++) {
    var id = patronIds[k];
    if (!foundIds[id]) {
      patronCache[id] = null;
    }
  }
}

function buildStaffTitleRequestRow(app, titleRequest, patronCache, pickupBranchNameCache, workflowTagsCache, phaseEntryCache) {
  var rowOptions = {};
  if (workflowTagsCache && workflowTagsCache[titleRequest.id] !== undefined) {
    rowOptions.workflowTags = workflowTagsCache[titleRequest.id];
  }
  var row = records.titleRequestToJson(titleRequest, app, rowOptions);
  if (phaseEntryCache && phaseEntryCache[titleRequest.id] !== undefined) {
    row.phaseEnteredAt = phaseEntryCache[titleRequest.id];
  } else {
    row.phaseEnteredAt = requestPhaseEntryFallback(titleRequest);
  }
  var patronRecord = cachedPatronForTitleRequest(app, titleRequest, patronCache);

  enrichRowWithPatron(row, patronRecord);
  enrichRowWithPickupBranch(app, row, patronRecord, pickupBranchNameCache);

  return row;
}

function sortableTime(value) {
  if (!value) return 0;
  var time = new Date(value).getTime();
  return isNaN(time) ? 0 : time;
}

function sortTitleRequestRowsByPhaseEntry(rows) {
  rows.sort(function (a, b) {
    return sortableTime(b.phaseEnteredAt) - sortableTime(a.phaseEnteredAt) ||
      sortableTime(b.updated) - sortableTime(a.updated) ||
      sortableTime(b.created) - sortableTime(a.created) ||
      String(b.id || "").localeCompare(String(a.id || ""));
  });
  return rows;
}

function cachedPatronForTitleRequest(app, titleRequest, patronCache) {
  var patronId = titleRequestPatronId(titleRequest);
  if (!patronId) {
    return null;
  }
  if (patronCache[patronId] !== undefined) {
    return patronCache[patronId];
  }

  try {
    patronCache[patronId] = app.findRecordById("patron_users", patronId);
  } catch (err) {
    patronCache[patronId] = null;
  }
  return patronCache[patronId];
}

function titleRequestPatronId(titleRequest) {
  return String(titleRequest.get("patron") || "").trim();
}

function enrichRowWithPatron(row, patronRecord) {
  var patronFirst = row.nameFirst || (patronRecord ? patronRecord.get("nameFirst") || "" : "");
  var patronLast = row.nameLast || (patronRecord ? patronRecord.get("nameLast") || "" : "");

  row.patronName = (String(patronFirst).trim() + " " + String(patronLast).trim()).trim();
  row.patronEmail = row.email || (patronRecord ? patronRecord.get("notificationEmail") || patronRecord.email() || "" : "");
  row.libraryOrgName = row.libraryOrgName || (patronRecord ? patronRecord.get("libraryOrgName") || "" : "");
}

function enrichRowWithPickupBranch(app, row, patronRecord, pickupBranchNameCache) {
  row.preferredPickupBranchId = row.preferredPickupBranchId || (patronRecord ? patronRecord.get("preferredPickupBranchId") || "" : "");
  row.preferredPickupBranchName = row.preferredPickupBranchName || (patronRecord ? patronRecord.get("preferredPickupBranchName") || "" : "");
  if (!row.preferredPickupBranchId) {
    row.preferredPickupBranchId = row.patronOrgId || (patronRecord ? patronRecord.get("patronOrgId") || "" : "") || "0";
  }
  if (!row.preferredPickupBranchName) {
    row.preferredPickupBranchName = cachedPickupBranchName(app, row.preferredPickupBranchId, pickupBranchNameCache);
  }
}

function cachedPickupBranchName(app, branchId, pickupBranchNameCache) {
  if (pickupBranchNameCache[branchId] === undefined) {
    pickupBranchNameCache[branchId] = orgs.pickupBranchDisplayName(app, branchId);
  }
  return pickupBranchNameCache[branchId];
}

function titleRequestListResponseScope(app, staff, scope) {
  var label = "All libraries";
  if (scope.mode === "library") {
    label = orgs.analyticsLibraryLabel(app, scope.libraryOrgId) || staff.get("libraryOrgName") || scope.libraryOrgId || "Current library";
  }
  return {
    mode: scope.mode,
    libraryOrgId: scope.libraryOrgId,
    label: label,
    superAdmin: routeUtils.isSuperAdmin(staff),
  };
}



module.exports = {
  staffTitleRequestsList,
  titleRequestListScope,
  fetchTitleRequestPage,
  preloadPatronsForTitleRequests,
  preloadWorkflowTagsForRequests,
  preloadPhaseEntryTimesForRequests,
  buildStaffTitleRequestRow,
  sortTitleRequestRowsByPhaseEntry,
  titleRequestListResponseScope,
  statusIdByCodeMap,
  requestPhaseEntryFallback,
  preloadPhaseEntryTimesBatch,
  collectMissingPatronIds,
  cachePatronBatch,
  sortableTime,
  cachedPatronForTitleRequest,
  titleRequestPatronId,
  enrichRowWithPatron,
  enrichRowWithPickupBranch,
  cachedPickupBranchName
};
