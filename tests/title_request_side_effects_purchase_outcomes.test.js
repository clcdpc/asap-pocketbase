const assert = require("assert");
const path = require("path");

const { MockRecord, createMockApp } = require("./helpers/mock_pb.js");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const sideEffectsPath = path.resolve(__dirname, "../lib/staff/title_request_side_effects.js");
const recordsPath = path.resolve(__dirname, "../lib/records.js");
const routeUtilsPath = path.resolve(__dirname, "../lib/route_utils.js");
const configPath = path.resolve(__dirname, "../lib/config.js");
const polarisPath = path.resolve(__dirname, "../lib/polaris.js");
const mailPath = path.resolve(__dirname, "../lib/mail.js");
const jobsPath = path.resolve(__dirname, "../lib/jobs.js");
const bibActionsPath = path.resolve(__dirname, "../lib/staff/title_request_bib_actions.js");

const paths = [sideEffectsPath, recordsPath, routeUtilsPath, configPath, polarisPath, mailPath, jobsPath, bibActionsPath];
const originalCaches = {};
paths.forEach((file) => { originalCaches[file] = require.cache[file]; });

function makeStaff(email) {
  return {
    id: "staff1",
    get(key) {
      if (key === "weekly_action_summary_email") return email;
      if (key === "username") return "selector";
      return "";
    }
  };
}

function makeContext(status, options) {
  options = options || {};
  return {
    action: options.action || "purchase",
    nextStatus: options.nextStatus || status,
    data: Object.assign({ emailPurchaseReminder: true, bibid: "12345" }, options.data || {}),
    record: new MockRecord({ id: "req1", status, barcode: "b1", autohold: true }),
    staff: makeStaff(options.email || "selector@example.org")
  };
}

function loadSideEffects(calls) {
  const recordsMock = {
    STATUS: {
      OUTSTANDING_PURCHASE: "outstanding_purchase",
      PENDING_HOLD: "pending_hold",
      HOLD_PLACED: "hold_placed",
      CLOSED: "closed",
      SUGGESTION: "suggestion"
    },
    normalizeStatus(value) { return String(value || "").trim(); },
    appendSystemNote() { calls.systemNotes += 1; }
  };

  require.cache[recordsPath] = { id: recordsPath, filename: recordsPath, loaded: true, exports: recordsMock };
  require.cache[routeUtilsPath] = {
    id: routeUtilsPath,
    filename: routeUtilsPath,
    loaded: true,
    exports: {
      staffRequestUrl(app, record) { return "https://asap.example.org/staff/?request=" + record.id; },
      appendQuery(base) { return base + "?stage=additional_copies&request=req1"; },
      noteSkippedEmail() { calls.skippedEmail += 1; }
    }
  };
  require.cache[configPath] = {
    id: configPath,
    filename: configPath,
    loaded: true,
    exports: {
      staffUrl() { return "https://asap.example.org/staff/"; },
      suggestionLimit() { return { autoPromote: false }; }
    }
  };
  require.cache[polarisPath] = {
    id: polarisPath,
    filename: polarisPath,
    loaded: true,
    exports: {
      adminStaffAuth() { return { token: "admin" }; },
      lookupPatron() { calls.lookupPatron += 1; return { PatronID: "patron1" }; },
      reconcileRecord() { calls.reconcile += 1; },
      placeHold() { calls.placeHold += 1; }
    }
  };
  require.cache[mailPath] = {
    id: mailPath,
    filename: mailPath,
    loaded: true,
    exports: {
      purchaseReminder() { calls.purchaseReminder += 1; return true; },
      additionalCopyReminder() { calls.additionalCopyReminder += 1; return true; },
      alreadyOwned() { calls.alreadyOwned += 1; return true; },
      rejected() { calls.rejected += 1; return true; }
    }
  };
  require.cache[jobsPath] = { id: jobsPath, filename: jobsPath, loaded: true, exports: { promoteRequestNow() {} } };
  require.cache[bibActionsPath] = { id: bibActionsPath, filename: bibActionsPath, loaded: true, exports: { staffActionPolarisAuth() {} } };

  delete require.cache[sideEffectsPath];
  return require(sideEffectsPath);
}

function newCalls() {
  return {
    purchaseReminder: 0,
    additionalCopyReminder: 0,
    alreadyOwned: 0,
    rejected: 0,
    lookupPatron: 0,
    reconcile: 0,
    placeHold: 0,
    skippedEmail: 0,
    systemNotes: 0
  };
}

function testPurchaseReminderSentForOutstandingPurchase() {
  const calls = newCalls();
  const sideEffects = loadSideEffects(calls);
  const result = sideEffects.sendPurchaseReminderIfRequested(createMockApp(), makeContext("outstanding_purchase"));

  assert.strictEqual(calls.purchaseReminder, 1);
  assert.strictEqual(result.requested, true);
  assert.strictEqual(result.sent, true);
  assert.strictEqual(result.message, "Purchase saved and reminder email sent.");
}

function testPurchaseReminderSuppressedForPendingHold() {
  const calls = newCalls();
  const sideEffects = loadSideEffects(calls);
  const result = sideEffects.sendPurchaseReminderIfRequested(createMockApp(), makeContext("pending_hold"));

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(result.requested, false);
  assert.strictEqual(result.sent, false);
  assert.strictEqual(result.message, "Purchase reminder not sent because this request skipped the purchase queue.");
}

function testPurchaseReminderSuppressedForHoldPlaced() {
  const calls = newCalls();
  const sideEffects = loadSideEffects(calls);
  const result = sideEffects.sendPurchaseReminderIfRequested(createMockApp(), makeContext("hold_placed"));

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(result.requested, false);
  assert.strictEqual(result.sent, false);
}

function testPurchaseReminderSuppressedForClosed() {
  const calls = newCalls();
  const sideEffects = loadSideEffects(calls);
  const result = sideEffects.sendPurchaseReminderIfRequested(createMockApp(), makeContext("closed"));

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(result.requested, false);
  assert.strictEqual(result.message, "Purchase reminder not sent because this request skipped the purchase queue.");
}

function testPurchaseToPendingHoldDoesNotSendPatronOutcomeEmail() {
  const calls = newCalls();
  const sideEffects = loadSideEffects(calls);
  sideEffects.handleAlreadyOwnOrRejectSideEffects(createMockApp(), makeContext("pending_hold"));

  assert.strictEqual(calls.alreadyOwned, 0);
  assert.strictEqual(calls.rejected, 0);
  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(calls.placeHold, 0);
  assert.strictEqual(calls.lookupPatron, 0);
}

function testPurchaseToHoldPlacedSendsOnePatronOutcomeEmail() {
  const calls = newCalls();
  const sideEffects = loadSideEffects(calls);
  sideEffects.handleAlreadyOwnOrRejectSideEffects(createMockApp(), makeContext("hold_placed"));

  assert.strictEqual(calls.alreadyOwned, 1);
  assert.strictEqual(calls.rejected, 0);
  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(calls.placeHold, 0);
  assert.strictEqual(calls.lookupPatron, 1);
}

try {
  testPurchaseReminderSentForOutstandingPurchase();
  testPurchaseReminderSuppressedForPendingHold();
  testPurchaseReminderSuppressedForHoldPlaced();
  testPurchaseReminderSuppressedForClosed();
  testPurchaseToPendingHoldDoesNotSendPatronOutcomeEmail();
  testPurchaseToHoldPlacedSendsOnePatronOutcomeEmail();
  console.log("title_request_side_effects_purchase_outcomes.test.js passed.");
} finally {
  paths.forEach((file) => {
    if (originalCaches[file]) require.cache[file] = originalCaches[file];
    else delete require.cache[file];
  });
}
