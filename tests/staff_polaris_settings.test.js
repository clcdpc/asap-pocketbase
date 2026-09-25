const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');
const { projectLegacySettingsFixture } = require('./helpers/legacy-settings-fixture.cjs');

(async () => {
  const root = path.join(__dirname, '..');
  const frontend = path.join(root, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-staff-polaris-settings-'));
  fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Event = dom.window.Event;
    global.CustomEvent = dom.window.CustomEvent;
    global.Option = dom.window.Option;
    global.requestAnimationFrame = callback => callback();

    const moduleAt = name => import(pathToFileURL(path.join(temporary, 'staff', 'js', name)).href);
    const [state, form, serializer, polaris, sequencing, save, refresh, http] = await Promise.all([
      moduleAt('state.js'), moduleAt('settings/form-population.js'), moduleAt('settings/serialize-save.js'),
      moduleAt('settings/polaris-fields.js'), moduleAt('settings/polaris-test.js'),
      moduleAt('settings/save-controller.js'), moduleAt('settings/refresh.js'), moduleAt('http.js')
    ]);
    state.setStaffSession({
      authenticated: true, accessAllowed: true, antiforgeryToken: 'test-token',
      staff: { role: 'super_admin', userPrincipalName: 'admin@example.org' }
    });
    state.setCurrentLibraryContextOrgId('system');

    const raw = JSON.parse(fs.readFileSync(path.join(
      __dirname, 'fixtures', 'settings', 'canonical-system-settings-response.json'), 'utf8'));
    raw.stored.systemSettings.staffUrl = 'https://staff.example.org/';
    raw.stored.systemSettings.leapBibUrlPattern = 'https://catalog.example.org/bib/{{bibid}}';
    raw.stored.systemSettings.leapPatronUrlPattern = 'https://catalog.example.org/patron/{{patron-id}}';
    raw.stored.systemSettings.formatIconUrlPattern = 'https://cdn.example.org/{format}.svg';
    raw.stored.systemSettings.patronEmbedAllowedOrigins = ['https://library.example.org'];
    raw.stored.polaris = {
      host: 'https://polaris.example.org', accessId: 'access-42', staffDomain: 'LIBRARY',
      adminUser: 'asap-service', workstationId: 73, systemPolarisUserId: 4201,
      organizationIdForRequests: 731, pickupOrganizationId: 910,
      hasApiKey: true, hasAdminPassword: true, version: 'rowversion-polaris'
    };
    raw.stored.workflow.suggestionLimit = 11;
    raw.effective.workflow.suggestionLimit = 11;
    raw.stored.workflow.outstandingTimeoutDays = 61;
    raw.effective.workflow.outstandingTimeoutDays = 61;
    raw.stored.providers = [
      { id: '201', key: 'external_search_1', isEnabled: true, label: 'Discovery', urlTemplate: 'https://discovery.example.org/{{title}}', sortOrder: 13 },
      { id: '202', key: 'external_search_2', isEnabled: false, label: 'Research Index', urlTemplate: 'https://research.example.org/{{isbn}}', sortOrder: 29 },
      { id: '203', key: 'external_search_3', isEnabled: true, label: 'Regional Catalog', urlTemplate: 'https://regional.example.org/{{author}}', sortOrder: 44 }
    ];
    let server = projectLegacySettingsFixture(raw);
    const organizations = [
      { id: '1', displayName: 'System', active: true },
      { id: '2', displayName: 'Central', active: true },
      { id: '3', displayName: 'North', active: false }
    ];
    const posts = [];
    const ordering = [];
    let staleNextSave = false;
    global.fetch = async (request, options = {}) => {
      const url = String(request);
      const method = String(options.method || 'GET').toUpperCase();
      if (url === '/api/asap/staff/legacy/settings' && method === 'POST') {
        const body = JSON.parse(options.body);
        posts.push(body);
        ordering.push('save');
        if (staleNextSave) {
          staleNextSave = false;
          server.version = 'newer-system-version';
          server.workflow.suggestionLimit = 19;
          return { ok: false, status: 409, statusText: 'Conflict', json: async () => ({
            code: 'stale_version', message: 'Settings changed in another session.', operationPhase: 'rejected'
          }) };
        }
        assert.strictEqual(body.orgId, 'system');
        assert.strictEqual(body.version, server.version);
        if (body.polaris) Object.assign(server.polaris, body.polaris);
        if (body.workflow) Object.assign(server.workflow, body.workflow);
        if (body.providers) server.providers = structuredClone(body.providers);
        server.version = `saved-${posts.length}`;
        return { ok: true, status: 200, statusText: 'OK', json: async () => ({ code: 'saved' }) };
      }
      if (url === '/api/asap/staff/polaris/test' && method === 'POST') {
        ordering.push('test');
        return { ok: true, status: 200, statusText: 'OK', json: async () => ({ code: 'polaris_connected' }) };
      }
      if (url.startsWith('/api/asap/staff/legacy/settings') && method === 'GET') {
        return { ok: true, status: 200, statusText: 'OK', json: async () => structuredClone(server) };
      }
      if (url.startsWith('/api/asap/staff/legacy/patron-codes')) {
        return { ok: true, status: 200, statusText: 'OK', json: async () => [
          { id: '31', description: 'Adult' }, { id: '47', description: 'Young adult' }
        ] };
      }
      if (url.startsWith('/api/asap/staff/legacy/organizations')) {
        return { ok: true, status: 200, statusText: 'OK', json: async () => organizations };
      }
      if (url.startsWith('/api/asap/staff/users')) {
        return { ok: true, status: 200, statusText: 'OK', json: async () => ({ users: [] }) };
      }
      return { ok: true, status: 200, statusText: 'OK', json: async () => ({ enabled: false }) };
    };
    async function renderCurrent() {
      await form.applyLibrarySettingsToForm(structuredClone(server));
      serializer.rememberLastSavedLibrarySettings(server);
      serializer.captureSettingsBaseline();
      return server;
    }
    await renderCurrent();
    refresh.registerSettingsRefreshHandlers({ refreshSettingsView: renderCurrent, loadStaffConfig: async () => {} });

    assert.strictEqual(document.getElementById('suggestion-limit').value, '11');
    assert.strictEqual(document.getElementById('outstanding-timeout-days').value, '61');
    assert.strictEqual(document.getElementById('polaris-requesting-org-id').value, '731');
    assert.strictEqual(document.getElementById('polaris-pickup-org-id').value, '910');
    assert.strictEqual(document.getElementById('polaris-api-key').value, '');
    assert.strictEqual(document.getElementById('polaris-admin-pass').value, '');
    assert.strictEqual(document.getElementById('wf-external-search-2-label').value, 'Research Index');
    assert.strictEqual(document.getElementById('wf-external-search-4-label').disabled, false);
    assert.deepStrictEqual([...document.querySelectorAll('.lib-participation-cb:checked')].map(node => node.value), ['2']);
    assert.strictEqual(Object.hasOwn(serializer.buildSettingsPayload().workflow || {}, 'suggestionLimit'), false);
    assert.strictEqual(Object.hasOwn(serializer.buildSettingsPayload().polaris || {}, 'apiKey'), false);

    document.getElementById('polaris-requesting-org-id').value = '732';
    assert.deepStrictEqual(serializer.buildSettingsPayload().polaris, { organizationIdForRequests: 732 });
    const saveAndTest = await sequencing.saveThenTestPolaris(
      () => save.saveSettings({ clearDelay: 0 }),
      () => http.authorizedJson('/api/asap/staff/polaris/test', { method: 'POST' })
    );
    assert.deepStrictEqual(ordering, ['save', 'test']);
    assert.strictEqual(saveAndTest.saved, true);
    assert.strictEqual(saveAndTest.tested, true);
    assert.strictEqual(posts[0].polaris.organizationIdForRequests, 732);
    assert.strictEqual(Object.hasOwn(posts[0], 'workflow'), false);
    assert.strictEqual(document.getElementById('polaris-requesting-org-id').value, '732');

    document.getElementById('wf-external-search-2-label').value = 'Edited Research Index';
    const providerEdit = serializer.buildSettingsPayload();
    assert.strictEqual(providerEdit.providers.find(row => row.key === 'external_search_2').label, 'Edited Research Index');
    assert.strictEqual(providerEdit.providers.some(row => row.key === 'external_search_4'), true,
      'the form sends its four visible provider slots for the server to compare');
    assert.strictEqual(Object.hasOwn(providerEdit, 'workflow'), false);
    await renderCurrent();

    polaris.bindPolarisSecretControls();
    const apiKey = document.getElementById('polaris-api-key');
    const clearApiKey = document.getElementById('polaris-clear-api-key');
    const adminPassword = document.getElementById('polaris-admin-pass');
    apiKey.value = 'replacement';
    clearApiKey.checked = true;
    clearApiKey.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.strictEqual(apiKey.value, '');
    adminPassword.value = 'new-password';
    const secrets = polaris.collectSettingsPolaris(true);
    assert.strictEqual(secrets.clearApiKey, true);
    assert.strictEqual(secrets.adminPassword, 'new-password');
    assert.strictEqual(Object.hasOwn(secrets, 'apiKey'), false);
    await renderCurrent();

    document.getElementById('suggestion-limit').value = '12';
    staleNextSave = true;
    const originalConsoleError = console.error;
    console.error = (...args) => {
      if (args[0]?.response?.code !== 'stale_version') originalConsoleError(...args);
    };
    try {
      assert.strictEqual(await save.saveSettings({ clearDelay: 0 }), false);
    } finally {
      console.error = originalConsoleError;
    }
    assert.strictEqual(posts.length, 2, 'stale save must not retry the mutation');
    assert.strictEqual(document.getElementById('suggestion-limit').value, '19',
      'stale recovery must render the authoritative readback');
    assert.strictEqual(state.lastSavedLibrarySettingsSnapshot.version, 'newer-system-version');

    console.log('Staff Polaris flat settings form, sparse save, secret, and stale-readback checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exit(1); });
