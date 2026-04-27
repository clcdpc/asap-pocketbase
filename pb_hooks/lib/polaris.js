const config = require(`${__hooks}/lib/config.js`);
const crypto = require(`${__hooks}/lib/crypto.js`);
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
    // Log full payload for debugging if it's an error
    if ($app.logger) {
      $app.logger().error("Polaris API Error Details", "url", ep.full, "status", result.statusCode, "payload", JSON.stringify(payload));
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
  return {
    PatronID: data.PatronID || "",
    Barcode: data.Barcode || barcode,
    EmailAddress: data.EmailAddress || "",
    NameFirst: data.NameFirst || "",
    NameLast: data.NameLast || "",
    PatronOrgID: data.PatronOrgID || "",
    RequestPickupBranchID: data.RequestPickupBranchID || "",
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

function searchBib(staff, isbn) {
  var cleaned = String(isbn || "").replace(/[-\s]/g, "");
  if (!cleaned) {
    return "";
  }
  var ep = endpoint("public", "search/bibs/keyword/ISBN");
  appendQuery(ep, "q=" + encodeURIComponent(cleaned));

  var payload = send("GET", ep, "", staff);
  var rows = payload.BibSearchRows || [];
  if (!rows.length) {
    return "";
  }
  return String(rows[0].ControlNumber || "");
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
  var body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
    "<HoldRequestCreateData>" +
    "<PatronID>" + escapeXml(patronId) + "</PatronID>" +
    "<BibID>" + escapeXml(bibId) + "</BibID>" +
    "<PickupOrgID>" + escapeXml(c.pickupOrgId) + "</PickupOrgID>" +
    "<WorkstationID>" + escapeXml(c.workstationId) + "</WorkstationID>" +
    "<UserID>" + escapeXml(c.userId) + "</UserID>" +
    "<RequestingOrgID>" + escapeXml(c.requestingOrgId) + "</RequestingOrgID>" +
    "</HoldRequestCreateData>";

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
  var body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
    "<HoldRequestReplyData>" +
    "<TxnGroupQualifier>" + escapeXml(holdPayload.TxnGroupQualifer || holdPayload.TxnGroupQualifier || "") + "</TxnGroupQualifier>" +
    "<TxnQualifier>" + escapeXml(holdPayload.TxnQualifier || "") + "</TxnQualifier>" +
    "<RequestingOrgID>" + escapeXml(c.requestingOrgId) + "</RequestingOrgID>" +
    "<Answer>1</Answer>" +
    "<State>3</State>" +
    "</HoldRequestReplyData>";
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

module.exports = {
  adminStaffAuth: adminStaffAuth,
  authenticatePatron: authenticatePatron,
  checkPatronCheckouts: checkPatronCheckouts,
  getBib: getBib,
  lookupPatron: lookupPatron,
  organizations: organizations,
  placeHold: placeHold,
  searchBib: searchBib,
  staffAuth: staffAuth,
};
