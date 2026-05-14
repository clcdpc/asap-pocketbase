const config = require(`${__hooks}/../lib/config.js`);
const crypto = require(`${__hooks}/../lib/crypto.js`);

const SENSITIVE_KEYS = new Set(["Barcode", "Password", "EmailAddress", "NameFirst", "NameLast", "PhoneNumber"]);

function redactPayload(payload) {
  if (!payload || typeof payload !== "object") return payload;
  const redacted = JSON.parse(JSON.stringify(payload));
  
  function walk(obj) {
    for (let key in obj) {
      if (SENSITIVE_KEYS.has(key)) {
        obj[key] = "[REDACTED]";
      } else if (typeof obj[key] === "object" && obj[key] !== null) {
        walk(obj[key]);
      }
    }
  }
  
  walk(redacted);
  return redacted;
}

function normalizePolarisId(value) {
  return String(value === undefined || value === null ? "" : value).trim();
}

function normalizeConfig(source) {
  source = source || {};
  return {
    host: source.host || "",
    accessId: source.accessId || "SuggestAPI",
    apiKey: source.apiKey || "",
    staffDomain: source.staffDomain || "",
    adminUser: source.adminUser || "",
    adminPassword: source.adminPassword || "",
    overridePassword: source.overridePassword || "",
    langId: source.langId || "1033",
    appId: source.appId || "100",
    orgId: source.orgId || "1",
    pickupOrgId: source.pickupOrgId || "0",
    requestingOrgId: source.requestingOrgId || "3",
    workstationId: source.workstationId || "1",
    userId: source.userId || "1",
  };
}

function cfg(polarisConfig) {
  var polaris = polarisConfig ? normalizeConfig(polarisConfig) : config.polaris();
  if (!polaris.host || !polaris.accessId || !polaris.apiKey) {
    throw new Error("Missing Polaris configuration");
  }
  return polaris;
}

function basePath(type, c) {
  c = c || cfg();
  // Based on CLC C# client: /[type]/v1/[lang]/[app]/[org]
  return "/PAPIService/REST/" + type + "/v1/" + c.langId + "/" + c.appId + "/" + c.orgId;
}

function hostUrl(c) {
  c = c || cfg();
  var host = c.host.replace(/\/+$/, "");
  if (host.indexOf("http://") !== 0 && host.indexOf("https://") !== 0) {
    host = "https://" + host;
  }
  return host;
}

function endpoint(type, path, c) {
  c = c || cfg();
  var rel = basePath(type, c) + "/" + path.replace(/^\/+/, "");
  var host = hostUrl(c);
  return {
    full: host + rel,
    signature: host + rel
  };
}

function signedHeaders(method, signatureUri, accessSecret, contentType, c) {
  c = c || cfg();
  var date = new Date().toUTCString();

  var signature = crypto.hmacSha1Base64(c.apiKey, method.toUpperCase() + signatureUri + date + (accessSecret || ""));

  return {
    "Authorization": "PWS " + c.accessId + ":" + signature,
    "Accept": "application/json",
    "Content-Type": contentType || "application/json",
    "Date": date,
    "PolarisDate": date,
  };
}

function send(method, ep, body, staffAuth, contentType, c) {
  c = c || cfg();
  var headers = signedHeaders(method, ep.signature, staffAuth ? staffAuth.AccessSecret : "", contentType, c);
  var requestBody = body || "";
  if (requestBody) {
    headers["Content-Length"] = String(utf8ByteLength(requestBody));
  }
  if (staffAuth && staffAuth.AccessToken) {
    headers["X-PAPI-AccessToken"] = staffAuth.AccessToken;
  }

  var result = $http.send({
    method: method,
    url: ep.full,
    headers: headers,
    body: requestBody,
    timeout: 30,
  });

  var payload = result.json || {};
  if (result.statusCode < 200 || result.statusCode > 299) {
    var msg = "Polaris request failed with HTTP " + result.statusCode + " [URL: " + ep.full + "]";
    if (payload && payload.ErrorMessage) msg += ": " + payload.ErrorMessage;
    // Log redacted payload for debugging if it's an error
    if ($app.logger) {
      $app.logger().error("Polaris API Error Details", "url", ep.full, "status", result.statusCode, "payload", JSON.stringify(redactPayload(payload)));
    }
    throw new Error(msg);
  }
  if (payload.PAPIErrorCode !== undefined && payload.PAPIErrorCode < 0) {
    throw new Error((payload.ErrorMessage || "Polaris returned an error") + " (Code: " + payload.PAPIErrorCode + ") [URL: " + ep.full + "]");
  }
  return payload;
}

function staffAuth(username, password, polarisConfig, domainOverride) {
  var c = cfg(polarisConfig);
  var ep = endpoint("protected", "authenticator/staff", c);
  return send("POST", ep, JSON.stringify({
    Domain: domainOverride !== undefined && domainOverride !== null ? String(domainOverride) : c.staffDomain,
    Username: username,
    Password: password,
  }), null, null, c);
}

function adminStaffAuth(polarisConfig) {
  var c = cfg(polarisConfig);
  return staffAuth(c.adminUser, c.adminPassword, c, c.staffDomain);
}

function getPatronBasic(staff, barcode) {
  var ep = endpoint("public", "patron/" + encodeURIComponent(barcode) + "/basicdata");
  var payload = send("GET", ep, "", staff);
  var data = payload.PatronBasicData || {};
  var patronOrgId = normalizePolarisId(data.PatronOrgID);
  var requestPickupBranchId = normalizePolarisId(data.RequestPickupBranchID);
  return {
    PatronID: data.PatronID || "",
    Barcode: data.Barcode || barcode,
    EmailAddress: data.EmailAddress || "",
    NameFirst: data.NameFirst || "",
    NameLast: data.NameLast || "",
    PatronOrgID: patronOrgId,
    RequestPickupBranchID: requestPickupBranchId,
    PreferredPickupBranchID: requestPickupBranchId || patronOrgId || "0",
  };
}

function authenticatePatron(barcode, password, staffAuth) {
  var staff = staffAuth || adminStaffAuth();
  if (!staff || !staff.AccessToken) {
    throw new Error("Admin staff authentication failed - check your Polaris settings.");
  }
  
  var ep = endpoint("public", "authenticator/patron");
  if ($app.logger) {
    $app.logger().info("Authenticating patron", "barcode", barcode);
  }
  
  send("POST", ep, JSON.stringify({
    Barcode: barcode,
    Password: password,
  }), staff);
  
  return getPatronBasic(staff, barcode);
}

function normalizeIdentifier(identifier) {
  var raw = String(identifier || "").trim();
  if (!raw) {
    return { ok: false, error: "missing_identifier", normalized: "" };
  }

  var normalized = raw.replace(/[\s\-_.:/]+/g, "").toUpperCase();
  if (!normalized) {
    return { ok: false, error: "missing_identifier", normalized: "" };
  }

  var validChars = /^[A-Z0-9]+$/;
  if (!validChars.test(normalized)) {
    return { ok: false, error: "invalid_characters", normalized: normalized };
  }

  return { ok: true, normalized: normalized };
}

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

function cleanSearchTerm(value) {
  return String(value || "").replace(/\s+/g, " ").trim();
}

function firstRowValue(row, names) {
  for (var i = 0; i < names.length; i++) {
    var value = row[names[i]];
    if (value !== undefined && value !== null && String(value).trim()) {
      return String(value).trim();
    }
  }
  return "";
}

function normalizedLabel(value) {
  return String(value || "")
    .replace(/:$/, "")
    .trim()
    .toLowerCase();
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
    labelSet[normalizedLabel(label)] = true;
  });

  for (var i = 0; i < rows.length; i++) {
    var row = rows[i] || {};
    var value = String(row.Value || "").trim();
    if (!value) continue;

    var elementId = String(row.ElementID || "").trim();
    var label = normalizedLabel(row.Label);

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

function cclQuotedValue(value) {
  return '"' + String(value || "").replace(/\\/g, "\\\\").replace(/"/g, '\\"') + '"';
}

function normalizeBibSearchRow(row) {
  row = row || {};
  return {
    bibId: firstRowValue(row, ["ControlNumber", "BibID", "BibId", "BibliographicRecordID", "BibliographicRecordId", "RecordID"]),
    title: firstRowValue(row, ["DisplayTitle", "FullTitle", "Title", "SortTitle"]),
    author: firstRowValue(row, ["Author", "PrimaryAuthor", "AuthorDisplay", "SortAuthor"]),
    publication: firstRowValue(row, ["PublicationDate", "PublicationYear", "PublishDate", "PublishedDate", "Date"]),
    format: firstRowValue(row, ["TypeOfMaterial", "PrimaryTypeOfMaterial", "MaterialType", "MaterialTypeDescription", "Format"]),
    physicalDescription: firstRowValue(row, ["Description"]),
    identifier: firstRowValue(row, ["ISBN", "ISSN", "UPC", "Identifier"]),
  };
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
  var first = firstRowValue(row, ["NameFirst", "FirstName", "PatronFirstName"]);
  var last = firstRowValue(row, ["NameLast", "LastName", "PatronLastName"]);
  var fullName = firstRowValue(row, ["PatronFirstLastName", "PatronName", "Name", "DisplayName"]);
  if (!fullName) {
    fullName = [first, last].filter(Boolean).join(" ").trim();
  }
  return {
    patronId: firstRowValue(row, ["PatronID", "PatronId", "ID"]),
    barcode: firstRowValue(row, ["Barcode", "PatronBarcode"]),
    name: fullName,
    nameFirst: first,
    nameLast: last,
    organizationId: firstRowValue(row, ["OrganizationID", "OrganizationId", "PatronOrgID", "AssignedBranchID"]),
    libraryOrgName: firstRowValue(row, ["OrganizationName", "AssignedBranchName", "Library", "LibraryName"])
  };
}

function searchBibs(staff, options) {
  options = options || {};
  var mode = String(options.mode || "identifier").trim().toLowerCase();
  var limit = parseInt(options.limit || 10, 10) || 10;
  if (limit < 1) limit = 1;
  if (limit > 25) limit = 25;

  var query = cleanSearchTerm(options.query);
  if (mode === "title_author") {
    query = cleanSearchTerm(query || (cleanSearchTerm(options.title) + " " + cleanSearchTerm(options.author)));
  }
  if (mode === "identifier") {
    var check = normalizeIdentifier(query);
    if (!check.ok) {
      return { status: "error", mode: mode, query: query, bibId: "", multipleMatches: false, totalMatches: 0, results: [], error: check.error };
    }
    query = check.normalized;
  } else if (["title", "author", "title_author"].indexOf(mode) < 0) {
    return { status: "error", mode: mode, query: query, bibId: "", multipleMatches: false, totalMatches: 0, results: [], error: "invalid_search_mode" };
  } else if (!query) {
    return { status: "error", mode: mode, query: query, bibId: "", multipleMatches: false, totalMatches: 0, results: [], error: "missing_query" };
  }

  try {
    var ep = endpoint("public", "search/bibs/keyword/kw");
    appendQuery(ep, "q=" + encodeURIComponent(query) + "&sortby=PD");

    var payload;
    try {
      payload = send("GET", ep, "", staff);
    } catch (err) {
      if (/\(Code:\s*-1\)/.test(err.message)) {
        return { status: "not_found", mode: mode, query: query, bibId: "", multipleMatches: false, totalMatches: 0, results: [], error: "" };
      }
      throw err;
    }
    var rows = bibSearchRows(payload);
    var totalMatches = Number(payload.TotalRecordsFound || rows.length || 0) || 0;
    var results = [];
    for (var i = 0; i < rows.length && results.length < limit; i++) {
      var result = normalizeBibSearchRow(rows[i]);
      if (result.bibId || result.title || result.author) {
        results.push(result);
      }
    }
    if (!results.length) {
      return { status: "not_found", mode: mode, query: query, bibId: "", multipleMatches: false, totalMatches: totalMatches, results: [], error: "" };
    }
    return {
      status: "found",
      mode: mode,
      query: query,
      bibId: results.length ? results[0].bibId : String(rows[0].ControlNumber || ""),
      multipleMatches: totalMatches > 1 || rows.length > 1,
      totalMatches: totalMatches,
      results: results,
      error: ""
    };
  } catch (err) {
    var errMsg = err && err.message ? err.message : String(err);
    return { status: "error", mode: mode, query: query, bibId: "", multipleMatches: false, totalMatches: 0, results: [], error: errMsg };
  }
}

function searchPatrons(staff, options) {
  options = options || {};
  var query = cleanSearchTerm(options.query);
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
    var ep = endpoint("protected", encodeURIComponent(accessToken) + "/search/patrons/boolean");
    appendQuery(ep,
      "q=" + encodeURIComponent("PATNF=" + cclQuotedValue(query)) +
      "&sortby=PATNF" +
      "&patronsperpage=" + encodeURIComponent(String(limit)) +
      "&page=1"
    );

    var payload = send("GET", ep, "", staff);
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

function searchBib(staff, identifier) {
  var result = searchBibs(staff, { mode: "identifier", query: identifier, limit: 10 });
  return {
    status: result.status,
    bibId: result.bibId,
    multipleMatches: result.multipleMatches,
    totalMatches: result.totalMatches,
    results: result.results || [],
    error: result.error || ""
  };
}

function lookupPatron(staff, barcode) {
  return getPatronBasic(staff, barcode);
}

function organizations(kind, staff) {
  kind = String(kind || "all").trim().toLowerCase();
  if (["all", "system", "library", "branch"].indexOf(kind) < 0) {
    kind = "all";
  }
  var ep = endpoint("public", "organizations/" + kind);
  var payload = send("GET", ep, "", staff || null);
  var rows = payload.OrganizationsGetRows || [];
  if (rows.OrganizationsGetRow) {
    rows = rows.OrganizationsGetRow;
  }
  if (!Array.isArray(rows)) {
    rows = rows ? [rows] : [];
  }
  return rows;
}

function normalizeRows(container, listName, rowName) {
  var rows = container || [];
  if (listName && rows[listName]) {
    rows = rows[listName];
  }
  if (rowName && rows[rowName]) {
    rows = rows[rowName];
  }
  if (!Array.isArray(rows) && rows && typeof rows === "object") {
    var keys = Object.keys(rows);
    for (var i = 0; i < keys.length; i++) {
      if (/Row$/.test(keys[i]) && rows[keys[i]]) {
        rows = rows[keys[i]];
        break;
      }
    }
  }
  if (!Array.isArray(rows)) {
    rows = rows ? [rows] : [];
  }
  return rows;
}

function getBibHoldings(staff, bibId) {
  var ep = endpoint("public", "bib/" + encodeURIComponent(bibId) + "/holdings");
  var payload = send("GET", ep, "", staff);
  return normalizeRows(payload.BibHoldingsGetRows || payload.BibHoldingsRows || payload.Holdings || payload, "", "BibHoldingsGetRow");
}

function booleanValue(value) {
  if (value === true) return true;
  if (value === false || value === undefined || value === null) return false;
  var text = String(value).trim().toLowerCase();
  return text === "true" || text === "1" || text === "yes" || text === "y";
}

function summarizeHoldability(holdings) {
  var rows = normalizeRows(holdings, "", "");
  var summary = {
    itemsTotal: 0,
    itemsIn: 0,
    holdableItems: 0,
    hasHoldableItems: false
  };
  for (var i = 0; i < rows.length; i++) {
    var row = rows[i] || {};
    var itemsTotal = parseInt(row.ItemsTotal || row.ItemTotal || row.TotalItems || 0, 10) || 0;
    var itemsIn = parseInt(row.ItemsIn || row.AvailableItems || 0, 10) || 0;
    summary.itemsTotal += itemsTotal || (row.Barcode ? 1 : 0);
    summary.itemsIn += itemsIn;
    if (booleanValue(row.Holdable)) {
      summary.holdableItems += Math.max(itemsTotal, 1);
    }
  }
  summary.hasHoldableItems = summary.holdableItems > 0;
  return summary;
}

function getPatronHoldRequests(staff, patronBarcode) {
  var ep = endpoint("public", "patron/" + encodeURIComponent(patronBarcode) + "/holdrequests/all");
  var payload = send("GET", ep, "", staff);
  return normalizeRows(payload.PatronHoldRequestsGetRows || payload.PatronHoldRequestRows || payload.HoldRequests || payload, "", "PatronHoldRequestsGetRow");
}

function isActiveHoldRequest(row) {
  row = row || {};
  var status = firstRowValue(row, ["Status", "StatusDescription", "RequestStatus", "HoldStatus"]).toLowerCase();
  if (!status) return true;
  return status.indexOf("cancel") < 0 && status.indexOf("expire") < 0 && status.indexOf("filled") < 0 && status.indexOf("deleted") < 0;
}

function patronHasHoldForBib(staff, patronBarcode, bibId) {
  var targetBibId = normalizePolarisId(bibId);
  var rows = getPatronHoldRequests(staff, patronBarcode);
  for (var i = 0; i < rows.length; i++) {
    var row = rows[i] || {};
    var rowBibId = normalizePolarisId(firstRowValue(row, ["BibID", "BibId", "BibliographicRecordID", "BibliographicRecordId", "RecordID"]));
    if (rowBibId && rowBibId === targetBibId && isActiveHoldRequest(row)) {
      return true;
    }
  }
  return false;
}

function placeHold(staff, bibId, patronId, options) {
  if (options === true) {
    throw new Error("placeHold test mode is not supported; use patronHasHoldForBib for read-only duplicate checks.");
  }
  options = options || {};
  var c = cfg();
  var ep = endpoint("public", "holdrequest");
  var body = buildXml("HoldRequestCreateData", {
    PatronID: patronId,
    BibID: bibId,
    PickupOrgID: c.pickupOrgId,
    WorkstationID: c.workstationId,
    UserID: c.userId,
    RequestingOrgID: c.requestingOrgId,
  });

  var payload = send("POST", ep, body, staff, "application/xml");
  if (payload.StatusType === 1) {
    return { ok: false, statusValue: payload.StatusValue || -1, payload: payload };
  }

  if (!options.noAutoReply && (payload.StatusType === 2 || payload.StatusType === 3) && payload.RequestGUID && String(payload.StatusValue || "") !== "6") {
    replyToHold(staff, payload, options.replyState || "3");
  }

  return { ok: true, statusValue: payload.StatusValue || 0, payload: payload };
}

function replyToHold(staff, holdPayload, state) {
  var c = cfg();
  var ep = endpoint("public", "holdrequest/" + encodeURIComponent(holdPayload.RequestGUID));
  var body = buildXml("HoldRequestReplyData", {
    TxnGroupQualifier: holdPayload.TxnGroupQualifer || holdPayload.TxnGroupQualifier || "",
    TxnQualifier: holdPayload.TxnQualifier || "",
    RequestingOrgID: c.requestingOrgId,
    Answer: "1",
    State: state || "3",
  });
  return send("PUT", ep, body, staff, "application/xml");
}

function checkPatronCheckouts(staff, barcode) {
  var ep = endpoint("public", "patron/" + encodeURIComponent(barcode) + "/itemsout/all");
  appendQuery(ep, "excludeecontent=true");
  var payload = send("GET", ep, "", staff);
  return payload.PatronItemsOutGetRows || [];
}

function appendQuery(ep, query) {
  if (!query) {
    return ep;
  }

  // Clean up the input query by removing leading ? or &
  var cleanQuery = query;
  if (cleanQuery.charAt(0) === "?" || cleanQuery.charAt(0) === "&") {
    cleanQuery = cleanQuery.substring(1);
  }

  // Determine separator based on existing URL
  var separator = ep.full.indexOf("?") !== -1 ? "&" : "?";

  ep.full += separator + cleanQuery;
  ep.signature += separator + cleanQuery;
  return ep;
}

function utf8ByteLength(value) {
  var str = String(value);
  var length = 0;
  for (var i = 0; i < str.length; i++) {
    var code = str.charCodeAt(i);
    if (code < 0x80) {
      length += 1;
    } else if (code < 0x800) {
      length += 2;
    } else if (code >= 0xd800 && code <= 0xdbff) {
      i++;
      length += 4;
    } else {
      length += 3;
    }
  }
  return length;
}

function escapeXml(value) {
  return String(value === undefined || value === null ? "" : value)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&apos;");
}

function buildXml(root, data) {
  var xml = '<?xml version="1.0" encoding="UTF-8"?>';
  xml += '<' + root + '>';
  for (var key in data) {
    if (Object.prototype.hasOwnProperty.call(data, key)) {
      xml += '<' + key + '>' + escapeXml(data[key]) + '</' + key + '>';
    }
  }
  xml += '</' + root + '>';
  return xml;
}

function getBib(staff, bibId) {
  var ep = endpoint("public", "bib/" + encodeURIComponent(bibId));
  var payload = send("GET", ep, "", staff);
  var rows = bibRows(payload);

  return {
    bibId: String(bibId || "").trim(),
    title: firstBibValueMatching(rows, [35], ["Title"], bibTitleRowAllowed),
    author: firstBibValue(rows, [18], ["Author"]),
    series: firstBibValue(rows, [19, 830], ["Series"]),
    format: firstBibValue(rows, [17], ["Format"]),
    identifier: firstBibValue(rows, [6, 24, 48], ["ISBN", "ISSN", "UPC"]),
    publisher: firstBibValue(rows, [2], ["Publisher", "Publisher, Date"]),
    description: firstBibValue(rows, [3], ["Description"])
  };
}


function reconcileRecord(app, staff, record, bibId, selectedPolarisResult) {
  if (!bibId) return;
  try {
    var bibInfo = getBib(staff, bibId);
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
  adminStaffAuth: adminStaffAuth,
  appendQuery: appendQuery,
  authenticatePatron: authenticatePatron,
  checkPatronCheckouts: checkPatronCheckouts,
  getBib: getBib,
  getBibHoldings: getBibHoldings,
  getPatronHoldRequests: getPatronHoldRequests,
  lookupPatron: lookupPatron,
  organizations: organizations,
  patronHasHoldForBib: patronHasHoldForBib,
  placeHold: placeHold,
  reconcileRecord: reconcileRecord,
  replyToHold: replyToHold,
  searchBib: searchBib,
  searchBibs: searchBibs,
  searchPatrons: searchPatrons,
  staffAuth: staffAuth,
  summarizeHoldability: summarizeHoldability,
};
