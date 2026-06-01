const config = require("../config.js");
const polaris = require("../polaris.js");

const COLLECTION = "polaris_pickup_branch_cache";
const PICKUP_BRANCH_CACHE_TTL_MS = 24 * 60 * 60 * 1000;

function normalizePatronOrgId(patronOrgId) {
  return String(patronOrgId || "").trim();
}

function decodeJsonValue(value) {
  if (!value) return [];
  if (Array.isArray(value)) return value;
  if (typeof value === "string") {
    try { return JSON.parse(value); } catch (err) { return []; }
  }
  if (typeof value === "object" && value !== null && value.constructor && (value.constructor.name === "Uint8Array" || value.constructor.name === "Array")) {
    var str = "";
    for (var i = 0; i < value.length; i++) str += String.fromCharCode(value[i]);
    try { return JSON.parse(str); } catch (err) { return []; }
  }
  return value;
}

function normalizeBranchList(branches) {
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
    out.push({ id: id, label: label || ("Branch " + id) });
  }
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

function readBranches(record) {
  return normalizeBranchList(record && record.get ? record.get("branches") : record && record.branches);
}

function saveCacheRecord(app, record, patronOrgId, branches, now) {
  if (!record) {
    record = new Record(app.findCollectionByNameOrId(COLLECTION));
    record.set("patronOrgId", patronOrgId);
  }
  record.set("branches", normalizeBranchList(branches));
  record.set("refreshedAt", (now || new Date()).toISOString());
  record.set("sourceKey", sourceKeyFor(app, patronOrgId));
  app.save(record);
  return record;
}

function getCachedPickupBranches(app, staff, patronOrgId, options) {
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
    return readBranches(record);
  }

  var branches = polaris.getPickupBranches(staff, patronOrgId);
  saveCacheRecord(app, record, patronOrgId, branches, now);
  return normalizeBranchList(branches);
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
  invalidatePickupBranchCache: invalidatePickupBranchCache,
  isPickupBranchCacheFresh: isPickupBranchCacheFresh,
  normalizeBranchList: normalizeBranchList,
};
