const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const modulePath = path.resolve(__dirname, "../lib/staff/title_request_actions.js");
const recordsPath = path.resolve(__dirname, "../lib/records.js");
const additionalCopiesPath = path.resolve(__dirname, "../lib/additional_copies.js");
const formatClaimRulesPath = path.resolve(__dirname, "../lib/format_claim_rules.js");
const contextPath = path.resolve(__dirname, "../lib/staff/title_request_action_context.js");
const bibActionsPath = path.resolve(__dirname, "../lib/staff/title_request_bib_actions.js");
const sideEffectsPath = path.resolve(__dirname, "../lib/staff/title_request_side_effects.js");

function makeRecord(fields) {
  return {
    id: fields.id || "req1",
    data: Object.assign({}, fields),
    get(key) {
      return this.data[key];
    },
    set(key, value) {
      this.data[key] = value;
    }
  };
}

function makeStaff(fields) {
  return {
    id: fields.id,
    get(key) {
      return fields[key];
    },
    getBool(key) {
      return !!fields[key];
    }
  };
}

function makeEvent() {
  return {
    app: {
      saveCalls: [],
      findRecordById() {
        return null;
      },
      save(record) {
        this.saveCalls.push(record);
      },
      logger() {
        return { info() {}, error() {} };
      }
    },
    request: {
      pathValue() {
        return "req1";
      }
    },
    json(code, payload) {
      return { code, payload };
    }
  };
}

function loadWithMocks(mocks) {
  require.cache[recordsPath] = { id: recordsPath, filename: recordsPath, loaded: true, exports: mocks.records };
  require.cache[additionalCopiesPath] = { id: additionalCopiesPath, filename: additionalCopiesPath, loaded: true, exports: mocks.additionalCopies };
  require.cache[formatClaimRulesPath] = { id: formatClaimRulesPath, filename: formatClaimRulesPath, loaded: true, exports: mocks.formatClaimRules };
  require.cache[contextPath] = { id: contextPath, filename: contextPath, loaded: true, exports: mocks.contextModule };
  require.cache[bibActionsPath] = { id: bibActionsPath, filename: bibActionsPath, loaded: true, exports: mocks.bibActions };
  require.cache[sideEffectsPath] = { id: sideEffectsPath, filename: sideEffectsPath, loaded: true, exports: mocks.sideEffects };
  delete require.cache[modulePath];
  return require(modulePath);
}

function testPendingHoldRequiresBibId() {
  const calls = { prepareBibAction: 0, updateTitleRequest: 0 };
  const staff = makeStaff({ username: "tester" });
  const record = makeRecord({ id: "req1", status: "new" });

  const recordsMock = {
    STATUS: { PENDING_HOLD: "pending_hold" },
    updateTitleRequest() {
      calls.updateTitleRequest += 1;
      return record;
    },
    titleRequestToJson() {
      return { id: "req1" };
    }
  };

  const context = {
    response: null,
    id: "req1",
    action: "purchase",
    nextStatus: "pending_hold",
    data: { bibid: "" },
    record,
    staff,
    formatChanged: false,
    originalFormat: "book"
  };

  const { staffTitleRequestAction } = loadWithMocks({
    records: recordsMock,
    additionalCopies: { createFromTitleRequest() { throw new Error("not expected"); }, toJson() { return {}; } },
    formatClaimRules: { applyFormatClaimRule() {} },
    contextModule: { titleRequestActionContext() { return context; } },
    bibActions: {
      finalizeTitleRequestCloseReason() {},
      prepareTitleRequestBibAction() {
        calls.prepareBibAction += 1;
        return null;
      }
    },
    sideEffects: {
      handleAlreadyOwnOrRejectSideEffects() {},
      sendPurchaseReminderIfRequested() { return null; },
      maybeRunImmediatePromoter() {}
    }
  });

  const result = staffTitleRequestAction(makeEvent());
  assert.strictEqual(result.code, 400);
  assert.strictEqual(result.payload.message, "BIB ID is required before moving this suggestion to Pending hold.");
  assert.strictEqual(calls.prepareBibAction, 0);
  assert.strictEqual(calls.updateTitleRequest, 0);
}

function testBibActionShortCircuits() {
  const calls = { updateTitleRequest: 0, sideEffects: 0 };
  const staff = makeStaff({ username: "tester" });
  const record = makeRecord({ id: "req1", status: "new" });

  const recordsMock = {
    STATUS: { PENDING_HOLD: "pending_hold" },
    updateTitleRequest() {
      calls.updateTitleRequest += 1;
      return record;
    },
    titleRequestToJson() {
      return { id: "req1" };
    }
  };

  const context = {
    response: null,
    id: "req1",
    action: "purchase",
    nextStatus: "new",
    data: { bibid: "12345" },
    record,
    staff,
    formatChanged: false,
    originalFormat: "book"
  };

  const { staffTitleRequestAction } = loadWithMocks({
    records: recordsMock,
    additionalCopies: { createFromTitleRequest() { throw new Error("not expected"); }, toJson() { return {}; } },
    formatClaimRules: { applyFormatClaimRule() {} },
    contextModule: { titleRequestActionContext() { return context; } },
    bibActions: {
      finalizeTitleRequestCloseReason() {},
      prepareTitleRequestBibAction(e) {
        return e.json(409, { message: "conflict" });
      }
    },
    sideEffects: {
      handleAlreadyOwnOrRejectSideEffects() {
        calls.sideEffects += 1;
      },
      sendPurchaseReminderIfRequested() {
        calls.sideEffects += 1;
        return null;
      },
      maybeRunImmediatePromoter() {
        calls.sideEffects += 1;
      }
    }
  });

  const result = staffTitleRequestAction(makeEvent());
  assert.strictEqual(result.code, 409);
  assert.strictEqual(result.payload.message, "conflict");
  assert.strictEqual(calls.updateTitleRequest, 0);
  assert.strictEqual(calls.sideEffects, 0);
}

function testAutoClaimUnclaimedSuggestion() {
  const calls = { setManualClaim: 0, save: 0, recordEvent: 0, updateTitleRequest: 0, sideEffects: 0 };
  const staff = makeStaff({ id: "staff1", username: "tester", displayName: "Tester" });
  const record = makeRecord({ id: "req1", status: "new", claimedByStaffUserId: "" });

  const recordsMock = {
    STATUS: { PENDING_HOLD: "pending_hold" },
    updateTitleRequest() {
      calls.updateTitleRequest += 1;
      return record;
    },
    titleRequestToJson(rec) {
      return { id: "req1", claimedByStaffUserId: rec.get("claimedByStaffUserId") };
    },
    recordEvent(app, rec, eventType, message, meta) {
      calls.recordEvent += 1;
      assert.strictEqual(eventType, "claim_manual_assigned");
    }
  };

  const context = {
    response: null,
    id: "req1",
    action: "purchase",
    nextStatus: "new",
    data: {},
    record,
    staff,
    formatChanged: false,
    originalFormat: "book"
  };

  const { staffTitleRequestAction } = loadWithMocks({
    records: recordsMock,
    additionalCopies: { createFromTitleRequest() { throw new Error("not expected"); }, toJson() { return {}; } },
    formatClaimRules: {
      applyFormatClaimRule() {},
      setManualClaim(rec, stf) {
        calls.setManualClaim += 1;
        rec.set("claimedByStaffUserId", stf.get("id"));
      },
      claimDisplayName() { return "Tester"; }
    },
    contextModule: { titleRequestActionContext() { return context; } },
    bibActions: {
      finalizeTitleRequestCloseReason() {},
      prepareTitleRequestBibAction() { return null; }
    },
    sideEffects: {
      handleAlreadyOwnOrRejectSideEffects() { calls.sideEffects += 1; },
      sendPurchaseReminderIfRequested() { calls.sideEffects += 1; return null; },
      maybeRunImmediatePromoter() { calls.sideEffects += 1; }
    }
  });

  const event = makeEvent();
  const result = staffTitleRequestAction(event);

  assert.strictEqual(result.code, 200);
  assert.strictEqual(calls.setManualClaim, 1);
  assert.strictEqual(calls.recordEvent, 1);
  assert.ok(event.app.saveCalls.includes(record));
  assert.strictEqual(result.payload.claimedByStaffUserId, "staff1");
}

function testAutoClaimTransfer() {
  const calls = { setManualClaim: 0, recordEvent: 0 };
  const staff = makeStaff({ id: "staff1", username: "tester", displayName: "Tester" });
  const record = makeRecord({ id: "req1", status: "new", claimedByStaffUserId: "staff2" });

  const recordsMock = {
    STATUS: { PENDING_HOLD: "pending_hold" },
    updateTitleRequest() { return record; },
    titleRequestToJson(rec) { return { id: "req1", claimedByStaffUserId: rec.get("claimedByStaffUserId") }; },
    recordEvent(app, rec, eventType, message, meta) {
      calls.recordEvent += 1;
      assert.strictEqual(eventType, "claim_manual_transferred");
    }
  };

  const context = {
    response: null,
    id: "req1",
    action: "purchase",
    nextStatus: "new",
    data: {},
    record,
    staff,
    formatChanged: false,
    originalFormat: "book"
  };

  const { staffTitleRequestAction } = loadWithMocks({
    records: recordsMock,
    additionalCopies: { createFromTitleRequest() { throw new Error("not expected"); }, toJson() { return {}; } },
    formatClaimRules: {
      applyFormatClaimRule() {},
      setManualClaim(rec, stf) {
        calls.setManualClaim += 1;
        rec.set("claimedByStaffUserId", stf.get("id"));
      },
      claimDisplayName() { return "Tester"; }
    },
    contextModule: { titleRequestActionContext() { return context; } },
    bibActions: {
      finalizeTitleRequestCloseReason() {},
      prepareTitleRequestBibAction() { return null; }
    },
    sideEffects: {
      handleAlreadyOwnOrRejectSideEffects() {},
      sendPurchaseReminderIfRequested() { return null; },
      maybeRunImmediatePromoter() {}
    }
  });

  const event = makeEvent();
  const result = staffTitleRequestAction(event);

  assert.strictEqual(result.code, 200);
  assert.strictEqual(calls.setManualClaim, 1);
  assert.strictEqual(calls.recordEvent, 1);
  assert.strictEqual(result.payload.claimedByStaffUserId, "staff1");
}

function testAutoClaimDoesNotDuplicate() {
  const calls = { setManualClaim: 0, recordEvent: 0 };
  const staff = makeStaff({ id: "staff1", username: "tester", displayName: "Tester" });
  const record = makeRecord({ id: "req1", status: "new", claimedByStaffUserId: "staff1" });

  const recordsMock = {
    STATUS: { PENDING_HOLD: "pending_hold" },
    updateTitleRequest() { return record; },
    titleRequestToJson(rec) { return { id: "req1", claimedByStaffUserId: rec.get("claimedByStaffUserId") }; },
    recordEvent(app, rec, eventType, message, meta) {
      calls.recordEvent += 1;
    }
  };

  const context = {
    response: null,
    id: "req1",
    action: "purchase",
    nextStatus: "new",
    data: {},
    record,
    staff,
    formatChanged: false,
    originalFormat: "book"
  };

  const { staffTitleRequestAction } = loadWithMocks({
    records: recordsMock,
    additionalCopies: { createFromTitleRequest() { throw new Error("not expected"); }, toJson() { return {}; } },
    formatClaimRules: {
      applyFormatClaimRule() {},
      setManualClaim(rec, stf) { calls.setManualClaim += 1; },
      claimDisplayName() { return "Tester"; }
    },
    contextModule: { titleRequestActionContext() { return context; } },
    bibActions: {
      finalizeTitleRequestCloseReason() {},
      prepareTitleRequestBibAction() { return null; }
    },
    sideEffects: {
      handleAlreadyOwnOrRejectSideEffects() {},
      sendPurchaseReminderIfRequested() { return null; },
      maybeRunImmediatePromoter() {}
    }
  });

  const event = makeEvent();
  const result = staffTitleRequestAction(event);

  assert.strictEqual(result.code, 200);
  assert.strictEqual(calls.setManualClaim, 0);
  assert.strictEqual(calls.recordEvent, 0); // Should not call recordEvent for claim if already claimed by actor
  assert.strictEqual(result.payload.claimedByStaffUserId, "staff1");
}

function testAutoClaimSkipsAssignmentAction() {
  const calls = { setManualClaim: 0, recordEvent: 0 };
  const staff = makeStaff({ id: "staff1", username: "tester", displayName: "Tester" });
  const record = makeRecord({ id: "req1", status: "new", claimedByStaffUserId: "" });

  const recordsMock = {
    STATUS: { PENDING_HOLD: "pending_hold" },
    updateTitleRequest() { return record; },
    titleRequestToJson(rec) { return { id: "req1", claimedByStaffUserId: rec.get("claimedByStaffUserId") }; },
    recordEvent(app, rec, eventType, message, meta) {
      calls.recordEvent += 1;
    }
  };

  const context = {
    response: null,
    id: "req1",
    action: "assign", // The action that skips
    nextStatus: "new",
    data: {},
    record,
    staff,
    formatChanged: false,
    originalFormat: "book"
  };

  const { staffTitleRequestAction } = loadWithMocks({
    records: recordsMock,
    additionalCopies: { createFromTitleRequest() { throw new Error("not expected"); }, toJson() { return {}; } },
    formatClaimRules: {
      applyFormatClaimRule() {},
      setManualClaim(rec, stf) { calls.setManualClaim += 1; },
      claimDisplayName() { return "Tester"; }
    },
    contextModule: { titleRequestActionContext() { return context; } },
    bibActions: {
      finalizeTitleRequestCloseReason() {},
      prepareTitleRequestBibAction() { return null; }
    },
    sideEffects: {
      handleAlreadyOwnOrRejectSideEffects() {},
      sendPurchaseReminderIfRequested() { return null; },
      maybeRunImmediatePromoter() {}
    }
  });

  const event = makeEvent();
  const result = staffTitleRequestAction(event);

  assert.strictEqual(result.code, 200);
  assert.strictEqual(calls.setManualClaim, 0);
  assert.strictEqual(calls.recordEvent, 0); // Skipped assignment action
  assert.strictEqual(result.payload.claimedByStaffUserId, ""); // remains empty
}

const originalCaches = {
  modulePath: require.cache[modulePath],
  recordsPath: require.cache[recordsPath],
  additionalCopiesPath: require.cache[additionalCopiesPath],
  formatClaimRulesPath: require.cache[formatClaimRulesPath],
  contextPath: require.cache[contextPath],
  bibActionsPath: require.cache[bibActionsPath],
  sideEffectsPath: require.cache[sideEffectsPath]
};

try {
  testPendingHoldRequiresBibId();
  testBibActionShortCircuits();
  testAutoClaimUnclaimedSuggestion();
  testAutoClaimTransfer();
  testAutoClaimDoesNotDuplicate();
  testAutoClaimSkipsAssignmentAction();
  console.log("staff_title_request_action_behavior.test.js passed.");
} finally {
  if (originalCaches.modulePath) require.cache[modulePath] = originalCaches.modulePath;
  else delete require.cache[modulePath];

  if (originalCaches.recordsPath) require.cache[recordsPath] = originalCaches.recordsPath;
  else delete require.cache[recordsPath];

  if (originalCaches.additionalCopiesPath) require.cache[additionalCopiesPath] = originalCaches.additionalCopiesPath;
  else delete require.cache[additionalCopiesPath];

  if (originalCaches.formatClaimRulesPath) require.cache[formatClaimRulesPath] = originalCaches.formatClaimRulesPath;
  else delete require.cache[formatClaimRulesPath];

  if (originalCaches.contextPath) require.cache[contextPath] = originalCaches.contextPath;
  else delete require.cache[contextPath];

  if (originalCaches.bibActionsPath) require.cache[bibActionsPath] = originalCaches.bibActionsPath;
  else delete require.cache[bibActionsPath];

  if (originalCaches.sideEffectsPath) require.cache[sideEffectsPath] = originalCaches.sideEffectsPath;
  else delete require.cache[sideEffectsPath];
}
