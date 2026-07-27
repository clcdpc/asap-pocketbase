const helpers = require("./helpers.js");
const auth = require("./auth.js");

function getPatronBasic(staff, barcode) {
  var ep = helpers.endpoint("public", "patron/" + encodeURIComponent(barcode) + "/basicdata");
  var payload = helpers.send("GET", ep, "", staff);
  var data = payload.PatronBasicData || {};
  var patronOrgId = helpers.normalizePolarisId(data.PatronOrgID);
  var requestPickupBranchId = helpers.normalizePolarisId(data.RequestPickupBranchID);
  var patronCodeId = helpers.normalizePolarisId(data.PatronCodeID);
  return {
    PatronID: data.PatronID || "",
    Barcode: data.Barcode || barcode,
    EmailAddress: data.EmailAddress || "",
    NameFirst: data.NameFirst || "",
    NameLast: data.NameLast || "",
    PatronCodeID: patronCodeId,
    PatronCode: helpers.firstRowValue(data, ["PatronCode", "PatronCodeDescription", "PatronCodeName", "Description"]),
    PatronOrgID: patronOrgId,
    RequestPickupBranchID: requestPickupBranchId,
    // Keep this field raw from Polaris. Do not synthesize PatronOrg fallback here.
    PreferredPickupBranchID: requestPickupBranchId || "",
    CurrentPreferredPickupBranchID: requestPickupBranchId || "",
  };
}

function lookupPatron(staff, barcode) {
  return getPatronBasic(staff, barcode);
}

function getPatronHoldRequests(staff, patronBarcode) {
  var ep = helpers.endpoint("public", "patron/" + encodeURIComponent(patronBarcode) + "/holdrequests/all");
  var payload = helpers.send("GET", ep, "", staff);
  return helpers.normalizeRows(payload.PatronHoldRequestsGetRows || payload.PatronHoldRequestRows || payload.HoldRequests || payload, "", "PatronHoldRequestsGetRow");
}

function isActiveHoldRequest(row) {
  row = row || {};
  var status = helpers.firstRowValue(row, ["Status", "StatusDescription", "RequestStatus", "HoldStatus"]).toLowerCase();
  if (!status) return true;
  return status.indexOf("cancel") < 0 && status.indexOf("expire") < 0 && status.indexOf("filled") < 0 && status.indexOf("deleted") < 0;
}

function patronHasHoldForBib(staff, patronBarcode, bibId) {
  var targetBibId = helpers.normalizePolarisId(bibId);
  var rows = getPatronHoldRequests(staff, patronBarcode);
  for (var i = 0; i < rows.length; i++) {
    var row = rows[i] || {};
    var rowBibId = helpers.normalizePolarisId(helpers.firstRowValue(row, ["BibID", "BibId", "BibliographicRecordID", "BibliographicRecordId", "RecordID"]));
    if (rowBibId && rowBibId === targetBibId && isActiveHoldRequest(row)) {
      return true;
    }
  }
  return false;
}


function normalizePickupBranchRow(row) {
  row = row || {};

  var id = helpers.normalizePolarisId(
    helpers.firstRowValue(row, [
      "OrganizationID",
      "OrganizationId",
      "OrgID",
      "OrgId",
      "PickupBranchID",
      "PickupBranchId",
      "BranchID",
      "BranchId",
      "ID",
      "Id"
    ])
  );

  var label = helpers.firstRowValue(row, [
    "DisplayName",
    "OrganizationName",
    "Name",
    "BranchName",
    "Description",
    "Label"
  ]);

  if (!label && id) {
    label = "Branch " + id;
  }

  if (!id) return null;

  return {
    id: id,
    label: label || id
  };
}

function normalizePickupBranches(payload) {
  payload = payload || {};
  var rows = helpers.normalizeRows(
    payload.PickupBranchesRows ||
    payload.PickupBranchRows ||
    payload.Branches ||
    payload,
    "",
    "PickupBranchRow"
  );

  var seen = {};
  var out = [];

  for (var i = 0; i < rows.length; i++) {
    var branch = normalizePickupBranchRow(rows[i]);
    if (!branch || seen[branch.id]) continue;
    seen[branch.id] = true;
    out.push(branch);
  }

  out.sort(function(a, b) {
    return String(a.label || "").localeCompare(String(b.label || ""));
  });

  return out;
}

function getPickupBranches(staff, patronOrgId) {
  patronOrgId = helpers.normalizePolarisId(patronOrgId);
  if (!patronOrgId) {
    throw new Error("Missing patronOrgId for pickup branch lookup");
  }

  var c = helpers.cfg();
  var endpointConfig = Object.assign({}, c, { orgId: patronOrgId });
  var ep = helpers.endpoint("public", "pickupbranches", endpointConfig);
  var payload = helpers.send("GET", ep, "", staff, undefined, endpointConfig);

  return normalizePickupBranches(payload);
}

function updatePatronPreferredPickupBranch(staff, barcode, pickupBranchId, actor) {
  barcode = String(barcode || "").trim();
  pickupBranchId = helpers.normalizePolarisId(pickupBranchId);
  actor = actor || {};

  if (!barcode) {
    throw new Error("Missing patron barcode");
  }

  if (!pickupBranchId) {
    throw new Error("Missing pickup branch ID");
  }

  var c = helpers.cfg();
  var logonUserId = actor.polarisUserId || c.userId;

  if (typeof $app !== "undefined" && $app.logger) {
    var logger = $app.logger();
    if (logger && logger.info) {
      logger.info(
        "Updating Polaris patron preferred pickup branch",
        "barcode", "[REDACTED]",
        "pickupBranchId", pickupBranchId,
        "logonUserSource", actor.polarisUserId ? "actor" : "system"
      );
    }
  }

  var body = helpers.buildXml("PatronUpdateData", {
    LogonBranchID: c.orgId,
    LogonUserID: logonUserId,
    LogonWorkstationID: c.workstationId,
    RequestPickupBranchID: pickupBranchId
  });

  var ep = helpers.endpoint(
    "public",
    "patron/" + encodeURIComponent(barcode)
  );

  return helpers.send("PUT", ep, body, staff, "application/xml");
}

function getPatronHoldRequestsByStatus(staff, patronBarcode, statusFilter) {
  var ep = helpers.endpoint("public", "patron/" + encodeURIComponent(patronBarcode) + "/holdrequests/" + encodeURIComponent(statusFilter));
  var payload = helpers.send("GET", ep, "", staff);
  return helpers.normalizeRows(payload.PatronHoldRequestsGetRows || payload, "", "PatronHoldRequestsGetRow");
}

function checkPatronCheckouts(staff, barcode) {
  var ep = helpers.endpoint("public", "patron/" + encodeURIComponent(barcode) + "/itemsout/all");
  helpers.appendQuery(ep, "excludeecontent=true");
  var payload = helpers.send("GET", ep, "", staff);
  return payload.PatronItemsOutGetRows || [];
}

function patronSearchRows(payload) {
  var rows = payload && payload.PatronSearchRows ? payload.PatronSearchRows : [];
  if (rows.PatronSearchRow) {
    rows = rows.PatronSearchRow;
  }
  if (!Array.isArray(rows)) {
    rows = rows ? [rows] : [];
  }
  return rows;
}

function normalizePatronSearchRow(row) {
  row = row || {};
  var first = helpers.firstRowValue(row, ["NameFirst", "FirstName", "PatronFirstName"]);
  var last = helpers.firstRowValue(row, ["NameLast", "LastName", "PatronLastName"]);
  var fullName = helpers.firstRowValue(row, ["PatronFirstLastName", "PatronName", "Name", "DisplayName"]);
  if (!fullName) {
    fullName = [first, last].filter(Boolean).join(" ").trim();
  }
  return {
    patronId: helpers.firstRowValue(row, ["PatronID", "PatronId", "ID"]),
    barcode: helpers.firstRowValue(row, ["Barcode", "PatronBarcode"]),
    name: fullName,
    nameFirst: first,
    nameLast: last,
    organizationId: helpers.firstRowValue(row, ["OrganizationID", "OrganizationId", "PatronOrgID", "AssignedBranchID"]),
    libraryOrgName: helpers.firstRowValue(row, ["OrganizationName", "AssignedBranchName", "Library", "LibraryName"])
  };
}

function searchPatrons(staff, options) {
  options = options || {};
  var query = helpers.cleanSearchTerm(options.query);
  var limit = parseInt(options.limit || 10, 10) || 10;
  if (limit < 1) limit = 1;
  if (limit > 25) limit = 25;

  if (!query) {
    return {
      status: "error",
      query: query,
      totalMatches: 0,
      results: [],
      error: "missing_query"
    };
  }

  try {
    var accessToken = staff && staff.AccessToken ? String(staff.AccessToken).trim() : "";
    if (!accessToken) {
      return {
        status: "error",
        query: query,
        totalMatches: 0,
        results: [],
        error: "missing_staff_access_token"
      };
    }
    var ep = helpers.endpoint("protected", encodeURIComponent(accessToken) + "/search/patrons/boolean");
    helpers.appendQuery(ep,
      "q=" + encodeURIComponent("PATNF=" + helpers.cclQuotedValue(query)) +
      "&sortby=PATNF" +
      "&patronsperpage=" + encodeURIComponent(String(limit)) +
      "&page=1"
    );

    var payload = helpers.send("GET", ep, "", staff);
    var rows = patronSearchRows(payload);
    var totalMatches = Number(payload.TotalRecordsFound || rows.length || 0) || 0;
    var results = [];

    for (var i = 0; i < rows.length && results.length < limit; i++) {
      var result = normalizePatronSearchRow(rows[i]);
      if (result.barcode || result.patronId || result.name) {
        results.push(result);
      }
    }

    if (!results.length) {
      return {
        status: "not_found",
        query: query,
        totalMatches: totalMatches,
        results: [],
        error: ""
      };
    }

    return {
      status: "found",
      query: query,
      totalMatches: totalMatches || results.length,
      results: results,
      error: ""
    };
  } catch (err) {
    var errMsg = err && err.message ? err.message : String(err);
    if (/\(Code:\s*-1\)/.test(errMsg)) {
      return {
        status: "not_found",
        query: query,
        totalMatches: 0,
        results: [],
        error: ""
      };
    }
    return {
      status: "error",
      query: query,
      totalMatches: 0,
      results: [],
      error: errMsg
    };
  }
}

module.exports = {
  getPatronBasic: getPatronBasic,
  lookupPatron: lookupPatron,
  getPatronHoldRequests: getPatronHoldRequests,
  getPatronHoldRequestsByStatus: getPatronHoldRequestsByStatus,
  isActiveHoldRequest: isActiveHoldRequest,
  patronHasHoldForBib: patronHasHoldForBib,
  checkPatronCheckouts: checkPatronCheckouts,
  searchPatrons: searchPatrons,
  normalizePickupBranchRow: normalizePickupBranchRow,
  normalizePickupBranches: normalizePickupBranches,
  getPickupBranches: getPickupBranches,
  updatePatronPreferredPickupBranch: updatePatronPreferredPickupBranch,
};
