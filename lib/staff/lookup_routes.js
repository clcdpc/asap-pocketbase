
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const effectiveLibrary = require(`${__hooks}/../lib/staff/effective_library.js`);

const claims = require(`${__hooks}/../lib/staff/title_request_claims.js`);

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

function resolveEffectiveStaffLibraryContext(e, staff, data) {
  return effectiveLibrary.resolveEffectiveStaffLibraryContext(e, staff, data);
}

function allowCrossLibraryPatronLookup(e, effectiveLibraryOrgId) {
  return effectiveLibrary.allowCrossLibraryPatronLookup(e, effectiveLibraryOrgId);
}

function patronMatchesStaffLookupScope(staff, patronData, effectiveLibraryOrgId, allowAnyRegisteredCardLogin) {
  return effectiveLibrary.patronMatchesStaffLookupScope(staff, patronData, effectiveLibraryOrgId, allowAnyRegisteredCardLogin);
}

function staffPatronLookupScopeMeta(e, effectiveLibraryOrgId, effectiveLibraryOrgName, allowAnyRegisteredCardLogin) {
  return effectiveLibrary.staffPatronLookupScopeMeta(e, effectiveLibraryOrgId, effectiveLibraryOrgName, allowAnyRegisteredCardLogin);
}

function withScopeMeta(payload, meta) {
  meta = meta || {};
  for (var key in meta) payload[key] = meta[key];
  return payload;
}

function staffPatronLookupResponse(patronRecord, patronData, meta) {
  return withScopeMeta({
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
    patronHomeLibraryOrgId: patronData.LibraryOrgID || patronRecord.get("libraryOrgId") || "",
    patronHomeLibraryOrgName: patronData.LibraryOrgName || patronRecord.get("libraryOrgName") || "",
  }, meta || {});
}

function resolveStaffPatronByBarcode(e, staff, staffAuth, barcode, context, allowAnyRegisteredCardLogin, meta) {
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

  var effectiveLibraryOrgId = context && context.libraryOrgId ? context.libraryOrgId : String(staff.get("libraryOrgId") || "").trim();
  if (!patronMatchesStaffLookupScope(staff, patronData, effectiveLibraryOrgId, allowAnyRegisteredCardLogin)) {
    var wrongLibrary = new Error("This patron belongs to a different library.");
    wrongLibrary.statusCode = 403;
    throw wrongLibrary;
  }

  var patronRecord = records.upsertPatronUser(e.app, patronData);
  return staffPatronLookupResponse(patronRecord, patronData, meta);
}

function filterPatronSearchResultsForStaffLibrary(e, staff, staffAuth, searchResults, context, allowAnyRegisteredCardLogin) {
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
      if (!patronData.LibraryOrgID || !patronMatchesStaffLookupScope(staff, patronData, context && context.libraryOrgId, allowAnyRegisteredCardLogin)) {
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
        preferredPickupBranchName: patronData.PreferredPickupBranchName || "",
        patronHomeLibraryOrgId: patronData.LibraryOrgID || "",
        patronHomeLibraryOrgName: patronData.LibraryOrgName || candidate.libraryOrgName || ""
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

    var context = resolveEffectiveStaffLibraryContext(e, staff, data);
    var allowAnyRegisteredCardLogin = allowCrossLibraryPatronLookup(e, context.libraryOrgId);
    var scopeMeta = staffPatronLookupScopeMeta(e, context.libraryOrgId, context.libraryOrgName, allowAnyRegisteredCardLogin);
    var staffAuth = polaris.adminStaffAuth();

    if (looksLikeBarcodeCandidate(raw)) {
      try {
        return e.json(200, resolveStaffPatronByBarcode(e, staff, staffAuth, raw, context, allowAnyRegisteredCardLogin, scopeMeta));
      } catch (err) {
        if (data.barcode && !data.query) {
          return e.json(err.statusCode || 400, withScopeMeta({ message: beautifyPatronError(err, raw) }, scopeMeta));
        }
        if (!shouldFallBackToPatronNameSearch(err)) {
          return e.json(err.statusCode || 400, withScopeMeta({ message: beautifyPatronError(err, raw) }, scopeMeta));
        }
      }
    }

    var search = polaris.searchPatrons(staffAuth, { query: raw, limit: 10 });
    if (search.status === "error") {
      e.app.logger().warn("Staff patron name search failed", "query", raw, "error", search.error || "");
      return e.json(400, { message: "Patron search failed. Try barcode, name, or first name then last name." });
    }

    var results = filterPatronSearchResultsForStaffLibrary(e, staff, staffAuth, search.results || [], context, allowAnyRegisteredCardLogin);
    if (!results.length) {
      return e.json(404, withScopeMeta({
        status: "not_found",
        message: beautifyPatronError(null, raw),
        results: []
      }, scopeMeta));
    }

    if (results.length === 1 && results[0].barcode) {
      try {
        return e.json(200, resolveStaffPatronByBarcode(e, staff, staffAuth, results[0].barcode, context, allowAnyRegisteredCardLogin, scopeMeta));
      } catch (err) {
        return e.json(err.statusCode || 400, withScopeMeta({ message: beautifyPatronError(err, results[0].barcode) }, scopeMeta));
      }
    }

    return e.json(200, withScopeMeta({
      status: "multiple",
      totalMatches: results.length,
      results: results
    }, scopeMeta));
  } catch (err) {
    e.app.logger().error("Unhandled staff patron lookup failure", "error", err && err.message ? err.message : String(err));
    return e.json(400, { message: "Patron search failed. Try barcode, name, or first name then last name." });
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
      var searchResult = polaris.searchBibs(e.app, polaris.adminStaffAuth(), {
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
    var info = polaris.getBib(e.app, staffAuth, bibId);

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
          var bibContext = resolveEffectiveStaffLibraryContext(e, staff, d);
          var bibAllowAnyRegisteredCardLogin = allowCrossLibraryPatronLookup(e, bibContext.libraryOrgId);
          if (!patronMatchesStaffLookupScope(staff, patron, bibContext.libraryOrgId, bibAllowAnyRegisteredCardLogin)) {
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
  looksLikeBarcodeCandidate,
  beautifyPatronError,
  shouldFallBackToPatronNameSearch,
  staffPatronLookupResponse,
  resolveEffectiveStaffLibraryContext,
  allowCrossLibraryPatronLookup,
  patronMatchesStaffLookupScope,
  staffPatronLookupScopeMeta,
  resolveStaffPatronByBarcode,
  filterPatronSearchResultsForStaffLibrary,
  staffLookupPatron,
  staffBibLookup
};
