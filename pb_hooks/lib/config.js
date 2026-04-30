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

function getSystemSettings(app) {
  return systemRecord(app || $app, "system_settings", "settings0000001", {
    settingsKey: "system",
    allowedStaffUsers: "",
    organizationsSyncStatus: "not_loaded",
    organizationsSyncMessage: "Polaris organizations have not been loaded yet."
  });
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

function mergeDuplicateStatusLabels(labels) {
  return Object.assign(defaultDuplicateStatusLabels(), parseJsonObject(labels, labels || {}));
}

function uiTextFromRecord(app, record) {
  const formatRules = require(`${__hooks}/lib/format_rules.js`);
  var logoUrl = "/jpl.png";
  try {
    var logoFile = record.get("logo");
    if (logoFile) logoUrl = "/api/files/ui_settings/" + record.id + "/" + logoFile;
  } catch (err) {}

  var publicationOptions = lines(record.get("publicationOptions"), ["Already published", "Coming soon", "Published a while back"]);
  var ageGroups = lines(record.get("ageGroups"), ["Adult", "Young Adult / Teen", "Children"]);
  var formats = materialFormats(app);

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
    duplicateStatusLabels: {
      suggestion: record.get("duplicateLabelSuggestion") || "Received",
      outstanding_purchase: record.get("duplicateLabelOutstandingPurchase") || "Under review",
      pending_hold: record.get("duplicateLabelPendingHold") || "Being prepared",
      hold_placed: record.get("duplicateLabelHoldPlaced") || "Hold placed",
      closed: record.get("duplicateLabelClosed") || "Completed",
      rejected: record.get("duplicateLabelRejected") || "Not selected for purchase",
      hold_completed: record.get("duplicateLabelHoldCompleted") || "Completed",
      hold_not_picked_up: record.get("duplicateLabelHoldNotPickedUp") || "Closed",
      manual: record.get("duplicateLabelManual") || "Closed",
      silent: record.get("duplicateLabelSilent") || "Closed",
      "Silently Closed": record.get("duplicateLabelSilent") || "Closed"
    },
    noEmailMessage: record.get("noEmailMessage") || "No email is specified on your library account, which means we won't be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.",
    systemNotEnabledMessage: record.get("systemNotEnabledMessage") || "Your library does not currently participate in this suggestion service.",
    ebookMessage: record.get("ebookMessage") || "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>",
    eaudiobookMessage: record.get("eaudiobookMessage") || "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>",
    publicationOptions: publicationOptions,
    ageGroups: ageGroups,
    formatLabels: formats.labels,
    availableFormats: formats.available,
    formatRules: formatRules.normalizeFormatRules(formats.rules)
  };
}

function materialFormats(app) {
  app = app || $app;
  var labels = {};
  var rules = {};
  var available = [];
  try {
    var rows = app.findRecordsByFilter("material_formats", "id != ''", "sortOrder", 200, 0);
    for (var i = 0; i < rows.length; i++) {
      var r = rows[i];
      var code = r.get("code");
      labels[code] = r.get("label") || code;
      if (r.getBool("enabled")) available.push(code);
      rules[code] = {
        messageBehavior: r.get("messageBehavior") || "none",
        fields: {
          title: { mode: r.get("titleMode") || "required", label: r.get("titleLabel") || "Title" },
          author: { mode: r.get("authorMode") || "required", label: r.get("authorLabel") || "Author" },
          identifier: { mode: r.get("identifierMode") || "optional", label: r.get("identifierLabel") || "ISBN" },
          agegroup: { mode: r.get("audienceMode") || "required", label: r.get("audienceLabel") || "Age Group" },
          publication: { mode: r.get("publicationMode") || "required", label: r.get("publicationLabel") || "Publication Timing" }
        }
      };
    }
  } catch (err) {}
  return { labels: labels, rules: rules, available: available };
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
    smtp: smtpFromRecord(getSmtpSettings(app)),
    emails: emailsFor(app, ""),
    allowedStaffUsers: sys ? sys.get("allowedStaffUsers") || "" : "",
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
  rels.forEach(function (id) {
    try {
      var org = app.findRecordById("polaris_organizations", id);
      if (org.get("organizationId")) ids.push(String(org.get("organizationId")));
    } catch (err) {}
  });
  return ids.join(",");
}

function librarySettings(app, libraryOrgId) {
  app = app || $app;
  return {
    emails: emailsFor(app, libraryOrgId),
    ui_text: uiText(app, libraryOrgId),
    workflow: workflowFromRecord(workflowRecord(app, libraryOrgId))
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
  return uiTextFromRecord(app, uiRecord(app, orgId));
}

function duplicateStatusLabels(app, orgId) {
  return mergeDuplicateStatusLabels(uiText(app, orgId).duplicateStatusLabels);
}

function allowedStaffUsers() {
  const identity = require(`${__hooks}/lib/identity.js`);
  var sys = getSystemSettings($app);
  var value = String(sys ? sys.get("allowedStaffUsers") || "" : "").trim();
  return value ? identity.parseAllowedStaffUsers(value, polaris().staffDomain) : [];
}

function importToken() {
  var value = $os.getenv("ASAP_IMPORT_TOKEN");
  return value ? String(value).trim() : "";
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
  importToken: importToken,
  mail: mail,
  mergeDuplicateStatusLabels: mergeDuplicateStatusLabels,
  outstandingTimeout: outstandingTimeout,
  outstandingTimeoutEmail: outstandingTimeoutEmail,
  parseJsonObject: parseJsonObject,
  polaris: polaris,
  rejectionTemplates: rejectionTemplates,
  savePolarisSettings: savePolarisSettings,
  suggestionLimit: suggestionLimit,
  uiText: uiText,
};
