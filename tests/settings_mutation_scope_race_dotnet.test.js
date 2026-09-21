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

function settingsData(organizationId, version, prefix) {
  const emptySet = { exists: false, values: [] };
  const configured = {
    workflow: {},
    patron: {},
    email: { fromAddress: 'system@example.org', fromName: 'System' },
    publicationOptions: emptySet,
    commonCreators: emptySet,
    allowedPatronCodeIds: emptySet,
    providers: [],
    formats: [],
    templates: [],
    branding: { hasLogo: false, altText: null }
  };
  return {
    orgId: organizationId === 1 ? 'system' : String(organizationId),
    version,
    organization: { id: organizationId, name: prefix + ' Library', abbreviation: prefix, active: true, version },
    stored: {
      systemSettings: {},
      polaris: {},
      configuredSystem: configured,
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
      logoAltText: prefix + ' logo'
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
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-race-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const settingsModule = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings.js')).href);

    for (const outcome of ['success', 'conflict']) {
      const html = fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8');
      const dom = new JSDOM(html, { url: 'http://localhost/staff/' });
      global.window = dom.window;
      global.document = dom.window.document;
      global.FormData = dom.window.FormData;
      global.Node = dom.window.Node;
      global.Event = dom.window.Event;
      window.confirm = () => true;

      const announcements = [];
      let saveStarted;
      let releaseSave;
      let saveRequestCount = 0;
      const settingsRequests = [];
      global.fetch = async (url) => {
        const requestUrl = String(url);
        if (requestUrl.endsWith('/api/asap/staff/session')) {
          return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
        }
        if (requestUrl.includes('/api/asap/staff/settings?orgId=')) {
          const organizationId = decodeURIComponent(requestUrl.split('orgId=')[1]);
          settingsRequests.push(organizationId);
          const prefix = organizationId === '2' ? 'Two' : organizationId === '3' ? 'Three' : 'System';
          return response(200, settingsData(organizationId === 'system' ? 1 : Number(organizationId), `${organizationId}-version`, prefix));
        }
        if (requestUrl.endsWith('/api/asap/staff/organizations')) {
          return response(200, [
            { id: 2, name: 'Library Two', abbreviation: 'TWO', active: true, version: 'org-2' },
            { id: 3, name: 'Library Three', abbreviation: 'THREE', active: true, version: 'org-3' }
          ]);
        }
        if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
        if (requestUrl.endsWith('/api/asap/staff/settings')) {
          saveRequestCount++;
          saveStarted?.();
          return new Promise(resolve => { releaseSave = () => resolve(
            outcome === 'success'
              ? response(200, { code: 'saved', data: { version: 'library-2-saved' } })
              : response(409, { code: 'stale_version', message: 'The library settings are stale.' })
          ); });
        }
        throw new Error('Unexpected request: ' + requestUrl);
      };

      const controller = settingsModule.createSettingsController({
        root: document.getElementById('settings-view'),
        tab: document.getElementById('settings-view-tab'),
        announce: (message, kind) => announcements.push({ message, kind }),
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

      const libraryTwoDraft = document.getElementById('patron-login-note');
      libraryTwoDraft.value = 'Draft for library two';
      libraryTwoDraft.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      const saveFinished = new Promise(resolve => { saveStarted = resolve; });
      document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
      await saveFinished;
      assert.strictEqual(saveRequestCount, 1);

      const announcementsBeforeSwitch = announcements.length;
      scope.value = '3';
      scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      await flush();
      assert.strictEqual(scope.value, '3');
      const libraryThreeDraft = document.getElementById('patron-login-note');
      libraryThreeDraft.value = 'Draft for library three';
      libraryThreeDraft.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      const requestsBeforeRelease = settingsRequests.length;

      releaseSave();
      await flush();
      await flush();

      assert.strictEqual(scope.value, '3', `${outcome} completion must not switch back to library two`);
      assert.strictEqual(
        document.getElementById('patron-login-note').value,
        'Draft for library three',
        `${outcome} completion must not overwrite the newer library-three draft`
      );
      assert.strictEqual(
        settingsRequests.length,
        requestsBeforeRelease,
        `${outcome} completion must not reload the newer settings scope`
      );
      assert.deepStrictEqual(
        announcements.slice(announcementsBeforeSwitch).filter(item =>
          /saved|stale|library two/i.test(item.message)),
        [],
        `${outcome} completion must not announce the old library-two result in library three`
      );

      scope.value = '2';
      scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      await flush();
      assert.strictEqual(document.getElementById('settings-scope-callout').hidden, false);
      assert.strictEqual(document.getElementById('settings-switch-to-system').hidden, false);
      document.getElementById('settings-switch-to-system').click();
      await flush();
      assert.strictEqual(scope.value, 'system');
      assert.strictEqual(document.getElementById('settings-scope-callout').hidden, true);
      assert.strictEqual(settingsRequests.at(-1), 'system');

      dom.window.close();
    }

    console.log('Settings mutation scope race regression checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
