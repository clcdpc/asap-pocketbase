const config = require(`${__hooks}/../lib/config.js`);
const identity = require(`${__hooks}/../lib/identity.js`);
const jobs = require(`${__hooks}/../lib/jobs.js`);
const mail = require(`${__hooks}/../lib/mail.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const polaris = require(`${__hooks}/../lib/polaris.js`);
const records = require(`${__hooks}/../lib/records.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);

const TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE = "This template can’t be deleted because it’s currently used by the auto-reject email. Assign a different template or disable auto-reject before deleting.";
const TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE = "TEMPLATE_IN_USE_BY_AUTO_REJECT";

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
    default_mine_unclaimed_filter: !!record.getBool("default_mine_unclaimed_filter"),
    weekly_action_summary_email: record.get("weekly_action_summary_email") || "",
  };
}

function staffLogin(e) {
  var data = routeUtils.body(e);
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

function staffProfileUpdate(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var payload = routeUtils.body(e);
  var summaryEmail = String(payload.weekly_action_summary_email || "").trim();
  staff.set("weekly_action_summary_enabled", routeUtils.boolValue(payload.weekly_action_summary_enabled, false));
  staff.set("purchase_reminder_default", routeUtils.boolValue(payload.purchase_reminder_default, false));
  // Staff-user-only preference: this is not a system or library-scoped setting.
  staff.set("default_mine_unclaimed_filter", routeUtils.boolValue(payload.default_mine_unclaimed_filter, false));
  staff.set("weekly_action_summary_email", summaryEmail);
  e.app.save(staff);
  return e.json(200, staffPublicJson(staff));
}

function staffUsersList(e) {
  var admin = routeUtils.requireAdminStaff(e);
  if (!admin) {
    return e.json(403, { message: "Admin access required" });
  }

  var users = records.listStaffUsers(e.app).filter(function (record) {
    return routeUtils.isSuperAdmin(admin) || routeUtils.sameLibrary(admin, record.get("libraryOrgId"));
  });

  return e.json(200, {
    canAssignSuperAdmin: routeUtils.isSuperAdmin(admin),
    users: users.map(staffPublicJson)
  });
}

function staffUserRoleUpdate(e) {
  var admin = routeUtils.requireAdminStaff(e);
  if (!admin) {
    return e.json(403, { message: "Admin access required" });
  }

  var id = String(e.request.pathValue("id") || "").trim();
  var payload = routeUtils.body(e);
  var nextRole = String(payload.role || "").trim().toLowerCase();
  if (!id) {
    return e.json(400, { message: "Staff user id is required." });
  }
  if (["staff", "admin", "super_admin"].indexOf(nextRole) < 0) {
    return e.json(400, { message: "Role must be staff, admin, or super admin." });
  }
  if (nextRole === "super_admin" && !routeUtils.isSuperAdmin(admin)) {
    return e.json(403, { message: "Only a super admin can assign the super admin role." });
  }

  var record;
  try {
    record = e.app.findRecordById("staff_users", id);
  } catch (err) {
    return e.json(404, { message: "Staff user not found." });
  }
  if (!routeUtils.isSuperAdmin(admin) && !routeUtils.sameLibrary(admin, record.get("libraryOrgId"))) {
    return e.json(404, { message: "Staff user not found." });
  }

  var currentRole = String(record.get("role") || "staff").toLowerCase();
  if (currentRole === "super_admin" && !routeUtils.isSuperAdmin(admin)) {
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
  var admin = routeUtils.requireAdminStaff(e);
  if (!admin) return e.json(403, { message: "Admin access required" });

  var payload = routeUtils.body(e);
  var staffDomain = config.polaris().staffDomain;
  var parsed = identity.parseStaffIdentity(payload.username || payload.identity || "", staffDomain);
  if (!parsed.username || !parsed.identityKey) {
    return e.json(400, { message: "Username or identity is required." });
  }

  var isSuper = routeUtils.isSuperAdmin(admin);
  var libraryOrgId = isSuper ? String(payload.libraryOrgId || "").trim() : String(admin.get("libraryOrgId") || "").trim();
  var libraryOrgName = isSuper ? String(payload.libraryOrgName || "").trim() : String(admin.get("libraryOrgName") || "").trim();
  var role = String(payload.role || "staff").trim().toLowerCase();

  if (["staff", "admin", "super_admin"].indexOf(role) < 0) {
    return e.json(400, { message: "Role must be staff, admin, or super admin." });
  }
  if (role === "super_admin" && !isSuper) {
    return e.json(403, { message: "Only a super admin can assign the super admin role." });
  }

  // Find existing
  var existing = records.findStaffByIdentity(e.app, parsed.identityKey);
  if (existing) {
    var existingLibraryOrgId = String(existing.get("libraryOrgId") || "").trim();
    if (!isSuper && existingLibraryOrgId !== libraryOrgId) {
      return e.json(403, { message: "This user already exists in another library. You do not have permission to manage this identity." });
    }
  }

  try {
    var displayName = String(payload.displayName || "").trim() || (existing ? existing.get("displayName") : "") || parsed.display;
    var record = records.upsertStaffUser(e.app, parsed, displayName, {
      role: role,
      scope: role === "super_admin" ? "system" : "library",
      libraryOrgId: role === "super_admin" ? "" : libraryOrgId,
      libraryOrgName: role === "super_admin" ? "System" : libraryOrgName,
      active: true
    });

    if (!isSuper) {
      e.app.logger().info("Staff identity provisioned by library admin", 
        "admin", admin.get("username"),
        "library", libraryOrgName,
        "targetIdentity", parsed.identityKey,
        "targetRole", role
      );
    }

    return e.json(201, staffPublicJson(record));
  } catch (err) {
    e.app.logger().error("Staff provisioning failed", "identityKey", parsed.identityKey, "error", String(err));
    return e.json(400, { message: err.message || "Could not provision staff user." });
  }
}


function staffUserDelete(e) {
  var admin = routeUtils.requireAdminStaff(e);
  if (!admin) return e.json(403, { message: "Admin access required" });
  var id = String(e.request.pathValue("id") || "").trim();
  if (!id) return e.json(400, { message: "Staff user id is required." });
  var record;
  try { record = e.app.findRecordById("staff_users", id); } catch (err) { return e.json(404, { message: "Staff user not found." }); }
  if (!routeUtils.isSuperAdmin(admin) && !routeUtils.sameLibrary(admin, record.get("libraryOrgId"))) return e.json(404, { message: "Staff user not found." });
  var currentRole = String(record.get("role") || "staff").toLowerCase();
  if (currentRole === "super_admin" && records.countSuperAdminUsers(e.app) <= 1) return e.json(400, { message: "At least one super admin user must remain." });
  e.app.delete(record);
  return e.json(200, { success: true });
}

function staffCreateSuggestion(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var data = routeUtils.body(e);
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
    if (!routeUtils.sameLibrary(staff, patronData.LibraryOrgID)) {
      return e.json(403, { message: "This patron belongs to a different library." });
    }
  } catch (err) {
    return e.json(400, { message: "Invalid patron barcode" });
  }

  var patronRecord = records.upsertPatronUser(e.app, patronData);

  try {
    data.staffLibraryOrgIdCreatedBy = staff.get("libraryOrgId") || "";
    routeUtils.applyIsbnCheckStatusForCreate(data, config.uiText());
    var record = records.createSuggestion(e.app, patronRecord, data, { skipLimits: true });

    var today = records.formatDate(new Date());
    var existing = String(record.get("notes") || "");
    record.set("notes", today + " Created on behalf of patron by " + staff.get("username") + ". " + existing);
    record.set("editedBy", staff.get("username"));
    record.set("updated", new Date().toISOString());
    e.app.save(record);
    record = routeUtils.runImmediateSubmissionIdentifierLookup(e, record);

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

function looksLikePatronBarcode(value) {
  var text = String(value || "").trim();
  return /^\d{6,}$/.test(text);
}

function staffPatronLookupResponse(patronRecord, patronData) {
  return {
    status: "selected",
    barcode: patronRecord.get("barcode"),
    nameFirst: patronRecord.get("nameFirst"),
    nameLast: patronRecord.get("nameLast"),
    email: patronRecord.email(),
    patronOrgId: patronRecord.get("patronOrgId") || "",
    libraryOrgId: patronRecord.get("libraryOrgId") || "",
    libraryOrgName: patronRecord.get("libraryOrgName") || "",
    preferredPickupBranchId: patronData.PreferredPickupBranchID || "",
    preferredPickupBranchName: patronData.PreferredPickupBranchName || "",
  };
}

function resolveStaffPatronByBarcode(e, staff, staffAuth, barcode) {
  var patronData = polaris.lookupPatron(staffAuth, barcode);
  if (!patronData.PatronID) {
    throw new Error("Patron not found");
  }

  patronData = orgs.attachPatronScope(e.app, patronData, staffAuth, e.app.logger());
  if (!patronData.LibraryOrgID) {
    var missingScope = new Error("Patron barcode was found, but its Polaris library could not be determined.");
    missingScope.statusCode = 400;
    throw missingScope;
  }

  if (!routeUtils.sameLibrary(staff, patronData.LibraryOrgID)) {
    var wrongLibrary = new Error("This patron belongs to a different library.");
    wrongLibrary.statusCode = 403;
    throw wrongLibrary;
  }

  var patronRecord = records.upsertPatronUser(e.app, patronData);
  return staffPatronLookupResponse(patronRecord, patronData);
}

function filterPatronSearchResultsForStaffLibrary(e, staff, staffAuth, searchResults) {
  var results = [];
  var seen = {};

  for (var i = 0; i < searchResults.length && results.length < 10; i++) {
    var candidate = searchResults[i] || {};
    var barcode = String(candidate.barcode || "").trim();
    if (!barcode || seen[barcode]) continue;
    seen[barcode] = true;

    try {
      var patronData = polaris.lookupPatron(staffAuth, barcode);
      patronData = orgs.attachPatronScope(e.app, patronData, staffAuth, e.app.logger());
      if (!patronData.LibraryOrgID || !routeUtils.sameLibrary(staff, patronData.LibraryOrgID)) {
        continue;
      }

      results.push({
        status: "candidate",
        barcode: patronData.Barcode || barcode,
        nameFirst: patronData.NameFirst || candidate.nameFirst || "",
        nameLast: patronData.NameLast || candidate.nameLast || "",
        name: [patronData.NameFirst || candidate.nameFirst || "", patronData.NameLast || candidate.nameLast || ""].filter(Boolean).join(" ").trim() || candidate.name || "Patron",
        email: patronData.EmailAddress || "",
        patronOrgId: patronData.PatronOrgID || candidate.organizationId || "",
        libraryOrgId: patronData.LibraryOrgID || "",
        libraryOrgName: patronData.LibraryOrgName || candidate.libraryOrgName || "",
        preferredPickupBranchId: patronData.PreferredPickupBranchID || "",
        preferredPickupBranchName: patronData.PreferredPickupBranchName || ""
      });
    } catch (err) {
      // Skip unresolvable search rows.
    }
  }

  return results;
}

function staffLookupPatron(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var data = routeUtils.body(e);
  var raw = String(data.query || data.barcode || "").trim();
  if (!raw) {
    return e.json(400, { message: "Enter a patron barcode or name." });
  }

  var staffAuth = polaris.adminStaffAuth();

  if (looksLikePatronBarcode(raw)) {
    try {
      return e.json(200, resolveStaffPatronByBarcode(e, staff, staffAuth, raw));
    } catch (err) {
      return e.json(err.statusCode || 400, { message: err.message || "Invalid patron barcode" });
    }
  }

  var search = polaris.searchPatrons(staffAuth, { query: raw, limit: 10 });
  if (search.status === "error") {
    e.app.logger().warn("Staff patron name search failed", "query", raw, "error", search.error || "");
    return e.json(400, { message: "Patron search failed. Try barcode, or first name then last name." });
  }

  var results = filterPatronSearchResultsForStaffLibrary(e, staff, staffAuth, search.results || []);
  if (!results.length) {
    return e.json(404, {
      status: "not_found",
      message: "No patron found. Try barcode, or first name then last name.",
      results: []
    });
  }

  if (results.length === 1 && results[0].barcode) {
    try {
      return e.json(200, resolveStaffPatronByBarcode(e, staff, staffAuth, results[0].barcode));
    } catch (err) {
      return e.json(err.statusCode || 400, { message: err.message || "Could not verify selected patron." });
    }
  }

  return e.json(200, {
    status: "multiple",
    totalMatches: results.length,
    results: results
  });
}

function staffTitleRequestsList(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var selectedScope = String(routeUtils.queryValue(e, "scope") || routeUtils.queryValue(e, "orgId") || "").trim();
  var result = [];
  var patronCache = {};
  var pickupBranchNameCache = {};
  var workflowTagsCache = {};
  var limit = 200;
  var offset = 0;
  var scope = titleRequestListScope(e.app, staff, selectedScope);

  if (!scope.canList) {
    return e.json(200, { items: [] });
  }

  while (true) {
    var page = fetchTitleRequestPage(e.app, scope, limit, offset);
    if (!page.length) {
      break;
    }

    preloadPatronsForTitleRequests(e.app, page, patronCache);
    preloadWorkflowTagsForRequests(e.app, page, workflowTagsCache);

    for (var i = 0; i < page.length; i++) {
      result.push(buildStaffTitleRequestRow(e.app, page[i], patronCache, pickupBranchNameCache, workflowTagsCache));
    }
    if (page.length < limit) {
      break;
    }
    offset += limit;
  }

  return e.json(200, {
    items: result,
    scope: titleRequestListResponseScope(e.app, staff, scope),
    availableLibraries: routeUtils.isSuperAdmin(staff) ? analyticsLibraryOptions(e.app) : []
  });
}

function titleRequestListScope(app, staff, selectedOrgId) {
  var isSuper = routeUtils.isSuperAdmin(staff);
  var staffLibraryOrgId = String(staff.get("libraryOrgId") || "").trim();
  var cleanSelected = String(selectedOrgId || "").trim();

  if (isSuper && (cleanSelected === "all" || cleanSelected === "system" || !cleanSelected)) {
    return {
      canList: true,
      mode: "all",
      libraryOrgId: "",
      filter: "id != ''",
      params: {}
    };
  }

  var libraryOrgId = isSuper ? cleanSelected : staffLibraryOrgId;
  if (!libraryOrgId) {
    return {
      canList: false,
      mode: "library",
      libraryOrgId: "",
      filter: "",
      params: {}
    };
  }

  return {
    canList: true,
    mode: "library",
    libraryOrgId: libraryOrgId,
    filter: "libraryOrgId = {:libraryOrgId}",
    params: { libraryOrgId: libraryOrgId }
  };
}

function fetchTitleRequestPage(app, scope, limit, offset) {
  return app.findRecordsByFilter("title_requests", scope.filter, "-created", limit, offset, scope.params);
}

function preloadPatronsForTitleRequests(app, titleRequests, patronCache) {
  var missingPatronIds = collectMissingPatronIds(titleRequests, patronCache);
  if (!missingPatronIds.length) {
    return;
  }

  var batchSize = 100;
  for (var i = 0; i < missingPatronIds.length; i += batchSize) {
    cachePatronBatch(app, missingPatronIds.slice(i, i + batchSize), patronCache);
  }
}


function preloadWorkflowTagsForRequests(app, titleRequests, workflowTagsCache) {
  var requestIds = [];
  for (var i = 0; i < titleRequests.length; i++) {
    var id = titleRequests[i].id;
    if (id && workflowTagsCache[id] === undefined) {
      requestIds.push(id);
      workflowTagsCache[id] = []; // initialize
    }
  }

  if (!requestIds.length) {
    return;
  }

  var batchSize = 100;
  for (var b = 0; b < requestIds.length; b += batchSize) {
    var batchIds = requestIds.slice(b, b + batchSize);

    var filterParts = [];
    var batchParams = {};
    for (var j = 0; j < batchIds.length; j++) {
      var pKey = "r" + j;
      filterParts.push("titleRequest = {:" + pKey + "}");
      batchParams[pKey] = batchIds[j];
    }

    var joinRows = app.findRecordsByFilter("title_request_tags", filterParts.join(" || "), "", batchIds.length * 10, 0, batchParams);
    if (!joinRows || joinRows.length === 0) continue;

    var tagIds = [];
    var tagParams = {};
    var tagConditions = [];
    var joinMap = {}; // mapping tagId -> list of requestIds

    for (var k = 0; k < joinRows.length; k++) {
      var reqId = joinRows[k].get("titleRequest");
      var tId = joinRows[k].get("tag");
      if (reqId && tId) {
        if (!joinMap[tId]) {
          joinMap[tId] = [];
          var pTagKey = "t" + tagIds.length;
          tagConditions.push("id = {:" + pTagKey + "}");
          tagParams[pTagKey] = tId;
          tagIds.push(tId);
        }
        joinMap[tId].push(reqId);
      }
    }

    if (tagConditions.length > 0) {
      // Due to potential large number of tags, we batch them as well, 100 is limit
      // title_request_tags gives us max 10 tags per request, if 100 requests, up to 1000 tags.
      // Need to chunk tagIds to 100
      var tagBatchSize = 100;
      for (var tb = 0; tb < tagConditions.length; tb += tagBatchSize) {
         var tagBatchConditions = tagConditions.slice(tb, tb + tagBatchSize);
         var batchFilter = tagBatchConditions.join(" || ");
         var tagRecords = app.findRecordsByFilter("workflow_tags", batchFilter, "", tagBatchConditions.length, 0, tagParams);
         if (tagRecords) {
           for (var tr = 0; tr < tagRecords.length; tr++) {
             var tId = tagRecords[tr].id;
             var tCode = tagRecords[tr].get("code") || tagRecords[tr].get("label") || "";
             var linkedReqs = joinMap[tId] || [];
             for (var lr = 0; lr < linkedReqs.length; lr++) {
                workflowTagsCache[linkedReqs[lr]].push(tCode);
             }
           }
         }
      }
    }
  }
}

function collectMissingPatronIds(titleRequests, patronCache) {
  var missingPatronIds = [];
  var seenInPage = {};

  for (var i = 0; i < titleRequests.length; i++) {
    var patronId = titleRequestPatronId(titleRequests[i]);
    if (patronId && patronCache[patronId] === undefined && !seenInPage[patronId]) {
      missingPatronIds.push(patronId);
      seenInPage[patronId] = true;
    }
  }

  return missingPatronIds;
}

function cachePatronBatch(app, patronIds, patronCache) {
  var filterParts = [];
  var batchParams = {};

  for (var i = 0; i < patronIds.length; i++) {
    filterParts.push("id = {:p" + i + "}");
    batchParams["p" + i] = patronIds[i];
  }

  var results = app.findRecordsByFilter("patron_users", filterParts.join(" || "), "", patronIds.length, 0, batchParams);
  var foundIds = {};
  for (var j = 0; j < results.length; j++) {
    var record = results[j];
    patronCache[record.id] = record;
    foundIds[record.id] = true;
  }

  for (var k = 0; k < patronIds.length; k++) {
    var id = patronIds[k];
    if (!foundIds[id]) {
      patronCache[id] = null;
    }
  }
}

function buildStaffTitleRequestRow(app, titleRequest, patronCache, pickupBranchNameCache, workflowTagsCache) {
  var rowOptions = {};
  if (workflowTagsCache && workflowTagsCache[titleRequest.id] !== undefined) {
    rowOptions.workflowTags = workflowTagsCache[titleRequest.id];
  }
  var row = records.titleRequestToJson(titleRequest, app, rowOptions);
  var patronRecord = cachedPatronForTitleRequest(app, titleRequest, patronCache);

  enrichRowWithPatron(row, patronRecord);
  enrichRowWithPickupBranch(app, row, patronRecord, pickupBranchNameCache);

  return row;
}

function cachedPatronForTitleRequest(app, titleRequest, patronCache) {
  var patronId = titleRequestPatronId(titleRequest);
  if (!patronId) {
    return null;
  }
  if (patronCache[patronId] !== undefined) {
    return patronCache[patronId];
  }

  try {
    patronCache[patronId] = app.findRecordById("patron_users", patronId);
  } catch (err) {
    patronCache[patronId] = null;
  }
  return patronCache[patronId];
}

function titleRequestPatronId(titleRequest) {
  return String(titleRequest.get("patron") || "").trim();
}

function enrichRowWithPatron(row, patronRecord) {
  var patronFirst = row.nameFirst || (patronRecord ? patronRecord.get("nameFirst") || "" : "");
  var patronLast = row.nameLast || (patronRecord ? patronRecord.get("nameLast") || "" : "");

  row.patronName = (String(patronFirst).trim() + " " + String(patronLast).trim()).trim();
  row.patronEmail = row.email || (patronRecord ? patronRecord.email() || "" : "");
  row.libraryOrgName = row.libraryOrgName || (patronRecord ? patronRecord.get("libraryOrgName") || "" : "");
}

function enrichRowWithPickupBranch(app, row, patronRecord, pickupBranchNameCache) {
  row.preferredPickupBranchId = row.preferredPickupBranchId || (patronRecord ? patronRecord.get("preferredPickupBranchId") || "" : "");
  row.preferredPickupBranchName = row.preferredPickupBranchName || (patronRecord ? patronRecord.get("preferredPickupBranchName") || "" : "");
  if (!row.preferredPickupBranchId) {
    row.preferredPickupBranchId = row.patronOrgId || (patronRecord ? patronRecord.get("patronOrgId") || "" : "") || "0";
  }
  if (!row.preferredPickupBranchName) {
    row.preferredPickupBranchName = cachedPickupBranchName(app, row.preferredPickupBranchId, pickupBranchNameCache);
  }
}

function cachedPickupBranchName(app, branchId, pickupBranchNameCache) {
  if (pickupBranchNameCache[branchId] === undefined) {
    pickupBranchNameCache[branchId] = orgs.pickupBranchDisplayName(app, branchId);
  }
  return pickupBranchNameCache[branchId];
}

function titleRequestListResponseScope(app, staff, scope) {
  var label = "All libraries";
  if (scope.mode === "library") {
    label = analyticsLibraryLabel(app, scope.libraryOrgId) || staff.get("libraryOrgName") || scope.libraryOrgId || "Current library";
  }
  return {
    mode: scope.mode,
    libraryOrgId: scope.libraryOrgId,
    label: label,
    superAdmin: routeUtils.isSuperAdmin(staff),
  };
}

function staffAnalytics(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var selectedScope = String(routeUtils.queryValue(e, "scope") || routeUtils.queryValue(e, "orgId") || "").trim();
  var dateRangeKey = String(routeUtils.queryValue(e, "range") || "last30").trim();
  var scope = resolveAnalyticsScope(e.app, staff, selectedScope);
  var dateRange = resolveAnalyticsDateRange(dateRangeKey);

  if (!scope.canRead) {
    return e.json(200, emptyAnalyticsResponse(scope, dateRange));
  }

  var rows = fetchAnalyticsRecords(e.app, scope);
  return e.json(200, {
    scope: analyticsScopeResponse(e.app, staff, scope),
    dateRange: {
      key: dateRange.key,
      start: dateRange.start.toISOString(),
      end: dateRange.end.toISOString()
    },
    availableLibraries: routeUtils.isSuperAdmin(staff) ? analyticsLibraryOptions(e.app) : [],
    summary: loadAnalyticsSummary(scope, dateRange, rows),
    stageCounts: loadStageCounts(scope, rows),
    closedReasons: loadClosedReasonBreakdown(scope, dateRange, rows),
    aging: loadAgingMetrics(scope, rows),
    exceptions: loadExceptionCounts(e.app, scope, dateRange, rows)
  });
}

function resolveAnalyticsScope(app, staff, selectedOrgId) {
  var isSuper = routeUtils.isSuperAdmin(staff);
  var staffLibraryOrgId = String(staff.get("libraryOrgId") || "").trim();
  var cleanSelected = String(selectedOrgId || "").trim();

  if (isSuper && (cleanSelected === "all" || cleanSelected === "system" || (!cleanSelected && !staffLibraryOrgId))) {
    return {
      canRead: true,
      mode: "all",
      libraryOrgId: "",
      filter: "id != ''",
      params: {}
    };
  }

  var libraryOrgId = isSuper ? (cleanSelected || staffLibraryOrgId) : staffLibraryOrgId;
  if (!libraryOrgId) {
    return {
      canRead: false,
      mode: "library",
      libraryOrgId: "",
      filter: "",
      params: {}
    };
  }

  return {
    canRead: true,
    mode: "library",
    libraryOrgId: libraryOrgId,
    filter: "libraryOrgId = {:libraryOrgId}",
    params: { libraryOrgId: libraryOrgId }
  };
}

function resolveAnalyticsDateRange(key, now) {
  var end = now ? new Date(now) : new Date();
  var start = new Date(end.getTime());
  var cleanKey = key === "thisMonth" || key === "last90" ? key : "last30";

  if (cleanKey === "thisMonth") {
    start = new Date(end.getFullYear(), end.getMonth(), 1);
  } else {
    start.setDate(start.getDate() - (cleanKey === "last90" ? 90 : 30));
  }

  return {
    key: cleanKey,
    start: start,
    end: end
  };
}

function fetchAnalyticsRecords(app, scope) {
  var rows = [];
  var limit = 500;
  var offset = 0;

  while (true) {
    var page = app.findRecordsByFilter("title_requests", scope.filter, "-created", limit, offset, scope.params);
    if (!page.length) {
      break;
    }
    rows = rows.concat(page);
    if (page.length < limit) {
      break;
    }
    offset += limit;
  }

  return rows;
}

function loadAnalyticsSummary(scope, dateRange, rows) {
  var newSuggestions = 0;
  var openRequests = 0;
  var closedRequests = 0;
  var closeAgeDays = 0;

  for (var i = 0; i < rows.length; i++) {
    var row = rows[i];
    var status = records.normalizeStatus(row.get("status"));
    if (status !== records.STATUS.CLOSED) {
      openRequests++;
    }
    if (dateInRange(row.get("created") || row.created, dateRange)) {
      newSuggestions++;
    }
    if (status === records.STATUS.CLOSED && dateInRange(row.get("updated") || row.updated, dateRange)) {
      closedRequests++;
      closeAgeDays += daysBetween(row.get("created") || row.created, row.get("updated") || row.updated);
    }
  }

  return {
    newSuggestions: newSuggestions,
    openRequests: openRequests,
    closedRequests: closedRequests,
    averageDaysToClose: closedRequests ? closeAgeDays / closedRequests : 0
  };
}

function loadStageCounts(scope, rows) {
  var counts = {};
  counts[records.STATUS.SUGGESTION] = 0;
  counts[records.STATUS.OUTSTANDING_PURCHASE] = 0;
  counts[records.STATUS.PENDING_HOLD] = 0;
  counts[records.STATUS.HOLD_PLACED] = 0;
  counts[records.STATUS.CLOSED] = 0;

  for (var i = 0; i < rows.length; i++) {
    var status = records.normalizeStatus(rows[i].get("status"));
    if (counts[status] !== undefined) {
      counts[status]++;
    }
  }

  return counts;
}

function loadClosedReasonBreakdown(scope, dateRange, rows) {
  var counts = {};
  for (var i = 0; i < rows.length; i++) {
    var row = rows[i];
    if (records.normalizeStatus(row.get("status")) !== records.STATUS.CLOSED) {
      continue;
    }
    if (!dateInRange(row.get("updated") || row.updated, dateRange)) {
      continue;
    }
    var reason = String(row.get("closeReason") || "").trim() || "unrecorded";
    counts[reason] = (counts[reason] || 0) + 1;
  }
  return Object.keys(counts).sort().map(function (reason) {
    return { reason: reason, count: counts[reason] };
  });
}

function loadAgingMetrics(scope, rows, now) {
  var thresholdDays = 30;
  var byStage = {};
  var openOlderThanThreshold = 0;
  var current = now ? new Date(now) : new Date();
  var openStages = [records.STATUS.SUGGESTION, records.STATUS.OUTSTANDING_PURCHASE, records.STATUS.PENDING_HOLD, records.STATUS.HOLD_PLACED];

  for (var s = 0; s < openStages.length; s++) {
    byStage[openStages[s]] = { status: openStages[s], count: 0, totalAgeDays: 0 };
  }

  for (var i = 0; i < rows.length; i++) {
    var row = rows[i];
    var status = records.normalizeStatus(row.get("status"));
    if (status === records.STATUS.CLOSED || !byStage[status]) {
      continue;
    }
    var ageDays = daysBetween(row.get("created") || row.created, current);
    if (ageDays > thresholdDays) {
      openOlderThanThreshold++;
    }
    byStage[status].count++;
    byStage[status].totalAgeDays += ageDays;
  }

  return {
    thresholdDays: thresholdDays,
    openOlderThanThreshold: openOlderThanThreshold,
    averageAgeByStage: openStages.map(function (status) {
      var row = byStage[status];
      return {
        status: status,
        count: row.count,
        averageAgeDays: row.count ? row.totalAgeDays / row.count : 0
      };
    })
  };
}

function loadExceptionCounts(app, scope, dateRange, rows) {
  var holdFailures = 0;
  var identifierFailures = 0;

  for (var i = 0; i < rows.length; i++) {
    var row = rows[i];
    var isbnStatus = String(row.get("isbnCheckStatus") || "").trim();
    if (isbnStatus === "error" || isbnStatus === "error_max_retries") {
      identifierFailures++;
    }
    var tags = records.workflowTagsForRequest(app, row);
    for (var j = 0; j < tags.length; j++) {
      var tag = String(tags[j] || "").toLowerCase();
      if (tag.indexOf("hold failed") === 0) {
        holdFailures++;
        break;
      }
    }
  }

  return {
    holdFailures: holdFailures,
    identifierFailures: identifierFailures
  };
}

function analyticsScopeResponse(app, staff, scope) {
  var label = "All libraries";
  if (scope.mode === "library") {
    label = analyticsLibraryLabel(app, scope.libraryOrgId) || staff.get("libraryOrgName") || scope.libraryOrgId || "Current library";
  }
  return {
    mode: scope.mode,
    libraryOrgId: scope.libraryOrgId,
    label: label,
    superAdmin: routeUtils.isSuperAdmin(staff)
  };
}

function analyticsLibraryOptions(app) {
  var rows = [];
  try {
    rows = app.findRecordsByFilter("polaris_organizations", "organizationCodeId = '2'", "displayName", 500, 0);
  } catch (err) {
    rows = [];
  }
  return rows.map(function (row) {
    return {
      orgId: String(row.get("organizationId") || "").trim(),
      name: row.get("displayName") || row.get("name") || String(row.get("organizationId") || "").trim()
    };
  }).filter(function (row) {
    return !!row.orgId;
  });
}

function analyticsLibraryLabel(app, orgId) {
  try {
    var org = config.findOrganization(app, orgId);
    return org ? (org.get("displayName") || org.get("name") || orgId) : orgId;
  } catch (err) {
    return orgId;
  }
}

function emptyAnalyticsResponse(scope, dateRange) {
  return {
    scope: {
      mode: scope.mode,
      libraryOrgId: scope.libraryOrgId,
      label: "No library assigned",
      superAdmin: false
    },
    dateRange: {
      key: dateRange.key,
      start: dateRange.start.toISOString(),
      end: dateRange.end.toISOString()
    },
    availableLibraries: [],
    summary: { newSuggestions: 0, openRequests: 0, closedRequests: 0, averageDaysToClose: 0 },
    stageCounts: loadStageCounts(scope, []),
    closedReasons: [],
    aging: loadAgingMetrics(scope, []),
    exceptions: { holdFailures: 0, identifierFailures: 0 }
  };
}

function dateInRange(value, dateRange) {
  var date = parseAnalyticsDate(value);
  return !!date && date >= dateRange.start && date <= dateRange.end;
}

function daysBetween(startValue, endValue) {
  var start = parseAnalyticsDate(startValue);
  var end = parseAnalyticsDate(endValue);
  if (!start || !end || end < start) {
    return 0;
  }
  return (end.getTime() - start.getTime()) / 86400000;
}

function parseAnalyticsDate(value) {
  if (value instanceof Date) {
    return value;
  }
  var text = String(value || "").trim();
  if (!text) {
    return null;
  }
  var normalized = text.indexOf(" ") > -1 ? text.replace(" ", "T") : text;
  var date = new Date(normalized);
  if (isNaN(date.getTime())) {
    date = new Date(text);
  }
  return isNaN(date.getTime()) ? null : date;
}

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

function staffClaimTitleRequest(e) {
  return mutateTitleRequestClaim(e, "claim");
}

function staffUnclaimTitleRequest(e) {
  return mutateTitleRequestClaim(e, "unclaim");
}

function mutateTitleRequestClaim(e, action) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = String(e.request.pathValue("id") || "").trim();

  try {
    var updated = runClaimMutation(e.app, function (app) {
      var record = app.findRecordById("title_requests", id);
      var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
      if (accessError) {
        return { response: accessError };
      }

      if (action === "claim") {
        return claimTitleRequest(app, staff, record);
      }
      return unclaimTitleRequest(app, staff, record);
    });

    if (updated && updated.response) {
      return updated.response;
    }
    return e.json(200, records.titleRequestToJson(updated, e.app));
  } catch (err) {
    if (err && err.statusCode) {
      return e.json(err.statusCode, { message: err.message });
    }
    e.app.logger().error("Staff claim action failed", "requestId", id, "error", String(err));
    return e.json(400, { message: "System error: " + err.message });
  }
}

function runClaimMutation(app, fn) {
  if (app && typeof app.runInTransaction === "function") {
    var result;
    app.runInTransaction(function (txApp) {
      result = fn(txApp);
    });
    return result;
  }
  return fn(app);
}

function claimTitleRequest(app, staff, record) {
  var claimantId = String(record.get("claimedByStaffUserId") || "").trim();
  var staffId = String(staff.id || "").trim();
  if (claimantId && claimantId !== staffId) {
    throw claimConflictError(record);
  }

  record.set("claimedByStaffUserId", staffId);
  record.set("claimedByDisplayName", staffClaimDisplayName(staff));
  record.set("claimedAt", new Date().toISOString());
  record.set("updated", new Date().toISOString());
  app.save(record);
  return app.findRecordById("title_requests", record.id);
}

function unclaimTitleRequest(app, staff, record) {
  var claimantId = String(record.get("claimedByStaffUserId") || "").trim();
  var staffId = String(staff.id || "").trim();
  if (claimantId && claimantId !== staffId && !routeUtils.isAdminRole(staff)) {
    throw forbiddenClaimError("Only the staff member who claimed this request, an admin, or a super admin can unclaim it.");
  }

  record.set("claimedByStaffUserId", "");
  record.set("claimedByDisplayName", "");
  record.set("claimedAt", "");
  record.set("updated", new Date().toISOString());
  app.save(record);
  return app.findRecordById("title_requests", record.id);
}

function staffClaimDisplayName(staff) {
  return String(staff.get("displayName") || staff.get("username") || staff.get("identityKey") || "Staff").trim();
}

function claimConflictError(record) {
  var name = String(record.get("claimedByDisplayName") || "another staff member").trim();
  var err = new Error("This request is already claimed by " + name + ".");
  err.statusCode = 409;
  return err;
}

function forbiddenClaimError(message) {
  var err = new Error(message);
  err.statusCode = 403;
  return err;
}

function staffTitleRequestAction(e) {
  try {
    var context = titleRequestActionContext(e);
    if (context.response) {
      return context.response;
    }

    if (context.nextStatus === records.STATUS.PENDING_HOLD && !String(context.data.bibid || "").trim()) {
      return e.json(400, { message: "BIB ID is required before moving this suggestion to Pending hold." });
    }

    var bibActionResponse = prepareTitleRequestBibAction(e, context);
    if (bibActionResponse) {
      return bibActionResponse;
    }

    finalizeTitleRequestCloseReason(e.app, context);
    context.record = records.updateTitleRequest(e.app, context.id, context.data, context.staff.get("username"));
    maybeRunImmediatePromoter(e.app, context);
    handleAlreadyOwnOrRejectSideEffects(e.app, context);

    var purchaseReminderEmail = sendPurchaseReminderIfRequested(e.app, context);
    var response = records.titleRequestToJson(context.record, e.app);
    response.purchaseReminderEmail = purchaseReminderEmail;
    return e.json(200, response);
  } catch (err) {
    e.app.logger().error("Staff action failed", "error", String(err));
    return e.json(400, { message: "System error: " + err.message });
  }
}

function titleRequestActionContext(e) {
  var staff = routeUtils.requireAuth(e, "staff_users");
  var id = e.request.pathValue("id");
  var data = routeUtils.body(e);
  var action = String(data.action || "");
  var nextStatus = records.normalizeStatus(data.status);
  var record;

  try {
    record = e.app.findRecordById("title_requests", id);
  } catch (findErr) {
    return {
      response: e.json(404, { message: "Suggestion not found: " + id })
    };
  }

  var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
  if (accessError) {
    return { response: accessError };
  }

  var oldStatus = records.normalizeStatus(record.get("status"));
  var originalIdentifier = String(record.get("identifier") || "").trim();
  var nextIdentifier = data.identifier !== undefined && data.identifier !== null
    ? String(data.identifier).trim()
    : originalIdentifier;

  return {
    response: null,
    staff: staff,
    id: id,
    data: data,
    action: action,
    record: record,
    oldStatus: oldStatus,
    nextStatus: nextStatus,
    isClosingRequest: nextStatus === records.STATUS.CLOSED,
    isDuplicateClose: action === "closeDuplicate",
    isActiveHoldTarget: nextStatus === records.STATUS.PENDING_HOLD || nextStatus === records.STATUS.HOLD_PLACED || action === "alreadyOwn",
    duplicateCloseNoteAdded: false,
    shouldRunImmediatePromoter: !!nextIdentifier && nextIdentifier !== originalIdentifier
  };
}

function prepareTitleRequestBibAction(e, context) {
  if (!context.data.bibid) {
    return null;
  }

  var bibid = String(context.data.bibid).trim();
  var barcode = context.record.get("barcode");
  var staffAuth = staffActionPolarisAuth(e.app);
  var duplicateResponse = handleDuplicateBibRequest(e, context, bibid);
  if (duplicateResponse) {
    return duplicateResponse;
  }

  reconcileBibAction(e.app, context, staffAuth, bibid);
  handleHoldTransitionForBibAction(e.app, context, staffAuth, bibid, barcode);
  return null;
}

function staffActionPolarisAuth(app) {
  try {
    return polaris.adminStaffAuth();
  } catch (err) {
    app.logger().warn("Polaris auth failed", "error", String(err));
  }
}

function handleDuplicateBibRequest(e, context, bibid) {
  var existing = e.app.findRecordsByFilter("title_requests",
    "barcode = {:barcode} && bibid = {:bibid} && id != {:id} && status != 'closed'",
    "", 1, 0, { barcode: context.record.get("barcode"), bibid: bibid, id: context.id });
  if (!existing || !existing.length) {
    return null;
  }

  records.addWorkflowTagForRequest(e.app, context.record, "Hold exists (same patron)");
  if (context.isDuplicateClose) {
    markDuplicateClose(context);
    return null;
  }

  if (wouldCreateActiveDuplicate(context, bibid)) {
    e.app.save(context.record);
    return e.json(400, { message: "Duplicate detected: This patron already has an open request for BIB ID " + bibid + "." });
  }

  return null;
}

function markDuplicateClose(context) {
  context.data.status = records.STATUS.CLOSED;
  context.nextStatus = records.STATUS.CLOSED;
  context.data.closeReason = records.CLOSE_REASON.DUPLICATE_HOLD;
  records.appendSystemNote(context.record, "Closed as duplicate because this patron already has an open request or hold for the same BIB ID.");
  context.duplicateCloseNoteAdded = true;
}

function wouldCreateActiveDuplicate(context, bibid) {
  var oldIsActiveHold = context.oldStatus === records.STATUS.PENDING_HOLD || context.oldStatus === records.STATUS.HOLD_PLACED;
  var bibidChanged = String(context.record.get("bibid") || "").trim() !== bibid;
  return context.isActiveHoldTarget && (!oldIsActiveHold || bibidChanged || context.action === "alreadyOwn");
}

function reconcileBibAction(app, context, staffAuth, bibid) {
  if (context.isDuplicateClose || context.isClosingRequest) {
    return;
  }

  polaris.reconcileRecord(app, staffAuth, context.record, bibid);
  context.data.title = context.record.get("title");
  context.data.author = context.record.get("author");
}

function handleHoldTransitionForBibAction(app, context, staffAuth, bibid, barcode) {
  if (context.nextStatus !== records.STATUS.PENDING_HOLD && !(context.oldStatus === records.STATUS.OUTSTANDING_PURCHASE && context.data.bibid)) {
    return;
  }

  if (context.record.getBool("autohold") === false) {
    closeAutoholdOptOutBibAction(context);
    return;
  }

  if (context.nextStatus === records.STATUS.PENDING_HOLD) {
    maybePromoteExistingPolarisHold(app, context, staffAuth, bibid, barcode);
  }
}

function closeAutoholdOptOutBibAction(context) {
  context.nextStatus = records.STATUS.CLOSED;
  context.data.status = context.nextStatus;
  context.data.closeReason = records.CLOSE_REASON.PURCHASED_NO_HOLD;
  var optOutReason = (context.action === "alreadyOwn")
    ? "Closed without hold because 'Already Own' was selected and patron opted out of automatic hold placement."
    : "Closed without hold because BIB ID was entered and patron opted out of automatic hold placement.";
  records.appendSystemNote(context.record, optOutReason);
}

function maybePromoteExistingPolarisHold(app, context, staffAuth, bibid, barcode) {
  try {
    var pPatron = polaris.lookupPatron(staffAuth, barcode);
    if (pPatron && pPatron.PatronID) {
      var holdCheck = polaris.placeHold(staffAuth, bibid, pPatron.PatronID, true);
      if (holdCheck && holdCheck.statusValue === 29) {
        context.nextStatus = records.STATUS.HOLD_PLACED;
        context.data.status = context.nextStatus;
        records.appendSystemNote(context.record, "Patron already has a hold in Polaris for this BIB ID. Moving directly to Hold placed.");
      }
    }
  } catch (polarisErr) {
    app.logger().warn("Polaris duplicate hold check failed during staff action", "error", String(polarisErr));
  }
}

function finalizeTitleRequestCloseReason(app, context) {
  if (context.nextStatus === records.STATUS.CLOSED && (context.action === "reject" || context.action === "silentClose")) {
    context.data.closeReason = (context.action === "silentClose") ? records.CLOSE_REASON.SILENT : records.CLOSE_REASON.REJECTED;
  }
  if (context.nextStatus === records.STATUS.CLOSED && context.isDuplicateClose) {
    context.data.closeReason = records.CLOSE_REASON.DUPLICATE_HOLD;
    records.addWorkflowTagForRequest(app, context.record, "Hold exists (same patron)");
    if (!context.duplicateCloseNoteAdded) {
      records.appendSystemNote(context.record, "Closed as duplicate because this patron already has an open request or hold for the same BIB ID.");
    }
  }
  if (context.nextStatus === records.STATUS.CLOSED && !context.data.closeReason && context.record.getBool("autohold") === false && context.data.bibid) {
    context.data.closeReason = records.CLOSE_REASON.PURCHASED_NO_HOLD;
  }
  if (context.nextStatus !== records.STATUS.CLOSED) {
    context.data.closeReason = "";
  }
}

function maybeRunImmediatePromoter(app, context) {
  if (!context.shouldRunImmediatePromoter) {
    return;
  }

  try {
    var updatedStatus = records.normalizeStatus(context.record.get("status"));
    if (updatedStatus === records.STATUS.SUGGESTION || config.suggestionLimit(app, "").autoPromote !== false) {
      jobs.promoteRequestNow(app, polaris.adminStaffAuth(), context.record);
      context.record = app.findRecordById("title_requests", context.record.id);
    }
  } catch (promoteErr) {
    app.logger().error("Immediate identifier promotion failed", "recordId", context.record.id, "error", String(promoteErr));
  }
}

function handleAlreadyOwnOrRejectSideEffects(app, context) {
  if (context.action !== "alreadyOwn" && context.action !== "reject") {
    return;
  }

  var patron = refreshedActionPatron(app, context.record);
  if (context.action === "alreadyOwn") {
    handleAlreadyOwnSideEffects(app, context, patron);
  }
  if (context.action === "reject") {
    sendRejectedActionEmail(app, context, patron);
  }
}

function refreshedActionPatron(app, record) {
  try {
    return polaris.lookupPatron(polaris.adminStaffAuth(), record.get("barcode"));
  } catch (err) {
    app.logger().warn("Could not refresh patron data for staff action email", "recordId", record.id, "error", String(err));
  }
  return null;
}

function handleAlreadyOwnSideEffects(app, context, patron) {
  var bibid = String(context.data.bibid || "").trim();
  if (bibid && patron && patron.PatronID) {
    placeAlreadyOwnedHold(app, context.record, bibid, patron);
  }
  sendAlreadyOwnedActionEmail(app, context.record, patron);
}

function placeAlreadyOwnedHold(app, record, bibid, patron) {
  var localStaffAuth;
  try {
    localStaffAuth = polaris.adminStaffAuth();
  } catch (e) { }

  try {
    polaris.reconcileRecord(app, localStaffAuth, record, bibid);
  } catch (reconcileErr) {
    app.logger().error("Already-owned reconcile failed during staff action", "recordId", record.id, "bibid", bibid, "error", String(reconcileErr));
  }

  if (record.getBool("autohold") === false) {
    records.appendSystemNote(record, "Skipped auto-hold for 'Already Own' action because patron opted out of automatic hold placement.");
    app.save(record);
    return;
  }

  try {
    polaris.placeHold(localStaffAuth, bibid, patron.PatronID, false);
    records.appendSystemNote(record, "Auto-placed hold for patron since item is already owned (BIB " + bibid + ")");
  } catch (holdErr) {
    app.logger().error("Auto-hold failed during alreadyOwn action", "recordId", record.id, "bibid", bibid, "error", String(holdErr));
  }
}

function sendAlreadyOwnedActionEmail(app, record, patron) {
  try {
    if (!mail.alreadyOwned(app, record, patron)) {
      routeUtils.noteSkippedEmail(app, record);
    }
  } catch (mailErr) {
    app.logger().error("Already-owned email failed", "recordId", record.id, "error", String(mailErr));
  }
}

function sendRejectedActionEmail(app, context, patron) {
  try {
    if (!mail.rejected(app, context.record, patron, context.data.rejectionTemplateId)) {
      routeUtils.noteSkippedEmail(app, context.record);
    }
  } catch (mailErr) {
    app.logger().error("Rejected suggestion email failed", "recordId", context.record.id, "error", String(mailErr));
  }
}

function sendPurchaseReminderIfRequested(app, context) {
  var purchaseReminderEmail = {
    requested: context.action === "purchase" && context.data.emailPurchaseReminder === true,
    sent: false,
    message: ""
  };

  if (!purchaseReminderEmail.requested) {
    return purchaseReminderEmail;
  }

  var staffEmail = String(context.staff.get("weekly_action_summary_email") || "").trim();
  if (!staffEmail) {
    purchaseReminderEmail.message = "Purchase saved. Add an email address to your staff profile to email yourself purchase reminders.";
    return purchaseReminderEmail;
  }

  try {
    purchaseReminderEmail.sent = !!mail.purchaseReminder(app, context.record, context.staff, staffEmail, routeUtils.staffRequestUrl(app, context.record));
    purchaseReminderEmail.message = purchaseReminderEmail.sent
      ? "Purchase saved and reminder email sent."
      : "Purchase saved, but email notifications are not configured.";
  } catch (mailErr) {
    app.logger().error("Purchase reminder email failed", "recordId", context.record.id, "staffUserId", context.staff.id, "error", String(mailErr));
    purchaseReminderEmail.message = "Purchase saved, but the reminder email could not be sent.";
  }
  return purchaseReminderEmail;
}

function staffDeleteClosedRequest(e) {
  var staff = routeUtils.requireAdminStaff(e);
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

  var accessError = routeUtils.requireTitleRequestAccess(e, staff, record);
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
  var staff = routeUtils.requireAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Admin access required." });
  }

  var data = routeUtils.body(e);
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

function staffTestPolaris(e) {
  if (!routeUtils.requireSuperAdminStaff(e)) {
    return e.json(403, { message: "Super admin access required" });
  }
  var data = routeUtils.body(e);
  var polarisData = data && data.polaris ? routeUtils.buildPolarisData(data) : config.polaris();
  return routeUtils.testPolarisConnection(e, polarisData);
}

function staffTestSmtp(e) {
  var staff = routeUtils.requireSuperAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Super admin access required" });
  }
  try {
    config.applyMailSettings(e.app);

    var d = routeUtils.body(e);
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
  if (!routeUtils.requireSuperAdminStaff(e)) {
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
  var staff = routeUtils.requireAuth(e, "staff_users");
  var d = routeUtils.body(e);
  var bibId = String(d.bibId || "").trim();
  var mode = String(d.mode || "").trim().toLowerCase();
  if (!bibId && mode) {
    var query = String(d.query || "").trim();
    var title = String(d.title || "").trim();
    var author = String(d.author || "").trim();
    var validModes = ["identifier", "title", "author", "title_author"];
    if (validModes.indexOf(mode) < 0) {
      return e.json(400, { message: "Invalid Polaris search mode." });
    }
    try {
      var searchResult = polaris.searchBibs(polaris.adminStaffAuth(), {
        mode: mode,
        query: query,
        title: title,
        author: author,
        limit: 10
      });
      return e.json(200, {
        success: searchResult.status !== "error",
        mode: searchResult.mode,
        query: searchResult.query,
        status: searchResult.status,
        totalMatches: searchResult.totalMatches,
        multipleMatches: searchResult.multipleMatches,
        results: searchResult.results || [],
        error: searchResult.error || ""
      });
    } catch (err) {
      return e.json(400, { message: err.message || String(err) });
    }
  }
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
          if (!routeUtils.sameLibrary(staff, patron.LibraryOrgID)) {
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
  var staff = routeUtils.requireAdminStaff(e);
  if (!staff) {
    return e.json(403, { message: "Admin access required to update settings." });
  }
  var payload = routeUtils.body(e);
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
    validateMaterialFormatsDeletion(app, scope, orgId, ui);
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
    setFormatFieldRule(record, "audience", fields.agegroup, "Age Group");
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

  // Delete all existing records for this scope first to avoid stale/duplicate rows
  var deleteFilter = scope === "system" ? "scope != 'library'" : "scope = 'library' && libraryOrganization = {:org}";
  var deleteParams = scope === "system" ? {} : { org: org ? org.id : "" };
  try {
    var existing = app.findRecordsByFilter("audience_groups", deleteFilter, "", 200, 0, deleteParams);
    existing.forEach(function (row) { app.delete(row); });
  } catch (err) {}

  // Insert fresh records in order
  var collection = app.findCollectionByNameOrId("audience_groups");
  labels.forEach(function (label, index) {
    var code = codeFromLabel(label, "group_" + (index + 1));
    var record = new Record(collection);
    record.set("scope", scope);
    if (org) record.set("libraryOrganization", org.id);
    record.set("code", code);
    record.set("label", label);
    record.set("sortOrder", (index + 1) * 10);
    app.save(record);
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
  staffAnalytics: staffAnalytics,
  staffClaimTitleRequest: staffClaimTitleRequest,
  staffUnclaimTitleRequest: staffUnclaimTitleRequest,
  staffTitleRequestAction: staffTitleRequestAction,
  staffCreateSuggestion: staffCreateSuggestion,
  staffSyncOrganizations: staffSyncOrganizations,
  staffTestPolaris: staffTestPolaris,
  staffTestSmtp: staffTestSmtp,
  staffEmailStatus: staffEmailStatus,
  staffBibLookup: staffBibLookup,
  getLibrarySettings: getLibrarySettings,
  updateLibrarySettings: updateLibrarySettings,
  saveWorkflowSettings: saveWorkflowSettings,
  saveRejectionTemplates: saveRejectionTemplates,
  staffSaveLogo: staffSaveLogo,
  staffResetLogo: staffResetLogo,
  assertRejectionTemplateNotUsedByAutoReject: assertRejectionTemplateNotUsedByAutoReject,
  titleRequestListScope: titleRequestListScope,
  resolveAnalyticsScope: resolveAnalyticsScope,
  resolveAnalyticsDateRange: resolveAnalyticsDateRange,
  loadAnalyticsSummary: loadAnalyticsSummary,
  loadStageCounts: loadStageCounts,
  loadClosedReasonBreakdown: loadClosedReasonBreakdown,
  loadAgingMetrics: loadAgingMetrics,
  loadExceptionCounts: loadExceptionCounts,
  TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE: TEMPLATE_IN_USE_BY_AUTO_REJECT_MESSAGE,
  TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE: TEMPLATE_IN_USE_BY_AUTO_REJECT_CODE,
  validateAudienceGroupsDeletion: validateAudienceGroupsDeletion,
  validateMaterialFormatsDeletion: validateMaterialFormatsDeletion,
};
