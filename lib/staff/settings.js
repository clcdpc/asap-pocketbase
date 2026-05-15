var __hooks = typeof __hooks !== "undefined" ? __hooks : __dirname + "/../../pb_hooks";

const config = require(`${__hooks}/../lib/config.js`);
const identity = require(`${__hooks}/../lib/identity.js`);
// const jobs = require(`${__hooks}/../lib/jobs.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const formatClaimRules = require(`${__hooks}/../lib/format_claim_rules.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
// const additionalCopies = require(`${__hooks}/../lib/additional_copies.js`);
const title_requests = require('./title_requests.js');

const TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE = "This template can’t be deleted because it’s currently used by the auto-reject email. Assign a different template or disable auto-reject before deleting.";

const TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE = "TEMPLATE_IN_USE_BY_AUTO_REJECT";

const EMAIL_TEMPLATE_KEYS = ["suggestion_submitted", "already_owned", "rejected", "hold_placed"];

function getLibraryOverridesSummary(e) {
  try {
    var staff = routeUtils.requireSuperAdminStaff(e);
    if (!staff) {
      return e.json(403, { message: "Super admin access required." });
    }

    var orgs = e.app.findRecordsByFilter("polaris_organizations", "organizationCodeId = '2'", "", 0, 0);
    var idToOrgId = {};
    if (orgs) {
      for (var i = 0; i < orgs.length; i++) {
        var o = orgs[i];
        idToOrgId[o.id] = String(o.get("organizationId") || "").trim();
      }
    }

    var summary = {};

    function addEntry(orgId, section) {
      if (!orgId || orgId === "system") return;
      if (!summary[orgId]) summary[orgId] = [];
      if (summary[orgId].indexOf(section) === -1) {
        summary[orgId].push(section);
      }
    }

    function processList(list, section, useOrgIdField) {
      if (!list) return;
      for (var i = 0; i < list.length; i++) {
        var row = list[i];
        var orgId = "";
        if (useOrgIdField) {
          orgId = String(row.get("orgId") || row.get("libraryOrgId") || "").trim();
        } else {
          var relId = String(row.get("libraryOrganization") || "").trim();
          orgId = idToOrgId[relId];
        }
        addEntry(orgId, section);
      }
    }

    // Workflow
    processList(e.app.findRecordsByFilter("workflow_settings", "scope = 'library'", "", 0, 0), "workflow");

    // Patron Experience
    processList(e.app.findRecordsByFilter("ui_settings", "scope = 'library'", "", 0, 0), "patron");
    processList(e.app.findRecordsByFilter("material_formats", "scope = 'library'", "", 0, 0), "patron");
    processList(e.app.findRecordsByFilter("patron_settings_overrides", "", "", 0, 0), "patron", true);
    processList(e.app.findRecordsByFilter("patron_library_settings", "", "", 0, 0), "patron");

    // Templates
    processList(e.app.findRecordsByFilter("email_templates", "scope = 'library'", "", 0, 0), "templates");
    processList(e.app.findRecordsByFilter("rejection_templates", "scope = 'library'", "", 0, 0), "templates");

    // Staff Access (show which libraries have users)
    processList(e.app.findRecordsByFilter("staff_users", "libraryOrgId != 'system'", "", 0, 0), "staff", true);

    return e.json(200, summary);
  } catch (err) {
    if ($app && $app.logger()) {
      $app.logger().error("Overrides summary failed", "error", String(err));
    }
    return e.json(400, { message: String(err) });
  }
}



function getLibrarySettings(e) {
  try {
    var staff = routeUtils.requireAdminStaff(e);
    if (!staff) {
      return e.json(403, { message: "Admin access required to view settings." });
    }
    var orgId = String(routeUtils.queryValue(e, "orgId") || "").trim();

    if (!orgId) {
      orgId = String(staff.get("libraryOrgId") || "").trim();
    }

    if (orgId !== "system" && orgId !== String(staff.get("libraryOrgId") || "").trim() && !routeUtils.isSuperAdmin(staff)) {
      return e.json(403, { message: "Access denied to these library settings." });
    }

    if (orgId === "system") {
      if (!routeUtils.isSuperAdmin(staff)) {
        return e.json(403, { message: "Only super admins can view system settings." });
      }
      var s = config.getSettings();
      var wf = config.suggestionLimit(e.app, "");
      return e.json(200, {
        orgId: orgId,
        emails: s.emails,
        ui_text: s.ui_text,
        workflow: workflowWithEnabled(e.app, wf),
        formatClaimRules: [],
        formatClaimStaffOptions: [],
        polaris: s.polaris,
        smtp: s.smtp,
        staffUrl: s.staffUrl,
        leapBibUrlPattern: s.leapBibUrlPattern || "",
        emailStatus: config.emailStatus(e.app, ""),
        organizationSync: organizationSyncStatus(e.app),
        isOverride: false
      });
    }

    var ls = config.librarySettings(e.app, orgId);
    return e.json(200, {
      orgId: orgId,
      emails: ls.emails,
      ui_text: ls.ui_text,
      workflow: workflowWithEnabled(e.app, ls.workflow),
      formatClaimRules: formatClaimRulesForLibrary(e.app, orgId),
      formatClaimStaffOptions: formatClaimStaffOptions(e.app, orgId),
      leapBibUrlPattern: ls.leapBibUrlPattern || "",
      emailStatus: config.emailStatus(e.app, orgId === "system" ? "" : orgId),
      organizationSync: organizationSyncStatus(e.app),
      isOverride: hasLibraryOverride(e.app, orgId)
    });
  } catch (err) {
    e.app.logger().error("Failed to load library settings", "error", String(err));
    return e.json(500, { message: err.message || String(err) });
  }
}

function formatClaimRulesForLibrary(app, orgId) {
  orgId = String(orgId || "").trim();
  if (!orgId || orgId === "system") return [];
  try {
    var rows = app.findRecordsByFilter("format_claim_rules", "libraryOrgId = {:libraryOrgId} && active = true", "format", 500, 0, { libraryOrgId: orgId });
    return rows.map(function (row) {
      var staffUserId = normalizeRelationId(row.get("staffUserId")) || normalizeRelationId(row.get("staffUser"));
      return {
        id: row.id,
        libraryOrgId: row.get("libraryOrgId") || "",
        format: row.get("format") || "",
        staffUserId: staffUserId || "",
        active: row.getBool("active")
      };
    });
  } catch (err) {
    return [];
  }
}

function normalizeRelationId(value) {
  if (!value) return "";
  if (typeof value === "string") return value;
  if (Array.isArray(value)) {
    if (!value.length) return "";
    return normalizeRelationId(value[0]);
  }
  if (typeof value === "object") {
    return String(value.id || value.recordId || value.value || "").trim();
  }
  return String(value || "").trim();
}

function formatClaimStaffOptions(app, orgId) {
  orgId = String(orgId || "").trim();
  if (!orgId || orgId === "system") return [];
  try {
    var rows = app.findRecordsByFilter("staff_users", "(libraryOrgId = {:libraryOrgId} || role = 'super_admin') && active = true", "displayName,username", 500, 0, { libraryOrgId: orgId });
    return rows.map(function (row) {
      return {
        id: row.id,
        displayName: title_requests.staffClaimDisplayName(row),
        username: row.get("username") || "",
        role: row.get("role") || "staff",
        libraryOrgId: row.get("libraryOrgId") || ""
      };
    });
  } catch (err) {
    return [];
  }
}

function workflowWithEnabled(app, workflow) {
  var copy = Object.assign({}, workflow || {});
  copy.enabledLibraryOrgIds = config.enabledLibraryOrgIds(app);
  return copy;
}

function organizationSyncStatus(app) {
  var sys = config.getSystemSettings(app);
  return {
    status: sys ? sys.get("organizationsSyncStatus") || "not_loaded" : "not_loaded",
    message: sys ? sys.get("organizationsSyncMessage") || "" : "",
    error: sys ? sys.get("organizationsSyncError") || "" : "",
    lastSynced: sys ? sys.get("organizationsLastSynced") || "" : ""
  };
}

function hasLibraryOverride(app, orgId) {
  var org = config.findOrganization(app, orgId);
  if (!org) return false;
  var filters = [
    ["workflow_settings", "scope = 'library' && libraryOrganization = {:org}"],
    ["ui_settings", "scope = 'library' && libraryOrganization = {:org}"],
    ["email_templates", "scope = 'library' && libraryOrganization = {:org}"],
    ["rejection_templates", "scope = 'library' && libraryOrganization = {:org}"],
    ["material_formats", "scope = 'library' && libraryOrganization = {:org}"],
    ["patron_settings_overrides", "orgId = {:orgId}"],
    ["patron_library_settings", "libraryOrganization = {:org}"]
  ];
  for (var i = 0; i < filters.length; i++) {
    try {
      app.findFirstRecordByFilter(filters[i][0], filters[i][1], { org: org.id, orgId: String(orgId || "").trim() });
      return true;
    } catch (err) { }
  }
  return false;
}

function updateLibrarySettings(e) {
  var staff = routeUtils.requireAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Admin access required to update settings." });
  }
  var payload = routeUtils.body(e);
  payload._staffUser = staff;
  var orgId = String(payload.orgId || "").trim();
  var action = String(payload.action || "save").toLowerCase();

  if (!orgId) {
    return e.json(400, { message: "orgId is required." });
  }

  if (orgId !== "system" && orgId !== String(staff.get("libraryOrgId") || "").trim() && !routeUtils.isSuperAdmin(staff)) {
    return e.json(403, { message: "Access denied to these library settings." });
  }

  // For library-scoped saves by non-super-admins, strip global-only fields
  // so even if the frontend sends them, they cannot affect system settings.
  if (orgId !== "system" && !routeUtils.isSuperAdmin(staff)) {
    delete payload.polaris;
    delete payload.smtp;
    delete payload.staffUrl;
    delete payload.leapBibUrlPattern;
    if (payload.workflow) {
      delete payload.workflow.enabledLibraryOrgIds;
    }
  }

  if (orgId === "system") {
    if (!routeUtils.isSuperAdmin(staff)) {
      return e.json(403, { message: "Only super admins can update system settings." });
    }
    try {
      saveSystemSettingsPayload(e.app, payload);
    } catch (err) {
      var systemErrorPayload = { message: err.message || String(err) };
      if (err.code) systemErrorPayload.code = err.code;
      return e.json(400, systemErrorPayload);
    }
  } else {
    try {
      if (action === "reset") {
        resetLibrarySettings(e.app, orgId);
      } else {
        saveLibraryScopedSettings(e.app, orgId, payload);
      }
    } catch (err) {
      var errorPayload = { message: err.message || String(err) };
      if (err.code) errorPayload.code = err.code;
      return e.json(400, errorPayload);
    }
  }

  return e.json(200, { success: true });
}

function recordForScope(app, collectionName, scope, orgId) {
  var collection = app.findCollectionByNameOrId(collectionName);
  if (scope === "system") {
    try {
      return app.findFirstRecordByFilter(collectionName, "scope = 'system'");
    } catch (err) {
      var sys = new Record(collection);
      sys.set("scope", "system");
      return sys;
    }
  }
  var org = config.findOrganization(app, orgId);
  if (!org) throw new Error("Library organization must be synced before saving library-specific settings.");
  try {
    return app.findFirstRecordByFilter(collectionName, "scope = 'library' && libraryOrganization = {:org}", { org: org.id });
  } catch (err) {
    var rec = new Record(collection);
    rec.set("scope", "library");
    rec.set("libraryOrganization", org.id);
    return rec;
  }
}

function saveSystemSettingsPayload(app, payload) {
  var systemSettingsData = {};
  var hasSystemSettingsData = false;
  if (Object.prototype.hasOwnProperty.call(payload, "staffUrl")) {
    systemSettingsData.staffUrl = payload.staffUrl;
    hasSystemSettingsData = true;
  }
  if (Object.prototype.hasOwnProperty.call(payload, "leapBibUrlPattern")) {
    systemSettingsData.leapBibUrlPattern = payload.leapBibUrlPattern;
    hasSystemSettingsData = true;
  }
  if (hasSystemSettingsData) {
    config.saveSystemSettings(app, systemSettingsData);
  }
  if (payload.polaris) {
    var polarisData = routeUtils.buildPolarisData({ polaris: payload.polaris });
    config.savePolarisSettings(app, polarisData);
    if (polarisData.host && polarisData.accessId && polarisData.apiKey) {
      try {
        orgs.syncOrganizations(app, polaris.adminStaffAuth(polarisData));
      } catch (syncErr) {
        app.logger().warn("Polaris organization sync failed after settings save", "error", String(syncErr));
      }
    }
  }
  if (payload.smtp) saveSmtpSettings(app, payload.smtp);
  saveWorkflowSettings(app, "system", "", payload.workflow || {});
  saveUiSettings(app, "system", "", payload.ui_text || {});
  saveEmailSettings(app, "system", "", payload.emails || {});
  if (payload.workflow && payload.workflow.enabledLibraryOrgIds !== undefined) {
    saveEnabledLibraries(app, payload.workflow.enabledLibraryOrgIds);
  }
}

function saveLibraryScopedSettings(app, orgId, payload) {
  saveWorkflowSettings(app, "library", orgId, payload.workflow || {});
  saveUiSettings(app, "library", orgId, payload.ui_text || {});
  saveEmailSettings(app, "library", orgId, payload.emails || {});
  if (Object.prototype.hasOwnProperty.call(payload, "formatClaimRules")) {
    saveFormatClaimRules(app, orgId, payload.formatClaimRules || [], payload._staffUser || null);
  }
}

function saveFormatClaimRules(app, orgId, rules, staff) {
  orgId = String(orgId || "").trim();
  if (!orgId || orgId === "system") return;
  var org = config.findOrganization(app, orgId);
  if (!org) throw new Error("Library organization must be synced before saving format claim rules.");
  var desired = {};
  (Array.isArray(rules) ? rules : []).forEach(function (rule) {
    var format = String(rule && rule.format || "").trim();
    var staffUserId = String(rule && rule.staffUserId || "").trim();
    if (format) desired[format] = staffUserId;
  });

  var existing = {};
  try {
    var rows = app.findRecordsByFilter("format_claim_rules", "libraryOrgId = {:libraryOrgId}", "", 500, 0, { libraryOrgId: orgId });
    rows.forEach(function (row) {
      existing[String(row.get("format") || "")] = row;
    });
  } catch (err) {}

  Object.keys(desired).forEach(function (format) {
    var staffUserId = desired[format];
    var row = existing[format] || null;
    if (!staffUserId) {
      if (row) app.delete(row);
      return;
    }
    var targetStaff = app.findRecordById("staff_users", staffUserId);
    if (!targetStaff || targetStaff.getBool("active") === false) {
      throw new Error("Automatic claimant for " + format + " is not an active staff user.");
    }
    if (String(targetStaff.get("libraryOrgId") || "").trim() !== orgId && String(targetStaff.get("role") || "") !== "super_admin") {
      throw new Error("Automatic claimant for " + format + " must belong to the selected library.");
    }
    if (!row) row = new Record(app.findCollectionByNameOrId("format_claim_rules"));
    row.set("libraryOrgId", orgId);
    row.set("libraryOrganization", org.id);
    row.set("format", format);
    row.set("staffUser", staffUserId);
    row.set("staffUserId", staffUserId);
    row.set("active", true);
    if (staff && staff.id) {
      if (!row.id) row.set("createdBy", staff.id);
      row.set("updatedBy", staff.id);
    }
    app.save(row);
  });
  Object.keys(existing).forEach(function (format) {
    if (!Object.prototype.hasOwnProperty.call(desired, format)) {
      app.delete(existing[format]);
    }
  });
}

function savePatronLibrarySettings(app, orgId, ui) {
  if (!ui) return;
  if (!config.findOrganization(app, orgId)) throw new Error("Library organization must be synced before saving library-specific settings.");
  var collection = app.findCollectionByNameOrId("patron_settings_overrides");
  var record;
  try {
    record = app.findFirstRecordByFilter("patron_settings_overrides", "orgId = {:orgId}", { orgId: String(orgId || "").trim() });
  } catch (err) {
    record = new Record(collection);
    record.set("orgId", String(orgId || "").trim());
  }
  if (ui.duplicateStatusLabels !== undefined) record.set("duplicateStatusLabels", config.mergeDuplicateStatusLabels(ui.duplicateStatusLabels));
  if (ui.publicationOptions !== undefined) record.set("publicationOptions", ui.publicationOptions);
  if (ui.formatRules !== undefined) record.set("patronFormatRules", ui.formatRules);
  if (ui.ebookMessage !== undefined) record.set("ebookMessage", ui.ebookMessage);
  if (ui.eaudiobookMessage !== undefined) record.set("eaudiobookMessage", ui.eaudiobookMessage);
  app.save(record);
}

function saveSmtpSettings(app, smtp) {
  var record = config.getSmtpSettings(app);
  ["host", "port", "tls"].forEach(function (key) {
    if (smtp[key] !== undefined) record.set(key, smtp[key]);
  });
  if (Object.prototype.hasOwnProperty.call(smtp, "username") && String(smtp.username || "").trim()) {
    record.set("username", String(smtp.username).trim());
  }
  if (Object.prototype.hasOwnProperty.call(smtp, "password") && String(smtp.password || "").trim()) {
    record.set("password", String(smtp.password));
  }
  if (smtp.fromAddress !== undefined) record.set("fromAddress", smtp.fromAddress);
  if (smtp.fromName !== undefined) record.set("fromName", smtp.fromName);
  app.save(record);
}

function saveEnabledLibraries(app, csv) {
  var sys = config.getSystemSettings(app);
  var ids = String(csv || "").split(",").map(function (s) { return s.trim(); }).filter(Boolean);
  var rels = [];
  var all = app.findRecordsByFilter("polaris_organizations", "organizationCodeId = '2'", "", 1000, 0);
  for (var i = 0; i < all.length; i++) {
    var enabled = ids.length === 0 || ids.indexOf(String(all[i].get("organizationId"))) >= 0;
    all[i].set("enabledForPatrons", enabled);
    app.save(all[i]);
    if (ids.indexOf(String(all[i].get("organizationId"))) >= 0) rels.push(all[i].id);
  }
  sys.set("enabledLibraries", rels);
  app.save(sys);
}

function saveWorkflowSettings(app, scope, orgId, wf) {
  var record = recordForScope(app, "workflow_settings", scope, orgId);
  ["suggestionLimit", "suggestionLimitMessage", "outstandingTimeoutEnabled", "outstandingTimeoutDays", "outstandingTimeoutSendEmail", "holdPickupTimeoutEnabled", "holdPickupTimeoutDays", "pendingHoldTimeoutEnabled", "pendingHoldTimeoutDays", "autoPromote", "commonAuthorsEnabled", "commonAuthorsList", "commonAuthorsMessage", "commonAuthorsLabel", "commonAuthorsHelp", "allowPatronAutoholdOptOut", "externalSearch1Enabled", "externalSearch1Label", "externalSearch1UrlTemplate", "externalSearch2Enabled", "externalSearch2Label", "externalSearch2UrlTemplate", "externalSearch3Enabled", "externalSearch3Label", "externalSearch3UrlTemplate", "externalSearch4Enabled", "externalSearch4Label", "externalSearch4UrlTemplate"].forEach(function (key) {
    if (wf[key] !== undefined) record.set(key, wf[key]);
  });
  if (!wf.outstandingTimeoutEnabled || !wf.outstandingTimeoutSendEmail) {
    record.set("outstandingTimeoutRejectionTemplate", "");
  } else if (Object.prototype.hasOwnProperty.call(wf, "outstandingTimeoutRejectionTemplateId")) {
    record.set("outstandingTimeoutRejectionTemplate", wf.outstandingTimeoutRejectionTemplateId || "");
  }
  app.save(record);
}

function validatePublicationOptionsDeletion(app, scope, orgId, ui) {
  if (ui.publicationOptions === undefined) return;
  var labels = Array.isArray(ui.publicationOptions) ? ui.publicationOptions : String(ui.publicationOptions || "").split(/\r?\n/);
  labels = labels.map(function (label) {
    return String(label && typeof label === "object" ? label.label || "" : label || "").trim();
  }).filter(Boolean);

  var keep = {};
  labels.forEach(function (label) {
    keep[label.toLowerCase()] = true;
  });

  var oldOptionsRaw;
  if (scope === "library") {
    try {
      var override = app.findFirstRecordByFilter("patron_settings_overrides", "orgId = {:orgId}", { orgId: String(orgId || "").trim() });
      oldOptionsRaw = override.get("publicationOptions");
    } catch (err) {}
  }

  if (oldOptionsRaw === undefined) {
    var record = recordForScope(app, "ui_settings", "system", "");
    oldOptionsRaw = record.get("publicationOptions");
  }
  var oldOptions = [];
  if (typeof oldOptionsRaw === "string") {
    var trimmed = oldOptionsRaw.trim();
    if (trimmed.charAt(0) === "[") {
      try { oldOptions = JSON.parse(trimmed); } catch (e) { }
    } else {
      oldOptions = trimmed.split(/\r?\n/).map(function(l) { return { label: l.trim() }; }).filter(function(o) { return o.label; });
    }
  } else if (Array.isArray(oldOptionsRaw)) {
    oldOptions = oldOptionsRaw;
  }

  var toCheck = [];
  oldOptions.forEach(function (opt) {
    var optLabel = String(opt && typeof opt === "object" ? opt.label || "" : opt || "").trim();
    if (!optLabel) return;
    if (!keep[optLabel.toLowerCase()]) {
      toCheck.push(optLabel);
    }
  });

  if (toCheck.length > 0) {
    var batchSize = 100;
    for (var j = 0; j < toCheck.length; j += batchSize) {
      var chunk = toCheck.slice(j, j + batchSize);
      var filterParts = [];
      var checkParams = {};
      for (var k = 0; k < chunk.length; k++) {
        filterParts.push("publication = {:p" + k + "}");
        checkParams["p" + k] = chunk[k];
      }
      var batchFilter = filterParts.join(" || ");
      try {
        var usedRequest = app.findFirstRecordByFilter("title_requests", batchFilter, checkParams);
        if (usedRequest) {
          var usedLabel = usedRequest.get("publication");
          var err = new Error("Publication timing '" + usedLabel + "' is currently in use by existing requests and cannot be deleted. You can disable it instead.");
          err.code = 400;
          throw err;
        }
      } catch (findErr) {
        if (findErr.message && findErr.message.indexOf("in use") >= 0) {
          throw findErr;
        }
      }
    }
  }
}

function saveUiSettings(app, scope, orgId, ui) {
  validatePublicationOptionsDeletion(app, scope, orgId, ui);
  validateMaterialFormatsDeletion(app, scope, orgId, ui);

  var record = recordForScope(app, "ui_settings", scope, orgId);
  var fieldMap = {
    logoAlt: "logoAlt", pageTitle: "pageTitle", barcodeLabel: "barcodeLabel", pinLabel: "pinLabel",
    loginPrompt: "loginPrompt", loginNote: "loginNote", suggestionFormNote: "suggestionFormNote",
    noEmailMessage: "noEmailMessage", successTitle: "successTitle", successMessage: "successMessage",
    alreadySubmittedMessage: "alreadySubmittedMessage", ebookMessage: "ebookMessage",
    eaudiobookMessage: "eaudiobookMessage"
  };
  Object.keys(fieldMap).forEach(function (key) {
    if (scope === "library" && (key === "ebookMessage" || key === "eaudiobookMessage")) return;
    if (ui[key] !== undefined) record.set(fieldMap[key], ui[key]);
  });
  if (ui.duplicateStatusLabels && scope === "system") {
    var d = ui.duplicateStatusLabels;
    record.set("duplicateLabelSuggestion", d.suggestion || "");
    record.set("duplicateLabelOutstandingPurchase", d.outstanding_purchase || "");
    record.set("duplicateLabelPendingHold", d.pending_hold || "");
    record.set("duplicateLabelHoldPlaced", d.hold_placed || "");
    record.set("duplicateLabelClosed", d.closed || "");
    record.set("duplicateLabelRejected", d.rejected || "");
    record.set("duplicateLabelHoldCompleted", d.hold_completed || "");
    record.set("duplicateLabelHoldNotPickedUp", d.hold_not_picked_up || "");
    record.set("duplicateLabelManual", d.manual || "");
    record.set("duplicateLabelSilent", d.silent || d["Silently Closed"] || "");
  }
  if (scope === "library") {
    savePatronLibrarySettings(app, orgId, ui);
  }
  if (ui.systemNotEnabledMessage !== undefined) record.set("systemNotEnabledMessage", ui.systemNotEnabledMessage);
  if (scope === "system" && ui.publicationOptions !== undefined) record.set("publicationOptions", optionsToJson(ui.publicationOptions));
  app.save(record);
  saveMaterialFormats(app, scope, orgId, ui);
}

function optionsToLines(options) {
  if (!Array.isArray(options)) return String(options || "");
  return options.map(function (item) {
    return String(item && typeof item === "object" ? item.label || "" : item || "").trim();
  }).filter(Boolean).join("\n");
}

function optionsToJson(options) {
  if (!Array.isArray(options)) return "[]";
  return JSON.stringify(options);
}

function scopedLookupRecord(app, collectionName, scope, orgId, code) {
  var collection = app.findCollectionByNameOrId(collectionName);
  var org = scope === "library" ? config.findOrganization(app, orgId) : null;
  if (scope === "library" && !org) {
    throw new Error("Library organization must be synced before saving library-specific settings.");
  }
  var filter = scope === "system" ? "scope = 'system' && code = {:code}" : "scope = 'library' && libraryOrganization = {:org} && code = {:code}";
  var params = scope === "system" ? { code: code } : { org: org.id, code: code };
  try {
    return app.findFirstRecordByFilter(collectionName, filter, params);
  } catch (err) {
    if (scope === "system") {
      // Also try finding any existing record with this code (handles legacy records with no scope set)
      try {
        var existing = app.findFirstRecordByData(collectionName, "code", code);
        if (existing) {
          existing.set("scope", "system");
          return existing;
        }
      } catch (err2) {}
    }
    var record = new Record(collection);
    record.set("scope", scope);
    if (org) record.set("libraryOrganization", org.id);
    record.set("code", code);
    return record;
  }
}

function validateMaterialFormatsDeletion(app, scope, orgId, ui) {
  if (ui.formatLabels === undefined) return;
  var keep = ui.formatLabels || {};
  var org = scope === "library" ? config.findOrganization(app, orgId) : null;
  var filter = scope === "system" ? "scope != 'library'" : "scope = 'library' && libraryOrganization = {:org}";
  var params = scope === "system" ? {} : { org: org ? org.id : "" };

  try {
    var rows = app.findRecordsByFilter("material_formats", filter, "", 200, 0, params);
    var toCheck = [];
    for (var i = 0; i < rows.length; i++) {
      var row = rows[i];
      var code = row.get("code");
      if (code && !Object.prototype.hasOwnProperty.call(keep, code)) {
        toCheck.push(row);
      }
    }

    if (toCheck.length > 0) {
      var batchSize = 100;
      for (var j = 0; j < toCheck.length; j += batchSize) {
        var chunk = toCheck.slice(j, j + batchSize);
        var filterParts = [];
        var checkParams = {};
        for (var k = 0; k < chunk.length; k++) {
          filterParts.push("format = {:p" + k + "}");
          checkParams["p" + k] = chunk[k].get("code");
        }
        var batchFilter = filterParts.join(" || ");
        try {
          var usedRequest = app.findFirstRecordByFilter("title_requests", batchFilter, checkParams);
          if (usedRequest) {
            var usedCode = usedRequest.get("format");
            var usedLabel = "";
            for (var k = 0; k < chunk.length; k++) {
              if (chunk[k].get("code") === usedCode) {
                usedLabel = chunk[k].get("label") || usedCode;
                break;
              }
            }
            var err = new Error("Format '" + usedLabel + "' is currently in use by existing requests and cannot be deleted. You can disable it instead.");
            err.code = 400;
            throw err;
          }
        } catch (findErr) {
          if (findErr.message && findErr.message.indexOf("in use") >= 0) {
            throw findErr;
          }
        }
      }
    }
  } catch (err) {
    if (err.message && err.message.indexOf("in use") >= 0) {
      throw err;
    }
  }
}

function saveMaterialFormats(app, scope, orgId, ui) {
  var labels = ui.formatLabels || {};
  var available = Array.isArray(ui.availableFormats) ? ui.availableFormats : [];
  var formatOrderPayload = Array.isArray(ui.formatOrder) ? ui.formatOrder : [];

  var rules = ui.formatRules || {};
  var orderedCodes = formatOrderPayload.filter(function (code, index) {
    return Object.prototype.hasOwnProperty.call(labels, code) && formatOrderPayload.indexOf(code) === index;
  });

  Object.keys(labels).forEach(function (code) {
    if (orderedCodes.indexOf(code) < 0) orderedCodes.push(code);
  });

  orderedCodes.forEach(function (code, index) {
    var record = scopedLookupRecord(app, "material_formats", scope, orgId, code);
    var rule = rules[code] || {};
    var fields = rule.fields || {};
    record.set("label", labels[code] || code);
    record.set("enabled", available.length ? available.indexOf(code) >= 0 : true);
    record.set("sortOrder", (index + 1) * 10);
    record.set("messageBehavior", rule.messageBehavior || "none");
    setFormatFieldRule(record, "title", fields.title, "Title");
    setFormatFieldRule(record, "author", fields.author, "Author");
    setFormatFieldRule(record, "identifier", fields.identifier, "Identifier number");
    setFormatFieldRule(record, "publication", fields.publication, "Publication Timing");
    app.save(record);
  });

  // Delete formats that are no longer in the provided labels map
  var org = scope === "library" ? config.findOrganization(app, orgId) : null;
  var filter = scope === "system" ? "scope != 'library'" : "scope = 'library' && libraryOrganization = {:org}";
  var params = scope === "system" ? {} : { org: org ? org.id : "" };
  var existing = app.findRecordsByFilter("material_formats", filter, "", 200, 0, params);
  existing.forEach(function (rec) {
    var code = rec.get("code");
    if (code && !Object.prototype.hasOwnProperty.call(labels, code)) {
      app.delete(rec);
    }
  });
}

function setFormatFieldRule(record, prefix, rule, fallback) {
  rule = rule || {};
  record.set(prefix + "Mode", rule.mode || (prefix === "identifier" ? "optional" : "required"));
  record.set(prefix + "Label", rule.label || fallback);
}

function saveEmailSettings(app, scope, orgId, emails) {
  EMAIL_TEMPLATE_KEYS.forEach(function (key) {
    var tpl = emails[key] || {};
    if (!tpl.subject && !tpl.body && scope === "library") {
      var existing = findEmailTemplateRecord(app, scope, orgId, key);
      if (existing) app.delete(existing);
      return;
    }
    var record = emailTemplateRecord(app, scope, orgId, key);
    record.set("templateKey", key);
    record.set("name", key);
    if (tpl.subject !== undefined) record.set("subject", tpl.subject);
    if (tpl.body !== undefined) record.set("body", tpl.body);
    if (emails.fromAddress !== undefined) record.set("fromAddress", emails.fromAddress);
    if (emails.fromName !== undefined) record.set("fromName", emails.fromName);
    record.set("enabled", true);
    app.save(record);
  });
  saveRejectionTemplates(app, scope, orgId, emails.rejection_templates || []);
}

function findEmailTemplateRecord(app, scope, orgId, key) {
  var org = scope === "library" ? config.findOrganization(app, orgId) : null;
  var filter = scope === "system" ? "scope = 'system' && templateKey = {:key}" : "scope = 'library' && libraryOrganization = {:org} && templateKey = {:key}";
  var params = scope === "system" ? { key: key } : { org: org.id, key: key };
  try {
    return app.findFirstRecordByFilter("email_templates", filter, params);
  } catch (err) {
    return null;
  }
}

function emailTemplateRecord(app, scope, orgId, key) {
  var org = scope === "library" ? config.findOrganization(app, orgId) : null;
  var existing = findEmailTemplateRecord(app, scope, orgId, key);
  if (existing) return existing;
  var rec = new Record(app.findCollectionByNameOrId("email_templates"));
  rec.set("scope", scope);
  if (org) rec.set("libraryOrganization", org.id);
  return rec;
}

function saveRejectionTemplates(app, scope, orgId, templates) {
  var org = scope === "library" ? config.findOrganization(app, orgId) : null;
  var keep = {};

  var templateIds = [];
  for (var j = 0; j < templates.length; j++) {
    if (templates[j] && templates[j].id) {
      var strId = String(templates[j].id);
      keep[strId] = true;
      templateIds.push(strId);
    }
  }

  var existingRecords = {};
  if (templateIds.length > 0) {
    var batchSize = 100;
    for (var k = 0; k < templateIds.length; k += batchSize) {
      var chunk = templateIds.slice(k, k + batchSize);
      var filterParts = [];
      var batchParams = {};
      for (var m = 0; m < chunk.length; m++) {
        filterParts.push("id = {:p" + m + "}");
        batchParams["p" + m] = chunk[m];
      }
      var batchFilter = filterParts.join(" || ");
      try {
        var results = app.findRecordsByFilter("rejection_templates", batchFilter, "", chunk.length, 0, batchParams);
        for (var n = 0; n < results.length; n++) {
          existingRecords[results[n].id] = results[n];
        }
      } catch (err) {
        // Ignored
      }
    }
  }

  for (var i = 0; i < templates.length; i++) {
    var t = templates[i] || {};
    var record = null;
    if (t.id && existingRecords[String(t.id)]) {
      record = existingRecords[String(t.id)];
    }
    if (!record) {
      record = new Record(app.findCollectionByNameOrId("rejection_templates"));
      record.set("scope", scope);
      if (org) record.set("libraryOrganization", org.id);
    }
    record.set("name", t.name || "Rejection template");
    record.set("subject", t.subject || "");
    record.set("body", t.body || "");
    record.set("enabled", true);
    record.set("sortOrder", i + 1);
    app.save(record);
    if (record.id) keep[String(record.id)] = true;
  }
  var filter = scope === "system" ? "scope = 'system' && enabled = true" : "scope = 'library' && libraryOrganization = {:org} && enabled = true";
  var params = scope === "system" ? {} : { org: org.id };
  try {
    var rows = app.findRecordsByFilter("rejection_templates", filter, "sortOrder", 200, 0, params);
    var toDelete = [];
    rows.forEach(function (row) {
      if (!keep[row.id]) toDelete.push(row);
    });

    if (toDelete.length > 0) {
      var checkFilter = [];
      var checkParams = {};
      toDelete.forEach(function (row, index) {
        var p = "p" + index;
        checkFilter.push("outstandingTimeoutRejectionTemplate = {:" + p + "}");
        checkParams[p] = row.id;
      });

      var inUseRecords = app.findRecordsByFilter("workflow_settings", checkFilter.join(" || "), "", 1, 0, checkParams);
      if (inUseRecords && inUseRecords.length > 0) {
        var inUseErr = new Error(TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE);
        inUseErr.code = TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE;
        throw inUseErr;
      }

      toDelete.forEach(function (row) {
        app.delete(row);
      });
    }
  } catch (err2) {
    throw err2;
  }
}

function assertRejectionTemplateNotUsedByAutoReject(app, templateId) {
  try {
    app.findFirstRecordByFilter("workflow_settings", "outstandingTimeoutRejectionTemplate = {:template}", { template: templateId });
  } catch (err) {
    return;
  }
  var inUseErr = new Error(TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE);
  inUseErr.code = TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE;
  throw inUseErr;
}

function resetLibrarySettings(app, orgId) {
  var org = config.findOrganization(app, orgId);
  if (!org) return;
  ["workflow_settings", "ui_settings", "email_templates", "rejection_templates", "material_formats"].forEach(function (collection) {
    try {
      var rows = app.findRecordsByFilter(collection, "scope = 'library' && libraryOrganization = {:org}", "", 200, 0, { org: org.id });
      rows.forEach(function (row) { app.delete(row); });
    } catch (err) { }
  });
  try {
    var overrideRows = app.findRecordsByFilter("patron_settings_overrides", "orgId = {:orgId}", "", 200, 0, { orgId: String(orgId || "").trim() });
    overrideRows.forEach(function (row) { app.delete(row); });
  } catch (errOverride) { }
  try {
    var patronRows = app.findRecordsByFilter("patron_library_settings", "libraryOrganization = {:org}", "", 200, 0, { org: org.id });
    patronRows.forEach(function (row) { app.delete(row); });
  } catch (err2) { }
  try {
    var brandingRows = app.findRecordsByFilter("library_settings", "libraryOrganization = {:org}", "", 200, 0, { org: org.id });
    brandingRows.forEach(function (row) { app.delete(row); });
  } catch (err3) { }
}

function staffSaveLogo(e) {
  var app = $app;
  try {
    var info = e.requestInfo();
    var auth = info.auth;
    var orgId = routeUtils.queryValue(e, "orgId");
    var isSystem = orgId === "system";
    var org = isSystem ? null : config.findOrganization(app, orgId);

    var isSuperAdmin = auth && auth.get("role") === "super_admin";
    var isAdmin = auth && auth.get("role") === "admin";

    if (isSystem && !isSuperAdmin) {
      return e.json(403, { success: false, message: "Only super admins can manage system settings." });
    }

    if (!isSystem && !org) {
      app.logger().error("Logo Upload Error: Org not found", "orgId", orgId);
      return e.json(400, { success: false, message: "Invalid library organization: " + orgId });
    }

    if (!isSystem && !isSuperAdmin) {
      var staffLibId = auth.get("libraryOrgId");
      var targetLibId = String(org.get("organizationId") || "").trim();
      if (!isAdmin || staffLibId !== targetLibId) {
        return e.json(403, { success: false, message: "You do not have permission to manage settings for this library." });
      }
    }

    var logoAlt = e.request.formValue("logoAlt") || "";
    var uploadedFiles = e.findUploadedFiles("logo");
    var logoFile = (uploadedFiles && uploadedFiles.length > 0) ? uploadedFiles[0] : null;

    // Fallback logging and extraction if findUploadedFiles fails
    if (!logoFile) {
        try {
            var formData = e.request.formData();
            app.logger().debug("Logo Upload: Manual FormData check", "hasFiles", !!formData.files, "keys", Object.keys(formData.files || {}));
            if (formData.files && formData.files.logo && formData.files.logo.length > 0) {
                logoFile = formData.files.logo[0];
            }
        } catch (err) {
            app.logger().debug("Logo Upload: FormData error", "err", err.message);
        }
    }

    app.logger().debug("Logo Upload: Final check", "orgId", orgId, "hasFile", !!logoFile, "fileName", logoFile ? logoFile.name : "null");

    var record = recordForScope(app, "ui_settings", isSystem ? "system" : "library", orgId);
    record.set("logoAlt", logoAlt);
    if (logoFile) {
      app.logger().debug("Logo Upload: Attaching file to ui_settings", "fileName", logoFile.name);
      record.set("logo", logoFile);
    }
    app.save(record);

    return e.json(200, { success: true, message: "Logo updated successfully." });
  } catch (globalErr) {
    app.logger().error("Global Logo Upload Error", "error", String(globalErr));
    return e.json(500, { success: false, message: "Server error: " + String(globalErr) });
  }
}

function staffResetLogo(e) {
  var app = $app;
  var auth = e.requestInfo().auth;
  var orgId = routeUtils.queryValue(e, "orgId");
  if (!orgId || orgId === "system") {
    return e.json(400, { success: false, message: "System logo cannot be reset to a default via this endpoint." });
  }

  var org = config.findOrganization(app, orgId);
  if (!org) {
    return e.json(400, { success: false, message: "Invalid library organization." });
  }

  var isSuperAdmin = auth && auth.get("role") === "super_admin";
  var isAdmin = auth && auth.get("role") === "admin";

  if (!isSuperAdmin) {
    var staffLibId = auth.get("libraryOrgId");
    var targetLibId = String(org.get("organizationId") || "").trim();
    if (!isAdmin || staffLibId !== targetLibId) {
      return e.json(403, { success: false, message: "You do not have permission to manage settings for this library." });
    }
  }

  try {
    var record = app.findFirstRecordByFilter("ui_settings", "scope = 'library' && libraryOrganization = {:org}", { org: org.id });
    record.set("logo", null);
    record.set("logoAlt", "");
    app.save(record);
  } catch (err) { }

  return e.json(200, { success: true, message: "Branding reset to system defaults." });
}

module.exports = {
  getLibraryOverridesSummary: getLibraryOverridesSummary,
  getLibrarySettings: getLibrarySettings,
  formatClaimRulesForLibrary: formatClaimRulesForLibrary,
  normalizeRelationId: normalizeRelationId,
  formatClaimStaffOptions: formatClaimStaffOptions,
  workflowWithEnabled: workflowWithEnabled,
  organizationSyncStatus: organizationSyncStatus,
  hasLibraryOverride: hasLibraryOverride,
  updateLibrarySettings: updateLibrarySettings,
  recordForScope: recordForScope,
  saveSystemSettingsPayload: saveSystemSettingsPayload,
  saveLibraryScopedSettings: saveLibraryScopedSettings,
  saveFormatClaimRules: saveFormatClaimRules,
  savePatronLibrarySettings: savePatronLibrarySettings,
  saveSmtpSettings: saveSmtpSettings,
  saveEnabledLibraries: saveEnabledLibraries,
  saveWorkflowSettings: saveWorkflowSettings,
  validatePublicationOptionsDeletion: validatePublicationOptionsDeletion,
  saveUiSettings: saveUiSettings,
  optionsToLines: optionsToLines,
  optionsToJson: optionsToJson,
  scopedLookupRecord: scopedLookupRecord,
  validateMaterialFormatsDeletion: validateMaterialFormatsDeletion,
  saveMaterialFormats: saveMaterialFormats,
  setFormatFieldRule: setFormatFieldRule,
  saveEmailSettings: saveEmailSettings,
  findEmailTemplateRecord: findEmailTemplateRecord,
  emailTemplateRecord: emailTemplateRecord,
  saveRejectionTemplates: saveRejectionTemplates,
  assertRejectionTemplateNotUsedByAutoReject: assertRejectionTemplateNotUsedByAutoReject,
  resetLibrarySettings: resetLibrarySettings,
  staffSaveLogo: staffSaveLogo,
  staffResetLogo: staffResetLogo,
  TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE: TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE,
  TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE: TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE,
  EMAIL_TEMPLATE_KEYS: EMAIL_TEMPLATE_KEYS
};
