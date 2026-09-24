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

function settingsData(organizationId) {
  const emptySet = { exists: false, values: [] };
  const configuredSystem = {
    workflow: {},
    patron: {},
    email: { fromAddress: 'system@example.org', fromName: 'System' },
    publicationOptions: emptySet,
    commonCreators: emptySet,
    allowedPatronCodeIds: emptySet,
    providers: [],
    formats: [],
    templates: [],
    branding: { hasLogo: false, altText: 'System inherited alt' }
  };
  return {
    orgId: String(organizationId),
    version: `library-${organizationId}-version`,
    organization: { id: organizationId, name: 'Library Two', abbreviation: 'TWO', active: true },
    stored: {
      systemSettings: {},
      polaris: {},
      configuredSystem,
      libraryOverride: organizationId === 1 ? null : {
        workflow: null,
        patron: null,
        email: null,
        publicationOptions: emptySet,
        commonCreators: emptySet,
        allowedPatronCodeIds: emptySet,
        providers: [],
        formats: [],
        templates: [],
        branding: { hasLogo: false, altText: null }
      },
      workflow: {},
      patron: {},
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
      logoAltText: 'System inherited alt'
    },
    workflow: {},
    ui_text: {},
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
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-logo-'));
  fs.cpSync(path.join(frontendRoot, 'staff-next'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const settingsModule = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings.js')).href);
    const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff-next', 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.FormData = dom.window.FormData;
    global.Blob = dom.window.Blob;
    global.Node = dom.window.Node;
    global.Event = dom.window.Event;
    global.File = dom.window.File;
    window.confirm = () => true;

    const logoRequests = [];
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/staff/session')) {
        return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
      }
      if (requestUrl.includes('/api/asap/staff/settings?orgId=')) {
        return response(200, settingsData(requestUrl.endsWith('orgId=system') ? 1 : 2));
      }
      if (requestUrl.endsWith('/api/asap/staff/organizations')) {
        return response(200, [
          { id: 2, name: 'Library Two', abbreviation: 'TWO', active: true, version: 'org-2' }
        ]);
      }
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) {
        return response(200, { code: 'ok', data: [] });
      }
      if (requestUrl.includes('/api/asap/staff/settings/logo')) {
        logoRequests.push({ url: requestUrl, body: options.body });
        return response(200, { code: 'logo_saved', data: { version: 'logo-version' } });
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    const controller = settingsModule.createSettingsController({
      root: document.getElementById('settings-view'),
      tab: document.getElementById('settings-view-tab'),
      announce: () => {},
      getStaff: () => ({ role: 'super_admin' })
    });
    controller.bind();
    controller.setStaff({
      id: '1',
      tenantId: 'tenant-1',
      objectId: 'object-1',
      role: 'super_admin',
      organizationId: 1
    });
    await controller.activate();

    const scope = document.getElementById('settings-scope');
    scope.value = '2';
    scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    await flush();
    assert.strictEqual(scope.value, '2');

    const alt = document.getElementById('branding-alt');
    const altOverride = alt.closest('[data-setting-section]').querySelector('.settings-override-toggle');
    assert.strictEqual(altOverride.checked, false);
    assert.strictEqual(alt.value, 'System inherited alt');

    const image = new dom.window.File([new Uint8Array([137, 80, 78, 71])], 'logo.png', { type: 'image/png' });
    Object.defineProperty(document.getElementById('branding-logo'), 'files', { value: [image], configurable: true });
    document.getElementById('save-branding-logo').click();
    await flush();
    await flush();

    assert.strictEqual(logoRequests.length, 1);
    assert.strictEqual(logoRequests[0].url, '/api/asap/staff/settings/logo?orgId=2');
    assert.strictEqual(logoRequests[0].body.get('version'), 'library-2-version');
    assert.strictEqual(logoRequests[0].body.get('orgId'), null);
    assert.strictEqual(logoRequests[0].body.get('logoAlt'), null);
    assert.strictEqual(logoRequests[0].body.get('logo').name, 'logo.png');

    document.getElementById('clear-branding-logo').click();
    await flush();
    await flush();

    assert.strictEqual(logoRequests.length, 2);
    assert.strictEqual(logoRequests[1].url, '/api/asap/staff/settings/logo?orgId=2');
    assert.strictEqual(logoRequests[1].body.get('version'), 'library-2-version');
    assert.strictEqual(logoRequests[1].body.get('clearLogo'), 'true');
    assert.strictEqual(logoRequests[1].body.get('logo'), null);
    assert.strictEqual(logoRequests[1].body.get('logoAlt'), null);

    dom.window.close();
    console.log('Settings logo multipart scope and independent-alt checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
