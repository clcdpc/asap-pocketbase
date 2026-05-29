
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);


const config = require(`${__hooks}/../lib/config.js`);

const orgs = require(`${__hooks}/../lib/orgs.js`);

const claimsRoutes = require(`${__hooks}/../lib/staff/title_request_claims.js`);
const staffClaimDisplayName = claimsRoutes.staffClaimDisplayName;

const settingsSave = require(`${__hooks}/../lib/staff/settings_save.js`);


function staffEmailStatus(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var orgId = String(routeUtils.queryValue(e, "orgId") || "").trim();
  if (!orgId) {
    orgId = routeUtils.isSuperAdmin(staff) ? "system" : String(staff.get("libraryOrgId") || "").trim();
  }
  if (orgId !== "system" && orgId !== String(staff.get("libraryOrgId") || "").trim() && !routeUtils.isSuperAdmin(staff)) {
    return e.json(403, { message: "Access denied to this library email status." });
  }
  return e.json(200, config.emailStatus(e.app, orgId === "system" ? "" : orgId));
}








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
      var s = config.getSettings(e.app);
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
        formatIconUrlPattern: s.formatIconUrlPattern || "",
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
      formatIconUrlPattern: ls.formatIconUrlPattern || "",
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
        displayName: staffClaimDisplayName(row),
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
      settingsSave.saveSystemSettingsPayload(e.app, payload);
    } catch (err) {
      var systemErrorPayload = { message: err.message || String(err) };
      if (err.code) systemErrorPayload.code = err.code;
      return e.json(400, systemErrorPayload);
    }
  } else {
    try {
      if (action === "reset") {
        settingsSave.settingsEmail.resetLibrarySettings(e.app, orgId);
      } else {
        settingsSave.saveLibraryScopedSettings(e.app, orgId, payload);
      }
    } catch (err) {
      var errorPayload = { message: err.message || String(err) };
      if (err.code) errorPayload.code = err.code;
      return e.json(400, errorPayload);
    }
  }

  return e.json(200, { success: true });
}



module.exports = {
  staffEmailStatus,
  getLibraryOverridesSummary,
  getLibrarySettings,
  formatClaimRulesForLibrary,
  normalizeRelationId,
  formatClaimStaffOptions,
  workflowWithEnabled,
  organizationSyncStatus,
  hasLibraryOverride,
  updateLibrarySettings
};
