function body(e) {
  return e.requestInfo().body || {};
}

function requestHeader(e, name) {
  var lower = String(name || "").toLowerCase();
  var info = {};
  try {
    info = e.requestInfo() || {};
  } catch (err) { }
  var headers = info.headers || {};
  if (headers) {
    if (typeof headers.get === "function") {
      return headers.get(name) || headers.get(lower) || "";
    }
    return headers[name] || headers[lower] || "";
  }
  try {
    if (e.request && e.request.header && typeof e.request.header.get === "function") {
      return e.request.header.get(name) || e.request.header.get(lower) || "";
    }
  } catch (err2) { }
  try {
    if (e.request && e.request.headers && typeof e.request.headers.get === "function") {
      return e.request.headers.get(name) || e.request.headers.get(lower) || "";
    }
  } catch (err3) { }
  return "";
}

function queryValue(e, name) {
  var info = {};
  try {
    info = e.requestInfo() || {};
  } catch (err) { }

  if (info.query) {
    if (typeof info.query.get === "function") {
      var fromGet = info.query.get(name);
      if (fromGet !== undefined && fromGet !== null) {
        return String(fromGet);
      }
    }
    if (info.query[name] !== undefined && info.query[name] !== null) {
      return String(info.query[name]);
    }
  }

  var urls = [];
  if (info.url) {
    urls.push(String(info.url));
  }
  try {
    if (e.request && e.request.url) {
      urls.push(String(e.request.url));
    }
  } catch (err) { }
  try {
    if (e.request && e.request.URL) {
      urls.push(String(e.request.URL));
    }
  } catch (err) { }

  for (var i = 0; i < urls.length; i++) {
    var value = queryValueFromUrl(urls[i], name);
    if (value !== "") {
      return value;
    }
  }

  return "";
}

function safeDecodeURIComponent(str) {
  try {
    return decodeURIComponent(str);
  } catch (e) {
    return str;
  }
}

function queryValueFromUrl(url, name) {
  var marker = "?";
  var queryIndex = url.indexOf(marker);
  if (queryIndex < 0) {
    return "";
  }
  var query = url.slice(queryIndex + 1).split("#")[0];
  var parts = query.split("&");
  for (var i = 0; i < parts.length; i++) {
    var pair = parts[i].split("=");
    if (safeDecodeURIComponent(pair[0] || "") === name) {
      return safeDecodeURIComponent((pair.slice(1).join("=") || "").replace(/\+/g, " "));
    }
  }
  return "";
}

function parseJsonObject(value, fallback) {
  fallback = fallback || {};
  if (value && typeof value === "object" && !Array.isArray(value)) {
    return value;
  }
  if (typeof value !== "string") {
    return fallback;
  }
  var text = value.trim();
  if (!text) {
    return fallback;
  }
  try {
    var parsed = JSON.parse(text);
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
      return parsed;
    }
    if (typeof parsed === "string") {
      var nested = JSON.parse(parsed);
      if (nested && typeof nested === "object" && !Array.isArray(nested)) {
        return nested;
      }
    }
  } catch (err) {
    return fallback;
  }
  return fallback;
}

function boolValue(value, defaultValue) {
  if (value === undefined || value === null || value === "") {
    return defaultValue;
  }
  if (value === true || value === false) {
    return value;
  }
  var normalized = String(value).trim().toLowerCase();
  if (normalized === "true" || normalized === "1" || normalized === "on" || normalized === "yes") {
    return true;
  }
  if (normalized === "false" || normalized === "0" || normalized === "off" || normalized === "no") {
    return false;
  }
  return !!value;
}

function firstValue(source, names, defaultValue) {
  for (var i = 0; i < names.length; i++) {
    var value = source[names[i]];
    if (value !== undefined && value !== null) {
      return value;
    }
  }
  return defaultValue;
}

module.exports = {
  body: body,
  requestHeader: requestHeader,
  queryValue: queryValue,
  queryValueFromUrl: queryValueFromUrl,
  parseJsonObject: parseJsonObject,
  boolValue: boolValue,
  firstValue: firstValue,
};
