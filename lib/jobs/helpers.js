const records = require("../records.js");

const POLARIS_TAG_FOUND = "Identifier found";
const POLARIS_TAG_NOT_FOUND = "Identifier number not found in system";
const POLARIS_TAG_MULTIPLE_MATCHES = "Multiple Polaris matches";
const POLARIS_MULTIPLE_MATCH_NOTE = "Identifier number search found multiple Polaris matches; ASAP used the first result by publication date descending.";

function mapIsbnCheckSuggestion(status) {
  if (status === "found") {
    return POLARIS_TAG_FOUND;
  }
  if (status === "not_found") {
    return "Identifier number not found in system";
  }
  return "";
}

function flagMultiplePolarisMatches(app, record, bibResult) {
  if (!bibResult || !bibResult.multipleMatches) {
    return;
  }
  records.addWorkflowTagForRequest(app, record, POLARIS_TAG_MULTIPLE_MATCHES);
  records.appendSystemNote(record, POLARIS_MULTIPLE_MATCH_NOTE);
}

module.exports = {
  POLARIS_TAG_FOUND: POLARIS_TAG_FOUND,
  POLARIS_TAG_NOT_FOUND: POLARIS_TAG_NOT_FOUND,
  POLARIS_TAG_MULTIPLE_MATCHES: POLARIS_TAG_MULTIPLE_MATCHES,
  POLARIS_MULTIPLE_MATCH_NOTE: POLARIS_MULTIPLE_MATCH_NOTE,
  mapIsbnCheckSuggestion: mapIsbnCheckSuggestion,
  flagMultiplePolarisMatches: flagMultiplePolarisMatches,
};
