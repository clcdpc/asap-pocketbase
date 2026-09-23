const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

function response(status, body) {
  return { ok: status >= 200 && status < 300, status, statusText: status === 503 ? 'Unavailable' : 'OK', json: async () => body };
}

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
  await Promise.resolve();
}

(async () => {
  const frontendRoot = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-branding-closure-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
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

    const state = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'state.js')).href);
    const settings = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings.js')).href);
    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'test-token',
      staff: { id: '27', role: 'admin', organizationId: '2', organizationName: 'Library Two', userPrincipalName: 'admin@example.org' }
    });

    const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'settings', 'canonical-library-settings-response.json'), 'utf8'));
    let versionNumber = 1;
    let systemAlt = 'System inherited alt';
    let libraryAlt = null;
    let libraryHasLogo = false;
    let loginNote = 'Original login note';
    let nextSettingsReadFails = false;
    let configFails = false;
    let configAlt = 'Original app logo';
    let configReads = 0;
    const brandingMutations = [];
    const settingsMutations = [];
    const version = () => `version-${versionNumber}`;
    function settingsData() {
      const data = structuredClone(fixture);
      data.version = version();
      data.stored.configuredSystem.branding.altText = systemAlt;
      data.stored.libraryOverride.branding = {
        hasLogo: libraryHasLogo,
        contentType: libraryHasLogo ? 'image/png' : null,
        fileName: libraryHasLogo ? 'selected.png' : null,
        altText: libraryAlt,
        version: libraryHasLogo || libraryAlt !== null ? `branding-${version()}` : null
      };
      data.stored.branding = { ...data.stored.libraryOverride.branding };
      data.effective.logoAltText = libraryAlt ?? systemAlt;
      data.effective.logoUrl = libraryHasLogo ? '/library-logo.png' : '/system-logo.png';
      data.effective.loginNote = loginNote;
      data.ui_text.loginNote = loginNote;
      return data;
    }

    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.includes('/api/asap/staff/settings/library?orgId=2')) {
        if (nextSettingsReadFails) {
          nextSettingsReadFails = false;
          return response(503, { code: 'settings_unavailable', message: 'Settings unavailable.' });
        }
        return response(200, settingsData());
      }
      if (requestUrl === '/api/asap/staff/settings/library' && options.method === 'POST') {
        const payload = JSON.parse(options.body);
        settingsMutations.push(payload);
        loginNote = payload.ui_text.loginNote;
        versionNumber++;
        return response(200, { code: 'saved' });
      }
      if (requestUrl.includes('/api/asap/staff/settings/logo') && options.method === 'POST') {
        const body = options.body;
        brandingMutations.push({ version: body.get('version'), alt: body.get('logoAlt'), hasAlt: body.has('logoAlt'), file: body.get('logo') });
        if (body.has('logoAlt')) libraryAlt = body.get('logoAlt').trim() || null;
        if (body.has('logo')) libraryHasLogo = true;
        versionNumber++;
        return response(200, { code: 'branding_saved' });
      }
      if (requestUrl.includes('/api/asap/staff/settings/logo') && options.method === 'DELETE') {
        throw new Error('A selected file must be cleared before branding reset');
      }
      if (requestUrl === '/api/asap/config') {
        configReads++;
        return configFails
          ? response(503, { code: 'config_unavailable', message: 'Config unavailable.' })
          : response(200, { logoUrl: '/app-logo.png', logoAlt: configAlt, publicationOptions: [] });
      }
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes')) return response(200, { code: 'ok', data: [] });
      if (requestUrl.startsWith('/api/asap/staff/users')) return response(200, { users: [] });
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    await settings.loadSettings({ skipAutoSync: true });
    const alt = document.getElementById('ui-logo-alt');
    const fileInput = document.getElementById('ui-logo-file');
    const clearFile = document.getElementById('btn-clear-selected-logo');
    const preview = document.getElementById('ui-logo-preview');
    const loginNoteInput = document.getElementById('ui-login-note');
    const saveButton = document.getElementById('settings-save-btn');
    assert.strictEqual(alt.value, systemAlt);
    assert.strictEqual(state.currentLegacySettingsFormModel.provenance.libraryBranding.altText, null);

    // JSDOM does not implement the file picker. Model its FileList, including native clearing via input.value = ''.
    let selectedFile = null;
    const nativeValue = Object.getOwnPropertyDescriptor(dom.window.HTMLInputElement.prototype, 'value');
    Object.defineProperty(fileInput, 'files', { configurable: true, get: () => selectedFile ? [selectedFile] : [] });
    Object.defineProperty(fileInput, 'value', {
      configurable: true,
      get() { return selectedFile ? 'C:\\fakepath\\selected.png' : nativeValue.get.call(this); },
      set(value) { if (value === '') selectedFile = null; nativeValue.set.call(this, value); }
    });
    function selectFile() {
      selectedFile = new dom.window.File(['logo'], 'selected.png', { type: 'image/png' });
      fileInput.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    }

    const authoritativePreview = preview.src;
    selectFile();
    for (let attempt = 0; attempt < 20 && !preview.src.startsWith('data:image/png'); attempt++) {
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    assert.match(preview.src, /^data:image\/png/, 'the selected file must have a temporary preview before clearing');
    loginNoteInput.value = 'Keep this draft';
    loginNoteInput.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.strictEqual(clearFile.classList.contains('hidden'), false);
    assert.strictEqual(saveButton.disabled, true);
    assert.strictEqual(await settings.saveSettings(), false);
    document.getElementById('btn-upload-logo').click();
    await flush();
    assert.strictEqual(brandingMutations.length, 0);
    clearFile.click();
    assert.strictEqual(fileInput.files.length, 0);
    assert.strictEqual(fileInput.value, '');
    assert.strictEqual(preview.src, authoritativePreview);
    assert.strictEqual(document.querySelector('.custom-file-label[for="ui-logo-file"]').textContent, 'Choose image...');
    assert.strictEqual(clearFile.classList.contains('hidden'), true);
    assert.strictEqual(loginNoteInput.value, 'Keep this draft');
    assert.strictEqual(saveButton.disabled, false);
    assert.strictEqual(await settings.saveSettings({ clearDelay: 0 }), true);
    assert.strictEqual(settingsMutations.length, 1);
    assert.strictEqual(settingsMutations[0].ui_text.loginNote, 'Keep this draft');

    // Inherited display text is never sent as a library override on a no-op or image-only save.
    document.getElementById('btn-upload-logo').click();
    await flush();
    assert.strictEqual(brandingMutations.length, 0);
    selectFile();
    document.getElementById('btn-upload-logo').click();
    await flush();
    await flush();
    assert.strictEqual(brandingMutations.length, 1);
    assert.strictEqual(brandingMutations[0].file.name, 'selected.png');
    assert.strictEqual(brandingMutations[0].hasAlt, false);
    assert.strictEqual(libraryAlt, null);
    selectFile();
    document.getElementById('btn-reset-logo').click();
    await flush();
    assert.strictEqual(fileInput.files.length, 1, 'reset must not silently discard a newly selected logo');
    clearFile.click();

    systemAlt = 'Updated system alt';
    versionNumber++;
    await settings.loadSettings({ skipAutoSync: true });
    assert.strictEqual(alt.value, systemAlt, 'image-only branding must continue to inherit future system alt changes');
    document.getElementById('btn-upload-logo').click();
    await flush();
    assert.strictEqual(brandingMutations.length, 1, 'an explicit logo image with inherited alt remains a no-op');

    alt.value = 'Intentional library alt';
    alt.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    document.getElementById('btn-upload-logo').click();
    await flush();
    await flush();
    assert.strictEqual(brandingMutations[1].alt, 'Intentional library alt');
    assert.strictEqual(brandingMutations[1].file, null);
    document.getElementById('btn-upload-logo').click();
    await flush();
    assert.strictEqual(brandingMutations.length, 2, 'unchanged explicit override is a no-op');

    alt.value = 'Another intentional alt';
    alt.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    nextSettingsReadFails = true;
    document.getElementById('btn-upload-logo').click();
    await flush();
    await flush();
    assert.strictEqual(brandingMutations[2].alt, 'Another intentional alt');
    assert.strictEqual(state.settingsReloadRequired, true);
    configAlt = 'Updated app logo';
    const configReadsBeforeRecovery = configReads;
    nextSettingsReadFails = true;
    document.getElementById('settings-reload-btn').click();
    await flush();
    await flush();
    assert.strictEqual(state.settingsReloadRequired, true, 'a failed explicit reload must remain recoverable');
    assert.strictEqual(configReads, configReadsBeforeRecovery, 'a failed Settings reload must not refresh config');
    document.getElementById('settings-reload-btn').click();
    await flush();
    await flush();
    assert.strictEqual(configReads, configReadsBeforeRecovery + 1);
    assert.strictEqual(document.getElementById('nav-logo').alt, configAlt);
    assert.match(document.getElementById('nav-logo').src, /app-logo\.png/);
    assert.strictEqual(state.settingsReloadRequired, false);
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, version());

    alt.value = 'Third intentional alt';
    alt.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    nextSettingsReadFails = true;
    document.getElementById('btn-upload-logo').click();
    await flush();
    await flush();
    assert.strictEqual(state.settingsReloadRequired, true);
    configFails = true;
    document.getElementById('settings-reload-btn').click();
    await flush();
    await flush();
    assert.strictEqual(state.settingsReloadRequired, false, 'auxiliary config failure must not invalidate Settings');
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, version());
    assert.strictEqual(state.settingsForm.inert, false);
    assert.match(Array.from(document.querySelectorAll('#toast-container .asap-toast')).at(-1).textContent,
      /settings reloaded, but app configuration could not be refreshed/i);

    configFails = false;
    alt.value = '';
    alt.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    document.getElementById('btn-upload-logo').click();
    await flush();
    await flush();
    assert.strictEqual(brandingMutations.at(-1).hasAlt, true, 'clearing an explicit override sends an empty alt field');
    assert.strictEqual(brandingMutations.at(-1).alt, '');
    assert.strictEqual(libraryAlt, null);
    assert.strictEqual(libraryHasLogo, true, 'clearing alt must preserve the library logo image');
    systemAlt = 'Future inherited alt';
    versionNumber++;
    await settings.loadSettings({ skipAutoSync: true });
    assert.strictEqual(alt.value, systemAlt);

    dom.window.close();
    console.log('Branding clear, inheritance, and explicit Settings recovery use the real UI controls');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
