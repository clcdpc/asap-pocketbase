const config = require(`${__hooks}/../lib/config.js`);
const formatClaimRules = require(`${__hooks}/../lib/format_claim_rules.js`);
const formatRules = require(`${__hooks}/../lib/format_rules.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const pickupPreference = require(`${__hooks}/../lib/polaris/pickup_preference_context.js`);
const records = require(`${__hooks}/../lib/records.js`);
const patronSessionContexts = require(`${__hooks}/../lib/patron_session_contexts.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);

function enabledLibraryList(appSettings) {
  return String(appSettings.enabledLibraryOrgIds || "").split(",").map(function (id) { return id.trim(); }).filter(function (id) { return id.length > 0; });
}

function resolveOrgName(app, libraryOrgId) {
  var org = libraryOrgId ? orgs.findOrganization(app, libraryOrgId) : null;
  return org ? String(org.get("displayName") || org.get("name") || "") : "";
}

function isLibraryParticipating(app, libraryOrgId) {
  var list = enabledLibraryList(config.getSettings(app));
  if (!list.length) return true;
  return list.indexOf(String(libraryOrgId || "").trim()) >= 0;
}

function participationMessage(app, libraryOrgId, libraryName) {
  var librarySettings = config.librarySettings(app, libraryOrgId);
  var msg = librarySettings.ui_text.systemNotEnabledMessage || "{{library}} does not currently participate in this suggestion service.";
  libraryName = String(libraryName || "").trim() || "Your library";
  if (msg.indexOf("{{library}}") >= 0) {
    msg = msg.replace(/\{\{library\}\}/g, libraryName);
  } else if (msg.indexOf("Your library") >= 0) {
    msg = msg.replace("Your library", libraryName);
  }
  return msg;
}

function resolvePatronLoginLibraryContext(app, data, patron) {
  data = data || {};
  patron = patron || {};
  // patron.LibraryOrgID is the patron home library returned by Polaris.
  var patronHomeLibraryOrgId = String(patron.LibraryOrgID || "").trim();
  var patronHomeLibraryOrgName = String(patron.LibraryOrgName || "").trim();
  // data.libraryOrgId is the patron experience launch context, not the patron home library.
  var experienceLibraryOrgId = String(data.libraryOrgId || "").trim();
  var experienceLibraryOrgName = experienceLibraryOrgId ? resolveOrgName(app, experienceLibraryOrgId) : "";
  var allowAnyRegisteredCardLogin = false;
  var effectiveLibraryOrgId = patronHomeLibraryOrgId;
  var effectiveLibraryOrgName = patronHomeLibraryOrgName;

  if (experienceLibraryOrgId) {
    var wf = config.workflowSettings ? config.workflowSettings(app, experienceLibraryOrgId) : {};
    allowAnyRegisteredCardLogin = !!wf.allowAnyRegisteredCardLogin;
    if (allowAnyRegisteredCardLogin) {
      effectiveLibraryOrgId = experienceLibraryOrgId;
      effectiveLibraryOrgName = experienceLibraryOrgName || patronHomeLibraryOrgName;
    }
  }

  return {
    patronHomeLibraryOrgId: patronHomeLibraryOrgId,
    patronHomeLibraryOrgName: patronHomeLibraryOrgName,
    experienceLibraryOrgId: experienceLibraryOrgId,
    experienceLibraryOrgName: experienceLibraryOrgName,
    // effectiveLibraryOrgId owns the ASAP request and supplies workflow/settings/UI text.
    effectiveLibraryOrgId: effectiveLibraryOrgId,
    effectiveLibraryOrgName: effectiveLibraryOrgName,
    allowAnyRegisteredCardLogin: allowAnyRegisteredCardLogin,
    crossLibraryLoginAllowed: !!(experienceLibraryOrgId && allowAnyRegisteredCardLogin)
  };
}

function patronLogin(e) {
  var data = {};
  try {
    data = routeUtils.body(e) || {};
    var barcode = String(data.username || data.barcode || "").trim();
    var password = String(data.password || data.pin || "");

    if (!barcode || !password) {
      return e.json(400, { message: "Barcode and PIN are required" });
    }

    var staffAuth = polaris.adminStaffAuth();
    var patron = polaris.authenticatePatron(barcode, password, staffAuth);
    patron = orgs.attachPatronScope(e.app, patron, staffAuth, e.app.logger());

    var context = resolvePatronLoginLibraryContext(e.app, data, patron);
    if (!context.patronHomeLibraryOrgId) {
      return e.json(403, { message: "Your library could not be determined from Polaris." });
    }

    if (context.experienceLibraryOrgId && !isLibraryParticipating(e.app, context.experienceLibraryOrgId)) {
      return e.json(403, { message: participationMessage(e.app, context.experienceLibraryOrgId, context.experienceLibraryOrgName || "Your library") });
    }

    if (!context.crossLibraryLoginAllowed && !isLibraryParticipating(e.app, context.patronHomeLibraryOrgId)) {
      return e.json(403, { message: participationMessage(e.app, context.patronHomeLibraryOrgId, context.patronHomeLibraryOrgName || "Your library") });
    }

    var librarySettings = config.librarySettings(e.app, context.effectiveLibraryOrgId);
    var record = records.upsertPatronUser(e.app, patron);
    var sessionContext = patronSessionContexts.createPatronSessionContext(e.app, record, context);
    var patronContextId = sessionContext.id || "";
    var crossLibraryLogin = !!(context.crossLibraryLoginAllowed && context.patronHomeLibraryOrgId !== context.effectiveLibraryOrgId);
    var pickupContext = pickupPreference.buildPickupPreferenceContext(e.app, staffAuth, patron);

    return e.json(200, {
      token: record.newAuthToken(),
      record: record,
      email: patron.EmailAddress || "",
      preferredPickupBranchId: pickupContext.currentPreferredPickupBranchId || patron.PreferredPickupBranchID || "",
      preferredPickupBranchName: pickupContext.currentPreferredPickupBranchName || patron.PreferredPickupBranchName || "",
      pickupBranches: pickupContext.pickupBranches || [],
      pickupBranchesRefreshedAt: pickupContext.pickupBranchesRefreshedAt || "",
      currentPreferredPickupBranchId: pickupContext.currentPreferredPickupBranchId || "",
      currentPreferredPickupBranchName: pickupContext.currentPreferredPickupBranchName || "",
      selectedPickupBranchId: pickupContext.selectedPickupBranchId || "",
      selectedPickupBranchName: pickupContext.selectedPickupBranchName || "",
      currentPreferenceAllowed: !!pickupContext.currentPreferenceAllowed,
      pickupBranchWarning: pickupContext.pickupBranchWarning || "",
      experienceLibraryOrgId: context.experienceLibraryOrgId,
      experienceLibraryOrgName: context.experienceLibraryOrgName,
      patronHomeLibraryOrgId: context.patronHomeLibraryOrgId,
      patronHomeLibraryOrgName: context.patronHomeLibraryOrgName,
      effectiveLibraryOrgId: context.effectiveLibraryOrgId,
      effectiveLibraryOrgName: context.effectiveLibraryOrgName,
      crossLibraryLogin: crossLibraryLogin,
      patronContextId: patronContextId,
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
  var uiText;
  try {
    var rawData = routeUtils.body(e) || {};
    var patronContextId = String(rawData.patronContextId || "").trim();
    var sessionContext = patronContextId ? patronSessionContexts.getPatronSessionContext(e.app, patron, patronContextId) : null;
    // Legacy sessions without patronContextId fall back only to the patron home library.
    var effectiveLibraryOrgId = String((sessionContext && sessionContext.effectiveLibraryOrgId) || patron.get("libraryOrgId") || "").trim();
    var effectiveLibraryOrgName = String((sessionContext && sessionContext.effectiveLibraryOrgName) || patron.get("libraryOrgName") || "").trim();
    uiText = config.uiText(e.app, effectiveLibraryOrgId);
    if (!effectiveLibraryOrgId) {
      return e.json(403, { message: "Your library could not be determined. Please log out and log back in before submitting a suggestion." });
    }
    var selectedPickupBranchId = String(rawData.preferredPickupBranchId || "").trim();
    if (!selectedPickupBranchId) {
      return e.json(400, { message: "Choose a valid preferred pickup location." });
    }
    var staffAuth = polaris.adminStaffAuth();
    var livePatron = orgs.attachPatronScope(e.app, polaris.lookupPatron(staffAuth, patron.get("barcode")), staffAuth, e.app.logger());
    var pickupContext = pickupPreference.buildPickupPreferenceContext(e.app, staffAuth, livePatron, { forceRefresh: true });
    var selectedBranch;
    try {
      selectedBranch = pickupPreference.validateSelectedPickupBranch(pickupContext, selectedPickupBranchId);
    } catch (pickupErr) {
      return e.json(400, { message: "Choose a valid preferred pickup location." });
    }
    var liveCurrentPickupId = pickupPreference.currentPreferredId(livePatron);
    if (selectedBranch.id !== liveCurrentPickupId) {
      try {
        polaris.updatePatronPreferredPickupBranch(staffAuth, patron.get("barcode"), selectedBranch.id, {
          type: "patron",
          barcode: patron.get("barcode") || "",
          polarisUserId: ""
        });
      } catch (pickupUpdateErr) {
        e.app.logger().error("Patron pickup preference update failed", "barcode", patron.get("barcode"), "error", String(pickupUpdateErr));
        return e.json(502, {
          message: "Your preferred pickup location could not be updated in Polaris. Please try again."
        });
      }
      patron.set("preferredPickupBranchId", selectedBranch.id);
      patron.set("preferredPickupBranchName", selectedBranch.label);
      e.app.save(patron);
    }
    var data = formatRules.sanitizePatronSuggestion(rawData, uiText);
    data.preferredPickupBranchId = selectedBranch.id;
    data.preferredPickupBranchName = selectedBranch.label;
    routeUtils.applyIsbnCheckStatusForCreate(data, uiText);
    var record = records.createSuggestion(e.app, patron, data, {
      effectiveLibraryOrgId: effectiveLibraryOrgId,
      effectiveLibraryOrgName: effectiveLibraryOrgName
    });
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
  createSuggestion: createSuggestion,
  resolveOrgName: resolveOrgName,
  isLibraryParticipating: isLibraryParticipating,
  resolvePatronLoginLibraryContext: resolvePatronLoginLibraryContext
};
