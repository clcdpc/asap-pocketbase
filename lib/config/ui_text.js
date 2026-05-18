const dbHelpers = require("./db_helpers.js");
const normalization = require("./normalization.js");
const defaults = require("./defaults.js");
const workflows = require("./workflows.js");

const systemRecord = dbHelpers.systemRecord;
const orgIdForSettings = dbHelpers.orgIdForSettings;
const safeCollection = dbHelpers.safeCollection;

const parseJsonObject = normalization.parseJsonObject;
const normalizeOptionList = normalization.normalizeOptionList;
const enabledOptionLabels = normalization.enabledOptionLabels;

const defaultDuplicateStatusLabels = defaults.defaultDuplicateStatusLabels;

const workflowFromRecord = workflows.workflowFromRecord;
const workflowRecord = workflows.workflowRecord;

let _formatRules;
function getFormatRules() {
  if (!_formatRules) _formatRules = require(`${__hooks}/../lib/format_rules.js`);
  return _formatRules;
}

function uiRecord(app, orgId) {
  app = app || $app;
  var orgRecordId = orgIdForSettings(app, orgId);
  if (orgRecordId) {
    try {
      return app.findFirstRecordByFilter("ui_settings", "scope = 'library' && libraryOrganization = {:org}", { org: orgRecordId });
    } catch (err) {
      if (typeof $app !== "undefined" && $app && $app.logger) {
        $app.logger().warn("Swallowed error", "error", String(err));
      }
    }
  }
  return systemRecord(app, "ui_settings", "uisettings00010", {
    scope: "system",
    logoAlt: "Library Logo",
    pageTitle: "Material Suggestion",
    barcodeLabel: "Library Card",
    pinLabel: "Pin",
    successTitle: "Suggestion Submitted",
    misconfiguredMessage: "The {{library}} suggestion system is currently misconfigured. Please contact staff."
  });
}

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

function patronSettingsOverrideRecord(app, orgId) {
  app = app || $app;
  var requestedOrgId = String(orgId || "").trim();
  if (!requestedOrgId || !safeCollection(app, "patron_settings_overrides")) return null;
  try {
    return app.findFirstRecordByFilter("patron_settings_overrides", "orgId = {:orgId}", { orgId: requestedOrgId });
  } catch (err) {
    return null;
  }
}

function legacyPatronLibrarySettingsRecord(app, orgId) {
  app = app || $app;
  var orgRecordId = orgIdForSettings(app, orgId);
  if (!orgRecordId || !safeCollection(app, "patron_library_settings")) return null;
  try {
    return app.findFirstRecordByFilter("patron_library_settings", "libraryOrganization = {:org}", { org: orgRecordId });
  } catch (err) {
    return null;
  }
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

function materialFormats(app, orgId) {
  app = app || $app;
  var labels = {};
  var rules = {};
  var available = [];
  var formatOrderList = [];
  try {
    var systemRows = app.findRecordsByFilter("material_formats", "scope = 'system'", "sortOrder", 200, 0, {});
    var libraryRows = [];
    var orgRecordId = orgIdForSettings(app, orgId);
    if (orgRecordId) {
      libraryRows = app.findRecordsByFilter("material_formats", "scope = 'library' && libraryOrganization = {:org}", "sortOrder", 200, 0, { org: orgRecordId });
    }
    var mergedByCode = {};
    var sourceOrderByCode = {};

    function assignSourceOrder(code, row, sourcePriority) {
      var sortOrder = row.getInt("sortOrder") || 0;
      var label = String(row.get("label") || code || "").toLowerCase();
      sourceOrderByCode[code] = { sortOrder: sortOrder, sourcePriority: sourcePriority, label: label, code: code };
    }

    function upsertRow(row, isOverride) {
      var code = String(row.get("code") || "").trim();
      if (!code) return;
      if (!mergedByCode[code]) mergedByCode[code] = { code: code };
      var target = mergedByCode[code];
      if (!isOverride || row.get("label")) target.label = row.get("label") || code;
      target.enabled = row.getBool("enabled");
      target.messageBehavior = row.get("messageBehavior") || "none";
      target.titleMode = row.get("titleMode") || "required";
      target.titleLabel = row.get("titleLabel") || "Title";
      target.authorMode = row.get("authorMode") || "required";
      target.authorLabel = row.get("authorLabel") || "Author";
      target.identifierMode = row.get("identifierMode") || "optional";
      target.identifierLabel = row.get("identifierLabel") || "Identifier number";
      target.publicationMode = row.get("publicationMode") || "required";
      target.publicationLabel = row.get("publicationLabel") || "Publication Timing";
      target.sortOrder = row.getInt("sortOrder") || 0;
      assignSourceOrder(code, row, isOverride ? 1 : 0);
    }

    for (var i = 0; i < systemRows.length; i++) upsertRow(systemRows[i], false);
    for (var j = 0; j < libraryRows.length; j++) upsertRow(libraryRows[j], true);

    var effectiveRows = Object.keys(mergedByCode).map(function (code) {
      return mergedByCode[code];
    });
    effectiveRows.sort(function (a, b) {
      if (a.sortOrder !== b.sortOrder) return a.sortOrder - b.sortOrder;
      var ao = sourceOrderByCode[a.code];
      var bo = sourceOrderByCode[b.code];
      if (ao && bo && ao.sourcePriority !== bo.sourcePriority) return ao.sourcePriority - bo.sourcePriority;
      var aLabel = String(a.label || a.code || "").toLowerCase();
      var bLabel = String(b.label || b.code || "").toLowerCase();
      if (aLabel < bLabel) return -1;
      if (aLabel > bLabel) return 1;
      return String(a.code).localeCompare(String(b.code));
    });

    for (var k = 0; k < effectiveRows.length; k++) {
      var r = effectiveRows[k];
      var code = r.code;
      formatOrderList.push(code);
      labels[code] = r.label || code;

      if (r.enabled) available.push(code);
      rules[code] = {
        messageBehavior: r.messageBehavior,
        fields: {
          title: { mode: r.titleMode, label: r.titleLabel },
          author: { mode: r.authorMode, label: r.authorLabel },
          identifier: { mode: r.identifierMode, label: r.identifierLabel },
          publication: { mode: r.publicationMode, label: r.publicationLabel }
        }
      };
    }
  } catch (err) {
    if (typeof $app !== "undefined" && $app && $app.logger) {
      $app.logger().warn("Swallowed error", "error", String(err));
    }
  }
  return { labels: labels, rules: rules, available: available, order: formatOrderList };
}

function mergeDuplicateStatusLabels(labels) {
  return Object.assign(defaultDuplicateStatusLabels(), parseJsonObject(labels, labels || {}));
}

function duplicateStatusLabels(app, orgId) {
  return mergeDuplicateStatusLabels(duplicateStatusLabelResolution(app || $app, orgId).labels);
}

function uiTextFromRecord(app, record, orgId) {
  const fRules = getFormatRules();
  var overrideRecord = orgId ? patronSettingsOverrideRecord(app, orgId) : null;

  var logoUrl = "/jpl.png";
  var logoAlt = "Library Logo";
  var brandingSource = "system";

  try {
    var logo = record.get("logo");
    var logoId = record.id;
    var alt = record.get("logoAlt");
    var isLibrary = orgId && record.get("scope") === "library";

    if (!logo && isLibrary) {
      var systemRecord = uiRecord(app, "");
      logo = systemRecord.get("logo");
      logoId = systemRecord.id;
    } else if (logo && isLibrary) {
      brandingSource = "library";
    }

    if (!alt && isLibrary) {
      var systemRecordForAlt = uiRecord(app, "");
      alt = systemRecordForAlt.get("logoAlt");
    }

    if (logo) {
      logoUrl = "/api/files/ui_settings/" + logoId + "/" + logo;
      logoAlt = alt || "Library Logo";
    }
  } catch (err) {
    if (typeof $app !== "undefined" && $app && $app.logger) {
      $app.logger().warn("Swallowed error", "error", String(err));
    }
  }

  var systemUiRecord = orgId ? uiRecord(app, "") : record;
  var globalPublicationOptions = normalizeOptionList(systemUiRecord.get("publicationOptions"), ["Already published", "Coming soon", "Published a while back"]);
  var publicationOptions = overrideRecord && overrideRecord.get("publicationOptions")
    ? normalizeOptionList(overrideRecord.get("publicationOptions"), enabledOptionLabels(globalPublicationOptions))
    : globalPublicationOptions;
  var formats = materialFormats(app, orgId);
  var overrideFormatRules = null;
  if (overrideRecord && overrideRecord.get("patronFormatRules")) {
    overrideFormatRules = parseJsonObject(overrideRecord.get("patronFormatRules"), {});
  }

  var duplicateResolution = duplicateStatusLabelResolution(app, orgId, orgId ? null : record);

  return {
    logoUrl: logoUrl,
    logoAlt: logoAlt,
    brandingSource: brandingSource,
    brandingInherited: brandingSource === "system" && !!orgId,
    pageTitle: record.get("pageTitle") || "Material Suggestion",
    barcodeLabel: record.get("barcodeLabel") || "Library Card",
    pinLabel: record.get("pinLabel") || "Pin",
    loginPrompt: record.get("loginPrompt") || "Please enter your information below to start the suggestion process.",
    suggestionFormNote: record.get("suggestionFormNote") || "If the library decides to purchase your suggestion, we will automatically place a hold on it and send a confirmation email. Make sure to check your spam folder if you don't see the email.",
    loginNote: record.get("loginNote") || "Use of this service requires a valid library card. Contact your library if you need assistance with your card or PIN.",
    successTitle: record.get("successTitle") || "Suggestion Submitted",
    successMessage: record.get("successMessage") || "You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>",
    alreadySubmittedMessage: record.get("alreadySubmittedMessage") || "This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library's suggestion service.</div>",
    duplicateStatusLabels: duplicateResolution.labels,
    duplicateStatusLabelsSource: duplicateResolution.source,
    duplicateStatusLabelsInherited: duplicateResolution.inherited,
    noEmailMessage: record.get("noEmailMessage") || "No email is specified on your library account, which means we won't be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.",
    systemNotEnabledMessage: systemUiRecord.get("systemNotEnabledMessage") || "{{library}} does not currently participate in this suggestion service.",
    misconfiguredMessage: systemUiRecord.get("misconfiguredMessage") || "The {{library}} suggestion system is currently misconfigured. Please contact staff.",
    ebookMessage: overrideRecord && overrideRecord.get("ebookMessage") ? overrideRecord.get("ebookMessage") : (record.get("ebookMessage") || "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>"),
    eaudiobookMessage: overrideRecord && overrideRecord.get("eaudiobookMessage") ? overrideRecord.get("eaudiobookMessage") : (record.get("eaudiobookMessage") || "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>"),
    publicationOptions: publicationOptions,
    formatLabels: formats.labels,
    formatOrder: formats.order,
    availableFormats: formats.available,
    formatRules: fRules.normalizeFormatRules(overrideFormatRules || formats.rules),
    patronSettingsInherited: !!orgId && !overrideRecord,
    allowPatronAutoholdOptOut: workflowFromRecord(workflowRecord(app, orgId)).allowPatronAutoholdOptOut
  };
}

function uiText(app, orgId) {
  app = app || $app;
  return uiTextFromRecord(app, uiRecord(app, orgId), orgId);
}

module.exports = {
  uiRecord: uiRecord,
  duplicateStatusLabelsFromUiRecord: duplicateStatusLabelsFromUiRecord,
  hasAnyDuplicateStatusLabel: hasAnyDuplicateStatusLabel,
  patronSettingsOverrideRecord: patronSettingsOverrideRecord,
  legacyPatronLibrarySettingsRecord: legacyPatronLibrarySettingsRecord,
  duplicateStatusLabelResolution: duplicateStatusLabelResolution,
  materialFormats: materialFormats,
  mergeDuplicateStatusLabels: mergeDuplicateStatusLabels,
  duplicateStatusLabels: duplicateStatusLabels,
  uiTextFromRecord: uiTextFromRecord,
  uiText: uiText,
};
