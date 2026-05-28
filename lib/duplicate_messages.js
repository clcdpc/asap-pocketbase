const config = require(`${__hooks}/../lib/config.js`);
const records = require(`${__hooks}/../lib/records.js`);
const htmlUtils = require(`${__hooks}/../lib/html_utils.js`);

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

module.exports = {
  formatDuplicateDate: formatDuplicateDate,
  duplicateStatusKey: duplicateStatusKey,
  formatLabelForDuplicate: formatLabelForDuplicate,
  duplicateMatchLabel: duplicateMatchLabel,
  renderDuplicateMessage: renderDuplicateMessage,
  duplicateConflictResponse: duplicateConflictResponse
};
