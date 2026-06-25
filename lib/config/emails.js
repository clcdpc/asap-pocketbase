const defaults = require("./defaults.js");
const smtp = require("./smtp.js");
const dbHelpers = require("./db_helpers.js");

const defaultEmailTemplates = defaults.defaultEmailTemplates;
const EMAIL_TEMPLATE_FIELDS = defaults.EMAIL_TEMPLATE_FIELDS;
const getSmtpSettings = smtp.getSmtpSettings;
const smtpFromRecord = smtp.smtpFromRecord;
const orgIdForSettings = dbHelpers.orgIdForSettings;

function rejectionTemplates(app, orgId) {
  app = app || $app;
  var rows = [];
  var libraryCustomRows = [];
  var systemRows = [];
  var hiddenSystemIds = {};
  var libraryOverridesBySourceId = {};

  function templateJson(row, inherited) {
    return {
      id: row.id,
      sourceTemplateId: row.get("sourceTemplateId") || "",
      scope: row.get("scope") || "",
      inherited: !!inherited,
      name: row.get("name"),
      subject: row.get("subject"),
      body: row.get("body")
    };
  }

  function readSystem() {
    try {
      systemRows = app.findRecordsByFilter("rejection_templates", "scope = 'system' && enabled = true", "sortOrder", 200, 0, {});
    } catch (err) {
      if (typeof $app !== "undefined" && $app && $app.logger) {
        $app.logger().warn("Swallowed error", "error", String(err));
      }
    }
  }

  function readLibrary(orgRecordId) {
    try {
      var libraryRows = app.findRecordsByFilter("rejection_templates", "scope = 'library' && libraryOrganization = {:org}", "sortOrder", 200, 0, { org: orgRecordId });
      libraryRows.forEach(function (row) {
        var sourceTemplateId = String(row.get("sourceTemplateId") || "").trim();
        if (sourceTemplateId) {
          if (row.getBool("enabled") === false) {
            hiddenSystemIds[sourceTemplateId] = true;
          } else {
            libraryOverridesBySourceId[sourceTemplateId] = row;
          }
          return;
        }
        if (row.getBool("enabled") !== false) libraryCustomRows.push(row);
      });
    } catch (err) {
      if (typeof $app !== "undefined" && $app && $app.logger) {
        $app.logger().warn("Swallowed error", "error", String(err));
      }
    }
  }

  readSystem();
  var orgRecordId = orgIdForSettings(app, orgId);
  if (orgRecordId) readLibrary(orgRecordId);

  systemRows.forEach(function (row) {
    if (hiddenSystemIds[row.id]) return;
    if (libraryOverridesBySourceId[row.id]) {
      rows.push(templateJson(libraryOverridesBySourceId[row.id], false));
    } else {
      rows.push(templateJson(row, !!orgRecordId));
    }
  });

  libraryCustomRows.forEach(function (row) {
    rows.push(templateJson(row, false));
  });

  return rows;
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
    } catch (err) {
      if (typeof $app !== "undefined" && $app && $app.logger) {
        $app.logger().warn("Swallowed error", "error", String(err));
      }
    }
  }
  applyRows("system", "");
  var orgRecordId = orgIdForSettings(app, orgId);
  if (orgRecordId) applyRows("library", orgRecordId);
  merged.rejection_templates = rejectionTemplates(app, orgId);
  return merged;
}

function emails() {
  return emailsFor($app, "");
}

function emailStatus(app, orgId) {
  var smtpSettings = smtpFromRecord(getSmtpSettings(app || $app));
  var e = emailsFor(app || $app, orgId);
  var enabled = !!String(smtpSettings.host || "").trim() && !!String(e.fromAddress || "").trim();
  return {
    enabled: enabled,
    hasSmtp: !!String(smtpSettings.host || "").trim(),
    hasSender: !!String(e.fromAddress || "").trim(),
    message: enabled ? "Email notifications are configured." : "Email notifications are not configured. Suggestions and staff workflows still work, but patron emails will not be sent."
  };
}

module.exports = {
  rejectionTemplates: rejectionTemplates,
  emailsFor: emailsFor,
  emails: emails,
  emailStatus: emailStatus,
};
