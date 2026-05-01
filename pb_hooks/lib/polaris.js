const config = require(`${__hooks}/lib/config.js`);
const crypto = require(`${__hooks}/lib/crypto.js`);

function redactPayload(payload) {
  if (!payload || typeof payload !== "object") return payload;
  const redacted = JSON.parse(JSON.stringify(payload));
  const sensitiveKeys = ["Barcode", "Password", "EmailAddress", "NameFirst", "NameLast", "PhoneNumber"];
  
  function walk(obj) {
    for (let key in obj) {
      if (sensitiveKeys.indexOf(key) >= 0) {
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
    var msg = "Polaris request failed with HTTP " + result.statusCode;
    if (payload && payload.ErrorMessage) msg += ": " + payload.ErrorMessage;
    // Log redacted payload for debugging if it's an error
    if ($app.logger) {
      $app.logger().error("Polaris API Error Details", "url", ep.full, "status", result.statusCode, "payload", JSON.stringify(redactPayload(payload)));
    }
    throw new Error(msg);
  }
  if (payload.PAPIErrorCode !== undefined && payload.PAPIErrorCode < 0) {
    throw new Error(payload.ErrorMessage || "Polaris returned an error");
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

function searchBib(staff, identifier) {
  var check = normalizeIdentifier(identifier);
  if (!check.ok) {
    return { status: "error", bibId: "", multipleMatches: false, totalMatches: 0, error: check.error };
  }

  try {
    var ep = endpoint("public", "search/bibs/keyword/KW");
    appendQuery(ep, "q=" + encodeURIComponent(check.normalized) + "&sortby=PD");

    var payload = send("GET", ep, "", staff);
    var rows = bibSearchRows(payload);
    var totalMatches = Number(payload.TotalRecordsFound || rows.length || 0) || 0;
    if (!rows.length) {
      return { status: "not_found", bibId: "", multipleMatches: false, totalMatches: totalMatches, error: "" };
    }
    return {
      status: "found",
      bibId: String(rows[0].ControlNumber || ""),
      multipleMatches: totalMatches > 1 || rows.length > 1,
      totalMatches: totalMatches,
      error: ""
    };
  } catch (err) {
    return { status: "error", bibId: "", multipleMatches: false, totalMatches: 0, error: err && err.message ? err.message : String(err) };
  }
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

function placeHold(staff, bibId, patronId) {
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

  if ((payload.StatusType === 2 || payload.StatusType === 3) && payload.RequestGUID) {
    replyToHold(staff, payload);
  }

  return { ok: true, statusValue: payload.StatusValue || 0, payload: payload };
}

function replyToHold(staff, holdPayload) {
  var c = cfg();
  var ep = endpoint("public", "holdrequest/" + encodeURIComponent(holdPayload.RequestGUID));
  var body = buildXml("HoldRequestReplyData", {
    TxnGroupQualifier: holdPayload.TxnGroupQualifer || holdPayload.TxnGroupQualifier || "",
    TxnQualifier: holdPayload.TxnQualifier || "",
    RequestingOrgID: c.requestingOrgId,
    Answer: "1",
    State: "3",
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
  var suffix = query.charAt(0) === "?" ? query : "?" + query;
  ep.full += suffix;
  ep.signature += suffix;
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
  var rows = payload.BibGetRows || [];
  var result = { title: "", author: "" };
  for (var i = 0; i < rows.length; i++) {
    var row = rows[i];
    if (row.ElementID === 19 || row.Label === "Title:") {
      result.title = row.Value;
    } else if (row.ElementID === 18 || row.Label === "Author:") {
      result.author = row.Value;
    }
  }
  return result;
}


function reconcileRecord(app, staff, record, bibId) {
  if (!bibId) return;
  try {
    var bibInfo = getBib(staff, bibId);
    if (bibInfo) {
      var oldTitle = String(record.get("title") || "").trim();
      var oldAuthor = String(record.get("author") || "").trim();
      var pTitle = String(bibInfo.title || "").trim();
      var pAuthor = String(bibInfo.author || "").trim();

      if (pTitle && oldTitle !== pTitle && oldTitle.indexOf(pTitle + " (") !== 0) {
        record.set("title", pTitle + " (" + oldTitle + ")");
      }
      if (pAuthor && oldAuthor !== pAuthor && oldAuthor.indexOf(pAuthor + " (") !== 0) {
        record.set("author", pAuthor + " (" + oldAuthor + ")");
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
  lookupPatron: lookupPatron,
  organizations: organizations,
  placeHold: placeHold,
  reconcileRecord: reconcileRecord,
  searchBib: searchBib,
  staffAuth: staffAuth,
};
