/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

function rel(name, collection, options) {
  options = options || {};
  options.collectionId = collection.id;
  options.maxSelect = options.maxSelect || 1;
  return field(name, "relation", options);
}

function duplicateLabelsFromUi(row) {
  return {
    suggestion: row.get("duplicateLabelSuggestion") || "",
    outstanding_purchase: row.get("duplicateLabelOutstandingPurchase") || "",
    pending_hold: row.get("duplicateLabelPendingHold") || "",
    hold_placed: row.get("duplicateLabelHoldPlaced") || "",
    closed: row.get("duplicateLabelClosed") || "",
    rejected: row.get("duplicateLabelRejected") || "",
    hold_completed: row.get("duplicateLabelHoldCompleted") || "",
    hold_not_picked_up: row.get("duplicateLabelHoldNotPickedUp") || "",
    manual: row.get("duplicateLabelManual") || "",
    silent: row.get("duplicateLabelSilent") || "",
    "Silently Closed": row.get("duplicateLabelSilent") || ""
  };
}

function hasLabels(labels) {
  var keys = Object.keys(labels || {});
  for (var i = 0; i < keys.length; i++) {
    if (String(labels[keys[i]] || "").trim()) return true;
  }
  return false;
}

migrate((app) => {
  const organizations = app.findCollectionByNameOrId("polaris_organizations");

  let collection;
  try {
    collection = app.findCollectionByNameOrId("patron_library_settings");
  } catch (err) {
    collection = new Collection({
      type: "base",
      name: "patron_library_settings",
      listRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
      viewRule: "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'",
      fields: [
        rel("libraryOrganization", organizations, { required: true }),
        field("duplicateRequestStatusLabels", "json")
      ],
      indexes: ["CREATE UNIQUE INDEX idx_patron_library_settings_org ON patron_library_settings (libraryOrganization)"]
    });
    app.save(collection);
  }

  try {
    const rows = app.findRecordsByFilter("ui_settings", "scope = 'library' && libraryOrganization != ''", "", 1000, 0);
    rows.forEach(function (row) {
      var org = row.get("libraryOrganization");
      var orgId = Array.isArray(org) ? org[0] : org;
      if (!orgId) return;
      var labels = duplicateLabelsFromUi(row);
      if (!hasLabels(labels)) return;
      try {
        app.findFirstRecordByFilter("patron_library_settings", "libraryOrganization = {:org}", { org: orgId });
        return;
      } catch (err) {}
      var record = new Record(collection);
      record.set("libraryOrganization", orgId);
      record.set("duplicateRequestStatusLabels", labels);
      app.save(record);
    });
  } catch (err2) {}
}, (app) => {
  try {
    const collection = app.findCollectionByNameOrId("patron_library_settings");
    app.delete(collection);
  } catch (err) {}
});
