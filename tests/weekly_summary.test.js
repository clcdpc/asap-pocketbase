const assert = require("assert");

global.__hooks = __dirname + "/../pb_hooks";

const jobs = require("../lib/jobs.js");

class MockRecord {
  constructor(data) {
    this.data = data;
  }
  get(key) {
    return this.data[key];
  }
}

function makeApp(rows, options = {}) {
  const additionalCopies = options.additionalCopies || [];
  return {
    findCollectionByNameOrId(collection) {
      if (collection === "system_settings" && options.staffUrl !== undefined) return { name: collection };
      throw new Error("not found");
    },
    findRecordById(collection, id) {
      if (collection === "system_settings" && id === "settings0000001" && options.staffUrl !== undefined) {
        return new MockRecord({ staffUrl: options.staffUrl });
      }
      throw new Error("not found");
    },
    findRecordsByFilter(collection, filter, sort, limit, offset, params) {
      if (collection === "additional_copy_requests") {
        let matches = additionalCopies.filter(row => row.status === "open");
        matches = matches.sort((a, b) => String(b.created).localeCompare(String(a.created)));
        return matches.slice(offset, offset + limit).map(row => new MockRecord(row));
      }
      assert.strictEqual(collection, "title_requests");
      const status = params.status;
      let matches = rows.filter(row => row.status === status);
      if (sort === "-created") {
        matches = matches.sort((a, b) => String(b.created).localeCompare(String(a.created)));
      }
      if (sort === "-updated") {
        matches = matches.sort((a, b) => String(b.updated).localeCompare(String(a.updated)));
      }
      return matches.slice(offset, offset + limit).map(row => new MockRecord(row));
    }
  };
}

const rows = [
  { status: "suggestion", title: "Newest", author: "A", created: "2026-04-30 10:00:00", updated: "2026-04-30 10:00:00" },
  { status: "suggestion", title: "Second", author: "B", created: "2026-04-29 10:00:00", updated: "2026-04-29 10:00:00" },
  { status: "closed", title: "Closed", author: "C", created: "2026-04-30 11:00:00", updated: "2026-04-30 11:00:00" },
  { status: "outstanding_purchase", title: "Needs Bib", author: "D", bibid: "", created: "2026-04-28 10:00:00", updated: "2026-04-30 09:00:00" },
  { status: "outstanding_purchase", title: "Already Bibbed", author: "E", bibid: "12345", created: "2026-04-27 10:00:00", updated: "2026-04-30 08:00:00" },
];

const summary = jobs.buildWeeklyStaffActionSummary(makeApp(rows));

assert.strictEqual(summary.newSubmissionsCount, 2);
assert.deepStrictEqual(summary.newSubmissionSample.map(item => item.title), ["Newest", "Second"]);
assert.strictEqual(summary.purchasesWithoutBibsCount, 1);
assert.deepStrictEqual(summary.purchasesWithoutBibsSample.map(item => item.title), ["Needs Bib"]);
assert.strictEqual(summary.additionalCopiesCount, 0);
assert.ok(summary.newSubmissionsUrl.endsWith("/staff/?stage=submitted"));
assert.ok(summary.purchasesWithoutBibsUrl.endsWith("/staff/?stage=purchased_waiting_for_bib"));
assert.ok(summary.additionalCopiesUrl.endsWith("/staff/?stage=additional_copies"));

const additionalSummary = jobs.buildWeeklyStaffActionSummary(makeApp(rows, {
  additionalCopies: [
    { status: "open", title: "Extra Two", author: "H", bibid: "222", created: "2026-05-02 10:00:00" },
    { status: "closed", title: "Closed Extra", author: "I", bibid: "333", created: "2026-05-03 10:00:00" },
    { status: "open", title: "Extra One", author: "G", bibid: "111", created: "2026-05-04 10:00:00" },
  ]
}));
assert.strictEqual(additionalSummary.additionalCopiesCount, 2);
assert.deepStrictEqual(additionalSummary.additionalCopiesSample.map(item => item.title), ["Extra One", "Extra Two"]);

const configuredSummary = jobs.buildWeeklyStaffActionSummary(makeApp(rows, { staffUrl: "https://asap.example.org/staff/" }));
assert.strictEqual(configuredSummary.newSubmissionsUrl, "https://asap.example.org/staff/?stage=submitted");
assert.strictEqual(configuredSummary.purchasesWithoutBibsUrl, "https://asap.example.org/staff/?stage=purchased_waiting_for_bib");

const noTrailingSlashSummary = jobs.buildWeeklyStaffActionSummary(makeApp(rows, { staffUrl: "http://localhost:8090/staff" }));
assert.strictEqual(noTrailingSlashSummary.newSubmissionsUrl, "http://localhost:8090/staff/?stage=submitted");

console.log("Weekly staff action summary tests passed.");
