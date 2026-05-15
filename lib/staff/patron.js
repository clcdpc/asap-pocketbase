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

function looksLikeBarcodeCandidate(value) {
  var text = String(value || "").trim();
  if (!text) return false;
  if (/\s/.test(text)) return false;
  return /^[A-Za-z0-9._:-]+$/.test(text);
}

function beautifyPatronError(err, query) {
  if (!err) return "No patron found. Try barcode, name, or first name then last name.";

  var msg = String(err.message || err);

  // Code -3000 is the standard Polaris "not found" or "invalid search" for patrons
  if (msg.indexOf("-3000") >= 0 || msg.indexOf("Error retrieving patron ID") >= 0) {
    return "We couldn't find a patron matching '" + (query || "that input") + "'. Please check the spelling and try again.";
  }

  if (msg.indexOf("belongs to a different library") >= 0) {
    return "This patron belongs to another library. You can only submit suggestions for patrons in your own library system.";
  }

  if (msg.indexOf("Polaris library could not be determined") >= 0) {
    return "Patron found, but their library membership is missing in Polaris.";
  }

  if (msg.indexOf("HTTP") >= 0 || msg.indexOf("failed") >= 0) {
    return "The library lookup service is temporarily unavailable. Please try again in a moment.";
  }

  return msg;
}

function shouldFallBackToPatronNameSearch(err) {
  if (!err) return false;
  if (err.statusCode === 403) return false;
  var text = String(err.message || err).toLowerCase();
  return text.indexOf("not found") >= 0 ||
    text.indexOf("invalid patron barcode") >= 0 ||
    text.indexOf("patron barcode") >= 0 && text.indexOf("invalid") >= 0 ||
    text.indexOf("error retrieving patron id") >= 0 ||
    text.indexOf("-3000") >= 0 ||
    text.indexOf("404") >= 0;
}

function staffPatronLookupResponse(patronRecord, patronData) {
  return {
    status: "selected",
    barcode: patronRecord.get("barcode"),
    nameFirst: patronRecord.get("nameFirst"),
    nameLast: patronRecord.get("nameLast"),
    email: patronData.EmailAddress || (patronRecord ? patronRecord.get("notificationEmail") : "") || patronRecord.email(),
    patronOrgId: patronRecord.get("patronOrgId") || "",
    libraryOrgId: patronRecord.get("libraryOrgId") || "",
    libraryOrgName: patronRecord.get("libraryOrgName") || "",
    preferredPickupBranchId: patronData.PreferredPickupBranchID || "",
    preferredPickupBranchName: patronData.PreferredPickupBranchName || "",
  };
}

function resolveStaffPatronByBarcode(e, staff, staffAuth, barcode) {
  var patronData = polaris.lookupPatron(staffAuth, barcode);
  if (!patronData.PatronID) {
    throw new Error("Patron not found");
  }

  patronData = orgs.attachPatronScope(e.app, patronData, staffAuth, e.app.logger());
  if (!patronData.LibraryOrgID) {
    var missingScope = new Error("Patron barcode was found, but its Polaris library could not be determined.");
    missingScope.statusCode = 400;
    throw missingScope;
  }

  if (!routeUtils.sameLibrary(staff, patronData.LibraryOrgID)) {
    var wrongLibrary = new Error("This patron belongs to a different library.");
    wrongLibrary.statusCode = 403;
    throw wrongLibrary;
  }

  var patronRecord = records.upsertPatronUser(e.app, patronData);
  return staffPatronLookupResponse(patronRecord, patronData);
}

function filterPatronSearchResultsForStaffLibrary(e, staff, staffAuth, searchResults) {
  var results = [];
  var seen = {};

  for (var i = 0; i < searchResults.length && results.length < 10; i++) {
    var candidate = searchResults[i] || {};
    var barcode = String(candidate.barcode || "").trim();
    if (!barcode || seen[barcode]) continue;
    seen[barcode] = true;

    try {
      var patronData = polaris.lookupPatron(staffAuth, barcode);
      patronData = orgs.attachPatronScope(e.app, patronData, staffAuth, e.app.logger());
      if (!patronData.LibraryOrgID || !routeUtils.sameLibrary(staff, patronData.LibraryOrgID)) {
        continue;
      }

      results.push({
        status: "candidate",
        barcode: patronData.Barcode || barcode,
        nameFirst: patronData.NameFirst || candidate.nameFirst || "",
        nameLast: patronData.NameLast || candidate.nameLast || "",
        name: [patronData.NameFirst || candidate.nameFirst || "", patronData.NameLast || candidate.nameLast || ""].filter(Boolean).join(" ").trim() || candidate.name || "Patron",
        email: patronData.EmailAddress || "",
        patronOrgId: patronData.PatronOrgID || candidate.organizationId || "",
        libraryOrgId: patronData.LibraryOrgID || "",
        libraryOrgName: patronData.LibraryOrgName || candidate.libraryOrgName || "",
        preferredPickupBranchId: patronData.PreferredPickupBranchID || "",
        preferredPickupBranchName: patronData.PreferredPickupBranchName || ""
      });
    } catch (err) {
      // Skip unresolvable search rows.
    }
  }

  return results;
}

function staffLookupPatron(e) {
  try {
    var staff = routeUtils.requireAuth(e, "staff_users");
    var data = routeUtils.body(e);
    var raw = String(data.query || data.barcode || "").trim();
    if (!raw) {
      return e.json(400, { message: "Enter a patron barcode or name." });
    }

    var staffAuth = polaris.adminStaffAuth();

    if (looksLikeBarcodeCandidate(raw)) {
      try {
        return e.json(200, resolveStaffPatronByBarcode(e, staff, staffAuth, raw));
      } catch (err) {
        if (data.barcode && !data.query) {
          return e.json(err.statusCode || 400, { message: beautifyPatronError(err, raw) });
        }
        if (!shouldFallBackToPatronNameSearch(err)) {
          return e.json(err.statusCode || 400, { message: beautifyPatronError(err, raw) });
        }
      }
    }

    var search = polaris.searchPatrons(staffAuth, { query: raw, limit: 10 });
    if (search.status === "error") {
      e.app.logger().warn("Staff patron name search failed", "query", raw, "error", search.error || "");
      return e.json(400, { message: "Patron search failed. Try barcode, name, or first name then last name." });
    }

    var results = filterPatronSearchResultsForStaffLibrary(e, staff, staffAuth, search.results || []);
    if (!results.length) {
      return e.json(404, {
        status: "not_found",
        message: beautifyPatronError(null, raw),
        results: []
      });
    }

    if (results.length === 1 && results[0].barcode) {
      try {
        return e.json(200, resolveStaffPatronByBarcode(e, staff, staffAuth, results[0].barcode));
      } catch (err) {
        return e.json(err.statusCode || 400, { message: beautifyPatronError(err, results[0].barcode) });
      }
    }

    return e.json(200, {
      status: "multiple",
      totalMatches: results.length,
      results: results
    });
  } catch (err) {
    e.app.logger().error("Unhandled staff patron lookup failure", "error", err && err.message ? err.message : String(err));
    return e.json(400, { message: "Patron search failed. Try barcode, name, or first name then last name." });
  }
}

function staffCreateSuggestion(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var data = routeUtils.body(e);
  var barcode = String(data.barcode || "").trim();
  if (!barcode) {
    return e.json(400, { message: "Barcode is required" });
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
    if (!routeUtils.sameLibrary(staff, patronData.LibraryOrgID)) {
      return e.json(403, { message: "This patron belongs to a different library." });
    }
  } catch (err) {
    return e.json(400, { message: "Invalid patron barcode" });
  }

  var patronRecord = records.upsertPatronUser(e.app, patronData);

  try {
    data.staffLibraryOrgIdCreatedBy = staff.get("libraryOrgId") || "";
    routeUtils.applyIsbnCheckStatusForCreate(data, config.uiText());
    var record = records.createSuggestion(e.app, patronRecord, data, {
      skipLimits: true,
      email: patronData.EmailAddress || ""
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

module.exports = {
  looksLikeBarcodeCandidate: looksLikeBarcodeCandidate,
  beautifyPatronError: beautifyPatronError,
  shouldFallBackToPatronNameSearch: shouldFallBackToPatronNameSearch,
  staffPatronLookupResponse: staffPatronLookupResponse,
  resolveStaffPatronByBarcode: resolveStaffPatronByBarcode,
  filterPatronSearchResultsForStaffLibrary: filterPatronSearchResultsForStaffLibrary,
  staffLookupPatron: staffLookupPatron,
  staffCreateSuggestion: staffCreateSuggestion
};
