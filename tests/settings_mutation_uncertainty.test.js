const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');
const { projectLegacySettingsFixture } = require('./helpers/legacy-settings-fixture.cjs');

const response = (status, body) => ({ ok: status >= 200 && status < 300, status, statusText: 'Response', json: async () => body });
const flush = async () => { await new Promise(resolve => setImmediate(resolve)); await Promise.resolve(); };

(async () => {
  const root = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-mutation-uncertainty-'));
  fs.cpSync(path.join(root, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(root, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
  try {
    const dom = new JSDOM(fs.readFileSync(path.join(root, 'staff', 'index.html'), 'utf8'), { url: 'http://localhost/staff/' });
    Object.assign(global, {
      window: dom.window, document: dom.window.document, FormData: dom.window.FormData,
      Blob: dom.window.Blob, File: dom.window.File, FileReader: dom.window.FileReader,
      Node: dom.window.Node, Event: dom.window.Event, CustomEvent: dom.window.CustomEvent,
      Option: dom.window.Option, requestAnimationFrame: callback => callback()
    });
    dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    dom.window.HTMLDialogElement.prototype.close = function () { this.open = false; };
    const state = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'state.js')).href);
    const settings = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings.js')).href);
    const patronCodes = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'patron-codes.js')).href);
    const outcome = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'mutation-outcome.js')).href);
    assert.strictEqual(outcome.classifyMutationOutcome({ status: 400, response: { code: 'logo_invalid' } }), 'definite_failure');
    assert.strictEqual(outcome.classifyMutationOutcome({ status: 400, response: { code: 'settings_invalid' } }), 'definite_failure');
    assert.strictEqual(outcome.isAmbiguousMutationError({ status: 400, response: { code: 'settings_invalid' } }), false);
    assert.strictEqual(outcome.isAmbiguousMutationError({ status: 409, response: { code: 'stale_version' } }), false);
    assert.strictEqual(outcome.isAmbiguousMutationError({ status: 403, response: { code: 'staff_scope_forbidden' } }), false);
    assert.strictEqual(outcome.isAmbiguousMutationError({ status: 502, response: { code: 'polaris_unavailable' } }), false);
    assert.strictEqual(outcome.isAmbiguousMutationError({ status: 502, response: { code: 'patron_codes_unavailable' } }), false);
    assert.strictEqual(outcome.isAmbiguousMutationError({ status: 400, response: { code: 'patron_code_unknown' } }), false);
    for (const status of [400, 408, 422, 500, 502, 503, 504]) {
      assert.strictEqual(outcome.isAmbiguousMutationError({ status }), true, `unrecognized HTTP ${status}`);
    }
    assert.strictEqual(outcome.isAmbiguousMutationError({ status: 502, response: { code: 'unknown' } }), true);
    assert.strictEqual(outcome.isAmbiguousMutationError({ status: 0 }), true);
    assert.strictEqual(outcome.isAmbiguousMutationError(new dom.window.DOMException('Aborted', 'AbortError')), true);
    state.setStaffSession({ authenticated: true, accessAllowed: true, antiforgeryToken: 'test-token',
      staff: { id: '27', role: 'admin', libraryOrgId: '2', libraryName: 'Library Two', email: 'admin@example.org' } });

    const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'settings', 'canonical-library-settings-response.json'), 'utf8'));
    let versionNumber = 1;
    let loginNote = 'Original note';
    let libraryAlt = 'Library alt';
    let libraryImage = false;
    let organizationName = 'Library Two';
    let patronCodeName = 'Adult';
    let organizationGets = 0;
    let patronCodeGets = 0;
    let failure = null;
    let releaseDeferredFailure = null;
    let deferNextConfig = false;
    let releaseDeferredConfig = null;
    let failNextSettingsGet = false;
    let deferNextOrganizationGet = false;
    let deferNextPatronCodeGet = false;
    let failNextOrganizationGet = false;
    let releaseDeferredOrganizationGet = null;
    let releaseDeferredPatronCodeGet = null;
    const mutations = [];
    const version = () => `version-${versionNumber}`;
    const data = () => {
      const result = structuredClone(fixture);
      result.version = version();
      result.ui_text.loginNote = loginNote;
      result.effective.loginNote = loginNote;
      result.stored.configuredSystem.branding.altText = 'System alt';
      result.stored.libraryOverride.branding = {
        hasLogo: libraryImage, contentType: libraryImage ? 'image/png' : null,
        fileName: libraryImage ? 'library.png' : null, altText: libraryAlt,
        version: libraryImage || libraryAlt !== null ? `branding-${version()}` : null
      };
      result.stored.branding = { ...result.stored.libraryOverride.branding };
      result.effective.logoAltText = libraryAlt ?? 'System alt';
      result.effective.logoUrl = libraryImage ? '/library-logo.png' : '/system-logo.png';
      const form = projectLegacySettingsFixture(result);
      form.uiText.loginNote = loginNote;
      form.uiText.logoAlt = libraryAlt ?? 'System alt';
      form.uiText.logoUrl = result.effective.logoUrl;
      form.uiText.brandingInherited = !libraryImage && libraryAlt === null;
      return form;
    };
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      const method = String(options.method || 'GET').toUpperCase();
      if (requestUrl.includes('/api/asap/staff/legacy/settings?orgId=2')) {
        if (failNextSettingsGet) {
          failNextSettingsGet = false;
          return response(503, {});
        }
        return response(200, data());
      }
      if (requestUrl === '/api/asap/config') {
        if (deferNextConfig) {
          deferNextConfig = false;
          return new Promise(resolve => {
            releaseDeferredConfig = () => resolve(response(200, { publicationOptions: [] }));
          });
        }
        return response(200, { publicationOptions: [] });
      }
      if (requestUrl.includes('/api/asap/staff/legacy/patron-codes')) {
        patronCodeGets++;
        if (deferNextPatronCodeGet) {
          deferNextPatronCodeGet = false;
          const body = [{ id: '7', description: patronCodeName }];
          return new Promise(resolve => { releaseDeferredPatronCodeGet = () => resolve(response(200, body)); });
        }
        return response(200, [{ id: '7', description: patronCodeName }]);
      }
      if (requestUrl === '/api/asap/staff/legacy/organizations') {
        organizationGets++;
        if (failNextOrganizationGet) {
          failNextOrganizationGet = false;
          return response(503, {});
        }
        if (deferNextOrganizationGet) {
          deferNextOrganizationGet = false;
          const body = [{ id: 2, displayName: organizationName, isActive: true }];
          return new Promise(resolve => { releaseDeferredOrganizationGet = () => resolve(response(200, body)); });
        }
        return response(200, [{ id: 2, displayName: organizationName, isActive: true }]);
      }
      if (requestUrl.startsWith('/api/asap/staff/users')) return response(200, { users: [] });
      let family;
      if ((requestUrl === '/api/asap/staff/settings/library' ||
          requestUrl === '/api/asap/staff/legacy/settings') && method === 'POST') {
        const body = JSON.parse(options.body);
        family = body.action === 'reset' ? 'library-reset' : 'settings-save';
        mutations.push({ family, version: body.version });
        if (failure?.family !== family || failure.commit) {
          if (family === 'library-reset') { libraryAlt = null; libraryImage = false; }
          else loginNote = body.ui_text.loginNote;
          versionNumber++;
        }
      } else if (requestUrl.includes('/api/asap/staff/settings/logo')) {
        family = method === 'DELETE' ? 'logo-reset' : 'logo-post';
        const submittedVersion = method === 'DELETE'
          ? new URL(requestUrl, 'http://localhost').searchParams.get('version') : options.body.get('version');
        mutations.push({ family, version: submittedVersion });
        if (failure?.family !== family || failure.commit) {
          if (method === 'DELETE') { libraryAlt = null; libraryImage = false; }
          else {
            if (options.body.has('logoAlt')) libraryAlt = options.body.get('logoAlt').trim() || null;
            if (options.body.has('logo')) libraryImage = true;
          }
          versionNumber++;
        }
      } else if (requestUrl === '/api/asap/staff/organizations/sync' && method === 'POST') {
        family = 'sync';
        mutations.push({ family });
        if (failure?.family !== family || failure.commit) {
          versionNumber++;
          organizationName = `Synced library ${versionNumber}`;
          patronCodeName = `Synced code ${versionNumber}`;
        }
      } else {
        throw new Error(`Unexpected request: ${requestUrl}`);
      }
      if (failure?.family === family) {
        const failedRequest = failure;
        failure = null;
        if (failedRequest.defer) {
          return new Promise((_, reject) => {
            releaseDeferredFailure = () => reject(new TypeError('Connection lost'));
          });
        }
        if (failedRequest.status) {
          return response(failedRequest.status, failedRequest.code
            ? { code: failedRequest.code, message: failedRequest.message } : {});
        }
        throw new TypeError('Connection lost');
      }
      return response(200, family === 'sync' ? { code: 'synced', data: { changed: 1 } } : { code: 'saved' });
    };

    await settings.loadSettings({ skipAutoSync: true });
    const logoInput = document.getElementById('ui-logo-file');
    let selectedLogo = new dom.window.File([new Uint8Array(2 * 1024 * 1024 + 1)], 'oversized.png', { type: 'image/png' });
    Object.defineProperty(logoInput, 'files', { configurable: true, get: () => selectedLogo ? [selectedLogo] : [] });
    logoInput.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    const validationVersion = version();
    const validationMessage = 'The logo must be between 1 byte and 2 MB.';
    failure = { family: 'logo-post', commit: false, status: 400, code: 'settings_invalid', message: validationMessage };
    document.getElementById('btn-upload-logo').click();
    await flush(); await flush();
    assert.strictEqual(mutations.filter(item => item.family === 'logo-post').length, 1, 'validation failure must not retry');
    assert.strictEqual(mutations.at(-1).version, validationVersion);
    assert.strictEqual(version(), validationVersion, 'validation must not change Settings version');
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, validationVersion);
    assert.strictEqual(state.settingsReloadRequired, false);
    assert.strictEqual(state.settingsForm.inert, false);
    assert.strictEqual(document.getElementById('settings-reload-btn').classList.contains('hidden'), true);
    assert.strictEqual(logoInput.files[0], selectedLogo, 'validation must keep the selected file');
    const validationToast = Array.from(document.querySelectorAll('#toast-container .asap-toast')).at(-1).textContent;
    assert.match(validationToast, /The logo must be between 1 byte and 2 MB\./);
    assert.doesNotMatch(validationToast, /request result could not be confirmed/i);
    assert.doesNotMatch(document.getElementById('settings-msg').textContent, /request result could not be confirmed/i);

    const correctedPng = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13,
      73, 72, 68, 82, 0, 0, 0, 1, 0, 0, 0, 1]);
    selectedLogo = new dom.window.File([correctedPng], 'corrected.png', { type: 'image/png' });
    logoInput.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    document.getElementById('btn-upload-logo').click();
    await flush(); await flush();
    assert.strictEqual(mutations.filter(item => item.family === 'logo-post').length, 2, 'corrected input can be submitted');
    assert.strictEqual(mutations.at(-1).version, validationVersion, 'retry uses the unchanged Settings version');
    assert.strictEqual(state.settingsReloadRequired, false);
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, version(), 'successful retry must read back authoritative Settings');
    selectedLogo = null;
    logoInput.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    async function recover(expectedVersion) {
      document.getElementById('settings-reload-btn').click();
      await flush(); await flush();
      assert.strictEqual(state.settingsReloadRequired, false);
      assert.strictEqual(state.settingsForm.inert, false);
      assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, expectedVersion);
    }
    async function assertUncertain(family, oldVersion, attemptedDraft) {
      assert.strictEqual(state.settingsReloadRequired, true, family);
      assert.strictEqual(state.settingsForm.inert, true, family);
      assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, oldVersion);
      assert.match(document.getElementById('settings-msg').textContent, /result could not be confirmed.*reload settings/i);
      if (attemptedDraft) assert.strictEqual(attemptedDraft.element.value, attemptedDraft.value);
      const count = mutations.length;
      assert.strictEqual(await settings.saveSettings(), false);
      document.getElementById('btn-upload-logo').click();
      document.getElementById('btn-reset-logo').click();
      document.getElementById('btn-reset-library-settings').click();
      assert.strictEqual(await settings.syncPolarisOrganizations(), null);
      await flush();
      assert.strictEqual(mutations.length, count, `${family} must not retry or allow other mutations`);
    }
    const note = document.getElementById('ui-login-note');
    for (const commit of [true, false]) {
      const oldVersion = version();
      note.value = commit ? 'Committed draft' : 'Uncommitted draft';
      note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      failure = { family: 'settings-save', commit, status: commit ? 502 : 500 };
      assert.strictEqual(await settings.saveSettings(), false);
      await assertUncertain('settings-save', oldVersion, { element: note, value: note.value });
      await recover(version());
      assert.strictEqual(note.value, loginNote);
    }

    const alt = document.getElementById('ui-logo-alt');
    const oldBrandingVersion = version();
    alt.value = 'Unconfirmed branding alt';
    alt.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    failure = { family: 'logo-post', commit: true, status: 504 };
    document.getElementById('btn-upload-logo').click();
    await flush(); await flush();
    await assertUncertain('logo-post', oldBrandingVersion, { element: alt, value: 'Unconfirmed branding alt' });
    await recover(version());
    assert.strictEqual(alt.value, 'Unconfirmed branding alt');

    libraryImage = true; versionNumber++;
    await settings.loadSettings({ skipAutoSync: true });
    const oldResetVersion = version();
    failure = { family: 'logo-reset', commit: true };
    document.getElementById('btn-reset-logo').click();
    await flush(); document.getElementById('confirm-dialog-ok').click();
    await flush(); await flush();
    await assertUncertain('logo-reset', oldResetVersion);
    await recover(version());
    assert.strictEqual(alt.value, 'System alt');
    assert.strictEqual(document.getElementById('ui-branding-status').textContent, 'System Default');
    assert.strictEqual(document.getElementById('btn-reset-logo').classList.contains('hidden'), true);

    libraryImage = true; libraryAlt = 'Library alt'; versionNumber++;
    await settings.loadSettings({ skipAutoSync: true });
    document.getElementById('btn-reset-logo').click();
    await flush(); document.getElementById('confirm-dialog-ok').click();
    await flush(); await flush();
    assert.strictEqual(document.getElementById('ui-branding-status').textContent, 'System Default');
    assert.strictEqual(document.getElementById('btn-reset-logo').classList.contains('hidden'), true);
    assert.strictEqual(alt.value, 'System alt');
    assert.match(Array.from(document.querySelectorAll('#toast-container .asap-toast')).at(-1).textContent,
      /branding reset to system defaults/i);

    // An alt-only clear removes the last override; an image keeps branding local.
    libraryAlt = 'Library alt'; versionNumber++;
    await settings.loadSettings({ skipAutoSync: true });
    const oldClearVersion = version();
    alt.value = '';
    failure = { family: 'logo-post', commit: true };
    document.getElementById('btn-upload-logo').click();
    await flush(); await flush();
    await assertUncertain('alt-only clear', oldClearVersion, { element: alt, value: '' });
    await recover(version());
    assert.strictEqual(document.getElementById('ui-branding-status').textContent, 'System Default');
    assert.strictEqual(document.getElementById('btn-reset-logo').classList.contains('hidden'), true);
    assert.strictEqual(alt.value, 'System alt');
    libraryImage = true; libraryAlt = 'Library alt'; versionNumber++;
    await settings.loadSettings({ skipAutoSync: true });
    alt.value = '';
    document.getElementById('btn-upload-logo').click();
    await flush(); await flush();
    assert.strictEqual(document.getElementById('ui-branding-status').textContent, 'Library Override');
    assert.strictEqual(document.getElementById('btn-reset-logo').classList.contains('hidden'), false);
    assert.strictEqual(alt.value, 'System alt');

    const oldLibraryResetVersion = version();
    failure = { family: 'library-reset', commit: true };
    document.getElementById('btn-reset-library-settings').click();
    await flush(); document.getElementById('confirm-dialog-ok').click();
    await flush(); await flush();
    await assertUncertain('library-reset', oldLibraryResetVersion);
    await recover(version());

    for (const commit of [true, false]) {
      const oldSyncVersion = version();
      const oldOrganizationName = organizationName;
      const oldPatronCodeName = patronCodeName;
      failure = { family: 'sync', commit, status: 502 };
      await assert.rejects(settings.syncPolarisOrganizations(), /Response/);
      assert.strictEqual(state.settingsSyncInProgress, false);
      assert.strictEqual(document.getElementById('btn-sync-organizations').disabled, true);
      assert.strictEqual(state.organizationsStatus, 'not_loaded');
      assert.doesNotMatch(document.getElementById('enabled-libraries-checkbox-container').textContent, /Organizations loading/);
      assert.doesNotMatch(document.getElementById('allowed-patron-code-container').textContent, /Patron codes loading/);
      await assertUncertain('sync', oldSyncVersion);
      const syncCount = mutations.filter(item => item.family === 'sync').length;
      const oldOrganizationGets = organizationGets;
      const oldPatronCodeGets = patronCodeGets;
      await recover(version());
      await settings.renderLibraryParticipationCheckboxes();
      await flush();
      assert.strictEqual(mutations.filter(item => item.family === 'sync').length, syncCount, 'reload must not repeat Polaris sync');
      assert.ok(organizationGets > oldOrganizationGets);
      assert.ok(patronCodeGets > oldPatronCodeGets);
      assert.match(document.getElementById('enabled-libraries-checkbox-container').textContent, new RegExp(organizationName));
      assert.match(document.getElementById('allowed-patron-code-container').textContent, new RegExp(patronCodeName));
      assert.strictEqual(organizationName === oldOrganizationName, !commit);
      assert.strictEqual(patronCodeName === oldPatronCodeName, !commit);
      assert.strictEqual(state.organizationsStatus, 'loaded');
      assert.strictEqual(state.settingsReloadRequired, false);
      assert.strictEqual(document.getElementById('btn-sync-organizations').disabled, false);
    }

    failure = { family: 'sync', commit: false, status: 502, code: 'polaris_unavailable' };
    await assert.rejects(settings.syncPolarisOrganizations(), /Response/);
    assert.strictEqual(state.settingsReloadRequired, false);
    assert.strictEqual(state.organizationsStatus, 'error');
    assert.doesNotMatch(document.getElementById('allowed-patron-code-container').textContent, /loading/i);
    assert.strictEqual(document.getElementById('btn-sync-organizations').disabled, false);
    assert.ok(await settings.syncPolarisOrganizations(), 'definite pre-commit rejection must permit retry');
    assert.strictEqual(state.settingsReloadRequired, false);

    // A reference GET started before sync must not overwrite the sync outcome when it finishes late.
    document.getElementById('enabled-libraries-checkbox-container').removeAttribute('data-loaded');
    deferNextOrganizationGet = true;
    deferNextPatronCodeGet = true;
    const pendingOrganizationGet = settings.renderLibraryParticipationCheckboxes();
    patronCodes.updatePatronCodesStatusUi('not_loaded', 'Refreshing choices.');
    const pendingPatronCodeGet = patronCodes.renderPatronCodeEligibilityOptions('7');
    assert.strictEqual(typeof releaseDeferredOrganizationGet, 'function');
    assert.strictEqual(typeof releaseDeferredPatronCodeGet, 'function');
    failure = { family: 'sync', commit: false, status: 503 };
    await assert.rejects(settings.syncPolarisOrganizations(), /Response/);
    releaseDeferredOrganizationGet();
    releaseDeferredPatronCodeGet();
    await Promise.all([pendingOrganizationGet, pendingPatronCodeGet]);
    assert.strictEqual(state.organizationsStatus, 'not_loaded');
    assert.strictEqual(document.getElementById('enabled-libraries-checkbox-container').getAttribute('data-loaded'), null);
    assert.strictEqual(document.getElementById('allowed-patron-code-container').getAttribute('data-loaded'), null);
    failNextSettingsGet = true;
    document.getElementById('settings-reload-btn').click();
    await flush(); await flush();
    assert.strictEqual(state.settingsReloadRequired, true, 'failed recovery must retain the mutation guard');
    assert.strictEqual(state.settingsForm.inert, true);
    await recover(version());
    await settings.renderLibraryParticipationCheckboxes();
    assert.strictEqual(state.organizationsStatus, 'loaded');

    // A local organization-list failure after a confirmed sync does not make the Settings version uncertain.
    failNextOrganizationGet = true;
    assert.ok(await settings.syncPolarisOrganizations());
    assert.strictEqual(state.settingsReloadRequired, false);
    assert.strictEqual(state.organizationsStatus, 'error');
    assert.match(document.getElementById('organizations-sync-result').textContent, /local organization choices could not be loaded/i);
    assert.doesNotMatch(document.getElementById('allowed-patron-code-container').textContent, /loading/i);

    // An old sync failure after the context is superseded must not guard the new context or leave old loading labels.
    failure = { family: 'sync', commit: false, defer: true };
    const supersededSync = settings.syncPolarisOrganizations();
    assert.strictEqual(state.organizationsStatus, 'loading');
    state.setCurrentLibraryContextOrgId('3');
    state.incrementLibraryContextLoadSerial();
    releaseDeferredFailure();
    await assert.rejects(supersededSync, /Connection lost/);
    assert.strictEqual(state.settingsReloadRequired, false);
    assert.notStrictEqual(state.organizationsStatus, 'loading');
    assert.notStrictEqual(patronCodes.getPatronCodesStatus(), 'loading');
    await settings.loadSettings({ skipAutoSync: true });

    state.setSettingsReloadRequired(true);
    deferNextConfig = true;
    document.getElementById('settings-reload-btn').click();
    await flush();
    assert.strictEqual(typeof releaseDeferredConfig, 'function');
    state.setCurrentLibraryContextOrgId('3');
    state.incrementLibraryContextLoadSerial();
    releaseDeferredConfig();
    await flush(); await flush();
    assert.strictEqual(state.settingsReloadRequired, true,
      'an old-context auxiliary refresh must not clear the new context recovery guard');
    await settings.loadSettings({ skipAutoSync: true });
    assert.strictEqual(state.settingsReloadRequired, false);

    // A lost response from a superseded library must not mark the new context uncertain.
    note.value = 'Old library draft';
    note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    failure = { family: 'settings-save', commit: true, defer: true };
    const pendingOldLibrarySave = settings.saveSettings();
    assert.strictEqual(typeof releaseDeferredFailure, 'function');
    state.setCurrentLibraryContextOrgId('3');
    state.incrementLibraryContextLoadSerial();
    note.value = 'New library draft';
    releaseDeferredFailure();
    assert.strictEqual(await pendingOldLibrarySave, false);
    assert.strictEqual(state.settingsReloadRequired, false);
    assert.strictEqual(state.settingsForm.inert, false);
    assert.strictEqual(note.value, 'New library draft');
    dom.window.close();
    console.log('Ambiguous Settings writes require explicit authoritative recovery across all mutation families');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exit(1); });
