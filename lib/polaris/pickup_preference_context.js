const pickupCache = require("./pickup_branch_cache.js");

function normalizeId(value) {
  return String(value || "").trim();
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
  return normalizeId(patron.RequestPickupBranchID || patron.PreferredPickupBranchID || "");
}

function loadBranches(app, staffAuth, patronOrgId, options) {
  options = options || {};
  var branches = pickupCache.getCachedPickupBranches(app, staffAuth, patronOrgId, options);
  return Array.isArray(branches) ? branches : [];
}

function buildPickupPreferenceContext(app, staffAuth, patron, options) {
  options = options || {};
  patron = patron || {};

  var patronOrgId = normalizeId(patron.PatronOrgID);
  if (!patronOrgId) {
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

  var branches = loadBranches(app, staffAuth, patronOrgId, options);
  var refreshedAt = new Date().toISOString();
  var currentId = currentPreferredId(patron);
  var currentName = String(patron.PreferredPickupBranchName || "").trim();
  var selected = findBranch(branches, currentId);

  return {
    pickupBranches: branches,
    pickupBranchesRefreshedAt: refreshedAt,
    currentPreferredPickupBranchId: currentId,
    currentPreferredPickupBranchName: selected ? selected.label : currentName,
    selectedPickupBranchId: selected ? selected.id : "",
    selectedPickupBranchName: selected ? selected.label : "",
    currentPreferenceAllowed: !!selected,
    pickupBranchWarning: selected ? "" : "Your current Polaris preferred pickup location is not available for this form. Please choose a pickup location before submitting."
  };
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
  buildPickupPreferenceContext: buildPickupPreferenceContext,
  validateSelectedPickupBranch: validateSelectedPickupBranch,
  findBranch: findBranch,
  currentPreferredId: currentPreferredId,
};
