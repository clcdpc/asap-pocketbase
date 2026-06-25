const settingsRecords = require(`${__hooks}/../lib/staff/settings_records.js`);
const recordForScope = settingsRecords.recordForScope;

const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const config = require(`${__hooks}/../lib/config.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);

const settingsUi = require(`${__hooks}/../lib/staff/settings_ui.js`);
const settingsEmail = require(`${__hooks}/../lib/staff/settings_email.js`);
const transactions = require(`${__hooks}/../lib/pb_transactions.js`);



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
  if (Object.prototype.hasOwnProperty.call(payload, "leapPatronUrlPattern")) {
    systemSettingsData.leapPatronUrlPattern = payload.leapPatronUrlPattern;
    hasSystemSettingsData = true;
  }
  if (Object.prototype.hasOwnProperty.call(payload, "formatIconUrlPattern")) {
    systemSettingsData.formatIconUrlPattern = payload.formatIconUrlPattern;
    hasSystemSettingsData = true;
  }
  if (Object.prototype.hasOwnProperty.call(payload, "patronEmbedAllowedOrigins")) {
    systemSettingsData.patronEmbedAllowedOrigins = payload.patronEmbedAllowedOrigins;
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
  settingsUi.saveUiSettings(app, "system", "", payload.ui_text || {});
  settingsEmail.saveEmailSettings(app, "system", "", payload.emails || {});
  if (payload.workflow && payload.workflow.enabledLibraryOrgIds !== undefined) {
    saveEnabledLibraries(app, payload.workflow.enabledLibraryOrgIds);
  }
}

function saveLibraryScopedSettings(app, orgId, payload) {
  saveWorkflowSettings(app, "library", orgId, payload.workflow || {});
  settingsUi.saveUiSettings(app, "library", orgId, payload.ui_text || {});
  settingsEmail.saveEmailSettings(app, "library", orgId, payload.emails || {});
  if (Object.prototype.hasOwnProperty.call(payload, "formatClaimRules")) {
    saveFormatClaimRules(app, orgId, payload.formatClaimRules || [], payload._staffUser || null);
  }
}

function saveFormatClaimRules(app, orgId, rules, staff) {
  return transactions.runInTransaction(app, function (txApp) {
    return saveFormatClaimRulesInApp(txApp, orgId, rules, staff);
  });
}

function saveFormatClaimRulesInApp(app, orgId, rules, staff) {
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

  var staffCache = {};

  Object.keys(desired).forEach(function (format) {
    var staffUserId = desired[format];
    var row = existing[format] || null;
    if (!staffUserId) {
      if (row) app.delete(row);
      return;
    }

    var targetStaff = staffCache[staffUserId];
    if (!targetStaff) {
      targetStaff = app.findRecordById("staff_users", staffUserId);
      staffCache[staffUserId] = targetStaff;
    }

    if (!targetStaff || targetStaff.getBool("active") === false) {
      throw new Error("Automatic claimant for " + format + " is not an active staff user.");
    }
    if (String(targetStaff.get("libraryOrgId") || "").trim() !== orgId && String(targetStaff.get("role") || "") !== "super_admin") {
      throw new Error("Automatic claimant for " + format + " must belong to the selected library.");
    }

    var isNew = !row;
    var changed = false;

    if (!row) {
      row = new Record(app.findCollectionByNameOrId("format_claim_rules"));
      row.set("libraryOrgId", orgId);
      row.set("libraryOrganization", org.id);
      row.set("format", format);
      changed = true;
    }

    if (row.get("staffUser") !== staffUserId) {
      row.set("staffUser", staffUserId);
      changed = true;
    }
    if (row.get("staffUserId") !== staffUserId) {
      row.set("staffUserId", staffUserId);
      changed = true;
    }
    if (row.get("active") !== true) {
      row.set("active", true);
      changed = true;
    }

    if (changed || isNew) {
      if (staff && staff.id) {
        if (!row.id) row.set("createdBy", staff.id);
        row.set("updatedBy", staff.id);
      }
      app.save(row);
    }
  });
  Object.keys(existing).forEach(function (format) {
    if (!Object.prototype.hasOwnProperty.call(desired, format)) {
      app.delete(existing[format]);
    }
  });
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
  var all = app.findRecordsByFilter("polaris_organizations", "organizationCodeId = '2'", "", 1000, 0);

  transactions.runInTransaction(app, function (txApp) {
    var rels = [];

    for (var i = 0; i < all.length; i++) {
      var organizationId = String(all[i].get("organizationId"));
      var enabled = ids.length === 0 || ids.indexOf(organizationId) >= 0;

      all[i].set("enabledForPatrons", enabled);
      txApp.save(all[i]);

      if (enabled) rels.push(all[i].id);
    }

    sys.set("enabledLibraries", rels);
    txApp.save(sys);
  });
}

function saveWorkflowSettings(app, scope, orgId, wf) {
  var record = recordForScope(app, "workflow_settings", scope, orgId);
  ["suggestionLimit", "suggestionLimitMessage", "outstandingTimeoutEnabled", "outstandingTimeoutDays", "outstandingTimeoutSendEmail", "holdPickupTimeoutEnabled", "holdPickupTimeoutDays", "pendingHoldTimeoutEnabled", "pendingHoldTimeoutDays", "additionalCopyTimeoutEnabled", "additionalCopyTimeoutDays", "autoPromote", "commonAuthorsEnabled", "commonAuthorsList", "commonAuthorsMessage", "commonAuthorsLabel", "commonAuthorsHelp", "allowPatronAutoholdOptOut", "allowAnyRegisteredCardLogin", "externalSearch1Enabled", "externalSearch1Label", "externalSearch1UrlTemplate", "externalSearch2Enabled", "externalSearch2Label", "externalSearch2UrlTemplate", "externalSearch3Enabled", "externalSearch3Label", "externalSearch3UrlTemplate", "externalSearch4Enabled", "externalSearch4Label", "externalSearch4UrlTemplate"].forEach(function (key) {
    if (wf[key] !== undefined) record.set(key, wf[key]);
  });
  if (!wf.outstandingTimeoutEnabled || !wf.outstandingTimeoutSendEmail) {
    record.set("outstandingTimeoutRejectionTemplate", "");
  } else if (Object.prototype.hasOwnProperty.call(wf, "outstandingTimeoutRejectionTemplateId")) {
    record.set("outstandingTimeoutRejectionTemplate", wf.outstandingTimeoutRejectionTemplateId || "");
  }
  app.save(record);
}




function resetLibraryScopedSettings(app, orgId) {
  return transactions.runInTransaction(app, function (txApp) {
    settingsEmail.resetLibrarySettingsInApp(txApp, orgId);

    orgId = String(orgId || "").trim();
    if (orgId && orgId !== "system") {
      try {
        var rows = txApp.findRecordsByFilter("format_claim_rules", "libraryOrgId = {:libraryOrgId}", "", 500, 0, { libraryOrgId: orgId });
        rows.forEach(function (row) {
          txApp.delete(row);
        });
      } catch (err) {}
    }
  });
}

module.exports = {
  recordForScope,
  saveSystemSettingsPayload,
  saveLibraryScopedSettings,
  resetLibraryScopedSettings,
  saveFormatClaimRules,
  saveSmtpSettings,
  saveEnabledLibraries,
  saveWorkflowSettings
};
