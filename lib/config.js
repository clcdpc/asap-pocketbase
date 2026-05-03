let _formatRules;
let _identity;
function getFormatRules() {
  if (!_formatRules) _formatRules = require(`${__hooks}/../lib/format_rules.js`);
  return _formatRules;
}
function getIdentity() {
  if (!_identity) _identity = require(`${__hooks}/../lib/identity.js`);
  return _identity;
}

function parseJsonObject(value, fallback) {
  fallback = fallback || {};
  if (value && typeof value === "object" && !Array.isArray(value)) return value;
  if (typeof value !== "string") return fallback;
  var text = value.trim();
  if (!text) return fallback;
  try {
    var parsed = JSON.parse(text);
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) return parsed;
  } catch (err) {}
  return fallback;
}

function parseJsonArray(value, fallback) {
  fallback = Array.isArray(fallback) ? fallback : [];
  if (Array.isArray(value)) return value;
  if (typeof value !== "string") return fallback;
  var text = value.trim();
  if (!text) return fallback;
  try {
    var parsed = JSON.parse(text);
    if (Array.isArray(parsed)) return parsed;
  } catch (err) {}
  return fallback;
}

function cloneJson(value) {
  return JSON.parse(JSON.stringify(value));
}

const DEFAULT_DUPLICATE_STATUS_LABELS = {
  suggestion: "Received",
  outstanding_purchase: "Under review",
  pending_hold: "Being prepared",
  hold_placed: "Hold placed",
  closed: "Completed",
  rejected: "Not selected for purchase",
  hold_completed: "Completed",
  hold_not_picked_up: "Closed",
  manual: "Closed",
  silent: "Closed",
  "Silently Closed": "Closed"
};

const DEFAULT_EMAILS = {
  suggestion_submitted: {
    subject: "Suggestion received: {{title}}",
    body: "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format. Our collection development team has received your request and will review it.\n\nIf we add this item, we will place a hold for you automatically and send another update.\n\nThank you for helping us shape the library collection."
  },
  already_owned: {
    subject: "{{title}} is already available",
    body: "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nThe library already owns this title or has it on order. We have placed a hold on card {{barcode}} so you will be notified when it is ready.\n\nThank you for using the library's suggestion service."
  },
  rejected: {
    subject: "Update on your suggestion: {{title}}",
    body: "Hello {{name}},\n\nThank you for suggesting {{title}} by {{author}} in {{format}} format.\n\nAfter review, we are not able to add this item to the collection at this time. We appreciate you taking the time to share your suggestion with us.\n\nThank you for helping us build a collection that reflects our community."
  },
  hold_placed: {
    subject: "Hold placed for {{title}}",
    body: "Hello {{name}},\n\nGood news. The library plans to add {{title}} by {{author}} in {{format}} format.\n\nWe have placed a hold on card {{barcode}}. You will receive the usual pickup notice when the item is ready.\n\nThank you for your suggestion."
  }
};

const EMAIL_TEMPLATE_KEYS = ["suggestion_submitted", "already_owned", "rejected", "hold_placed"];
const EMAIL_TEMPLATE_FIELDS = ["subject", "body"];

function defaultDuplicateStatusLabels() {
  return cloneJson(DEFAULT_DUPLICATE_STATUS_LABELS);
}

function defaultEmailTemplates() {
  return cloneJson(DEFAULT_EMAILS);
}

function safeRecord(app, collection, field, value) {
  try {
    return app.findFirstRecordByData(collection, field, value);
  } catch (err) {
    return null;
  }
}

function safeCollection(app, name) {
  try {
    return app.findCollectionByNameOrId(name);
  } catch (err) {
    return null;
  }
}

function systemRecord(app, collectionName, id, defaults) {
  var collection = safeCollection(app, collectionName);
  if (!collection) return null;
  try {
    return app.findRecordById(collectionName, id);
  } catch (err) {
    var record = new Record(collection);
    record.set("id", id);
    Object.keys(defaults || {}).forEach(function (key) {
      record.set(key, defaults[key]);
    });
    app.save(record);
    return record;
  }
}

function findOrganization(app, orgId) {
  orgId = String(orgId || "").trim();
  if (!orgId) return null;
  return safeRecord(app, "polaris_organizations", "organizationId", orgId);
}

function orgIdForSettings(app, orgId) {
  var org = findOrganization(app, orgId);
  return org ? org.id : "";
}

function lines(value, fallback) {
  var raw = String(value || "").split(/\r?\n/).map(function (line) { return line.trim(); }).filter(Boolean);
  return raw.length ? raw : fallback.slice();
}

function optionIdFromLabel(label, fallback) {
  var code = String(label || "").trim().toLowerCase().replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "");
  return code || fallback || "option";
}

function normalizeOptionList(value, fallbackLabels) {
  var fallback = (fallbackLabels || []).map(function (label, index) {
    return { id: optionIdFromLabel(label, "option_" + (index + 1)), label: label, enabled: true, sortOrder: (index + 1) * 10 };
  });
  var raw;
  if (Array.isArray(value)) {
    raw = value;
  } else if (typeof value === "string" && value.trim().charAt(0) === "[") {
    raw = parseJsonArray(value, []);
  } else {
    raw = lines(value, []).map(function (label) { return { label: label }; });
  }
  if (!raw.length) return fallback;
  var seenLabels = {};
  var seenIds = {};
  var out = [];
  raw.forEach(function (item, index) {
    var obj = item && typeof item === "object" ? item : { label: item };
    var label = String(obj.label || obj.name || obj.value || "").trim();
    if (!label) return;
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
  out.sort(function (a, b) { return (a.sortOrder || 0) - (b.sortOrder || 0); });
  return out.length ? out : fallback;
}

function enabledOptionLabels(options) {
  return normalizeOptionList(options, []).filter(function (opt) { return opt.enabled !== false; }).map(function (opt) { return opt.label; });
}

function getSystemSettings(app) {
  return systemRecord(app || $app, "system_settings", "settings0000001", {
    settingsKey: "system",
    allowedStaffUsers: "",
    staffUrl: defaultStaffUrl(),
    leapBibUrlPattern: "",
    organizationsSyncStatus: "not_loaded",
    organizationsSyncMessage: "Polaris organizations have not been loaded yet."
  });
}

function envValue(name) {
  try {
    return String($os.getenv(name) || "").trim();
  } catch (err) {
    return "";
  }
}

function stripUrlHash(value) {
  var text = String(value || "").trim();
  var hashIndex = text.indexOf("#");
  return hashIndex >= 0 ? text.slice(0, hashIndex) : text;
}

function normalizeStaffUrl(value) {
  var text = stripUrlHash(value);
  if (!text) throw new Error("Staff URL is required.");
  if (!/^https?:\/\//i.test(text)) {
    throw new Error("Staff URL must start with http:// or https://.");
  }
  if (/\s/.test(text)) {
    throw new Error("Enter a valid Staff URL beginning with http:// or https://.");
  }
  var match = text.match(/^(https?):\/\/([^/?#]+)(.*)$/i);
  if (!match || !match[2]) {
    throw new Error("Enter a valid Staff URL beginning with http:// or https://.");
  }
  var normalized = match[1].toLowerCase() + "://" + match[2] + (match[3] || "");
  var queryIndex = normalized.indexOf("?");
  var beforeQuery = queryIndex >= 0 ? normalized.slice(0, queryIndex) : normalized;
  var afterQuery = queryIndex >= 0 ? normalized.slice(queryIndex) : "";
  if (/\/staff$/i.test(beforeQuery)) {
    normalized = beforeQuery + "/" + afterQuery;
  }
  return normalized;
}

function normalizeLeapBibUrlPattern(value) {
  var text = String(value || "").trim();
  if (!text) return "";
  if (!/^https?:\/\//i.test(text)) {
    throw new Error("Leap Bib URL pattern must begin with http:// or https://.");
  }
  if (text.indexOf("{{bibid}}") < 0) {
    throw new Error("Leap Bib URL pattern must include {{bibid}}.");
  }
  return text;
}

function staffUrlFromEnv(value) {
  var normalized = normalizeStaffUrl(value);
  var queryIndex = normalized.indexOf("?");
  var beforeQuery = queryIndex >= 0 ? normalized.slice(0, queryIndex) : normalized;
  var afterQuery = queryIndex >= 0 ? normalized.slice(queryIndex) : "";
  beforeQuery = beforeQuery.replace(/\/+$/, "");
  if (!/\/staff$/i.test(beforeQuery)) {
    beforeQuery += "/staff";
  }
  return beforeQuery + "/" + afterQuery;
}

function defaultStaffUrl() {
  var envStaffUrl = envValue("ASAP_STAFF_URL");
  if (envStaffUrl) {
    try {
      return staffUrlFromEnv(envStaffUrl);
    } catch (err) {
      return envStaffUrl;
    }
  }
  var envPublicUrl = envValue("ASAP_PUBLIC_URL");
  if (envPublicUrl) {
    try {
      return staffUrlFromEnv(envPublicUrl);
    } catch (err2) {
      return envPublicUrl;
    }
  }
  return "";
}

function staffUrl(app) {
  var sys = null;
  try {
    sys = getSystemSettings(app || $app);
  } catch (err) {}
  var configured = "";
  try {
    configured = sys ? String(sys.get("staffUrl") || "").trim() : "";
  } catch (err) {}
  if (configured) return normalizeStaffUrl(configured);

  var envStaffUrl = envValue("ASAP_STAFF_URL");
  if (envStaffUrl) return staffUrlFromEnv(envStaffUrl);

  var envPublicUrl = envValue("ASAP_PUBLIC_URL");
  if (envPublicUrl) return staffUrlFromEnv(envPublicUrl);

  return "http://localhost:8090/staff/";
}

function saveSystemSettings(app, data) {
  var record = getSystemSettings(app);
  if (data && Object.prototype.hasOwnProperty.call(data, "staffUrl")) {
    record.set("staffUrl", normalizeStaffUrl(data.staffUrl));
  }
  if (data && Object.prototype.hasOwnProperty.call(data, "leapBibUrlPattern")) {
    record.set("leapBibUrlPattern", normalizeLeapBibUrlPattern(data.leapBibUrlPattern));
  }
  app.save(record);
  return record;
}

function getPolarisSettings(app) {
  return systemRecord(app || $app, "polaris_settings", "polaris00000010", {
    settingsKey: "system",
    accessId: "SuggestAPI",
    overridePassword: "admin",
    langId: "1033",
    appId: "100",
    orgId: "1",
    pickupOrgId: "0",
    requestingOrgId: "3",
    workstationId: "1",
    userId: "1",
    autoPromote: true
  });
}

function getSmtpSettings(app) {
  return systemRecord(app || $app, "smtp_settings", "smtp00000000100", {
    settingsKey: "system",
    port: 587,
    tls: true,
    fromName: "Library Collection Development"
  });
}

function workflowRecord(app, orgId) {
  app = app || $app;
  var orgRecordId = orgIdForSettings(app, orgId);
  if (orgRecordId) {
    try {
      return app.findFirstRecordByFilter("workflow_settings", "scope = 'library' && libraryOrganization = {:org}", { org: orgRecordId });
    } catch (err) {}
  }
  return systemRecord(app, "workflow_settings", "workflow0000010", {
    scope: "system",
    suggestionLimit: 5,
    suggestionLimitMessage: "Weekly suggestion limit reached. You can try again after {{next_available_date}}.",
    outstandingTimeoutDays: 30,
    holdPickupTimeoutDays: 14,
    pendingHoldTimeoutDays: 14,
    commonAuthorsMessage: "We automatically purchase all upcoming titles by this author. Please check the catalog to place a hold on 'On Order' items."
  });
}

function uiRecord(app, orgId) {
  app = app || $app;
  var orgRecordId = orgIdForSettings(app, orgId);
  if (orgRecordId) {
    try {
      return app.findFirstRecordByFilter("ui_settings", "scope = 'library' && libraryOrganization = {:org}", { org: orgRecordId });
    } catch (err) {}
  }
  return systemRecord(app, "ui_settings", "uisettings00010", {
    scope: "system",
    logoAlt: "Library Logo",
    pageTitle: "Material Suggestion",
    barcodeLabel: "Library Card",
    pinLabel: "Pin",
    successTitle: "Suggestion Submitted"
  });
}

function polarisFromRecord(record) {
  record = record || {};
  function str(name, fallback) {
    return String(record.get ? record.get(name) || "" : fallback || "");
  }
  return {
    host: str("host"),
    accessId: str("accessId", "SuggestAPI") || "SuggestAPI",
    apiKey: str("apiKey"),
    staffDomain: str("staffDomain"),
    adminUser: str("adminUser"),
    adminPassword: str("adminPassword"),
    overridePassword: str("overridePassword"),
    langId: str("langId") || "1033",
    appId: str("appId") || "100",
    orgId: str("orgId") || "1",
    pickupOrgId: str("pickupOrgId") || "0",
    requestingOrgId: str("requestingOrgId") || "3",
    workstationId: str("workstationId") || "1",
    userId: str("userId") || "1",
    autoPromote: record.getBool ? record.getBool("autoPromote") !== false : true
  };
}

function smtpFromRecord(record) {
  return {
    host: String(record && record.get ? record.get("host") || "" : "").trim(),
    port: parseInt(record && record.get ? record.get("port") : 587, 10) || 587,
    username: String(record && record.get ? record.get("username") || "" : "").trim(),
    password: record && record.get ? record.get("password") || "" : "",
    tls: record && record.getBool ? record.getBool("tls") !== false : true,
  };
}

function smtpPublicFromRecord(record) {
  return {
    enabled: !!String(record && record.get ? record.get("host") || "" : "").trim(),
    host: String(record && record.get ? record.get("host") || "" : "").trim(),
    port: parseInt(record && record.get ? record.get("port") : 587, 10) || 587,
    tls: record && record.getBool ? record.getBool("tls") !== false : true,
    fromAddress: String(record && record.get ? record.get("fromAddress") || "" : "").trim(),
    fromName: String(record && record.get ? record.get("fromName") || "" : "").trim(),
    usernameSet: !!String(record && record.get ? record.get("username") || "" : "").trim(),
    passwordSet: !!String(record && record.get ? record.get("password") || "" : "").trim()
  };
}

function mergeDuplicateStatusLabels(labels) {
  return Object.assign(defaultDuplicateStatusLabels(), parseJsonObject(labels, labels || {}));
}

function duplicateStatusLabelsFromUiRecord(record) {
  record = record || {};
  function get(name) {
    try {
      return record.get ? record.get(name) : "";
    } catch (err) {
      return "";
    }
  }
  return {
    suggestion: get("duplicateLabelSuggestion"),
    outstanding_purchase: get("duplicateLabelOutstandingPurchase"),
    pending_hold: get("duplicateLabelPendingHold"),
    hold_placed: get("duplicateLabelHoldPlaced"),
    closed: get("duplicateLabelClosed"),
    rejected: get("duplicateLabelRejected"),
    hold_completed: get("duplicateLabelHoldCompleted"),
    hold_not_picked_up: get("duplicateLabelHoldNotPickedUp"),
    manual: get("duplicateLabelManual"),
    silent: get("duplicateLabelSilent"),
    "Silently Closed": get("duplicateLabelSilent")
  };
}

function hasAnyDuplicateStatusLabel(labels) {
  labels = labels || {};
  var keys = Object.keys(labels);
  for (var i = 0; i < keys.length; i++) {
    if (String(labels[keys[i]] || "").trim()) return true;
  }
  return false;
}

function patronSettingsOverrideRecord(app, orgId) {
  app = app || $app;
  var requestedOrgId = String(orgId || "").trim();
  if (!requestedOrgId || !safeCollection(app, "patron_settings_overrides")) return null;
  try {
    return app.findFirstRecordByFilter("patron_settings_overrides", "orgId = {:orgId}", { orgId: requestedOrgId });
  } catch (err) {
    return null;
  }
}

function legacyPatronLibrarySettingsRecord(app, orgId) {
  app = app || $app;
  var orgRecordId = orgIdForSettings(app, orgId);
  if (!orgRecordId || !safeCollection(app, "patron_library_settings")) return null;
  try {
    return app.findFirstRecordByFilter("patron_library_settings", "libraryOrganization = {:org}", { org: orgRecordId });
  } catch (err) {
    return null;
  }
}

function duplicateStatusLabelResolution(app, orgId, systemUiRecord) {
  app = app || $app;
  var defaults = defaultDuplicateStatusLabels();
  var systemRecord = systemUiRecord || uiRecord(app, "");

  var rawGlobalLabels = duplicateStatusLabelsFromUiRecord(systemRecord);
  var globalLabels = Object.assign({}, defaults, rawGlobalLabels);
  var requestedOrgId = String(orgId || "").trim();

  if (!requestedOrgId) {
    return {
      labels: globalLabels,
      source: hasAnyDuplicateStatusLabel(rawGlobalLabels) ? "global" : "default",
      inherited: false
    };
  }

  var libraryLabels = null;
  var overrideRecord = patronSettingsOverrideRecord(app, requestedOrgId);

  if (overrideRecord) {
    var parsedOverride = parseJsonObject(overrideRecord.get("duplicateStatusLabels"), {});
    if (hasAnyDuplicateStatusLabel(parsedOverride)) {
      libraryLabels = parsedOverride;
    }
  } else {
    var libraryRecord = legacyPatronLibrarySettingsRecord(app, requestedOrgId);
    if (libraryRecord) {
      libraryLabels = parseJsonObject(libraryRecord.get("duplicateRequestStatusLabels"), {});
    }
  }

  if (libraryLabels) {
    return {
      labels: Object.assign({}, globalLabels, libraryLabels),
      source: "library",
      inherited: false
    };
  }

  return {
    labels: globalLabels,
    source: "global",
    inherited: true
  };
}

function scopedRows(app, collectionName, orgId) {
  app = app || $app;
  var rows = [];
  function read(scope, orgRecordId) {
    try {
      var filter = scope === "system" ? "scope = 'system'" : "scope = 'library' && libraryOrganization = {:org}";
      var params = scope === "system" ? {} : { org: orgRecordId };
      rows = rows.concat(app.findRecordsByFilter(collectionName, filter, "sortOrder", 200, 0, params));
    } catch (err) {
      if (scope === "system") {
        try {
          rows = rows.concat(app.findRecordsByFilter(collectionName, "id != ''", "sortOrder", 200, 0));
        } catch (err2) {}
      }
    }
  }
  read("system", "");
  var orgRecordId = orgIdForSettings(app, orgId);
  if (orgRecordId) read("library", orgRecordId);
  return rows;
}

function uiTextFromRecord(app, record, orgId) {
  const formatRules = getFormatRules();
  var overrideRecord = orgId ? patronSettingsOverrideRecord(app, orgId) : null;
  var logoUrl = "/jpl.png";
  try {
    var logoFile = record.get("logo");
    if (logoFile) logoUrl = "/api/files/ui_settings/" + record.id + "/" + logoFile;
  } catch (err) {}

  var globalPublicationOptions = normalizeOptionList(record.get("publicationOptions"), ["Already published", "Coming soon", "Published a while back"]);
  var globalAgeGroups = normalizeOptionList(record.get("ageGroups"), ["Adult", "Young Adult / Teen", "Children"]);
  var publicationOptions = overrideRecord && overrideRecord.get("publicationOptions")
    ? normalizeOptionList(overrideRecord.get("publicationOptions"), enabledOptionLabels(globalPublicationOptions))
    : globalPublicationOptions;
  var ageGroups = overrideRecord && overrideRecord.get("ageGroups")
    ? normalizeOptionList(overrideRecord.get("ageGroups"), enabledOptionLabels(globalAgeGroups))
    : normalizeOptionList(audienceGroups(app, orgId, enabledOptionLabels(globalAgeGroups)), enabledOptionLabels(globalAgeGroups));
  var formats = materialFormats(app, orgId);
  var overrideFormatRules = null;
  if (overrideRecord && overrideRecord.get("patronFormatRules")) {
    overrideFormatRules = parseJsonObject(overrideRecord.get("patronFormatRules"), {});
  }

  var duplicateResolution = duplicateStatusLabelResolution(app, orgId, orgId ? null : record);

  return {
    logoUrl: logoUrl,
    logoAlt: record.get("logoAlt") || "Library Logo",
    pageTitle: record.get("pageTitle") || "Material Suggestion",
    barcodeLabel: record.get("barcodeLabel") || "Library Card",
    pinLabel: record.get("pinLabel") || "Pin",
    loginPrompt: record.get("loginPrompt") || "Please enter your information below to start the suggestion process.",
    suggestionFormNote: record.get("suggestionFormNote") || "If the library decides to purchase your suggestion, we will automatically place a hold on it and send a confirmation email. Make sure to check your spam folder if you don't see the email.",
    loginNote: record.get("loginNote") || "Use of this service requires a valid library card. Contact your library if you need assistance with your card or PIN.",
    successTitle: record.get("successTitle") || "Suggestion Submitted",
    successMessage: record.get("successMessage") || "You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>",
    alreadySubmittedMessage: record.get("alreadySubmittedMessage") || "This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library's suggestion service.</div>",
    duplicateStatusLabels: duplicateResolution.labels,
    duplicateStatusLabelsSource: duplicateResolution.source,
    duplicateStatusLabelsInherited: duplicateResolution.inherited,
    noEmailMessage: record.get("noEmailMessage") || "No email is specified on your library account, which means we won't be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.",
    systemNotEnabledMessage: record.get("systemNotEnabledMessage") || "Your library does not currently participate in this suggestion service.",
    ebookMessage: overrideRecord && overrideRecord.get("ebookMessage") ? overrideRecord.get("ebookMessage") : (record.get("ebookMessage") || "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>"),
    eaudiobookMessage: overrideRecord && overrideRecord.get("eaudiobookMessage") ? overrideRecord.get("eaudiobookMessage") : (record.get("eaudiobookMessage") || "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>"),
    publicationOptions: publicationOptions,
    ageGroups: ageGroups,
    formatLabels: formats.labels,
    availableFormats: formats.available,
    formatRules: formatRules.normalizeFormatRules(overrideFormatRules || formats.rules),
    patronSettingsSource: overrideRecord ? "library" : "global",
    patronSettingsInherited: !!orgId && !overrideRecord
  };
}

function materialFormats(app, orgId) {
  app = app || $app;
  var labels = {};
  var rules = {};
  var available = [];
  var seenAvailable = {};
  try {
    var rows = scopedRows(app, "material_formats", orgId);
    for (var i = 0; i < rows.length; i++) {
      var r = rows[i];
      var code = r.get("code");
      if (!code) continue;
      labels[code] = r.get("label") || code;
      if (r.getBool("enabled")) {
        if (!seenAvailable[code]) available.push(code);
        seenAvailable[code] = true;
      } else if (seenAvailable[code]) {
        available = available.filter(function (value) { return value !== code; });
        seenAvailable[code] = false;
      }
      rules[code] = {
        messageBehavior: r.get("messageBehavior") || "none",
        fields: {
          title: { mode: r.get("titleMode") || "required", label: r.get("titleLabel") || "Title" },
          author: { mode: r.get("authorMode") || "required", label: r.get("authorLabel") || "Author" },
          identifier: { mode: r.get("identifierMode") || "optional", label: r.get("identifierLabel") || "Identifier number" },
          agegroup: { mode: r.get("audienceMode") || "required", label: r.get("audienceLabel") || "Age Group" },
          publication: { mode: r.get("publicationMode") || "required", label: r.get("publicationLabel") || "Publication Timing" }
        }
      };
    }
  } catch (err) {}
  return { labels: labels, rules: rules, available: available };
}

function audienceGroups(app, orgId, fallback) {
  app = app || $app;
  var labelsByCode = {};
  var order = [];
  try {
    var rows = scopedRows(app, "audience_groups", orgId);
    for (var i = 0; i < rows.length; i++) {
      var code = String(rows[i].get("code") || "").trim();
      if (!code) continue;
      if (order.indexOf(code) < 0) order.push(code);
      labelsByCode[code] = rows[i].get("label") || code;
    }
  } catch (err) {}
  var labels = order.map(function (code) { return labelsByCode[code]; }).filter(Boolean);
  return labels.length ? labels : (fallback || ["Adult", "Young Adult / Teen", "Children"]).slice();
}

function workflowFromRecord(record) {
  return {
    suggestionLimit: record.getInt("suggestionLimit") || 5,
    suggestionLimitMessage: record.get("suggestionLimitMessage") || "Weekly suggestion limit reached. You can try again after {{next_available_date}}.",
    outstandingTimeoutEnabled: record.getBool("outstandingTimeoutEnabled"),
    outstandingTimeoutDays: record.getInt("outstandingTimeoutDays") || 30,
    outstandingTimeoutSendEmail: record.getBool("outstandingTimeoutSendEmail"),
    outstandingTimeoutRejectionTemplateId: String(record.get("outstandingTimeoutRejectionTemplate") || ""),
    holdPickupTimeoutEnabled: record.getBool("holdPickupTimeoutEnabled"),
    holdPickupTimeoutDays: record.getInt("holdPickupTimeoutDays") || 14,
    pendingHoldTimeoutEnabled: record.getBool("pendingHoldTimeoutEnabled"),
    pendingHoldTimeoutDays: record.getInt("pendingHoldTimeoutDays") || 14,
    commonAuthorsEnabled: record.getBool("commonAuthorsEnabled"),
    commonAuthorsList: record.get("commonAuthorsList") || "",
    commonAuthorsMessage: record.get("commonAuthorsMessage") || "We automatically purchase all upcoming titles by this author. Please check the catalog to place a hold on 'On Order' items."
  };
}

function emailsFor(app, orgId) {
  app = app || $app;
  var merged = defaultEmailTemplates();
  var sender = getSmtpSettings(app);
  merged.fromAddress = sender ? sender.get("fromAddress") || "" : "";
  merged.fromName = sender ? sender.get("fromName") || "" : "";
  function applyRows(scope, orgRecordId) {
    try {
      var filter = scope === "system" ? "scope = 'system'" : "scope = 'library' && libraryOrganization = {:org}";
      var params = scope === "system" ? {} : { org: orgRecordId };
      var rows = app.findRecordsByFilter("email_templates", filter, "", 200, 0, params);
      rows.forEach(function (row) {
        var key = row.get("templateKey");
        if (!merged[key]) merged[key] = {};
        EMAIL_TEMPLATE_FIELDS.forEach(function (fieldName) {
          if (String(row.get(fieldName) || "").trim()) merged[key][fieldName] = row.get(fieldName);
        });
        if (row.get("fromAddress")) merged.fromAddress = row.get("fromAddress");
        if (row.get("fromName")) merged.fromName = row.get("fromName");
      });
    } catch (err) {}
  }
  applyRows("system", "");
  var orgRecordId = orgIdForSettings(app, orgId);
  if (orgRecordId) applyRows("library", orgRecordId);
  merged.rejection_templates = rejectionTemplates(app, orgId);
  return merged;
}

function rejectionTemplates(app, orgId) {
  app = app || $app;
  var rows = [];
  function read(scope, orgRecordId) {
    try {
      var filter = scope === "system" ? "scope = 'system' && enabled = true" : "scope = 'library' && libraryOrganization = {:org} && enabled = true";
      var params = scope === "system" ? {} : { org: orgRecordId };
      rows = rows.concat(app.findRecordsByFilter("rejection_templates", filter, "sortOrder", 200, 0, params));
    } catch (err) {}
  }
  read("system", "");
  var orgRecordId = orgIdForSettings(app, orgId);
  if (orgRecordId) read("library", orgRecordId);
  return rows.map(function (row) {
    return { id: row.id, name: row.get("name"), subject: row.get("subject"), body: row.get("body") };
  });
}

function getSettings() {
  var app = $app;
  var sys = getSystemSettings(app);
  var wf = workflowFromRecord(workflowRecord(app, ""));
  return Object.assign({
    polaris: polaris(),
    smtp: smtpPublicFromRecord(getSmtpSettings(app)),
    emails: emailsFor(app, ""),
    allowedStaffUsers: sys ? sys.get("allowedStaffUsers") || "" : "",
    staffUrl: staffUrl(app),
    leapBibUrlPattern: sys ? sys.get("leapBibUrlPattern") || "" : "",
    enabledLibraryOrgIds: enabledLibraryOrgIds(app),
    ui_text: uiText(app, "")
  }, wf);
}

function enabledLibraryOrgIds(app) {
  app = app || $app;
  var sys = getSystemSettings(app);
  if (!sys) return "";
  var ids = [];
  var rels = sys.get("enabledLibraries") || [];
  if (!Array.isArray(rels)) rels = rels ? [rels] : [];
  if (rels.length === 0) return "";

  var chunkLimit = 100;
  for (var i = 0; i < rels.length; i += chunkLimit) {
    var chunk = rels.slice(i, i + chunkLimit);
    var filterParts = [];
    var params = {};
    for (var j = 0; j < chunk.length; j++) {
      var key = "p" + j;
      filterParts.push("id = {:" + key + "}");
      params[key] = chunk[j];
    }
    var filter = filterParts.join(" || ");
    try {
      var records = app.findRecordsByFilter("polaris_organizations", filter, "", chunk.length, 0, params);
      for (var k = 0; k < records.length; k++) {
        var org = records[k];
        if (org.get("organizationId")) ids.push(String(org.get("organizationId")));
      }
    } catch (err) {}
  }

  return ids.join(",");
}

function librarySettings(app, libraryOrgId) {
  app = app || $app;
  return {
    emails: emailsFor(app, libraryOrgId),
    ui_text: uiText(app, libraryOrgId),
    workflow: workflowFromRecord(workflowRecord(app, libraryOrgId)),
    leapBibUrlPattern: getSystemSettings(app).get("leapBibUrlPattern") || ""
  };
}

function polaris() {
  return polarisFromRecord(getPolarisSettings($app));
}

function mail() {
  return smtpFromRecord(getSmtpSettings($app));
}

function emails() {
  return emailsFor($app, "");
}

function emailStatus(app, orgId) {
  var smtp = smtpFromRecord(getSmtpSettings(app || $app));
  var e = emailsFor(app || $app, orgId);
  var enabled = !!String(smtp.host || "").trim() && !!String(e.fromAddress || "").trim();
  return {
    enabled: enabled,
    hasSmtp: !!String(smtp.host || "").trim(),
    hasSender: !!String(e.fromAddress || "").trim(),
    message: enabled ? "Email notifications are configured." : "Email notifications are not configured. Suggestions and staff workflows still work, but patron emails will not be sent."
  };
}

function suggestionLimit(app, orgId) {
  return workflowFromRecord(workflowRecord(app || $app, orgId));
}

function outstandingTimeout(app, orgId) {
  var wf = suggestionLimit(app, orgId);
  return { enabled: wf.outstandingTimeoutEnabled, days: wf.outstandingTimeoutDays };
}

function outstandingTimeoutEmail(app, orgId) {
  var wf = workflowFromRecord(workflowRecord(app || $app, orgId));
  return { enabled: wf.outstandingTimeoutSendEmail, templateId: wf.outstandingTimeoutRejectionTemplateId };
}

function holdPickupTimeout(app, orgId) {
  var wf = suggestionLimit(app, orgId);
  return { enabled: wf.holdPickupTimeoutEnabled, days: wf.holdPickupTimeoutDays };
}

function pendingHoldTimeout(app, orgId) {
  var wf = suggestionLimit(app, orgId);
  return { enabled: wf.pendingHoldTimeoutEnabled, days: wf.pendingHoldTimeoutDays };
}

function uiText(app, orgId) {
  app = app || $app;
  return uiTextFromRecord(app, uiRecord(app, orgId), orgId);
}

function duplicateStatusLabels(app, orgId) {
  return mergeDuplicateStatusLabels(duplicateStatusLabelResolution(app || $app, orgId).labels);
}

function allowedStaffUsers() {
  const identity = getIdentity();
  var sys = getSystemSettings($app);
  var value = String(sys ? sys.get("allowedStaffUsers") || "" : "").trim();
  return value ? identity.parseAllowedStaffUsers(value, polaris().staffDomain) : [];
}

function applyMailSettings(app) {
  var cfg = smtpFromRecord(getSmtpSettings(app || $app));
  var e = emailsFor(app || $app, "");
  var settings = app.settings();
  if (e.fromAddress) {
    settings.meta.senderAddress = e.fromAddress;
    settings.meta.senderName = e.fromName || "Library Collection Development";
  }
  if (cfg.host) {
    settings.smtp.enabled = true;
    settings.smtp.host = cfg.host;
    settings.smtp.port = cfg.port;
    settings.smtp.username = cfg.username;
    settings.smtp.password = cfg.password;
    settings.smtp.tls = cfg.tls;
    settings.smtp.authMethod = cfg.username ? "LOGIN" : "";
  }
  app.save(settings);
}

function savePolarisSettings(app, data) {
  var record = getPolarisSettings(app);
  var allowed = {
    host: true, accessId: true, apiKey: true, staffDomain: true, adminUser: true,
    adminPassword: true, overridePassword: true, langId: true, appId: true, orgId: true,
    pickupOrgId: true, requestingOrgId: true, workstationId: true, userId: true, autoPromote: true
  };
  Object.keys(data || {}).forEach(function (key) {
    if (allowed[key]) record.set(key, data[key]);
  });
  if (!record.get("firstSuccessfulSaveAt") && data.host && data.accessId && data.apiKey) {
    record.set("firstSuccessfulSaveAt", new Date().toISOString());
  }
  app.save(record);
  return record;
}

module.exports = {
  allowedStaffUsers: allowedStaffUsers,
  applyMailSettings: applyMailSettings,
  defaultDuplicateStatusLabels: defaultDuplicateStatusLabels,
  defaultEmailTemplates: defaultEmailTemplates,
  duplicateStatusLabels: duplicateStatusLabels,
  emailStatus: emailStatus,
  emails: emails,
  enabledLibraryOrgIds: enabledLibraryOrgIds,
  findOrganization: findOrganization,
  getPolarisSettings: getPolarisSettings,
  getSettings: getSettings,
  getSmtpSettings: getSmtpSettings,
  getSystemSettings: getSystemSettings,
  librarySettings: librarySettings,
  holdPickupTimeout: holdPickupTimeout,
  pendingHoldTimeout: pendingHoldTimeout,
  mail: mail,
  mergeDuplicateStatusLabels: mergeDuplicateStatusLabels,
  normalizeLeapBibUrlPattern: normalizeLeapBibUrlPattern,
  normalizeStaffUrl: normalizeStaffUrl,
  outstandingTimeout: outstandingTimeout,
  outstandingTimeoutEmail: outstandingTimeoutEmail,
  parseJsonObject: parseJsonObject,
  polaris: polaris,
  rejectionTemplates: rejectionTemplates,
  scopedRows: scopedRows,
  saveSystemSettings: saveSystemSettings,
  savePolarisSettings: savePolarisSettings,
  staffUrl: staffUrl,
  suggestionLimit: suggestionLimit,
  uiText: uiText,
};
