var __hooks = typeof __hooks !== "undefined" ? __hooks : __dirname + "/../../pb_hooks";


const config = require(`${__hooks}/../lib/config.js`);
const identity = require(`${__hooks}/../lib/identity.js`);
// const jobs = require(`${__hooks}/../lib/jobs.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const formatClaimRules = require(`${__hooks}/../lib/format_claim_rules.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
// const additionalCopies = require(`${__hooks}/../lib/additional_copies.js`);

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
    availableLibraries: routeUtils.isSuperAdmin(staff) ? analyticsLibraryOptions(e.app) : []
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
  var filterParts = [];
  var batchParams = {};

  for (var i = 0; i < patronIds.length; i++) {
    filterParts.push("id = {:p" + i + "}");
    batchParams["p" + i] = patronIds[i];
  }

  var results = app.findRecordsByFilter("patron_users", filterParts.join(" || "), "", patronIds.length, 0, batchParams);
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
    label = analyticsLibraryLabel(app, scope.libraryOrgId) || staff.get("libraryOrgName") || scope.libraryOrgId || "Current library";
  }
  return {
    mode: scope.mode,
    libraryOrgId: scope.libraryOrgId,
    label: label,
    superAdmin: routeUtils.isSuperAdmin(staff),
  };
}

function staffClaimTitleRequest(e) {
  return mutateTitleRequestClaim(e, "claim");
}

function staffUnclaimTitleRequest(e) {
  return mutateTitleRequestClaim(e, "unclaim");
}

function mutateTitleRequestClaim(e, action) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = String(e.request.pathValue("id") || "").trim();

  try {
    var updated = runClaimMutation(e.app, function (app) {
      var record = app.findRecordById("title_requests", id);
      var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
      if (accessError) {
        return { response: accessError };
      }

      if (action === "claim") {
        return claimTitleRequest(app, staff, record);
      }
      return unclaimTitleRequest(app, staff, record);
    });

    if (updated && updated.response) {
      return updated.response;
    }
    return e.json(200, records.titleRequestToJson(updated, e.app));
  } catch (err) {
    if (err && err.statusCode) {
      return e.json(err.statusCode, { message: err.message });
    }
    e.app.logger().error("Staff claim action failed", "requestId", id, "error", String(err));
    return e.json(400, { message: "System error: " + err.message });
  }
}

function runClaimMutation(app, fn) {
  if (app && typeof app.runInTransaction === "function") {
    var result;
    app.runInTransaction(function (txApp) {
      result = fn(txApp);
    });
    return result;
  }
  return fn(app);
}

function claimTitleRequest(app, staff, record) {
  var claimantId = String(record.get("claimedByStaffUserId") || "").trim();
  var staffId = String(staff.id || "").trim();
  if (claimantId && claimantId !== staffId) {
    throw claimConflictError(record);
  }

  formatClaimRules.setManualClaim(record, staff);
  app.save(record);
  records.recordEvent(app, record, "claim_manual_assigned", "Manually claimed by " + staffClaimDisplayName(staff) + ".", {
    actorName: staff.get("username") || staffClaimDisplayName(staff)
  });
  return app.findRecordById("title_requests", record.id);
}

function unclaimTitleRequest(app, staff, record) {
  var claimantId = String(record.get("claimedByStaffUserId") || "").trim();
  var staffId = String(staff.id || "").trim();
  if (claimantId && claimantId !== staffId && !routeUtils.isAdminRole(staff)) {
    throw forbiddenClaimError("Only the staff member who claimed this request, an admin, or a super admin can unclaim it.");
  }

  formatClaimRules.clearClaim(record);
  app.save(record);
  records.recordEvent(app, record, "claim_manual_cleared", "Manual claim cleared by " + staffClaimDisplayName(staff) + ".", {
    actorName: staff.get("username") || staffClaimDisplayName(staff)
  });
  return app.findRecordById("title_requests", record.id);
}

function staffClaimDisplayName(staff) {
  return String(staff.get("displayName") || staff.get("username") || staff.get("identityKey") || "Staff").trim();
}

function claimConflictError(record) {
  var name = String(record.get("claimedByDisplayName") || "another staff member").trim();
  var err = new Error("This request is already claimed by " + name + ".");
  err.statusCode = 409;
  return err;
}

function forbiddenClaimError(message) {
  var err = new Error(message);
  err.statusCode = 403;
  return err;
}

function staffTitleRequestAction(e) {
  try {
    var context = titleRequestActionContext(e);
    if (context.response) {
      return context.response;
    }

    if (context.nextStatus === records.STATUS.PENDING_HOLD && !String(context.data.bibid || "").trim()) {
      return e.json(400, { message: "BIB ID is required before moving this suggestion to Pending hold." });
    }

    var bibActionResponse = prepareTitleRequestBibAction(e, context);
    if (bibActionResponse) {
      return bibActionResponse;
    }

    finalizeTitleRequestCloseReason(e.app, context);
    context.record = records.updateTitleRequest(e.app, context.id, context.data, context.staff.get("username"));
    if (context.formatChanged) {
      formatClaimRules.applyFormatClaimRule(e.app, context.record, {
        trigger: "format_changed",
        previousFormat: context.originalFormat,
        actorName: context.staff.get("username") || "system"
      });
      context.record = e.app.findRecordById("title_requests", context.id);
    }
    applyCatalogFoundWorkflow(e.app, context.record, context.data, context.staff);
    maybeRunImmediatePromoter(e.app, context);
    handleAlreadyOwnOrRejectSideEffects(e.app, context);

    var purchaseReminderEmail = sendPurchaseReminderIfRequested(e.app, context);
    var response = records.titleRequestToJson(context.record, e.app);
    response.purchaseReminderEmail = purchaseReminderEmail;

    e.app.logger().info("Staff action succeeded", "recordId", context.id, "action", context.action, "nextStatus", context.nextStatus);
    return e.json(200, response);
  } catch (err) {
    e.app.logger().error("Staff action failed", "error", String(err), "recordId", e.request.pathValue("id"));
    return e.json(400, { message: "System error: " + err.message });
  }
}

function applyCatalogFoundWorkflow(app, record, data, staff) {
  var action = String(data && data.action || "").trim();
  var bibId = String(data && data.bibid || "").trim();
  if (action !== "catalogFound" || !bibId) {
    return;
  }

  records.addWorkflowTagForRequest(app, record, "Identifier found");
  records.appendSystemNote(
    record,
    "Staff selected Polaris BIB " + bibId + "; request queued for hold placement."
  );

  records.setCanonicalRefs(app, record);
  app.save(record);
}

function titleRequestActionContext(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = e.request.pathValue("id");
  var data = routeUtils.body(e);
  var action = String(data.action || "");
  var nextStatus = records.normalizeStatus(data.status);
  var record;

  try {
    record = e.app.findRecordById("title_requests", id);
  } catch (findErr) {
    return {
      response: e.json(404, { message: "Suggestion not found: " + id })
    };
  }

  var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
  if (accessError) {
    return { response: accessError };
  }

  var oldStatus = records.normalizeStatus(record.get("status"));
  var originalIdentifier = String(record.get("identifier") || "").trim();
  var nextIdentifier = data.identifier !== undefined && data.identifier !== null
    ? String(data.identifier).trim()
    : originalIdentifier;
  var originalFormat = records.normalizeFormat(record.get("format"));
  var nextFormat = data.format !== undefined && data.format !== null
    ? records.normalizeFormat(data.format)
    : originalFormat;
  var originalBibId = String(record.get("bibid") || "").trim();
  var nextBibId = data.bibid !== undefined && data.bibid !== null
    ? String(data.bibid).trim()
    : originalBibId;

  return {
    response: null,
    staff: staff,
    id: id,
    data: data,
    action: action,
    record: record,
    oldStatus: oldStatus,
    nextStatus: nextStatus,
    isClosingRequest: nextStatus === records.STATUS.CLOSED,
    isDuplicateClose: action === "closeDuplicate",
    isActiveHoldTarget: nextStatus === records.STATUS.PENDING_HOLD || nextStatus === records.STATUS.HOLD_PLACED || action === "alreadyOwn",
    duplicateCloseNoteAdded: false,
    originalFormat: originalFormat,
    formatChanged: nextFormat !== originalFormat,
    shouldRunImmediatePromoter: (!!nextIdentifier && nextIdentifier !== originalIdentifier) || (!!nextBibId && nextBibId !== originalBibId)
  };
}

function prepareTitleRequestBibAction(e, context) {
  if (!context.data.bibid) {
    return null;
  }

  var bibid = String(context.data.bibid).trim();
  var barcode = context.record.get("barcode");
  var staffAuth = staffActionPolarisAuth(e.app);
  var duplicateResponse = handleDuplicateBibRequest(e, context, bibid);
  if (duplicateResponse) {
    return duplicateResponse;
  }

  reconcileBibAction(e.app, context, staffAuth, bibid);
  handleHoldTransitionForBibAction(e.app, context, staffAuth, bibid, barcode);
  return null;
}

function staffActionPolarisAuth(app) {
  try {
    return polaris.adminStaffAuth();
  } catch (err) {
    app.logger().warn("Polaris auth failed", "error", String(err));
  }
}

function handleDuplicateBibRequest(e, context, bibid) {
  var existing = e.app.findRecordsByFilter("title_requests",
    "barcode = {:barcode} && bibid = {:bibid} && id != {:id} && status != 'closed'",
    "", 1, 0, { barcode: context.record.get("barcode"), bibid: bibid, id: context.id });
  if (!existing || !existing.length) {
    return null;
  }

  records.addWorkflowTagForRequest(e.app, context.record, "Hold exists (same patron)");
  if (context.isDuplicateClose) {
    markDuplicateClose(context);
    return null;
  }

  if (wouldCreateActiveDuplicate(context, bibid)) {
    e.app.save(context.record);
    return e.json(409, {
      code: "duplicate_open_request",
      message: "This patron already has an open request for this BIB ID. This request was flagged; close it as a duplicate if it should not continue.",
      duplicate: records.duplicateContext(existing[0], "bibid")
    });
  }

  return null;
}

function markDuplicateClose(context) {
  context.data.status = records.STATUS.CLOSED;
  context.nextStatus = records.STATUS.CLOSED;
  context.data.closeReason = records.CLOSE_REASON.DUPLICATE_HOLD;
  records.appendSystemNote(context.record, "Closed as duplicate because this patron already has an open request or hold for the same BIB ID.");
  context.duplicateCloseNoteAdded = true;
}

function wouldCreateActiveDuplicate(context, bibid) {
  var oldIsActiveHold = context.oldStatus === records.STATUS.PENDING_HOLD || context.oldStatus === records.STATUS.HOLD_PLACED;
  var bibidChanged = String(context.record.get("bibid") || "").trim() !== bibid;
  return context.isActiveHoldTarget && (!oldIsActiveHold || bibidChanged || context.action === "alreadyOwn");
}

function reconcileBibAction(app, context, staffAuth, bibid) {
  if (context.isDuplicateClose || context.isClosingRequest) {
    return;
  }

  var beforeTitle = String(context.record.get("title") || "");
  var beforeAuthor = String(context.record.get("author") || "");
  polaris.reconcileRecord(app, staffAuth, context.record, bibid, {
    bibId: context.data.selectedPolarisBibId,
    title: context.data.selectedPolarisTitle,
    author: context.data.selectedPolarisAuthor,
    identifier: context.data.selectedPolarisIdentifier,
    publication: context.data.selectedPolarisPublication,
    format: context.data.selectedPolarisFormat
  });
  var reconciledTitle = String(context.record.get("title") || "");
  var reconciledAuthor = String(context.record.get("author") || "");
  if (reconciledTitle !== beforeTitle) {
    context.data.title = reconciledTitle;
  }
  if (reconciledAuthor !== beforeAuthor) {
    context.data.author = reconciledAuthor;
  }
}

function handleHoldTransitionForBibAction(app, context, staffAuth, bibid, barcode) {
  if (context.nextStatus !== records.STATUS.PENDING_HOLD && !(context.oldStatus === records.STATUS.OUTSTANDING_PURCHASE && context.data.bibid)) {
    return;
  }

  if (context.record.getBool("autohold") === false) {
    closeAutoholdOptOutBibAction(context);
    return;
  }

  if (context.nextStatus === records.STATUS.PENDING_HOLD) {
    maybePromoteExistingPolarisHold(app, context, staffAuth, bibid, barcode);
  }
}

function closeAutoholdOptOutBibAction(context) {
  context.nextStatus = records.STATUS.CLOSED;
  context.data.status = records.STATUS.CLOSED;
  context.data.closeReason = records.CLOSE_REASON.PURCHASED_NO_HOLD;
  var optOutReason = (context.action === "alreadyOwn")
    ? "Closed without hold because 'Already Own' was selected and patron opted out of automatic hold placement."
    : "Closed without hold because BIB ID was entered and patron opted out of automatic hold placement.";
  records.appendSystemNote(context.record, optOutReason);
}

function maybePromoteExistingPolarisHold(app, context, staffAuth, bibid, barcode) {
  try {
    var pPatron = polaris.lookupPatron(staffAuth, barcode);
    if (pPatron && pPatron.PatronID) {
      if (polaris.patronHasHoldForBib(staffAuth, barcode, bibid)) {
        context.nextStatus = records.STATUS.HOLD_PLACED;
        context.data.status = context.nextStatus;
        records.appendSystemNote(context.record, "Patron already has a hold in Polaris for this BIB ID. Moving directly to Hold placed.");
      }
    }
  } catch (polarisErr) {
    app.logger().warn("Polaris duplicate hold check failed during staff action", "error", String(polarisErr));
  }
}

function finalizeTitleRequestCloseReason(app, context) {
  if (context.nextStatus === records.STATUS.CLOSED && (context.action === "reject" || context.action === "silentClose")) {
    context.data.closeReason = (context.action === "silentClose") ? records.CLOSE_REASON.SILENT : records.CLOSE_REASON.REJECTED;
  }
  if (context.nextStatus === records.STATUS.CLOSED && context.isDuplicateClose) {
    context.data.closeReason = records.CLOSE_REASON.DUPLICATE_HOLD;
    records.addWorkflowTagForRequest(app, context.record, "Hold exists (same patron)");
    if (!context.duplicateCloseNoteAdded) {
      records.appendSystemNote(context.record, "Closed as duplicate because this patron already has an open request or hold for the same BIB ID.");
    }
  }
  if (context.nextStatus === records.STATUS.CLOSED && !context.data.closeReason && context.record.getBool("autohold") === false && context.data.bibid) {
    context.data.closeReason = records.CLOSE_REASON.PURCHASED_NO_HOLD;
  }
  if (context.nextStatus !== records.STATUS.CLOSED) {
    context.data.closeReason = "";
  }
}

function maybeRunImmediatePromoter(app, context) {
  if (!context.shouldRunImmediatePromoter) {
    return;
  }

  try {
    var updatedStatus = records.normalizeStatus(context.record.get("status"));
    if (updatedStatus === records.STATUS.SUGGESTION || config.suggestionLimit(app, "").autoPromote !== false) {
      jobs.promoteRequestNow(app, polaris.adminStaffAuth(), context.record);
      context.record = app.findRecordById("title_requests", context.record.id);
    }
  } catch (promoteErr) {
    app.logger().error("Immediate identifier promotion failed", "recordId", context.record.id, "error", String(promoteErr));
  }
}

function handleAlreadyOwnOrRejectSideEffects(app, context) {
  if (context.action !== "alreadyOwn" && context.action !== "reject") {
    return;
  }

  var patron = refreshedActionPatron(app, context.record);
  if (context.action === "alreadyOwn") {
    handleAlreadyOwnSideEffects(app, context, patron);
  }
  if (context.action === "reject") {
    sendRejectedActionEmail(app, context, patron);
  }
}

function refreshedActionPatron(app, record) {
  try {
    return polaris.lookupPatron(polaris.adminStaffAuth(), record.get("barcode"));
  } catch (err) {
    app.logger().warn("Could not refresh patron data for staff action email", "recordId", record.id, "error", String(err));
  }
  return null;
}

function handleAlreadyOwnSideEffects(app, context, patron) {
  var bibid = String(context.data.bibid || "").trim();
  if (bibid && patron && patron.PatronID) {
    placeAlreadyOwnedHold(app, context.record, bibid, patron);
  }
  sendAlreadyOwnedActionEmail(app, context.record, patron);
}

function placeAlreadyOwnedHold(app, record, bibid, patron) {
  var localStaffAuth;
  try {
    localStaffAuth = polaris.adminStaffAuth();
  } catch (e) { }

  try {
    polaris.reconcileRecord(app, localStaffAuth, record, bibid);
  } catch (reconcileErr) {
    app.logger().error("Already-owned reconcile failed during staff action", "recordId", record.id, "bibid", bibid, "error", String(reconcileErr));
  }

  if (record.getBool("autohold") === false) {
    records.appendSystemNote(record, "Skipped auto-hold for 'Already Own' action because patron opted out of automatic hold placement.");
    app.save(record);
    return;
  }

  try {
    polaris.placeHold(localStaffAuth, bibid, patron.PatronID, false);
    records.appendSystemNote(record, "Auto-placed hold for patron since item is already owned (BIB " + bibid + ")");
  } catch (holdErr) {
    app.logger().error("Auto-hold failed during alreadyOwn action", "recordId", record.id, "bibid", bibid, "error", String(holdErr));
  }
}

function sendAlreadyOwnedActionEmail(app, record, patron) {
  try {
    if (!mail.alreadyOwned(app, record, patron)) {
      routeUtils.noteSkippedEmail(app, record);
    }
  } catch (mailErr) {
    app.logger().error("Already-owned email failed", "recordId", record.id, "error", String(mailErr));
  }
}

function sendRejectedActionEmail(app, context, patron) {
  try {
    if (!mail.rejected(app, context.record, patron, context.data.rejectionTemplateId)) {
      routeUtils.noteSkippedEmail(app, context.record);
    }
  } catch (mailErr) {
    app.logger().error("Rejected suggestion email failed", "recordId", context.record.id, "error", String(mailErr));
  }
}

function sendPurchaseReminderIfRequested(app, context) {
  var purchaseReminderEmail = {
    requested: context.action === "purchase" && context.data.emailPurchaseReminder === true,
    sent: false,
    message: ""
  };

  if (!purchaseReminderEmail.requested) {
    return purchaseReminderEmail;
  }

  var staffEmail = String(context.staff.get("weekly_action_summary_email") || "").trim();
  if (!staffEmail) {
    purchaseReminderEmail.message = "Purchase saved. Add an email address to your staff profile to email yourself purchase reminders.";
    return purchaseReminderEmail;
  }

  try {
    purchaseReminderEmail.sent = !!mail.purchaseReminder(app, context.record, context.staff, staffEmail, routeUtils.staffRequestUrl(app, context.record));
    purchaseReminderEmail.message = purchaseReminderEmail.sent
      ? "Purchase saved and reminder email sent."
      : "Purchase saved, but email notifications are not configured.";
  } catch (mailErr) {
    app.logger().error("Purchase reminder email failed", "recordId", context.record.id, "staffUserId", context.staff.id, "error", String(mailErr));
    purchaseReminderEmail.message = "Purchase saved, but the reminder email could not be sent.";
  }
  return purchaseReminderEmail;
}

function staffDeleteClosedRequest(e) {
  var staff = routeUtils.requireAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Admin access required." });
  }

  var id = e.request.pathValue("id");
  var record;
  try {
    record = e.app.findRecordById("title_requests", id);
  } catch (err) {
    return e.json(404, { message: "Closed request not found." });
  }

  var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
  if (accessError) {
    return accessError;
  }

  if (records.normalizeStatus(record.get("status")) !== records.STATUS.CLOSED) {
    return e.json(400, { message: "Only closed requests can be deleted." });
  }

  try {
    records.deleteTitleRequestWithAudit(e.app, record, staff, "single");
    return e.json(200, { success: true });
  } catch (err2) {
    return e.json(400, { message: err2.message || "Could not delete closed request." });
  }
}

function staffDeleteClosedRequestsBulk(e) {
  var staff = routeUtils.requireAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Admin access required." });
  }

  var data = routeUtils.body(e);
  if (String(data.confirm || "") !== "DELETE") {
    return e.json(400, { message: "Type DELETE to confirm bulk deletion." });
  }

  try {
    var deleted = records.deleteClosedRequestsBulk(e.app, staff, data.confirm);
    return e.json(200, { success: true, deleted: deleted });
  } catch (err) {
    return e.json(400, { message: err.message || "Could not delete closed requests." });
  }
}

module.exports = {
  staffTitleRequestsList: staffTitleRequestsList,
  titleRequestListScope: titleRequestListScope,
  fetchTitleRequestPage: fetchTitleRequestPage,
  preloadPatronsForTitleRequests: preloadPatronsForTitleRequests,
  preloadWorkflowTagsForRequests: preloadWorkflowTagsForRequests,
  statusIdByCodeMap: statusIdByCodeMap,
  requestPhaseEntryFallback: requestPhaseEntryFallback,
  preloadPhaseEntryTimesForRequests: preloadPhaseEntryTimesForRequests,
  preloadPhaseEntryTimesBatch: preloadPhaseEntryTimesBatch,
  collectMissingPatronIds: collectMissingPatronIds,
  cachePatronBatch: cachePatronBatch,
  buildStaffTitleRequestRow: buildStaffTitleRequestRow,
  sortableTime: sortableTime,
  sortTitleRequestRowsByPhaseEntry: sortTitleRequestRowsByPhaseEntry,
  cachedPatronForTitleRequest: cachedPatronForTitleRequest,
  titleRequestPatronId: titleRequestPatronId,
  enrichRowWithPatron: enrichRowWithPatron,
  enrichRowWithPickupBranch: enrichRowWithPickupBranch,
  cachedPickupBranchName: cachedPickupBranchName,
  titleRequestListResponseScope: titleRequestListResponseScope,
  staffClaimTitleRequest: staffClaimTitleRequest,
  staffUnclaimTitleRequest: staffUnclaimTitleRequest,
  mutateTitleRequestClaim: mutateTitleRequestClaim,
  runClaimMutation: runClaimMutation,
  claimTitleRequest: claimTitleRequest,
  unclaimTitleRequest: unclaimTitleRequest,
  staffClaimDisplayName: staffClaimDisplayName,
  claimConflictError: claimConflictError,
  forbiddenClaimError: forbiddenClaimError,
  staffTitleRequestAction: staffTitleRequestAction,
  applyCatalogFoundWorkflow: applyCatalogFoundWorkflow,
  titleRequestActionContext: titleRequestActionContext,
  prepareTitleRequestBibAction: prepareTitleRequestBibAction,
  staffActionPolarisAuth: staffActionPolarisAuth,
  handleDuplicateBibRequest: handleDuplicateBibRequest,
  markDuplicateClose: markDuplicateClose,
  wouldCreateActiveDuplicate: wouldCreateActiveDuplicate,
  reconcileBibAction: reconcileBibAction,
  handleHoldTransitionForBibAction: handleHoldTransitionForBibAction,
  closeAutoholdOptOutBibAction: closeAutoholdOptOutBibAction,
  maybePromoteExistingPolarisHold: maybePromoteExistingPolarisHold,
  finalizeTitleRequestCloseReason: finalizeTitleRequestCloseReason,
  maybeRunImmediatePromoter: maybeRunImmediatePromoter,
  handleAlreadyOwnOrRejectSideEffects: handleAlreadyOwnOrRejectSideEffects,
  refreshedActionPatron: refreshedActionPatron,
  handleAlreadyOwnSideEffects: handleAlreadyOwnSideEffects,
  placeAlreadyOwnedHold: placeAlreadyOwnedHold,
  sendAlreadyOwnedActionEmail: sendAlreadyOwnedActionEmail,
  sendRejectedActionEmail: sendRejectedActionEmail,
  sendPurchaseReminderIfRequested: sendPurchaseReminderIfRequested,
  staffDeleteClosedRequest: staffDeleteClosedRequest,
  staffDeleteClosedRequestsBulk: staffDeleteClosedRequestsBulk
};
