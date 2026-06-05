const assert = require('assert');
const path = require('path');

global.__hooks = path.resolve(__dirname, '../pb_hooks');

// Import settingsSave to test it directly
const settingsSave = require('../lib/staff/settings_save.js');
const config = require('../lib/config.js');

// Mock config.saveSystemSettings to track calls
let savedSystemSettings = null;
config.saveSystemSettings = (app, data) => {
  savedSystemSettings = data;
};

global.Record = function MockRecord(collection) {
  return {
    values: {},
    get(name) { return this.values[name]; },
    set(name, value) { this.values[name] = value; },
    save() {}
  };
};

// Mock other dependencies that saveSystemSettingsPayload might call
const mockApp = {
  save() {},
  findCollectionByNameOrId() { return { name: 'mock' }; },
  logger() { return { warn() {}, error() {}, info() {}, debug() {} }; }
};

// Mock external dependencies
const settingsUi = require('../lib/staff/settings_ui.js');
settingsUi.saveUiSettings = () => {};

const settingsEmail = require('../lib/staff/settings_email.js');
settingsEmail.saveEmailSettings = () => {};

// Mock config functions that might be called
config.savePolarisSettings = () => {};
config.getSmtpSettings = () => ({ set: () => {} });

console.log('Running system settings save payload test...');

const payload = {
  staffUrl: 'https://example.org/staff',
  formatIconUrlPattern: 'https://example.org/icons/{format}.png',
  patronEmbedAllowedOrigins: 'https://www.library.org'
};

// Act
settingsSave.saveSystemSettingsPayload(mockApp, payload);

// Assert
assert.ok(savedSystemSettings, 'Expected saveSystemSettings to be called');
assert.strictEqual(savedSystemSettings.formatIconUrlPattern, 'https://example.org/icons/{format}.png', 'Expected formatIconUrlPattern to be saved');
assert.strictEqual(savedSystemSettings.patronEmbedAllowedOrigins, 'https://www.library.org', 'Expected patronEmbedAllowedOrigins to be saved');

console.log('System settings save payload test passed!');
