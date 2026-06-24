const assert = require('assert');
const fs = require('fs');
const path = require('path');

const navSource = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/app/nav.js'), 'utf8');
const settingsSource = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/settings.js'), 'utf8');
const libraryContextSource = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/settings/library-context.js'), 'utf8');
const saveControllerSource = fs.readFileSync(path.join(__dirname, '../pb_public/staff/js/settings/save-controller.js'), 'utf8');
const stylesSource = fs.readFileSync(path.join(__dirname, '../pb_public/staff/styles.css'), 'utf8');

function extractFunction(source, name) {
  const asyncStart = source.indexOf('async function ' + name + '(');
  const regularStart = source.indexOf('function ' + name + '(');
  const start = asyncStart >= 0 ? asyncStart : regularStart;
  if (start < 0) throw new Error('Could not find function ' + name);
  const bodyStart = source.indexOf('{', start);
  let depth = 0;
  for (let i = bodyStart; i < source.length; i++) {
    if (source[i] === '{') depth++;
    if (source[i] === '}') depth--;
    if (depth === 0) return source.slice(start, i + 1);
  }
  throw new Error('Could not extract function ' + name);
}

function makeSelect(initialValue) {
  const select = {
    options: [
      { value: 'system', text: 'System Defaults' },
      { value: '2', text: 'Alexandria (ID 2)' }
    ],
    selectedIndex: 0,
    get value() {
      return this._value;
    },
    set value(nextValue) {
      this._value = nextValue;
      this.selectedIndex = this.options.findIndex(option => option.value === nextValue);
    }
  };
  select.value = initialValue;
  return select;
}

function loadSwitchHarness(confirmResult) {
  const switchSource = extractFunction(libraryContextSource, 'switchLibraryContext');
  return new Function('makeSelect', 'confirmResult', `
    let currentLibraryContextOrgId = '2';
    let settingsDirty = true;
    let currentSettingsSection = 'smtp';
    let confirmCalls = 0;
    let cleanCalls = 0;
    const loadedOrgIds = [];
    const activatedSections = [];
    const savedOrgIds = [];
    const display = { textContent: 'Alexandria (ID 2)' };
    const select = makeSelect('2');
    const document = {
      getElementById(id) {
        if (id === 'select-library-context') return select;
        if (id === 'library-context-display') return display;
        return null;
      }
    };
    async function showConfirm(title, message) {
      confirmCalls++;
      return confirmResult;
    }
    function setCurrentLibraryContextOrgId(orgId) {
      currentLibraryContextOrgId = orgId;
    }
    function saveSuperAdminLibraryContext(orgId) {
      savedOrgIds.push(orgId);
    }
    async function loadLibrarySettings(orgId) {
      loadedOrgIds.push(orgId);
    }
    function markSettingsClean() {
      cleanCalls++;
      settingsDirty = false;
    }
    function activateSettingsSection(section, options) {
      activatedSections.push({ section, options });
    }
    ${switchSource}
    return {
      switchLibraryContext,
      select,
      display,
      get currentLibraryContextOrgId() { return currentLibraryContextOrgId; },
      get settingsDirty() { return settingsDirty; },
      get confirmCalls() { return confirmCalls; },
      get cleanCalls() { return cleanCalls; },
      loadedOrgIds,
      activatedSections,
      savedOrgIds
    };
  `)(makeSelect, confirmResult);
}

(async () => {
console.log('Running system-level settings guard tests...');

assert.ok(
  navSource.includes("switchBtn.textContent = 'Switch to System Level';"),
  'System-only guard should use system-level language, not default language'
);

assert.ok(
  navSource.includes("await handleLibraryContextSwitch('system');"),
  'System-only guard should route clicks through the shared context switch helper'
);

assert.ok(
  stylesSource.includes('.settings-panel-locked .btn:not(.settings-nav-link):not(.system-only-switch-link)'),
  'Locked panels should not block pointer events for the switch-to-system-level action'
);

assert.ok(
  saveControllerSource.includes('const isSystemSave = currentLibraryContextOrgId === \'system\';'),
  'Save path should explicitly distinguish system saves from library saves'
);
assert.ok(
  saveControllerSource.includes('if (isSystemSave)') &&
    saveControllerSource.includes('libraryPayload.smtp = payload.smtp;') &&
    saveControllerSource.includes('libraryPayload.polaris = payload.polaris;') &&
    saveControllerSource.includes('libraryPayload.patronEmbedAllowedOrigins = payload.patronEmbedAllowedOrigins;'),
  'System-only payload keys should only be included for system-context saves'
);

let harness = loadSwitchHarness(false);
let switched = await harness.switchLibraryContext('system', harness.select);
assert.strictEqual(switched, false, 'Canceling the dirty warning should cancel the context switch');
assert.strictEqual(harness.confirmCalls, 1, 'Dirty library edits should trigger a confirmation before switching');
assert.strictEqual(harness.select.value, '2', 'Canceling should restore the previous library selection');
assert.strictEqual(harness.currentLibraryContextOrgId, '2', 'Canceling should keep the current library context');
assert.deepStrictEqual(harness.loadedOrgIds, [], 'Canceling should not reload system settings');

harness = loadSwitchHarness(true);
switched = await harness.switchLibraryContext('system', harness.select);
assert.strictEqual(switched, true, 'Confirming the dirty warning should allow the context switch');
assert.strictEqual(harness.confirmCalls, 1, 'Dirty library edits should still be confirmed before switching');
assert.strictEqual(harness.select.value, 'system', 'Confirming should select the system context');
assert.strictEqual(harness.currentLibraryContextOrgId, 'system', 'Confirming should update the current settings context');
assert.deepStrictEqual(harness.loadedOrgIds, ['system'], 'Confirming should reload system-level settings');
assert.strictEqual(harness.display.textContent, 'System Defaults', 'Confirming should refresh the visible context label');
assert.strictEqual(harness.cleanCalls, 1, 'Confirming should mark the new context clean after loading');
assert.deepStrictEqual(harness.savedOrgIds, ['system'], 'Confirming should persist the system context selection');
assert.strictEqual(harness.activatedSections[0].section, 'smtp', 'Confirming should refresh the active system-only section guard');

console.log('All system-level settings guard tests passed!');
})().catch(err => {
  console.error(err);
  process.exit(1);
});
