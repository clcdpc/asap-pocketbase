const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

const modulePath = path.resolve(__dirname, "../lib/staff/title_request_actions.js");
const routeUtilsPath = path.resolve(__dirname, "../lib/route_utils.js");
const recordsPath = path.resolve(__dirname, "../lib/records.js");
const configPath = path.resolve(__dirname, "../lib/config.js");
const polarisPath = path.resolve(__dirname, "../lib/polaris.js");
const orgsPath = path.resolve(__dirname, "../lib/orgs.js");
const pickupPath = path.resolve(__dirname, "../lib/polaris/pickup_preference_context.js");
const actorPath = path.resolve(__dirname, "../lib/staff/polaris_actor.js");
const additionalCopiesPath = path.resolve(__dirname, "../lib/additional_copies.js");
const formatClaimRulesPath = path.resolve(__dirname, "../lib/format_claim_rules.js");
const contextPath = path.resolve(__dirname, "../lib/staff/title_request_action_context.js");
const bibActionsPath = path.resolve(__dirname, "../lib/staff/title_request_bib_actions.js");
const sideEffectsPath = path.resolve(__dirname, "../lib/staff/title_request_side_effects.js");

function makeRecord(fields) {
  return {
    id: fields.id || "req1",
    data: Object.assign({}, fields),
    get(k) { return this.data[k]; },
    set(k, v) { this.data[k] = v; }
  };
}

function makeStaff(fields) {
  return {
    get(k) { return fields[k]; }
  };
}

function loadWithMocks(mocks) {
  require.cache[routeUtilsPath] = { id: routeUtilsPath, filename: routeUtilsPath, loaded: true, exports: mocks.routeUtils };
  require.cache[recordsPath] = { id: recordsPath, filename: recordsPath, loaded: true, exports: mocks.records };
  require.cache[configPath] = { id: configPath, filename: configPath, loaded: true, exports: mocks.config };
  require.cache[polarisPath] = { id: polarisPath, filename: polarisPath, loaded: true, exports: mocks.polaris };
  require.cache[orgsPath] = { id: orgsPath, filename: orgsPath, loaded: true, exports: mocks.orgs };
  require.cache[pickupPath] = { id: pickupPath, filename: pickupPath, loaded: true, exports: mocks.pickup };
  require.cache[actorPath] = { id: actorPath, filename: actorPath, loaded: true, exports: mocks.actor };
  require.cache[additionalCopiesPath] = { id: additionalCopiesPath, filename: additionalCopiesPath, loaded: true, exports: {} };
  require.cache[formatClaimRulesPath] = { id: formatClaimRulesPath, filename: formatClaimRulesPath, loaded: true, exports: {} };
  require.cache[contextPath] = { id: contextPath, filename: contextPath, loaded: true, exports: { titleRequestActionContext() { return {}; } } };
  require.cache[bibActionsPath] = { id: bibActionsPath, filename: bibActionsPath, loaded: true, exports: { finalizeTitleRequestCloseReason() {}, prepareTitleRequestBibAction() { return null; } } };
  require.cache[sideEffectsPath] = { id: sideEffectsPath, filename: sideEffectsPath, loaded: true, exports: { handleAlreadyOwnOrRejectSideEffects() {}, sendPurchaseReminderIfRequested() { return null; }, maybeRunImmediatePromoter() {} } };
  delete require.cache[modulePath];
  return require(modulePath);
}

function makeEvent(app, id, body) {
  return {
    app: app,
    request: { pathValue() { return id; } },
    json(code, payload) { return { code, payload }; },
    _body: body || {}
  };
}

function baseSetup(status) {
  const record = makeRecord({
    id: "req1",
    status: status || "pending_hold",
    barcode: "2900",
    preferredPickupBranchId: "10",
    preferredPickupBranchName: "Old Branch"
  });
  const staff = makeStaff({ username: "tester", role: "admin", polarisUserId: "700" });
  const calls = { polarisUpdate: 0, save: 0, notes: 0, events: [], setCanonicalRefs: 0 };
  const app = {
    findRecordById() { return record; },
    save() { calls.save++; },
    logger() { return { error() {}, warn() {}, info() {} }; }
  };
  const routeUtils = {
    requireAuth() { return staff; },
    requireTitleRequestAccess() { return null; },
    body(e) { return e._body; }
  };
  const records = {
    STATUS: { HOLD_PLACED: "hold_placed", CLOSED: "closed" },
    normalizeStatus(v) { return String(v || ""); },
    appendSystemNote() { calls.notes++; },
    recordEvent(appArg, recArg, type, message, options) { calls.events.push({ type, options }); },
    setCanonicalRefs() { calls.setCanonicalRefs++; },
    titleRequestToJson(rec) { return { id: rec.id, preferredPickupBranchId: rec.get("preferredPickupBranchId") }; }
  };
  const config = { polaris() { return { userId: "999" }; } };
  const polaris = {
    adminStaffAuth() { return { AccessToken: "x" }; },
    lookupPatron() { return { RequestPickupBranchID: "10" }; },
    updatePatronPreferredPickupBranch() { calls.polarisUpdate++; }
  };
  const orgs = { attachPatronScope(appArg, patron) { return patron; } };
  const pickup = {
    buildPickupPreferenceContext() { return { pickupBranches: [{ id: "10", label: "Old Branch" }, { id: "20", label: "New Branch" }] }; },
    buildAvailablePickupPreferenceContext(appArg, staffAuthArg, patronArg, options) {
      options = options || {};
      let ctx = this.buildPickupPreferenceContext(appArg, staffAuthArg, patronArg, options);
      if (!options.forceRefresh && !(ctx.pickupBranches || []).length) {
        ctx = this.buildPickupPreferenceContext(appArg, staffAuthArg, patronArg, Object.assign({}, options, { forceRefresh: true }));
      }
      return ctx;
    },
    validateSelectedPickupBranch(ctx, id) {
      const b = (ctx.pickupBranches || []).find((x) => x.id === id);
      if (!b) throw new Error("invalid");
      return b;
    },
    currentPreferredId() { return "10"; },
    findBranch(list, id) { return (list || []).find((x) => x.id === id) || null; }
  };
  const actor = { resolvePolarisUpdateActor() { return { fallbackUsed: false }; } };
  return { record, staff, calls, app, routeUtils, records, config, polaris, orgs, pickup, actor };
}

function testChangedPickupWritesMetadata() {
  const t = baseSetup("pending_hold");
  const mod = loadWithMocks(t);
  const res = mod.staffTitleRequestPickupPreferenceUpdate(makeEvent(t.app, "req1", {
    preferredPickupBranchId: "20",
    currentPreferredPickupBranchIdAtLoad: "10"
  }));
  assert.strictEqual(res.code, 200);
  assert.strictEqual(t.calls.polarisUpdate, 1);
  assert.strictEqual(t.calls.notes, 1);
  assert.strictEqual(t.calls.events.length, 1);
  const evt = t.calls.events[0];
  assert.strictEqual(evt.type, "pickup_preference_changed");
  assert.strictEqual(evt.options.metadata.fromPickupBranchId, "10");
  assert.strictEqual(evt.options.metadata.toPickupBranchId, "20");
  assert.strictEqual(evt.options.metadata.polarisUserIdFallbackUsed, false);
}

function testPolarisFailureBlocksSnapshotSave() {
  const t = baseSetup("pending_hold");
  t.polaris.updatePatronPreferredPickupBranch = function () { throw new Error("upstream"); };
  const mod = loadWithMocks(t);
  const res = mod.staffTitleRequestPickupPreferenceUpdate(makeEvent(t.app, "req1", {
    preferredPickupBranchId: "20",
    currentPreferredPickupBranchIdAtLoad: "10"
  }));
  assert.strictEqual(res.code, 502);
  assert.strictEqual(t.record.get("preferredPickupBranchId"), "10");
  assert.strictEqual(t.calls.save, 0);
  assert.strictEqual(t.calls.notes, 0);
  assert.strictEqual(t.calls.events.length, 0);
}

function testHoldPlacedRejected() {
  const t = baseSetup("hold_placed");
  const mod = loadWithMocks(t);
  const res = mod.staffTitleRequestPickupPreferenceUpdate(makeEvent(t.app, "req1", {
    preferredPickupBranchId: "20",
    currentPreferredPickupBranchIdAtLoad: "10"
  }));
  assert.strictEqual(res.code, 400);
  assert.strictEqual(t.calls.polarisUpdate, 0);
  assert.strictEqual(t.calls.save, 0);
}

function testEmptyCacheTriggersForcedRefresh() {
  const t = baseSetup("pending_hold");

  var buildCount = 0;
  var refreshedWithForce = false;

  t.pickup.buildPickupPreferenceContext = function (appArg, staffAuthArg, patronArg, options) {
    buildCount++;
    options = options || {};
    if (options.forceRefresh) {
      refreshedWithForce = true;
      return {
        pickupBranches: [{ id: "9", label: "Fairfield County Main Library" }],
        selectedPickupBranchId: "9",
        pickupBranchWarning: ""
      };
    }
    return {
      pickupBranches: [],
      selectedPickupBranchId: "",
      pickupBranchWarning: "Your current Polaris preferred pickup location is not available for this form."
    };
  };

  const mod = loadWithMocks(t);
  const res = mod.staffTitleRequestPickupOptions(makeEvent(t.app, "req1", {}));

  assert.strictEqual(res.code, 200);
  assert.strictEqual(buildCount, 2);
  assert.strictEqual(refreshedWithForce, true);
  assert.strictEqual(res.payload.pickupBranches.length, 1);
  assert.strictEqual(res.payload.pickupBranches[0].id, "9");
  assert.strictEqual(res.payload.pickupOptionsUnavailable, false);
}

function testEmptyCacheStillEmptyAfterForcedRefresh() {
  const t = baseSetup("pending_hold");

  var buildCount = 0;

  t.pickup.buildPickupPreferenceContext = function (appArg, staffAuthArg, patronArg, options) {
    buildCount++;
    return {
      pickupBranches: [],
      selectedPickupBranchId: "",
      pickupBranchWarning: "Pickup locations could not be loaded for this patron. Refresh pickup locations or try again later."
    };
  };

  const mod = loadWithMocks(t);
  const res = mod.staffTitleRequestPickupOptions(makeEvent(t.app, "req1", {}));

  assert.strictEqual(res.code, 200);
  assert.strictEqual(buildCount, 2);
  assert.strictEqual(res.payload.pickupBranches.length, 0);
  assert.strictEqual(res.payload.pickupOptionsUnavailable, true);
  assert.ok(res.payload.pickupBranchWarning.includes("could not be loaded"));
}

function testUpdateEndpointRetriesStaleEmptyCache() {
  const t = baseSetup("pending_hold");

  var buildCount = 0;

  t.pickup.buildPickupPreferenceContext = function (appArg, staffAuthArg, patronArg, options) {
    buildCount++;
    options = options || {};
    if (options.forceRefresh) {
      return {
        pickupBranches: [{ id: "20", label: "New Branch" }]
      };
    }
    return {
      pickupBranches: []
    };
  };

  const mod = loadWithMocks(t);
  const res = mod.staffTitleRequestPickupPreferenceUpdate(makeEvent(t.app, "req1", {
    preferredPickupBranchId: "20",
    currentPreferredPickupBranchIdAtLoad: "10"
  }));

  assert.strictEqual(res.code, 200);
  assert.strictEqual(t.calls.polarisUpdate, 1);
  assert.strictEqual(t.record.get("preferredPickupBranchId"), "20");
}

testChangedPickupWritesMetadata();
testPolarisFailureBlocksSnapshotSave();
testHoldPlacedRejected();
testEmptyCacheTriggersForcedRefresh();
testEmptyCacheStillEmptyAfterForcedRefresh();
testUpdateEndpointRetriesStaleEmptyCache();
console.log("staff_title_request_pickup_update.test.js passed.");
