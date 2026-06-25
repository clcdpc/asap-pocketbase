const helpers = require("../helpers.js");
const { config } = require("../../config.js");

const HOLD_REPLY_STATE_BY_STATUS_VALUE = {
  "3": "1", // item available locally
  "4": "2", // accept ILL policy
  "5": "3", // accept even with existing holds
  "6": "4", // no items attached / linked
  "7": "5"  // accept local hold policy / charge
};

function getBibHoldings(staff, bibId) {
  var ep = helpers.endpoint("public", "bib/" + encodeURIComponent(bibId) + "/holdings");
  var payload = helpers.send("GET", ep, "", staff);
  return helpers.normalizeRows(payload.BibHoldingsGetRows || payload.BibHoldingsRows || payload.Holdings || payload, "", "BibHoldingsGetRow");
}

function summarizeHoldability(holdings) {
  var rows = helpers.normalizeRows(holdings, "", "");
  var summary = {
    itemsTotal: 0,
    itemsIn: 0,
    holdableItems: 0,
    hasHoldableItems: false
  };
  for (var i = 0; i < rows.length; i++) {
    var row = rows[i] || {};
    var itemsTotal = 1;
    var itemsIn = helpers.normalizeNumeric(row.ItemsIn || row.AvailableItems);
    if (itemsIn > 1) itemsIn = 1;

    summary.itemsTotal += itemsTotal;
    summary.itemsIn += itemsIn;

    if (helpers.booleanValue(row.Holdable)) {
      summary.holdableItems += itemsTotal;
    }
  }
  summary.hasHoldableItems = summary.holdableItems > 0;
  return summary;
}

function summarizeHoldingsByLibrary(holdings, myLibraryOrgId, resolveParentLibrary) {
  var rows = helpers.normalizeRows(holdings, "", "");
  var summary = {
    myLibraryCount: 0,
    otherLibraryCount: 0,
    consortiumCount: 0,
    isHoldable: false,
    hasHoldableAtMyLibrary: false,
    locations: []
  };

  myLibraryOrgId = String(myLibraryOrgId || "").trim();

  for (var i = 0; i < rows.length; i++) {
    var row = rows[i] || {};
    var locationId = helpers.normalizePolarisId(row.LocationID || row.OrganizationID || row.OrgID);
    if (!locationId) continue;

    var itemsTotal = 1;
    var itemsIn = helpers.normalizeNumeric(row.ItemsIn || row.AvailableItems);
    if (itemsIn > 1) itemsIn = 1;

    var holdable = helpers.booleanValue(row.Holdable);
    var owningLibraryOrgId = resolveParentLibrary ? resolveParentLibrary(locationId) : locationId;
    var isMyLibrary = owningLibraryOrgId === myLibraryOrgId;

    if (isMyLibrary) {
      summary.myLibraryCount += itemsTotal;
      if (holdable) summary.hasHoldableAtMyLibrary = true;
    } else {
      summary.otherLibraryCount += itemsTotal;
    }

    summary.consortiumCount += itemsTotal;

    if (holdable) {
      summary.isHoldable = true;
    }

    summary.locations.push({
      locationId: locationId,
      locationName: row.LocationName || row.OrganizationName || "",
      owningLibraryOrgId: owningLibraryOrgId,
      itemsTotal: itemsTotal,
      itemsIn: itemsIn,
      holdable: holdable
    });
  }
  return summary;
}

function placeHold(staff, bibId, patronId, options) {
  if (options === true) {
    throw new Error("placeHold test mode is not supported; use patronHasHoldForBib for read-only duplicate checks.");
  }
  options = options || {};
  var c = helpers.cfg();
  var ep = helpers.endpoint("public", "holdrequest");

  var body = helpers.buildXml("HoldRequestCreateData", {
    PatronID: patronId,
    BibID: bibId,
    PickupOrgID: options.pickupOrgId || c.pickupOrgId,
    WorkstationID: c.workstationId,
    UserID: c.userId,
    RequestingOrgID: options.requestingOrgId || c.requestingOrgId,
  });

  var payload = helpers.send("POST", ep, body, staff, "application/xml");
  if (payload.StatusType === 1) {
    return { ok: false, statusType: 1, statusValue: payload.StatusValue || -1, payload: payload };
  }

  if (!options.noAutoReply && payload.StatusType === 3 && payload.RequestGUID) {
    var statusValue = String(payload.StatusValue || "");
    var replyState = HOLD_REPLY_STATE_BY_STATUS_VALUE[statusValue];

    var autoAcceptable = ["3", "4", "5"];
    if (replyState && autoAcceptable.indexOf(statusValue) !== -1) {
      replyToHold(staff, payload, replyState, options);
      return { ok: true, statusType: payload.StatusType, statusValue: payload.StatusValue || 0, payload: payload, replied: true };
    }
  }

  return { ok: true, statusType: payload.StatusType, statusValue: payload.StatusValue || 0, payload: payload };
}

function replyToHold(staff, holdPayload, state, options) {
  options = options || {};
  var c = helpers.cfg();
  var ep = helpers.endpoint("public", "holdrequest/" + encodeURIComponent(holdPayload.RequestGUID));
  var body = helpers.buildXml("HoldRequestReplyData", {
    TxnGroupQualifier: holdPayload.TxnGroupQualifer || holdPayload.TxnGroupQualifier || "",
    TxnQualifier: holdPayload.TxnQualifier || "",
    RequestingOrgID: options.requestingOrgId || c.requestingOrgId,
    Answer: "1", // Yes
    State: state || "3",
  });
  return helpers.send("PUT", ep, body, staff, "application/xml");
}

module.exports = { getBibHoldings, summarizeHoldability, summarizeHoldingsByLibrary, placeHold, replyToHold };
