
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);

function staffAnalytics(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var selectedScope = String(routeUtils.queryValue(e, "scope") || routeUtils.queryValue(e, "orgId") || "").trim();
  var dateRangeKey = String(routeUtils.queryValue(e, "range") || "last30").trim();
  var scope = resolveAnalyticsScope(e.app, staff, selectedScope);
  var dateRange = resolveAnalyticsDateRange(dateRangeKey);

  if (!scope.canRead) {
    return e.json(200, emptyAnalyticsResponse(scope, dateRange));
  }

  var rows = fetchAnalyticsRecords(e.app, scope);
  var firstHoldPlacedTimes = loadFirstHoldPlacedEventTimes(e.app, rows);
  return e.json(200, {
    scope: analyticsScopeResponse(e.app, staff, scope),
    dateRange: {
      key: dateRange.key,
      start: dateRange.start.toISOString(),
      end: dateRange.end.toISOString()
    },
    availableLibraries: routeUtils.isSuperAdmin(staff) ? orgs.analyticsLibraryOptions(e.app) : [],
    summary: loadAnalyticsSummary(scope, dateRange, rows, firstHoldPlacedTimes),
    stageCounts: loadStageCounts(scope, rows),
    closedReasons: loadClosedReasonBreakdown(scope, dateRange, rows),
    aging: loadAgingMetrics(scope, rows),
    exceptions: loadExceptionCounts(e.app, scope, dateRange, rows)
  });
}

function resolveAnalyticsScope(app, staff, selectedOrgId) {
  var isSuper = routeUtils.isSuperAdmin(staff);
  var staffLibraryOrgId = String(staff.get("libraryOrgId") || "").trim();
  var cleanSelected = String(selectedOrgId || "").trim();

  if (isSuper && (cleanSelected === "all" || cleanSelected === "system" || (!cleanSelected && !staffLibraryOrgId))) {
    return {
      canRead: true,
      mode: "all",
      libraryOrgId: "",
      filter: "id != ''",
      params: {}
    };
  }

  var libraryOrgId = isSuper ? (cleanSelected || staffLibraryOrgId) : staffLibraryOrgId;
  if (!libraryOrgId) {
    return {
      canRead: false,
      mode: "library",
      libraryOrgId: "",
      filter: "",
      params: {}
    };
  }

  return {
    canRead: true,
    mode: "library",
    libraryOrgId: libraryOrgId,
    filter: "libraryOrgId = {:libraryOrgId}",
    params: { libraryOrgId: libraryOrgId }
  };
}

function resolveAnalyticsDateRange(key, now) {
  var end = now ? new Date(now) : new Date();
  var start = new Date(end.getTime());
  var cleanKey = key === "thisMonth" || key === "last90" ? key : "last30";

  if (cleanKey === "thisMonth") {
    start = new Date(end.getFullYear(), end.getMonth(), 1);
  } else {
    start.setDate(start.getDate() - (cleanKey === "last90" ? 90 : 30));
  }

  return {
    key: cleanKey,
    start: start,
    end: end
  };
}

function fetchAnalyticsRecords(app, scope) {
  var rows = [];
  var limit = 500;
  var offset = 0;

  while (true) {
    var page = app.findRecordsByFilter("title_requests", scope.filter, "-created", limit, offset, scope.params);
    if (!page.length) {
      break;
    }
    rows = rows.concat(page);
    if (page.length < limit) {
      break;
    }
    offset += limit;
  }

  offset = 0;
  while (true) {
    var copyPage = app.findRecordsByFilter("additional_copy_requests", scope.filter, "-created", limit, offset, scope.params);
    if (!copyPage.length) {
      break;
    }
    for (var i = 0; i < copyPage.length; i++) {
      var copyStatus = records.normalizeStatus(copyPage[i].get("status"));
      if (copyStatus !== records.STATUS.CLOSED) {
        copyPage[i].set("status", "additional_copies");
      }
    }
    rows = rows.concat(copyPage);
    if (copyPage.length < limit) {
      break;
    }
    offset += limit;
  }

  return rows;
}

function loadAnalyticsSummary(scope, dateRange, rows, firstHoldPlacedTimes) {
  var newSuggestions = 0;
  var openRequests = 0;
  var closedRequests = 0;
  var heldRequests = 0;
  var holdAgeDays = 0;
  var holdTimes = firstHoldPlacedTimes || {};

  for (var i = 0; i < rows.length; i++) {
    var row = rows[i];
    var rawStatus = String(row.get("status") || "").trim();
    var status = rawStatus === "additional_copies" ? "additional_copies" : records.normalizeStatus(rawStatus);
    if (status !== records.STATUS.CLOSED) {
      openRequests++;
    }
    if (dateInRange(row.get("created") || row.created, dateRange)) {
      newSuggestions++;
    }
    if (status === records.STATUS.CLOSED && dateInRange(row.get("updated") || row.updated, dateRange)) {
      closedRequests++;
    }
    var firstHoldPlacedAt = holdTimes[row.id];
    if (firstHoldPlacedAt && dateInRange(firstHoldPlacedAt, dateRange)) {
      heldRequests++;
      holdAgeDays += daysBetween(row.get("created") || row.created, firstHoldPlacedAt);
    }
  }

  return {
    newSuggestions: newSuggestions,
    openRequests: openRequests,
    closedRequests: closedRequests,
    heldRequests: heldRequests,
    averageDaysToHold: heldRequests ? holdAgeDays / heldRequests : 0
  };
}

function loadFirstHoldPlacedEventTimes(app, rows) {
  var requestIds = [];
  var requestById = {};
  var firstByRequest = {};

  for (var i = 0; i < rows.length; i++) {
    if (rows[i] && rows[i].id) {
      requestIds.push(rows[i].id);
      requestById[rows[i].id] = rows[i];
    }
  }

  if (!requestIds.length) {
    return firstByRequest;
  }

  var batchSize = 100;
  for (var b = 0; b < requestIds.length; b += batchSize) {
    loadFirstHoldPlacedEventTimesBatch(app, requestIds.slice(b, b + batchSize), requestById, firstByRequest);
  }

  return firstByRequest;
}

function loadFirstHoldPlacedEventTimesBatch(app, requestIds, requestById, firstByRequest) {
  var filterParts = [];
  var params = {};
  for (var i = 0; i < requestIds.length; i++) {
    var key = "r" + i;
    filterParts.push("titleRequest = {:" + key + "}");
    params[key] = requestIds[i];
  }

  var offset = 0;
  var limit = 200;
  while (true) {
    var events = app.findRecordsByFilter("title_request_events", filterParts.join(" || "), "id", limit, offset, params);
    if (!events || !events.length) {
      break;
    }

    for (var j = 0; j < events.length; j++) {
      var event = events[j];
      if (String(event.get("eventType") || "").trim() !== "hold_placed") {
        continue;
      }
      var requestId = String(event.get("titleRequest") || "").trim();
      if (!requestId) {
        continue;
      }
      var eventTime = holdPlacedEventTime(event, requestById[requestId]);
      if (eventTime && (!firstByRequest[requestId] || compareAnalyticsDates(eventTime, firstByRequest[requestId]) < 0)) {
        firstByRequest[requestId] = eventTime;
      }
    }

    if (events.length < limit) {
      break;
    }
    offset += limit;
  }
}

function holdPlacedEventTime(event, requestRecord) {
  var eventTime = event.get("created") || event.created || "";
  if (eventTime) {
    return eventTime;
  }

  if (requestRecord && records.normalizeStatus(requestRecord.get("status")) === records.STATUS.HOLD_PLACED) {
    return requestRecord.get("updated") || requestRecord.updated || "";
  }

  return "";
}

function compareAnalyticsDates(a, b) {
  var dateA = parseAnalyticsDate(a);
  var dateB = parseAnalyticsDate(b);
  if (!dateA && !dateB) return 0;
  if (!dateA) return 1;
  if (!dateB) return -1;
  return dateA.getTime() - dateB.getTime();
}

function loadStageCounts(scope, rows) {
  var counts = {};
  counts[records.STATUS.SUGGESTION] = 0;
  counts[records.STATUS.OUTSTANDING_PURCHASE] = 0;
  counts[records.STATUS.PENDING_HOLD] = 0;
  counts[records.STATUS.HOLD_PLACED] = 0;
  counts[records.STATUS.CLOSED] = 0;
  counts["additional_copies"] = 0;

  for (var i = 0; i < rows.length; i++) {
    var rawStatus = String(rows[i].get("status") || "").trim();
    var status = rawStatus === "additional_copies" ? "additional_copies" : records.normalizeStatus(rawStatus);
    if (counts[status] !== undefined) {
      counts[status]++;
    }
  }

  return counts;
}

function loadClosedReasonBreakdown(scope, dateRange, rows) {
  var counts = {};
  for (var i = 0; i < rows.length; i++) {
    var row = rows[i];
    if (records.normalizeStatus(row.get("status")) !== records.STATUS.CLOSED) {
      continue;
    }
    if (!dateInRange(row.get("updated") || row.updated, dateRange)) {
      continue;
    }
    var reason = String(row.get("closeReason") || "").trim() || "unrecorded";
    counts[reason] = (counts[reason] || 0) + 1;
  }
  return Object.keys(counts).sort().map(function (reason) {
    return { reason: reason, count: counts[reason] };
  });
}

function loadAgingMetrics(scope, rows, now) {
  var thresholdDays = 30;
  var byStage = {};
  var openOlderThanThreshold = 0;
  var current = now ? new Date(now) : new Date();
  var openStages = [records.STATUS.SUGGESTION, records.STATUS.OUTSTANDING_PURCHASE, records.STATUS.PENDING_HOLD, records.STATUS.HOLD_PLACED, "additional_copies"];

  for (var s = 0; s < openStages.length; s++) {
    byStage[openStages[s]] = { status: openStages[s], count: 0, totalAgeDays: 0 };
  }

  for (var i = 0; i < rows.length; i++) {
    var row = rows[i];
    var rawStatus = String(row.get("status") || "").trim();
    var status = rawStatus === "additional_copies" ? "additional_copies" : records.normalizeStatus(rawStatus);
    if (status === records.STATUS.CLOSED || !byStage[status]) {
      continue;
    }
    var ageDays = daysBetween(row.get("created") || row.created, current);
    if (ageDays > thresholdDays) {
      openOlderThanThreshold++;
    }
    byStage[status].count++;
    byStage[status].totalAgeDays += ageDays;
  }

  return {
    thresholdDays: thresholdDays,
    openOlderThanThreshold: openOlderThanThreshold,
    averageAgeByStage: openStages.map(function (status) {
      var row = byStage[status];
      return {
        status: status,
        count: row.count,
        averageAgeDays: row.count ? row.totalAgeDays / row.count : 0
      };
    })
  };
}

function loadExceptionCounts(app, scope, dateRange, rows) {
  var holdFailures = 0;
  var identifierFailures = 0;

  var tagsByRequestId = records.workflowTagsForRequests(app, rows);

  for (var i = 0; i < rows.length; i++) {
    var row = rows[i];
    var isbnStatus = String(row.get("isbnCheckStatus") || "").trim();
    if (isbnStatus === "error" || isbnStatus === "error_max_retries") {
      identifierFailures++;
    }
    var tags = tagsByRequestId[row.id] || [];
    for (var j = 0; j < tags.length; j++) {
      var tag = String(tags[j] || "").toLowerCase();
      if (tag.indexOf("hold failed") === 0) {
        holdFailures++;
        break;
      }
    }
  }

  return {
    holdFailures: holdFailures,
    identifierFailures: identifierFailures
  };
}

function analyticsScopeResponse(app, staff, scope) {
  var label = "All libraries";
  if (scope.mode === "library") {
    label = orgs.analyticsLibraryLabel(app, scope.libraryOrgId) || staff.get("libraryOrgName") || scope.libraryOrgId || "Current library";
  }
  return {
    mode: scope.mode,
    libraryOrgId: scope.libraryOrgId,
    label: label,
    superAdmin: routeUtils.isSuperAdmin(staff)
  };
}


function emptyAnalyticsResponse(scope, dateRange) {
  return {
    scope: {
      mode: scope.mode,
      libraryOrgId: scope.libraryOrgId,
      label: "No library assigned",
      superAdmin: false
    },
    dateRange: {
      key: dateRange.key,
      start: dateRange.start.toISOString(),
      end: dateRange.end.toISOString()
    },
    availableLibraries: [],
    summary: { newSuggestions: 0, openRequests: 0, closedRequests: 0, heldRequests: 0, averageDaysToHold: 0 },
    stageCounts: loadStageCounts(scope, []),
    closedReasons: [],
    aging: loadAgingMetrics(scope, []),
    exceptions: { holdFailures: 0, identifierFailures: 0 }
  };
}

function dateInRange(value, dateRange) {
  var date = parseAnalyticsDate(value);
  return !!date && date >= dateRange.start && date <= dateRange.end;
}

function daysBetween(startValue, endValue) {
  var start = parseAnalyticsDate(startValue);
  var end = parseAnalyticsDate(endValue);
  if (!start || !end || end < start) {
    return 0;
  }
  return (end.getTime() - start.getTime()) / 86400000;
}

function parseAnalyticsDate(value) {
  if (value instanceof Date) {
    return value;
  }
  var text = String(value || "").trim();
  if (!text) {
    return null;
  }
  var normalized = text.indexOf(" ") > -1 ? text.replace(" ", "T") : text;
  var date = new Date(normalized);
  if (isNaN(date.getTime())) {
    date = new Date(text);
  }
  return isNaN(date.getTime()) ? null : date;
}



module.exports = {
  staffAnalytics,
  resolveAnalyticsScope,
  resolveAnalyticsDateRange,
  fetchAnalyticsRecords,
  loadAnalyticsSummary,
  loadFirstHoldPlacedEventTimes,
  loadFirstHoldPlacedEventTimesBatch,
  holdPlacedEventTime,
  compareAnalyticsDates,
  loadStageCounts,
  loadClosedReasonBreakdown,
  loadAgingMetrics,
  loadExceptionCounts,
  analyticsScopeResponse: analyticsScopeResponse,
  emptyAnalyticsResponse: emptyAnalyticsResponse,
};
