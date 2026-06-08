function createHoldCheckResult() {
  return {
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
  };
}

function createOrgSyncResult() {
  return { orgsSynced: 0, errors: 0 };
}

function createIsbnCheckResult() {
  return createHoldCheckResult();
}

module.exports = {
  createHoldCheckResult: createHoldCheckResult,
  createOrgSyncResult: createOrgSyncResult,
  createIsbnCheckResult: createIsbnCheckResult,
};
