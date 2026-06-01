const pickupCache = require("./pickup_branch_cache.js");
const helpers = require("./helpers.js");

function normalizeId(value) {
  var raw = helpers.normalizePolarisId(value);
  if (!raw) return "";
  if (!/^\d+$/.test(raw)) return "";
  return raw;
}

function findBranch(branches, id) {
  id = normalizeId(id);
  if (!id) return null;
  branches = Array.isArray(branches) ? branches : [];
  for (var i = 0; i < branches.length; i++) {
    var branch = branches[i] || {};
    if (normalizeId(branch.id) === id) return { id: normalizeId(branch.id), label: String(branch.label || "").trim() };
  }
  return null;
}

function currentPreferredId(patron) {
  patron = patron || {};
  return normalizeId(
    patron.CurrentPreferredPickupBranchID ||
    patron.RequestPickupBranchID ||
    patron.PreferredPickupBranchID ||
    ""
  );
}

function appendUnique(list, value) {
  value = normalizeId(value);
  if (!value) return;
  for (var i = 0; i < list.length; i++) {
    if (list[i] === value) return;
  }
  list.push(value);
}

function loadBranches(app, staffAuth, patronOrgIds, options) {
  options = options || {};
  patronOrgIds = Array.isArray(patronOrgIds) ? patronOrgIds : [patronOrgIds];
  for (var i = 0; i < patronOrgIds.length; i++) {
    var candidateOrgId = normalizeId(patronOrgIds[i]);
    if (!candidateOrgId) continue;
    try {
      var cacheResult = pickupCache.getCachedPickupBranchesWithMeta(app, staffAuth, candidateOrgId, options);
      var branches = Array.isArray(cacheResult && cacheResult.branches) ? cacheResult.branches : [];
      if (branches.length) {
        return {
          branches: branches,
          refreshedAt: String((cacheResult && cacheResult.refreshedAt) || "")
        };
      }
    } catch (err) {
      // Try the next org candidate before surfacing a higher-level warning.
    }
  }
  return {
    branches: [],
    refreshedAt: ""
  };
}

function buildPickupPreferenceContext(app, staffAuth, patron, options) {
  options = options || {};
  patron = patron || {};

  var patronOrgId = normalizeId(patron.PatronOrgID);
  var libraryOrgId = normalizeId(patron.LibraryOrgID);
  if (!patronOrgId && !libraryOrgId) {
    return {
      pickupBranches: [],
      pickupBranchesRefreshedAt: "",
      currentPreferredPickupBranchId: "",
      currentPreferredPickupBranchName: "",
      selectedPickupBranchId: "",
      selectedPickupBranchName: "",
      currentPreferenceAllowed: false,
      pickupBranchWarning: "Pickup locations could not be loaded because the patron registered library is missing."
    };
  }

  var orgCandidates = [];
  appendUnique(orgCandidates, patronOrgId);
  try {
    var c = helpers.cfg(app);
    // pickupOrgId is a Polaris-specific override for pickup-branch operations when set.
    if (normalizeId(c.pickupOrgId) && normalizeId(c.pickupOrgId) !== "0") appendUnique(orgCandidates, c.pickupOrgId);
    appendUnique(orgCandidates, c.orgId);
  } catch (err) {
    // Ignore config resolution errors; fallback candidates below still apply.
  }
  appendUnique(orgCandidates, libraryOrgId);
  var cacheResult = loadBranches(app, staffAuth, orgCandidates, options);
  var branches = cacheResult.branches;
  var refreshedAt = cacheResult.refreshedAt || "";
  var currentId = currentPreferredId(patron);
  var currentName = String(patron.PreferredPickupBranchName || "").trim();

  if (app && currentId && (!currentName || currentName === currentId || currentName.toLowerCase() === "branch " + currentId)) {
    try {
      var orgsLib = require("../orgs.js");
      currentName = orgsLib.pickupBranchDisplayName(app, currentId, staffAuth);
    } catch (err) {
      // Ignore resolution errors
    }
  }

  var selected = findBranch(branches, currentId);

  var warning = "";
  if (!branches.length) {
    warning = "Pickup locations could not be loaded for this patron. Refresh pickup locations or try again later.";
  } else if (!selected) {
    warning = currentId
      ? "Your current Polaris preferred pickup location is not available for this form. Please choose a pickup location before submitting."
      : "Choose a preferred pickup location before submitting.";
  }

  return {
    pickupBranches: branches,
    pickupBranchesRefreshedAt: refreshedAt,
    currentPreferredPickupBranchId: currentId,
    currentPreferredPickupBranchName: selected ? selected.label : currentName,
    selectedPickupBranchId: selected ? selected.id : "",
    selectedPickupBranchName: selected ? selected.label : "",
    currentPreferenceAllowed: !!selected,
    pickupBranchWarning: warning
  };
}

function buildAvailablePickupPreferenceContext(app, staffAuth, patron, options) {
  options = options || {};
  var pickupContext = buildPickupPreferenceContext(app, staffAuth, patron, options);

  if (!options.forceRefresh && !(pickupContext.pickupBranches || []).length) {
    var forceOptions = Object.assign({}, options, { forceRefresh: true });
    pickupContext = buildPickupPreferenceContext(app, staffAuth, patron, forceOptions);
  }

  return pickupContext;
}

function validateSelectedPickupBranch(context, selectedId) {
  selectedId = normalizeId(selectedId);
  if (!selectedId) {
    var missing = new Error("Preferred pickup location is required.");
    missing.code = 400;
    throw missing;
  }
  var selected = findBranch((context && context.pickupBranches) || [], selectedId);
  if (!selected) {
    var invalid = new Error("Selected pickup location is not available for this patron.");
    invalid.code = 400;
    throw invalid;
  }
  return selected;
}

module.exports = {
  buildAvailablePickupPreferenceContext: buildAvailablePickupPreferenceContext,
  buildPickupPreferenceContext: buildPickupPreferenceContext,
  validateSelectedPickupBranch: validateSelectedPickupBranch,
  findBranch: findBranch,
  currentPreferredId: currentPreferredId,
};
