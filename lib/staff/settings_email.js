const TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE = "TEMPLATE_IN_USE_BY_AUTO_REJECT";
const TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE = "This template can’t be deleted because it’s currently used by the auto-reject email. Assign a different template or disable auto-reject before deleting.";
const settingsRecords = require(`${__hooks}/../lib/staff/settings_records.js`);
const recordForScope = settingsRecords.recordForScope;

const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const records = require(`${__hooks}/../lib/records.js`);
const config = require(`${__hooks}/../lib/config.js`);
const transactions = require(`${__hooks}/../lib/pb_transactions.js`);



function saveEmailSettings(app, scope, orgId, emails) {
  return transactions.runInTransaction(app, function (txApp) {
    EMAIL_TEMPLATE_KEYS.forEach(function (key) {
      var tpl = emails[key] || {};
      if (!tpl.subject && !tpl.body && scope === "library") {
        var existing = findEmailTemplateRecord(txApp, scope, orgId, key);
        if (existing) txApp.delete(existing);
        return;
      }
      var record = emailTemplateRecord(txApp, scope, orgId, key);
      record.set("templateKey", key);
      record.set("name", key);
      if (tpl.subject !== undefined) record.set("subject", tpl.subject);
      if (tpl.body !== undefined) record.set("body", tpl.body);
      if (emails.fromAddress !== undefined) record.set("fromAddress", emails.fromAddress);
      if (emails.fromName !== undefined) record.set("fromName", emails.fromName);
      record.set("enabled", true);
      txApp.save(record);
    });
    saveRejectionTemplatesInApp(txApp, scope, orgId, emails.rejection_templates || []);
  });
}

const EMAIL_TEMPLATE_KEYS = ["suggestion_submitted", "purchase_approved", "already_owned", "rejected", "hold_placed"];

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
  return transactions.runInTransaction(app, function (txApp) {
    return saveRejectionTemplatesInApp(txApp, scope, orgId, templates);
  });
}

function saveRejectionTemplatesInApp(app, scope, orgId, templates) {
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
  return transactions.runInTransaction(app, function (txApp) {
    return resetLibrarySettingsInApp(txApp, orgId);
  });
}

function resetLibrarySettingsInApp(app, orgId) {
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



module.exports = {
  TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE,
  TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE,
  saveEmailSettings,
  findEmailTemplateRecord,
  emailTemplateRecord,
  saveRejectionTemplates,
  assertRejectionTemplateNotUsedByAutoReject,
  resetLibrarySettings,
  resetLibrarySettingsInApp
};
