const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const config = require(`${__hooks}/../lib/config.js`);
const records = require(`${__hooks}/../lib/records.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const additionalCopies = require(`${__hooks}/../lib/additional_copies.js`);
const formatClaimRules = require(`${__hooks}/../lib/format_claim_rules.js`);
const effectiveLibrary = require(`${__hooks}/../lib/staff/effective_library.js`);
const pickupPreference = require(`${__hooks}/../lib/polaris/pickup_preference_context.js`);
const resolvePolarisUpdateActor = require(`${__hooks}/../lib/staff/polaris_actor.js`).resolvePolarisUpdateActor;

function staffMaterialTypesSync(e) {
  try {
    if (!routeUtils.requireSuperAdminStaff(e)) {
      return e.json(403, { success: false, message: "Super admin access required" });
    }
    var auth = polaris.adminStaffAuth();
    var details = polaris.getMARCTypeOfMaterials(auth);
    if (details && Object.keys(details).length > 0) {
      var settings = e.app.findRecordById("polaris_settings", "polaris00000010");
      settings.set("materialTypesCache", {
        version: 2,
        rows: details
      });
      settings.set("materialTypesCacheUpdated", new Date().toISOString());
      e.app.save(settings);
      return e.json(200, { success: true, count: Object.keys(details).length });
    }
    return e.json(400, { success: false, message: "No material types returned from Polaris." });
  } catch (err) {
    return e.json(400, { success: false, message: err.message || String(err) });
  }
}


function staffCreateSuggestion(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var data = routeUtils.body(e);
  var effectiveStaffLibrary = effectiveLibrary.resolveEffectiveStaffLibraryContext(e, staff, data);
  if (!effectiveStaffLibrary.libraryOrgId) {
    return e.json(400, { message: "Select a library before creating a staff suggestion." });
  }
  if (!effectiveLibrary.effectiveLibraryParticipates(e.app, effectiveStaffLibrary.libraryOrgId)) {
    return e.json(403, { message: "Selected library does not currently participate in ASAP." });
  }
  var uiText = config.uiText(e.app, effectiveStaffLibrary.libraryOrgId);
  var barcode = String(data.barcode || "").trim();
  var selectedPickupBranchId = String(data.preferredPickupBranchId || "").trim();
  if (!barcode) {
    return e.json(400, { message: "Barcode is required" });
  }
  if (!selectedPickupBranchId) {
    return e.json(400, { message: "Choose a valid preferred pickup location." });
  }

  var patronData;
  var staffAuth;
  try {
    staffAuth = polaris.adminStaffAuth();
    patronData = polaris.lookupPatron(staffAuth, barcode);
    if (!patronData.PatronID) {
      throw new Error("Patron not found");
    }
    patronData = orgs.attachPatronScope(e.app, patronData, staffAuth, e.app.logger());
    if (!patronData.LibraryOrgID) {
      return e.json(400, { message: "Patron barcode was found, but its Polaris library could not be determined." });
    }
    var wf = config.workflowSettings ? config.workflowSettings(e.app, effectiveStaffLibrary.libraryOrgId) : {};
    if (!wf.allowAnyRegisteredCardLogin && String(patronData.LibraryOrgID || "").trim() !== String(effectiveStaffLibrary.libraryOrgId || "").trim()) {
      return e.json(403, { message: "This patron belongs to a different library." });
    }
  } catch (err) {
    return e.json(400, { message: "Invalid patron barcode" });
  }

  var patronRecord = records.upsertPatronUser(e.app, patronData);
  var pickupContext = pickupPreference.buildPickupPreferenceContext(e.app, staffAuth, patronData);
  var selectedBranch;
  try {
    selectedBranch = pickupPreference.validateSelectedPickupBranch(pickupContext, selectedPickupBranchId);
  } catch (pickupErr) {
    pickupContext = pickupPreference.buildPickupPreferenceContext(e.app, staffAuth, patronData, { forceRefresh: true });
    try {
      selectedBranch = pickupPreference.validateSelectedPickupBranch(pickupContext, selectedPickupBranchId);
    } catch (pickupErr2) {
      return e.json(400, { message: "Choose a valid preferred pickup location." });
    }
  }

  var currentPickupId = pickupPreference.currentPreferredId(patronData);
  var actor;
  try {
    actor = resolvePolarisUpdateActor(staff, config.polaris(e.app));
  } catch (actorErr) {
    return e.json(actorErr.code || 403, { message: actorErr.message || "Staff account is missing Polaris actor details." });
  }
  var pickupChanged = selectedBranch.id !== currentPickupId;
  if (pickupChanged) {
    try {
      polaris.updatePatronPreferredPickupBranch(staffAuth, barcode, selectedBranch.id, actor);
    } catch (pickupUpdateErr) {
      e.app.logger().error("Staff pickup preference update failed", "barcode", barcode, "staff", staff.get("username") || "", "error", String(pickupUpdateErr));
      return e.json(502, {
        message: "Preferred pickup location could not be updated in Polaris. The suggestion was not created."
      });
    }
  }
  data.preferredPickupBranchId = selectedBranch.id;
  data.preferredPickupBranchName = selectedBranch.label;
  if (pickupChanged) {
    patronRecord.set("preferredPickupBranchId", selectedBranch.id);
    patronRecord.set("preferredPickupBranchName", selectedBranch.label);
    e.app.save(patronRecord);
  }

  try {
    data.staffLibraryOrgIdCreatedBy = effectiveStaffLibrary.libraryOrgId || staff.get("libraryOrgId") || "";
    routeUtils.applyIsbnCheckStatusForCreate(data, uiText);
    var record = records.createSuggestion(e.app, patronRecord, data, {
      skipLimits: true,
      email: patronData.EmailAddress || "",
      effectiveLibraryOrgId: effectiveStaffLibrary.libraryOrgId,
      effectiveLibraryOrgName: effectiveStaffLibrary.libraryOrgName
    });
    formatClaimRules.applyFormatClaimRule(e.app, record, {
      trigger: "submission",
      actorName: staff.get("username") || "system"
    });

    var today = records.formatDate(new Date());
    var existing = String(record.get("notes") || "");
    record.set("notes", today + " Created on behalf of patron by " + staff.get("username") + ". " + existing);
    record.set("editedBy", staff.get("username"));
    record.set("updated", new Date().toISOString());
    if (pickupChanged && actor.fallbackUsed) {
      records.appendSystemNote(record, "Preferred pickup location updated in Polaris by " + (staff.get("username") || "super_admin") + " using configured system Polaris user ID.");
    }
    e.app.save(record);
    record = routeUtils.runImmediateSubmissionIdentifierLookup(e, record);

    // Trigger confirmation email
    var emailPatronConfirmation = data.emailPatronConfirmation === true;
    if (emailPatronConfirmation) {
      try {
        if (record.get("email")) {
          var sent = mail.suggestionSubmitted(e.app, record);
          if (sent) {
            records.appendSystemNote(record, "Submission confirmation email sent to patron.");
          } else {
            records.appendSystemNote(record, "Submission confirmation email could not be sent.");
          }
        } else {
          records.appendSystemNote(record, "Submission confirmation email skipped because the patron has no email address.");
        }
        e.app.save(record);
      } catch (err) {
        records.appendSystemNote(record, "Submission confirmation email could not be sent.");
        e.app.save(record);
        e.app.logger().error("Staff-created suggestion confirmation email failed", "recordId", record.id, "error", String(err));
      }
    }

    return e.json(201, record);
  } catch (err) {
    if (err.code) {
      return e.json(err.code, { message: err.message });
    }
    throw err;
  }
}

function staffDeleteClosedRequest(e) {
  var staff = routeUtils.requireAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Admin access required." });
  }

  var id = e.request.pathValue("id");
  var record;
  var isAdditionalCopy = false;

  try {
    record = e.app.findRecordById("title_requests", id);
  } catch (err) {
    try {
      record = e.app.findRecordById("additional_copy_requests", id);
      isAdditionalCopy = true;
    } catch (err2) {
      return e.json(404, { message: "Closed request not found." });
    }
  }

  // Common access check (additional copy uses same library logic)
  var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
  if (accessError) {
    return accessError;
  }

  if (records.normalizeStatus(record.get("status")) !== records.STATUS.CLOSED) {
    return e.json(400, { message: "Only closed requests can be deleted." });
  }

  try {
    if (isAdditionalCopy) {
      records.deleteAdditionalCopyRequestWithAudit(e.app, record, staff, "single");
    } else {
      records.deleteTitleRequestWithAudit(e.app, record, staff, "single");
    }
    return e.json(200, { success: true });
  } catch (err3) {
    return e.json(400, { message: err3.message || "Could not delete closed request." });
  }
}

function staffDeleteClosedRequestsBulk(e) {
  var staff = routeUtils.requireAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Admin access required." });
  }

  var data = routeUtils.body(e);
  if (String(data.confirm || "") !== "DELETE") {
    return e.json(400, { message: "Type DELETE to confirm bulk deletion." });
  }

  try {
    var deleted = records.deleteClosedRequestsBulk(e.app, staff, data.confirm);
    return e.json(200, { success: true, deleted: deleted });
  } catch (err) {
    return e.json(400, { message: err.message || "Could not delete closed requests." });
  }
}

function staffTestPolaris(e) {
  if (!routeUtils.requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  var data = routeUtils.body(e);
  var polarisData = data && data.polaris ? routeUtils.buildPolarisData(data) : config.polaris(e.app);
  return routeUtils.testPolarisConnection(e, polarisData);
}

function staffTestSmtp(e) {
  var staff = routeUtils.requireSuperAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Super admin access required" });
  }
  try {
    config.applyMailSettings(e.app);

    var d = routeUtils.body(e);
    var email = String(d.email || "").trim() || staff.get("email");
    if (!email) {
      return e.json(400, { success: false, message: "No recipient email address specified (and your staff account has no email)." });
    }
    var subject = "Test SMTP Connection";
    var text = "This is a test email from Auto Suggest a Purchase to confirm SMTP settings are working.";
    var html = "<p>This is a test email from Auto Suggest a Purchase to confirm SMTP settings are working.</p>";
    var ok = mail.send(e.app, email, subject, text, html);

    if (ok) {
      return e.json(200, { success: true, message: "Test email sent to " + email + "!" });
    }
    return e.json(400, { success: false, message: "Mailer failed. Please check your from address and SMTP settings." });
  } catch (err) {
    return e.json(400, { success: false, message: err.message || String(err) });
  }
}

function staffSyncOrganizations(e) {
  if (!routeUtils.requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  try {
    var result = orgs.syncOrganizations(e.app, polaris.adminStaffAuth());
    return e.json(200, {
      success: true,
      synced: result.synced || 0,
      message: "Organization hierarchy synced."
    });
  } catch (err) {
    return e.json(400, { success: false, message: err.message || String(err) });
  }
}

module.exports = {
  staffCreateSuggestion,
  staffDeleteClosedRequest,
  staffDeleteClosedRequestsBulk,
  staffTestPolaris,
  staffTestSmtp,
  staffSyncOrganizations,
  staffMaterialTypesSync
};
