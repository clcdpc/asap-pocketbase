const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const sideEffectsPath = path.resolve(__dirname, "../lib/staff/title_request_side_effects.js");
const routeUtilsPath = path.resolve(__dirname, "../lib/route_utils.js");
const recordsPath = path.resolve(__dirname, "../lib/records.js");
const configPath = path.resolve(__dirname, "../lib/config.js");
const polarisPath = path.resolve(__dirname, "../lib/polaris.js");
const mailPath = path.resolve(__dirname, "../lib/mail.js");
const jobsPath = path.resolve(__dirname, "../lib/jobs.js");
const bibActionsPath = path.resolve(__dirname, "../lib/staff/title_request_bib_actions.js");

function makeRecord(fields) {
  return {
    id: fields.id || "req1",
    data: Object.assign({}, fields),
    get(key) {
      return this.data[key];
    },
    getBool(key) {
      return !!this.data[key];
    },
    set(key, value) {
      this.data[key] = value;
    }
  };
}

function makeStaff(fields) {
  return {
    id: fields.id || "staff1",
    get(key) {
      return fields[key];
    }
  };
}

function makeApp() {
  return {
    saveCalls: [],
    save(record) {
      this.saveCalls.push(record);
    },
    logger() {
      return { error() {}, warn() {} };
    }
  };
}

function withSideEffects(calls) {
  calls.purchaseReminder = 0;
  calls.additionalCopyReminder = 0;
  calls.alreadyOwned = 0;
  calls.rejected = 0;
  calls.holdPlaced = 0;
  calls.lookupPatron = 0;
  calls.placeHold = 0;
  calls.reconcileRecord = 0;

  const recordsMock = {
    STATUS: {
      SUGGESTION: "suggestion",
      PENDING_HOLD: "pending_hold",
      HOLD_PLACED: "hold_placed",
      OUTSTANDING_PURCHASE: "outstanding_purchase",
      CLOSED: "closed"
    },
    normalizeStatus(value) {
      const map = {
        pending_hold: "pending_hold",
        hold_placed: "hold_placed",
        outstanding_purchase: "outstanding_purchase",
        closed: "closed",
        suggestion: "suggestion"
      };
      return map[String(value)] || "suggestion";
    },
    appendSystemNote() {}
  };

  const mailMock = {
    purchaseReminder() {
      calls.purchaseReminder += 1;
      return true;
    },
    additionalCopyReminder() {
      calls.additionalCopyReminder += 1;
      return true;
    },
    alreadyOwned() {
      calls.alreadyOwned += 1;
      return true;
    },
    rejected() {
      calls.rejected += 1;
      return true;
    },
    holdPlaced() {
      calls.holdPlaced += 1;
      return true;
    }
  };

  require.cache[routeUtilsPath] = { id: routeUtilsPath, filename: routeUtilsPath, loaded: true, exports: {
    appendQuery(base) { return base + "?stage=additional_copies&request=req1"; },
    staffRequestUrl(app, record) { return "https://asap.example.org/staff/?stage=" + record.get("status") + "&request=" + record.id; },
    noteSkippedEmail() {}
  } };
  require.cache[recordsPath] = { id: recordsPath, filename: recordsPath, loaded: true, exports: recordsMock };
  require.cache[configPath] = { id: configPath, filename: configPath, loaded: true, exports: {
    staffUrl() { return "https://asap.example.org/staff/"; },
    suggestionLimit() { return { autoPromote: false }; }
  } };
  require.cache[polarisPath] = { id: polarisPath, filename: polarisPath, loaded: true, exports: {
    adminStaffAuth() { return { token: "staff" }; },
    lookupPatron() { calls.lookupPatron += 1; return { PatronID: "patron1", EmailAddress: "patron@example.org" }; },
    placeHold() { calls.placeHold += 1; return true; },
    reconcileRecord() { calls.reconcileRecord += 1; }
  } };
  require.cache[mailPath] = { id: mailPath, filename: mailPath, loaded: true, exports: mailMock };
  require.cache[jobsPath] = { id: jobsPath, filename: jobsPath, loaded: true, exports: { promoteRequestNow() {} } };
  require.cache[bibActionsPath] = { id: bibActionsPath, filename: bibActionsPath, loaded: true, exports: { staffActionPolarisAuth() { return { token: "staff" }; } } };

  delete require.cache[sideEffectsPath];
  return require(sideEffectsPath);
}

function testPurchaseToOutstandingPurchaseSendsReminder() {
  const calls = {};
  const sideEffects = withSideEffects(calls);
  const result = sideEffects.sendPurchaseReminderIfRequested(makeApp(), {
    action: "purchase",
    data: { emailPurchaseReminder: true },
    record: makeRecord({ id: "req1", status: "outstanding_purchase" }),
    staff: makeStaff({ weekly_action_summary_email: "selector@example.org" })
  });

  assert.strictEqual(calls.purchaseReminder, 1);
  assert.strictEqual(result.requested, true);
  assert.strictEqual(result.sent, true);
  assert.strictEqual(result.message, "Purchase saved and reminder email sent.");
}

function testPurchaseToPendingHoldSuppressesReminderAndPatronEmail() {
  const calls = {};
  const sideEffects = withSideEffects(calls);
  const app = makeApp();
  const context = {
    action: "purchase",
    data: { emailPurchaseReminder: true, bibid: "12345" },
    record: makeRecord({ id: "req1", status: "pending_hold", barcode: "b1" }),
    staff: makeStaff({ weekly_action_summary_email: "selector@example.org" })
  };

  const result = sideEffects.sendPurchaseReminderIfRequested(app, context);
  sideEffects.handleStaffActionPatronEmailSideEffects(app, context);

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(calls.additionalCopyReminder, 0);
  assert.strictEqual(calls.alreadyOwned, 0);
  assert.strictEqual(calls.rejected, 0);
  assert.strictEqual(calls.holdPlaced, 0);
  assert.strictEqual(calls.lookupPatron, 0);
  assert.strictEqual(result.requested, true);
  assert.strictEqual(result.sent, false);
  assert.strictEqual(result.message, "Purchase reminder not sent because this request skipped the purchase queue.");
}

function testPurchaseToPendingHoldUncheckedReminderStaysQuiet() {
  const calls = {};
  const sideEffects = withSideEffects(calls);
  const result = sideEffects.sendPurchaseReminderIfRequested(makeApp(), {
    action: "purchase",
    data: { emailPurchaseReminder: false, bibid: "12345" },
    record: makeRecord({ id: "req1", status: "pending_hold", barcode: "b1" }),
    staff: makeStaff({ weekly_action_summary_email: "selector@example.org" })
  });

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(result.requested, false);
  assert.strictEqual(result.sent, false);
  assert.strictEqual(result.message, "");
}

function testPurchaseToHoldPlacedSuppressesReminderAndSendsOnePatronOutcomeEmail() {
  const calls = {};
  const sideEffects = withSideEffects(calls);
  const app = makeApp();
  const context = {
    action: "purchase",
    data: { emailPurchaseReminder: true, bibid: "12345" },
    record: makeRecord({ id: "req1", status: "hold_placed", barcode: "b1" }),
    staff: makeStaff({ weekly_action_summary_email: "selector@example.org" })
  };

  const result = sideEffects.sendPurchaseReminderIfRequested(app, context);
  sideEffects.handleStaffActionPatronEmailSideEffects(app, context);

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(calls.additionalCopyReminder, 0);
  assert.strictEqual(calls.alreadyOwned, 1);
  assert.strictEqual(calls.rejected, 0);
  assert.strictEqual(calls.holdPlaced, 0);
  assert.strictEqual(calls.placeHold, 0);
  assert.strictEqual(calls.lookupPatron, 1);
  assert.strictEqual(result.requested, true);
  assert.strictEqual(result.sent, false);
  assert.strictEqual(result.message, "Purchase reminder not sent because this request skipped the purchase queue.");
}

function testPurchaseToClosedSuppressesReminder() {
  const calls = {};
  const sideEffects = withSideEffects(calls);
  const result = sideEffects.sendPurchaseReminderIfRequested(makeApp(), {
    action: "purchase",
    data: { emailPurchaseReminder: true },
    record: makeRecord({ id: "req1", status: "closed" }),
    staff: makeStaff({ weekly_action_summary_email: "selector@example.org" })
  });

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(result.requested, true);
  assert.strictEqual(result.sent, false);
  assert.strictEqual(result.message, "Purchase reminder not sent because this request skipped the purchase queue.");
}

function testAdditionalCopyReminderStillSendsForHoldWorkflowStatus() {
  const calls = {};
  const sideEffects = withSideEffects(calls);
  const result = sideEffects.sendPurchaseReminderIfRequested(makeApp(), {
    action: "additionalCopy",
    data: { emailPurchaseReminder: true },
    record: makeRecord({ id: "req1", status: "pending_hold" }),
    staff: makeStaff({ weekly_action_summary_email: "selector@example.org" })
  });

  assert.strictEqual(calls.additionalCopyReminder, 1);
  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(result.requested, true);
  assert.strictEqual(result.sent, true);
  assert.strictEqual(result.message, "Additional copy saved and reminder email sent.");
}

const originalCaches = {
  sideEffectsPath: require.cache[sideEffectsPath],
  routeUtilsPath: require.cache[routeUtilsPath],
  recordsPath: require.cache[recordsPath],
  configPath: require.cache[configPath],
  polarisPath: require.cache[polarisPath],
  mailPath: require.cache[mailPath],
  jobsPath: require.cache[jobsPath],
  bibActionsPath: require.cache[bibActionsPath]
};

try {
  testPurchaseToOutstandingPurchaseSendsReminder();
  testPurchaseToPendingHoldSuppressesReminderAndPatronEmail();
  testPurchaseToPendingHoldUncheckedReminderStaysQuiet();
  testPurchaseToHoldPlacedSuppressesReminderAndSendsOnePatronOutcomeEmail();
  testPurchaseToClosedSuppressesReminder();
  testAdditionalCopyReminderStillSendsForHoldWorkflowStatus();
  console.log("staff_title_request_side_effects.test.js passed.");
} finally {
  const cachePaths = {
    sideEffectsPath,
    routeUtilsPath,
    recordsPath,
    configPath,
    polarisPath,
    mailPath,
    jobsPath,
    bibActionsPath
  };
  Object.keys(originalCaches).forEach((key) => {
    const cachePath = cachePaths[key];
    if (originalCaches[key]) require.cache[cachePath] = originalCaches[key];
    else delete require.cache[cachePath];
  });
}
