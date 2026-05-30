const identity = require("../identity.js");
const helpers = require("./helpers.js");

function hasStaffUsers(app) {
  try {
    return app.countRecords("staff_users") > 0;
  } catch (err) {
    try {
      let records = app.findRecordsByFilter("staff_users", "id != ''", "", 1, 0);
      return records.length > 0;
    } catch (innerErr) {
      app.logger().error("Staff user count failed", "error", String(innerErr));
      return false;
    }
  }
}

function listStaffUsers(app) {
  let users = [];
  let limit = 100;
  let offset = 0;
  while (true) {
    let page = app.findRecordsByFilter("staff_users", "id != ''", "username", limit, offset);
    if (!page.length) {
      break;
    }
    users = users.concat(page);
    if (page.length < limit) {
      break;
    }
    offset += limit;
  }
  return users;
}

function countAdminUsers(app) {
  let admins = app.findRecordsByFilter("staff_users", "role = 'admin'", "", 2, 0);
  return admins.length;
}

function countSuperAdminUsers(app) {
  let admins = app.findRecordsByFilter("staff_users", "role = 'super_admin'", "", 2, 0);
  return admins.length;
}

function findStaffByIdentity(app, identityKey) {
  return helpers.findFirstByData(app, "staff_users", "identityKey", identityKey);
}

function staffAuthEmailForIdentity(identityKey) {
  return String(identityKey || "").replace(/[^a-z0-9._-]+/g, ".") + "@staff.asap.local";
}

function duplicateStaffUserError() {
  let err = new Error("Staff user already exists.");
  err.code = 409;
  err.duplicate = true;
  return err;
}

function isDuplicateSaveError(err) {
  let text = String(err && (err.message || err)).toLowerCase();
  return text.indexOf("unique") >= 0 || text.indexOf("already exists") >= 0 || text.indexOf("validation_not_unique") >= 0;
}

function findStaffByEmail(app, email) {
  email = String(email || "").trim().toLowerCase();
  if (!email) return null;
  return helpers.findFirstByData(app, "staff_users", "email", email);
}

function applyStaffUserFields(app, record, staffIdentity, displayName, options) {
  options = options || {};
  let username = identity.normalizeUsername(staffIdentity.username || "");
  let domain = identity.normalizeDomain(staffIdentity.domain || "");
  let identityKey = staffIdentity.identityKey || identity.buildIdentityKey(domain, username);

  record.set("username", username);
  record.set("domain", domain);
  record.set("identityKey", identityKey);
  record.set("displayName", displayName || username);
  record.set("role", options.role || options.defaultRole || "staff");
  record.set("active", options.active !== false);
  if (options.polarisUserId !== undefined) {
    record.set("polarisUserId", String(options.polarisUserId || ""));
  }
  if (options.branchOrgId !== undefined) {
    record.set("branchOrgId", String(options.branchOrgId || ""));
    helpers.setRelation(record, "branchOrganization", helpers.organizationByPolarisId(app, options.branchOrgId));
  }
  if (options.libraryOrgId !== undefined) {
    record.set("libraryOrgId", String(options.libraryOrgId || ""));
    helpers.setRelation(record, "libraryOrganization", helpers.organizationByPolarisId(app, options.libraryOrgId));
  }
  if (options.libraryOrgName !== undefined) {
    record.set("libraryOrgName", String(options.libraryOrgName || ""));
  }
  if (options.scope !== undefined) {
    record.set("scope", String(options.scope || "library"));
  } else if (!record.get("scope")) {
    record.set("scope", options.defaultRole === "super_admin" || options.role === "super_admin" ? "system" : "library");
  }
  if (options.lastOrgSync !== false) {
    record.set("lastOrgSync", new Date().toISOString());
  }
  if (options.updateLastLogin) {
    record.set("lastLogin", new Date().toISOString());
  }
  record.set("lastPolarisLogin", new Date().toISOString());
  record.setVerified(true);
}

function createStaffUser(app, staffIdentity, displayName, options) {
  options = options || {};
  if (typeof staffIdentity === "string") {
    staffIdentity = identity.parseStaffIdentity(staffIdentity, options.defaultDomain || "");
  }
  let username = identity.normalizeUsername(staffIdentity.username || "");
  let domain = identity.normalizeDomain(staffIdentity.domain || "");
  let identityKey = staffIdentity.identityKey || identity.buildIdentityKey(domain, username);
  let authEmail = String(options.email || "").trim().toLowerCase() || staffAuthEmailForIdentity(identityKey);

  if (findStaffByIdentity(app, identityKey) || findStaffByEmail(app, authEmail)) {
    throw duplicateStaffUserError();
  }

  let record = new Record(app.findCollectionByNameOrId("staff_users"));
  record.setEmail(authEmail);
  record.setRandomPassword();
  applyStaffUserFields(app, record, {
    username: username,
    domain: domain,
    identityKey: identityKey,
  }, displayName, options);

  try {
    app.save(record);
  } catch (err) {
    if (isDuplicateSaveError(err)) {
      throw duplicateStaffUserError();
    }
    throw err;
  }
  return record;
}

function upsertStaffUser(app, staffIdentity, displayName, options) {
  options = options || {};
  if (typeof staffIdentity === "string") {
    staffIdentity = identity.parseStaffIdentity(staffIdentity, options.defaultDomain || "");
  }
  let username = identity.normalizeUsername(staffIdentity.username || "");
  let domain = identity.normalizeDomain(staffIdentity.domain || "");
  let identityKey = staffIdentity.identityKey || identity.buildIdentityKey(domain, username);
  let record = findStaffByIdentity(app, identityKey);
  let existingRole = "";
  if (!record) {
    record = new Record(app.findCollectionByNameOrId("staff_users"));
    record.setEmail(staffAuthEmailForIdentity(identityKey));
    record.setRandomPassword();
  } else {
    existingRole = String(record.get("role") || "").trim();
  }
  options.role = options.role || existingRole || options.defaultRole || "staff";
  options.active = true;
  applyStaffUserFields(app, record, staffIdentity, displayName, options);
  app.save(record);
  return record;
}

function queryStaffUsersByFilter(app, filter, params) {
  let users = [];
  let limit = 100;
  let offset = 0;
  while (true) {
    let page = app.findRecordsByFilter("staff_users", filter, "username", limit, offset, params || {});
    if (!page.length) {
      break;
    }
    users = users.concat(page);
    if (page.length < limit) {
      break;
    }
    offset += limit;
  }
  return users;
}

function listScopedStaffUsers(app, adminUser, targetOrgId) {
  let role = String(adminUser.get("role") || "").toLowerCase();
  let isSuper = role === "super_admin";
  let isAdmin = role === "admin" || isSuper;

  if (!isAdmin) {
    throw new Error("Admin access required");
  }

  targetOrgId = String(targetOrgId || "").trim();

  let filter = "";
  let params = {};

  if (isSuper) {
    if (!targetOrgId || targetOrgId === "system" || targetOrgId === "all") {
      filter = "id != ''";
    } else {
      filter = "libraryOrgId = {:targetOrgId} || role = 'super_admin'";
      params.targetOrgId = targetOrgId;
    }
  } else {
    let adminLibraryOrgId = String(adminUser.get("libraryOrgId") || "").trim();
    if (!adminLibraryOrgId) {
      filter = "id = ''";
    } else {
      filter = "libraryOrgId = {:adminLibraryOrgId}";
      params.adminLibraryOrgId = adminLibraryOrgId;
    }
  }

  return queryStaffUsersByFilter(app, filter, params);
}

module.exports = {
  hasStaffUsers: hasStaffUsers,
  listStaffUsers: listStaffUsers,
  countAdminUsers: countAdminUsers,
  countSuperAdminUsers: countSuperAdminUsers,
  findStaffByIdentity: findStaffByIdentity,
  findStaffByEmail: findStaffByEmail,
  createStaffUser: createStaffUser,
  upsertStaffUser: upsertStaffUser,
  listScopedStaffUsers: listScopedStaffUsers,
};
