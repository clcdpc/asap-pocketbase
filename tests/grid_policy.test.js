const assert = require("assert");
const path = require("path");
const { pathToFileURL } = require("url");

async function runTests() {
  const moduleUrl = pathToFileURL(path.resolve(__dirname, "../pb_public/staff/js/grid-policy.mjs")).href;
  const policy = await import(moduleUrl);

  const labels = policy.getFilterableLabelsForRow({
    status: "suggestion",
    bibid: "12345",
    autohold: false,
    isbnCheckStatus: "not_found",
    workflowTags: [
      "Identifier number not found",
      "Identifier found",
      "Identifier found",
      "91",
      "Hold failed"
    ]
  });

  assert.deepStrictEqual(labels, [
    "Hold failed",
    "Identifier number not found in system",
    "Identifier found",
    "No hold requested"
  ]);

  const counts = policy.tagCountsForRecords([
    { workflowTags: ["Identifier found"] },
    { workflowTags: ["Identifier number found", "Hold failed"] },
    { autohold: false, workflowTags: [] }
  ]);

  assert.deepStrictEqual(counts.map(([label, count]) => [label, count]), [
    ["Hold failed", 1],
    ["Identifier found", 2],
    ["No hold requested", 1]
  ]);

  console.log("Grid policy tests passed.");
}

runTests().catch((error) => {
  console.error(error);
  process.exit(1);
});
