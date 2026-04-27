const config = require(`${__hooks}/lib/config.js`);
const importer = require(`${__hooks}/lib/importer.js`);
const jobs = require(`${__hooks}/lib/jobs.js`);
const identity = require(`${__hooks}/lib/identity.js`);
const formatRules = require(`${__hooks}/lib/format_rules.js`);
const mail = require(`${__hooks}/lib/mail.js`);
const orgs = require(`${__hooks}/lib/orgs.js`);
const polaris = require(`${__hooks}/lib/polaris.js`);
const records = require(`${__hooks}/lib/records.js`);

function body(e) {
  return e.requestInfo().body || {};
}

function queryValue(e, name) {
  var info = {};
  try {
    info = e.requestInfo() || {};
  } catch (err) {}

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
  } catch (err) {}
  try {
    if (e.request && e.request.URL) {
      urls.push(String(e.request.URL));
    }
  } catch (err) {}

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
    return e.json(404, { message: "Title request not found." });
  }
  return null;
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
    lastPolarisLogin: record.getString("lastPolarisLogin") || "",
  };
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

function getOrCreateSettingsRecord(app) {
  try {
    return app.findRecordById("app_settings", "settings0000001");
  } catch (err) {
    var collection = app.findCollectionByNameOrId("app_settings");
    var record = new Record(collection);
    record.set("id", "settings0000001");
    record.set("polaris", {});
    record.set("smtp", {
      host: "",
      port: 587,
      username: "",
      password: "",
      from: "",
      fromName: "Library Collection Development",
      tls: true
    });
    record.set("ui_text", {
      noEmailMessage: "No email is specified on your library account, which means we won't be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.",
      ebookMessage: "<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>",
      eaudiobookMessage: "<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href=\"https://help.libbyapp.com/en-us/6260.htm\" target=\"_blank\" rel=\"noreferrer\">Learn how to suggest a purchase using Libby here.</a></p>",
      publicationOptions: ["Already published", "Coming soon", "Published a while back"]
    });
    record.set("emails", {});
    record.set("allowedStaffUsers", "");
    record.set("suggestionLimit", 5);
    record.set("suggestionLimitMessage", "You have submitted 5 suggestions this week. Please try again next week.");
    record.set("outstandingTimeoutEnabled", false);
    record.set("outstandingTimeoutDays", 30);
    return record;
  }
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
    autoPromote: boolValue(firstValue(source, ["autoPromote"], false), false)
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

  var settings = getOrCreateSettingsRecord(e.app);
  settings.set("polaris", polarisData);
  e.app.save(settings);

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
    bootstrapMessage: "Initial setup is complete. Your account is the consortium super admin; future staff logins will be created with non-admin staff roles."
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
  if (currentRole === "super_admin" && nextRole !== "super_admin" && records.countSuperAdminUsers(e.app) <= 1) {
    return e.json(400, { message: "At least one super admin user must remain." });
  }

  record.set("role", nextRole);
  e.app.save(record);

  return e.json(200, staffPublicJson(record));
}

function staffLogin(e) {
  var data = body(e);
  var staffIdentity = identity.parseStaffIdentity(data.username || "", config.polaris().staffDomain);
  var password = String(data.password || "");
  if (!staffIdentity.username || !password) {
    return e.json(400, { message: "Username and password are required" });
  }

  var allowed = config.allowedStaffUsers();
  if (allowed.length && allowed.indexOf(staffIdentity.identityKey) < 0) {
    throw new UnauthorizedError("Invalid credentials");
  }

  if (!records.hasStaffUsers(e.app)) {
    return e.json(409, {
      setupRequired: true,
      message: "Initial setup is required before staff login."
    });
  }

  var override = config.polaris().overridePassword;
  var isOverride = override && password === override;

  var displayName = staffIdentity.display;
  var existing = records.findStaffByIdentity(e.app, staffIdentity.identityKey);
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
    lastOrgSync: !!staffScope
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
  var record = records.upsertPatronUser(e.app, patron);
  // Let patron use system defaults if library specifically hasn't overridden
  var librarySettings = config.getLibrarySettings(e.app, patron.LibraryOrgID);

  return e.json(200, {
    token: record.newAuthToken(),
    record: record,
    email: patron.EmailAddress || "",
    preferredPickupBranchId: patron.PreferredPickupBranchID || "",
    preferredPickupBranchName: patron.PreferredPickupBranchName || "",
    ui_text: librarySettings.ui_text
  });
}

function createSuggestion(e) {
  var patron = requireAuth(e, "patron_users");
  try {
    if (!String(patron.get("libraryOrgId") || "").trim()) {
      return e.json(403, { message: "Your library could not be determined. Please log out and log back in before submitting a suggestion." });
    }
    var uiText = config.uiText();
    var data = formatRules.sanitizePatronSuggestion(body(e), uiText);
    var record = records.createSuggestion(e.app, patron, data);
    
    // Trigger confirmation email
    try {
      mail.suggestionSubmitted(e.app, record);
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
    var record = records.createSuggestion(e.app, patronRecord, data);
    
    var today = records.formatDate(new Date());
    var existing = String(record.get("notes") || "");
    record.set("notes", today + " Created on behalf of patron by " + staff.get("username") + ". " + existing);
    record.set("editedBy", staff.get("username"));
    record.set("updated", new Date().toISOString());
    e.app.save(record);

    // Trigger confirmation email
    try {
      mail.suggestionSubmitted(e.app, record);
    } catch (mailErr) {
      e.app.logger().error("Confirmation email failed (staff submission)", "recordId", record.id, "error", String(mailErr));
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
    for (var i = 0; i < page.length; i++) {
      result.push(records.titleRequestToJson(page[i]));
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

function staffTitleRequestAction(e) {
  try {
    var staff = requireAuth(e, "staff_users");
    var id = e.request.pathValue("id");
    var data = body(e);
    var action = String(data.action || "");
    var nextStatus = records.normalizeStatus(data.status);
    
    var record;
    try {
      record = e.app.findRecordById("title_requests", id);
    } catch (findErr) {
      return e.json(404, { message: "Title request not found: " + id });
    }
    var accessError = requireTitleRequestAccess(e, staff, record);
    if (accessError) {
      return accessError;
    }

    if (nextStatus === records.STATUS.PENDING_HOLD && !String(data.bibid || "").trim()) {
      return e.json(400, { message: "BIB ID is required before moving a suggestion to Pending Hold." });
    }
    
    // Check for duplicate open requests for this patron with same BIB ID
    if (data.bibid) {
      var bibid = String(data.bibid).trim();
      var barcode = record.get("barcode");
      var existing = e.app.findRecordsByFilter("title_requests", 
        "barcode = {:barcode} && bibid = {:bibid} && id != {:id} && status != 'closed'",
        "", 1, 0, { barcode: barcode, bibid: bibid, id: id });
      if (existing && existing.length > 0) {
         return e.json(400, { message: "Duplicate detected: This patron already has an open request for BIB ID " + bibid + "." });
      }

      // If moving to Pending Hold, check Polaris for an existing hold
      if (nextStatus === records.STATUS.PENDING_HOLD) {
        try {
          var staffAuth = polaris.adminStaffAuth();
          var pPatron = polaris.lookupPatron(staffAuth, barcode);
          if (pPatron && pPatron.PatronID) {
            var holdCheck = polaris.placeHold(staffAuth, bibid, pPatron.PatronID, true); // testMode = true
            if (holdCheck && holdCheck.statusValue === 29) {
              // Already has a hold in Polaris! 
              // We'll promote it straight to HOLD_PLACED instead of PENDING_HOLD
              nextStatus = records.STATUS.HOLD_PLACED;
              data.status = nextStatus;
              records.appendSystemNote(record, "Patron already has a hold in Polaris for this BIB ID. Moving straight to Hold Placed.");
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
    if (nextStatus !== records.STATUS.CLOSED) {
      data.closeReason = "";
    }
    
    record = records.updateTitleRequest(e.app, id, data, staff.get("username"));

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
          try {
            var staffAuth = polaris.adminStaffAuth();
            polaris.placeHold(staffAuth, bibid, patron.PatronID, false); // testMode = false
            records.appendSystemNote(record, "Auto-placed hold for patron since item is already owned (BIB " + bibid + ")");
          } catch (holdErr) {
            e.app.logger().error("Auto-hold failed during alreadyOwn action", "recordId", record.id, "bibid", bibid, "error", String(holdErr));
          }
        }
        try {
          mail.alreadyOwned(e.app, record, patron);
        } catch (mailErr) {
          e.app.logger().error("Already-owned email failed", "recordId", record.id, "error", String(mailErr));
        }
      }
      if (action === "reject") {
        try {
          mail.rejected(e.app, record, patron);
        } catch (mailErr) {
          e.app.logger().error("Rejected suggestion email failed", "recordId", record.id, "error", String(mailErr));
        }
      }
    }

    return e.json(200, records.titleRequestToJson(record));
  } catch (err) {
    e.app.logger().error("Staff action failed", "error", String(err));
    return e.json(400, { message: "System error: " + err.message });
  }
}

function runHoldCheck(e) {
  if (!requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  return e.json(200, jobs.runScheduledHoldCheck(e.app));
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

    var emails = {};
    var ui_text = {};
    var workflow = {};
    var isOverride = false;

    if (orgId === "system") {
      if (!isSuperAdmin(staff)) {
        return e.json(403, { message: "Only super admins can view system settings." });
      }
      var s = config.getSettings();
      emails = s.emails;
      ui_text = s.ui_text;
      workflow = {
        suggestionLimit: s.suggestionLimit,
        suggestionLimitMessage: s.suggestionLimitMessage,
        outstandingTimeoutEnabled: s.outstandingTimeoutEnabled,
        outstandingTimeoutDays: s.outstandingTimeoutDays,
        holdPickupTimeoutEnabled: s.holdPickupTimeoutEnabled,
        holdPickupTimeoutDays: s.holdPickupTimeoutDays
      };
    } else {
      try {
        var record = e.app.findFirstRecordByData("library_settings", "libraryOrgId", orgId);
        var dbEmails = parseRecordJsonObject(record, "emails", {});
        var dbUiText = parseRecordJsonObject(record, "ui_text", {});
        var dbWorkflow = parseRecordJsonObject(record, "workflow", {});
        isOverride = true;
        
        var ls = config.librarySettings(e.app, orgId);
        emails = ls.emails;
        ui_text = ls.ui_text;
        workflow = ls.workflow;
      } catch (err) {
        var ls = config.librarySettings(e.app, orgId);
        emails = ls.emails;
        ui_text = ls.ui_text;
        workflow = ls.workflow;
      }
    }

    return e.json(200, {
      orgId: orgId,
      emails: emails,
      ui_text: ui_text,
      workflow: workflow,
      isOverride: isOverride
    });
  } catch (err) {
    e.app.logger().error("Failed to load library settings", "error", String(err));
    return e.json(500, { message: err.message || String(err) });
  }
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
    var record = getOrCreateSettingsRecord(e.app);
    if (payload.emails) record.set("emails", config.normalizeEmailTemplates(parseJsonObject(payload.emails, {})));
    if (payload.ui_text) record.set("ui_text", parseJsonObject(payload.ui_text, {}));
    if (payload.workflow) {
      var wf = parseJsonObject(payload.workflow, {});
      if (wf.suggestionLimit !== undefined) record.set("suggestionLimit", wf.suggestionLimit);
      if (wf.suggestionLimitMessage !== undefined) record.set("suggestionLimitMessage", wf.suggestionLimitMessage);
      if (wf.outstandingTimeoutEnabled !== undefined) record.set("outstandingTimeoutEnabled", wf.outstandingTimeoutEnabled);
      if (wf.outstandingTimeoutDays !== undefined) record.set("outstandingTimeoutDays", wf.outstandingTimeoutDays);
      if (wf.holdPickupTimeoutEnabled !== undefined) record.set("holdPickupTimeoutEnabled", wf.holdPickupTimeoutEnabled);
      if (wf.holdPickupTimeoutDays !== undefined) record.set("holdPickupTimeoutDays", wf.holdPickupTimeoutDays);
    }
    e.app.save(record);
  } else {
    if (action === "reset") {
      try {
        var record = e.app.findFirstRecordByData("library_settings", "libraryOrgId", orgId);
        e.app.delete(record);
      } catch (err) {}
    } else {
      var record;
      try {
        record = e.app.findFirstRecordByData("library_settings", "libraryOrgId", orgId);
      } catch (err) {
        var collection = e.app.findCollectionByNameOrId("library_settings");
        record = new Record(collection);
        record.set("libraryOrgId", orgId);
      }
      if (payload.emails) record.set("emails", config.normalizeEmailTemplates(parseJsonObject(payload.emails, {})));
      if (payload.ui_text) record.set("ui_text", parseJsonObject(payload.ui_text, {}));
      if (payload.workflow) record.set("workflow", parseJsonObject(payload.workflow, {}));
      e.app.save(record);
    }
  }

  return e.json(200, { success: true });
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
  staffLogin: staffLogin,
  staffLookupPatron: staffLookupPatron,
  staffUsersList: staffUsersList,
  staffUserRoleUpdate: staffUserRoleUpdate,
  staffTitleRequestsList: staffTitleRequestsList,
  staffTitleRequestAction: staffTitleRequestAction,
  staffCreateSuggestion: staffCreateSuggestion,
  staffSyncOrganizations: staffSyncOrganizations,
  staffTestPolaris: staffTestPolaris,
  staffTestSmtp: staffTestSmtp,
  getLibrarySettings: getLibrarySettings,
  updateLibrarySettings: updateLibrarySettings,
  staffRunPromoterCheck: staffRunPromoterCheck,
};
