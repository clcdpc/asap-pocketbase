
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
const config = require(`${__hooks}/../lib/config.js`);
const settingsRecords = require(`${__hooks}/../lib/staff/settings_records.js`);
const recordForScope = settingsRecords.recordForScope;

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
  staffSaveLogo,
  staffResetLogo
};
