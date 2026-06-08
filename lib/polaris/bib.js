const helpers = require("./helpers.js");
const auth = require("./auth.js");
const patron = require("./patron.js");
const config = require("../config.js");

const PRIMARY_TOM_LABELS = {
  "1": "Book",
  "3": "Periodical",
  "10": "Sound Recording",
  "14": "Musical Score",
  "15": "Map",
  "19": "Computer File",
  "33": "DVD",
  "36": "eBook",
  "37": "Audio Book",
  "41": "eAudiobook",
  "52": "Audio Book on CD",
  "53": "Large Print"
};

const HOLD_REPLY_STATE_BY_STATUS_VALUE = {
  "3": "1", // item available locally
  "4": "2", // accept ILL policy
  "5": "3", // accept even with existing holds
  "6": "4", // no items attached / linked
  "7": "5"  // accept local hold policy / charge
};

function bibSearchRows(payload) {
  var rows = payload && payload.BibSearchRows ? payload.BibSearchRows : [];
  if (rows.BibSearchRow) {
    rows = rows.BibSearchRow;
  }
  if (!Array.isArray(rows)) {
    rows = rows ? [rows] : [];
  }
  return rows;
}

function bibRows(payload) {
  var rows = payload && payload.BibGetRows ? payload.BibGetRows : [];
  if (rows.BibGetRow) {
    rows = rows.BibGetRow;
  }
  if (!Array.isArray(rows)) {
    rows = rows ? [rows] : [];
  }
  return rows;
}

function firstBibValue(rows, elementIds, labels) {
  return firstBibValueMatching(rows, elementIds, labels, null);
}

function firstBibValueMatching(rows, elementIds, labels, acceptRow) {
  var idSet = {};
  var labelSet = {};

  elementIds.forEach(function(id) {
    idSet[String(id)] = true;
  });

  labels.forEach(function(label) {
    labelSet[helpers.normalizedLabel(label)] = true;
  });

  for (var i = 0; i < rows.length; i++) {
    var row = rows[i] || {};
    var value = String(row.Value || "").trim();
    if (!value) continue;

    var elementId = String(row.ElementID || "").trim();
    var label = helpers.normalizedLabel(row.Label);

    if (idSet[elementId] || labelSet[label]) {
      if (acceptRow && !acceptRow(row, value, elementId, label)) continue;
      return value;
    }
  }

  return "";
}

function bibTitleRowAllowed(row, value, elementId, label) {
  if (elementId === "830") return false;
  if (label === "series") return false;
  if (/^--/.test(String(value || "").trim())) return false;
  return true;
}

function getMARCTypeOfMaterialRows(staff) {
  var ep = helpers.endpoint("public", "marctypeofmaterials");
  var payload = helpers.send("GET", ep, "", staff || null);
  var rows = helpers.normalizeRows(payload.MARCTypeOfMaterialsRows || payload, "", "MARCTypeOfMaterialsRow");
  var out = {};
  for (var i = 0; i < rows.length; i++) {
    var row = rows[i] || {};
    var id = String(row.MARCTypeOfMaterialID || row.TypeOfMaterialID || row.ID || "").trim();
    var desc = String(row.Description || "").trim();
    var searchCode = String(row.SearchCode || "").trim();
    if (id && desc) {
      out[id] = {
        id: id,
        id2: padMaterialTypeId(id),
        searchCode: searchCode,
        description: desc
      };
    }
  }
  return out;
}

function padMaterialTypeId(id) {
  var raw = String(id || "").trim();
  if (/^\d$/.test(raw)) return "0" + raw;
  return raw;
}

function formatMaterialIconUrl(app, materialType) {
  var pattern = config.formatIconUrlPattern(app);
  if (!pattern) return "";
  return pattern
    .replace(/\{MARCTypeOfMaterialID\}/g, materialType.id)
    .replace(/\{MARCTypeOfMaterialID2\}/g, materialType.id2)
    .replace(/\{id\}/g, materialType.id)
    .replace(/\{id2\}/g, materialType.id2)
    .replace(/\{SearchCode\}/g, encodeURIComponent(materialType.searchCode || ""));
}

function normalizeMaterialTypesCache(cached) {
  if (!cached) return null;
  var data = cached;
  if (typeof cached === "string") {
    try { data = JSON.parse(cached); } catch (e) { return null; }
  } else if (typeof cached === "object" && cached !== null && cached.constructor && (cached.constructor.name === "Uint8Array" || cached.constructor.name === "Array")) {
    try {
      var str = "";
      for (var i = 0; i < cached.length; i++) { str += String.fromCharCode(cached[i]); }
      data = JSON.parse(str);
    } catch (e) { return null; }
  }

  if (!data || typeof data !== "object") return null;

  // v2 shape: { version: 2, rows: { "1": { ... } } }
  if (data.version === 2 && data.rows && typeof data.rows === "object") {
    return data.rows;
  }

  // v1 shape: { "1": "Book" }
  var rows = {};
  Object.keys(data).forEach(function(id) {
    if (typeof data[id] === "string") {
      rows[id] = {
        id: id,
        id2: padMaterialTypeId(id),
        searchCode: "",
        description: data[id]
      };
    } else if (data[id] && typeof data[id] === "object") {
      rows[id] = data[id];
    }
  });
  return Object.keys(rows).length > 0 ? rows : null;
}

var _materialTypeDetailsMap = null;
var _materialTypeDetailsLastCheck = 0;

function getMaterialTypeDetailsMap(app) {
  var now = new Date();
  if (_materialTypeDetailsMap && (now.getTime() - _materialTypeDetailsLastCheck < 60000)) {
    return _materialTypeDetailsMap;
  }
  _materialTypeDetailsLastCheck = now.getTime();

  app = app || $app;
  var settings = null;
  try {
    settings = app.findRecordById("polaris_settings", "polaris00000010");
  } catch (err) {
    return {};
  }

  var cached = settings.get("materialTypesCache");
  var lastUpdated = settings.get("materialTypesCacheUpdated");
  var expirationMs = 24 * 60 * 60 * 1000; // 24 hours

  var isExpired = !lastUpdated || (now.getTime() - new Date(lastUpdated).getTime() > expirationMs);
  var rows = normalizeMaterialTypesCache(cached);

  if (rows && !isExpired) {
    _materialTypeDetailsMap = rows;
    return rows;
  }

  try {
    var fetched = getMARCTypeOfMaterialRows(auth.adminStaffAuth());
    if (fetched && Object.keys(fetched).length > 0) {
      settings.set("materialTypesCache", {
        version: 2,
        rows: fetched
      });
      settings.set("materialTypesCacheUpdated", now.toISOString());
      app.save(settings);
      _materialTypeDetailsMap = fetched;
      return fetched;
    }
  } catch (err) {
    if (app && app.logger) {
      app.logger().warn("Failed to fetch Polaris material types", "error", String(err));
    }
  }

  return rows || {};
}

function getMaterialTypeDetails(app, id) {
  var details = getMaterialTypeDetailsMap(app);
  return details[id] || null;
}

function getMaterialTypesMap(app) {
  var details = getMaterialTypeDetailsMap(app);
  var map = {};
  Object.keys(details).forEach(function(id) {
    map[id] = details[id].description;
  });
  
  if (Object.keys(map).length === 0) return PRIMARY_TOM_LABELS;
  return map;
}

function getBibFormatLabel(app, row, bibGetFormat) {
  var tomDesc = String(row.MaterialTypeDescription || row.MaterialType || row.materialTypeDesc || "").trim();
  if (tomDesc && !/^\d+$/.test(tomDesc)) return tomDesc;

  var tomId = String(row.PrimaryTypeOfMaterial || row.primaryTomId || "").trim();
  
  var dynamicMap = getMaterialTypesMap(app);
  if (dynamicMap && dynamicMap[tomId]) return dynamicMap[tomId];

  if (PRIMARY_TOM_LABELS[tomId]) return PRIMARY_TOM_LABELS[tomId];

  return bibGetFormat || tomDesc || tomId || "Unknown";
}

function normalizeBibSearchRow(app, row) {
  row = row || {};
  var primaryTomId = String(row.PrimaryTypeOfMaterial || "").trim();
  var materialType = getMaterialTypeDetails(app, primaryTomId);
  var formatLabel = getBibFormatLabel(app, row);

  return {
    bibId: helpers.firstRowValue(row, ["ControlNumber", "BibID", "BibId", "BibliographicRecordID", "BibliographicRecordId", "RecordID"]),
    title: helpers.firstRowValue(row, ["DisplayTitle", "FullTitle", "Title", "SortTitle"]),
    author: helpers.firstRowValue(row, ["Author", "PrimaryAuthor", "AuthorDisplay", "SortAuthor"]),
    publication: helpers.firstRowValue(row, ["PublicationDate", "PublicationYear", "PublishDate", "PublishedDate", "Date"]),
    format: formatLabel,
    primaryTomId: primaryTomId,
    materialTypeDesc: String(row.MaterialTypeDescription || ""),
    formatIconUrl: materialType ? formatMaterialIconUrl(app, materialType) : "",
    formatIconAlt: materialType ? materialType.description : formatLabel,
    materialTypeSearchCode: materialType ? materialType.searchCode : "",
    physicalDescription: helpers.firstRowValue(row, ["Description"]),
    identifier: helpers.firstRowValue(row, ["ISBN", "ISSN", "UPC", "Identifier"]),
    score: 0
  };
}

function buildBibSearchRequests(options) {
  var mode = String(options.mode || "title").trim().toLowerCase();
  var query = helpers.cleanSearchTerm(options.query);
  var title = helpers.cleanSearchTerm(options.title || query);
  var author = helpers.cleanSearchTerm(options.author);

  switch (mode) {
    case "title":
      return [{ type: "keyword", qualifier: "TI", q: title, sortby: "RELEVANCE" }];

    case "author":
      return [{ type: "keyword", qualifier: "AU", q: author || query, sortby: "AU" }];

    case "identifier":
      var check = helpers.normalizeIdentifier(query);
      var normalized = check.ok ? check.normalized : query;
      return [
        { type: "keyword", qualifier: "ISBN", q: normalized, sortby: "PDTI" },
        { type: "keyword", qualifier: "UPC", q: normalized, sortby: "PDTI" },
        { type: "keyword", qualifier: "LCCN", q: normalized, sortby: "PDTI" }
      ];

    case "title_author":
      var requests = [];
      if (title && author) {
        requests.push({ 
          type: "boolean", 
          q: "TI=" + helpers.cclQuotedValue(title) + " AND AU=" + helpers.cclQuotedValue(author), 
          sortby: "PDTI" 
        });
      }
      requests.push({ 
        type: "keyword", 
        qualifier: "TI", 
        q: title, 
        sortby: "RELEVANCE", 
        postFilterAuthor: author 
      });
      return requests;

    default:
      return [{ type: "keyword", qualifier: "KW", q: query, sortby: "RELEVANCE" }];
  }
}

function scoreBibResult(result, options) {
  var score = 0;
  var targetTitle = helpers.normalizedLabel(options.title || options.query);
  var targetAuthor = helpers.normalizedLabel(options.author);
  var targetId = helpers.normalizeIdentifier(options.query).normalized;

  var rowTitle = helpers.normalizedLabel(result.title);
  var rowAuthor = helpers.normalizedLabel(result.author);
  var rowId = helpers.normalizeIdentifier(result.identifier).normalized;

  if (targetId && rowId && rowId.indexOf(targetId) !== -1) {
    score += 200;
  }

  if (targetTitle && rowTitle === targetTitle) {
    score += 100;
  } else if (targetTitle && rowTitle.indexOf(targetTitle) === 0) {
    score += 40;
  }

  if (targetAuthor && rowAuthor.indexOf(targetAuthor) !== -1) {
    score += 30;
  }

  var year = parseInt(String(result.publication || "").replace(/\D/g, "").substring(0, 4), 10);
  if (year > 1900 && year <= new Date().getFullYear()) {
    score += Math.min(5, (year - 1900) / 20);
  }

  return score;
}

function searchBibs(app, staff, options) {
  options = options || {};
  var limit = parseInt(options.limit || 10, 10) || 10;
  if (limit < 1) limit = 1;
  if (limit > 25) limit = 25;

  var requests = [];
  try {
    requests = buildBibSearchRequests(options);
  } catch (err) {
    return { status: "error", mode: options.mode, query: options.query, bibId: "", multipleMatches: false, totalMatches: 0, results: [], error: err.message };
  }

  var allResults = [];
  var seenBibIds = {};
  var totalMatches = 0;
  var filteredByMaterialType = false;

  for (var r = 0; r < requests.length; r++) {
    var req = requests[r];
    try {
      var path = req.type === "boolean" ? "search/bibs/boolean" : ("search/bibs/keyword/" + (req.qualifier || "KW"));
      var ep = helpers.endpoint("public", path);
      var queryParams = "q=" + encodeURIComponent(req.q) + "&sortby=" + (req.sortby || "RELEVANCE") + "&bibsperpage=" + limit + "&page=1&notran=1";
      helpers.appendQuery(ep, queryParams);

      var payload;
      try {
        payload = helpers.send("GET", ep, "", staff);
      } catch (err) {
        if (/\(Code:\s*-1\)/.test(err.message)) continue;
        throw err;
      }

      var rows = bibSearchRows(payload);
      totalMatches = Math.max(totalMatches, Number(payload.TotalRecordsFound || rows.length || 0) || 0);

      var NON_HOLDABLE_TOMS = { "36": true, "41": true };

      for (var i = 0; i < rows.length; i++) {
        var result = normalizeBibSearchRow(app, rows[i]);
        if (!result.bibId || seenBibIds[result.bibId]) continue;
        if (NON_HOLDABLE_TOMS[result.primaryTomId]) {
          filteredByMaterialType = true;
          continue;
        }

        if (req.postFilterAuthor) {
          var targetAuthor = helpers.normalizedLabel(req.postFilterAuthor);
          var rowAuthor = helpers.normalizedLabel(result.author);
          if (rowAuthor.indexOf(targetAuthor) === -1) continue;
        }

        result.score = scoreBibResult(result, options);
        allResults.push(result);
        seenBibIds[result.bibId] = true;
      }

      if (allResults.length >= limit) break;
    } catch (err) {
      if ($app.logger) $app.logger().error("Polaris search request failed", "error", err.message, "request", JSON.stringify(req));
    }
  }

  allResults.sort(function(a, b) {
    return b.score - a.score;
  });

  var results = allResults.slice(0, limit);

  if (!results.length) {
    return { status: "not_found", mode: options.mode, query: options.query, bibId: "", multipleMatches: false, totalMatches: totalMatches, results: [], error: "", filteredByMaterialType: filteredByMaterialType };
  }

  return {
    status: "found",
    mode: options.mode,
    query: options.query,
    bibId: results[0].bibId,
    multipleMatches: totalMatches > 1 || results.length > 1,
    totalMatches: totalMatches,
    results: results,
    error: "",
    filteredByMaterialType: filteredByMaterialType
  };
}

function searchBib(app, staff, identifier) {
  var result = searchBibs(app, staff, { mode: "identifier", query: identifier, limit: 10 });
  return {
    status: result.status,
    bibId: result.bibId,
    multipleMatches: result.multipleMatches,
    totalMatches: result.totalMatches,
    results: result.results || [],
    error: result.error || "",
    filteredByMaterialType: result.filteredByMaterialType || false
  };
}

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

function getBib(app, staff, bibId) {
  var ep = helpers.endpoint("public", "bib/" + encodeURIComponent(bibId));
  var payload = helpers.send("GET", ep, "", staff);
  var rows = bibRows(payload);

  var primaryTomId = String(payload.PrimaryTypeOfMaterial || "").trim();
  var materialType = getMaterialTypeDetails(app, primaryTomId);
  var formatLabel = getBibFormatLabel(app, payload, firstBibValue(rows, [17], ["Format"]));

  return {
    bibId: String(bibId || "").trim(),
    title: firstBibValueMatching(rows, [35], ["Title"], bibTitleRowAllowed),
    author: firstBibValue(rows, [18], ["Author"]),
    series: firstBibValue(rows, [19, 830], ["Series"]),
    format: formatLabel,
    primaryTomId: primaryTomId,
    formatIconUrl: materialType ? formatMaterialIconUrl(app, materialType) : "",
    formatIconAlt: materialType ? materialType.description : formatLabel,
    identifier: firstBibValue(rows, [6, 24, 48], ["ISBN", "ISSN", "UPC"]),
    publisher: firstBibValue(rows, [2], ["Publisher", "Publisher, Date"]),
    description: firstBibValue(rows, [3], ["Description"])
  };
}

function mergeCatalogValue(catalogValue, oldValue) {
  var catalog = String(catalogValue || "").trim();
  var old = String(oldValue || "").trim();
  if (!catalog) return old;
  if (!old || old === catalog) return catalog;
  if (old.indexOf(catalog + " (") === 0) return old;

  var oldBase = old.replace(/\s+\([^()]*\)\s*$/, "").trim();
  if (oldBase && (oldBase === catalog || oldBase.indexOf(catalog) === 0 || catalog.indexOf(oldBase) === 0)) {
    return old;
  }

  return catalog + " (" + old + ")";
}

function reconcileRecord(app, staff, record, bibId, selectedPolarisResult) {
  if (!bibId) return;
  try {
    var bibInfo = getBib(app, staff, bibId);
    if (bibInfo) {
      var oldTitle = String(record.get("title") || "").trim();
      var oldAuthor = String(record.get("author") || "").trim();
      var pTitle = String(bibInfo.title || "").trim();
      var pAuthor = String(bibInfo.author || "").trim();
      var selectedBibId = String(selectedPolarisResult && selectedPolarisResult.bibId || "").trim();
      var selectedTitle = String(selectedPolarisResult && selectedPolarisResult.title || "").trim();
      var selectedMatchesBib = selectedBibId && selectedBibId === String(bibId || "").trim();

      if (selectedMatchesBib && selectedTitle && !pTitle) {
        pTitle = selectedTitle;
      } else if (selectedMatchesBib && selectedTitle && pTitle && selectedTitle !== pTitle) {
        if (app && app.logger) {
          app.logger().warn("Selected Polaris title differs from BIB detail title; preserving selected title", "bibId", bibId, "selectedTitle", selectedTitle, "detailTitle", pTitle);
        }
        pTitle = selectedTitle;
      }

      var mergedTitle = mergeCatalogValue(pTitle, oldTitle);
      if (mergedTitle && mergedTitle !== oldTitle) {
        record.set("title", mergedTitle);
      }
      var mergedAuthor = mergeCatalogValue(pAuthor, oldAuthor);
      if (mergedAuthor && mergedAuthor !== oldAuthor) {
        record.set("author", mergedAuthor);
      }
    }
  } catch (err) {
    if (app && app.logger) {
      app.logger().warn("Reconciliation failed", "bibId", bibId, "error", String(err));
    }
  }
}

module.exports = {
  getMaterialTypeDetailsMap: getMaterialTypeDetailsMap,
  getMaterialTypeDetails: getMaterialTypeDetails,
  getMaterialTypesMap: getMaterialTypesMap,
  normalizeMaterialTypesCache: normalizeMaterialTypesCache,
  getMARCTypeOfMaterialRows: getMARCTypeOfMaterialRows,
  getMARCTypeOfMaterials: getMARCTypeOfMaterialRows, // backward compatibility
  normalizeBibSearchRow: normalizeBibSearchRow,
  searchBibs: searchBibs,
  searchBib: searchBib,
  getBibHoldings: getBibHoldings,
  summarizeHoldability: summarizeHoldability,
  summarizeHoldingsByLibrary: summarizeHoldingsByLibrary,
  placeHold: placeHold,
  replyToHold: replyToHold,
  getBib: getBib,
  reconcileRecord: reconcileRecord,
};
