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
    orgId: String(organizationId),
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
    const httpModule = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'http.js')).href);

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
      let holdNextSettingsGet = false;
      let signalOldSettingsGet;
      let releaseOldSettingsGet;
      let oldSettingsGetStarted;
      const oldSettingsGetSeen = new Promise(resolve => { oldSettingsGetStarted = resolve; });
      let sessionInvalidations = 0;
      httpModule.onSessionInvalid(() => { sessionInvalidations += 1; });
      global.fetch = async (url, options) => {
        const requestUrl = String(url);
        if (requestUrl.endsWith('/api/asap/staff/session')) {
          return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
        }
        if (requestUrl.includes('/api/asap/staff/settings?orgId=')) {
          const organizationId = decodeURIComponent(requestUrl.split('orgId=')[1]);
          settingsRequests.push(organizationId);
          if (holdNextSettingsGet) {
            holdNextSettingsGet = false;
            signalOldSettingsGet = options.signal;
            oldSettingsGetStarted();
            return new Promise(resolve => {
              releaseOldSettingsGet = () => resolve(response(401, { code: 'staff_session_invalid' }));
            });
          }
          const prefix = organizationId === '2' ? 'Two' : organizationId === '3' ? 'Three' : 'System';
          return response(200, settingsData(organizationId === 'system' ? 1 : Number(organizationId), `${organizationId}-version`, prefix));
        }
        if (requestUrl.endsWith('/api/asap/staff/organizations')) {
          return response(200, [
            { id: 2, name: 'Library Two', abbreviation: 'TWO', isActive: true, version: 'org-2' },
            { id: 3, name: 'Library Three', abbreviation: 'THREE', isActive: true, version: 'org-3' }
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
        prepareDeparture: () => ({ commit: () => true }),
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
      holdNextSettingsGet = true;
      const obsoleteLoad = controller.load({ silent: true });
      await oldSettingsGetSeen;
      const saveFinished = new Promise(resolve => { saveStarted = resolve; });
      document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
      await saveFinished;
      assert.strictEqual(saveRequestCount, 1);
      assert.strictEqual(signalOldSettingsGet.aborted, true);
      releaseOldSettingsGet();
      await obsoleteLoad;
      assert.strictEqual(sessionInvalidations, 0,
        'an aborted stale Settings GET must not invalidate the current session');

      const announcementsBeforeBlockedSwitch = announcements.length;
      scope.value = '3';
      scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      await flush();
      assert.strictEqual(scope.value, '2', 'scope change must wait for the authoritative save result');
      assert.strictEqual(document.getElementById('settings-form').inert, true);
      assert.ok(announcements.slice(announcementsBeforeBlockedSwitch).some(item =>
        /wait for the settings change/i.test(item.message)));
      const requestsBeforeRelease = settingsRequests.length;

      releaseSave();
      for (let attempt = 0; attempt < 12 && document.getElementById('settings-form').inert; attempt++) {
        await flush();
      }
      assert.strictEqual(document.getElementById('settings-form').inert, false);
      assert.strictEqual(scope.value, '2');
      assert.ok(settingsRequests.length > requestsBeforeRelease,
        'the owning library must refresh after the save or conflict result');

      const announcementsBeforeSwitch = announcements.length;
      scope.value = '3';
      scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      await flush();
      assert.strictEqual(scope.value, '3');
      const libraryThreeDraft = document.getElementById('patron-login-note');
      libraryThreeDraft.value = 'Draft for library three';
      libraryThreeDraft.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      const requestsAfterSwitch = settingsRequests.length;
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
        requestsAfterSwitch,
        `${outcome} completion must not reload the newer settings scope`
      );
      assert.deepStrictEqual(
        announcements.slice(announcementsBeforeSwitch).filter(item =>
          /saved|stale|library two/i.test(item.message)),
        [],
        `${outcome} completion must not announce the old library-two result in library three`
      );

      dom.window.close();
    }

    for (const mutation of ['save', 'reset', 'logo', 'clear', 'logo-abort']) {
      const html = fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8');
      const dom = new JSDOM(html, { url: 'http://localhost/staff/' });
      global.window = dom.window;
      global.document = dom.window.document;
      global.FormData = dom.window.FormData;
      global.Node = dom.window.Node;
      global.Event = dom.window.Event;
      window.confirm = () => true;
      let mutationRequests = 0;
      let failReload = false;
      global.fetch = async url => {
        const requestUrl = String(url);
        if (requestUrl.endsWith('/api/asap/staff/session')) {
          return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
        }
        if (requestUrl.includes('/api/asap/staff/settings?orgId=')) {
          const organizationId = decodeURIComponent(requestUrl.split('orgId=')[1]);
          const data = settingsData(organizationId === 'system' ? 1 : 2,
            `${organizationId}-version`, organizationId === 'system' ? 'System' : 'Two');
          if (organizationId === 'system') data.orgId = 'system';
          return failReload ? response(503, { message: 'Unavailable' })
            : response(200, data);
        }
        if (requestUrl.endsWith('/api/asap/staff/organizations')) {
          return response(200, [{ id: 2, name: 'Library Two', isActive: true, version: 'org-2' }]);
        }
        if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) {
          return response(200, { code: 'ok', data: [] });
        }
        if (requestUrl.endsWith('/api/asap/staff/settings') ||
            requestUrl.includes('/api/asap/staff/settings/reset?') ||
            requestUrl.includes('/api/asap/staff/settings/logo?')) {
          mutationRequests += 1;
          const failure = new Error('Connection closed after request was sent');
          if (mutation === 'logo-abort') failure.name = 'AbortError';
          throw failure;
        }
        throw new Error('Unexpected request: ' + requestUrl);
      };
      const controller = settingsModule.createSettingsController({
        prepareDeparture: () => ({ commit: () => true }),
        root: document.getElementById('settings-view'),
        tab: document.getElementById('settings-view-tab'),
        announce: () => {},
        getStaff: () => ({ role: 'super_admin' })
      });
      controller.bind();
      controller.setStaff({ id: '1', tenantId: 'tenant-1', objectId: 'object-1',
        role: 'super_admin', organizationId: 1 });
      await controller.activate();
      const scope = document.getElementById('settings-scope');
      scope.value = '2';
      scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      await flush();
      if (mutation === 'save') {
        const draft = document.getElementById('patron-login-note');
        draft.value = 'Unsaved library draft';
        draft.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
        document.getElementById('settings-form').dispatchEvent(
          new dom.window.Event('submit', { bubbles: true, cancelable: true }));
      } else if (mutation === 'reset') {
        document.getElementById('settings-reset').click();
      } else {
        if (mutation.startsWith('logo')) {
          const file = new dom.window.File(['gif'], 'draft.gif', { type: 'image/gif' });
          Object.defineProperty(document.getElementById('branding-logo'), 'files',
            { configurable: true, value: [file] });
        }
        document.getElementById(mutation.startsWith('logo') ? 'save-branding-logo' : 'clear-branding-logo').click();
      }
      for (let attempt = 0; attempt < 10 && document.getElementById('settings-form').inert; attempt++) {
        await flush();
      }
      assert.strictEqual(mutationRequests, 1, `${mutation} should submit once`);
      assert.strictEqual(controller.hasUnconfirmedOutcome(), true, `${mutation} must retain uncertainty`);
      assert.match(document.getElementById('settings-message').textContent, /outcome is uncertain/i);
      assert.strictEqual(document.getElementById('settings-save-title').textContent,
        'Outcome uncertain; reload needed');
      const saveBar = document.querySelector('.settings-save-bar');
      assert.strictEqual(saveBar.classList.contains('attention'), true,
        `${mutation} uncertainty must keep the save bar in attention state`);
      if (mutation === 'reset' || mutation === 'clear') {
        assert.strictEqual(controller.isDirty(), false,
          `${mutation} must require attention even without a settings draft`);
        assert.strictEqual(document.getElementById('settings-discard').hidden, true);
      }
      assert.strictEqual(document.getElementById('settings-save').disabled, true);
      assert.strictEqual(document.getElementById('settings-reset').disabled, true);
      assert.strictEqual(document.getElementById('save-branding-logo').disabled, true);
      if (mutation === 'save') {
        assert.strictEqual(document.getElementById('patron-login-note').value, 'Unsaved library draft');
      }
      if (mutation.startsWith('logo')) {
        assert.strictEqual(document.getElementById('branding-logo').files.length, 1);
      }
      document.getElementById('settings-form').dispatchEvent(
        new dom.window.Event('submit', { bubbles: true, cancelable: true }));
      document.getElementById('settings-reset').click();
      document.getElementById('save-branding-logo').click();
      document.getElementById('clear-branding-logo').click();
      await flush();
      assert.strictEqual(mutationRequests, 1, `${mutation} must not retry before verifying current values`);
      if (mutation === 'reset') {
        scope.value = 'system';
        scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
        await flush();
        assert.strictEqual(scope.value, '2',
          'an uncertain reset must keep its library context until authoritative reload');
        assert.match(document.getElementById('settings-message').textContent,
          /reload current settings to verify the uncertain change/i);
      }
      failReload = true;
      document.getElementById('settings-refresh').click();
      await flush();
      assert.strictEqual(controller.hasUnconfirmedOutcome(), true,
        `${mutation} uncertainty must survive a failed authoritative reload`);
      assert.match(document.getElementById('settings-message').textContent, /still uncertain/i);
      assert.strictEqual(saveBar.classList.contains('attention'), true);
      failReload = false;
      document.getElementById('settings-refresh').click();
      await flush();
      assert.strictEqual(controller.hasUnconfirmedOutcome(), false,
        `${mutation} uncertainty clears only after authoritative reload`);
      assert.strictEqual(controller.isDirty(), false);
      assert.strictEqual(saveBar.classList.contains('attention'), false,
        `${mutation} attention clears after confirmed reload`);
      assert.strictEqual(document.getElementById('settings-save').disabled, true);
      assert.strictEqual(document.getElementById('settings-discard').hidden, true);
      assert.strictEqual(document.getElementById('settings-reset').disabled, false);
      if (mutation === 'reset') {
        scope.value = 'system';
        scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
        await flush();
        assert.strictEqual(scope.value, 'system',
          'scope switching resumes after authoritative reload resolves the reset');
      }
      dom.window.close();
    }

    {
      const html = fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8');
      const dom = new JSDOM(html, { url: 'http://localhost/staff/' });
      global.window = dom.window;
      global.document = dom.window.document;
      global.FormData = dom.window.FormData;
      global.Node = dom.window.Node;
      global.Event = dom.window.Event;
      window.confirm = () => true;
      let releaseFailedScope;
      let allowThree = false;
      global.fetch = async url => {
        const requestUrl = String(url);
        if (requestUrl.endsWith('/api/asap/staff/session')) {
          return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
        }
        if (requestUrl.includes('/api/asap/staff/settings?orgId=')) {
          const organizationId = decodeURIComponent(requestUrl.split('orgId=')[1]);
          if (organizationId === '3' && !allowThree) {
            return new Promise(resolve => { releaseFailedScope = () => resolve(response(503, { message: 'Unavailable' })); });
          }
          const prefix = organizationId === '2' ? 'Two' : organizationId === '3' ? 'Three' : 'System';
          return response(200, settingsData(organizationId === 'system' ? 1 : Number(organizationId),
            `${organizationId}-version`, prefix));
        }
        if (requestUrl.endsWith('/api/asap/staff/organizations')) {
          return response(200, [
            { id: 2, name: 'Library Two', abbreviation: 'TWO', isActive: true, version: 'org-2' },
            { id: 3, name: 'Library Three', abbreviation: 'THREE', isActive: true, version: 'org-3' }
          ]);
        }
        if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
        throw new Error('Unexpected request: ' + requestUrl);
      };
      const controller = settingsModule.createSettingsController({
        prepareDeparture: () => ({ commit: () => true }),
        root: document.getElementById('settings-view'),
        tab: document.getElementById('settings-view-tab'),
        announce: () => {},
        getStaff: () => ({ role: 'super_admin' })
      });
      controller.bind();
      controller.setStaff({ id: '1', tenantId: 'tenant-1', objectId: 'object-1',
        role: 'super_admin', organizationId: 1 });
      await controller.activate();
      const scope = document.getElementById('settings-scope');
      const form = document.getElementById('settings-form');
      scope.value = '2';
      scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      await flush();
      assert.strictEqual(form.hidden, false);
      assert.strictEqual(document.getElementById('branding-alt').value, 'Two logo');
      scope.value = '3';
      scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      await flush();
      assert.strictEqual(form.hidden, false, 'the source stays mounted until the replacement is ready');
      assert.strictEqual(scope.value, '2', 'the source scope stays authoritative during target loading');
      assert.strictEqual(document.getElementById('branding-alt').value, 'Two logo');
      assert.strictEqual(controller.isDirty(), false, 'the unchanged source remains clean');
      releaseFailedScope();
      await flush();
      assert.strictEqual(form.hidden, false, 'failed scope load retains the source owner');
      assert.strictEqual(scope.value, '2');
      assert.strictEqual(document.getElementById('branding-alt').value, 'Two logo');
      assert.strictEqual(document.getElementById('settings-refresh').disabled, false);
      allowThree = true;
      scope.value = '3'; scope.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      await flush();
      assert.strictEqual(form.hidden, false);
      assert.strictEqual(document.getElementById('branding-alt').value, 'Three logo');
      dom.window.close();
    }

    for (const outcome of ['committed', 'uncertain']) {
      const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), { url: 'http://localhost/staff/' });
      Object.assign(global, { window: dom.window, document: dom.window.document, FormData: dom.window.FormData, Node: dom.window.Node });
      const receipts = [], posts = [];
      let complete, fail;
      global.fetch = async (url, options = {}) => {
        const requestUrl = String(url);
        if (requestUrl.endsWith('/session')) return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
        if (requestUrl.endsWith('/settings') && options.method === 'POST') {
          posts.push(options); return new Promise((resolve, reject) => { complete = resolve; fail = reject; });
        }
        if (requestUrl.includes('/settings?')) return response(200, settingsData(1, 'replacement-version', 'Replacement'));
        if (requestUrl.endsWith('/organizations')) return response(200, []);
        if (requestUrl.includes('/patron-codes?')) return response(200, { data: [] });
        throw new Error(`Unexpected request: ${requestUrl}`);
      };
      const options = { root: document.getElementById('settings-view'), tab: document.getElementById('settings-view-tab'), announce() {},
        onCommitted: (...args) => receipts.push(args), onUnconfirmed: (...args) => receipts.push(args) };
      const first = settingsModule.createSettingsController(options); first.bind();
      first.setStaff({ id: '1', tenantId: 'tenant-a', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1 });
      await first.activate();
      const input = document.getElementById('patron-login-note'); input.value = 'Submitted draft';
      input.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
      await flush(); assert.ok(complete); assert.equal(posts[0].signal, undefined, 'committing Settings request has no read cancellation signal');
      first.dispose();
      const replacement = settingsModule.createSettingsController(options); replacement.bind();
      replacement.setStaff({ id: '2', tenantId: 'tenant-b', authenticationEmail: 'b@example.org', role: 'super_admin', organizationId: 1 });
      await replacement.activate(); first.dispose();
      if (outcome === 'committed') complete(response(200, { data: { version: 'old-commit-version' } }));
      else fail(new Error('Lost response'));
      await flush(); await flush();
      assert.equal(receipts.at(-1)[1].id, '1'); assert.equal(receipts.at(-1)[2].outcome, outcome, 'record old attempt truth before presentation checks');
      assert.equal(document.getElementById('settings-version').value, 'replacement-version');
      assert.equal(document.getElementById('patron-login-note').value, '');
      assert.equal(document.getElementById('settings-form').inert, false, 'retired finally cannot change replacement interactivity');
      assert.equal(replacement.hasPendingMutation(), false); replacement.dispose(); dom.window.close();
    }
    for (const outcome of ['committed', 'uncertain']) {
      const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), { url: 'http://localhost/staff/' });
      Object.assign(global, { window: dom.window, document: dom.window.document, FormData: dom.window.FormData, Node: dom.window.Node });
      const owner = { id: '20', tenantId: 'same-tenant', authenticationEmail: 'staff@example.org',
        userPrincipalName: 'staff@example.org', role: 'super_admin', organizationId: 1, version: 'actor-v1' };
      let complete, fail;
      const receipts = [];
      global.fetch = async (url, options = {}) => {
        if (url.endsWith('/session')) return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
        if (url.endsWith('/settings') && options.method === 'POST') return new Promise((resolve, reject) => { complete = resolve; fail = reject; });
        if (url.includes('/settings?')) return response(200, settingsData(1, 'current-version', 'Current'));
        if (url.endsWith('/organizations')) return response(200, []);
        if (url.includes('/patron-codes?')) return response(200, { data: [] });
        throw new Error(`Unexpected request: ${url}`);
      };
      const controller = settingsModule.createSettingsController({ root: document.getElementById('settings-view'),
        tab: document.getElementById('settings-view-tab'), announce() {},
        onCommitted: (...args) => receipts.push(args), onUnconfirmed: (...args) => receipts.push(args) });
      controller.bind(); controller.setStaff(owner); await controller.activate();
      document.getElementById('patron-login-note').value = 'Old actor draft';
      document.getElementById('patron-login-note').dispatchEvent(new dom.window.Event('input', { bubbles: true }));
      document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
      await flush(); assert.ok(complete); assert.equal(controller.hasPendingMutation(), true);
      controller.setStaff({ ...owner, version: 'actor-v2', displayName: 'Revised', purchaseReminderDefault: true });
      assert.equal(controller.hasPendingMutation(), true, 'same actor preference changes preserve captured mutation');
      controller.setStaff({ ...owner, authenticationEmail: undefined });
      assert.equal(controller.hasPendingMutation(), false, 'principal-name fallback cannot adopt a command from different authentication evidence');
      await controller.activate();
      if (outcome === 'committed') complete(response(200, { data: { version: 'old-actor-version' } }));
      else fail(new Error('Old actor response lost'));
      await flush(); await flush();
      assert.equal(receipts.at(-1)[1], owner); assert.equal(receipts.at(-1)[2].outcome, outcome);
      assert.equal(controller.hasUnconfirmedOutcome(), false, 'late old failure cannot block replacement Settings');
      assert.equal(document.getElementById('settings-version').value, 'current-version');
      controller.dispose(); dom.window.close();
    }
    console.log('Settings scope races and two canonical authentication-evidence replacement cases passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
