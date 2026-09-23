const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

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
    const outcome = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'mutation-outcome.js')).href);
    assert.strictEqual(outcome.isAmbiguousMutationError({ status: 422 }), false);
    assert.strictEqual(outcome.isAmbiguousMutationError({ status: 409, response: { code: 'stale_version' } }), false);
    assert.strictEqual(outcome.isAmbiguousMutationError({ status: 400, message: 'Cannot abort operation' }), false);
    assert.strictEqual(outcome.isAmbiguousMutationError({ status: 0 }), true);
    assert.strictEqual(outcome.isAmbiguousMutationError(new dom.window.DOMException('Aborted', 'AbortError')), true);
    state.setStaffSession({ authenticated: true, accessAllowed: true, antiforgeryToken: 'test-token',
      staff: { id: '27', role: 'admin', organizationId: '2', organizationName: 'Library Two', userPrincipalName: 'admin@example.org' } });

    const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'settings', 'canonical-library-settings-response.json'), 'utf8'));
    let versionNumber = 1;
    let loginNote = 'Original note';
    let libraryAlt = 'Library alt';
    let libraryImage = false;
    let failure = null;
    let releaseDeferredFailure = null;
    let deferNextConfig = false;
    let releaseDeferredConfig = null;
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
      return result;
    };
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      const method = String(options.method || 'GET').toUpperCase();
      if (requestUrl.includes('/api/asap/staff/settings/library?orgId=2')) return response(200, data());
      if (requestUrl === '/api/asap/config') {
        if (deferNextConfig) {
          deferNextConfig = false;
          return new Promise(resolve => {
            releaseDeferredConfig = () => resolve(response(200, { publicationOptions: [] }));
          });
        }
        return response(200, { publicationOptions: [] });
      }
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes')) return response(200, { code: 'ok', data: [] });
      if (requestUrl.startsWith('/api/asap/staff/users')) return response(200, { users: [] });
      let family;
      if (requestUrl === '/api/asap/staff/settings/library' && method === 'POST') {
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
        if (failure?.family !== family || failure.commit) versionNumber++;
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
        throw new TypeError('Connection lost');
      }
      return response(200, family === 'sync' ? { code: 'synced', data: { changed: 1 } } : { code: 'saved' });
    };

    await settings.loadSettings({ skipAutoSync: true });
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
      failure = { family: 'settings-save', commit };
      assert.strictEqual(await settings.saveSettings(), false);
      await assertUncertain('settings-save', oldVersion, { element: note, value: note.value });
      await recover(version());
      assert.strictEqual(note.value, loginNote);
    }

    const alt = document.getElementById('ui-logo-alt');
    const oldBrandingVersion = version();
    alt.value = 'Unconfirmed branding alt';
    alt.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    failure = { family: 'logo-post', commit: true };
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

    const oldSyncVersion = version();
    failure = { family: 'sync', commit: true };
    await assert.rejects(settings.syncPolarisOrganizations(), /Connection lost/);
    await assertUncertain('sync', oldSyncVersion);
    await recover(version());

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
