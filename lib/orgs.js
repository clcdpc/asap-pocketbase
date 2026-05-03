const polaris = require(`${__hooks}/../lib/polaris.js`);

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
  var parent = org.parentOrganizationId ? findOrganization(app, org.parentOrganizationId) : null;
  record.set("parentOrganization", parent ? parent.id : "");
  record.set("enabledForPatrons", !!record.getBool("enabledForPatrons"));
  record.set("raw", org.raw);
  record.set("lastSynced", new Date().toISOString());
  app.save(record);
  return record;
}

function setSyncStatus(app, status, message, error) {
  try {
    var settings = app.findRecordById("system_settings", "settings0000001");
    settings.set("organizationsSyncStatus", status);
    settings.set("organizationsSyncMessage", message || "");
    settings.set("organizationsSyncError", error || "");
    if (status === "loaded") settings.set("organizationsLastSynced", new Date().toISOString());
    app.save(settings);
  } catch (err) {}
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
  setSyncStatus(app, "loading", "Organizations loading from Polaris.", "");
  var kinds = ["system", "library", "branch"];
  var count = 0;
  try {
    for (var i = 0; i < kinds.length; i++) {
      var rows = polaris.organizations(kinds[i], staffAuth);
      for (var j = 0; j < rows.length; j++) {
        if (upsertOrganization(app, rows[j])) {
          count++;
        }
      }
    }
    relinkParents(app);
    setSyncStatus(app, "loaded", "Polaris organizations loaded successfully.", "");
    return { synced: count };
  } catch (err) {
    setSyncStatus(app, "error", "Polaris connected, but organizations could not be loaded.", err.message || String(err));
    throw err;
  }
}

function relinkParents(app) {
  var offset = 0;
  while (true) {
    var rows = app.findRecordsByFilter("polaris_organizations", "parentOrganizationId != ''", "", 200, offset);
    if (!rows.length) break;

    var parentIdsMap = {};
    for (var i = 0; i < rows.length; i++) {
      var pid = normalizeOrgId(rows[i].get("parentOrganizationId"));
      if (pid) {
        parentIdsMap[pid] = true;
      }
    }

    var parentCache = {};
    var uniqueParentIds = Object.keys(parentIdsMap);
    if (uniqueParentIds.length > 0) {
      var chunkSize = 100;
      for (var i = 0; i < uniqueParentIds.length; i += chunkSize) {
        var chunk = uniqueParentIds.slice(i, i + chunkSize);
        var filterParts = [];
        var params = {};
        for (var j = 0; j < chunk.length; j++) {
          filterParts.push("organizationId = {:p" + j + "}");
          params["p" + j] = chunk[j];
        }

        try {
          var parents = app.findRecordsByFilter("polaris_organizations", filterParts.join(" || "), "", chunk.length, 0, params);
          for (var j = 0; j < parents.length; j++) {
            parentCache[parents[j].get("organizationId")] = parents[j].id;
          }
        } catch (err) {}
      }
    }

    for (var i = 0; i < rows.length; i++) {
      var pid = normalizeOrgId(rows[i].get("parentOrganizationId"));
      rows[i].set("parentOrganization", parentCache[pid] || "");
      app.save(rows[i]);
    }

    if (rows.length < 200) break;
    offset += 200;
  }
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
  if (logger) logger.info("Attaching patron scope", "patronOrgId", patronOrgId);
  
  var scope = resolveParentLibrary(app, patronOrgId, { staffAuth: staffAuth, logger: logger });
  if (scope && scope.libraryOrgId) {
    patron.PatronOrgID = patronOrgId;
    patron.LibraryOrgID = scope.libraryOrgId;
    patron.LibraryOrgName = scope.libraryOrgName;
    if (logger) logger.info("Patron scope resolved", "libraryOrgId", patron.LibraryOrgID);
  } else {
    if (logger) logger.warn("Patron scope NOT resolved", "patronOrgId", patronOrgId);
  }

  var pickupBranchId = normalizeOrgId(
    patron.PreferredPickupBranchID ||
    patron.preferredPickupBranchId ||
    patron.RequestPickupBranchID ||
    patron.PatronOrgID ||
    patron.patronOrgId ||
    "0"
  );
  if (pickupBranchId) {
    patron.PreferredPickupBranchID = pickupBranchId;
    if (pickupBranchId === "0") {
      patron.PreferredPickupBranchName = "Patron registered branch";
      return patron;
    }

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
      patron.PreferredPickupBranchName = String(pickupOrg.get("displayName") || pickupOrg.get("name") || pickupBranchId);
    } else {
      patron.PreferredPickupBranchName = "Branch ID: " + pickupBranchId;
    }
  }

  return patron;
}

function pickupBranchDisplayName(app, pickupBranchId, staffAuth, logger) {
  pickupBranchId = normalizeOrgId(pickupBranchId || "0");
  if (!pickupBranchId || pickupBranchId === "0") {
    return "Patron registered branch";
  }

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
    return String(pickupOrg.get("displayName") || pickupOrg.get("name") || pickupBranchId);
  }
  return "Branch ID: " + pickupBranchId;
}

module.exports = {
  attachPatronScope: attachPatronScope,
  normalizeOrgId: normalizeOrgId,
  pickupBranchDisplayName: pickupBranchDisplayName,
  resolveParentLibrary: resolveParentLibrary,
  setSyncStatus: setSyncStatus,
  syncOrganizations: syncOrganizations,
};
