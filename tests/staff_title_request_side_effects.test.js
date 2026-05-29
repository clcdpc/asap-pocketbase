const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const modulePath = path.resolve(__dirname, "../lib/staff/title_request_side_effects.js");
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
    set(key, value) {
      this.data[key] = value;
    },
    getBool(key) {
      return this.data[key] === true;
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
      return { warn() {}, error() {} };
    }
  };
}

function recordsMock() {
  const STATUS = {
    SUGGESTION: "suggestion",
    PENDING_HOLD: "pending_hold",
    HOLD_PLACED: "hold_placed",
    OUTSTANDING_PURCHASE: "outstanding_purchase",
    CLOSED: "closed"
  };
  return {
    STATUS,
    normalizeStatus(value) {
      return STATUS[String(value).toUpperCase()] || String(value || "suggestion");
    },
    appendSystemNote() {}
  };
}

function loadSideEffects(overrides) {
  const mocks = Object.assign({
    routeUtils: {
      noteSkippedEmail() {},
      appendQuery(url, params) {
        return url + "?stage=" + params.stage + "&request=" + params.request;
      },
      staffRequestUrl(app, record) {
        return "https://asap.example.org/staff/?request=" + record.id;
      }
    },
    records: recordsMock(),
    config: {
      staffUrl() {
        return "https://asap.example.org/staff/";
      }
    },
    polaris: {
      adminStaffAuth() {
        return { token: "admin" };
      },
      lookupPatron() {
        return { PatronID: "patron1" };
      },
      reconcileRecord() {},
      placeHold() {}
    },
    mail: {
      alreadyOwned() { return true; },
      rejected() { return true; },
      purchaseReminder() { return true; },
      additionalCopyReminder() { return true; }
    },
    jobs: { promoteRequestNow() {} },
    bibActions: { staffActionPolarisAuth() {} }
  }, overrides || {});

  require.cache[routeUtilsPath] = { id: routeUtilsPath, filename: routeUtilsPath, loaded: true, exports: mocks.routeUtils };
  require.cache[recordsPath] = { id: recordsPath, filename: recordsPath, loaded: true, exports: mocks.records };
  require.cache[configPath] = { id: configPath, filename: configPath, loaded: true, exports: mocks.config };
  require.cache[polarisPath] = { id: polarisPath, filename: polarisPath, loaded: true, exports: mocks.polaris };
  require.cache[mailPath] = { id: mailPath, filename: mailPath, loaded: true, exports: mocks.mail };
  require.cache[jobsPath] = { id: jobsPath, filename: jobsPath, loaded: true, exports: mocks.jobs };
  require.cache[bibActionsPath] = { id: bibActionsPath, filename: bibActionsPath, loaded: true, exports: mocks.bibActions };
  delete require.cache[modulePath];
  return require(modulePath);
}

function makePurchaseContext(status) {
  return {
    action: "purchase",
    nextStatus: status,
    data: { bibid: "12345", emailPurchaseReminder: true },
    record: makeRecord({ id: "req1", status, barcode: "b1", autohold: true }),
    staff: makeStaff({ id: "staff1", username: "tester", weekly_action_summary_email: "staff@example.org" })
  };
}

function testPurchaseReminderSentForOutstandingPurchase() {
  const calls = { purchaseReminder: 0 };
  const sideEffects = loadSideEffects({
    mail: {
      alreadyOwned() { return true; },
      rejected() { return true; },
      additionalCopyReminder() { return true; },
      purchaseReminder(app, record, staff, toEmail, itemUrl) {
        calls.purchaseReminder += 1;
        assert.strictEqual(record.get("status"), "outstanding_purchase");
        assert.strictEqual(toEmail, "staff@example.org");
        assert.ok(itemUrl.includes("req1"));
        return true;
      }
    }
  });

  const result = sideEffects.sendPurchaseReminderIfRequested(makeApp(), makePurchaseContext("outstanding_purchase"));

  assert.strictEqual(calls.purchaseReminder, 1);
  assert.deepStrictEqual(result, {
    requested: true,
    sent: true,
    message: "Purchase saved and reminder email sent."
  });
}

function testPurchaseReminderSuppressedForPendingHold() {
  const calls = { purchaseReminder: 0 };
  const sideEffects = loadSideEffects({
    mail: {
      alreadyOwned() { return true; },
      rejected() { return true; },
      additionalCopyReminder() { return true; },
      purchaseReminder() { calls.purchaseReminder += 1; return true; }
    }
  });

  const result = sideEffects.sendPurchaseReminderIfRequested(makeApp(), makePurchaseContext("pending_hold"));

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.deepStrictEqual(result, {
    requested: false,
    sent: false,
    message: "Purchase reminder not sent because this request skipped the purchase queue."
  });
}

function testPurchaseReminderSuppressedForHoldPlaced() {
  const calls = { purchaseReminder: 0 };
  const sideEffects = loadSideEffects({
    mail: {
      alreadyOwned() { return true; },
      rejected() { return true; },
      additionalCopyReminder() { return true; },
      purchaseReminder() { calls.purchaseReminder += 1; return true; }
    }
  });

  const result = sideEffects.sendPurchaseReminderIfRequested(makeApp(), makePurchaseContext("hold_placed"));

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(result.requested, false);
  assert.strictEqual(result.sent, false);
  assert.strictEqual(result.message, "Purchase reminder not sent because this request skipped the purchase queue.");
}

function testPurchaseReminderSuppressedForClosed() {
  const calls = { purchaseReminder: 0 };
  const sideEffects = loadSideEffects({
    mail: {
      alreadyOwned() { return true; },
      rejected() { return true; },
      additionalCopyReminder() { return true; },
      purchaseReminder() { calls.purchaseReminder += 1; return true; }
    }
  });

  const result = sideEffects.sendPurchaseReminderIfRequested(makeApp(), makePurchaseContext("closed"));

  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(result.requested, false);
  assert.strictEqual(result.sent, false);
}

function testPurchasePendingHoldDoesNotSendPatronOutcomeEmail() {
  const calls = { alreadyOwned: 0, rejected: 0, placeHold: 0 };
  const sideEffects = loadSideEffects({
    polaris: {
      adminStaffAuth() { return { token: "admin" }; },
      lookupPatron() { return { PatronID: "patron1" }; },
      reconcileRecord() {},
      placeHold() { calls.placeHold += 1; }
    },
    mail: {
      alreadyOwned() { calls.alreadyOwned += 1; return true; },
      rejected() { calls.rejected += 1; return true; },
      additionalCopyReminder() { return true; },
      purchaseReminder() { return true; }
    }
  });

  sideEffects.handleAlreadyOwnOrRejectSideEffects(makeApp(), makePurchaseContext("pending_hold"));

  assert.strictEqual(calls.alreadyOwned, 0);
  assert.strictEqual(calls.rejected, 0);
  assert.strictEqual(calls.placeHold, 0);
}

function testPurchaseHoldPlacedSendsSinglePatronOutcomeEmail() {
  const calls = { alreadyOwned: 0, purchaseReminder: 0, rejected: 0, placeHold: 0 };
  const sideEffects = loadSideEffects({
    polaris: {
      adminStaffAuth() { return { token: "admin" }; },
      lookupPatron() { return { PatronID: "patron1" }; },
      reconcileRecord() {},
      placeHold() { calls.placeHold += 1; }
    },
    mail: {
      alreadyOwned(app, record, patron) {
        calls.alreadyOwned += 1;
        assert.strictEqual(record.get("status"), "hold_placed");
        assert.strictEqual(patron.PatronID, "patron1");
        return true;
      },
      rejected() { calls.rejected += 1; return true; },
      additionalCopyReminder() { return true; },
      purchaseReminder() { calls.purchaseReminder += 1; return true; }
    }
  });

  const app = makeApp();
  const context = makePurchaseContext("hold_placed");
  sideEffects.handleAlreadyOwnOrRejectSideEffects(app, context);
  const reminder = sideEffects.sendPurchaseReminderIfRequested(app, context);

  assert.strictEqual(calls.alreadyOwned, 1);
  assert.strictEqual(calls.rejected, 0);
  assert.strictEqual(calls.placeHold, 0);
  assert.strictEqual(calls.purchaseReminder, 0);
  assert.strictEqual(reminder.requested, false);
}

const originalCaches = {
  modulePath: require.cache[modulePath],
  routeUtilsPath: require.cache[routeUtilsPath],
  recordsPath: require.cache[recordsPath],
  configPath: require.cache[configPath],
  polarisPath: require.cache[polarisPath],
  mailPath: require.cache[mailPath],
  jobsPath: require.cache[jobsPath],
  bibActionsPath: require.cache[bibActionsPath]
};

try {
  testPurchaseReminderSentForOutstandingPurchase();
  testPurchaseReminderSuppressedForPendingHold();
  testPurchaseReminderSuppressedForHoldPlaced();
  testPurchaseReminderSuppressedForClosed();
  testPurchasePendingHoldDoesNotSendPatronOutcomeEmail();
  testPurchaseHoldPlacedSendsSinglePatronOutcomeEmail();
  console.log("staff_title_request_side_effects.test.js passed.");
} finally {
  for (const [key, cacheEntry] of Object.entries(originalCaches)) {
    const target = key === "modulePath" ? modulePath
      : key === "routeUtilsPath" ? routeUtilsPath
      : key === "recordsPath" ? recordsPath
      : key === "configPath" ? configPath
      : key === "polarisPath" ? polarisPath
      : key === "mailPath" ? mailPath
      : key === "jobsPath" ? jobsPath
      : bibActionsPath;
    if (cacheEntry) require.cache[target] = cacheEntry;
    else delete require.cache[target];
  }
}
