const helpers = require("../helpers.js");
const auth = require("../auth.js");
const config = require("../../config.js");
const normalization = require("../../config/normalization.js");
const { padMaterialTypeId } = require("./parse.js");

const PRIMARY_TOM_LABELS = {
  "1": "Book",
  "3": "Periodical",
  "10": "Sound Recording",
  "14": "Musical Score",
  "15": "Map",
  "19": "Computer File",
  "33": "DVD",
  "36": "eBook",
  "37": "Audio Book",
  "41": "eAudiobook",
  "52": "Audio Book on CD",
  "53": "Large Print"
};

function getMARCTypeOfMaterialRows(staff) {
  var ep = helpers.endpoint("public", "marctypeofmaterials");
  var payload = helpers.send("GET", ep, "", staff || null);
  var rows = helpers.normalizeRows(payload.MARCTypeOfMaterialsRows || payload, "", "MARCTypeOfMaterialsRow");
  var out = {};
  for (var i = 0; i < rows.length; i++) {
    var row = rows[i] || {};
    var id = String(row.MARCTypeOfMaterialID || row.TypeOfMaterialID || row.ID || "").trim();
    var desc = String(row.Description || "").trim();
    var searchCode = String(row.SearchCode || "").trim();
    if (id && desc) {
      out[id] = {
        id: id,
        id2: padMaterialTypeId(id),
        searchCode: searchCode,
        description: desc
      };
    }
  }
  return out;
}

function formatMaterialIconUrl(app, materialType) {
  var pattern = config.formatIconUrlPattern(app);
  if (!pattern) return "";
  return pattern
    .replace(/\{MARCTypeOfMaterialID\}/g, materialType.id)
    .replace(/\{MARCTypeOfMaterialID2\}/g, materialType.id2)
    .replace(/\{id\}/g, materialType.id)
    .replace(/\{id2\}/g, materialType.id2)
    .replace(/\{SearchCode\}/g, encodeURIComponent(materialType.searchCode || ""));
}

function normalizeMaterialTypesCache(cached) {
  if (!cached) return null;
  var data = normalization.parseJsonObject(cached, null);

  if (!data || typeof data !== "object") return null;

  // v2 shape: { version: 2, rows: { "1": { ... } } }
  if (data.version === 2 && data.rows && typeof data.rows === "object") {
    return data.rows;
  }

  // v1 shape: { "1": "Book" }
  var rows = {};
  Object.keys(data).forEach(function(id) {
    if (typeof data[id] === "string") {
      rows[id] = {
        id: id,
        id2: padMaterialTypeId(id),
        searchCode: "",
        description: data[id]
      };
    } else if (data[id] && typeof data[id] === "object") {
      rows[id] = data[id];
    }
  });
  return Object.keys(rows).length > 0 ? rows : null;
}

var _materialTypeDetailsMap = null;
var _materialTypeDetailsLastCheck = 0;

function getMaterialTypeDetailsMap(app) {
  var now = new Date();
  if (_materialTypeDetailsMap && (now.getTime() - _materialTypeDetailsLastCheck < 60000)) {
    return _materialTypeDetailsMap;
  }
  _materialTypeDetailsLastCheck = now.getTime();

  app = app || $app;
  var settings = null;
  try {
    settings = app.findRecordById("polaris_settings", "polaris00000010");
  } catch (err) {
    return {};
  }

  var cached = settings.get("materialTypesCache");
  var lastUpdated = settings.get("materialTypesCacheUpdated");
  var expirationMs = 24 * 60 * 60 * 1000; // 24 hours

  var isExpired = !lastUpdated || (now.getTime() - new Date(lastUpdated).getTime() > expirationMs);
  var rows = normalizeMaterialTypesCache(cached);

  if (rows && !isExpired) {
    _materialTypeDetailsMap = rows;
    return rows;
  }

  try {
    var fetched = getMARCTypeOfMaterialRows(auth.adminStaffAuth());
    if (fetched && Object.keys(fetched).length > 0) {
      settings.set("materialTypesCache", {
        version: 2,
        rows: fetched
      });
      settings.set("materialTypesCacheUpdated", now.toISOString());
      app.save(settings);
      _materialTypeDetailsMap = fetched;
      return fetched;
    }
  } catch (err) {
    if (app && app.logger) {
      app.logger().warn("Failed to fetch Polaris material types", "error", String(err));
    }
  }

  return rows || {};
}

function getMaterialTypeDetails(app, id) {
  var details = getMaterialTypeDetailsMap(app);
  return details[id] || null;
}

function getMaterialTypesMap(app) {
  var details = getMaterialTypeDetailsMap(app);
  var map = {};
  Object.keys(details).forEach(function(id) {
    map[id] = details[id].description;
  });

  if (Object.keys(map).length === 0) return PRIMARY_TOM_LABELS;
  return map;
}

function getBibFormatLabel(app, row, bibGetFormat) {
  var tomDesc = String(row.MaterialTypeDescription || row.MaterialType || row.materialTypeDesc || "").trim();
  if (tomDesc && !/^\d+$/.test(tomDesc)) return tomDesc;

  var tomId = String(row.PrimaryTypeOfMaterial || row.primaryTomId || "").trim();

  var dynamicMap = getMaterialTypesMap(app);
  if (dynamicMap && dynamicMap[tomId]) return dynamicMap[tomId];

  if (PRIMARY_TOM_LABELS[tomId]) return PRIMARY_TOM_LABELS[tomId];

  return bibGetFormat || tomDesc || tomId || "Unknown";
}

module.exports = {
  getMARCTypeOfMaterialRows,
  formatMaterialIconUrl, normalizeMaterialTypesCache,
  getMaterialTypeDetailsMap, getMaterialTypeDetails,
  getMaterialTypesMap, getBibFormatLabel, PRIMARY_TOM_LABELS
};
