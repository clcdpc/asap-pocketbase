function isByteArray(value) {
  if (!value || typeof value === "string") return false;
  try {
    if (typeof value.length === "number" && value.length > 0) {
      var first = value[0];
      return typeof first === "number" && first >= 0 && first <= 255;
    }
  } catch (e) {
    if (typeof $app !== "undefined" && $app && $app.logger) {
      $app.logger().warn("Swallowed error", "error", String(e));
    }
  }
  return false;
}

function decodeUtf8Bytes(bytes) {
  if (!bytes) return "";
  if (typeof bytes === "string") return bytes;
  if (typeof TextDecoder !== "undefined") {
    try {
      return new TextDecoder("utf-8").decode(new Uint8Array(bytes));
    } catch (e) {
      // Fall through to JS implementation
    }
  }
  var str = "";
  try {
    for (var i = 0; i < bytes.length;) {
      var b1 = bytes[i++];
      if (b1 < 0x80) {
        str += String.fromCharCode(b1);
      } else if (b1 >= 0xC2 && b1 < 0xE0 && i < bytes.length) {
        var b2 = bytes[i++];
        str += String.fromCharCode(((b1 & 0x1F) << 6) | (b2 & 0x3F));
      } else if (b1 >= 0xE0 && b1 < 0xF0 && i + 1 < bytes.length) {
        var b2a = bytes[i++];
        var b3 = bytes[i++];
        str += String.fromCharCode(((b1 & 0x0F) << 12) | ((b2a & 0x3F) << 6) | (b3 & 0x3F));
      } else if (b1 >= 0xF0 && b1 < 0xF5 && i + 2 < bytes.length) {
        var b2b = bytes[i++];
        var b3b = bytes[i++];
        var b4 = bytes[i++];
        var codePoint = ((b1 & 0x07) << 18) | ((b2b & 0x3F) << 12) | ((b3b & 0x3F) << 6) | (b4 & 0x3F);
        codePoint -= 0x10000;
        str += String.fromCharCode(0xD800 + (codePoint >> 10), 0xDC00 + (codePoint & 0x3FF));
      } else {
        str += "\uFFFD";
      }
    }
    return str;
  } catch (e) {
    return String(bytes);
  }
}

function decodeByteArray(value) {
  if (!isByteArray(value)) return null;
  for (var i = 0; i < value.length; i++) {
    if (value[i] === 9 || value[i] === 10 || value[i] === 13 || value[i] === 32) continue;
    if (value[i] !== 91 && value[i] !== 123) return null;
    break;
  }
  return decodeUtf8Bytes(value);
}

function parseJsonObject(value, fallback) {
  fallback = fallback || {};
  var decoded = decodeByteArray(value);
  if (decoded !== null) value = decoded;
  if (value && typeof value === "object" && !Array.isArray(value)) return value;
  if (typeof value !== "string") return fallback;
  var text = value.trim();
  if (!text) return fallback;
  try {
    var parsed = JSON.parse(text);
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) return parsed;
  } catch (err) {
    if (typeof $app !== "undefined" && $app && $app.logger) {
      $app.logger().warn("Swallowed error", "error", String(err));
    }
  }
  return fallback;
}

function parseJsonArray(value, fallback) {
  fallback = Array.isArray(fallback) ? fallback : [];
  var decoded = decodeByteArray(value);
  if (decoded !== null) value = decoded;
  if (Array.isArray(value)) return value;
  if (typeof value !== "string") return fallback;
  var text = value.trim();
  if (!text) return fallback;
  try {
    var parsed = JSON.parse(text);
    if (Array.isArray(parsed)) return parsed;
  } catch (err) {
    if (typeof $app !== "undefined" && $app && $app.logger) {
      $app.logger().warn("Swallowed error", "error", String(err));
    }
  }
  return fallback;
}

function cloneJson(value) {
  return JSON.parse(JSON.stringify(value));
}

function lines(value, fallback) {
  return String(value || "").split(/\r?\n/).map(function (line) { return line.trim(); }).filter(Boolean);
}

function optionIdFromLabel(label, fallback) {
  return String(label || "").trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/(^-|-$)/g, "") || fallback || "";
}

function normalizeOptionList(value, fallbackLabels) {
  var fallback = (fallbackLabels || []).map(function (label, index) {
    return { id: optionIdFromLabel(label, "option_" + (index + 1)), label: label, enabled: true, sortOrder: (index + 1) * 10 };
  });
  var raw;
  var decoded = decodeByteArray(value);
  if (decoded !== null) {
    value = decoded;
  } else if (isByteArray(value)) {
    var str = decodeUtf8Bytes(value);
    if (str && (str.indexOf("[") >= 0 || str.indexOf("{") >= 0)) value = str;
  }

  if (Array.isArray(value)) {
    if (value.length > 0 && typeof value[0] === "number") {
      var s = decodeUtf8Bytes(value);
      if (s && (s.trim().charAt(0) === "[" || s.trim().charAt(0) === "{")) {
        try {
          raw = JSON.parse(s);
        } catch (e) {
          raw = lines(s, []).map(function (label) { return { label: label }; });
        }
      } else {
        raw = value.map(function(v) { return { label: String(v) }; });
      }
    } else {
      raw = value;
    }
  } else if (typeof value === "string") {
    var trimmed = value.trim();
    if (trimmed.charAt(0) === "[") {
      raw = parseJsonArray(trimmed, []);
    } else {
      raw = lines(trimmed, []).map(function (label) { return { label: label }; });
    }
  } else {
    raw = [];
  }
  if (!raw.length) return fallback;
  var seenLabels = {};
  var seenIds = {};
  var numericLabelsCount = 0;
  var out = [];
  raw.forEach(function (item, index) {
    var obj = item && typeof item === "object" ? item : { label: item };
    var label = String(obj.label || obj.name || obj.value || "").trim();
    if (!label) return;

    if (/^\d+$/.test(label)) numericLabelsCount++;

    var labelKey = label.toLowerCase();
    if (seenLabels[labelKey]) return;
    seenLabels[labelKey] = true;
    var id = String(obj.id || "").trim() || optionIdFromLabel(label, "option_" + (index + 1));
    var baseId = id;
    var suffix = 2;
    while (seenIds[id]) {
      id = baseId + "_" + suffix++;
    }
    seenIds[id] = true;
    out.push({
      id: id,
      label: label,
      enabled: obj.enabled !== false,
      sortOrder: Number(obj.sortOrder || ((index + 1) * 10))
    });
  });

  // Safety check: if more than 50% of labels are numeric and there are many of them, 
  // it's likely a failed byte-array-to-string conversion. Fall back.
  if (out.length > 3 && (numericLabelsCount / out.length) > 0.5) {
    return fallback;
  }

  out.sort(function (a, b) { return (a.sortOrder || 0) - (b.sortOrder || 0); });
  return out.length ? out : fallback;
}

function enabledOptionLabels(options) {
  return normalizeOptionList(options, []).filter(function (opt) { return opt.enabled !== false; }).map(function (opt) { return opt.label; });
}

function clampInteger(value, fallback, min, max) {
  var val = parseInt(value, 10);
  if (isNaN(val)) return fallback;
  if (min !== undefined && val < min) return min;
  if (max !== undefined && val > max) return max;
  return val;
}

function envValue(name) {
  try {
    if (typeof $os !== "undefined" && $os.getenv) {
      return String($os.getenv(name) || "").trim();
    }
  } catch (err) {}
  if (typeof process !== "undefined" && process.env) {
    return String(process.env[name] || "").trim();
  }
  return "";
}

function envInteger(name, fallback, min, max) {
  return clampInteger(envValue(name), fallback, min, max);
}

function stripUrlHash(value) {
  value = String(value || "").trim();
  var hashIdx = value.indexOf("#");
  if (hashIdx >= 0) value = value.substring(0, hashIdx);
  return value.trim();
}

function normalizeStaffUrl(value) {
  value = stripUrlHash(value);
  if (!value) return "";
  if (!value.endsWith("/")) value += "/";
  if (!value.endsWith("/staff/")) value += "staff/";
  return value;
}

function normalizeLeapBibUrlPattern(value) {
  value = String(value || "").trim();
  if (!value) return "";
  if (value.indexOf("{{bibid}}") < 0) {
    if (!value.endsWith("/")) value += "/";
    value += "{{bibid}}";
  }
  return value;
}

function normalizeFormatIconUrlPattern(value) {
  value = String(value || "").trim();
  if (!value) return defaultFormatIconUrlPattern();
  // Basic safety check for placeholders
  var hasPlaceholder = [
    "{MARCTypeOfMaterialID}",
    "{MARCTypeOfMaterialID2}",
    "{id}",
    "{id2}",
    "{SearchCode}"
  ].some(function(p) { return value.indexOf(p) >= 0; });

  if (!hasPlaceholder) return defaultFormatIconUrlPattern();

  // Basic security check
  if (/^(javascript:|data:)/i.test(value)) return defaultFormatIconUrlPattern();

  return value;
}

function staffUrlFromEnv(value) {
  value = stripUrlHash(value);
  if (!value) return "";
  if (!value.endsWith("/")) value += "/";
  return value + "staff/";
}

function defaultStaffUrl() {
  var envStaff = envValue("ASAP_STAFF_URL");
  if (envStaff) return staffUrlFromEnv(envStaff);
  var envBase = envValue("ASAP_BASE_URL");
  if (envBase) return staffUrlFromEnv(envBase);
  return "http://localhost:8090/staff/";
}

function defaultFormatIconUrlPattern() {
  return "https://catalog.clcohio.org/polaris/themes/shared/formats/formatid{MARCTypeOfMaterialID2}.gif";
}

module.exports = {
  isByteArray: isByteArray,
  decodeUtf8Bytes: decodeUtf8Bytes,
  decodeByteArray: decodeByteArray,
  parseJsonObject: parseJsonObject,
  parseJsonArray: parseJsonArray,
  cloneJson: cloneJson,
  lines: lines,
  optionIdFromLabel: optionIdFromLabel,
  normalizeOptionList: normalizeOptionList,
  enabledOptionLabels: enabledOptionLabels,
  clampInteger: clampInteger,
  envValue: envValue,
  envInteger: envInteger,
  stripUrlHash: stripUrlHash,
  normalizeStaffUrl: normalizeStaffUrl,
  normalizeLeapBibUrlPattern: normalizeLeapBibUrlPattern,
  normalizeFormatIconUrlPattern: normalizeFormatIconUrlPattern,
  staffUrlFromEnv: staffUrlFromEnv,
  defaultStaffUrl: defaultStaffUrl,
  defaultFormatIconUrlPattern: defaultFormatIconUrlPattern,
};
