const assert = require("assert");
const path = require("path");

const root = path.resolve(__dirname, "..");
global.__hooks = path.join(root, "pb_hooks");

const sideEffectsPath = path.join(root, "lib/staff/title_request_side_effects.js");
const recordsPath = path.join(root, "lib/records.js");
const routeUtilsPath = path.join(root, "lib/route_utils.js");
const configPath = path.join(root, "lib/config.js");
const polarisPath = path.join(root, "lib/polaris.js");
const mailPath = path.join(root, "lib/mail.js");
const jobsPath = path.join(root, "lib/jobs.js");
const bibActionsPath = path.join(root, "lib/staff/title_request_bib_actions.js");

const mockedPaths = [
  sideEffectsPath,
  recordsPath,
  routeUtilsPath,
  configPath,
  polarisPath,
  mailPath,
  jobsPath,
  bibActionsPath
];
const originalCaches = mockedPaths.map((modulePath) => ({
  modulePath,
  cacheEntry: require.cache[modulePath]
}));

function restoreCache() {
  originalCaches.forEach(({ modulePath, cacheEntry }) => {
    if (cacheEntry) require.cache[modulePath] = cacheEntry;
    else delete require.cache[modulePath];
  });
}

function makeRecord(status) {
  return {
    id: "req1",
    data: { status, barcode: "2001", bibid: "bib1" },
    get(key) {
      return this.data[key];
    },
    set(key, value) {
      this.data[key] = value;
    },
    getBool(key) {
      return !!this.data[key];
    }
  };
}

function makeStaff(email) {
  return {
    id: "staff1",
    get(key) {
      if (key === "weekly_action_summary_email") return email;
      if (key === "username") return "tester";
      return "";
    }
  };
}

function makeApp() {
  return {
    logger() {
      return { warn() {}, error() {}, info() {} };
    },
    save() {}
  };
}

function loadSideEffects(calls) {
  delete require.cache[sideEffectsPath];
  require.cache[recordsPath] = { id: recordsPath, filename: recordsPath, loaded: true, exports: {
    STATUS: {
      OUTSTANDING_PURCHASE: "outstanding_purchase",
      PENDING_HOLD: "pending_hold",
      HOLD_PLACED: "hold_placed",
      CLOSED: "closed",
      SUGGESTION: "suggestion"
    },
    normalizeStatus(value) { return String(value || "").trim(); },
    appendSystemNote() {}
  } };
  require.cache[routeUtilsPath] = { id: routeUtilsPath, filename: routeUtilsPath, loaded: true, exports: {
    staffRequestUrl(app, record) { return "https://staff.example/requests/" + record.id; },
    appendQuery(url) { return url + "?stage=additional_copies"; },
    noteSkippedEmail() { calls.noteSkippedEmail += 1; }
  } };
  require.cache[configPath] = { id: configPath, filename: configPath, loaded: true, exports: {
    staffUrl() { return "https://staff.example/"; },
    suggestionLimit() { return { autoPromote: false }; }
  } };
  require.cache[polarisPath] = { id: polarisPath, filename: polarisPath, loaded: true, exports: {
    adminStaffAuth() { return { token: "admin" }; },
    lookupPatron() { calls.lookupPatron += 1; return { PatronID: "patron1" }; },
    reconcileRecord() { calls.reconcileRecord += 1; },
    placeHold() { calls.placeHold += 1; }
  } };
  require.cache[mailPath] = { id: mailPath, filename: mailPath, loaded: true, exports: {
    purchaseReminder() { calls.purchaseReminder += 1; return true; },
    additionalCopyReminder() { calls.additionalCopyReminder += 1; return true; },
    alreadyOwned() { calls.alreadyOwned += 1; return true; },
    rejected() { calls.rejected += 1; return true; }
  } };
  require.cache[jobsPath] = { id: jobsPath, filename: jobsPath, loaded: true, exports: {
    promoteRequestNow() { calls.promoteRequestNow += 1; }
  } };
  require.cache[bibActionsPath] = { id: bibActionsPath, filename: bibActionsPath, loaded: true, exports: {
    staffActionPolarisAuth() { return { token: "admin" }; }
  } };
  return require(sideEffectsPath);
}

function makeCalls() {
  return {
    purchaseReminder: 0,
    additionalCopyReminder: 0,
    alreadyOwned: 0,
    rejected: 0,
    lookupPatron: 0,
    reconcileRecord: 0,
    placeHold: 0,
    noteSkippedEmail: 0,
    promoteRequestNow: 0
  };
}

function purchaseContext(status, emailPurchaseReminder) {
  return {
    action: "purchase",
    nextStatus: "pending_hold",
    data: { emailPurchaseReminder, bibid: "bib1" },
    record: makeRecord(status),
    staff: makeStaff("selector@example.org")
  };
}

function testPurchaseReminderSentOnlyForOutstandingPurchase() {
  const calls = makeCalls();
  const sideEffects = loadSideEffects(calls);
  const result = sideEffects.sendPurchaseReminderIfRequested(makeApp(), purchaseContext("outstanding_purchase", true));

  assert.strictEqual(calls.purchaseReminder, 1);
  assert.strictEqual(result.requested, true);
  assert.strictEqual(result.sent, true);
  assert.strictEqual(result.message, "Purchase saved and reminder email sent.");
}

function testPurchaseReminderSuppressedForPendingHold() {
  const calls = makeCalls();
  const sideEffects = loadSideEffects(calls);
  const result = sideEffects.sendPurchaseReminderIfRequested(makeApp(), purchaseContext("pending_hold", true));

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(result.requested, false);
  assert.strictEqual(result.sent, false);
  assert.strictEqual(result.message, "Purchase reminder not sent because this request skipped the purchase queue.");
}

function testPurchaseReminderSuppressedForHoldPlaced() {
  const calls = makeCalls();
  const sideEffects = loadSideEffects(calls);
  const result = sideEffects.sendPurchaseReminderIfRequested(makeApp(), purchaseContext("hold_placed", true));

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(result.requested, false);
  assert.strictEqual(result.sent, false);
}

function testPurchaseReminderSuppressedForClosed() {
  const calls = makeCalls();
  const sideEffects = loadSideEffects(calls);
  const result = sideEffects.sendPurchaseReminderIfRequested(makeApp(), purchaseContext("closed", true));

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(result.requested, false);
  assert.strictEqual(result.sent, false);
}

function testPurchaseToPendingHoldDoesNotSendPatronOutcomeEmail() {
  const calls = makeCalls();
  const sideEffects = loadSideEffects(calls);
  sideEffects.handleAlreadyOwnOrRejectSideEffects(makeApp(), purchaseContext("pending_hold", true));

  assert.strictEqual(calls.alreadyOwned, 0);
  assert.strictEqual(calls.rejected, 0);
  assert.strictEqual(calls.placeHold, 0);
}

function testPurchaseToHoldPlacedSendsOnePatronOutcomeEmail() {
  const calls = makeCalls();
  const sideEffects = loadSideEffects(calls);
  sideEffects.handleAlreadyOwnOrRejectSideEffects(makeApp(), purchaseContext("hold_placed", true));
  const reminder = sideEffects.sendPurchaseReminderIfRequested(makeApp(), purchaseContext("hold_placed", true));

  assert.strictEqual(calls.alreadyOwned, 1);
  assert.strictEqual(calls.rejected, 0);
  assert.strictEqual(calls.placeHold, 0);
  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(reminder.requested, false);
}

try {
  testPurchaseReminderSentOnlyForOutstandingPurchase();
  testPurchaseReminderSuppressedForPendingHold();
  testPurchaseReminderSuppressedForHoldPlaced();
  testPurchaseReminderSuppressedForClosed();
  testPurchaseToPendingHoldDoesNotSendPatronOutcomeEmail();
  testPurchaseToHoldPlacedSendsOnePatronOutcomeEmail();
  console.log("staff_title_request_side_effects.test.js passed.");
} finally {
  restoreCache();
}
