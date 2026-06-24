const helpers = require("../helpers.js");

function bibSearchRows(payload) {
  var rows = payload && payload.BibSearchRows ? payload.BibSearchRows : [];
  if (rows.BibSearchRow) {
    rows = rows.BibSearchRow;
  }
  if (!Array.isArray(rows)) {
    rows = rows ? [rows] : [];
  }
  return rows;
}

function bibRows(payload) {
  var rows = payload && payload.BibGetRows ? payload.BibGetRows : [];
  if (rows.BibGetRow) {
    rows = rows.BibGetRow;
  }
  if (!Array.isArray(rows)) {
    rows = rows ? [rows] : [];
  }
  return rows;
}

function firstBibValue(rows, elementIds, labels) {
  return firstBibValueMatching(rows, elementIds, labels, null);
}

function firstBibValueMatching(rows, elementIds, labels, acceptRow) {
  var idSet = {};
  var labelSet = {};

  elementIds.forEach(function(id) {
    idSet[String(id)] = true;
  });

  labels.forEach(function(label) {
    labelSet[helpers.normalizedLabel(label)] = true;
  });

  for (var i = 0; i < rows.length; i++) {
    var row = rows[i] || {};
    var value = String(row.Value || "").trim();
    if (!value) continue;

    var elementId = String(row.ElementID || "").trim();
    var label = helpers.normalizedLabel(row.Label);

    if (idSet[elementId] || labelSet[label]) {
      if (acceptRow && !acceptRow(row, value, elementId, label)) continue;
      return value;
    }
  }

  return "";
}

function bibTitleRowAllowed(row, value, elementId, label) {
  if (elementId === "830") return false;
  if (label === "series") return false;
  if (/^--/.test(String(value || "").trim())) return false;
  return true;
}

function padMaterialTypeId(id) {
  var raw = String(id || "").trim();
  if (/^\d$/.test(raw)) return "0" + raw;
  return raw;
}

module.exports = { bibSearchRows, bibRows, firstBibValue, firstBibValueMatching, bibTitleRowAllowed, padMaterialTypeId };
