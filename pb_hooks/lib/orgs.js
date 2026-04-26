const polaris = require(`${__hooks}/lib/polaris.js`);

const ORG_CODE = {
  SYSTEM: "1",
  LIBRARY: "2",
  BRANCH: "3",
};

function normalizeOrgId(value) {
  value = String(value === undefined || value === null ? "" : value).trim();
  return value;
}

function normalizeOrgRow(row) {
  row = row || {};
  var orgId = normalizeOrgId(row.OrganizationID || row.organizationId);
  return {
    organizationId: orgId,
    organizationCodeId: String(row.OrganizationCodeID || row.organizationCodeId || "").trim(),
    name: String(row.Name || row.name || "").trim(),
    abbreviation: String(row.Abbreviation || row.abbreviation || "").trim(),
    displayName: String(row.DisplayName || row.displayName || row.Name || row.name || "").trim(),
    parentOrganizationId: normalizeOrgId(row.ParentOrganizationID || row.parentOrganizationId),
    raw: row,
  };
}

function upsertOrganization(app, row) {
  var org = normalizeOrgRow(row);
  if (!org.organizationId) {
    return null;
  }

  var record = findOrganization(app, org.organizationId);
  if (!record) {
    record = new Record(app.findCollectionByNameOrId("polaris_organizations"));
  }

  record.set("organizationId", org.organizationId);
  record.set("organizationCodeId", org.organizationCodeId);
  record.set("name", org.name);
  record.set("abbreviation", org.abbreviation);
  record.set("displayName", org.displayName);
  record.set("parentOrganizationId", org.parentOrganizationId);
  record.set("raw", org.raw);
  record.set("lastSynced", new Date().toISOString());
  app.save(record);
  return record;
}

function findOrganization(app, organizationId) {
  organizationId = normalizeOrgId(organizationId);
  if (!organizationId) {
    return null;
  }
  try {
    return app.findFirstRecordByData("polaris_organizations", "organizationId", organizationId);
  } catch (err) {
    return null;
  }
}

function syncOrganizations(app, staffAuth) {
  staffAuth = staffAuth || polaris.adminStaffAuth();
  var kinds = ["system", "library", "branch"];
  var count = 0;
  for (var i = 0; i < kinds.length; i++) {
    var rows = polaris.organizations(kinds[i], staffAuth);
    for (var j = 0; j < rows.length; j++) {
      if (upsertOrganization(app, rows[j])) {
        count++;
      }
    }
  }
  return { synced: count };
}

function resolveParentLibrary(app, organizationId, options) {
  options = options || {};
  var orgId = normalizeOrgId(organizationId);
  if (!orgId) {
    return null;
  }

  var record = findOrganization(app, orgId);
  if (!record && options.syncIfMissing !== false) {
    try {
      syncOrganizations(app, options.staffAuth);
      record = findOrganization(app, orgId);
    } catch (err) {
      if (options.logger) {
        options.logger.warn("Polaris organization sync failed", "organizationId", orgId, "error", String(err));
      }
    }
  }

  var visited = {};
  while (record) {
    var currentId = String(record.get("organizationId") || "").trim();
    if (!currentId || visited[currentId]) {
      return null;
    }
    visited[currentId] = true;

    var codeId = String(record.get("organizationCodeId") || "").trim();
    if (codeId === ORG_CODE.LIBRARY) {
      return {
        branchOrgId: orgId,
        libraryOrgId: currentId,
        libraryOrgName: String(record.get("displayName") || record.get("name") || currentId),
      };
    }
    if (codeId === ORG_CODE.SYSTEM) {
      return {
        branchOrgId: orgId,
        libraryOrgId: "",
        libraryOrgName: String(record.get("displayName") || record.get("name") || "System"),
        scope: "system",
      };
    }

    var parentId = String(record.get("parentOrganizationId") || "").trim();
    if (!parentId) {
      return null;
    }
    record = findOrganization(app, parentId);
  }
  return null;
}

function attachPatronScope(app, patron, staffAuth, logger) {
  patron = patron || {};
  var patronOrgId = normalizeOrgId(patron.PatronOrgID || patron.patronOrgId);
  var scope = resolveParentLibrary(app, patronOrgId, { staffAuth: staffAuth, logger: logger });
  if (scope && scope.libraryOrgId) {
    patron.PatronOrgID = patronOrgId;
    patron.LibraryOrgID = scope.libraryOrgId;
    patron.LibraryOrgName = scope.libraryOrgName;
  }

  var pickupBranchId = normalizeOrgId(patron.RequestPickupBranchID || patron.preferredPickupBranchId);
  if (pickupBranchId) {
    var pickupOrg = findOrganization(app, pickupBranchId);
    if (!pickupOrg && staffAuth) {
      try {
        syncOrganizations(app, staffAuth);
        pickupOrg = findOrganization(app, pickupBranchId);
      } catch (err) {
        if (logger) logger.warn("Failed to sync organizations for pickup branch", "id", pickupBranchId, "error", String(err));
      }
    }

    if (pickupOrg) {
      patron.PreferredPickupBranchID = pickupBranchId;
      patron.PreferredPickupBranchName = String(pickupOrg.get("displayName") || pickupOrg.get("name") || pickupBranchId);
    } else {
      patron.PreferredPickupBranchID = pickupBranchId;
      patron.PreferredPickupBranchName = "Branch ID: " + pickupBranchId;
    }
  }

  return patron;
}

module.exports = {
  attachPatronScope: attachPatronScope,
  normalizeOrgId: normalizeOrgId,
  resolveParentLibrary: resolveParentLibrary,
  syncOrganizations: syncOrganizations,
};
