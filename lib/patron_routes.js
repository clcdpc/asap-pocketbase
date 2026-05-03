const config = require(`${__hooks}/../lib/config.js`);
const formatRules = require(`${__hooks}/../lib/format_rules.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);

function patronLogin(e) {
  try {
    var data = routeUtils.body(e);
    var barcode = String(data.username || data.barcode || "").trim();
    var password = String(data.password || data.pin || "");

    if (!barcode || !password) {
      return e.json(400, { message: "Barcode and PIN are required" });
    }

    var staffAuth = polaris.adminStaffAuth();
    var patron = polaris.authenticatePatron(barcode, password, staffAuth);
    patron = orgs.attachPatronScope(e.app, patron, staffAuth, e.app.logger());

    if (!patron.LibraryOrgID) {
      return e.json(403, { message: "Your library could not be determined from Polaris." });
    }

    var appSettings = config.getSettings();
    var enabledLibraries = String(appSettings.enabledLibraryOrgIds || "").trim();
    var librarySettings = config.librarySettings(e.app, patron.LibraryOrgID);

    if (enabledLibraries) {
      var enabledList = enabledLibraries.split(",").map(function (id) { return id.trim(); }).filter(function (id) { return id.length > 0; });
      if (enabledList.length > 0 && enabledList.indexOf(String(patron.LibraryOrgID)) < 0) {
        var msg = librarySettings.ui_text.systemNotEnabledMessage || "Your library does not currently participate in this suggestion service.";
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
      message = "The library suggestion system is currently misconfigured. Please contact staff.";
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
