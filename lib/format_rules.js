const FORMAT_KEYS = ["book", "audiobook_cd", "dvd", "music_cd", "ebook", "eaudiobook"];
const FIELD_KEYS = ["title", "author", "identifier", "agegroup", "publication"];
const FIELD_MODES = { REQUIRED: "required", OPTIONAL: "optional", HIDDEN: "hidden" };
const MESSAGE_BEHAVIORS = { NONE: "none", EBOOK: "ebookMessage", EAUDIOBOOK: "eaudiobookMessage" };

const DEFAULT_RULES = {
  book: {
    messageBehavior: MESSAGE_BEHAVIORS.NONE,
    fields: {
      title: { mode: FIELD_MODES.REQUIRED, label: "Title" },
      author: { mode: FIELD_MODES.REQUIRED, label: "Author" },
      identifier: { mode: FIELD_MODES.OPTIONAL, label: "Identifier number" },
      agegroup: { mode: FIELD_MODES.REQUIRED, label: "Age Group" },
      publication: { mode: FIELD_MODES.REQUIRED, label: "Publication Timing" },
    },
  },
  audiobook_cd: {
    messageBehavior: MESSAGE_BEHAVIORS.NONE,
    fields: {
      title: { mode: FIELD_MODES.REQUIRED, label: "Title" },
      author: { mode: FIELD_MODES.REQUIRED, label: "Author" },
      identifier: { mode: FIELD_MODES.OPTIONAL, label: "Identifier number" },
      agegroup: { mode: FIELD_MODES.REQUIRED, label: "Age Group" },
      publication: { mode: FIELD_MODES.REQUIRED, label: "Publication Timing" },
    },
  },
  dvd: {
    messageBehavior: MESSAGE_BEHAVIORS.NONE,
    fields: {
      title: { mode: FIELD_MODES.REQUIRED, label: "Title" },
      author: { mode: FIELD_MODES.REQUIRED, label: "Director/Actors/Producer" },
      identifier: { mode: FIELD_MODES.HIDDEN, label: "UPC" },
      agegroup: { mode: FIELD_MODES.REQUIRED, label: "Age Group" },
      publication: { mode: FIELD_MODES.REQUIRED, label: "Publication Timing" },
    },
  },
  music_cd: {
    messageBehavior: MESSAGE_BEHAVIORS.NONE,
    fields: {
      title: { mode: FIELD_MODES.REQUIRED, label: "Title" },
      author: { mode: FIELD_MODES.REQUIRED, label: "Artist" },
      identifier: { mode: FIELD_MODES.HIDDEN, label: "UPC" },
      agegroup: { mode: FIELD_MODES.REQUIRED, label: "Age Group" },
      publication: { mode: FIELD_MODES.REQUIRED, label: "Publication Timing" },
    },
  },
  ebook: {
    messageBehavior: MESSAGE_BEHAVIORS.EBOOK,
    fields: {
      title: { mode: FIELD_MODES.REQUIRED, label: "Title" },
      author: { mode: FIELD_MODES.REQUIRED, label: "Author" },
      identifier: { mode: FIELD_MODES.OPTIONAL, label: "Identifier number" },
      agegroup: { mode: FIELD_MODES.REQUIRED, label: "Age Group" },
      publication: { mode: FIELD_MODES.REQUIRED, label: "Publication Timing" },
    },
  },
  eaudiobook: {
    messageBehavior: MESSAGE_BEHAVIORS.EAUDIOBOOK,
    fields: {
      title: { mode: FIELD_MODES.REQUIRED, label: "Title" },
      author: { mode: FIELD_MODES.REQUIRED, label: "Author" },
      identifier: { mode: FIELD_MODES.OPTIONAL, label: "Identifier number" },
      agegroup: { mode: FIELD_MODES.REQUIRED, label: "Age Group" },
      publication: { mode: FIELD_MODES.REQUIRED, label: "Publication Timing" },
    },
  },
};

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function defaultFormatRules() {
  return clone(DEFAULT_RULES);
}

function normalizeFormat(value) {
  value = String(value || "").trim();
  return FORMAT_KEYS.indexOf(value) >= 0 ? value : "book";
}

function normalizeMode(value, fallback) {
  value = String(value || "").trim();
  if (value === FIELD_MODES.REQUIRED || value === FIELD_MODES.OPTIONAL || value === FIELD_MODES.HIDDEN) {
    return value;
  }
  return fallback || FIELD_MODES.OPTIONAL;
}

function normalizeMessageBehavior(value, fallback) {
  value = String(value || "").trim();
  if (value === MESSAGE_BEHAVIORS.NONE || value === MESSAGE_BEHAVIORS.EBOOK || value === MESSAGE_BEHAVIORS.EAUDIOBOOK) {
    return value;
  }
  return fallback || MESSAGE_BEHAVIORS.NONE;
}

function normalizeFormatRules(rules) {
  rules = rules || {};
  var normalized = defaultFormatRules();

  // Get a unique list of all format keys (default + incoming)
  var allKeys = clone(FORMAT_KEYS);
  var incomingKeys = Object.keys(rules);
  for (var k = 0; k < incomingKeys.length; k++) {
    if (allKeys.indexOf(incomingKeys[k]) === -1) {
      allKeys.push(incomingKeys[k]);
    }
  }

  for (var i = 0; i < allKeys.length; i++) {
    var format = allKeys[i];
    var incomingFormat = rules[format] || {};
    
    // If it's a new format not in DEFAULT_RULES, initialize it
    if (!normalized[format]) {
      normalized[format] = {
        messageBehavior: MESSAGE_BEHAVIORS.NONE,
        fields: {}
      };
    }

    normalized[format].messageBehavior = normalizeMessageBehavior(incomingFormat.messageBehavior, normalized[format].messageBehavior);

    var incomingFields = incomingFormat.fields || {};
    for (var j = 0; j < FIELD_KEYS.length; j++) {
      var field = FIELD_KEYS[j];
      var incomingField = incomingFields[field] || {};
      
      // Default field rules for newly added formats
      var defaultField = (normalized[format].fields && normalized[format].fields[field]) || {
        mode: field === "title" ? FIELD_MODES.REQUIRED : FIELD_MODES.OPTIONAL,
        label: field.charAt(0).toUpperCase() + field.slice(1)
      };

      var mode = normalizeMode(incomingField.mode, defaultField.mode);
      if (field === "title") {
        mode = FIELD_MODES.REQUIRED;
      }
      normalized[format].fields[field] = {
        mode: mode,
        label: String(incomingField.label || defaultField.label || field).trim() || defaultField.label || field,
      };
    }
  }

  return normalized;
}

function requiredValue(data, field) {
  if (field === "identifier") {
    return String(data.identifier || data.isbn || "").trim();
  }
  return String(data[field] || "").trim();
}

function sanitizePatronSuggestion(data, uiText) {
  data = Object.assign({}, data || {});
  var format = normalizeFormat(data.format);
  var rules = normalizeFormatRules(uiText && uiText.formatRules);
  var rule = rules[format] || rules.book;

  if (rule.messageBehavior !== MESSAGE_BEHAVIORS.NONE) {
    var messageErr = new Error("This format is informational only and cannot be submitted from the patron form.");
    messageErr.code = 400;
    throw messageErr;
  }

  data.format = format;
  for (var i = 0; i < FIELD_KEYS.length; i++) {
    var field = FIELD_KEYS[i];
    var fieldRule = rule.fields[field];
    var mode = field === "title" ? FIELD_MODES.REQUIRED : fieldRule.mode;
    var label = fieldRule.label || field;

    if (mode === FIELD_MODES.HIDDEN) {
      if (field === "identifier") {
        data.identifier = "";
        data.isbn = "";
      } else {
        data[field] = "";
      }
      continue;
    }

    if (field === "identifier") {
      data.identifier = requiredValue(data, field);
      data.isbn = data.identifier;
    }

    if (mode === FIELD_MODES.REQUIRED && !requiredValue(data, field)) {
      var err = new Error(label + " is required.");
      err.code = 400;
      throw err;
    }
  }

  return data;
}

module.exports = {
  FIELD_KEYS: FIELD_KEYS,
  FORMAT_KEYS: FORMAT_KEYS,
  MESSAGE_BEHAVIORS: MESSAGE_BEHAVIORS,
  defaultFormatRules: defaultFormatRules,
  normalizeFormatRules: normalizeFormatRules,
  normalizeMessageBehavior: normalizeMessageBehavior,
  sanitizePatronSuggestion: sanitizePatronSuggestion,
};
