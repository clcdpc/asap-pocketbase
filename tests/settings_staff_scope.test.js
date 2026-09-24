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

function settingsData() {
  const emptySet = { exists: false, values: [] };
  return {
    orgId: '2',
    version: 'library-2-version',
    organization: { id: 2, name: 'Library Two', abbreviation: 'TWO', active: true },
    stored: {
      systemSettings: {},
      polaris: {},
      configuredSystem: {
        workflow: {},
        patron: {},
        email: { fromAddress: 'system@example.org', fromName: 'System' },
        publicationOptions: emptySet,
        commonCreators: emptySet,
        allowedPatronCodeIds: emptySet,
        providers: [],
        formats: [],
        templates: [],
        branding: { hasLogo: false, altText: 'System alt' }
      },
      libraryOverride: {
        workflow: {},
        patron: { loginNote: 'Existing library note' },
        email: {},
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
      logoAltText: 'System alt'
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
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-staff-scope-'));
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
    global.Node = dom.window.Node;
    global.Event = dom.window.Event;
    window.confirm = () => true;

    const settingsRequests = [];
    let saveBody;
    let saveCompleted;
    const saveFinished = new Promise(resolve => { saveCompleted = resolve; });
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/staff/session')) {
        return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
      }
      if (requestUrl.includes('/api/asap/staff/settings?orgId=')) {
        settingsRequests.push(requestUrl);
        return response(200, settingsData());
      }
      if (requestUrl.endsWith('/api/asap/staff/organizations')) {
        return response(200, []);
      }
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) {
        return response(200, { code: 'ok', data: [] });
      }
      if (requestUrl.endsWith('/api/asap/staff/settings')) {
        saveBody = JSON.parse(options.body);
        saveCompleted();
        return response(200, { code: 'saved', data: { version: 'library-2-saved' } });
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

    assert.deepStrictEqual(settingsRequests, ['/api/asap/staff/settings?orgId=2']);
    assert.strictEqual(document.getElementById('settings-scope').value, '2');
    assert.strictEqual(document.getElementById('settings-scope-field').hidden, true);

    const note = document.getElementById('patron-login-note');
    note.value = 'Own library edit';
    note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true,
      cancelable: true
    }));
    await saveFinished;
    await flush();

    assert.strictEqual(saveBody.orgId, '2');
    assert.strictEqual(saveBody.patron.loginNote, 'Own library edit');
    assert.strictEqual(Object.prototype.hasOwnProperty.call(saveBody, 'systemSettings'), false);
    assert.strictEqual(Object.prototype.hasOwnProperty.call(saveBody, 'polaris'), false);
    assert.ok(settingsRequests.every(url => url === '/api/asap/staff/settings?orgId=2'));

    dom.window.close();
    console.log('Non-super-admin settings scope survives initial option population and save');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
