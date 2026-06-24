const { getMARCTypeOfMaterialRows, normalizeMaterialTypesCache, getMaterialTypeDetailsMap, getMaterialTypeDetails, getMaterialTypesMap } = require("./bib/material-types.js");
const { normalizeBibSearchRow, searchBibs, searchBib } = require("./bib/search.js");
const { getBibHoldings, summarizeHoldability, summarizeHoldingsByLibrary, placeHold, replyToHold } = require("./bib/holdings.js");
const { getBib, reconcileRecord } = require("./bib/detail.js");

module.exports = {
  getMaterialTypeDetailsMap, getMaterialTypeDetails, getMaterialTypesMap,
  normalizeMaterialTypesCache, getMARCTypeOfMaterialRows,
  getMARCTypeOfMaterials: getMARCTypeOfMaterialRows, // backward compatibility
  normalizeBibSearchRow, searchBibs, searchBib,
  getBibHoldings, summarizeHoldability, summarizeHoldingsByLibrary,
  placeHold, replyToHold, getBib, reconcileRecord
};
