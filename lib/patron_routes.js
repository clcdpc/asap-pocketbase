const config = require(`${__hooks}/../lib/config.js`);
const formatClaimRules = require(`${__hooks}/../lib/format_claim_rules.js`);
const formatRules = require(`${__hooks}/../lib/format_rules.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);

function patronLogin(e) {
  var data = {};
  try {
    data = routeUtils.body(e) || {};
    var barcode = String(data.username || data.barcode || "").trim();
    var password = String(data.password || data.pin || "");

    if (!barcode || !password) {
      return e.json(400, { message: "Barcode and PIN are required" });
    }

    var appSettings = config.getSettings(e.app);
    var enabledLibraries = String(appSettings.enabledLibraryOrgIds || "").trim();

    // Pre-check participation if libraryOrgId is provided (avoids Polaris check if disabled)
    var libraryOrgId = String(data.libraryOrgId || "").trim();
    if (libraryOrgId && enabledLibraries) {
      var enabledList = enabledLibraries.split(",").map(function (id) { return id.trim(); }).filter(function (id) { return id.length > 0; });
      if (enabledList.length > 0 && enabledList.indexOf(libraryOrgId) < 0) {
        var librarySettings = config.librarySettings(e.app, libraryOrgId);
        var msg = librarySettings.ui_text.systemNotEnabledMessage || "{{library}} does not currently participate in this suggestion service.";
        var org = orgs.findOrganization(e.app, libraryOrgId);
        var libraryName = org ? String(org.get("displayName") || org.get("name") || "Your library") : "Your library";
        
        msg = msg.replace(/\{\{library\}\}/g, libraryName);
        if (msg.indexOf("Your library") >= 0) {
          msg = msg.replace("Your library", libraryName);
        }
        return e.json(403, { message: msg });
      }
    }

    var staffAuth = polaris.adminStaffAuth();
    var patron = polaris.authenticatePatron(barcode, password, staffAuth);
    patron = orgs.attachPatronScope(e.app, patron, staffAuth, e.app.logger());

    if (!patron.LibraryOrgID) {
      return e.json(403, { message: "Your library could not be determined from Polaris." });
    }

    var librarySettings = config.librarySettings(e.app, patron.LibraryOrgID);

    if (enabledLibraries) {
      var enabledList = enabledLibraries.split(",").map(function (id) { return id.trim(); }).filter(function (id) { return id.length > 0; });
      if (enabledList.length > 0 && enabledList.indexOf(String(patron.LibraryOrgID)) < 0) {
        var msg = librarySettings.ui_text.systemNotEnabledMessage || "Your library does not currently participate in this suggestion service.";
        var libraryName = String(patron.LibraryOrgName || "").trim() || "Your library";
        if (msg.indexOf("{{library}}") >= 0) {
          msg = msg.replace(/\{\{library\}\}/g, libraryName);
        } else if (msg.indexOf("Your library") >= 0) {
          msg = msg.replace("Your library", libraryName);
        }
        return e.json(403, { message: msg });
      }
    }

    var record = records.upsertPatronUser(e.app, patron);

    return e.json(200, {
      token: record.newAuthToken(),
      record: record,
      email: patron.EmailAddress || "",
      preferredPickupBranchId: patron.PreferredPickupBranchID || "",
      preferredPickupBranchName: patron.PreferredPickupBranchName || "",
      ui_text: librarySettings.ui_text
    });
  } catch (err) {
    e.app.logger().error("Patron login failed", "error", String(err));
    var status = 401;
    var message = "Incorrect Login - Please try again";

    var errStr = String(err);
    if (errStr.indexOf("Polaris configuration") >= 0 || errStr.indexOf("Admin staff authentication") >= 0) {
      status = 500;
      var libraryOrgId = String((data && data.libraryOrgId) || "").trim();
      var libraryName = "library";

      if (libraryOrgId) {
        var org = orgs.findOrganization(e.app, libraryOrgId);
        if (org) {
          libraryName = String(org.get("displayName") || org.get("name") || libraryName);
        }
      } else if (typeof patron !== 'undefined' && patron && patron.LibraryOrgName) {
        libraryName = patron.LibraryOrgName;
      }

      var globalUiText = config.uiText(e.app, "");
      message = globalUiText.misconfiguredMessage || "The {{library}} suggestion system is currently misconfigured. Please contact staff.";
      message = message.replace(/\{\{library\}\}/g, libraryName);
      if (message.indexOf("The library") === 0 && libraryName !== "library") {
        message = message.replace("The library", "The " + libraryName);
      }
    } else {
      message = "Incorrect Login - Please try again";
    }

    return e.json(status, { message: message });
  }
}

function createSuggestion(e) {
  var patron = routeUtils.requireAuth(e, "patron_users");
  var uiText = config.uiText(e.app, patron.get("libraryOrgId"));
  try {
    if (!String(patron.get("libraryOrgId") || "").trim()) {
      return e.json(403, { message: "Your library could not be determined. Please log out and log back in before submitting a suggestion." });
    }
    var data = formatRules.sanitizePatronSuggestion(routeUtils.body(e), uiText);
    routeUtils.applyIsbnCheckStatusForCreate(data, uiText);
    var record = records.createSuggestion(e.app, patron, data);
    formatClaimRules.applyFormatClaimRule(e.app, record, { trigger: "submission" });
    record = routeUtils.runImmediateSubmissionIdentifierLookup(e, record);

    // Trigger confirmation email
    try {
      if (!mail.suggestionSubmitted(e.app, record)) {
        routeUtils.noteSkippedEmail(e.app, record);
      }
    } catch (mailErr) {
      e.app.logger().error("Confirmation email failed", "recordId", record.id, "error", String(mailErr));
    }

    return e.json(201, {
      id: record.id,
      successTitle: uiText.successTitle,
      successMessage: uiText.successMessage
    });
  } catch (err) {
    if (err.code) {
      if (err.code === 409) {
        return routeUtils.duplicateConflictResponse(e, err, uiText);
      }
      return e.json(err.code, { message: err.message });
    }
    throw err;
  }
}

module.exports = {
  patronLogin: patronLogin,
  createSuggestion: createSuggestion
};
