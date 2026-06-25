const dbHelpers = require("../db_helpers.js");
const systemRecord = dbHelpers.systemRecord;
const orgIdForSettings = dbHelpers.orgIdForSettings;
const safeCollection = dbHelpers.safeCollection;

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

module.exports = { uiRecord, patronSettingsOverrideRecord, legacyPatronLibrarySettingsRecord };
