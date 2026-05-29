const dbHelpers = require("./db_helpers.js");
const normalization = require("./normalization.js");
const polarisMod = require("./polaris.js");
const smtp = require("./smtp.js");
const emails = require("./emails.js");
const uiText = require("./ui_text.js");
const workflows = require("./workflows.js");

const systemRecord = dbHelpers.systemRecord;
const orgIdForSettings = dbHelpers.orgIdForSettings;
const safeRecord = dbHelpers.safeRecord;

const normalizeStaffUrl = normalization.normalizeStaffUrl;
const staffUrlFromEnv = normalization.staffUrlFromEnv;
const defaultStaffUrl = normalization.defaultStaffUrl;
const envValue = normalization.envValue;
const defaultFormatIconUrlPattern = normalization.defaultFormatIconUrlPattern;
const normalizeFormatIconUrlPattern = normalization.normalizeFormatIconUrlPattern;

const polaris = polarisMod.polaris;
const getPolarisSettings = polarisMod.getPolarisSettings;

const smtpPublicFromRecord = smtp.smtpPublicFromRecord;
const getSmtpSettings = smtp.getSmtpSettings;

const emailsFor = emails.emailsFor;
const uiTextGet = uiText.uiText;

const workflowFromRecord = workflows.workflowFromRecord;
const workflowRecord = workflows.workflowRecord;

let _identity;
function getIdentity() {
  if (!_identity) _identity = require(`${__hooks}/../lib/identity.js`);
  return _identity;
}

function getSystemSettings(app) {
  return systemRecord(app || $app, "system_settings", "settings0000001", {
    settingsKey: "system",
    allowedStaffUsers: "",
    staffUrl: defaultStaffUrl(),
    leapBibUrlPattern: "",
    formatIconUrlPattern: defaultFormatIconUrlPattern(),
    organizationsSyncStatus: "not_loaded",
    organizationsSyncMessage: "Polaris organizations have not been loaded yet."
  });
}

function staffUrl(app) {
  var sys = null;
  try {
    sys = getSystemSettings(app || $app);
  } catch (err) {
    if (typeof $app !== "undefined" && $app && $app.logger) {
      $app.logger().warn("Swallowed error", "error", String(err));
    }
  }
  var configured = "";
  try {
    configured = sys ? String(sys.get("staffUrl") || "").trim() : "";
  } catch (err) {
    if (typeof $app !== "undefined" && $app && $app.logger) {
      $app.logger().warn("Swallowed error", "error", String(err));
    }
  }
  if (configured) return normalizeStaffUrl(configured);

  var envStaffUrl = envValue("ASAP_STAFF_URL");
  if (envStaffUrl) return staffUrlFromEnv(envStaffUrl);

  var envPublicUrl = envValue("ASAP_PUBLIC_URL");
  if (envPublicUrl) return staffUrlFromEnv(envPublicUrl);

  return "http://localhost:8090/staff/";
}

function saveSystemSettings(app, data) {
  var record = getSystemSettings(app);
  if (data && Object.prototype.hasOwnProperty.call(data, "staffUrl")) {
    record.set("staffUrl", normalizeStaffUrl(data.staffUrl));
  }
  if (data && Object.prototype.hasOwnProperty.call(data, "allowedStaffUsers")) {
    record.set("allowedStaffUsers", data.allowedStaffUsers);
  }
  if (data && Object.prototype.hasOwnProperty.call(data, "leapBibUrlPattern")) {
    record.set("leapBibUrlPattern", data.leapBibUrlPattern);
  }
  if (data && Object.prototype.hasOwnProperty.call(data, "formatIconUrlPattern")) {
    record.set("formatIconUrlPattern", normalizeFormatIconUrlPattern(data.formatIconUrlPattern));
  }
  app.save(record);
  return record;
}

function enabledLibraryOrgIds(app) {
  app = app || $app;
  var sys = getSystemSettings(app);
  if (!sys) return "";
  var ids = [];
  var rels = sys.get("enabledLibraries") || [];
  if (!Array.isArray(rels)) rels = rels ? [rels] : [];
  if (rels.length === 0) return "";

  var chunkLimit = 100;
  for (var i = 0; i < rels.length; i += chunkLimit) {
    var chunk = rels.slice(i, i + chunkLimit);
    var filterParts = [];
    var params = {};
    for (var j = 0; j < chunk.length; j++) {
      var key = "p" + j;
      filterParts.push("id = {:" + key + "}");
      params[key] = chunk[j];
    }
    var filter = filterParts.join(" || ");
    try {
      var records = app.findRecordsByFilter("polaris_organizations", filter, "", chunk.length, 0, params);
      for (var k = 0; k < records.length; k++) {
        var org = records[k];
        if (org.get("organizationId")) ids.push(String(org.get("organizationId")));
      }
    } catch (err) {
      if (typeof $app !== "undefined" && $app && $app.logger) {
        $app.logger().warn("Swallowed error", "error", String(err));
      }
    }
  }

  return ids.join(",");
}

function getSettings(app) {
  app = app || $app;
  var sys = getSystemSettings(app);
  var wf = workflowFromRecord(workflowRecord(app, ""));
  return Object.assign({
    polaris: polaris(app),
    smtp: smtpPublicFromRecord(getSmtpSettings(app)),
    emails: emailsFor(app, ""),
    allowedStaffUsers: sys ? sys.get("allowedStaffUsers") || "" : "",
    staffUrl: staffUrl(app),
    leapBibUrlPattern: sys ? sys.get("leapBibUrlPattern") || "" : "",
    formatIconUrlPattern: normalizeFormatIconUrlPattern(
      sys ? sys.get("formatIconUrlPattern") || "" : ""
    ),
    enabledLibraryOrgIds: enabledLibraryOrgIds(app),
    ui_text: uiTextGet(app, "")
  }, wf);
}

function librarySettings(app, libraryOrgId) {
  app = app || $app;
  var sys = getSystemSettings(app);
  return {
    emails: emailsFor(app, libraryOrgId),
    ui_text: uiTextGet(app, libraryOrgId),
    workflow: workflowFromRecord(workflowRecord(app, libraryOrgId)),
    leapBibUrlPattern: sys.get("leapBibUrlPattern") || "",
    formatIconUrlPattern: normalizeFormatIconUrlPattern(
      sys.get("formatIconUrlPattern") || ""
    )
  };
}

function formatIconUrlPattern(app) {
  var sys = getSystemSettings(app || $app);
  return normalizeFormatIconUrlPattern(
    sys ? sys.get("formatIconUrlPattern") || "" : ""
  );
}

function allowedStaffUsers() {
  const identity = getIdentity();
  var sys = getSystemSettings($app);
  var value = String(sys ? sys.get("allowedStaffUsers") || "" : "").trim();
  return value ? identity.parseAllowedStaffUsers(value, polaris().staffDomain) : [];
}

module.exports = {
  getSystemSettings: getSystemSettings,
  staffUrl: staffUrl,
  formatIconUrlPattern: formatIconUrlPattern,
  saveSystemSettings: saveSystemSettings,
  enabledLibraryOrgIds: enabledLibraryOrgIds,
  getSettings: getSettings,
  librarySettings: librarySettings,
  allowedStaffUsers: allowedStaffUsers,
};
