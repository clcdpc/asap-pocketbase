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
    statusText: status >= 200 && status < 300 ? 'OK' : 'Request failed',
    json: async () => body
  };
}

function deferred() {
  let resolve;
  const promise = new Promise(nextResolve => { resolve = nextResolve; });
  return { promise, resolve };
}

function settingsData() {
  const emptySet = { exists: false, values: [] };
  return {
    orgId: 'system',
    version: 'system-version',
    organization: null,
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
      libraryOverride: null,
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
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-load-status-'));
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
    window.confirm = () => true;

    const announcements = [];
    const appStatus = document.getElementById('app-status');
    const announce = (message, kind = '') => {
      announcements.push({ message, kind });
      appStatus.textContent = message || '';
      appStatus.className = `status-message${kind ? ` ${kind}` : ''}`;
    };
    const settingsGate = deferred();
    const organizationsGate = deferred();
    const patronCodesGate = deferred();
    let initialLoad = true;
    let failSettings = false;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/staff/session')) {
        return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
      }
      if (requestUrl.includes('/api/asap/staff/settings?orgId=')) {
        if (initialLoad) return settingsGate.promise;
        return failSettings
          ? response(500, { message: 'Settings service unavailable.' })
          : response(200, settingsData());
      }
      if (requestUrl.endsWith('/api/asap/staff/organizations')) {
        if (initialLoad) return organizationsGate.promise;
        return response(200, [
          { id: 1, name: 'System', active: true, version: 'system-org' },
          { id: 2, name: 'Library Two', active: true, version: 'library-org' }
        ]);
      }
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) {
        if (initialLoad) return patronCodesGate.promise;
        return response(200, { code: 'ok', data: [] });
      }
      if (requestUrl === '/api/asap/staff/polaris/test' && options.method === 'POST') {
        return response(200, { data: { connected: true, organizationCount: 2 } });
      }
      if (requestUrl === '/api/asap/staff/users') {
        return response(200, { data: { canAssignSuperAdmin: true, users: [] } });
      }
      if (requestUrl === '/api/asap/staff/audit?limit=50') {
        return response(200, { data: [] });
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    const controller = settingsModule.createSettingsController({
      root: document.getElementById('settings-view'),
      tab: document.getElementById('settings-view-tab'),
      announce,
      getStaff: () => ({ role: 'super_admin', organizationId: 1 })
    });
    controller.bind();
    controller.setStaff({ id: '1', role: 'super_admin', organizationId: 1 });

    const initialLoadPromise = controller.activate();
    await flush();
    assert.strictEqual(document.getElementById('settings-message').textContent, 'Loading settings...');
    assert.strictEqual(appStatus.textContent, 'Loading settings...');

    settingsGate.resolve(response(200, settingsData()));
    organizationsGate.resolve(response(200, [
      { id: 1, name: 'System', active: true, version: 'system-org' },
      { id: 2, name: 'Library Two', active: true, version: 'library-org' }
    ]));
    patronCodesGate.resolve(response(200, { code: 'ok', data: [] }));
    await initialLoadPromise;
    initialLoad = false;
    await flush();

    assert.strictEqual(document.getElementById('settings-message').textContent, '',
      'Successful settings loads must clear the local status');
    assert.strictEqual(appStatus.textContent, '',
      'Successful settings loads must clear the global status');
    assert.strictEqual(announcements.some(item => /loaded/i.test(item.message)), false,
      'Successful settings loads must not announce passive loaded text');
    assert.match(fs.readFileSync(path.join(frontendRoot, 'staff', 'styles.css'), 'utf8'),
      /\.status-message:empty, \.settings-message:empty, \.settings-result:empty \{ min-height: 0; margin: 0; \}/,
      'empty live regions must collapse instead of reserving layout space');

    failSettings = true;
    await controller.load();
    assert.strictEqual(document.getElementById('settings-message').textContent, 'Settings service unavailable.');
    assert.strictEqual(appStatus.textContent, 'Settings service unavailable.');
    assert.strictEqual(document.getElementById('settings-message').className, 'settings-message error');
    assert.strictEqual(appStatus.className, 'status-message error');

    failSettings = false;
    document.getElementById('btn-test-polaris').click();
    await flush();
    assert.strictEqual(document.getElementById('settings-message').textContent, 'Polaris connection succeeded.');
    assert.strictEqual(appStatus.textContent, 'Polaris connection succeeded.');
    assert.strictEqual(document.getElementById('settings-message').className, 'settings-message success');
    assert.strictEqual(appStatus.className, 'status-message success');

    document.getElementById('settings-nav-staff').click();
    await flush();
    assert.strictEqual(document.getElementById('staff-access-status').textContent, '',
      'Staff roster loads must not announce passive loaded text');
    assert.strictEqual(document.getElementById('staff-access-status').className, 'settings-result');

    dom.window.close();
    console.log('Settings loading, error, action feedback, and empty-status layout checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
