const config = require("../config.js");
const crypto = require("../crypto.js");

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

function decodeByteArray(value) {
  if (!value) return "";
  if (typeof value === "string") return value;
  if (value.constructor && (value.constructor.name === "Uint8Array" || value.constructor.name === "Array")) {
    var str = "";
    for (var i = 0; i < value.length; i++) {
      str += String.fromCharCode(value[i]);
    }
    return str;
  }
  return String(value);
}

function normalizeNumeric(value) {
  var str = decodeByteArray(value).trim();
  if (!str) return 0;
  if (str.indexOf(",") !== -1) {
    str = str.split(",")[0];
  }
  return parseInt(str, 10) || 0;
}

function normalizePolarisId(value) {
  return decodeByteArray(value).trim();
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
  var isApp = polarisConfig && (
    typeof polarisConfig.findCollectionByNameOrId === "function" ||
    typeof polarisConfig.findRecordById === "function" ||
    typeof polarisConfig.findRecordsByFilter === "function"
  );
  var polaris = (polarisConfig && !isApp) ? normalizeConfig(polarisConfig) : config.polaris(isApp ? polarisConfig : null);
  if (!polaris.host || !polaris.accessId || !polaris.apiKey) {
    throw new Error("Missing Polaris configuration");
  }
  return polaris;
}

function basePath(type, c) {
  c = c || cfg();
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
    if ($app.logger) {
      $app.logger().error("Polaris API Error Details", 
        "url", ep.full, 
        "status", result.statusCode, 
        "payload", JSON.stringify(redactPayload(payload))
      );
    }

    var msg = "Polaris request failed (HTTP " + result.statusCode + ")";
    if (payload && payload.ErrorMessage) msg += ": " + payload.ErrorMessage;
    throw new Error(msg);
  }

  if (payload.PAPIErrorCode !== undefined && payload.PAPIErrorCode < 0) {
    if ($app.logger) {
      $app.logger().error("Polaris Application Error", 
        "url", ep.full, 
        "code", payload.PAPIErrorCode, 
        "message", payload.ErrorMessage, 
        "payload", JSON.stringify(redactPayload(payload))
      );
    }

    var errMsg = payload.ErrorMessage || "Polaris returned an error";
    throw new Error(errMsg + " (Code: " + payload.PAPIErrorCode + ")");
  }
  return payload;
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

function cclQuotedValue(value) {
  return '"' + String(value || "").replace(/\\/g, "\\\\").replace(/"/g, '\\"') + '"';
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

function booleanValue(value) {
  if (value === true) return true;
  if (value === false || value === undefined || value === null) return false;
  var text = String(value).trim().toLowerCase();
  return text === "true" || text === "1" || text === "yes" || text === "y";
}

function appendQuery(ep, query) {
  if (!query) {
    return ep;
  }
  var cleanQuery = query;
  if (cleanQuery.charAt(0) === "?" || cleanQuery.charAt(0) === "&") {
    cleanQuery = cleanQuery.substring(1);
  }
  var separator = ep.full.indexOf("?") !== -1 ? "&" : "?";
  ep.full += separator + cleanQuery;
  ep.signature += separator + cleanQuery;
  return ep;
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

module.exports = {
  redactPayload: redactPayload,
  decodeByteArray: decodeByteArray,
  normalizeNumeric: normalizeNumeric,
  normalizePolarisId: normalizePolarisId,
  cfg: cfg,
  endpoint: endpoint,
  send: send,
  normalizeIdentifier: normalizeIdentifier,
  cleanSearchTerm: cleanSearchTerm,
  firstRowValue: firstRowValue,
  normalizedLabel: normalizedLabel,
  cclQuotedValue: cclQuotedValue,
  normalizeRows: normalizeRows,
  booleanValue: booleanValue,
  appendQuery: appendQuery,
  buildXml: buildXml,
};
