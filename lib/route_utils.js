const config = require(`${__hooks}/../lib/config.js`);
const formatRules = require(`${__hooks}/../lib/format_rules.js`);
const jobs = require(`${__hooks}/../lib/jobs.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);

function body(e) {
  return e.requestInfo().body || {};
}

function requestHeader(e, name) {
  var lower = String(name || "").toLowerCase();
  var info = {};
  try {
    info = e.requestInfo() || {};
  } catch (err) { }
  var headers = info.headers || {};
  if (headers) {
    if (typeof headers.get === "function") {
      return headers.get(name) || headers.get(lower) || "";
    }
    return headers[name] || headers[lower] || "";
  }
  try {
    if (e.request && e.request.header && typeof e.request.header.get === "function") {
      return e.request.header.get(name) || e.request.header.get(lower) || "";
    }
  } catch (err2) { }
  try {
    if (e.request && e.request.headers && typeof e.request.headers.get === "function") {
      return e.request.headers.get(name) || e.request.headers.get(lower) || "";
    }
  } catch (err3) { }
  return "";
}

function queryValue(e, name) {
  var info = {};
  try {
    info = e.requestInfo() || {};
  } catch (err) { }

  if (info.query) {
    if (typeof info.query.get === "function") {
      var fromGet = info.query.get(name);
      if (fromGet !== undefined && fromGet !== null) {
        return String(fromGet);
      }
    }
    if (info.query[name] !== undefined && info.query[name] !== null) {
      return String(info.query[name]);
    }
  }

  var urls = [];
  if (info.url) {
    urls.push(String(info.url));
  }
  try {
    if (e.request && e.request.url) {
      urls.push(String(e.request.url));
    }
  } catch (err) { }
  try {
    if (e.request && e.request.URL) {
      urls.push(String(e.request.URL));
    }
  } catch (err) { }

  for (var i = 0; i < urls.length; i++) {
    var value = queryValueFromUrl(urls[i], name);
    if (value !== "") {
      return value;
    }
  }

  return "";
}

function queryValueFromUrl(url, name) {
  var marker = "?";
  var queryIndex = url.indexOf(marker);
  if (queryIndex < 0) {
    return "";
  }
  var query = url.slice(queryIndex + 1).split("#")[0];
  var parts = query.split("&");
  for (var i = 0; i < parts.length; i++) {
    var pair = parts[i].split("=");
    if (decodeURIComponent(pair[0] || "") === name) {
      return decodeURIComponent((pair.slice(1).join("=") || "").replace(/\+/g, " "));
    }
  }
  return "";
}

function parseJsonObject(value, fallback) {
  fallback = fallback || {};
  if (value && typeof value === "object" && !Array.isArray(value)) {
    return value;
  }
  if (typeof value !== "string") {
    return fallback;
  }
  var text = value.trim();
  if (!text) {
    return fallback;
  }
  try {
    var parsed = JSON.parse(text);
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
      return parsed;
    }
    if (typeof parsed === "string") {
      var nested = JSON.parse(parsed);
      if (nested && typeof nested === "object" && !Array.isArray(nested)) {
        return nested;
      }
    }
  } catch (err) {
    return fallback;
  }
  return fallback;
}

function requireAuth(e, collectionName) {
  var auth = e.requestInfo().auth;
  if (!auth || !auth.collection || auth.collection().name !== collectionName) {
    throw new UnauthorizedError("Unauthorized");
  }
  return auth;
}

function requireAdminStaff(e) {
  var staff = requireAuth(e, "staff_users");
  if (!isAdminRole(staff)) {
    return null;
  }
  return staff;
}

function requireSuperAdminStaff(e) {
  var staff = requireAuth(e, "staff_users");
  if (!isSuperAdmin(staff)) {
    return null;
  }
  return staff;
}

function isSuperAdmin(staff) {
  return String(staff.get("role") || "").toLowerCase() === "super_admin";
}

function isAdminRole(staff) {
  var role = String(staff.get("role") || "").toLowerCase();
  return role === "admin" || role === "super_admin";
}

function sameLibrary(staff, libraryOrgId) {
  if (isSuperAdmin(staff)) {
    return true;
  }
  var staffLibraryOrgId = String(staff.get("libraryOrgId") || "").trim();
  libraryOrgId = String(libraryOrgId || "").trim();
  return !!staffLibraryOrgId && !!libraryOrgId && staffLibraryOrgId === libraryOrgId;
}

function canAccessTitleRequest(staff, record) {
  return sameLibrary(staff, record.get("libraryOrgId"));
}

function requireTitleRequestAccess(e, staff, record) {
  if (!canAccessTitleRequest(staff, record)) {
    return e.json(404, { message: "Suggestion not found." });
  }
  return null;
}

function isIsbnCapableFormat(format, uiText) {
  var rules = formatRules.normalizeFormatRules(uiText && uiText.formatRules);
  var key = String(format || "book").trim() || "book";
  var rule = rules[key] || rules.book || {};
  var fields = rule.fields || {};
  var identifier = fields.identifier || {};
  var mode = String(identifier.mode || "optional");
  return mode === "required" || mode === "optional";
}

function applyIsbnCheckStatusForCreate(data, uiText) {
  var identifier = String(data.identifier || data.isbn || "").trim();
  if (!identifier) {
    data.isbnCheckStatus = "skipped_no_isbn";
    return;
  }
  if (isIsbnCapableFormat(data.format, uiText)) {
    data.isbnCheckStatus = "pending";
    return;
  }
  data.isbnCheckStatus = "skipped_no_isbn";
}

function runImmediateSubmissionIdentifierLookup(e, record) {
  if (!record || !String(record.get("identifier") || "").trim()) {
    return record;
  }
  try {
    jobs.promoteRequestNow(e.app, polaris.adminStaffAuth(), record);
    return e.app.findRecordById("title_requests", record.id);
  } catch (err) {
    e.app.logger().error("Immediate submission identifier lookup failed", "recordId", record.id, "error", String(err));
    return record;
  }
}

function escapeHtml(value) {
  return String(value === undefined || value === null ? "" : value)
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function formatDuplicateDate(value) {
  var text = String(value || "").trim();
  if (!text) {
    return "";
  }
  var date = new Date(text.replace(" ", "T"));
  if (isNaN(date.getTime())) {
    return text;
  }
  return date.toLocaleDateString("en-US", {
    year: "numeric",
    month: "long",
    day: "numeric"
  });
}

function duplicateStatusKey(duplicate) {
  duplicate = duplicate || {};
  var status = String(duplicate.status || "").trim();
  var closeReason = String(duplicate.closeReason || "").trim();
  if (status === records.STATUS.CLOSED && closeReason) {
    return closeReason;
  }
  return status || records.STATUS.SUGGESTION;
}

function formatLabelForDuplicate(format, uiText) {
  var labels = (uiText && uiText.formatLabels) || {};
  return labels[format] || String(format || "");
}

function appendQuery(url, params) {
  url = String(url || "").trim();
  if (!url) return "";
  var hash = "";
  var hashIndex = url.indexOf("#");
  if (hashIndex >= 0) {
    hash = url.slice(hashIndex);
    url = url.slice(0, hashIndex);
  }
  var separator = url.indexOf("?") >= 0 ? "&" : "?";
  var parts = [];
  Object.keys(params || {}).forEach(function (key) {
    var value = params[key];
    if (value !== undefined && value !== null && String(value) !== "") {
      parts.push(encodeURIComponent(key) + "=" + encodeURIComponent(String(value)));
    }
  });
  return parts.length ? url + separator + parts.join("&") + hash : url + hash;
}

function staffRequestUrl(app, record) {
  var base = config.staffUrl(app);
  var stage = records.normalizeStatus(record.get("status"));
  return appendQuery(base, { stage: stage, request: record.id });
}

function duplicateMatchLabel(matchType) {
  var labels = {
    identifier: "identifier number",
    title_format: "title and format",
    bibid: "catalog record"
  };
  return labels[matchType] || "suggestion";
}

function renderDuplicateMessage(uiText, duplicate) {
  uiText = uiText || {};
  duplicate = duplicate || {};
  var labels = config.defaultDuplicateStatusLabels ? config.defaultDuplicateStatusLabels() : {};
  labels = Object.assign({}, labels, uiText.duplicateStatusLabels || {});
  var statusKey = duplicateStatusKey(duplicate);
  var statusLabel = labels[statusKey] || labels[duplicate.status] || labels.closed || "Submitted";
  var template = uiText.alreadySubmittedMessage || "This suggestion has already been submitted from your account.";
  var data = {
    duplicate_date: formatDuplicateDate(duplicate.created),
    duplicate_status: statusLabel,
    duplicate_title: duplicate.title || "",
    duplicate_author: duplicate.author || "",
    duplicate_format: formatLabelForDuplicate(duplicate.format, uiText),
    duplicate_match_type: duplicateMatchLabel(duplicate.matchType)
  };
  return template.replace(/{{(\w+)}}/g, function (match, key) {
    return Object.prototype.hasOwnProperty.call(data, key) ? escapeHtml(data[key]) : match;
  });
}

function duplicateConflictResponse(e, err, uiText) {
  var duplicate = err.duplicate || null;
  var message = duplicate ? renderDuplicateMessage(uiText, duplicate) : (uiText.alreadySubmittedMessage || (err.message ? escapeHtml(err.message) : ""));
  return e.json(409, {
    message: err.message,
    conflictTitle: "Already Submitted",
    conflictMessage: message,
    duplicate: duplicate
  });
}

function noteSkippedEmail(app, record) {
  mail.noteSkipped(app, record);
}

function firstValue(source, names, defaultValue) {
  for (var i = 0; i < names.length; i++) {
    var value = source[names[i]];
    if (value !== undefined && value !== null) {
      return value;
    }
  }
  return defaultValue;
}

function boolValue(value, defaultValue) {
  if (value === undefined || value === null || value === "") {
    return defaultValue;
  }
  if (value === true || value === false) {
    return value;
  }
  var normalized = String(value).trim().toLowerCase();
  if (normalized === "true" || normalized === "1" || normalized === "on" || normalized === "yes") {
    return true;
  }
  if (normalized === "false" || normalized === "0" || normalized === "off" || normalized === "no") {
    return false;
  }
  return !!value;
}

function buildPolarisData(data) {
  var source = data && data.polaris ? data.polaris : (data || {});
  return {
    host: String(firstValue(source, ["host", "polarisHost"], "") || "").trim(),
    accessId: String(firstValue(source, ["accessId", "polarisAccessId"], "") || "").trim(),
    apiKey: String(firstValue(source, ["apiKey", "polarisApiKey"], "") || ""),
    staffDomain: String(firstValue(source, ["staffDomain", "polarisStaffDomain"], "") || "").trim(),
    adminUser: String(firstValue(source, ["adminUser", "polarisAdminUser"], "") || "").trim(),
    adminPassword: String(firstValue(source, ["adminPassword", "polarisAdminPassword"], "") || ""),
    overridePassword: String(firstValue(source, ["overridePassword", "polarisOverridePassword"], "") || ""),
    langId: String(firstValue(source, ["langId"], "1033") || "1033"),
    appId: String(firstValue(source, ["appId"], "100") || "100"),
    orgId: String(firstValue(source, ["orgId"], "1") || "1"),
    pickupOrgId: String(firstValue(source, ["pickupOrgId"], "0") || "0"),
    requestingOrgId: String(firstValue(source, ["requestingOrgId"], "3") || "3"),
    workstationId: String(firstValue(source, ["workstationId"], "1") || "1"),
    userId: String(firstValue(source, ["userId"], "1") || "1"),
    autoPromote: boolValue(firstValue(source, ["autoPromote"], true), true)
  };
}

function missingPolarisTestFields(polarisData) {
  var missing = [];
  if (!polarisData.host) missing.push("host");
  if (!polarisData.accessId) missing.push("access ID");
  if (!polarisData.apiKey) missing.push("API key");
  if (!polarisData.adminUser) missing.push("system staff username");
  if (!polarisData.adminPassword) missing.push("system staff password");
  return missing;
}

function testPolarisConnection(e, polarisData) {
  var missing = missingPolarisTestFields(polarisData);
  if (missing.length) {
    return e.json(400, {
      success: false,
      message: "Missing Polaris " + missing.join(", ") + "."
    });
  }

  try {
    var auth = polaris.adminStaffAuth(polarisData);
    if (auth && auth.AccessToken) {
      return e.json(200, { success: true, message: "Polaris API connection successful!" });
    }
    return e.json(400, { success: false, message: "Authentication failed without an explicit error." });
  } catch (err) {
    return e.json(400, { success: false, message: err.message || String(err) });
  }
}

module.exports = {
  body: body,
  requestHeader: requestHeader,
  queryValue: queryValue,
  queryValueFromUrl: queryValueFromUrl,
  parseJsonObject: parseJsonObject,
  requireAuth: requireAuth,
  requireAdminStaff: requireAdminStaff,
  requireSuperAdminStaff: requireSuperAdminStaff,
  isSuperAdmin: isSuperAdmin,
  isAdminRole: isAdminRole,
  sameLibrary: sameLibrary,
  canAccessTitleRequest: canAccessTitleRequest,
  requireTitleRequestAccess: requireTitleRequestAccess,
  isIsbnCapableFormat: isIsbnCapableFormat,
  applyIsbnCheckStatusForCreate: applyIsbnCheckStatusForCreate,
  runImmediateSubmissionIdentifierLookup: runImmediateSubmissionIdentifierLookup,
  escapeHtml: escapeHtml,
  formatDuplicateDate: formatDuplicateDate,
  duplicateStatusKey: duplicateStatusKey,
  formatLabelForDuplicate: formatLabelForDuplicate,
  appendQuery: appendQuery,
  staffRequestUrl: staffRequestUrl,
  duplicateMatchLabel: duplicateMatchLabel,
  renderDuplicateMessage: renderDuplicateMessage,
  duplicateConflictResponse: duplicateConflictResponse,
  noteSkippedEmail: noteSkippedEmail,
  firstValue: firstValue,
  boolValue: boolValue,
  buildPolarisData: buildPolarisData,
  missingPolarisTestFields: missingPolarisTestFields,
  testPolarisConnection: testPolarisConnection,
};
