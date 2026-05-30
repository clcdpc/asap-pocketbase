const assert = require("assert");
const path = require("path");

global.__hooks = path.resolve(__dirname, "../pb_hooks");

// Require settingsSave FIRST to handle the circular dependency between settings_save.js and settings_ui.js
const settingsSave = require("../lib/staff/settings_save.js");

// Mock record class
class MockRecord {
  constructor(collectionName, data) {
    this.collectionName = collectionName;
    this.data = data || {};
    this.id = "mock_" + Math.random().toString(16).slice(2);
  }
  get(key) {
    return this.data[key];
  }
  set(key, value) {
    this.data[key] = value;
  }
  getBool(key) {
    return !!this.data[key];
  }
  getInt(key) {
    return parseInt(this.data[key], 10) || 0;
  }
}
global.Record = MockRecord;

// Require other libraries
const config = require("../lib/config.js");
const orgs = require("../lib/orgs.js");
const polaris = require("../lib/polaris.js");
const settingsUi = require("../lib/staff/settings_ui.js");
const settingsEmail = require("../lib/staff/settings_email.js");

// Mock standard operations to prevent actual DB/network requests
settingsUi.saveUiSettings = () => {};
settingsEmail.saveEmailSettings = () => {};

// Mock config functions
let savePolarisSettingsCalledWith = null;
config.savePolarisSettings = (app, data) => {
  savePolarisSettingsCalledWith = data;
};

// We also mock getSmtpSettings to return a MockRecord if needed
config.getSmtpSettings = () => new MockRecord("smtp_settings");

// Mock polaris and orgs functions
let syncOrganizationsCalled = false;
let syncOrganizationsError = null;
orgs.syncOrganizations = (app, staffAuth) => {
  syncOrganizationsCalled = true;
  if (syncOrganizationsError) {
    throw syncOrganizationsError;
  }
  return { synced: 5 };
};

let adminStaffAuthCalledWith = null;
polaris.adminStaffAuth = (polarisData) => {
  adminStaffAuthCalledWith = polarisData;
  return { AccessToken: "mock-token" };
};

// Mock warning logger
let warningLogged = null;
const mockApp = {
  findCollectionByNameOrId: () => ({ name: "mock" }),
  findFirstRecordByFilter: () => new MockRecord("mock"),
  save: () => {},
  logger() {
    return {
      warn(msg, ...args) {
        warningLogged = { msg, args };
      },
      error() {},
      info() {},
      debug() {}
    };
  }
};

function runTests() {
  console.log("Running settings_save tests...");

  // Test Case 1: saveSystemSettingsPayload with payload.polaris does not throw ReferenceError
  // and config.savePolarisSettings is called.
  // We supply a payload where host/accessId/apiKey are present.
  const payload = {
    polaris: {
      host: "polaris.test.org",
      accessId: "test-access-id",
      apiKey: "test-api-key"
    }
  };

  savePolarisSettingsCalledWith = null;
  syncOrganizationsCalled = false;
  adminStaffAuthCalledWith = null;
  warningLogged = null;

  try {
    settingsSave.saveSystemSettingsPayload(mockApp, payload);
    console.log("- saveSystemSettingsPayload does not throw ReferenceError: passed");
  } catch (err) {
    assert.fail("saveSystemSettingsPayload threw an error: " + err.stack);
  }

  // Verify config.savePolarisSettings was called with correctly normalized data
  assert.ok(savePolarisSettingsCalledWith, "Expected config.savePolarisSettings to be called");
  assert.strictEqual(savePolarisSettingsCalledWith.host, "polaris.test.org");
  assert.strictEqual(savePolarisSettingsCalledWith.accessId, "test-access-id");
  assert.strictEqual(savePolarisSettingsCalledWith.apiKey, "test-api-key");
  console.log("- config.savePolarisSettings is called: passed");

  // Verify org sync is attempted when host/accessId/apiKey are present
  assert.ok(syncOrganizationsCalled, "Expected orgs.syncOrganizations to be called when credentials are present");
  assert.ok(adminStaffAuthCalledWith, "Expected polaris.adminStaffAuth to be called");
  assert.strictEqual(adminStaffAuthCalledWith.host, "polaris.test.org");
  console.log("- org sync is attempted when host/accessId/apiKey are present: passed");

  // Test Case 2: org sync failure is logged as warning and does not fail save
  syncOrganizationsCalled = false;
  syncOrganizationsError = new Error("Connection failed");
  warningLogged = null;

  try {
    settingsSave.saveSystemSettingsPayload(mockApp, payload);
    assert.ok(syncOrganizationsCalled, "Expected orgs.syncOrganizations to be called");
    assert.ok(warningLogged, "Expected warning log to be created");
    assert.strictEqual(warningLogged.msg, "Polaris organization sync failed after settings save");
    console.log("- org sync failure is logged as warning and does not fail save: passed");
  } catch (err) {
    assert.fail("saveSystemSettingsPayload failed when sync failed: " + err.stack);
  }

  // Test Case 3: org sync is NOT attempted if host, accessId or apiKey are missing
  const incompletePayloads = [
    { polaris: { accessId: "a", apiKey: "b" } },
    { polaris: { host: "h", apiKey: "b" } },
    { polaris: { host: "h", accessId: "a" } }
  ];

  incompletePayloads.forEach((p, index) => {
    syncOrganizationsCalled = false;
    settingsSave.saveSystemSettingsPayload(mockApp, p);
    assert.strictEqual(syncOrganizationsCalled, false, `Expected no sync for incomplete payload #${index}`);
  });
  console.log("- no org sync attempted for incomplete polaris credentials: passed");

  console.log("All settings_save tests passed!");
}

try {
  runTests();
} catch (err) {
  console.error("Test failed:");
  console.error(err);
  process.exit(1);
}
