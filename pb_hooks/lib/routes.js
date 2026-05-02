const config = require(`${__hooks}/lib/config.js`);
const importer = require(`${__hooks}/lib/importer.js`);
const jobs = require(`${__hooks}/lib/jobs.js`);
const identity = require(`${__hooks}/lib/identity.js`);
const formatRules = require(`${__hooks}/lib/format_rules.js`);
const mail = require(`${__hooks}/lib/mail.js`);
const orgs = require(`${__hooks}/lib/orgs.js`);
const polaris = require(`${__hooks}/lib/polaris.js`);
const records = require(`${__hooks}/lib/records.js`);

const TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE = "This template can’t be deleted because it’s currently used by the auto-reject email. Assign a different template or disable auto-reject before deleting.";
const TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE = "TEMPLATE_IN_USE_BY_AUTO_REJECT";

function body(e) {
  return e.requestInfo().body || {};
}

function requestHeader(e, name) {
  var lower = String(name || "").toLowerCase();
  var info = {};
  try {
    info = e.requestInfo() || {};
  } catch (err) { }
  var headers = info.headers || {};
  if (headers) {
    if (typeof headers.get === "function") {
      return headers.get(name) || headers.get(lower) || "";
    }
    return headers[name] || headers[lower] || "";
  }
  try {
    if (e.request && e.request.header && typeof e.request.header.get === "function") {
      return e.request.header.get(name) || e.request.header.get(lower) || "";
    }
  } catch (err2) { }
  try {
    if (e.request && e.request.headers && typeof e.request.headers.get === "function") {
      return e.request.headers.get(name) || e.request.headers.get(lower) || "";
    }
  } catch (err3) { }
  return "";
}

function queryValue(e, name) {
  var info = {};
  try {
    info = e.requestInfo() || {};
  } catch (err) { }

  if (info.query) {
    if (typeof info.query.get === "function") {
      var fromGet = info.query.get(name);
      if (fromGet !== undefined && fromGet !== null) {
        return String(fromGet);
      }
    }
    if (info.query[name] !== undefined && info.query[name] !== null) {
      return String(info.query[name]);
    }
  }

  var urls = [];
  if (info.url) {
    urls.push(String(info.url));
  }
  try {
    if (e.request && e.request.url) {
      urls.push(String(e.request.url));
    }
  } catch (err) { }
  try {
    if (e.request && e.request.URL) {
      urls.push(String(e.request.URL));
    }
  } catch (err) { }

  for (var i = 0; i < urls.length; i++) {
    var value = queryValueFromUrl(urls[i], name);
    if (value !== "") {
      return value;
    }
  }

  return "";
}

function queryValueFromUrl(url, name) {
  var marker = "?";
  var queryIndex = url.indexOf(marker);
  if (queryIndex < 0) {
    return "";
  }
  var query = url.slice(queryIndex + 1).split("#")[0];
  var parts = query.split("&");
  for (var i = 0; i < parts.length; i++) {
    var pair = parts[i].split("=");
    if (decodeURIComponent(pair[0] || "") === name) {
      return decodeURIComponent((pair.slice(1).join("=") || "").replace(/\+/g, " "));
    }
  }
  return "";
}

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

function requireAuth(e, collectionName) {
  var auth = e.requestInfo().auth;
  if (!auth || !auth.collection || auth.collection().name !== collectionName) {
    throw new UnauthorizedError("Unauthorized");
  }
  return auth;
}

function requireAdminStaff(e) {
  var staff = requireAuth(e, "staff_users");
  if (!isAdminRole(staff)) {
    return null;
  }
  return staff;
}

function requireSuperAdminStaff(e) {
  var staff = requireAuth(e, "staff_users");
  if (!isSuperAdmin(staff)) {
    return null;
  }
  return staff;
}

function isSuperAdmin(staff) {
  return String(staff.get("role") || "").toLowerCase() === "super_admin";
}

function isAdminRole(staff) {
  var role = String(staff.get("role") || "").toLowerCase();
  return role === "admin" || role === "super_admin";
}

function sameLibrary(staff, libraryOrgId) {
  if (isSuperAdmin(staff)) {
    return true;
  }
  var staffLibraryOrgId = String(staff.get("libraryOrgId") || "").trim();
  libraryOrgId = String(libraryOrgId || "").trim();
  return !!staffLibraryOrgId && !!libraryOrgId && staffLibraryOrgId === libraryOrgId;
}

function canAccessTitleRequest(staff, record) {
  return sameLibrary(staff, record.get("libraryOrgId"));
}

function requireTitleRequestAccess(e, staff, record) {
  if (!canAccessTitleRequest(staff, record)) {
    return e.json(404, { message: "Suggestion not found." });
  }
  return null;
}


function isIsbnCapableFormat(format, uiText) {
  var rules = formatRules.normalizeFormatRules(uiText && uiText.formatRules);
  var key = String(format || "book").trim() || "book";
  var rule = rules[key] || rules.book || {};
  var fields = rule.fields || {};
  var identifier = fields.identifier || {};
  var mode = String(identifier.mode || "optional");
  return mode === "required" || mode === "optional";
}

function applyIsbnCheckStatusForCreate(data, uiText) {
  var identifier = String(data.identifier || data.isbn || "").trim();
  if (!identifier) {
    data.isbnCheckStatus = "skipped_no_isbn";
    return;
  }
  if (isIsbnCapableFormat(data.format, uiText)) {
    data.isbnCheckStatus = "pending";
    return;
  }
  data.isbnCheckStatus = "skipped_no_isbn";
}

function runImmediateSubmissionIdentifierLookup(e, record) {
  if (!record || !String(record.get("identifier") || "").trim()) {
    return record;
  }
  try {
    jobs.promoteRequestNow(e.app, polaris.adminStaffAuth(), record);
    return e.app.findRecordById("title_requests", record.id);
  } catch (err) {
    e.app.logger().error("Immediate submission identifier lookup failed", "recordId", record.id, "error", String(err));
    return record;
  }
}

function escapeHtml(value) {
  return String(value === undefined || value === null ? "" : value)
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function formatDuplicateDate(value) {
  var text = String(value || "").trim();
  if (!text) {
    return "";
  }
  var date = new Date(text.replace(" ", "T"));
  if (isNaN(date.getTime())) {
    return text;
  }
  return date.toLocaleDateString("en-US", {
    year: "numeric",
    month: "long",
    day: "numeric"
  });
}

function duplicateStatusKey(duplicate) {
  duplicate = duplicate || {};
  var status = String(duplicate.status || "").trim();
  var closeReason = String(duplicate.closeReason || "").trim();
  if (status === records.STATUS.CLOSED && closeReason) {
    return closeReason;
  }
  return status || records.STATUS.SUGGESTION;
}

function formatLabelForDuplicate(format, uiText) {
  var labels = (uiText && uiText.formatLabels) || {};
  return labels[format] || String(format || "");
}

function appendQuery(url, params) {
  url = String(url || "").trim();
  if (!url) return "";
  var hash = "";
  var hashIndex = url.indexOf("#");
  if (hashIndex >= 0) {
    hash = url.slice(hashIndex);
    url = url.slice(0, hashIndex);
  }
  var separator = url.indexOf("?") >= 0 ? "&" : "?";
  var parts = [];
  Object.keys(params || {}).forEach(function (key) {
    var value = params[key];
    if (value !== undefined && value !== null && String(value) !== "") {
      parts.push(encodeURIComponent(key) + "=" + encodeURIComponent(String(value)));
    }
  });
  return parts.length ? url + separator + parts.join("&") + hash : url + hash;
}

function staffRequestUrl(app, record) {
  var base = config.staffUrl(app);
  var stage = records.normalizeStatus(record.get("status"));
  return appendQuery(base, { stage: stage, request: record.id });
}

function duplicateMatchLabel(matchType) {
  var labels = {
    identifier: "identifier number",
    title_format: "title and format",
    bibid: "catalog record"
  };
  return labels[matchType] || "suggestion";
}

function renderDuplicateMessage(uiText, duplicate) {
  uiText = uiText || {};
  duplicate = duplicate || {};
  var labels = config.defaultDuplicateStatusLabels ? config.defaultDuplicateStatusLabels() : {};
  labels = Object.assign({}, labels, uiText.duplicateStatusLabels || {});
  var statusKey = duplicateStatusKey(duplicate);
  var statusLabel = labels[statusKey] || labels[duplicate.status] || labels.closed || "Submitted";
  var template = uiText.alreadySubmittedMessage || "This suggestion has already been submitted from your account.";
  var data = {
    duplicate_date: formatDuplicateDate(duplicate.created),
    duplicate_status: statusLabel,
    duplicate_title: duplicate.title || "",
    duplicate_author: duplicate.author || "",
    duplicate_format: formatLabelForDuplicate(duplicate.format, uiText),
    duplicate_match_type: duplicateMatchLabel(duplicate.matchType)
  };
  return template.replace(/{{(\w+)}}/g, function (match, key) {
    return Object.prototype.hasOwnProperty.call(data, key) ? escapeHtml(data[key]) : match;
  });
}

function duplicateConflictResponse(e, err, uiText) {
  var duplicate = err.duplicate || null;
  var message = duplicate ? renderDuplicateMessage(uiText, duplicate) : (uiText.alreadySubmittedMessage || (err.message ? escapeHtml(err.message) : ""));
  return e.json(409, {
    message: err.message,
    conflictTitle: "Already Submitted",
    conflictMessage: message,
    duplicate: duplicate
  });
}

function noteSkippedEmail(app, record) {
  mail.noteSkipped(app, record);
}

function staffPublicJson(record) {
  return {
    id: record.id,
    username: record.get("username") || "",
    domain: record.get("domain") || "",
    identityKey: record.get("identityKey") || "",
    displayName: record.get("displayName") || "",
    role: record.get("role") || "staff",
    active: !!record.getBool("active"),
    branchOrgId: record.get("branchOrgId") || "",
    libraryOrgId: record.get("libraryOrgId") || "",
    libraryOrgName: record.get("libraryOrgName") || "",
    scope: record.get("scope") || "",
    lastLogin: record.getString("lastLogin") || "",
    lastPolarisLogin: record.getString("lastPolarisLogin") || "",
    weekly_action_summary_enabled: !!record.getBool("weekly_action_summary_enabled"),
    purchase_reminder_default: !!record.getBool("purchase_reminder_default"),
    weekly_action_summary_email: record.get("weekly_action_summary_email") || "",
  };
}

function staffProfileUpdate(e) {
  var staff = requireAuth(e, "staff_users");
  var payload = body(e);
  var summaryEmail = String(payload.weekly_action_summary_email || "").trim();
  staff.set("weekly_action_summary_enabled", boolValue(payload.weekly_action_summary_enabled, false));
  staff.set("purchase_reminder_default", boolValue(payload.purchase_reminder_default, false));
  staff.set("weekly_action_summary_email", summaryEmail);
  e.app.save(staff);
  return e.json(200, staffPublicJson(staff));
}

function polarisConfigured() {
  var p = config.polaris();
  return !!(p.host && p.accessId && p.apiKey);
}

function setupStatus(e) {
  var hasStaff = records.hasStaffUsers(e.app);
  return e.json(200, {
    setupRequired: !hasStaff,
    hasStaffUsers: hasStaff,
    polarisConfigured: polarisConfigured()
  });
}

function firstValue(source, names, defaultValue) {
  for (var i = 0; i < names.length; i++) {
    var value = source[names[i]];
    if (value !== undefined && value !== null) {
      return value;
    }
  }
  return defaultValue;
}

function boolValue(value, defaultValue) {
  if (value === undefined || value === null || value === "") {
    return defaultValue;
  }
  if (value === true || value === false) {
    return value;
  }
  var normalized = String(value).trim().toLowerCase();
  if (normalized === "true" || normalized === "1" || normalized === "on" || normalized === "yes") {
    return true;
  }
  if (normalized === "false" || normalized === "0" || normalized === "off" || normalized === "no") {
    return false;
  }
  return !!value;
}

function buildPolarisData(data) {
  var source = data && data.polaris ? data.polaris : (data || {});
  return {
    host: String(firstValue(source, ["host", "polarisHost"], "") || "").trim(),
    accessId: String(firstValue(source, ["accessId", "polarisAccessId"], "") || "").trim(),
    apiKey: String(firstValue(source, ["apiKey", "polarisApiKey"], "") || ""),
    staffDomain: String(firstValue(source, ["staffDomain", "polarisStaffDomain"], "") || "").trim(),
    adminUser: String(firstValue(source, ["adminUser", "polarisAdminUser"], "") || "").trim(),
    adminPassword: String(firstValue(source, ["adminPassword", "polarisAdminPassword"], "") || ""),
    overridePassword: String(firstValue(source, ["overridePassword", "polarisOverridePassword"], "") || ""),
    langId: String(firstValue(source, ["langId"], "1033") || "1033"),
    appId: String(firstValue(source, ["appId"], "100") || "100"),
    orgId: String(firstValue(source, ["orgId"], "1") || "1"),
    pickupOrgId: String(firstValue(source, ["pickupOrgId"], "0") || "0"),
    requestingOrgId: String(firstValue(source, ["requestingOrgId"], "3") || "3"),
    workstationId: String(firstValue(source, ["workstationId"], "1") || "1"),
    userId: String(firstValue(source, ["userId"], "1") || "1"),
    autoPromote: boolValue(firstValue(source, ["autoPromote"], true), true)
  };
}

function missingPolarisTestFields(polarisData) {
  var missing = [];
  if (!polarisData.host) missing.push("host");
  if (!polarisData.accessId) missing.push("access ID");
  if (!polarisData.apiKey) missing.push("API key");
  if (!polarisData.adminUser) missing.push("system staff username");
  if (!polarisData.adminPassword) missing.push("system staff password");
  return missing;
}

function testPolarisConnection(e, polarisData) {
  var missing = missingPolarisTestFields(polarisData);
  if (missing.length) {
    return e.json(400, {
      success: false,
      message: "Missing Polaris " + missing.join(", ") + "."
    });
  }

  try {
    var auth = polaris.adminStaffAuth(polarisData);
    if (auth && auth.AccessToken) {
      return e.json(200, { success: true, message: "Polaris API connection successful!" });
    }
    return e.json(400, { success: false, message: "Authentication failed without an explicit error." });
  } catch (err) {
    return e.json(400, { success: false, message: err.message || String(err) });
  }
}

function initialSetup(e) {
  if (records.hasStaffUsers(e.app)) {
    return e.json(409, { message: "Initial setup has already been completed." });
  }

  var data = body(e);
  var polarisData = buildPolarisData(data);
  var staffIdentity = identity.parseStaffIdentity(data.adminUsername || "", polarisData.staffDomain);
  if (!staffIdentity.username) {
    return e.json(400, { message: "Initial admin username is required." });
  }

  if (!polarisData.host || !polarisData.accessId || !polarisData.apiKey) {
    return e.json(400, { message: "Polaris host, access ID, and API key are required." });
  }

  config.savePolarisSettings(e.app, polarisData);

  var orgSync = { success: false, synced: 0, message: "Organization sync has not run." };
  try {
    orgSync = orgs.syncOrganizations(e.app, polaris.adminStaffAuth(polarisData));
    orgSync.success = true;
    orgSync.message = "Organization hierarchy synced.";
  } catch (syncErr) {
    orgSync = { success: false, synced: 0, message: syncErr.message || String(syncErr) };
    orgs.setSyncStatus(e.app, "error", "Polaris connected, but organizations could not be loaded.", orgSync.message);
  }

  var record = records.upsertStaffUser(e.app, staffIdentity, staffIdentity.display, {
    defaultRole: "super_admin",
    scope: "system",
    branchOrgId: "",
    libraryOrgId: "",
    libraryOrgName: "System",
    lastOrgSync: false
  });
  return e.json(200, {
    token: record.newAuthToken(),
    record: record,
    bootstrapAdmin: true,
    bootstrapMessage: "Initial setup is complete. Your account is the consortium super admin; future staff logins will be created with non-admin staff roles.",
    organizationSync: orgSync
  });
}

function setupTestPolaris(e) {
  if (records.hasStaffUsers(e.app)) {
    return e.json(404, { success: false, message: "The requested resource wasn't found." });
  }

  return testPolarisConnection(e, buildPolarisData(body(e)));
}

function staffUsersList(e) {
  var admin = requireAdminStaff(e);
  if (!admin) {
    return e.json(403, { message: "Admin access required" });
  }

  var users = records.listStaffUsers(e.app).filter(function (record) {
    return isSuperAdmin(admin) || sameLibrary(admin, record.get("libraryOrgId"));
  });

  return e.json(200, {
    canAssignSuperAdmin: isSuperAdmin(admin),
    users: users.map(staffPublicJson)
  });
}

function staffUserRoleUpdate(e) {
  var admin = requireAdminStaff(e);
  if (!admin) {
    return e.json(403, { message: "Admin access required" });
  }

  var id = String(e.request.pathValue("id") || "").trim();
  var payload = body(e);
  var nextRole = String(payload.role || "").trim().toLowerCase();
  if (!id) {
    return e.json(400, { message: "Staff user id is required." });
  }
  if (["staff", "admin", "super_admin"].indexOf(nextRole) < 0) {
    return e.json(400, { message: "Role must be staff, admin, or super admin." });
  }
  if (nextRole === "super_admin" && !isSuperAdmin(admin)) {
    return e.json(403, { message: "Only a super admin can assign the super admin role." });
  }

  var record;
  try {
    record = e.app.findRecordById("staff_users", id);
  } catch (err) {
    return e.json(404, { message: "Staff user not found." });
  }
  if (!isSuperAdmin(admin) && !sameLibrary(admin, record.get("libraryOrgId"))) {
    return e.json(404, { message: "Staff user not found." });
  }

  var currentRole = String(record.get("role") || "staff").toLowerCase();
  if (currentRole === "super_admin" && !isSuperAdmin(admin)) {
    return e.json(403, { message: "Only a super admin can modify a super admin's role." });
  }
  if (currentRole === "super_admin" && nextRole !== "super_admin" && records.countSuperAdminUsers(e.app) <= 1) {
    return e.json(400, { message: "At least one super admin user must remain." });
  }

  record.set("role", nextRole);
  e.app.save(record);

  return e.json(200, staffPublicJson(record));
}


function staffUserCreate(e) {
  var admin = requireAdminStaff(e);
  if (!admin) return e.json(403, { message: "Admin access required" });
  var payload = body(e);
  var parsed = identity.parseStaffIdentity(payload.username || payload.identity || "", config.polaris().staffDomain);
  if (!parsed.username) return e.json(400, { message: "Username or identity is required." });
  var libraryOrgId = String(payload.libraryOrgId || "").trim();
  var libraryOrgName = String(payload.libraryOrgName || "").trim();
  var role = String(payload.role || "staff").trim().toLowerCase();
  if (["staff", "admin", "super_admin"].indexOf(role) < 0) return e.json(400, { message: "Role must be staff, admin, or super admin." });
  if (role === "super_admin" && !isSuperAdmin(admin)) return e.json(403, { message: "Only a super admin can assign the super admin role." });
  if (!isSuperAdmin(admin) && !sameLibrary(admin, libraryOrgId)) return e.json(403, { message: "Library admins can only create staff in their own library." });
  try {
    var record = records.createStaffUser(e.app, parsed, payload.displayName || parsed.display, {
      role: role,
      scope: role === "super_admin" ? "system" : "library",
      libraryOrgId: role === "super_admin" ? "" : libraryOrgId,
      libraryOrgName: role === "super_admin" ? "System" : libraryOrgName,
      active: true
    });
    return e.json(201, staffPublicJson(record));
  } catch (err) {
    if (err && err.code === 409) {
      var message = isSuperAdmin(admin)
        ? "This user already exists. Existing accounts cannot be added again from this form. Use the existing user record to change roles or permissions."
        : "This user already exists and cannot be added again.";
      return e.json(409, { message: message });
    }
    e.app.logger().error("Staff user create failed", "identityKey", parsed.identityKey, "error", String(err));
    return e.json(400, { message: err.message || "Could not create staff user." });
  }
}

function staffUserDelete(e) {
  var admin = requireAdminStaff(e);
  if (!admin) return e.json(403, { message: "Admin access required" });
  var id = String(e.request.pathValue("id") || "").trim();
  if (!id) return e.json(400, { message: "Staff user id is required." });
  var record;
  try { record = e.app.findRecordById("staff_users", id); } catch (err) { return e.json(404, { message: "Staff user not found." }); }
  if (!isSuperAdmin(admin) && !sameLibrary(admin, record.get("libraryOrgId"))) return e.json(404, { message: "Staff user not found." });
  var currentRole = String(record.get("role") || "staff").toLowerCase();
  if (currentRole === "super_admin" && records.countSuperAdminUsers(e.app) <= 1) return e.json(400, { message: "At least one super admin user must remain." });
  e.app.delete(record);
  return e.json(200, { success: true });
}
function staffLogin(e) {
  var data = body(e);
  var staffIdentity = identity.parseStaffIdentity(data.username || "", config.polaris().staffDomain);
  var password = String(data.password || "");
  if (!staffIdentity.username || !password) {
    return e.json(400, { message: "Username and password are required" });
  }

  var hasAnyStaff = records.hasStaffUsers(e.app);
  if (!hasAnyStaff) {
    return e.json(409, {
      setupRequired: true,
      message: "Initial setup is required before staff login."
    });
  }

  var existing = records.findStaffByIdentity(e.app, staffIdentity.identityKey);
  if (!existing || !existing.getBool("active")) {
    throw new UnauthorizedError("Invalid credentials");
  }

  var override = config.polaris().overridePassword;
  var isOverride = override && $security.equal(password, override);

  var displayName = staffIdentity.display;
  var staffScope = null;
  var auth = null;
  if (!isOverride) {
    auth = polaris.staffAuth(staffIdentity.username, password, null, staffIdentity.authDomain || staffIdentity.domain);
    if (auth && auth.DisplayName) {
      displayName = auth.DisplayName;
    }
    var branchOrgId = String(auth.BranchID || "").trim();
    staffScope = orgs.resolveParentLibrary(e.app, branchOrgId, {
      staffAuth: auth,
      logger: e.app.logger()
    });
    if (!staffScope || !staffScope.libraryOrgId) {
      return e.json(403, { message: "Your staff account authenticated, but its Polaris branch could not be mapped to a parent library." });
    }
  } else if (existing) {
    staffScope = {
      branchOrgId: existing.get("branchOrgId") || "",
      libraryOrgId: existing.get("libraryOrgId") || "",
      libraryOrgName: existing.get("libraryOrgName") || "",
      scope: existing.get("scope") || "",
    };
  }

  var bootstrapsAdmin = !records.hasStaffUsers(e.app);
  var record = records.upsertStaffUser(e.app, staffIdentity, displayName, {
    defaultRole: bootstrapsAdmin ? "super_admin" : "staff",
    polarisUserId: auth ? auth.PolarisUserID : undefined,
    branchOrgId: staffScope ? staffScope.branchOrgId : undefined,
    libraryOrgId: staffScope ? staffScope.libraryOrgId : undefined,
    libraryOrgName: staffScope ? staffScope.libraryOrgName : undefined,
    scope: staffScope ? (staffScope.scope || "library") : undefined,
    lastOrgSync: !!staffScope,
    updateLastLogin: true
  });

  return e.json(200, {
    token: record.newAuthToken(),
    record: record,
    bootstrapAdmin: bootstrapsAdmin,
    bootstrapMessage: bootstrapsAdmin
      ? "This is the first staff login, so your account has been made the consortium super admin. Future staff logins will be created with non-admin staff roles."
      : ""
  });
}

function patronLogin(e) {
  try {
    var data = body(e);
    var barcode = String(data.username || data.barcode || "").trim();
    var password = String(data.password || data.pin || "");

    if (!barcode || !password) {
      return e.json(400, { message: "Barcode and PIN are required" });
    }

    var staffAuth = polaris.adminStaffAuth();
    var patron = polaris.authenticatePatron(barcode, password, staffAuth);
    patron = orgs.attachPatronScope(e.app, patron, staffAuth, e.app.logger());

    if (!patron.LibraryOrgID) {
      return e.json(403, { message: "Your library could not be determined from Polaris." });
    }

    var appSettings = config.getSettings();
    var enabledLibraries = String(appSettings.enabledLibraryOrgIds || "").trim();
    var librarySettings = config.librarySettings(e.app, patron.LibraryOrgID);

    if (enabledLibraries) {
      var enabledList = enabledLibraries.split(",").map(function (id) { return id.trim(); }).filter(function (id) { return id.length > 0; });
      if (enabledList.length > 0 && enabledList.indexOf(String(patron.LibraryOrgID)) < 0) {
        var msg = librarySettings.ui_text.systemNotEnabledMessage || "Your library does not currently participate in this suggestion service.";
        return e.json(403, { message: msg });
      }
    }

    var record = records.upsertPatronUser(e.app, patron);

    return e.json(200, {
      token: record.newAuthToken(),
      record: record,
      email: patron.EmailAddress || "",
      preferredPickupBranchId: patron.PreferredPickupBranchID || "",
      preferredPickupBranchName: patron.PreferredPickupBranchName || "",
      ui_text: librarySettings.ui_text
    });
  } catch (err) {
    e.app.logger().error("Patron login failed", "error", String(err));
    var status = 401;
    var message = "Incorrect Login - Please try again";

    var errStr = String(err);
    if (errStr.indexOf("Polaris configuration") >= 0 || errStr.indexOf("Admin staff authentication") >= 0) {
      status = 500;
      message = "The library suggestion system is currently misconfigured. Please contact staff.";
    } else {
      message = "Incorrect Login - Please try again";
    }

    return e.json(status, { message: message });
  }
}

function createSuggestion(e) {
  var patron = requireAuth(e, "patron_users");
  var uiText = config.uiText(e.app, patron.get("libraryOrgId"));
  try {
    if (!String(patron.get("libraryOrgId") || "").trim()) {
      return e.json(403, { message: "Your library could not be determined. Please log out and log back in before submitting a suggestion." });
    }
    var data = formatRules.sanitizePatronSuggestion(body(e), uiText);
    applyIsbnCheckStatusForCreate(data, uiText);
    var record = records.createSuggestion(e.app, patron, data);
    record = runImmediateSubmissionIdentifierLookup(e, record);

    // Trigger confirmation email
    try {
      if (!mail.suggestionSubmitted(e.app, record)) {
        noteSkippedEmail(e.app, record);
      }
    } catch (mailErr) {
      e.app.logger().error("Confirmation email failed", "recordId", record.id, "error", String(mailErr));
    }

    return e.json(201, {
      id: record.id,
      successTitle: uiText.successTitle,
      successMessage: uiText.successMessage
    });
  } catch (err) {
    if (err.code) {
      if (err.code === 409) {
        return duplicateConflictResponse(e, err, uiText);
      }
      return e.json(err.code, { message: err.message });
    }
    throw err;
  }
}

function staffCreateSuggestion(e) {
  var staff = requireAuth(e, "staff_users");
  var data = body(e);
  var barcode = String(data.barcode || "").trim();
  if (!barcode) {
    return e.json(400, { message: "Barcode is required" });
  }

  var patronData;
  var staffAuth;
  try {
    staffAuth = polaris.adminStaffAuth();
    patronData = polaris.lookupPatron(staffAuth, barcode);
    if (!patronData.PatronID) {
      throw new Error("Patron not found");
    }
    patronData = orgs.attachPatronScope(e.app, patronData, staffAuth, e.app.logger());
    if (!patronData.LibraryOrgID) {
      return e.json(400, { message: "Patron barcode was found, but its Polaris library could not be determined." });
    }
    if (!sameLibrary(staff, patronData.LibraryOrgID)) {
      return e.json(403, { message: "This patron belongs to a different library." });
    }
  } catch (err) {
    return e.json(400, { message: "Invalid patron barcode" });
  }

  var patronRecord = records.upsertPatronUser(e.app, patronData);

  try {
    data.staffLibraryOrgIdCreatedBy = staff.get("libraryOrgId") || "";
    applyIsbnCheckStatusForCreate(data, config.uiText());
    var record = records.createSuggestion(e.app, patronRecord, data, { skipLimits: true });

    var today = records.formatDate(new Date());
    var existing = String(record.get("notes") || "");
    record.set("notes", today + " Created on behalf of patron by " + staff.get("username") + ". " + existing);
    record.set("editedBy", staff.get("username"));
    record.set("updated", new Date().toISOString());
    e.app.save(record);
    record = runImmediateSubmissionIdentifierLookup(e, record);

    // Trigger confirmation email
    var emailPatronConfirmation = data.emailPatronConfirmation === true;
    if (emailPatronConfirmation) {
      try {
        if (record.get("email")) {
          var sent = mail.suggestionSubmitted(e.app, record);
          if (sent) {
            records.appendSystemNote(record, "Submission confirmation email sent to patron.");
          } else {
            records.appendSystemNote(record, "Submission confirmation email could not be sent.");
          }
        } else {
          records.appendSystemNote(record, "Submission confirmation email skipped because the patron has no email address.");
        }
        e.app.save(record);
      } catch (err) {
        records.appendSystemNote(record, "Submission confirmation email could not be sent.");
        e.app.save(record);
        e.app.logger().error("Staff-created suggestion confirmation email failed", "recordId", record.id, "error", String(err));
      }
    }

    return e.json(201, record);
  } catch (err) {
    if (err.code) {
      return e.json(err.code, { message: err.message });
    }
    throw err;
  }
}

function staffLookupPatron(e) {
  var staff = requireAuth(e, "staff_users");
  var data = body(e);
  var barcode = String(data.barcode || "").trim();
  if (!barcode) {
    return e.json(400, { message: "Barcode is required" });
  }

  try {
    var staffAuth = polaris.adminStaffAuth();
    var patronData = polaris.lookupPatron(staffAuth, barcode);
    if (!patronData.PatronID) {
      throw new Error("Patron not found");
    }
    patronData = orgs.attachPatronScope(e.app, patronData, staffAuth, e.app.logger());
    if (!patronData.LibraryOrgID) {
      return e.json(400, { message: "Patron barcode was found, but its Polaris library could not be determined." });
    }
    if (!sameLibrary(staff, patronData.LibraryOrgID)) {
      return e.json(403, { message: "This patron belongs to a different library." });
    }
    var patronRecord = records.upsertPatronUser(e.app, patronData);
    return e.json(200, {
      barcode: patronRecord.get("barcode"),
      nameFirst: patronRecord.get("nameFirst"),
      nameLast: patronRecord.get("nameLast"),
      email: patronRecord.email(),
      patronOrgId: patronRecord.get("patronOrgId") || "",
      libraryOrgId: patronRecord.get("libraryOrgId") || "",
      libraryOrgName: patronRecord.get("libraryOrgName") || "",
      preferredPickupBranchId: patronData.PreferredPickupBranchID || "",
      preferredPickupBranchName: patronData.PreferredPickupBranchName || "",
    });
  } catch (err) {
    return e.json(400, { message: "Invalid patron barcode" });
  }
}

function staffTitleRequestsList(e) {
  var staff = requireAuth(e, "staff_users");
  var result = [];
  var patronCache = {};
  var pickupBranchNameCache = {};
  var limit = 200;
  var offset = 0;
  var filter = "id != ''";
  var params = {};
  if (!isSuperAdmin(staff)) {
    var libraryOrgId = String(staff.get("libraryOrgId") || "").trim();
    if (!libraryOrgId) {
      return e.json(200, { items: [] });
    }
    filter = "libraryOrgId = {:libraryOrgId}";
    params.libraryOrgId = libraryOrgId;
  }

  while (true) {
    var page = e.app.findRecordsByFilter("title_requests", filter, "-created", limit, offset, params);
    if (!page.length) {
      break;
    }

    var missingPatronIds = [];
    var seenInPage = {};
    for (var i = 0; i < page.length; i++) {
      var pId = String(page[i].get("patron") || "").trim();
      if (pId && patronCache[pId] === undefined && !seenInPage[pId]) {
        missingPatronIds.push(pId);
        seenInPage[pId] = true;
      }
    }

    if (missingPatronIds.length > 0) {
      var batchSize = 100;
      for (var j = 0; j < missingPatronIds.length; j += batchSize) {
        var chunk = missingPatronIds.slice(j, j + batchSize);
        var filterParts = [];
        var batchParams = {};
        for (var k = 0; k < chunk.length; k++) {
          filterParts.push("id = {:p" + k + "}");
          batchParams["p" + k] = chunk[k];
        }
        var batchFilter = filterParts.join(" || ");
        var results = e.app.findRecordsByFilter("patron_users", batchFilter, "", chunk.length, 0, batchParams);
        var foundIds = {};
        for (var k = 0; k < results.length; k++) {
          var r = results[k];
          patronCache[r.id] = r;
          foundIds[r.id] = true;
        }
        for (var k = 0; k < chunk.length; k++) {
          var id = chunk[k];
          if (!foundIds[id]) {
            patronCache[id] = null;
          }
        }
      }
    }

    for (var i = 0; i < page.length; i++) {
      var row = records.titleRequestToJson(page[i], e.app);
      var patronId = String(page[i].get("patron") || "").trim();
      var patronRecord = null;

      if (patronId) {
        if (patronCache[patronId] !== undefined) {
          patronRecord = patronCache[patronId];
        } else {
          try {
            patronRecord = e.app.findRecordById("patron_users", patronId);
          } catch (err) {
            patronRecord = null;
          }
          patronCache[patronId] = patronRecord;
        }
      }

      var patronFirst = row.nameFirst || (patronRecord ? patronRecord.get("nameFirst") || "" : "");
      var patronLast = row.nameLast || (patronRecord ? patronRecord.get("nameLast") || "" : "");
      var patronName = (String(patronFirst).trim() + " " + String(patronLast).trim()).trim();
      var patronEmail = row.email || (patronRecord ? patronRecord.email() || "" : "");
      var libraryOrgName = row.libraryOrgName || (patronRecord ? patronRecord.get("libraryOrgName") || "" : "");

      row.patronName = patronName;
      row.patronEmail = patronEmail;
      row.libraryOrgName = libraryOrgName;
      row.preferredPickupBranchId = row.preferredPickupBranchId || (patronRecord ? patronRecord.get("preferredPickupBranchId") || "" : "");
      row.preferredPickupBranchName = row.preferredPickupBranchName || (patronRecord ? patronRecord.get("preferredPickupBranchName") || "" : "");
      if (!row.preferredPickupBranchId) {
        row.preferredPickupBranchId = row.patronOrgId || (patronRecord ? patronRecord.get("patronOrgId") || "" : "") || "0";
      }
      if (!row.preferredPickupBranchName) {
        if (pickupBranchNameCache[row.preferredPickupBranchId] === undefined) {
          pickupBranchNameCache[row.preferredPickupBranchId] = orgs.pickupBranchDisplayName(e.app, row.preferredPickupBranchId);
        }
        row.preferredPickupBranchName = pickupBranchNameCache[row.preferredPickupBranchId];
      }

      result.push(row);
    }
    if (page.length < limit) {
      break;
    }
    offset += limit;
  }

  return e.json(200, {
    items: result,
    scope: {
      libraryOrgId: staff.get("libraryOrgId") || "",
      libraryOrgName: staff.get("libraryOrgName") || "",
      superAdmin: isSuperAdmin(staff),
    }
  });
}

function staffEmailStatus(e) {
  var staff = requireAuth(e, "staff_users");
  var orgId = String(queryValue(e, "orgId") || "").trim();
  if (!orgId) {
    orgId = isSuperAdmin(staff) ? "system" : String(staff.get("libraryOrgId") || "").trim();
  }
  if (orgId !== "system" && orgId !== String(staff.get("libraryOrgId") || "").trim() && !isSuperAdmin(staff)) {
    return e.json(403, { message: "Access denied to this library email status." });
  }
  return e.json(200, config.emailStatus(e.app, orgId === "system" ? "" : orgId));
}

function staffTitleRequestAction(e) {
  try {
    var staff = requireAuth(e, "staff_users");
    var id = e.request.pathValue("id");
    var data = body(e);
    var action = String(data.action || "");
    var nextStatus = records.normalizeStatus(data.status);
    var isClosingRequest = nextStatus === records.STATUS.CLOSED;
    var isDuplicateClose = action === "closeDuplicate";
    var isActiveHoldTarget = nextStatus === records.STATUS.PENDING_HOLD || nextStatus === records.STATUS.HOLD_PLACED || action === "alreadyOwn";
    var oldStatus = "";
    var duplicateCloseNoteAdded = false;

    var record;
    try {
      record = e.app.findRecordById("title_requests", id);
    } catch (findErr) {
      return e.json(404, { message: "Suggestion not found: " + id });
    }
    var accessError = requireTitleRequestAccess(e, staff, record);
    if (accessError) {
      return accessError;
    }
    oldStatus = records.normalizeStatus(record.get("status"));
    var originalIdentifier = String(record.get("identifier") || "").trim();
    var nextIdentifier = data.identifier !== undefined && data.identifier !== null
      ? String(data.identifier).trim()
      : originalIdentifier;
    var shouldRunImmediatePromoter = !!nextIdentifier && nextIdentifier !== originalIdentifier;

    if (nextStatus === records.STATUS.PENDING_HOLD && !String(data.bibid || "").trim()) {
      return e.json(400, { message: "BIB ID is required before moving this suggestion to Pending hold." });
    }

    // Check for duplicate open requests for this patron with same BIB ID
    if (data.bibid) {
      var bibid = String(data.bibid).trim();
      var barcode = record.get("barcode");
      var staffAuth;
      try {
        staffAuth = polaris.adminStaffAuth();
      } catch (err) {
        e.app.logger().warn("Polaris auth failed", "error", String(err));
      }
      var existing = e.app.findRecordsByFilter("title_requests",
        "barcode = {:barcode} && bibid = {:bibid} && id != {:id} && status != 'closed'",
        "", 1, 0, { barcode: barcode, bibid: bibid, id: id });
      if (existing && existing.length > 0) {
        records.addWorkflowTagForRequest(e.app, record, "Hold exists (same patron)");
        var oldIsActiveHold = oldStatus === records.STATUS.PENDING_HOLD || oldStatus === records.STATUS.HOLD_PLACED;
        var bibidChanged = String(record.get("bibid") || "").trim() !== bibid;
        var wouldCreateActiveDuplicate = isActiveHoldTarget && (!oldIsActiveHold || bibidChanged || action === "alreadyOwn");
        if (isDuplicateClose) {
          data.status = records.STATUS.CLOSED;
          nextStatus = records.STATUS.CLOSED;
          data.closeReason = records.CLOSE_REASON.DUPLICATE_HOLD;
          records.appendSystemNote(record, "Closed as duplicate because this patron already has an open request or hold for the same BIB ID.");
          duplicateCloseNoteAdded = true;
        } else if (wouldCreateActiveDuplicate) {
          e.app.save(record);
          return e.json(400, { message: "Duplicate detected: This patron already has an open request for BIB ID " + bibid + "." });
        }
      }

      if (!isDuplicateClose && !isClosingRequest) {
        // Reconcile manual input with Polaris data
        polaris.reconcileRecord(e.app, staffAuth, record, bibid);

        // The reconcileRecord function updates the record with the title from Polaris
        // and prepends it to the original title in parentheses.
        // We must pass this updated title and author through the `data` variable
        // so it is correctly saved by `records.updateTitleRequest`.
        data.title = record.get("title");
        data.author = record.get("author");
      }

      // If moving to Pending hold, check Polaris for an existing hold.
      if (nextStatus === records.STATUS.PENDING_HOLD) {
        try {
          var pPatron = polaris.lookupPatron(staffAuth, barcode);
          if (pPatron && pPatron.PatronID) {
            var holdCheck = polaris.placeHold(staffAuth, bibid, pPatron.PatronID, true); // testMode = true
            if (holdCheck && holdCheck.statusValue === 29) {
              // Already has a hold in Polaris! 
              // Move directly to HOLD_PLACED instead of PENDING_HOLD.
              nextStatus = records.STATUS.HOLD_PLACED;
              data.status = nextStatus;
              records.appendSystemNote(record, "Patron already has a hold in Polaris for this BIB ID. Moving directly to Hold placed.");
            }
          }
        } catch (polarisErr) {
          e.app.logger().warn("Polaris duplicate hold check failed during staff action", "error", String(polarisErr));
        }
      }
    }

    if (nextStatus === records.STATUS.CLOSED && (action === "reject" || action === "silentClose")) {
      data.closeReason = (action === "silentClose") ? records.CLOSE_REASON.SILENT : records.CLOSE_REASON.REJECTED;
    }
    if (nextStatus === records.STATUS.CLOSED && isDuplicateClose) {
      data.closeReason = records.CLOSE_REASON.DUPLICATE_HOLD;
      records.addWorkflowTagForRequest(e.app, record, "Hold exists (same patron)");
      if (!duplicateCloseNoteAdded) {
        records.appendSystemNote(record, "Closed as duplicate because this patron already has an open request or hold for the same BIB ID.");
      }
    }
    if (nextStatus !== records.STATUS.CLOSED) {
      data.closeReason = "";
    }

    record = records.updateTitleRequest(e.app, id, data, staff.get("username"));

    if (shouldRunImmediatePromoter) {
      try {
        var updatedStatus = records.normalizeStatus(record.get("status"));
        if (updatedStatus === records.STATUS.SUGGESTION || config.polaris().autoPromote !== false) {
          jobs.promoteRequestNow(e.app, polaris.adminStaffAuth(), record);
          record = e.app.findRecordById("title_requests", record.id);
        }
      } catch (promoteErr) {
        e.app.logger().error("Immediate identifier promotion failed", "recordId", record.id, "error", String(promoteErr));
      }
    }

    var purchaseReminderEmail = {
      requested: action === "purchase" && data.emailPurchaseReminder === true,
      sent: false,
      message: ""
    };

    if (action === "alreadyOwn" || action === "reject") {
      var patron = null;
      try {
        patron = polaris.lookupPatron(polaris.adminStaffAuth(), record.get("barcode"));
      } catch (err) {
        e.app.logger().warn("Could not refresh patron data for staff action email", "recordId", record.id, "error", String(err));
      }

      if (action === "alreadyOwn") {
        var bibid = String(data.bibid || "").trim();
        if (bibid && patron && patron.PatronID) {
          var localStaffAuth;
          try {
            localStaffAuth = polaris.adminStaffAuth();
          } catch (e) { }
          polaris.reconcileRecord(e.app, localStaffAuth, record, bibid);
          try {
            polaris.placeHold(localStaffAuth, bibid, patron.PatronID, false); // testMode = false
            records.appendSystemNote(record, "Auto-placed hold for patron since item is already owned (BIB " + bibid + ")");
          } catch (holdErr) {
            e.app.logger().error("Auto-hold failed during alreadyOwn action", "recordId", record.id, "bibid", bibid, "error", String(holdErr));
          }
        }
        try {
          if (!mail.alreadyOwned(e.app, record, patron)) {
            noteSkippedEmail(e.app, record);
          }
        } catch (mailErr) {
          e.app.logger().error("Already-owned email failed", "recordId", record.id, "error", String(mailErr));
        }
      }
      if (action === "reject") {
        try {
          if (!mail.rejected(e.app, record, patron, data.rejectionTemplateId)) {
            noteSkippedEmail(e.app, record);
          }
        } catch (mailErr) {
          e.app.logger().error("Rejected suggestion email failed", "recordId", record.id, "error", String(mailErr));
        }
      }
    }

    if (purchaseReminderEmail.requested) {
      var staffEmail = String(staff.get("weekly_action_summary_email") || "").trim();
      if (!staffEmail) {
        purchaseReminderEmail.message = "Purchase saved. Add an email address to your staff profile to email yourself purchase reminders.";
      } else {
        try {
          purchaseReminderEmail.sent = !!mail.purchaseReminder(e.app, record, staff, staffEmail, staffRequestUrl(e.app, record));
          purchaseReminderEmail.message = purchaseReminderEmail.sent
            ? "Purchase saved and reminder email sent."
            : "Purchase saved, but email notifications are not configured.";
        } catch (mailErr) {
          e.app.logger().error("Purchase reminder email failed", "recordId", record.id, "staffUserId", staff.id, "error", String(mailErr));
          purchaseReminderEmail.message = "Purchase saved, but the reminder email could not be sent.";
        }
      }
    }

    var response = records.titleRequestToJson(record, e.app);
    response.purchaseReminderEmail = purchaseReminderEmail;
    return e.json(200, response);
  } catch (err) {
    e.app.logger().error("Staff action failed", "error", String(err));
    return e.json(400, { message: "System error: " + err.message });
  }
}

function staffDeleteClosedRequest(e) {
  var staff = requireAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Admin access required." });
  }

  var id = e.request.pathValue("id");
  var record;
  try {
    record = e.app.findRecordById("title_requests", id);
  } catch (err) {
    return e.json(404, { message: "Closed request not found." });
  }

  var accessError = requireTitleRequestAccess(e, staff, record);
  if (accessError) {
    return accessError;
  }

  if (records.normalizeStatus(record.get("status")) !== records.STATUS.CLOSED) {
    return e.json(400, { message: "Only closed requests can be deleted." });
  }

  try {
    records.deleteTitleRequestWithAudit(e.app, record, staff, "single");
    return e.json(200, { success: true });
  } catch (err2) {
    return e.json(400, { message: err2.message || "Could not delete closed request." });
  }
}

function staffDeleteClosedRequestsBulk(e) {
  var staff = requireAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Admin access required." });
  }

  var data = body(e);
  if (String(data.confirm || "") !== "DELETE") {
    return e.json(400, { message: "Type DELETE to confirm bulk deletion." });
  }

  try {
    var deleted = records.deleteClosedRequestsBulk(e.app, staff, data.confirm);
    return e.json(200, { success: true, deleted: deleted });
  } catch (err) {
    return e.json(400, { message: err.message || "Could not delete closed requests." });
  }
}

function runHoldCheck(e) {
  if (!requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  return e.json(200, jobs.runScheduledHoldCheck(e.app));
}

function runWeeklyStaffActionSummary(e) {
  var secret = "";
  try {
    secret = String($os.getenv("ASAP_CRON_SECRET") || "").trim();
  } catch (err) { }
  var authorized = false;
  var authHeader = String(requestHeader(e, "Authorization") || "").trim();
  if (secret && authHeader === "Bearer " + secret) {
    authorized = true;
  } else {
    var auth = e.requestInfo().auth;
    authorized = !!(auth && auth.isSuperuser && auth.isSuperuser());
    if (!authorized && auth && auth.collection && auth.collection().name === "staff_users") {
      authorized = isSuperAdmin(auth);
    }
  }
  if (!authorized) {
    return e.json(401, { message: "Unauthorized" });
  }
  try {
    return e.json(200, jobs.runWeeklyStaffActionSummary(e.app, { force: boolValue(body(e).force, false) }));
  } catch (err) {
    e.app.logger().error("Weekly staff action summary job failed", "error", String(err));
    return e.json(400, { message: err.message || String(err) });
  }
}

function importTitleRequests(e) {
  if (!hasImportAccess(e)) {
    throw new UnauthorizedError("Unauthorized");
  }

  var info = e.requestInfo();
  var rows = [];
  if (info.body && info.body.records) {
    rows = info.body.records;
  } else if (info.body && info.body.csv) {
    rows = importer.parseCsv(String(info.body.csv));
  } else {
    rows = importer.parseCsv(toString(e.request.body));
  }
  return e.json(200, importer.importRows(e.app, rows));
}

function hasImportAccess(e) {
  var auth = e.requestInfo().auth;
  if (auth && auth.isSuperuser && auth.isSuperuser()) {
    return true;
  }
  var token = e.request.header.get("X-ASAP-Import-Token");
  var expected = config.importToken();
  return expected && token && $security.equal(token, expected);
}

function staffTestPolaris(e) {
  if (!requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  var data = body(e);
  var polarisData = data && data.polaris ? buildPolarisData(data) : config.polaris();
  return testPolarisConnection(e, polarisData);
}

function staffTestSmtp(e) {
  var staff = requireSuperAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Super admin access required" });
  }
  try {
    const config = require(`${__hooks}/lib/config.js`);
    config.applyMailSettings(e.app);

    var mail = require(`${__hooks}/lib/mail.js`);
    var d = body(e);
    var email = String(d.email || "").trim() || staff.get("email");
    if (!email) {
      return e.json(400, { success: false, message: "No recipient email address specified (and your staff account has no email)." });
    }
    var subject = "Test SMTP Connection";
    var text = "This is a test email from Auto Suggest a Purchase to confirm SMTP settings are working.";
    var html = "<p>This is a test email from Auto Suggest a Purchase to confirm SMTP settings are working.</p>";
    var ok = mail.send(e.app, email, subject, text, html);

    if (ok) {
      return e.json(200, { success: true, message: "Test email sent to " + email + "!" });
    }
    return e.json(400, { success: false, message: "Mailer failed. Please check your from address and SMTP settings." });
  } catch (err) {
    return e.json(400, { success: false, message: err.message || String(err) });
  }
}

function staffSyncOrganizations(e) {
  if (!requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  try {
    var result = orgs.syncOrganizations(e.app, polaris.adminStaffAuth());
    return e.json(200, {
      success: true,
      synced: result.synced || 0,
      message: "Organization hierarchy synced."
    });
  } catch (err) {
    return e.json(400, { success: false, message: err.message || String(err) });
  }
}

function staffBibLookup(e) {
  var staff = requireAuth(e, "staff_users");
  var d = body(e);
  var bibId = String(d.bibId || "").trim();
  if (!bibId) {
    return e.json(400, { message: "BIB ID is required" });
  }

  try {
    var staffAuth = polaris.adminStaffAuth();
    var info = polaris.getBib(staffAuth, bibId);

    var barcode = String(d.barcode || "").trim();
    if (barcode && info) {
      try {
        var patron = polaris.lookupPatron(staffAuth, barcode);
        if (patron && patron.PatronID) {
          patron = orgs.attachPatronScope(e.app, patron, staffAuth, e.app.logger());
          if (!sameLibrary(staff, patron.LibraryOrgID)) {
            return e.json(403, { message: "This patron belongs to a different library." });
          }
          // Check if patron already has a hold on this BIB ID
          var holdCheck = polaris.placeHold(staffAuth, bibId, patron.PatronID, true); // testMode = true
          info.patronHoldCheck = holdCheck;
        }
      } catch (patronErr) {
        // Log but don't fail the whole bib lookup
        e.app.logger().warn("Patron hold check failed during bib lookup", "barcode", barcode, "error", String(patronErr));
      }
    }

    return e.json(200, info);
  } catch (err) {
    return e.json(400, { message: err.message || String(err) });
  }
}



function getLibrarySettings(e) {
  try {
    var staff = requireAuth(e, "staff_users");
    var orgId = String(queryValue(e, "orgId") || "").trim();

    if (!orgId) {
      orgId = String(staff.get("libraryOrgId") || "").trim();
    }

    if (orgId !== "system" && orgId !== String(staff.get("libraryOrgId") || "").trim() && !isSuperAdmin(staff)) {
      return e.json(403, { message: "Access denied to these library settings." });
    }

    if (orgId === "system") {
      if (!isSuperAdmin(staff)) {
        return e.json(403, { message: "Only super admins can view system settings." });
      }
      var s = config.getSettings();
      var wf = config.suggestionLimit(e.app, "");
      return e.json(200, {
        orgId: orgId,
        emails: s.emails,
        ui_text: s.ui_text,
        workflow: workflowWithEnabled(e.app, wf),
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
    ["audience_groups", "scope = 'library' && libraryOrganization = {:org}"],
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
  var staff = requireAuth(e, "staff_users");
  var payload = body(e);
  var orgId = String(payload.orgId || "").trim();
  var action = String(payload.action || "save").toLowerCase();

  if (!orgId) {
    return e.json(400, { message: "orgId is required." });
  }

  if (orgId !== "system" && orgId !== String(staff.get("libraryOrgId") || "").trim() && !isSuperAdmin(staff)) {
    return e.json(403, { message: "Access denied to these library settings." });
  }

  if (orgId === "system") {
    if (!isSuperAdmin(staff)) {
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
    var polarisData = buildPolarisData({ polaris: payload.polaris });
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
  if (ui.ageGroups !== undefined) record.set("ageGroups", ui.ageGroups);
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
  ["suggestionLimit", "suggestionLimitMessage", "outstandingTimeoutEnabled", "outstandingTimeoutDays", "outstandingTimeoutSendEmail", "holdPickupTimeoutEnabled", "holdPickupTimeoutDays", "pendingHoldTimeoutEnabled", "pendingHoldTimeoutDays", "commonAuthorsEnabled", "commonAuthorsList", "commonAuthorsMessage"].forEach(function (key) {
    if (wf[key] !== undefined) record.set(key, wf[key]);
  });
  if (!wf.outstandingTimeoutEnabled || !wf.outstandingTimeoutSendEmail) {
    record.set("outstandingTimeoutRejectionTemplate", "");
  } else if (Object.prototype.hasOwnProperty.call(wf, "outstandingTimeoutRejectionTemplateId")) {
    record.set("outstandingTimeoutRejectionTemplate", wf.outstandingTimeoutRejectionTemplateId || "");
  }
  app.save(record);
}

function validateAudienceGroupsDeletion(app, scope, orgId, ui) {
  if (ui.ageGroups === undefined) return;
  var labels = Array.isArray(ui.ageGroups) ? ui.ageGroups : String(ui.ageGroups || "").split(/\r?\n/);
  labels = labels.map(function (label) {
    return String(label && typeof label === "object" ? label.label || "" : label || "").trim();
  }).filter(Boolean);

  var keep = {};
  labels.forEach(function (label, index) {
    var code = codeFromLabel(label, "group_" + (index + 1));
    keep[code] = true;
  });

  var org = scope === "library" ? config.findOrganization(app, orgId) : null;
  var filter = scope === "system" ? "scope = 'system'" : "scope = 'library' && libraryOrganization = {:org}";
  var params = scope === "system" ? {} : { org: org ? org.id : "" };

  try {
    var rows = app.findRecordsByFilter("audience_groups", filter, "", 200, 0, params);
    var toCheck = [];
    for (var i = 0; i < rows.length; i++) {
      var row = rows[i];
      if (!keep[String(row.get("code") || "")]) {
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
          filterParts.push("audienceGroup = {:p" + k + "}");
          checkParams["p" + k] = chunk[k].id;
        }
        var batchFilter = filterParts.join(" || ");
        try {
          var usedRequest = app.findFirstRecordByFilter("title_requests", batchFilter, checkParams);
          if (usedRequest) {
            var usedGroupId = usedRequest.get("audienceGroup");
            var usedLabel = "";
            for (var k = 0; k < chunk.length; k++) {
              if (chunk[k].id === usedGroupId) {
                usedLabel = chunk[k].get("label");
                break;
              }
            }
            var err = new Error("Age group '" + usedLabel + "' is currently in use by existing requests and cannot be deleted. You can disable it instead.");
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

  var record = recordForScope(app, "ui_settings", "system", "");
  var oldOptionsRaw = record.get("publicationOptions");
  var oldOptions = [];
  if (typeof oldOptionsRaw === "string" && oldOptionsRaw.trim().charAt(0) === "[") {
    try { oldOptions = JSON.parse(oldOptionsRaw); } catch (e) { }
  } else {
    oldOptions = String(oldOptionsRaw || "").split(/\r?\n/).map(function (opt) {
      return { label: opt.trim() };
    });
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
  if (scope === "system") {
    validateAudienceGroupsDeletion(app, scope, orgId, ui);
    validatePublicationOptionsDeletion(app, scope, orgId, ui);
  }
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
  if (scope === "system" && ui.ageGroups !== undefined) record.set("ageGroups", optionsToLines(ui.ageGroups));
  app.save(record);
  if (scope === "system") {
    saveMaterialFormats(app, scope, orgId, ui);
    saveAudienceGroups(app, scope, orgId, ui);
  }
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
    try {
      if (scope === "system") {
        return app.findFirstRecordByData(collectionName, "code", code);
      }
    } catch (err2) { }
    var record = new Record(collection);
    record.set("scope", scope);
    if (org) record.set("libraryOrganization", org.id);
    record.set("code", code);
    return record;
  }
}

function saveMaterialFormats(app, scope, orgId, ui) {
  var labels = ui.formatLabels || {};
  var available = Array.isArray(ui.availableFormats) ? ui.availableFormats : [];
  var rules = ui.formatRules || {};
  Object.keys(labels).forEach(function (code, index) {
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
    setFormatFieldRule(record, "audience", fields.agegroup, "Age Group");
    setFormatFieldRule(record, "publication", fields.publication, "Publication Timing");
    app.save(record);
  });
}

function codeFromLabel(label, fallback) {
  var text = String(label || "").trim();
  var known = {
    adult: "adult",
    "young adult / teen": "teen",
    "young adult": "teen",
    teen: "teen",
    children: "children",
    child: "children",
    kids: "children"
  };
  var lower = text.toLowerCase();
  if (known[lower]) return known[lower];
  var code = lower.replace(/[^a-z0-9]+/g, "_").replace(/^_+|_+$/g, "");
  return code || fallback;
}

function saveAudienceGroups(app, scope, orgId, ui) {
  if (ui.ageGroups === undefined) return;
  var labels = Array.isArray(ui.ageGroups) ? ui.ageGroups : String(ui.ageGroups || "").split(/\r?\n/);
  labels = labels.map(function (label) {
    return String(label && typeof label === "object" ? label.label || "" : label || "").trim();
  }).filter(Boolean);
  var org = scope === "library" ? config.findOrganization(app, orgId) : null;
  if (scope === "library" && !org) {
    throw new Error("Library organization must be synced before saving library-specific settings.");
  }
  var keep = {};
  labels.forEach(function (label, index) {
    var code = codeFromLabel(label, "group_" + (index + 1));
    keep[code] = true;
    var record = scopedLookupRecord(app, "audience_groups", scope, orgId, code);
    record.set("label", label);
    record.set("sortOrder", (index + 1) * 10);
    app.save(record);
  });
  var filter = scope === "system" ? "scope = 'system'" : "scope = 'library' && libraryOrganization = {:org}";
  var params = scope === "system" ? {} : { org: org.id };
  try {
    var rows = app.findRecordsByFilter("audience_groups", filter, "", 200, 0, params);
    rows.forEach(function (row) {
      if (!keep[String(row.get("code") || "")]) app.delete(row);
    });
  } catch (err) { }
}

function setFormatFieldRule(record, prefix, rule, fallback) {
  rule = rule || {};
  record.set(prefix + "Mode", rule.mode || (prefix === "identifier" ? "optional" : "required"));
  record.set(prefix + "Label", rule.label || fallback);
}

function saveEmailSettings(app, scope, orgId, emails) {
  EMAIL_TEMPLATE_KEYS.forEach(function (key) {
    var tpl = emails[key] || {};
    if (!tpl.subject && !tpl.body && scope === "library") return;
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

const EMAIL_TEMPLATE_KEYS = ["suggestion_submitted", "already_owned", "rejected", "hold_placed"];

function emailTemplateRecord(app, scope, orgId, key) {
  var org = scope === "library" ? config.findOrganization(app, orgId) : null;
  var filter = scope === "system" ? "scope = 'system' && templateKey = {:key}" : "scope = 'library' && libraryOrganization = {:org} && templateKey = {:key}";
  var params = scope === "system" ? { key: key } : { org: org.id, key: key };
  try {
    return app.findFirstRecordByFilter("email_templates", filter, params);
  } catch (err) {
    var rec = new Record(app.findCollectionByNameOrId("email_templates"));
    rec.set("scope", scope);
    if (org) rec.set("libraryOrganization", org.id);
    return rec;
  }
}

function saveRejectionTemplates(app, scope, orgId, templates) {
  var org = scope === "library" ? config.findOrganization(app, orgId) : null;
  var keep = {};
  for (var i = 0; i < templates.length; i++) {
    var t = templates[i] || {};
    if (t.id) keep[String(t.id)] = true;
    var record = null;
    if (t.id) {
      try { record = app.findRecordById("rejection_templates", t.id); } catch (err) { }
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
    rows.forEach(function (row) {
      if (keep[row.id]) return;
      assertRejectionTemplateNotUsedByAutoReject(app, row.id);
      app.delete(row);
    });
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
  ["workflow_settings", "ui_settings", "email_templates", "rejection_templates", "material_formats", "audience_groups"].forEach(function (collection) {
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
}

function staffRunPromoterCheck(e) {
  if (!requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  try {
    var polaris = require(`${__hooks}/lib/polaris.js`);
    var jobs = require(`${__hooks}/lib/jobs.js`);
    var auth = polaris.adminStaffAuth();
    var result = { promoted: 0 };
    jobs.processOutstandingPurchases(e.app, auth, result);
    return e.json(200, result);
  } catch (err) {
    return e.json(400, { message: err.message || String(err) });
  }
}

module.exports = {
  createSuggestion: createSuggestion,
  initialSetup: initialSetup,
  importTitleRequests: importTitleRequests,
  patronLogin: patronLogin,
  runHoldCheck: runHoldCheck,
  setupStatus: setupStatus,
  setupTestPolaris: setupTestPolaris,
  staffBibLookup: staffBibLookup,
  staffEmailStatus: staffEmailStatus,
  staffLogin: staffLogin,
  staffProfileUpdate: staffProfileUpdate,
  staffLookupPatron: staffLookupPatron,
  staffUsersList: staffUsersList,
  staffUserRoleUpdate: staffUserRoleUpdate,
  staffUserCreate: staffUserCreate,
  staffUserDelete: staffUserDelete,
  staffDeleteClosedRequest: staffDeleteClosedRequest,
  staffDeleteClosedRequestsBulk: staffDeleteClosedRequestsBulk,
  staffTitleRequestsList: staffTitleRequestsList,
  staffTitleRequestAction: staffTitleRequestAction,
  staffCreateSuggestion: staffCreateSuggestion,
  staffSyncOrganizations: staffSyncOrganizations,
  staffTestPolaris: staffTestPolaris,
  staffTestSmtp: staffTestSmtp,
  getLibrarySettings: getLibrarySettings,
  updateLibrarySettings: updateLibrarySettings,
  renderDuplicateMessage: renderDuplicateMessage,
  staffRunPromoterCheck: staffRunPromoterCheck,
  runWeeklyStaffActionSummary: runWeeklyStaffActionSummary,
  saveWorkflowSettings: saveWorkflowSettings,
  saveRejectionTemplates: saveRejectionTemplates,
  assertRejectionTemplateNotUsedByAutoReject: assertRejectionTemplateNotUsedByAutoReject,
  TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE: TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE,
  TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE: TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE,
};
