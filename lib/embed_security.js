function cleanLine(value) {
  return String(value || "").trim();
}

function originLines(value) {
  return String(value || "")
    .split(/[\r\n,]+/)
    .map(cleanLine)
    .filter(Boolean);
}

function rejectUnsafeCharacters(text) {
  return /[\s"'`;\\]/.test(text);
}

function normalizeWildcardOrigin(text) {
  var match = text.match(/^(https?):\/\/\*\.(.+)$/i);
  if (!match) return "";
  var scheme = match[1].toLowerCase();
  var host = String(match[2] || "").toLowerCase();
  if (!host || host.indexOf("/") >= 0 || host.indexOf("?") >= 0 || host.indexOf("#") >= 0) {
    throw new Error("Embed domains must be origins only, without paths or query strings.");
  }
  if (scheme !== "https") {
    throw new Error("Wildcard embed domains must use https://.");
  }
  if (!/^[a-z0-9.-]+(?::[0-9]+)?$/.test(host) || host.indexOf("..") >= 0 || host.indexOf(".") < 0) {
    throw new Error("Invalid wildcard embed domain: " + text);
  }
  return scheme + "://*." + host;
}

function normalizePlainOrigin(text) {
  var match = text.match(/^(https?):\/\/([^/?#]+)(.*)$/i);
  if (!match) {
    throw new Error("Invalid embed domain: " + text);
  }
  var protocol = match[1].toLowerCase();
  var host = String(match[2] || "").toLowerCase();
  var rest = String(match[3] || "");
  if (rest && rest !== "/") {
    throw new Error("Embed domains must be origins only, without paths or query strings.");
  }
  if (!host || host.indexOf("@") >= 0 || !/^[a-z0-9.:[\]-]+$/.test(host)) {
    throw new Error("Invalid embed domain: " + text);
  }
  var hostname = host.replace(/:[0-9]+$/, "");
  var isLocal = hostname === "localhost" || hostname === "127.0.0.1" || hostname === "[::1]";
  if (protocol !== "https" && !(protocol === "http" && isLocal)) {
    throw new Error("Embed domains must use https://, except localhost for development.");
  }
  return protocol + "://" + host;
}

function normalizeEmbedOrigin(text) {
  text = cleanLine(text);
  if (!text) return "";
  if (rejectUnsafeCharacters(text)) {
    throw new Error("Embed domains cannot contain whitespace, quotes, semicolons, or backslashes.");
  }
  if (/^https?:\/\/\*\./i.test(text)) {
    return normalizeWildcardOrigin(text);
  }
  return normalizePlainOrigin(text);
}

function normalizeEmbedAllowedOrigins(value) {
  var seen = {};
  var normalized = [];
  originLines(value).forEach(function (line) {
    var origin = normalizeEmbedOrigin(line);
    if (origin && !seen[origin]) {
      seen[origin] = true;
      normalized.push(origin);
    }
  });
  return normalized.join("\n");
}

function embedAllowedOriginsList(value) {
  return originLines(normalizeEmbedAllowedOrigins(value));
}

function frameAncestorsCsp(value) {
  var origins = embedAllowedOriginsList(value);
  return "frame-ancestors 'self'" + (origins.length ? " " + origins.join(" ") : "");
}

module.exports = {
  normalizeEmbedOrigin: normalizeEmbedOrigin,
  normalizeEmbedAllowedOrigins: normalizeEmbedAllowedOrigins,
  embedAllowedOriginsList: embedAllowedOriginsList,
  frameAncestorsCsp: frameAncestorsCsp
};
