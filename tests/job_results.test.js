const assert = require("assert");
const job_results = require("../lib/job_results.js");

function test(name, fn) {
  try {
    fn();
    console.log(`✅ ${name} passed`);
  } catch (err) {
    console.error(`❌ ${name} failed:`, err);
    throw err;
  }
}

console.log("Running tests for lib/job_results.js...");

  test("createHoldCheckResult returns expected object structure", () => {
    const holdCheckResult = job_results.createHoldCheckResult();
    assert.deepStrictEqual(holdCheckResult, {
      timedOut: 0,
      holdPickupTimeouts: 0,
      isbnChecksFound: 0,
      isbnChecksNotFound: 0,
      skipped: 0,
      promoted: 0,
      holdsPlaced: 0,
      checkoutClosures: 0,
      holdStatusClosures: 0,
      errors: 0,
    });
  });

test("createOrgSyncResult returns expected object structure", () => {
  const orgSyncResult = job_results.createOrgSyncResult();
  assert.deepStrictEqual(orgSyncResult, {
    orgsSynced: 0,
    errors: 0,
  });
});

  test("createIsbnCheckResult returns expected object structure", () => {
    const isbnCheckResult = job_results.createIsbnCheckResult();
    assert.deepStrictEqual(isbnCheckResult, {
      timedOut: 0,
      holdPickupTimeouts: 0,
      isbnChecksFound: 0,
      isbnChecksNotFound: 0,
      skipped: 0,
      promoted: 0,
      holdsPlaced: 0,
      checkoutClosures: 0,
      holdStatusClosures: 0,
      errors: 0,
    });
  });

console.log("\njob_results.test.js finished.");
