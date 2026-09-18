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
    statusText: 'OK',
    json: async () => body
  };
}

function settingsData(note, version) {
  const emptySet = { exists: false, values: [] };
  const systemNote = 'System login note';
  const libraryOverride = note === systemNote ? null : {
    workflow: null,
    patron: { loginNote: note },
    email: null,
    publicationOptions: emptySet,
    commonCreators: emptySet,
    allowedPatronCodeIds: emptySet,
    providers: [],
    formats: [],
    templates: [],
    branding: { hasLogo: false, altText: null }
  };
  const configuredSystem = {
    workflow: {},
    patron: { loginNote: systemNote },
    email: { fromAddress: 'system@example.org', fromName: 'System' },
    publicationOptions: emptySet,
    commonCreators: emptySet,
    allowedPatronCodeIds: emptySet,
    providers: [],
    formats: [],
    templates: [],
    branding: { hasLogo: false, altText: 'System alt' }
  };
  return {
    orgId: '2',
    version,
    organization: { id: 2, name: 'Library Two', abbreviation: 'TWO', active: true, version },
    stored: {
      systemSettings: {},
      polaris: {},
      configuredSystem,
      libraryOverride,
      workflow: {},
      patron: libraryOverride?.patron || configuredSystem.patron,
      email: {},
      origins: [],
      publicationOptions: [],
      commonCreators: [],
      allowedPatronCodeIds: [],
      providers: [],
      formats: [],
      customFields: [],
      templates: [],
      autoClaimRules: [],
      branding: { hasLogo: false, altText: null }
    },
    effective: {
      allowedPatronCodeIds: [],
      publicationOptions: [],
      commonCreators: [],
      externalSearchProviders: [],
      formats: [],
      customFields: [],
      email: { fromAddress: 'system@example.org', fromName: 'System' },
      logoAltText: 'System alt',
      loginNote: note
    },
    workflow: {},
    ui_text: { loginNote: note },
    emails: { fromAddress: 'system@example.org', fromName: 'System', templates: [] }
  };
}

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
  await Promise.resolve();
}

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-save-completion-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const settingsModule = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings.js')).href);
    const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.Node = dom.window.Node;
    global.Event = dom.window.Event;

    let saved = false;
    let settingsRequests = 0;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/staff/session')) {
        return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
      }
      if (requestUrl.includes('/api/asap/staff/settings?orgId=2')) {
        settingsRequests += 1;
        return response(200, saved
          ? settingsData('Saved library note', 'after-save')
          : settingsData('System login note', 'before-save'));
      }
      if (requestUrl.endsWith('/api/asap/staff/organizations')) return response(200, []);
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) {
        return response(200, { code: 'ok', data: [] });
      }
      if (requestUrl.endsWith('/api/asap/staff/settings')) {
        assert.strictEqual(JSON.parse(options.body).patron.loginNote, 'Saved library note');
        saved = true;
        return response(200, { code: 'saved', data: { version: 'after-save' } });
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    const controller = settingsModule.createSettingsController({
      root: document.getElementById('settings-view'),
      tab: document.getElementById('settings-view-tab'),
      announce: () => {},
      getStaff: () => ({ role: 'admin', organizationId: 2 })
    });
    controller.bind();
    controller.setStaff({
      id: '2',
      tenantId: 'tenant-1',
      objectId: 'object-2',
      role: 'admin',
      organizationId: 2
    });
    await controller.activate();

    const override = document.querySelector('[data-setting-section="patron"][data-setting-key="loginNote"] .settings-override-toggle');
    override.checked = true;
    override.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    const note = document.getElementById('patron-login-note');
    note.value = 'Saved library note';
    note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true,
      cancelable: true
    }));

    for (let attempt = 0; attempt < 20 && settingsRequests < 2; attempt++) await flush();
    for (let attempt = 0; attempt < 20; attempt++) await flush();

    assert.strictEqual(settingsRequests, 2);
    assert.strictEqual(document.getElementById('settings-version').value, 'after-save');
    assert.strictEqual(document.getElementById('settings-save').disabled, true);
    assert.strictEqual(document.getElementById('settings-save-title').textContent, 'No changes');
    assert.strictEqual(controller.isDirty(), false);
    dom.window.close();
    console.log('Settings save completion refreshes the baseline and leaves the form clean');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
