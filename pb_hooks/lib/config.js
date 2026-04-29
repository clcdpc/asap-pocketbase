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

function parseRecordJsonObject(record, fieldName, fallback) {
  fallback = fallback || {};
  if (!record) {
    return fallback;
  }
  var fromString = "";
  try {
    fromString = record.getString(fieldName);
  } catch (err) {
    fromString = "";
  }
  var parsedString = parseJsonObject(fromString, null);
  if (parsedString) {
    return parsedString;
  }
  var direct = parseJsonObject(record.get(fieldName), null);
  if (direct) {
    return direct;
  }
  return fallback;
}

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

function cloneJson(value) {
  return JSON.parse(JSON.stringify(value));
}

function defaultEmailTemplates() {
  return cloneJson(DEFAULT_EMAILS);
}

function mergeEmailTemplates(baseTemplates, overrideTemplates) {
  var base = parseJsonObject(baseTemplates, defaultEmailTemplates());
  var overrides = parseJsonObject(overrideTemplates, {});
  var merged = {};

  var overrideFromAddress = String(overrides.fromAddress || "").trim();
  var overrideFromName = String(overrides.fromName || "").trim();
  merged.fromAddress = overrideFromAddress ? overrideFromAddress : String(base.fromAddress || "");
  merged.fromName = overrideFromName ? overrideFromName : String(base.fromName || "");

  for (var i = 0; i < EMAIL_TEMPLATE_KEYS.length; i++) {
    var key = EMAIL_TEMPLATE_KEYS[i];
    merged[key] = {};
    var baseTemplate = parseJsonObject(base[key], {});
    var overrideTemplate = parseJsonObject(overrides[key], {});

    for (var j = 0; j < EMAIL_TEMPLATE_FIELDS.length; j++) {
      var field = EMAIL_TEMPLATE_FIELDS[j];
      var overrideValue = overrideTemplate[field];
      var baseValue = baseTemplate[field];
      merged[key][field] = String(overrideValue || "").trim() ? String(overrideValue) : String(baseValue || "");
    }
  }

  if (overrides.rejection_templates && Array.isArray(overrides.rejection_templates)) {
    merged.rejection_templates = overrides.rejection_templates;
  } else if (base.rejection_templates && Array.isArray(base.rejection_templates)) {
    merged.rejection_templates = base.rejection_templates;
  } else {
    merged.rejection_templates = [];
  }

  return merged;
}

function normalizeEmailTemplates(templates) {
  return mergeEmailTemplates(defaultEmailTemplates(), templates);
}

function diffEmailTemplates(baseTemplates, nextTemplates) {
  var base = normalizeEmailTemplates(baseTemplates);
  var next = normalizeEmailTemplates(nextTemplates);
  var diff = {};

  var nextFromAddress = String(next.fromAddress || "").trim();
  var baseFromAddress = String(base.fromAddress || "").trim();
  if (nextFromAddress && nextFromAddress !== baseFromAddress) {
    diff.fromAddress = nextFromAddress;
  }

  var nextFromName = String(next.fromName || "").trim();
  var baseFromName = String(base.fromName || "").trim();
  if (nextFromName && nextFromName !== baseFromName) {
    diff.fromName = nextFromName;
  }

  for (var i = 0; i < EMAIL_TEMPLATE_KEYS.length; i++) {
    var key = EMAIL_TEMPLATE_KEYS[i];
    var templateDiff = {};
    for (var j = 0; j < EMAIL_TEMPLATE_FIELDS.length; j++) {
      var field = EMAIL_TEMPLATE_FIELDS[j];
      var nextValue = String(next[key][field] || "").trim();
      var baseValue = String(base[key][field] || "").trim();
      if (nextValue && nextValue !== baseValue) {
        templateDiff[field] = next[key][field];
      }
    }
    if (Object.keys(templateDiff).length) {
      diff[key] = templateDiff;
    }
  }

  if (next.rejection_templates && Array.isArray(next.rejection_templates)) {
    diff.rejection_templates = next.rejection_templates;
  }

  return diff;
}

function hasEmailTemplateOverrides(templates) {
  templates = parseJsonObject(templates, {});

  if (String(templates.fromAddress || "").trim() || String(templates.fromName || "").trim()) {
    return true;
  }

  for (var i = 0; i < EMAIL_TEMPLATE_KEYS.length; i++) {
    var key = EMAIL_TEMPLATE_KEYS[i];
    var template = parseJsonObject(templates[key], {});
    for (var j = 0; j < EMAIL_TEMPLATE_FIELDS.length; j++) {
      var field = EMAIL_TEMPLATE_FIELDS[j];
      if (String(template[field] || "").trim()) {
        return true;
      }
    }
  }
  return false;
}

function getSettings() {
  const formatRules = require(`${__hooks}/lib/format_rules.js`);
  const defaultUiText = {
    logoUrl: "/jpl.png",
    logoAlt: "Library Logo",
    pageTitle: "Material Suggestion",
    barcodeLabel: "Library Card",
    pinLabel: "Pin",
    loginPrompt: "Please enter your information below to start the suggestion process.",
    suggestionFormNote: "If the library decides to purchase your suggestion, we will automatically place a hold on it and send a confirmation email. Make sure to check your spam folder if you don't see the email.",
    loginNote: "Use of this service requires a valid library card. Contact your library if you need assistance with your card or PIN.",
    successTitle: "Suggestion Submitted",
    successMessage: "You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>",
    alreadySubmittedMessage: "This suggestion has already been submitted from your account. You may submit an ISBN that other patrons have suggested, but you cannot submit the same ISBN twice from the same account. Check the catalog to see if the material was acquired and place a hold.<div>Thank you for using this library's suggestion service.</div>",
    noEmailMessage: "No email is specified on your library account, which means we won't be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.",
    systemNotEnabledMessage: "Your library does not currently participate in this suggestion service.",
    ebookMessage: "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>",
    eaudiobookMessage: "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>",
    publicationOptions: ["Already published", "Coming soon", "Published a while back"],
    ageGroups: ["Adult", "Young Adult / Teen", "Children"],
    formatLabels: {
      book: "Book",
      audiobook_cd: "Audiobook (Physical CD)",
      dvd: "DVD",
      music_cd: "Music CD",
      ebook: "eBook",
      eaudiobook: "eAudiobook"
    },
    availableFormats: ["book", "audiobook_cd", "dvd", "music_cd", "ebook", "eaudiobook"],
    formatRules: formatRules.defaultFormatRules()
  };

  const defaultEmails = defaultEmailTemplates();

  try {
    const record = $app.findRecordById("app_settings", "settings0000001");
    let logoUrl = "/jpl.png";
    const logoFile = record.get("logo");
    if (logoFile) {
      logoUrl = `/api/files/app_settings/${record.id}/${logoFile}`;
    }

    var dbUiText = parseRecordJsonObject(record, "ui_text", {});
    var dbPolaris = parseRecordJsonObject(record, "polaris", {});
    var dbSmtp = parseRecordJsonObject(record, "smtp", {});
    var dbEmails = parseRecordJsonObject(record, "emails", {});

    var mergedUiText = Object.assign({}, defaultUiText, dbUiText, { logoUrl: logoUrl });
    mergedUiText.formatRules = formatRules.normalizeFormatRules(mergedUiText.formatRules);

    return {
      polaris: dbPolaris,
      smtp: dbSmtp,
      emails: mergeEmailTemplates(defaultEmails, dbEmails),
      allowedStaffUsers: record.getString("allowedStaffUsers") || "",
      enabledLibraryOrgIds: record.getString("enabledLibraryOrgIds") || "",
      suggestionLimit: record.getInt("suggestionLimit") || 5,
      suggestionLimitMessage: record.getString("suggestionLimitMessage") || "Weekly suggestion limit reached",
      outstandingTimeoutEnabled: record.getBool("outstandingTimeoutEnabled"),
      outstandingTimeoutDays: record.getInt("outstandingTimeoutDays") || 30,
      holdPickupTimeoutEnabled: record.getBool("holdPickupTimeoutEnabled"),
      holdPickupTimeoutDays: record.getInt("holdPickupTimeoutDays") || 14,
      pendingHoldTimeoutEnabled: record.getBool("pendingHoldTimeoutEnabled"),
      pendingHoldTimeoutDays: record.getInt("pendingHoldTimeoutDays") || 14,
      commonAuthorsEnabled: record.getBool("commonAuthorsEnabled"),
      commonAuthorsList: record.getString("commonAuthorsList") || "",
      commonAuthorsMessage: record.getString("commonAuthorsMessage") || "We automatically purchase all upcoming titles by this author. Please check the catalog to place a hold on 'On Order' items.",
      outstandingTimeoutSendEmail: record.getBool("outstandingTimeoutSendEmail"),
      outstandingTimeoutRejectionTemplateId: record.getString("outstandingTimeoutRejectionTemplateId") || "",
      ui_text: mergedUiText
    };
  } catch (err) {
    return { polaris: {}, smtp: {}, emails: defaultEmailTemplates(), allowedStaffUsers: "", suggestionLimit: 5, suggestionLimitMessage: "Weekly suggestion limit reached", outstandingTimeoutEnabled: false, outstandingTimeoutDays: 30, holdPickupTimeoutEnabled: false, holdPickupTimeoutDays: 14, pendingHoldTimeoutEnabled: false, pendingHoldTimeoutDays: 14, enabledLibraryOrgIds: "", commonAuthorsEnabled: false, commonAuthorsList: "", commonAuthorsMessage: "We automatically purchase all upcoming titles by this author. Please check the catalog to place a hold on 'On Order' items.", outstandingTimeoutSendEmail: false, outstandingTimeoutRejectionTemplateId: "", ui_text: defaultUiText };
  }
}

function emails() {
  return getSettings().emails;
}

function librarySettings(app, libraryOrgId) {
  const systemDefaults = getSettings();
  libraryOrgId = String(libraryOrgId || "").trim();
  if (!libraryOrgId) {
    return {
      emails: systemDefaults.emails,
      ui_text: systemDefaults.ui_text,
      workflow: {
        suggestionLimit: systemDefaults.suggestionLimit,
        suggestionLimitMessage: systemDefaults.suggestionLimitMessage,
        outstandingTimeoutEnabled: systemDefaults.outstandingTimeoutEnabled,
        outstandingTimeoutDays: systemDefaults.outstandingTimeoutDays,
        holdPickupTimeoutEnabled: systemDefaults.holdPickupTimeoutEnabled,
        holdPickupTimeoutDays: systemDefaults.holdPickupTimeoutDays,
        pendingHoldTimeoutEnabled: systemDefaults.pendingHoldTimeoutEnabled,
        pendingHoldTimeoutDays: systemDefaults.pendingHoldTimeoutDays,
        commonAuthorsEnabled: systemDefaults.commonAuthorsEnabled,
        commonAuthorsList: systemDefaults.commonAuthorsList,
        commonAuthorsMessage: systemDefaults.commonAuthorsMessage,
        outstandingTimeoutSendEmail: systemDefaults.outstandingTimeoutSendEmail,
        outstandingTimeoutRejectionTemplateId: systemDefaults.outstandingTimeoutRejectionTemplateId
      }
    };
  }

  try {
    const record = app.findFirstRecordByData("library_settings", "libraryOrgId", libraryOrgId);
    var dbEmails = parseRecordJsonObject(record, "emails", {});
    var dbUiText = parseRecordJsonObject(record, "ui_text", {});
    var dbWorkflow = parseRecordJsonObject(record, "workflow", {});

    const formatRules = require(`${__hooks}/lib/format_rules.js`);
    var mergedUiText = Object.assign({}, systemDefaults.ui_text, dbUiText);
    mergedUiText.formatRules = formatRules.normalizeFormatRules(mergedUiText.formatRules);

    return {
      emails: mergeEmailTemplates(systemDefaults.emails, dbEmails),
      ui_text: mergedUiText,
      workflow: {
        suggestionLimit: typeof dbWorkflow.suggestionLimit === "number" ? dbWorkflow.suggestionLimit : systemDefaults.suggestionLimit,
        suggestionLimitMessage: dbWorkflow.suggestionLimitMessage || systemDefaults.suggestionLimitMessage,
        outstandingTimeoutEnabled: typeof dbWorkflow.outstandingTimeoutEnabled === "boolean" ? dbWorkflow.outstandingTimeoutEnabled : systemDefaults.outstandingTimeoutEnabled,
        outstandingTimeoutDays: typeof dbWorkflow.outstandingTimeoutDays === "number" ? dbWorkflow.outstandingTimeoutDays : systemDefaults.outstandingTimeoutDays,
        holdPickupTimeoutEnabled: typeof dbWorkflow.holdPickupTimeoutEnabled === "boolean" ? dbWorkflow.holdPickupTimeoutEnabled : systemDefaults.holdPickupTimeoutEnabled,
        holdPickupTimeoutDays: typeof dbWorkflow.holdPickupTimeoutDays === "number" ? dbWorkflow.holdPickupTimeoutDays : systemDefaults.holdPickupTimeoutDays,
        pendingHoldTimeoutEnabled: typeof dbWorkflow.pendingHoldTimeoutEnabled === "boolean" ? dbWorkflow.pendingHoldTimeoutEnabled : systemDefaults.pendingHoldTimeoutEnabled,
        pendingHoldTimeoutDays: typeof dbWorkflow.pendingHoldTimeoutDays === "number" ? dbWorkflow.pendingHoldTimeoutDays : systemDefaults.pendingHoldTimeoutDays,
        commonAuthorsEnabled: typeof dbWorkflow.commonAuthorsEnabled === "boolean" ? dbWorkflow.commonAuthorsEnabled : systemDefaults.commonAuthorsEnabled,
        commonAuthorsList: dbWorkflow.commonAuthorsList || systemDefaults.commonAuthorsList,
        commonAuthorsMessage: dbWorkflow.commonAuthorsMessage || systemDefaults.commonAuthorsMessage,
        outstandingTimeoutSendEmail: typeof dbWorkflow.outstandingTimeoutSendEmail === "boolean" ? dbWorkflow.outstandingTimeoutSendEmail : systemDefaults.outstandingTimeoutSendEmail,
        outstandingTimeoutRejectionTemplateId: dbWorkflow.outstandingTimeoutRejectionTemplateId || systemDefaults.outstandingTimeoutRejectionTemplateId
      }
    };
  } catch (err) {
    return {
      emails: systemDefaults.emails,
      ui_text: systemDefaults.ui_text,
      workflow: {
        suggestionLimit: systemDefaults.suggestionLimit,
        suggestionLimitMessage: systemDefaults.suggestionLimitMessage,
        outstandingTimeoutEnabled: systemDefaults.outstandingTimeoutEnabled,
        outstandingTimeoutDays: systemDefaults.outstandingTimeoutDays,
        holdPickupTimeoutEnabled: systemDefaults.holdPickupTimeoutEnabled,
        holdPickupTimeoutDays: systemDefaults.holdPickupTimeoutDays,
        pendingHoldTimeoutEnabled: systemDefaults.pendingHoldTimeoutEnabled,
        pendingHoldTimeoutDays: systemDefaults.pendingHoldTimeoutDays,
        commonAuthorsEnabled: systemDefaults.commonAuthorsEnabled,
        commonAuthorsList: systemDefaults.commonAuthorsList,
        commonAuthorsMessage: systemDefaults.commonAuthorsMessage,
        outstandingTimeoutSendEmail: systemDefaults.outstandingTimeoutSendEmail,
        outstandingTimeoutRejectionTemplateId: systemDefaults.outstandingTimeoutRejectionTemplateId
      }
    };
  }
}

function polaris() {
  const s = getSettings().polaris;
  return {
    host: s.host || "",
    accessId: s.accessId || "SuggestAPI",
    apiKey: s.apiKey || "",
    staffDomain: s.staffDomain || "",
    adminUser: s.adminUser || "",
    adminPassword: s.adminPassword || "",
    overridePassword: s.overridePassword || "",
    langId: s.langId || "1033",
    appId: s.appId || "100",
    orgId: s.orgId || "1",
    pickupOrgId: s.pickupOrgId || "0",
    requestingOrgId: s.requestingOrgId || "3",
    workstationId: s.workstationId || "1",
    userId: s.userId || "1",
  };
}

function mail() {
  const s = getSettings().smtp;
  return {
    host: String(s.host || "").trim(),
    port: parseInt(s.port, 10) || 587,
    username: String(s.username || "").trim(),
    password: s.password || "",
    tls: s.tls !== false,
  };
}

function emailStatus(app, orgId) {
  var smtp = mail();
  var e = app ? librarySettings(app, orgId).emails : emails();
  var hasSmtp = !!String(smtp.host || "").trim();
  var hasSender = !!String(e.fromAddress || "").trim();
  var enabled = hasSmtp && hasSender;
  return {
    enabled: enabled,
    hasSmtp: hasSmtp,
    hasSender: hasSender,
    message: enabled
      ? "Email notifications are configured."
      : "Email notifications are not configured. Suggestions and staff workflows still work, but patron emails will not be sent."
  };
}

function suggestionLimit(app, orgId) {
  if (!app) return getSettings();
  return librarySettings(app, orgId).workflow;
}

function outstandingTimeout(app, orgId) {
  if (!app) {
    const s = getSettings();
    return { enabled: s.outstandingTimeoutEnabled, days: s.outstandingTimeoutDays };
  }
  const wf = librarySettings(app, orgId).workflow;
  return { enabled: wf.outstandingTimeoutEnabled, days: wf.outstandingTimeoutDays };
}

function outstandingTimeoutEmail(app, orgId) {
  const settings = librarySettings(app, orgId);
  const wf = settings.workflow;
  const templates = settings.emails.rejection_templates || [];
  let templateId = String(wf.outstandingTimeoutRejectionTemplateId || "").trim();
  
  if (!wf.outstandingTimeoutSendEmail) {
    return { enabled: false, templateId: "" };
  }
  
  if (templates.length === 1 && !templateId) {
    templateId = templates[0].id;
  }
  
  return {
    enabled: true,
    templateId: templateId
  };
}

function holdPickupTimeout(app, orgId) {
  if (!app) {
    const s = getSettings();
    return { enabled: s.holdPickupTimeoutEnabled, days: s.holdPickupTimeoutDays };
  }
  const wf = librarySettings(app, orgId).workflow;
  return { enabled: wf.holdPickupTimeoutEnabled, days: wf.holdPickupTimeoutDays };
}

function pendingHoldTimeout(app, orgId) {
  if (!app) {
    const s = getSettings();
    return { enabled: s.pendingHoldTimeoutEnabled, days: s.pendingHoldTimeoutDays };
  }
  const wf = librarySettings(app, orgId).workflow;
  return { enabled: wf.pendingHoldTimeoutEnabled, days: wf.pendingHoldTimeoutDays };
}

function uiText(app, orgId) {
  if (!app) return getSettings().ui_text;
  return librarySettings(app, orgId).ui_text;
}

function allowedStaffUsers() {
  const identity = require(`${__hooks}/lib/identity.js`);
  const value = String(getSettings().allowedStaffUsers || "").trim();
  if (!value) return [];
  return identity.parseAllowedStaffUsers(value, polaris().staffDomain);
}

function importToken() {
  var value = $os.getenv("ASAP_IMPORT_TOKEN");
  return value ? String(value).trim() : "";
}

function applyMailSettings(app) {
  var cfg = mail();
  var e = emails();
  if (!e.fromAddress && !cfg.host) {
    return;
  }

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

module.exports = {
  allowedStaffUsers: allowedStaffUsers,
  applyMailSettings: applyMailSettings,
  defaultEmailTemplates: defaultEmailTemplates,
  diffEmailTemplates: diffEmailTemplates,
  emailStatus: emailStatus,
  emails: emails,
  hasEmailTemplateOverrides: hasEmailTemplateOverrides,
  getSettings: getSettings,
  librarySettings: librarySettings,
  holdPickupTimeout: holdPickupTimeout,
  pendingHoldTimeout: pendingHoldTimeout,
  importToken: importToken,
  mail: mail,
  mergeEmailTemplates: mergeEmailTemplates,
  normalizeEmailTemplates: normalizeEmailTemplates,
  outstandingTimeout: outstandingTimeout,
  outstandingTimeoutEmail: outstandingTimeoutEmail,
  polaris: polaris,
  suggestionLimit: suggestionLimit,
  uiText: uiText,
};
