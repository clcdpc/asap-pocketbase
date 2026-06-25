const dbHelpers = require("../db_helpers.js");
const orgIdForSettings = dbHelpers.orgIdForSettings;

function materialFormats(app, orgId) {
  app = app || $app;
  var labels = {};
  var rules = {};
  var available = [];
  var formatOrderList = [];
  try {
    var systemRows = app.findRecordsByFilter("material_formats", "scope = 'system'", "sortOrder", 200, 0, {});
    var libraryRows = [];
    var orgRecordId = orgIdForSettings(app, orgId);
    if (orgRecordId) {
      libraryRows = app.findRecordsByFilter("material_formats", "scope = 'library' && libraryOrganization = {:org}", "sortOrder", 200, 0, { org: orgRecordId });
    }
    var mergedByCode = {};
    var sourceOrderByCode = {};

    function assignSourceOrder(code, row, sourcePriority) {
      var sortOrder = row.getInt("sortOrder") || 0;
      var label = String(row.get("label") || code || "").toLowerCase();
      sourceOrderByCode[code] = { sortOrder: sortOrder, sourcePriority: sourcePriority, label: label, code: code };
    }

    function upsertRow(row, isOverride) {
      var code = String(row.get("code") || "").trim();
      if (!code) return;
      if (!mergedByCode[code]) mergedByCode[code] = { code: code };
      var target = mergedByCode[code];
      if (!isOverride || row.get("label")) target.label = row.get("label") || code;
      target.enabled = row.getBool("enabled");
      target.messageBehavior = row.get("messageBehavior") || "none";
      target.titleMode = row.get("titleMode") || "required";
      target.titleLabel = row.get("titleLabel") || "Title";
      target.authorMode = row.get("authorMode") || "required";
      target.authorLabel = row.get("authorLabel") || "Author";
      target.identifierMode = row.get("identifierMode") || "optional";
      target.identifierLabel = row.get("identifierLabel") || "Identifier number";
      target.publicationMode = row.get("publicationMode") || "required";
      target.publicationLabel = row.get("publicationLabel") || "Publication Timing";
      target.sortOrder = row.getInt("sortOrder") || 0;
      assignSourceOrder(code, row, isOverride ? 1 : 0);
    }

    for (var i = 0; i < systemRows.length; i++) upsertRow(systemRows[i], false);
    for (var j = 0; j < libraryRows.length; j++) upsertRow(libraryRows[j], true);

    var effectiveRows = Object.keys(mergedByCode).map(function (code) {
      return mergedByCode[code];
    });
    effectiveRows.sort(function (a, b) {
      if (a.sortOrder !== b.sortOrder) return a.sortOrder - b.sortOrder;
      var ao = sourceOrderByCode[a.code];
      var bo = sourceOrderByCode[b.code];
      if (ao && bo && ao.sourcePriority !== bo.sourcePriority) return ao.sourcePriority - bo.sourcePriority;
      var aLabel = String(a.label || a.code || "").toLowerCase();
      var bLabel = String(b.label || b.code || "").toLowerCase();
      if (aLabel < bLabel) return -1;
      if (aLabel > bLabel) return 1;
      return String(a.code).localeCompare(String(b.code));
    });

    for (var k = 0; k < effectiveRows.length; k++) {
      var r = effectiveRows[k];
      var code = r.code;
      formatOrderList.push(code);
      labels[code] = r.label || code;

      if (r.enabled) available.push(code);
      rules[code] = {
        messageBehavior: r.messageBehavior,
        fields: {
          title: { mode: r.titleMode, label: r.titleLabel },
          author: { mode: r.authorMode, label: r.authorLabel },
          identifier: { mode: r.identifierMode, label: r.identifierLabel },
          publication: { mode: r.publicationMode, label: r.publicationLabel }
        }
      };
    }
  } catch (err) {
    if (typeof $app !== "undefined" && $app && $app.logger) {
      $app.logger().warn("Swallowed error", "error", String(err));
    }
  }
  return { labels: labels, rules: rules, available: available, order: formatOrderList };
}

module.exports = { materialFormats };
