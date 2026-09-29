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

function settingsData(note, version, formats = []) {
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
    formats,
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
    formats,
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
      formats,
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
      formats,
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
    let savedNote = 'Saved library note';
    let refreshFailureStatus = 0;
    let saveCount = 0;
    let committedCount = 0;
    const committedMessages = [];
    let settingsRequests = 0;
    let formatRows = [];
    let formatDeleteCount = 0;
    global.fetch = async (url, options = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/staff/session')) {
        return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
      }
      if (requestUrl.includes('/api/asap/staff/settings?orgId=2')) {
        settingsRequests += 1;
        if (refreshFailureStatus) return response(refreshFailureStatus,
          { code: 'settings_unavailable', message: 'Refresh unavailable' });
        return response(200, saved
          ? settingsData(savedNote, saveCount === 1 ? 'after-save' : `after-save-${saveCount}`, formatRows)
          : settingsData('System login note', 'before-save'));
      }
      if (requestUrl.endsWith('/api/asap/staff/organizations')) return response(200, []);
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) {
        return response(200, { code: 'ok', data: [] });
      }
      if (requestUrl.endsWith('/api/asap/staff/settings')) {
        const submittedNote = JSON.parse(options.body).patron.loginNote;
        if (saveCount < 3) {
          assert.strictEqual(submittedNote,
            ['Saved library note', 'Saved again', 'Saved after 401'][saveCount]);
        }
        saveCount += 1;
        saved = true;
        if (submittedNote) savedNote = submittedNote;
        return response(200, { code: 'saved', data: { version: saveCount === 1 ? 'after-save' : `after-save-${saveCount}` } });
      }
      if (requestUrl.includes('/api/asap/staff/settings/formats/')) {
        assert.strictEqual(options.method, 'DELETE');
        formatDeleteCount += 1;
        return formatDeleteCount === 1
          ? response(200, { code: 'deleted' })
          : response(401, { code: 'unauthorized' });
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    const controller = settingsModule.createSettingsController({
      root: document.getElementById('settings-view'),
      tab: document.getElementById('settings-view-tab'),
      announce: () => {},
      getStaff: () => ({ role: 'admin', organizationId: 2 }),
      onCommitted: message => {
        committedCount += 1;
        committedMessages.push(message);
      }
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

    note.value = 'Saved again';
    note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    refreshFailureStatus = 503;
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true, cancelable: true
    }));
    for (let attempt = 0; attempt < 20 && saveCount < 2; attempt++) await flush();
    for (let attempt = 0; attempt < 20; attempt++) await flush();
    assert.strictEqual(committedCount, 2);
    assert.strictEqual(document.getElementById('settings-save-title').textContent, 'Saved; reload needed');
    assert.strictEqual(document.getElementById('settings-save').disabled, true);
    assert.match(document.getElementById('settings-message').textContent, /Settings saved, but/);

    refreshFailureStatus = 0;
    document.getElementById('settings-refresh').click();
    for (let attempt = 0; attempt < 20 &&
      document.getElementById('settings-save-title').textContent !== 'No changes'; attempt++) await flush();
    assert.strictEqual(document.getElementById('settings-version').value, 'after-save-2');
    note.value = 'Saved after 401';
    note.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    refreshFailureStatus = 401;
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true, cancelable: true
    }));
    for (let attempt = 0; attempt < 20 && saveCount < 3; attempt++) await flush();
    for (let attempt = 0; attempt < 20; attempt++) await flush();
    assert.strictEqual(committedCount, 3);
    assert.strictEqual(document.getElementById('settings-save-title').textContent, 'Saved; reload needed');
    assert.strictEqual(document.getElementById('settings-save').disabled, true);
    assert.match(document.getElementById('settings-message').textContent, /Settings saved, but/);

    formatRows = [
      { id: '101', version: 'format-v1', code: 'local_one', label: 'Local one', ownerOrganizationId: 2, isEnabled: true },
      { id: '102', version: 'format-v2', code: 'local_two', label: 'Local two', ownerOrganizationId: 2, isEnabled: true }
    ];
    refreshFailureStatus = 0;
    document.getElementById('settings-refresh').click();
    for (let attempt = 0; attempt < 20 &&
      document.querySelectorAll('.settings-format-row button[aria-label="Delete"]').length < 2; attempt++) await flush();
    assert.strictEqual(document.querySelectorAll('.settings-format-row button[aria-label="Delete"]').length, 2);
    document.querySelector('.settings-format-row button[aria-label="Delete"]').click();
    document.querySelector('.settings-format-row button[aria-label="Delete"]').click();
    assert.strictEqual(document.getElementById('settings-save').disabled, false);
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', {
      bubbles: true,
      cancelable: true
    }));
    for (let attempt = 0; attempt < 20 && formatDeleteCount < 2; attempt++) await flush();
    for (let attempt = 0; attempt < 20; attempt++) await flush();
    assert.strictEqual(formatDeleteCount, 2);
    assert.match(committedMessages.at(-2), /Format deletions confirmed: 0 of 2/);
    assert.match(committedMessages.at(-1), /Format deletions confirmed: 1 of 2/);
    assert.match(document.getElementById('settings-message').textContent,
      /Settings saved\. Format deletions confirmed: 1 of 2\. A follow-up action failed/);
    assert.strictEqual(document.getElementById('settings-save-title').textContent, 'Saved; reload needed');
    dom.window.close();
    console.log('Settings save completion refreshes the baseline and leaves the form clean');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
