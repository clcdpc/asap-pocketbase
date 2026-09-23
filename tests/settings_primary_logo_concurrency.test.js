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
    let nextLibrarySettingsReadFails = false;
    let settingsReads = 0;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.includes('/api/asap/staff/settings/library?orgId=2')) {
        settingsReads++;
        if (nextLibrarySettingsReadFails) {
          nextLibrarySettingsReadFails = false;
          return response(503, { code: 'settings_unavailable', message: 'Settings refresh temporarily unavailable.' });
        }
        return response(200, settingsData(serverVersion, serverAltText));
      }
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes')) return response(200, { code: 'ok', data: [] });
      if (requestUrl.startsWith('/api/asap/staff/users')) return response(200, { users: [] });
      if (requestUrl === '/api/asap/config') return response(200, { publicationOptions: [] });
      if (requestUrl === '/api/asap/staff/settings/library' && String(options.method || 'GET').toUpperCase() === 'POST') {
        const payload = JSON.parse(options.body);
        libraryResets.push(payload);
        assert.strictEqual(payload.orgId, '2');
        assert.strictEqual(payload.action, 'reset');
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

    document.getElementById('ui-logo-alt').value = 'Uploaded logo description';
    document.getElementById('btn-upload-logo').click();
    await flush();
    await flush();
    assert.strictEqual(logoMutations[0].method, 'POST');
    assert.strictEqual(logoMutations[0].submittedVersion, 'version-1', 'logo upload must send the loaded Settings version');
    assert.strictEqual(settingsReads, 2, 'successful branding updates must reload the current Settings response');
    assert.strictEqual(document.getElementById('ui-logo-alt').value, 'Uploaded logo description');

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

    document.getElementById('btn-reset-library-settings').click();
    await flush();
    document.getElementById('confirm-dialog-ok').click();
    await flush();
    await flush();
    assert.deepStrictEqual(libraryResets[0], { orgId: '2', action: 'reset', version: 'version-4' },
      'Reset Library Settings must send its current Settings version and exact selected scope');
    assert.strictEqual(serverVersion, 'version-5');
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

    await settings.loadSettings();
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, 'version-7');
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

    dom.window.close();
    console.log('Primary Settings direct actions send current scope versions and reload stale state without retry');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
