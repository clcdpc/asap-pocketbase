const config = require("../config.js");
const polaris = require("../polaris.js");
const helpers = require("./helpers.js");
const normalization = require("../config/normalization.js");

const COLLECTION = "polaris_pickup_branch_cache";
const PICKUP_BRANCH_CACHE_TTL_MS = 24 * 60 * 60 * 1000;

function normalizePatronOrgId(patronOrgId) {
  var raw = helpers.normalizePolarisId(patronOrgId);
  if (!raw) return "";
  if (!/^\d+$/.test(raw)) return "";
  return raw;
}

function decodeJsonValue(value) {
  return normalization.parseJsonArray(value, []);
}

function normalizeBranchList(branches, app) {
  branches = decodeJsonValue(branches);
  if (!Array.isArray(branches)) return [];
  var seen = {};
  var out = [];
  for (var i = 0; i < branches.length; i++) {
    var row = branches[i] || {};
    var id = String(row.id || row.OrganizationID || row.OrgID || row.PickupBranchID || "").trim();
    var label = String(row.label || row.DisplayName || row.OrganizationName || row.BranchName || "").trim();
    if (!id || seen[id]) continue;
    seen[id] = true;

    if (app && (!label || label === id || label.toLowerCase() === "branch " + id)) {
      try {
        var org = app.findFirstRecordByData("polaris_organizations", "organizationId", id);
        if (org) {
          label = String(org.get("displayName") || org.get("name") || label).trim();
        }
      } catch (err) {
        // Ignore DB lookup errors
      }
    }

    out.push({ id: id, label: label || ("Branch " + id) });
  }

  out.sort(function (a, b) {
    return String(a.label || "").localeCompare(String(b.label || ""));
  });

  return out;
}

function isPickupBranchCacheFresh(record, now) {
  if (!record) return false;
  now = now || new Date();
  var refreshedAt = record.get ? record.get("refreshedAt") : record.refreshedAt;
  if (!refreshedAt) return false;
  var refreshedTime = new Date(refreshedAt).getTime();
  if (!refreshedTime) return false;
  return now.getTime() - refreshedTime < PICKUP_BRANCH_CACHE_TTL_MS;
}

function sourceKeyFor(app, patronOrgId) {
  var c = config.polaris(app);
  return [c.host, c.langId, c.appId, patronOrgId].join("|");
}

function findCacheRecord(app, patronOrgId) {
  try {
    return app.findFirstRecordByFilter(COLLECTION, "patronOrgId = {:patronOrgId}", { patronOrgId: patronOrgId });
  } catch (err) {
    return null;
  }
}

function readBranches(record, app) {
  return normalizeBranchList(record && record.get ? record.get("branches") : record && record.branches, app);
}

function readRefreshedAt(record) {
  if (!record) return "";
  return String(record.get ? record.get("refreshedAt") : record.refreshedAt || "");
}

function saveCacheRecord(app, record, patronOrgId, branches, now) {
  if (!record) {
    record = new Record(app.findCollectionByNameOrId(COLLECTION));
    record.set("patronOrgId", patronOrgId);
  }
  record.set("branches", normalizeBranchList(branches, app));
  record.set("refreshedAt", (now || new Date()).toISOString());
  record.set("sourceKey", sourceKeyFor(app, patronOrgId));
  app.save(record);
  return record;
}

function getCachedPickupBranches(app, staff, patronOrgId, options) {
  return getCachedPickupBranchesWithMeta(app, staff, patronOrgId, options).branches;
}

function getCachedPickupBranchesWithMeta(app, staff, patronOrgId, options) {
  options = options || {};
  patronOrgId = normalizePatronOrgId(patronOrgId);
  if (!patronOrgId) {
    throw new Error("Missing patronOrgId for pickup branch cache lookup");
  }

  var now = options.now || new Date();
  var record = findCacheRecord(app, patronOrgId);
  var expectedSourceKey = sourceKeyFor(app, patronOrgId);
  var currentSourceKey = record && record.get ? record.get("sourceKey") : record && record.sourceKey;
  var sourceMatches = !currentSourceKey || currentSourceKey === expectedSourceKey;

  if (!options.forceRefresh && sourceMatches && isPickupBranchCacheFresh(record, now)) {
    return {
      branches: readBranches(record, app),
      refreshedAt: readRefreshedAt(record),
      fromCache: true
    };
  }

  var branches = polaris.getPickupBranches(staff, patronOrgId);
  record = saveCacheRecord(app, record, patronOrgId, branches, now);
  return {
    branches: normalizeBranchList(branches, app),
    refreshedAt: readRefreshedAt(record),
    fromCache: false
  };
}

function invalidatePickupBranchCache(app, patronOrgId) {
  patronOrgId = normalizePatronOrgId(patronOrgId);
  if (!patronOrgId) {
    throw new Error("Missing patronOrgId for pickup branch cache invalidation");
  }
  var record = findCacheRecord(app, patronOrgId);
  if (!record) return false;
  app.delete(record);
  return true;
}

module.exports = {
  PICKUP_BRANCH_CACHE_TTL_MS: PICKUP_BRANCH_CACHE_TTL_MS,
  getCachedPickupBranches: getCachedPickupBranches,
  getCachedPickupBranchesWithMeta: getCachedPickupBranchesWithMeta,
  invalidatePickupBranchCache: invalidatePickupBranchCache,
  isPickupBranchCacheFresh: isPickupBranchCacheFresh,
  normalizeBranchList: normalizeBranchList,
};
