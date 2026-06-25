const defaults = require("../defaults.js");
const normalization = require("../normalization.js");
const { uiRecord, patronSettingsOverrideRecord, legacyPatronLibrarySettingsRecord } = require("./records.js");

const parseJsonObject = normalization.parseJsonObject;
const defaultDuplicateStatusLabels = defaults.defaultDuplicateStatusLabels;

function duplicateStatusLabelsFromUiRecord(record) {
  record = record || {};
  function get(name) {
    try {
      return record.get ? record.get(name) : "";
    } catch (err) {
      return "";
    }
  }
  return {
    suggestion: get("duplicateLabelSuggestion"),
    outstanding_purchase: get("duplicateLabelOutstandingPurchase"),
    pending_hold: get("duplicateLabelPendingHold"),
    hold_placed: get("duplicateLabelHoldPlaced"),
    closed: get("duplicateLabelClosed"),
    rejected: get("duplicateLabelRejected"),
    hold_completed: get("duplicateLabelHoldCompleted"),
    hold_not_picked_up: get("duplicateLabelHoldNotPickedUp"),
    manual: get("duplicateLabelManual"),
    silent: get("duplicateLabelSilent"),
    "Silently Closed": get("duplicateLabelSilent")
  };
}

function hasAnyDuplicateStatusLabel(labels) {
  labels = labels || {};
  var keys = Object.keys(labels);
  for (var i = 0; i < keys.length; i++) {
    if (String(labels[keys[i]] || "").trim()) return true;
  }
  return false;
}

function duplicateStatusLabelResolution(app, orgId, systemUiRecord) {
  app = app || $app;
  var defs = defaultDuplicateStatusLabels();
  var sysRecord = systemUiRecord || uiRecord(app, "");

  var rawGlobalLabels = duplicateStatusLabelsFromUiRecord(sysRecord);
  var globalLabels = Object.assign({}, defs, rawGlobalLabels);
  var requestedOrgId = String(orgId || "").trim();

  if (!requestedOrgId) {
    return {
      labels: globalLabels,
      source: hasAnyDuplicateStatusLabel(rawGlobalLabels) ? "global" : "default",
      inherited: false
    };
  }

  var libraryLabels = null;
  var overrideRecord = patronSettingsOverrideRecord(app, requestedOrgId);

  if (overrideRecord) {
    var parsedOverride = parseJsonObject(overrideRecord.get("duplicateStatusLabels"), {});
    if (hasAnyDuplicateStatusLabel(parsedOverride)) {
      libraryLabels = parsedOverride;
    }
  } else {
    var libraryRecord = legacyPatronLibrarySettingsRecord(app, requestedOrgId);
    if (libraryRecord) {
      libraryLabels = parseJsonObject(libraryRecord.get("duplicateRequestStatusLabels"), {});
    }
  }

  if (libraryLabels) {
    return {
      labels: Object.assign({}, globalLabels, libraryLabels),
      source: "library",
      inherited: false
    };
  }

  return {
    labels: globalLabels,
    source: "global",
    inherited: true
  };
}

function mergeDuplicateStatusLabels(labels) {
  return Object.assign(defaultDuplicateStatusLabels(), parseJsonObject(labels, labels || {}));
}

function duplicateStatusLabels(app, orgId) {
  return mergeDuplicateStatusLabels(duplicateStatusLabelResolution(app || $app, orgId).labels);
}

module.exports = { duplicateStatusLabelsFromUiRecord, hasAnyDuplicateStatusLabel, duplicateStatusLabelResolution, mergeDuplicateStatusLabels, duplicateStatusLabels };
