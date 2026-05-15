var __hooks = typeof __hooks !== "undefined" ? __hooks : __dirname + "/../../pb_hooks";


const config = require(`${__hooks}/../lib/config.js`);
const identity = require(`${__hooks}/../lib/identity.js`);
// const jobs = require(`${__hooks}/../lib/jobs.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const formatClaimRules = require(`${__hooks}/../lib/format_claim_rules.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
// const additionalCopies = require(`${__hooks}/../lib/additional_copies.js`);

function staffTestPolaris(e) {
  if (!routeUtils.requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  var data = routeUtils.body(e);
  var polarisData = data && data.polaris ? routeUtils.buildPolarisData(data) : config.polaris();
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

function staffEmailStatus(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var orgId = String(routeUtils.queryValue(e, "orgId") || "").trim();
  if (!orgId) {
    orgId = routeUtils.isSuperAdmin(staff) ? "system" : String(staff.get("libraryOrgId") || "").trim();
  }
  if (orgId !== "system" && orgId !== String(staff.get("libraryOrgId") || "").trim() && !routeUtils.isSuperAdmin(staff)) {
    return e.json(403, { message: "Access denied to this library email status." });
  }
  return e.json(200, config.emailStatus(e.app, orgId === "system" ? "" : orgId));
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

function staffBibLookup(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var d = routeUtils.body(e);
  var bibId = String(d.bibId || "").trim();
  var mode = String(d.mode || "").trim().toLowerCase();
  if (!bibId && mode) {
    var query = String(d.query || "").trim();
    var title = String(d.title || "").trim();
    var author = String(d.author || "").trim();
    var validModes = ["identifier", "title", "author", "title_author"];
    if (validModes.indexOf(mode) < 0) {
      return e.json(400, { message: "Invalid Polaris search mode." });
    }
    try {
      var searchResult = polaris.searchBibs(polaris.adminStaffAuth(), {
        mode: mode,
        query: query,
        title: title,
        author: author,
        limit: 10
      });
      return e.json(200, {
        success: searchResult.status !== "error",
        mode: searchResult.mode,
        query: searchResult.query,
        status: searchResult.status,
        totalMatches: searchResult.totalMatches,
        multipleMatches: searchResult.multipleMatches,
        results: searchResult.results || [],
        error: searchResult.error || ""
      });
    } catch (err) {
      return e.json(400, { message: err.message || String(err) });
    }
  }
  if (!bibId) {
    return e.json(400, { message: "BIB ID is required" });
  }

  try {
    var staffAuth = polaris.adminStaffAuth();
    var info = polaris.getBib(staffAuth, bibId);

    // Add holdings summary for the staff member's library
    var holdings = polaris.getBibHoldings(staffAuth, bibId);
    var myLibraryOrgId = staff.get("libraryOrgId") || "";
    info.holdingsSummary = polaris.summarizeHoldingsByLibrary(holdings, myLibraryOrgId, function(locId) {
      var resolved = orgs.resolveParentLibrary(e.app, locId);
      return resolved ? resolved.libraryOrgId : locId;
    });

    var barcode = String(d.barcode || "").trim();
    if (barcode && info) {

      try {
        var patron = polaris.lookupPatron(staffAuth, barcode);
        if (patron && patron.PatronID) {
          patron = orgs.attachPatronScope(e.app, patron, staffAuth, e.app.logger());
          if (!routeUtils.sameLibrary(staff, patron.LibraryOrgID)) {
            return e.json(403, { message: "This patron belongs to a different library." });
          }
          var hasHold = polaris.patronHasHoldForBib(staffAuth, barcode, bibId);
          info.patronHoldCheck = hasHold
            ? { ok: true, statusValue: 29, readOnly: true }
            : { ok: true, statusValue: 0, readOnly: true };
        }
      } catch (patronErr) {
        // Log but don't fail the whole bib lookup
        e.app.logger().warn("Patron hold check failed during bib lookup", "barcode", barcode, "error", String(patronErr));
      }
    }

    return e.json(200, info);
  } catch (err) {
    return e.json(400, { message: err.message || String(err) });
  }
}

module.exports = {
  staffTestPolaris: staffTestPolaris,
  staffTestSmtp: staffTestSmtp,
  staffEmailStatus: staffEmailStatus,
  staffSyncOrganizations: staffSyncOrganizations,
  staffBibLookup: staffBibLookup
};
