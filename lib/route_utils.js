const config = require(`${__hooks}/../lib/config.js`);
const formatRules = require(`${__hooks}/../lib/format_rules.js`);
const jobs = require(`${__hooks}/../lib/jobs.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);
const httpUtils = require(`${__hooks}/../lib/http_utils.js`);
const htmlUtils = require(`${__hooks}/../lib/html_utils.js`);
const authz = require(`${__hooks}/../lib/authz.js`);
const duplicateUtils = require(`${__hooks}/../lib/duplicate_messages.js`);
const polarisConfigUtils = require(`${__hooks}/../lib/polaris_config_utils.js`);

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

function noteSkippedEmail(app, record) {
  mail.noteSkipped(app, record);
}

module.exports = Object.assign(
  {},
  httpUtils,
  htmlUtils,
  authz,
  duplicateUtils,
  polarisConfigUtils,
  {
    isIsbnCapableFormat: isIsbnCapableFormat,
    applyIsbnCheckStatusForCreate: applyIsbnCheckStatusForCreate,
    runImmediateSubmissionIdentifierLookup: runImmediateSubmissionIdentifierLookup,
    appendQuery: appendQuery,
    staffRequestUrl: staffRequestUrl,
    noteSkippedEmail: noteSkippedEmail,
  }
);
