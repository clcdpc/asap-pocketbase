const config = require(`${__hooks}/../lib/config.js`);
const formatRules = require(`${__hooks}/../lib/format_rules.js`);
const jobs = require(`${__hooks}/../lib/jobs.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);
const httpUtils = require(`${__hooks}/../lib/http_utils.js`);
const htmlUtils = require(`${__hooks}/../lib/html_utils.js`);
const authz = require(`${__hooks}/../lib/authz.js`);

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
  var getVal = function(obj, key) {
    if (!obj) return "";
    if (typeof obj.get === "function") return obj.get(key) || "";
    return obj[key] || "";
  };

  var data = {
    duplicate_date: formatDuplicateDate(getVal(duplicate, "created")),
    duplicate_status: statusLabel,
    duplicate_title: getVal(duplicate, "title"),
    duplicate_author: getVal(duplicate, "author"),
    duplicate_format: formatLabelForDuplicate(getVal(duplicate, "format"), uiText),
    duplicate_match_type: duplicateMatchLabel(getVal(duplicate, "matchType")),
    // Aliases
    title: getVal(duplicate, "title"),
    author: getVal(duplicate, "author"),
    format: formatLabelForDuplicate(getVal(duplicate, "format"), uiText)
  };
  return template.replace(/{{(\w+)}}/g, function (match, key) {
    return Object.prototype.hasOwnProperty.call(data, key) ? htmlUtils.escapeHtml(data[key]) : match;
  });
}

function duplicateConflictResponse(e, err, uiText) {
  var duplicate = err.duplicate || null;
  var message = duplicate ? renderDuplicateMessage(uiText, duplicate) : (uiText.alreadySubmittedMessage || (err.message ? htmlUtils.escapeHtml(err.message) : ""));
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

function buildPolarisData(data) {
  var source = data && data.polaris ? data.polaris : (data || {});
  return {
    host: String(httpUtils.firstValue(source, ["host", "polarisHost"], "") || "").trim(),
    accessId: String(httpUtils.firstValue(source, ["accessId", "polarisAccessId"], "") || "").trim(),
    apiKey: String(httpUtils.firstValue(source, ["apiKey", "polarisApiKey"], "") || ""),
    staffDomain: String(httpUtils.firstValue(source, ["staffDomain", "polarisStaffDomain"], "") || "").trim(),
    adminUser: String(httpUtils.firstValue(source, ["adminUser", "polarisAdminUser"], "") || "").trim(),
    adminPassword: String(httpUtils.firstValue(source, ["adminPassword", "polarisAdminPassword"], "") || ""),
    overridePassword: String(httpUtils.firstValue(source, ["overridePassword", "polarisOverridePassword"], "") || ""),
    langId: String(httpUtils.firstValue(source, ["langId"], "1033") || "1033"),
    appId: String(httpUtils.firstValue(source, ["appId"], "100") || "100"),
    orgId: String(httpUtils.firstValue(source, ["orgId"], "1") || "1"),
    pickupOrgId: String(httpUtils.firstValue(source, ["pickupOrgId"], "0") || "0"),
    requestingOrgId: String(httpUtils.firstValue(source, ["requestingOrgId"], "3") || "3"),
    workstationId: String(httpUtils.firstValue(source, ["workstationId"], "1") || "1"),
    userId: String(httpUtils.firstValue(source, ["userId"], "1") || "1")
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

module.exports = Object.assign(
  {},
  httpUtils,
  htmlUtils,
  authz,
  {
    isIsbnCapableFormat: isIsbnCapableFormat,
    applyIsbnCheckStatusForCreate: applyIsbnCheckStatusForCreate,
    runImmediateSubmissionIdentifierLookup: runImmediateSubmissionIdentifierLookup,
    formatDuplicateDate: formatDuplicateDate,
    duplicateStatusKey: duplicateStatusKey,
    formatLabelForDuplicate: formatLabelForDuplicate,
    appendQuery: appendQuery,
    staffRequestUrl: staffRequestUrl,
    duplicateMatchLabel: duplicateMatchLabel,
    renderDuplicateMessage: renderDuplicateMessage,
    duplicateConflictResponse: duplicateConflictResponse,
    noteSkippedEmail: noteSkippedEmail,
    buildPolarisData: buildPolarisData,
    missingPolarisTestFields: missingPolarisTestFields,
    testPolarisConnection: testPolarisConnection,
  }
);
