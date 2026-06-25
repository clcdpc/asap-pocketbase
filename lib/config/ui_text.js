const customFields = require("../custom_fields.js");
const normalization = require("./normalization.js");
const workflows = require("./workflows.js");

const {
  uiRecord,
  patronSettingsOverrideRecord,
  legacyPatronLibrarySettingsRecord
} = require("./ui-text/records.js");

const {
  duplicateStatusLabelsFromUiRecord,
  hasAnyDuplicateStatusLabel,
  duplicateStatusLabelResolution,
  mergeDuplicateStatusLabels,
  duplicateStatusLabels
} = require("./ui-text/duplicate-labels.js");

const { materialFormats } = require("./ui-text/formats.js");

const normalizeOptionList = normalization.normalizeOptionList;
const enabledOptionLabels = normalization.enabledOptionLabels;
const parseJsonObject = normalization.parseJsonObject;

const workflowFromRecord = workflows.workflowFromRecord;
const workflowRecord = workflows.workflowRecord;

let _formatRules;
function getFormatRules() {
  if (!_formatRules) _formatRules = require(`${__hooks}/../lib/format_rules.js`);
  return _formatRules;
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
  var additionalFieldDefinitions = overrideRecord
    ? customFields.normalizeDefinitions(overrideRecord.get("additionalFieldDefinitions"))
    : [];

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
    suggestionFormNote: record.get("suggestionFormNote") || "If the library approves your suggestion for purchase, we will email you while it is awaiting ordering and cataloging. Once the item is available in the catalog, we will automatically place a hold when possible and send another update.",
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
    formatRules: fRules.normalizeFormatRules(overrideFormatRules || formats.rules, additionalFieldDefinitions),
    additionalFieldDefinitions: additionalFieldDefinitions,
    patronSettingsInherited: !!orgId && !overrideRecord,
    allowPatronAutoholdOptOut: workflowFromRecord(workflowRecord(app, orgId)).allowPatronAutoholdOptOut
  };
}

function uiText(app, orgId) {
  app = app || $app;
  return uiTextFromRecord(app, uiRecord(app, orgId), orgId);
}

module.exports = {
  uiRecord, duplicateStatusLabelsFromUiRecord, hasAnyDuplicateStatusLabel,
  patronSettingsOverrideRecord, legacyPatronLibrarySettingsRecord,
  duplicateStatusLabelResolution, materialFormats,
  mergeDuplicateStatusLabels, duplicateStatusLabels,
  uiTextFromRecord, uiText
};
