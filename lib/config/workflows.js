const dbHelpers = require("./db_helpers.js");
const normalization = require("./normalization.js");

const systemRecord = dbHelpers.systemRecord;
const orgIdForSettings = dbHelpers.orgIdForSettings;
const safeCollection = dbHelpers.safeCollection;
const clampInteger = normalization.clampInteger;
const envInteger = normalization.envInteger;

function defaultWorkflowValues() {
  return {
    scope: "system",
    suggestionLimit: 5,
    outstandingTimeoutDays: 30,
    outstandingTimeoutEnabled: false,
    outstandingTimeoutSendEmail: false,
    outstandingTimeoutRejectionTemplate: "",
    holdPickupTimeoutDays: 14,
    holdPickupTimeoutEnabled: false,
    pendingHoldTimeoutDays: 14,
    pendingHoldTimeoutEnabled: false,
    additionalCopyTimeoutDays: 14,
    additionalCopyTimeoutEnabled: false,
    autoPromote: false,
    commonAuthorsEnabled: false,
    commonAuthorsList: "",
    commonAuthorsLabel: "Popular Creators",
    commonAuthorsHelp: "See if this is a creator we already collect.",
    commonAuthorsMessage: "We automatically purchase all upcoming titles by this creator. Please check the catalog to place a hold on 'On Order' items.",
    allowPatronAutoholdOptOut: true,
    // System default with library override: a library can opt in to accepting any valid Polaris card for its patron experience.
    allowAnyRegisteredCardLogin: false,
    // System default with library override: restrict patron access by Polaris PatronCodeID when enabled.
    patronCodeEligibilityEnabled: false,
    allowedPatronCodeIds: "",
    patronCodeEligibilityMessage: "Your library card is not eligible to use this suggestion service.",
    externalSearch1Enabled: true,
    externalSearch1Label: "Search Amazon",
    externalSearch1UrlTemplate: "https://www.amazon.com/s?k={{title}}",
    externalSearch2Enabled: true,
    externalSearch2Label: "Search Goodreads",
    externalSearch2UrlTemplate: "https://www.goodreads.com/search?q={{title}}",
    externalSearch3Enabled: true,
    externalSearch3Label: "Search WorldCat",
    externalSearch3UrlTemplate: "https://www.worldcat.org/search?q={{title}}",
    externalSearch4Enabled: false,
    externalSearch4Label: "",
    externalSearch4UrlTemplate: ""
  };
}

function systemWorkflowRecord(app) {
  return systemRecord(app, "workflow_settings", "workflow0000010", defaultWorkflowValues());
}

function libraryWorkflowRecord(app, orgId) {
  var orgRecordId = orgIdForSettings(app, orgId);
  if (!orgRecordId) return null;
  try {
    return app.findFirstRecordByFilter("workflow_settings", "scope = 'library' && libraryOrganization = {:org}", { org: orgRecordId });
  } catch (err) {
    return null;
  }
}

function hasScopedWorkflowValue(record, key) {
  if (!record) return false;
  try {
    var val = record.get(key);
    return val !== null && val !== undefined && String(val).trim() !== "";
  } catch (err) {
    return false;
  }
}

function effectiveWorkflowRecord(app, orgId) {
  app = app || $app;
  var sysRecord = systemWorkflowRecord(app);
  var libRecord = orgId ? libraryWorkflowRecord(app, orgId) : null;

  return {
    get: function (key) {
      if (libRecord && hasScopedWorkflowValue(libRecord, key)) {
        return libRecord.get(key);
      }
      return sysRecord ? sysRecord.get(key) : null;
    },
    getBool: function (key) {
      if (libRecord && hasScopedWorkflowValue(libRecord, key)) {
        return libRecord.getBool(key);
      }
      return sysRecord ? sysRecord.getBool(key) : false;
    },
    getInt: function (key) {
      if (libRecord && hasScopedWorkflowValue(libRecord, key)) {
        return libRecord.getInt(key);
      }
      return sysRecord ? sysRecord.getInt(key) : 0;
    }
  };
}

function workflowRecord(app, orgId) {
  return effectiveWorkflowRecord(app || $app, orgId);
}

function workflowFromRecord(record) {
  return {
    suggestionLimit: record.getInt("suggestionLimit") || 5,
    suggestionLimitMessage: record.get("suggestionLimitMessage") || "Weekly suggestion limit reached. You can try again after {{next_available_date}}.",
    outstandingTimeoutEnabled: record.getBool("outstandingTimeoutEnabled"),
    outstandingTimeoutDays: record.getInt("outstandingTimeoutDays") || 30,
    outstandingTimeoutSendEmail: record.getBool("outstandingTimeoutSendEmail"),
    outstandingTimeoutRejectionTemplateId: String(record.get("outstandingTimeoutRejectionTemplate") || ""),
    holdPickupTimeoutEnabled: record.getBool("holdPickupTimeoutEnabled"),
    holdPickupTimeoutDays: record.getInt("holdPickupTimeoutDays") || 14,
    pendingHoldTimeoutEnabled: record.getBool("pendingHoldTimeoutEnabled"),
    pendingHoldTimeoutDays: record.getInt("pendingHoldTimeoutDays") || 14,
    additionalCopyTimeoutEnabled: record.getBool("additionalCopyTimeoutEnabled"),
    additionalCopyTimeoutDays: record.getInt("additionalCopyTimeoutDays") || 14,
    autoPromote: !!record.getBool("autoPromote"),
    commonAuthorsEnabled: record.getBool("commonAuthorsEnabled"),
    commonAuthorsList: record.get("commonAuthorsList") || "",
    commonAuthorsLabel: record.get("commonAuthorsLabel") || "Popular Creators",
    commonAuthorsHelp: record.get("commonAuthorsHelp") || "See if this is a creator we already collect.",
    commonAuthorsMessage: record.get("commonAuthorsMessage") || "We automatically purchase all upcoming titles by this creator. Please check the catalog to place a hold on 'On Order' items.",
    allowPatronAutoholdOptOut: record.getBool("allowPatronAutoholdOptOut"),
    allowAnyRegisteredCardLogin: record.getBool("allowAnyRegisteredCardLogin"),
    patronCodeEligibilityEnabled: record.getBool("patronCodeEligibilityEnabled"),
    allowedPatronCodeIds: record.get("allowedPatronCodeIds") || "",
    patronCodeEligibilityMessage: record.get("patronCodeEligibilityMessage") || "Your library card is not eligible to use this suggestion service.",
    externalSearch1Enabled: record.getBool("externalSearch1Enabled"),
    externalSearch1Label: record.get("externalSearch1Label") || "Search Amazon",
    externalSearch1UrlTemplate: record.get("externalSearch1UrlTemplate") || "https://www.amazon.com/s?k={{title}}",
    externalSearch2Enabled: record.getBool("externalSearch2Enabled"),
    externalSearch2Label: record.get("externalSearch2Label") || "Search Goodreads",
    externalSearch2UrlTemplate: record.get("externalSearch2UrlTemplate") || "https://www.goodreads.com/search?q={{title}}",
    externalSearch3Enabled: record.getBool("externalSearch3Enabled"),
    externalSearch3Label: record.get("externalSearch3Label") || "Search WorldCat",
    externalSearch3UrlTemplate: record.get("externalSearch3UrlTemplate") || "https://www.worldcat.org/search?q={{title}}",
    externalSearch4Enabled: record.getBool("externalSearch4Enabled"),
    externalSearch4Label: record.get("externalSearch4Label") || "",
    externalSearch4UrlTemplate: record.get("externalSearch4UrlTemplate") || ""
  };
}

function workflowSettings(app, orgId) {
  return workflowFromRecord(workflowRecord(app || $app, orgId));
}

function suggestionLimit(app, orgId) {
  return workflowSettings(app, orgId);
}

function getTimeoutSettings(app, orgId, enabledKey, daysKey) {
  var wf = workflowSettings(app, orgId);
  return { enabled: wf[enabledKey], days: wf[daysKey] };
}

function outstandingTimeout(app, orgId) {
  return getTimeoutSettings(app, orgId, "outstandingTimeoutEnabled", "outstandingTimeoutDays");
}

function outstandingTimeoutEmail(app, orgId) {
  var wf = workflowSettings(app, orgId);
  return { enabled: wf.outstandingTimeoutSendEmail, templateId: wf.outstandingTimeoutRejectionTemplateId };
}

function holdPickupTimeout(app, orgId) {
  return getTimeoutSettings(app, orgId, "holdPickupTimeoutEnabled", "holdPickupTimeoutDays");
}

function pendingHoldTimeout(app, orgId) {
  return getTimeoutSettings(app, orgId, "pendingHoldTimeoutEnabled", "pendingHoldTimeoutDays");
}

function additionalCopyTimeout(app, orgId) {
  return getTimeoutSettings(app, orgId, "additionalCopyTimeoutEnabled", "additionalCopyTimeoutDays");
}

function queueEnvKey(queueName, suffix) {
  return "ASAP_" + String(queueName || "job").replace(/[^a-z0-9]+/gi, "_").replace(/^_+|_+$/g, "").toUpperCase() + "_" + suffix;
}

function jobLimits(queueName) {
  var pageSize = envInteger("ASAP_JOB_PAGE_SIZE", 50, 1, 500);
  var maxPerRun = envInteger("ASAP_JOB_MAX_PER_RUN", 500, 1, 5000);
  var queue = String(queueName || "").trim();
  if (queue) {
    if (queue.indexOf("timeout") >= 0) {
      pageSize = envInteger("ASAP_TIMEOUT_PAGE_SIZE", pageSize, 1, 500);
      maxPerRun = envInteger("ASAP_TIMEOUT_MAX_PER_RUN", maxPerRun, 1, 5000);
    }
    pageSize = envInteger(queueEnvKey(queue, "PAGE_SIZE"), pageSize, 1, 500);
    maxPerRun = envInteger(queueEnvKey(queue, "MAX_PER_RUN"), maxPerRun, 1, 5000);
  }
  return { pageSize: pageSize, maxPerRun: maxPerRun };
}

module.exports = {
  defaultWorkflowValues: defaultWorkflowValues,
  systemWorkflowRecord: systemWorkflowRecord,
  libraryWorkflowRecord: libraryWorkflowRecord,
  effectiveWorkflowRecord: effectiveWorkflowRecord,
  workflowRecord: workflowRecord,
  workflowFromRecord: workflowFromRecord,
  workflowSettings: workflowSettings,
  suggestionLimit: suggestionLimit,
  outstandingTimeout: outstandingTimeout,
  outstandingTimeoutEmail: outstandingTimeoutEmail,
  holdPickupTimeout: holdPickupTimeout,
  pendingHoldTimeout: pendingHoldTimeout,
  additionalCopyTimeout: additionalCopyTimeout,
  jobLimits: jobLimits,
};
