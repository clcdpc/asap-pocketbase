const assert = require("assert");
const path = require("path");
const { pathToFileURL } = require("url");

async function runTests() {
  const moduleUrl = pathToFileURL(path.resolve(__dirname, "../pb_public/staff/js/grid-row-actions.mjs")).href;
  const { buildRowActions } = await import(moduleUrl);

  const suggestion = buildRowActions({
    id: "req1",
    status: "suggestion",
    claimedByStaffUserId: "staff-1"
  }, {
    currentStatus: "suggestion",
    currentStaffId: "staff-1",
    isAdmin: false
  });

  assert.deepStrictEqual(suggestion.visible.map(action => action.label), ["Purchase", "Reject"]);
  assert.deepStrictEqual(suggestion.secondary.map(action => action.label), ["Already own", "Unclaim", "Silent close", "Edit"]);

  const pendingHold = buildRowActions({
    id: "req2",
    status: "pending_hold",
    bibid: "12345",
    workflowTags: ["Hold exists (same patron)"]
  }, {
    currentStatus: "pending_hold",
    currentStaffId: "staff-1",
    isAdmin: true
  });

  assert.deepStrictEqual(pendingHold.primary.label, "Undo");
  assert.deepStrictEqual(pendingHold.secondary.map(action => action.label), [
    "Close duplicate",
    "Buy another copy",
    "Claim",
    "Silent close",
    "Edit"
  ]);

  const additionalCopy = buildRowActions({
    id: "copy1",
    status: "open",
    type: "additional_copy"
  }, {
    currentStatus: "additional_copies",
    currentStaffId: "staff-1",
    isAdmin: true
  });

  assert.strictEqual(additionalCopy.primary.label, "Close");
  assert.deepStrictEqual(additionalCopy.secondary.map(action => action.label), ["Claim"]);

  console.log("Grid row action tests passed.");
}

runTests().catch((error) => {
  console.error(error);
  process.exit(1);
});
