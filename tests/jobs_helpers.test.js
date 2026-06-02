const assert = require("assert");
const helpers = require("../lib/jobs/helpers.js");
const records = require("../lib/records.js");

console.log("Running tests for lib/jobs/helpers.js...");

const originalAddTag = records.addWorkflowTagForRequest;
const originalAppendNote = records.appendSystemNote;

let tagsAdded = [];
let notesAppended = [];

records.addWorkflowTagForRequest = (app, record, tag) => {
  tagsAdded.push({app, record, tag});
};

records.appendSystemNote = (record, note) => {
  notesAppended.push({record, note});
};

// flagMultiplePolarisMatches - Does nothing when bibResult is falsy
tagsAdded = [];
notesAppended = [];
helpers.flagMultiplePolarisMatches({}, {}, null);
assert.strictEqual(tagsAdded.length, 0);
assert.strictEqual(notesAppended.length, 0);

// flagMultiplePolarisMatches - Does nothing when bibResult.multipleMatches is falsy
tagsAdded = [];
notesAppended = [];
helpers.flagMultiplePolarisMatches({}, {}, { multipleMatches: false });
assert.strictEqual(tagsAdded.length, 0);
assert.strictEqual(notesAppended.length, 0);

// flagMultiplePolarisMatches - Adds tag and note when bibResult.multipleMatches is truthy
tagsAdded = [];
notesAppended = [];
const mockApp = { name: "app" };
const mockRecord = { id: "123" };
const mockBibResult = { multipleMatches: true };

helpers.flagMultiplePolarisMatches(mockApp, mockRecord, mockBibResult);

assert.strictEqual(tagsAdded.length, 1);
assert.strictEqual(tagsAdded[0].app, mockApp);
assert.strictEqual(tagsAdded[0].record, mockRecord);
assert.strictEqual(tagsAdded[0].tag, helpers.POLARIS_TAG_MULTIPLE_MATCHES);

assert.strictEqual(notesAppended.length, 1);
assert.strictEqual(notesAppended[0].record, mockRecord);
assert.strictEqual(notesAppended[0].note, helpers.POLARIS_MULTIPLE_MATCH_NOTE);

// mapIsbnCheckSuggestion - Returns found tag for found status
assert.strictEqual(helpers.mapIsbnCheckSuggestion("found"), helpers.POLARIS_TAG_FOUND);

// mapIsbnCheckSuggestion - Returns not found tag for not_found status
assert.strictEqual(helpers.mapIsbnCheckSuggestion("not_found"), helpers.POLARIS_TAG_NOT_FOUND);

// mapIsbnCheckSuggestion - Returns empty string for other statuses
assert.strictEqual(helpers.mapIsbnCheckSuggestion("other"), "");
assert.strictEqual(helpers.mapIsbnCheckSuggestion(null), "");
assert.strictEqual(helpers.mapIsbnCheckSuggestion(undefined), "");

records.addWorkflowTagForRequest = originalAddTag;
records.appendSystemNote = originalAppendNote;

console.log("jobs_helpers.test.js passed.");
