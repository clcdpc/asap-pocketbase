const config = require(`${__hooks}/lib/config.js`);
const identity = require(`${__hooks}/lib/identity.js`);
const orgs = require(`${__hooks}/lib/orgs.js`);
const polaris = require(`${__hooks}/lib/polaris.js`);
const records = require(`${__hooks}/lib/records.js`);
const routeUtils = require(`${__hooks}/lib/route_utils.js`);

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

function initialSetup(e) {
  if (records.hasStaffUsers(e.app)) {
    return e.json(409, { message: "Initial setup has already been completed." });
  }

  var data = routeUtils.body(e);
  var polarisData = routeUtils.buildPolarisData(data);
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

  return routeUtils.testPolarisConnection(e, routeUtils.buildPolarisData(routeUtils.body(e)));
}

module.exports = {
  setupStatus: setupStatus,
  initialSetup: initialSetup,
  setupTestPolaris: setupTestPolaris
};
