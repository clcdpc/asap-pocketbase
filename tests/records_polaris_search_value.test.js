const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";

class MockRecord {
  constructor(initial = {}) {
    this.data = { ...initial };
    this.id = initial.id || "req_1";
  }
  get(key) {
    return this.data[key];
  }
  getBool(key) {
    return !!this.data[key];
  }
}

const records = require("../lib/records.js");

assert.strictEqual(
  records.polarisSubmittedSearchValue("Angel down / Daniel Kraus. (Test Search Buttons)"),
  "Test Search Buttons"
);

assert.strictEqual(
  records.polarisSubmittedSearchValue("Kraus, Daniel, 1975- author. ()"),
  ""
);

assert.strictEqual(
  records.polarisSubmittedSearchValue("Dune (Deluxe Edition)"),
  "Dune"
);

assert.strictEqual(
  records.polarisSubmittedSearchValue("The title: a novel / Author Name."),
  "The title a novel Author Name"
);

const row = records.titleRequestToJson(new MockRecord({
  title: "Catalog title / Catalog Author. (Patron title)",
  author: "Catalog, Author, 1975- author. (Patron author)",
  status: "suggestion"
}));

assert.strictEqual(row.polarisSearchTitle, "Patron title");
assert.strictEqual(row.polarisSearchAuthor, "Patron author");

console.log("records Polaris search value tests passed.");
