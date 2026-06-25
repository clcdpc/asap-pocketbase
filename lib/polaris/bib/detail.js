const helpers = require("../helpers.js");
const { bibRows, firstBibValue, firstBibValueMatching, bibTitleRowAllowed } = require("./parse.js");
const { getMaterialTypeDetails, getBibFormatLabel, formatMaterialIconUrl } = require("./material-types.js");

function getBib(app, staff, bibId) {
  var ep = helpers.endpoint("public", "bib/" + encodeURIComponent(bibId));
  var payload = helpers.send("GET", ep, "", staff);
  var rows = bibRows(payload);

  var primaryTomId = String(payload.PrimaryTypeOfMaterial || "").trim();
  var materialType = getMaterialTypeDetails(app, primaryTomId);
  var formatLabel = getBibFormatLabel(app, payload, firstBibValue(rows, [17], ["Format"]));

  return {
    bibId: String(bibId || "").trim(),
    title: firstBibValueMatching(rows, [35], ["Title"], bibTitleRowAllowed),
    author: firstBibValue(rows, [18], ["Author"]),
    series: firstBibValue(rows, [19, 830], ["Series"]),
    format: formatLabel,
    primaryTomId: primaryTomId,
    formatIconUrl: materialType ? formatMaterialIconUrl(app, materialType) : "",
    formatIconAlt: materialType ? materialType.description : formatLabel,
    identifier: firstBibValue(rows, [6, 24, 48], ["ISBN", "ISSN", "UPC"]),
    publisher: firstBibValue(rows, [2], ["Publisher", "Publisher, Date"]),
    description: firstBibValue(rows, [3], ["Description"])
  };
}

function mergeCatalogValue(catalogValue, oldValue) {
  var catalog = String(catalogValue || "").trim();
  var old = String(oldValue || "").trim();
  if (!catalog) return old;
  if (!old || old === catalog) return catalog;
  if (old.indexOf(catalog + " (") === 0) return old;

  var oldBase = old.replace(/\s+\([^()]*\)\s*$/, "").trim();
  if (oldBase && (oldBase === catalog || oldBase.indexOf(catalog) === 0 || catalog.indexOf(oldBase) === 0)) {
    return old;
  }

  return catalog + " (" + old + ")";
}

function reconcileRecord(app, staff, record, bibId, selectedPolarisResult) {
  if (!bibId) return;
  try {
    var bibInfo = getBib(app, staff, bibId);
    if (bibInfo) {
      var oldTitle = String(record.get("title") || "").trim();
      var oldAuthor = String(record.get("author") || "").trim();
      var pTitle = String(bibInfo.title || "").trim();
      var pAuthor = String(bibInfo.author || "").trim();
      var selectedBibId = String(selectedPolarisResult && selectedPolarisResult.bibId || "").trim();
      var selectedTitle = String(selectedPolarisResult && selectedPolarisResult.title || "").trim();
      var selectedMatchesBib = selectedBibId && selectedBibId === String(bibId || "").trim();

      if (selectedMatchesBib && selectedTitle && !pTitle) {
        pTitle = selectedTitle;
      } else if (selectedMatchesBib && selectedTitle && pTitle && selectedTitle !== pTitle) {
        if (app && app.logger) {
          app.logger().warn("Selected Polaris title differs from BIB detail title; preserving selected title", "bibId", bibId, "selectedTitle", selectedTitle, "detailTitle", pTitle);
        }
        pTitle = selectedTitle;
      }

      var mergedTitle = mergeCatalogValue(pTitle, oldTitle);
      if (mergedTitle && mergedTitle !== oldTitle) {
        record.set("title", mergedTitle);
      }
      var mergedAuthor = mergeCatalogValue(pAuthor, oldAuthor);
      if (mergedAuthor && mergedAuthor !== oldAuthor) {
        record.set("author", mergedAuthor);
      }
    }
  } catch (err) {
    if (app && app.logger) {
      app.logger().warn("Reconciliation failed", "bibId", bibId, "error", String(err));
    }
  }
}

module.exports = { getBib, mergeCatalogValue, reconcileRecord };
