const FIELD_TYPES = ["text", "textarea", "select"];
const FIELD_MODES = ["required", "optional", "hidden"];
const MAX_LENGTHS = {
  text: 250,
  textarea: 2000
};

function normalizeKey(value) {
  return String(value || "")
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "_")
    .replace(/^_+|_+$/g, "");
}

function normalizeString(value) {
  return String(value || "").trim();
}

function normalizeSortOrder(value, fallback) {
  var number = Number(value);
  if (!isFinite(number)) return fallback;
  return number;
}

function parseArray(value) {
  if (Array.isArray(value)) {
    if (value.length && typeof value[0] === "number") {
      try {
        var decoded = String.fromCharCode.apply(null, value);
        var parsedBytes = JSON.parse(decoded);
        return Array.isArray(parsedBytes) ? parsedBytes : [];
      } catch (err) {}
    }
    return value;
  }
  if (typeof value === "string" && value.trim().charAt(0) === "[") {
    try {
      var parsed = JSON.parse(value);
      return Array.isArray(parsed) ? parsed : [];
    } catch (err2) {
      return [];
    }
  }
  return [];
}

function normalizeOptions(options) {
  options = parseArray(options);

  var seen = {};
  var normalized = [];
  for (var i = 0; i < options.length; i++) {
    var option = options[i] || {};
    var label = normalizeString(option.label || option.name || option.value);
    if (!label) continue;

    var id = normalizeKey(option.id || label);
    if (!id || seen[id]) continue;
    seen[id] = true;

    normalized.push({
      id: id,
      label: label,
      enabled: option.enabled !== false,
      sortOrder: normalizeSortOrder(option.sortOrder, (i + 1) * 10)
    });
  }

  normalized.sort(function (a, b) {
    var sortDiff = a.sortOrder - b.sortOrder;
    if (sortDiff !== 0) return sortDiff;
    return a.label.localeCompare(b.label);
  });

  return normalized;
}

function normalizeDefinitions(definitions) {
  definitions = parseArray(definitions);

  var seen = {};
  var normalized = [];
  for (var i = 0; i < definitions.length; i++) {
    var definition = definitions[i] || {};
    var label = normalizeString(definition.label);
    var type = normalizeString(definition.type);
    var key = normalizeKey(definition.key || label);

    if (!key || seen[key] || !label || FIELD_TYPES.indexOf(type) === -1) continue;
    seen[key] = true;

    normalized.push({
      key: key,
      label: label,
      type: type,
      helpText: normalizeString(definition.helpText),
      enabled: definition.enabled !== false,
      sortOrder: normalizeSortOrder(definition.sortOrder, (i + 1) * 10),
      options: type === "select" ? normalizeOptions(definition.options) : []
    });
  }

  normalized.sort(function (a, b) {
    var sortDiff = a.sortOrder - b.sortOrder;
    if (sortDiff !== 0) return sortDiff;
    return a.label.localeCompare(b.label);
  });

  return normalized;
}

function normalizeMode(value) {
  value = normalizeString(value);
  if (FIELD_MODES.indexOf(value) === -1) return "optional";
  return value;
}

function enabledOptions(definition) {
  return (definition.options || []).filter(function (option) {
    return option.enabled !== false;
  });
}

function normalizeFormatCustomFieldRules(rules, definitions) {
  rules = rules || {};
  definitions = normalizeDefinitions(definitions);

  var normalized = {};
  for (var i = 0; i < definitions.length; i++) {
    var definition = definitions[i];
    var rule = rules[definition.key] || {};
    var mode = normalizeMode(rule.mode);

    if (definition.enabled === false) {
      mode = "hidden";
    } else if (mode === "required" && definition.type === "select" && enabledOptions(definition).length === 0) {
      mode = "optional";
    }

    normalized[definition.key] = { mode: mode };
  }

  return normalized;
}

function error400(message) {
  var err = new Error(message);
  err.code = 400;
  return err;
}

function findSelectOption(definition, value) {
  var submitted = normalizeString(value);
  var options = enabledOptions(definition);
  for (var i = 0; i < options.length; i++) {
    var option = options[i];
    if (option.id === submitted || option.label === submitted) {
      return option;
    }
  }
  return null;
}

function sanitizeSubmittedValues(values, definitions, rules) {
  values = values || {};
  definitions = normalizeDefinitions(definitions);
  rules = normalizeFormatCustomFieldRules(rules, definitions);

  var sanitized = {};
  for (var i = 0; i < definitions.length; i++) {
    var definition = definitions[i];
    var rule = rules[definition.key] || { mode: "optional" };
    if (rule.mode === "hidden") continue;

    var rawValue = Object.prototype.hasOwnProperty.call(values, definition.key) ? values[definition.key] : "";
    var value = normalizeString(rawValue);

    if (!value) {
      if (rule.mode === "required") {
        throw error400(definition.label + " is required.");
      }
      continue;
    }

    if (definition.type === "select") {
      var option = findSelectOption(definition, value);
      if (!option) {
        if (rule.mode === "required") {
          throw error400(definition.label + " is required.");
        }
        continue;
      }
      sanitized[definition.key] = {
        label: definition.label,
        type: definition.type,
        value: option.id,
        displayValue: option.label
      };
      continue;
    }

    sanitized[definition.key] = {
      label: definition.label,
      type: definition.type,
      value: value.slice(0, MAX_LENGTHS[definition.type])
    };
  }

  return sanitized;
}

module.exports = {
  normalizeDefinitions: normalizeDefinitions,
  normalizeFormatCustomFieldRules: normalizeFormatCustomFieldRules,
  sanitizeSubmittedValues: sanitizeSubmittedValues
};
