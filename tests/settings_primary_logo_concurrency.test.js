const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

function response(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 409 ? 'Conflict' : 'OK',
    json: async () => body
  };
}

const canonicalLibrarySettings = JSON.parse(fs.readFileSync(
  path.join(__dirname, 'fixtures', 'settings', 'canonical-library-settings-response.json'), 'utf8'));

function settingsData(version, altText) {
  const result = structuredClone(canonicalLibrarySettings);
  result.version = version;
  result.stored.branding = { ...result.stored.branding, altText, version: `branding-${version}` };
  result.stored.libraryOverride.branding = { ...result.stored.libraryOverride.branding, altText, version: `branding-${version}` };
  result.effective.logoAltText = altText;
  return result;
}

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
  await Promise.resolve();
}

(async () => {
  const frontendRoot = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-primary-logo-version-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
    assert.strictEqual(dom.window.document.getElementById('ui-logo-file').getAttribute('accept'),
      'image/png, image/jpeg, image/gif', 'the primary upload contract must match the .NET logo validator');
    assert.doesNotMatch(dom.window.document.getElementById('ui-logo-file').parentElement.nextElementSibling.textContent, /svg/i);
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.Blob = dom.window.Blob;
    global.File = dom.window.File;
    global.FileReader = dom.window.FileReader;
    global.Node = dom.window.Node;
    global.Event = dom.window.Event;
    global.CustomEvent = dom.window.CustomEvent;
    global.Option = dom.window.Option;
    global.requestAnimationFrame = callback => callback();
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };

    const state = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'state.js')).href);
    const settings = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings.js')).href);
    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'test-token',
      staff: { id: '27', role: 'admin', organizationId: '2', organizationName: 'Library Two', userPrincipalName: 'admin@example.org' }
    });

    let serverVersion = 'version-1';
    let serverAltText = 'Library alt one';
    const logoMutations = [];
    const libraryResets = [];
    let nextLibraryResetIsStale = false;
    let nextLibraryResetStaleReadFails = false;
    let nextLogoUploadStaleReadFails = false;
    let nextLogoResetStaleReadFails = false;
    let deferNextLogoUpload = false;
    let releaseDeferredLogoUpload = null;
    let nextLibrarySettingsReadFails = false;
    let deferNextLibrarySettingsRead = false;
    let releaseDeferredLibrarySettingsRead = null;
    let settingsReads = 0;
    let configReads = 0;
    let nextConfigReadFails = false;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.includes('/api/asap/staff/settings/library?orgId=2')) {
        settingsReads++;
        if (deferNextLibrarySettingsRead) {
          deferNextLibrarySettingsRead = false;
          return new Promise(resolve => {
            releaseDeferredLibrarySettingsRead = () => resolve(response(200, settingsData(serverVersion, serverAltText)));
          });
        }
        if (nextLibrarySettingsReadFails) {
          nextLibrarySettingsReadFails = false;
          return response(503, { code: 'settings_unavailable', message: 'Settings refresh temporarily unavailable.' });
        }
        return response(200, settingsData(serverVersion, serverAltText));
      }
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes')) return response(200, { code: 'ok', data: [] });
      if (requestUrl.startsWith('/api/asap/staff/users')) return response(200, { users: [] });
      if (requestUrl === '/api/asap/config') {
        configReads++;
        if (nextConfigReadFails) {
          nextConfigReadFails = false;
          return response(503, { code: 'config_unavailable', message: 'Config refresh temporarily unavailable.' });
        }
        return response(200, { publicationOptions: [] });
      }
      if (requestUrl === '/api/asap/staff/settings/library' && String(options.method || 'GET').toUpperCase() === 'POST') {
        const payload = JSON.parse(options.body);
        libraryResets.push(payload);
        assert.strictEqual(payload.orgId, '2');
        assert.strictEqual(payload.action, 'reset');
        if (nextLibraryResetStaleReadFails) {
          nextLibraryResetStaleReadFails = false;
          serverVersion = 'version-13';
          serverAltText = 'Authoritative alt after failed stale library reset';
          nextLibrarySettingsReadFails = true;
          return response(409, { code: 'stale_version', message: 'These settings changed in another session.' });
        }
        if (nextLibraryResetIsStale) {
          nextLibraryResetIsStale = false;
          serverVersion = 'version-6';
          serverAltText = 'Latest Settings after stale reset';
          return response(409, { code: 'stale_version', message: 'These settings changed in another session.' });
        }
        if (libraryResets.length === 3) {
          serverVersion = 'version-7';
          serverAltText = 'System inherited alt after reset';
          nextLibrarySettingsReadFails = true;
          return response(200, { code: 'reset' });
        }
        serverVersion = 'version-5';
        serverAltText = 'System inherited alt';
        return response(200, { code: 'reset' });
      }
      if (requestUrl.includes('/api/asap/staff/settings/logo')) {
        const method = String(options.method || 'GET').toUpperCase();
        const submittedVersion = method === 'POST'
          ? options.body.get('version')
          : new URL(requestUrl, 'http://localhost').searchParams.get('version');
        logoMutations.push({ method, requestUrl, submittedVersion });

        if (deferNextLogoUpload && method === 'POST') {
          deferNextLogoUpload = false;
          serverVersion = 'version-10';
          serverAltText = options.body.get('logoAlt');
          return new Promise(resolve => {
            releaseDeferredLogoUpload = () => resolve(response(200, { code: 'branding_saved' }));
          });
        }
        if (nextLogoUploadStaleReadFails && method === 'POST') {
          nextLogoUploadStaleReadFails = false;
          serverVersion = 'version-11';
          serverAltText = 'Authoritative alt after failed stale upload';
          nextLibrarySettingsReadFails = true;
          return response(409, { code: 'stale_version', message: 'These settings changed in another session.' });
        }
        if (nextLogoResetStaleReadFails && method === 'DELETE') {
          nextLogoResetStaleReadFails = false;
          serverVersion = 'version-12';
          serverAltText = 'Authoritative alt after failed stale logo reset';
          nextLibrarySettingsReadFails = true;
          return response(409, { code: 'stale_version', message: 'These settings changed in another session.' });
        }

        if (method === 'POST' && logoMutations.length === 1) {
          serverVersion = 'version-2';
          serverAltText = options.body.get('logoAlt');
          return response(200, { code: 'branding_saved', data: { version: serverVersion } });
        }
        if (method === 'DELETE' && logoMutations.length === 2) {
          serverVersion = 'version-3';
          serverAltText = 'Library alt after another session';
          return response(409, { code: 'stale_version', message: 'These settings changed in another session.' });
        }
        if (method === 'POST' && logoMutations.length === 3) {
          serverVersion = 'version-4';
          serverAltText = 'Latest logo alt after the stale upload';
          return response(409, { code: 'stale_version', message: 'These settings changed in another session.' });
        }
        if (method === 'POST' && logoMutations.length === 4) {
          serverVersion = 'version-8';
          serverAltText = 'Committed upload with unavailable reload';
          nextLibrarySettingsReadFails = true;
          return response(200, { code: 'branding_saved', data: { version: serverVersion } });
        }
        if (method === 'DELETE' && logoMutations.length === 5) {
          serverVersion = 'version-9';
          serverAltText = 'Committed inherited alt with unavailable reload';
          nextLibrarySettingsReadFails = true;
          return response(200, { code: 'branding_reset', data: { version: serverVersion } });
        }
        throw new Error(`Unexpected logo mutation ${method} ${requestUrl}`);
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    await settings.loadSettings();
    assert.strictEqual(state.currentLibraryContextOrgId, '2');
    assert.strictEqual(document.getElementById('ui-logo-alt').value, 'Library alt one');

    const loginNote = document.getElementById('ui-login-note');
    const logoFileInput = document.getElementById('ui-logo-file');
    let selectedLogoFile = new dom.window.File(['logo'], 'logo.png', { type: 'image/png' });
    const nativeFileValue = Object.getOwnPropertyDescriptor(dom.window.HTMLInputElement.prototype, 'value');
    Object.defineProperty(logoFileInput, 'files', {
      configurable: true,
      get: () => selectedLogoFile ? [selectedLogoFile] : []
    });
    Object.defineProperty(logoFileInput, 'value', {
      configurable: true,
      get() { return selectedLogoFile ? 'C:\\fakepath\\logo.png' : nativeFileValue.get.call(this); },
      set(value) { if (value === '') selectedLogoFile = null; nativeFileValue.set.call(this, value); }
    });
    logoFileInput.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    const originalLoginNote = loginNote.value;
    loginNote.value = 'Unrelated unsaved login note';
    loginNote.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    document.getElementById('btn-upload-logo').click();
    await flush();
    assert.strictEqual(logoMutations.length, 0, 'branding upload must preserve unrelated Settings drafts');
    document.getElementById('btn-reset-logo').click();
    await flush();
    assert.strictEqual(logoMutations.length, 0, 'branding reset must preserve unrelated Settings drafts');
    document.getElementById('btn-reset-library-settings').click();
    await flush();
    assert.strictEqual(libraryResets.length, 0, 'library reset must preserve an uncommitted Settings draft');
    assert.strictEqual(loginNote.value, 'Unrelated unsaved login note');
    assert.strictEqual(state.settingsDirty, true);
    assert.strictEqual(await settings.saveSettings(), false,
      'ordinary Settings save must not clear a logo file that is not part of its payload');
    assert.strictEqual(libraryResets.length, 0);
    assert.strictEqual(logoFileInput.files.length, 1);
    assert.strictEqual(await settings.syncPolarisOrganizations(), null);
    assert.strictEqual(logoFileInput.files.length, 1);
    loginNote.value = originalLoginNote;
    loginNote.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.strictEqual(state.settingsDirty, true, 'a selected logo alone is an unsaved draft');
    assert.strictEqual(document.getElementById('settings-save-btn').disabled, true);
    const readsBeforeFileOnlyReopen = settingsReads;
    await settings.loadSettings({ preserveDraft: true });
    assert.strictEqual(settingsReads, readsBeforeFileOnlyReopen);
    assert.strictEqual(logoFileInput.files.length, 1, 'reopening Settings must preserve a selected logo');
    document.getElementById('btn-clear-selected-logo').click();
    assert.strictEqual(logoFileInput.files.length, 0);
    assert.strictEqual(state.settingsDirty, false);

    document.getElementById('ui-logo-alt').value = 'Uploaded logo description';
    document.getElementById('btn-upload-logo').click();
    await flush();
    await flush();
    assert.strictEqual(logoMutations[0].method, 'POST');
    assert.strictEqual(logoMutations[0].submittedVersion, 'version-1', 'logo upload must send the loaded Settings version');
    assert.strictEqual(settingsReads, 2, 'successful branding updates must reload the current Settings response');
    assert.strictEqual(document.getElementById('ui-logo-alt').value, 'Uploaded logo description');
    assert.strictEqual(state.settingsReloadRequired, false);
    assert.strictEqual(state.settingsForm.inert, false);

    const readsBeforeStaleReset = settingsReads;
    document.getElementById('btn-reset-logo').click();
    await flush();
    document.getElementById('confirm-dialog-ok').click();
    await flush();
    await flush();
    assert.strictEqual(logoMutations[1].method, 'DELETE');
    assert.strictEqual(logoMutations[1].submittedVersion, 'version-2', 'logo reset must send the current loaded Settings version');
    assert.strictEqual(settingsReads, readsBeforeStaleReset + 1, 'stale logo reset must reload Settings without retrying');
    assert.strictEqual(document.getElementById('ui-logo-alt').value, 'Library alt after another session');
    const staleResetToasts = document.querySelectorAll('#toast-container .asap-toast');
    assert.match(staleResetToasts[staleResetToasts.length - 1].textContent, /changed in another session.*reloaded/i);

    document.getElementById('ui-logo-alt').value = 'Stale draft logo description';
    const readsBeforeStaleUpload = settingsReads;
    document.getElementById('btn-upload-logo').click();
    await flush();
    await flush();
    assert.strictEqual(logoMutations[2].method, 'POST');
    assert.strictEqual(logoMutations[2].submittedVersion, 'version-3');
    assert.strictEqual(logoMutations.length, 3, 'stale logo upload must not be automatically retried');
    assert.strictEqual(settingsReads, readsBeforeStaleUpload + 1, 'stale logo upload must reload authoritative Settings');
    assert.strictEqual(document.getElementById('ui-logo-alt').value, 'Latest logo alt after the stale upload');

    const configReadsBeforeLibraryReset = configReads;
    nextConfigReadFails = true;
    document.getElementById('btn-reset-library-settings').click();
    await flush();
    document.getElementById('confirm-dialog-ok').click();
    await flush();
    await flush();
    assert.deepStrictEqual(libraryResets[0], { orgId: '2', action: 'reset', version: 'version-4' },
      'Reset Library Settings must send its current Settings version and exact selected scope');
    assert.strictEqual(serverVersion, 'version-5');
    assert.strictEqual(configReads, configReadsBeforeLibraryReset + 1,
      'a successful library reset must refresh derived app configuration');
    assert.strictEqual(state.settingsReloadRequired, false,
      'an auxiliary config failure must not invalidate the authoritative Settings version');
    assert.match(Array.from(document.querySelectorAll('#toast-container .asap-toast')).at(-1).textContent,
      /settings were reset and reloaded, but app configuration could not be refreshed/i);
    assert.strictEqual(document.getElementById('ui-logo-alt').value, 'System inherited alt',
      'a successful library reset must reload inherited branding values');

    nextLibraryResetIsStale = true;
    document.getElementById('btn-reset-library-settings').click();
    await flush();
    document.getElementById('confirm-dialog-ok').click();
    await flush();
    await flush();
    assert.deepStrictEqual(libraryResets[1], { orgId: '2', action: 'reset', version: 'version-5' });
    assert.strictEqual(libraryResets.length, 2, 'a stale reset must never be automatically retried');
    assert.strictEqual(serverVersion, 'version-6');
    assert.strictEqual(document.getElementById('ui-logo-alt').value, 'Latest Settings after stale reset',
      'a stale reset must reload authoritative current values');

    const readsBeforeResetRefreshFailure = settingsReads;
    document.getElementById('btn-reset-library-settings').click();
    await flush();
    document.getElementById('confirm-dialog-ok').click();
    await flush();
    await flush();
    assert.deepStrictEqual(libraryResets[2], { orgId: '2', action: 'reset', version: 'version-6' });
    assert.strictEqual(settingsReads, readsBeforeResetRefreshFailure + 1,
      'a successful reset with failed reload must attempt exactly one authoritative refresh');
    const resetRefreshFailureToasts = document.querySelectorAll('#toast-container .asap-toast');
    assert.match(resetRefreshFailureToasts[resetRefreshFailureToasts.length - 1].textContent,
      /were reset, but current values could not be reloaded/i,
      'the UI must report reset success separately from a failed follow-up read');
    assert.strictEqual(document.getElementById('ui-logo-alt').value, 'Latest Settings after stale reset',
      'a failed reload must not render an assumed inherited value');
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, 'version-6',
      'the reset response must not be used as a guessed Settings version');
    assert.strictEqual(state.settingsReloadRequired, true);
    assert.strictEqual(document.getElementById('settings-save-title').textContent, 'Reload required');
    assert.strictEqual(state.settingsForm.inert, true);
    assert.strictEqual(document.getElementById('settings-reload-btn').classList.contains('hidden'), false);
    const mutationsBeforeBlockedReset = libraryResets.length;
    assert.strictEqual(await settings.saveSettings(), false);
    document.getElementById('btn-reset-library-settings').click();
    await flush();
    assert.strictEqual(libraryResets.length, mutationsBeforeBlockedReset,
      'another reset must be blocked while the committed version is unknown');
    document.getElementById('btn-upload-logo').click();
    await flush();
    assert.strictEqual(logoMutations.length, 3,
      'branding must also be blocked while the committed version is unknown');

    await settings.loadSettings();
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, 'version-7');
    assert.strictEqual(state.settingsReloadRequired, false);
    assert.strictEqual(state.settingsForm.inert, false);
    const readsBeforeUploadRefreshFailure = settingsReads;
    document.getElementById('ui-logo-alt').value = 'Committed upload with unavailable reload';
    document.getElementById('btn-upload-logo').click();
    await flush();
    await flush();
    assert.strictEqual(logoMutations.length, 4, 'successful upload must be sent exactly once');
    assert.strictEqual(logoMutations[3].submittedVersion, 'version-7');
    assert.strictEqual(settingsReads, readsBeforeUploadRefreshFailure + 1);
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, 'version-7');
    assert.strictEqual(state.settingsReloadRequired, true);
    assert.strictEqual(document.getElementById('settings-save-title').textContent, 'Reload required');
    assert.match(document.getElementById('settings-msg').textContent, /branding was changed, but current settings could not be reloaded/i);
    assert.match(Array.from(document.querySelectorAll('#toast-container .asap-toast')).at(-1).textContent,
      /branding was changed, but current settings could not be reloaded.*reload settings/i);
    assert.strictEqual(await settings.saveSettings(), false, 'a failed branding reload must block Settings save');
    assert.strictEqual(document.getElementById('ui-logo-alt').value, 'Committed upload with unavailable reload');

    await settings.loadSettings();
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, 'version-8');
    assert.strictEqual(state.settingsReloadRequired, false);
    const readsBeforeResetLogoRefreshFailure = settingsReads;
    document.getElementById('btn-reset-logo').click();
    await flush();
    document.getElementById('confirm-dialog-ok').click();
    await flush();
    await flush();
    assert.strictEqual(logoMutations.length, 5, 'successful logo reset must be sent exactly once');
    assert.strictEqual(logoMutations[4].submittedVersion, 'version-8');
    assert.strictEqual(settingsReads, readsBeforeResetLogoRefreshFailure + 1);
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, 'version-8');
    assert.strictEqual(state.settingsReloadRequired, true);
    assert.strictEqual(document.getElementById('settings-save-title').textContent, 'Reload required');
    assert.match(document.getElementById('settings-msg').textContent, /branding was changed, but current settings could not be reloaded/i);
    assert.match(Array.from(document.querySelectorAll('#toast-container .asap-toast')).at(-1).textContent,
      /branding was changed, but current settings could not be reloaded.*reload settings/i);
    assert.strictEqual(document.getElementById('ui-logo-alt').value, 'Committed upload with unavailable reload',
      'failed reload must not render guessed inherited branding');

    const blockedMutations = logoMutations.length;
    document.getElementById('btn-upload-logo').click();
    document.getElementById('btn-reset-logo').click();
    document.getElementById('settings-discard-btn').click();
    await flush();
    assert.strictEqual(logoMutations.length, blockedMutations);
    assert.strictEqual(state.settingsReloadRequired, true);
    assert.strictEqual(state.settingsForm.inert, true);
    document.getElementById('settings-reload-btn').click();
    await flush();
    await flush();
    assert.strictEqual(state.settingsReloadRequired, false);
    assert.strictEqual(state.settingsForm.inert, false);
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, 'version-9');
    assert.strictEqual(document.getElementById('ui-logo-alt').value, 'Committed inherited alt with unavailable reload');

    document.getElementById('ui-logo-alt').value = 'Branding while request is pending';
    deferNextLogoUpload = true;
    document.getElementById('btn-upload-logo').click();
    assert.strictEqual(typeof releaseDeferredLogoUpload, 'function');
    assert.strictEqual(state.settingsForm.inert, true);
    const mutationsDuringPendingBranding = logoMutations.length;
    document.getElementById('btn-reset-logo').click();
    assert.strictEqual(await settings.saveSettings(), false);
    assert.strictEqual(logoMutations.length, mutationsDuringPendingBranding);
    releaseDeferredLogoUpload();
    await flush();
    await flush();
    assert.strictEqual(state.settingsForm.inert, false);
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, 'version-10');
    assert.strictEqual(document.getElementById('ui-logo-alt').value, 'Branding while request is pending');

    async function assertFailedStaleRecovery(expectedVersion, family) {
      await flush();
      await flush();
      const mutationCount = logoMutations.length + libraryResets.length;
      assert.strictEqual(state.settingsReloadRequired, true, `${family} must require reload after failed stale recovery`);
      assert.strictEqual(state.settingsForm.inert, true);
      assert.strictEqual(document.getElementById('settings-save-title').textContent, 'Reload required');
      assert.strictEqual(await settings.saveSettings(), false);
      document.getElementById('btn-upload-logo').click();
      await flush();
      assert.strictEqual(logoMutations.length + libraryResets.length, mutationCount,
        `${family} must not retry or allow a subsequent mutation`);
      document.getElementById('settings-reload-btn').click();
      await flush();
      await flush();
      assert.strictEqual(state.settingsReloadRequired, false);
      assert.strictEqual(state.settingsForm.inert, false);
      assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, expectedVersion);
      assert.strictEqual(document.getElementById('ui-logo-alt').value, serverAltText);
    }

    nextLogoUploadStaleReadFails = true;
    document.getElementById('ui-logo-alt').value = 'Stale upload draft';
    document.getElementById('btn-upload-logo').click();
    await assertFailedStaleRecovery('version-11', 'logo upload');

    nextLogoResetStaleReadFails = true;
    document.getElementById('btn-reset-logo').click();
    await flush();
    document.getElementById('confirm-dialog-ok').click();
    await assertFailedStaleRecovery('version-12', 'logo reset');

    nextLibraryResetStaleReadFails = true;
    document.getElementById('btn-reset-library-settings').click();
    await flush();
    document.getElementById('confirm-dialog-ok').click();
    await assertFailedStaleRecovery('version-13', 'library reset');

    deferNextLibrarySettingsRead = true;
    const pendingAuthoritativeLoad = settings.loadSettings({ skipAutoSync: true });
    await flush();
    assert.strictEqual(typeof releaseDeferredLibrarySettingsRead, 'function');
    assert.strictEqual(state.settingsForm.inert, true, 'Settings inputs must remain protected during an authoritative load');
    const mutationsDuringLoad = logoMutations.length + libraryResets.length;
    document.getElementById('btn-upload-logo').click();
    document.getElementById('btn-reset-library-settings').click();
    await flush();
    assert.strictEqual(logoMutations.length + libraryResets.length, mutationsDuringLoad);
    releaseDeferredLibrarySettingsRead();
    await pendingAuthoritativeLoad;
    assert.strictEqual(state.settingsForm.inert, false);

    dom.window.close();
    console.log('Primary Settings direct actions send current scope versions and reload stale state without retry');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
