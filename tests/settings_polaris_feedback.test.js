const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const staff = { id: '20', tenantId: 'polaris-feedback', authenticationEmail: 'admin@example.org',
  role: 'super_admin', organizationId: 1, version: 'actor-v1' };
const response = (status, body) => ({ ok: status < 400, status, statusText: 'Test response', json: async () => body });
const flush = () => new Promise(resolve => setImmediate(resolve));
async function until(predicate) {
  for (let count = 0; count < 100 && !predicate(); count++) await flush();
  assert.ok(predicate(), 'Expected Polaris settings state');
}

async function polarisFixture(journey, initialAccepted = {}) {
  await fixture(async ui => {
    const { createSettingsController } = await ui.load('settings');
    let accepted = { hasApiKey: false, hasAdminPassword: false, hasPostmarkToken: false, ...initialAccepted };
    let version = 1, loadFailure = false;
    let save = async body => {
      if (body.polaris?.apiKey) accepted.hasApiKey = true;
      if (body.polaris?.adminPassword) accepted.hasAdminPassword = true;
      if (body.email?.postmarkToken) accepted.hasPostmarkToken = true;
      if (body.email?.clearPostmarkToken) accepted.hasPostmarkToken = false;
      return response(200, { code: 'saved', data: { orgId: body.orgId, version: `settings-${++version}` } });
    };
    let diagnostic = async () => response(200, { code: 'polaris_connected', data: { connected: true, organizationCount: 2 } });
    let sync = async () => response(200, { code: 'synced', data: { received: 3, changed: 2 } });
    const commits = [], unconfirmed = [], catalogs = [], refreshed = [], notices = [], posts = [];
    let diagnosticCalls = 0, syncCalls = 0;
    global.fetch = async (path, init = {}) => {
      if (path.endsWith('/session')) return response(200, { authenticated: true, accessAllowed: true, staff, antiforgeryToken: 'test' });
      if (path === '/api/asap/staff/polaris/test') { diagnosticCalls++; return diagnostic(init); }
      if (path === '/api/asap/staff/organizations/sync') { syncCalls++; return sync(init); }
      if (path === '/api/asap/staff/settings') {
        const body = JSON.parse(init.body); posts.push(body); return save(body);
      }
      if (path.includes('/settings?')) {
        if (loadFailure) return response(503, { message: 'Settings reload unavailable.' });
        const scope = new URL(path, 'https://localhost').searchParams.get('orgId');
        const emptySet = { exists: false, values: [] };
        const configured = { polaris: { ...accepted }, workflow: {}, patron: { loginNote: 'Saved' },
          email: { hasPostmarkToken: accepted.hasPostmarkToken },
          publicationOptions: emptySet, commonCreators: emptySet, allowedPatronCodeIds: emptySet,
          providers: [], formats: [], templates: [], branding: { hasLogo: false, altText: null } };
        return response(200, { orgId: scope, version: `settings-${version}`,
          organization: scope === 'system' ? null : { id: 2, name: 'Library', active: true },
          stored: { configuredSystem: configured, libraryOverride: scope === 'system' ? null : { ...configured, patron: null },
            systemSettings: { enabledLibraryOrgIds: [2], libraryOrgIds: [2] }, polaris: { ...accepted },
            origins: [], formats: [], providers: [], formatRules: [], customFields: [], templates: [], autoClaimRules: [] },
          effective: { formats: [], customFields: [], externalSearchProviders: [], allowedPatronCodeIds: [],
            publicationOptions: [], commonCreators: [] }, ui_text: { loginNote: 'Saved' }, emails: {}, workflow: {}, autoClaimStaff: [] });
      }
      if (path.endsWith('/organizations')) return response(200, { code: 'ok', data: [
        { id: 1, name: 'System', abbreviation: null, organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org-1' },
        { id: 2, name: 'Library', abbreviation: 'LIB', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-2' }
      ] });
      if (path.includes('/patron-codes')) return response(200, { data: [] });
      throw new Error(`Unexpected Polaris feedback path: ${path}`);
    };
    const controller = createSettingsController({ root: ui.get('#settings-view'), tab: ui.get('#settings-view-tab'),
      announce: message => notices.push(message), onCommitted: (...args) => commits.push(args),
      onUnconfirmed: (...args) => unconfirmed.push(args), onOrganizationCatalogCommitted: owner => catalogs.push(owner),
      onRefreshed: (...args) => refreshed.push(args) });
    const change = (selector, value) => {
      ui.get(selector).value = value;
      ui.get(selector).dispatchEvent(new ui.dom.window.Event('input', { bubbles: true }));
    };
    const submit = async () => {
      ui.get('#settings-form').dispatchEvent(new ui.dom.window.Event('submit', { bubbles: true, cancelable: true }));
      assert.equal(controller.hasPendingMutation(), true, ui.get('#settings-message').textContent);
      await until(() => !controller.hasPendingMutation());
    };
    try {
      controller.bind(); controller.setStaff(staff); await controller.activate('polaris');
      await journey({ ...ui, controller, change, submit, posts, commits, unconfirmed, catalogs, notices, refreshed,
        accept: value => { accepted = { ...accepted, ...value }; }, setSave: fn => { save = fn; },
        setDiagnostic: fn => { diagnostic = fn; }, setSync: fn => { sync = fn; },
        failLoad: value => { loadFailure = value; }, diagnosticCalls: () => diagnosticCalls, syncCalls: () => syncCalls });
    } finally { controller.dispose(); }
  });
}

(async () => {
  await polarisFixture(async ({ get, dom, controller, change, submit, posts, setSave, accept, diagnosticCalls }) => {
    const key = get('#polaris-api-key'), password = get('#polaris-admin-pass');
    const postmark = get('#email-postmark-token');
    assert.equal(key.value, ''); assert.equal(password.value, '');
    assert.equal(key.placeholder, ''); assert.equal(password.placeholder, '');
    assert.equal(postmark.value, ''); assert.equal(postmark.placeholder, '');
    assert.equal(get('#email-token-status').textContent, 'No system Postmark token is stored.');
    assert.equal(get('#polaris-api-key-status').textContent, 'No API key saved.');
    assert.equal(get('#polaris-admin-pass-status').textContent, 'No system staff password saved.');
    assert.equal(key.getAttribute('aria-describedby'), 'polaris-api-key-status');
    assert.equal(password.getAttribute('aria-describedby'), 'polaris-admin-pass-status');
    key.focus(); key.blur(); password.focus(); password.blur();
    assert.equal(controller.isDirty(), false, 'Focusing and leaving replacement fields does not create a draft.');
    const clearPostmark = get('#email-clear-postmark-token');
    clearPostmark.checked = true;
    clearPostmark.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.equal(get('#email-token-status').textContent,
      'Postmark token clear is selected. No token is currently stored; saving keeps it unconfigured.');
    assert.equal(get('#email-postmark-token').placeholder, '');
    clearPostmark.checked = false;
    clearPostmark.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.equal(get('#email-token-status').textContent, 'No system Postmark token is stored.');
    assert.equal(controller.isDirty(), false, 'Canceling a clear for a missing token restores the clean state.');
    change('#email-postmark-token', 'unsaved-first-token');
    clearPostmark.checked = true;
    clearPostmark.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.equal(get('#email-postmark-token').value, '', 'Clear discards a first replacement draft before it is saved.');
    assert.equal(get('#email-token-status').textContent,
      'Postmark token clear is selected. No token is currently stored; saving keeps it unconfigured.');
    await submit();
    assert.equal(posts.at(-1).email.clearPostmarkToken, true);
    assert.equal(Object.hasOwn(posts.at(-1).email, 'postmarkToken'), false,
      'Clear keeps its explicit clear payload even when it replaces a typed first draft.');
    assert.equal(get('#email-postmark-token').placeholder, '');
    assert.equal(get('#email-token-status').textContent, 'No system Postmark token is stored.');
    change('#polaris-api-key', 'discarded-key'); change('#polaris-admin-pass', 'discarded-password');
    change('#polaris-api-key', ''); change('#polaris-admin-pass', '');
    assert.equal(controller.isDirty(), false, 'Deleting a replacement while credentials are missing restores the clean form.');
    assert.equal(key.placeholder, ''); assert.equal(password.placeholder, '');
    change('#polaris-api-key', 'replacement-key'); change('#polaris-admin-pass', 'replacement-password');
    assert.equal(get('#polaris-api-key-status').textContent, 'No API key saved.', 'typing does not imply persistence');
    setSave(async () => response(400, { message: 'Replacement rejected.' }));
    await submit();
    assert.equal(key.value, 'replacement-key'); assert.equal(password.value, 'replacement-password');
    assert.equal(get('#polaris-api-key-status').textContent, 'No API key saved.', 'rejection retains accepted missing state');
    setSave(async body => {
      assert.equal(body.polaris.apiKey, 'replacement-key'); assert.equal(body.polaris.adminPassword, 'replacement-password');
      accept({ hasApiKey: true, hasAdminPassword: true });
      return response(200, { code: 'saved', data: { orgId: 'system', version: 'settings-2' } });
    });
    await submit();
    assert.equal(key.value, ''); assert.equal(password.value, '');
    assert.equal(key.placeholder, '********'); assert.equal(password.placeholder, '********');
    assert.match(get('#polaris-api-key-status').textContent, /^API key saved\. Leave blank/);
    assert.match(get('#polaris-admin-pass-status').textContent, /^System staff password saved\. Leave blank/);
    assert.equal(diagnosticCalls(), 0, 'presence never requires a connection test');
    change('#polaris-access-id', 'blank-secret-save');
    setSave(async body => {
      assert.equal(Object.hasOwn(body.polaris, 'apiKey'), false);
      assert.equal(Object.hasOwn(body.polaris, 'adminPassword'), false);
      return response(200, { code: 'saved', data: { orgId: 'system', version: 'settings-3' } });
    });
    await submit();
    assert.match(get('#polaris-api-key-status').textContent, /^API key saved\./);
    assert.equal(key.value, ''); assert.equal(password.value, '');
    assert.equal(key.placeholder, '********'); assert.equal(password.placeholder, '********');
    change('#polaris-api-key', 'discarded-key'); change('#polaris-admin-pass', 'discarded-password');
    change('#polaris-api-key', ''); change('#polaris-admin-pass', '');
    assert.equal(controller.isDirty(), false, 'Deleting replacements restores the accepted saved baseline.');
    assert.equal(key.placeholder, '********'); assert.equal(password.placeholder, '********');
    const target = await controller.prepareScope('2'); target.accept();
    assert.equal(key.disabled, true); assert.equal(password.disabled, true);
    assert.equal(get('#btn-test-polaris').matches(':disabled'), true);
    // Even script-populated disabled replacements must not escape through a library settings save.
    key.value = 'ignored-library-key'; password.value = 'ignored-library-password';
    change('#patron-login-note', 'Library draft');
    setSave(async body => {
      assert.equal(Object.hasOwn(body, 'polaris'), false);
      return response(200, { code: 'saved', data: { orgId: '2', version: 'library-2' } });
    });
    await submit();
    assert.equal(Object.hasOwn(posts.at(-1), 'polaris'), false);
  });

  for (const outcome of ['committed', 'rejected', 'uncertain']) {
    await polarisFixture(async ({ get, controller, change, submit, setSave, accept, failLoad, commits, unconfirmed }) => {
      change('#polaris-api-key', 'replacement-key'); change('#polaris-admin-pass', 'replacement-password');
      setSave(async () => {
        if (outcome === 'uncertain') return response(200, { code: 'saved', data: { orgId: 'system' } });
        if (outcome === 'rejected') return response(400, { message: 'Replacement rejected.' });
        accept({ hasApiKey: true, hasAdminPassword: true }); failLoad(true);
        return response(200, { code: 'saved', data: { orgId: 'system', version: 'replacement-saved' } });
      });
      await submit();
      assert.equal(get('#polaris-api-key').value, 'replacement-key', `${outcome}: an unavailable reload retains the replacement input`);
      assert.equal(get('#polaris-admin-pass').value, 'replacement-password');
      if (outcome === 'committed') {
        assert.equal(get('#polaris-api-key-status').textContent, 'API key replacement saved. Reload settings to refresh the credential status.');
        assert.equal(get('#polaris-admin-pass-status').textContent, 'System staff password replacement saved. Reload settings to refresh the credential status.');
        assert.equal(get('#polaris-api-key').placeholder, '', 'A committed replacement does not guess a new saved flag before reload.');
        assert.equal(get('#polaris-admin-pass').placeholder, '');
        assert.equal(controller.hasUnconfirmedOutcome(), false);
        assert.equal(commits.length, 1); assert.equal(unconfirmed.length, 0);
        assert.equal(get('#settings-save-title').textContent, 'Saved; reload needed');
        failLoad(false); await controller.load({ silent: true });
        assert.equal(get('#polaris-api-key').value, ''); assert.equal(get('#polaris-admin-pass').value, '');
        assert.equal(get('#polaris-api-key').placeholder, '********'); assert.equal(get('#polaris-admin-pass').placeholder, '********');
        assert.equal(get('#polaris-api-key-status').textContent,
          'API key saved. Leave blank to keep the current key; enter a new key to replace it.');
        assert.equal(get('#polaris-admin-pass-status').textContent,
          'System staff password saved. Leave blank to keep the current password; enter a new password to replace it.');
        assert.equal(get('#settings-save-title').textContent, 'No changes');
      } else {
        assert.equal(get('#polaris-api-key-status').textContent, 'No API key saved.', `${outcome}: no confirmed save may be implied`);
        assert.equal(get('#polaris-admin-pass-status').textContent, 'No system staff password saved.');
        assert.equal(commits.length, 0); assert.equal(unconfirmed.length, outcome === 'uncertain' ? 1 : 0);
      }
    });
  }

  await polarisFixture(async ({ get, dom, controller, change, submit, setSave, accept, failLoad, posts }) => {
    const token = get('#email-postmark-token'), clear = get('#email-clear-postmark-token');
    const status = get('#email-token-status');
    assert.equal(token.value, ''); assert.equal(token.placeholder, '********');
    assert.equal(token.getAttribute('aria-describedby'), 'email-token-status');
    assert.equal(status.getAttribute('role'), 'status'); assert.equal(status.getAttribute('aria-live'), 'polite');
    assert.equal(status.textContent,
      'A system Postmark token is stored. Leave blank to keep it; enter a new token to replace it.');
    token.focus(); token.blur();
    assert.equal(controller.isDirty(), false, 'Focusing and leaving Postmark does not create a draft.');

    clear.checked = true; clear.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.equal(token.placeholder, '', 'Selecting clear hides the retain-current mask.');
    assert.match(status.textContent, /removal is pending/);
    clear.checked = false; clear.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.equal(token.placeholder, '********', 'Canceling clear restores the accepted saved mask.');
    assert.match(status.textContent, /token is stored/);
    assert.equal(controller.isDirty(), false, 'Canceling clear restores the original draft.');

    change('#email-postmark-token', 'replacement-postmark-token');
    setSave(async body => {
      assert.equal(body.email.postmarkToken, 'replacement-postmark-token');
      assert.equal(Object.hasOwn(body.email, 'clearPostmarkToken'), false);
      return response(400, { message: 'Postmark replacement rejected.' });
    });
    await submit();
    assert.equal(posts.at(-1).email.postmarkToken, 'replacement-postmark-token');
    assert.equal(token.value, 'replacement-postmark-token');
    assert.equal(status.textContent,
      'A system Postmark token is stored. Leave blank to keep it; enter a new token to replace it.',
      'A rejected replacement does not claim that the new token was saved.');
    token.value = ''; token.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.equal(token.placeholder, '********', 'Deleting an unsaved replacement exposes the accepted saved mask.');

    clear.checked = true; clear.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    setSave(async body => {
      assert.equal(body.email.clearPostmarkToken, true);
      assert.equal(Object.hasOwn(body.email, 'postmarkToken'), false);
      return response(400, { message: 'Postmark removal rejected.' });
    });
    await submit();
    assert.equal(posts.at(-1).email.clearPostmarkToken, true);
    assert.equal(token.placeholder, '', 'A rejected clear retains the selected-removal presentation.');
    assert.match(status.textContent, /removal is pending/);
    clear.checked = false; clear.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.equal(token.placeholder, '********');
    assert.match(status.textContent, /token is stored/);
    assert.equal(controller.isDirty(), false, 'Canceling a rejected clear restores the saved state.');

    clear.checked = true; clear.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    setSave(async body => {
      assert.equal(body.email.clearPostmarkToken, true);
      return response(200, { code: 'saved', data: { orgId: 'system' } });
    });
    await submit();
    assert.equal(controller.hasUnconfirmedOutcome(), true);
    assert.match(status.textContent, /removal outcome is uncertain/);
    assert.equal(token.placeholder, '', 'An uncertain clear remains masked as pending until authoritative reload.');
    await controller.load({ silent: true });
    assert.equal(controller.hasUnconfirmedOutcome(), false);
    assert.equal(token.placeholder, '********');
    assert.match(status.textContent, /token is stored/);
    assert.equal(clear.checked, false);

    change('#email-postmark-token', 'confirmed-postmark-replacement');
    failLoad(true);
    setSave(async body => {
      assert.equal(body.email.postmarkToken, 'confirmed-postmark-replacement');
      assert.equal(Object.hasOwn(body.email, 'clearPostmarkToken'), false);
      accept({ hasPostmarkToken: true });
      return response(200, { code: 'saved', data: { orgId: 'system', version: 'postmark-replacement' } });
    });
    await submit();
    assert.equal(token.value, 'confirmed-postmark-replacement');
    assert.equal(status.textContent, 'Postmark token replacement saved. Reload settings to refresh credential status.');
    assert.equal(controller.hasUnconfirmedOutcome(), false, 'A committed replacement with failed reload remains confirmed.');
    failLoad(false);
    await controller.load({ silent: true });
    assert.equal(token.value, ''); assert.equal(token.placeholder, '********');
    assert.match(status.textContent, /token is stored/);

    clear.checked = true; clear.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    failLoad(true);
    setSave(async body => {
      assert.equal(body.email.clearPostmarkToken, true);
      assert.equal(Object.hasOwn(body.email, 'postmarkToken'), false);
      accept({ hasPostmarkToken: false });
      return response(200, { code: 'saved', data: { orgId: 'system', version: 'postmark-cleared' } });
    });
    await submit();
    assert.equal(token.value, ''); assert.equal(token.placeholder, '');
    assert.equal(status.textContent, 'Postmark token removal saved. Reload settings to refresh credential status.');
    assert.equal(clear.checked, true, 'A failed refresh keeps the committed clear receipt visible until reload.');
    assert.equal(controller.hasUnconfirmedOutcome(), false, 'A confirmed clear with failed refresh is not an uncertain outcome.');
    assert.equal(posts.at(-1).email.clearPostmarkToken, true);
    assert.equal(Object.hasOwn(posts.at(-1).email, 'postmarkToken'), false);
    failLoad(false);
    await controller.load({ silent: true });
    assert.equal(token.value, ''); assert.equal(token.placeholder, '');
    assert.equal(status.textContent, 'No system Postmark token is stored.');
    assert.equal(clear.checked, false, 'An accepted reload clears the pending removal checkbox.');

    change('#email-postmark-token', 'new-postmark-token');
    setSave(async body => {
      assert.equal(body.email.postmarkToken, 'new-postmark-token');
      assert.equal(Object.hasOwn(body.email, 'clearPostmarkToken'), false);
      accept({ hasPostmarkToken: true });
      return response(200, { code: 'saved', data: { orgId: 'system', version: 'postmark-added' } });
    });
    await submit();
    assert.equal(posts.at(-1).email.postmarkToken, 'new-postmark-token');
    assert.equal(token.value, ''); assert.equal(token.placeholder, '********');
    assert.match(status.textContent, /token is stored/,
      'A saved replacement receives its mask only after the accepted reload flag changes.');
  }, { hasPostmarkToken: true });

  await polarisFixture(async ({ get, controller, setDiagnostic, setSync, failLoad, commits, unconfirmed, catalogs, notices }) => {
    const live = get('#polaris-action-announcement');
    assert.equal(live.closest('form'), null, 'sync announcements remain outside the inert form');
    assert.equal(live.getAttribute('role'), 'status'); assert.equal(live.getAttribute('aria-live'), 'polite');
    let completeDiagnostic;
    setDiagnostic(() => new Promise(resolve => { completeDiagnostic = resolve; }));
    get('#btn-test-polaris').click(); await until(() => completeDiagnostic);
    assert.equal(get('#polaris-test-result').textContent, 'Testing saved Polaris connection...');
    assert.equal(live.textContent, get('#polaris-test-result').textContent);
    assert.equal(get('#settings-message').textContent, '');
    completeDiagnostic(response(200, { code: 'polaris_connected', data: { connected: true, organizationCount: 2 } }));
    await until(() => !get('#btn-test-polaris').disabled);
    assert.equal(get('#polaris-test-result').textContent, 'Connected; 2 organizations available.');
    assert.equal(get('#polaris-api-key-status').textContent, 'No API key saved.', 'connectivity cannot set saved presence');
    let completeSync;
    setSync(() => new Promise(resolve => { completeSync = resolve; }));
    get('#btn-sync-organizations').click(); await until(() => completeSync);
    assert.equal(get('#polaris-test-result').textContent, '', 'sync replaces an earlier diagnostic success');
    assert.equal(get('#organizations-sync-result').textContent, 'Synchronizing Polaris organizations...');
    assert.equal(live.textContent, get('#organizations-sync-result').textContent);
    assert.equal(get('#settings-form').inert, true);
    completeSync(response(200, { code: 'synced', data: { received: 3, changed: 2 } }));
    await until(() => !controller.hasPendingMutation());
    assert.equal(get('#organizations-sync-result').textContent, 'Synchronized 3 organizations.', 'accepted refresh retains its own sync result');
    assert.equal(commits.length, 1); assert.equal(catalogs.length, 1); assert.equal(unconfirmed.length, 0);
    assert.equal(commits[0][1], staff); assert.equal(get('#settings-form').inert, false);
    assert.equal(get('#settings-message').textContent, '');
    assert.equal(notices.some(message => /Polaris|synchroniz/i.test(message)), false, 'routine local results do not create visible global notices');
    setDiagnostic(async () => response(502, { code: 'polaris_unavailable' }));
    get('#btn-test-polaris').click(); await until(() => !get('#btn-test-polaris').disabled);
    assert.equal(get('#organizations-sync-result').textContent, '');
    assert.equal(get('#polaris-test-result').textContent, 'Polaris is unavailable.');
    assert.equal(get('#polaris-test-result').classList.contains('error'), true);
    setSync(async () => response(400, { message: 'Synchronization rejected.' }));
    get('#btn-sync-organizations').click(); await until(() => !controller.hasPendingMutation());
    assert.equal(get('#polaris-test-result').textContent, '');
    assert.equal(get('#organizations-sync-result').textContent, 'Synchronization rejected.');
    assert.equal(controller.hasUnconfirmedOutcome(), false);
    failLoad(true);
    setSync(async () => response(200, { code: 'synced', data: { received: 4, changed: 1 } }));
    get('#btn-sync-organizations').click(); await until(() => !controller.hasPendingMutation());
    assert.equal(get('#organizations-sync-result').textContent,
      'Synchronized 4 organizations. Current values could not be refreshed. Reload before editing.');
    assert.equal(get('#settings-message').textContent, '', 'the sync refresh failure is local');
    assert.equal(get('#settings-form').inert, true); assert.equal(controller.hasUnconfirmedOutcome(), false);
    assert.equal(get('#settings-save-title').textContent, 'Saved; reload needed');
    assert.equal(commits.length, 2); assert.equal(unconfirmed.length, 0);
    failLoad(false); await controller.load({ silent: true });
    assert.equal(get('#organizations-sync-result').textContent, ''); assert.equal(live.textContent, '');
    assert.equal(get('#settings-form').inert, false);
  });

  for (const failure of ['transport', 'malformed', 'server']) {
    await polarisFixture(async ({ get, controller, setSync, unconfirmed, commits, notices, refreshed }) => {
      setSync(async () => {
        if (failure === 'transport') throw new TypeError('Network offline');
        return failure === 'server' ? response(503, { message: 'Provider unavailable.' })
          : response(200, { code: 'synced', data: { received: -1, changed: 0 } });
      });
      get('#btn-sync-organizations').click(); await until(() => !controller.hasPendingMutation());
      const result = get('#organizations-sync-result').textContent;
      assert.equal(result, 'Organization synchronization outcome is uncertain. Reload current values before retrying.');
      assert.equal(get('#polaris-action-announcement').textContent, result);
      assert.equal(get('#settings-message').textContent, '');
      assert.equal(notices.some(message => /uncertain/i.test(message)), false);
      assert.equal(controller.hasUnconfirmedOutcome(), true); assert.equal(controller.inspectDeparture().blocked, true);
      assert.equal(get('#settings-form').inert, true); assert.equal(commits.length, 0);
      assert.equal(get('#settings-save-title').textContent, 'Outcome uncertain; reload needed');
      assert.equal(unconfirmed.length, 1); assert.equal(unconfirmed[0][1], staff);
      await controller.load({ silent: true });
      assert.equal(controller.hasUnconfirmedOutcome(), false);
      assert.equal(get('#organizations-sync-result').textContent, '');
      assert.equal(refreshed.at(-1)[1].catalogChanged, true, 'uncertain sync review still invalidates organization consumers');
    });
  }

  for (const retirement of ['reload', 'scope', 'account', 'suspend']) {
    await polarisFixture(async ({ get, controller, setDiagnostic }) => {
      let complete, signal;
      setDiagnostic(init => { signal = init.signal; return new Promise(resolve => { complete = resolve; }); });
      get('#btn-test-polaris').click(); await until(() => complete);
      if (retirement === 'reload') await controller.load({ silent: true });
      if (retirement === 'scope') { const target = await controller.prepareScope('2'); target.accept(); }
      if (retirement === 'account') { controller.setStaff({ ...staff, id: '21' }); await controller.activate('polaris'); }
      if (retirement === 'suspend') { assert.equal(controller.suspend(), true); await controller.activate('polaris'); }
      assert.equal(signal.aborted, true, `${retirement}: the retired diagnostic is cancelled`);
      complete(response(200, { code: 'polaris_connected', data: { connected: true, organizationCount: 99 } }));
      await flush(); await flush();
      assert.equal(get('#polaris-test-result').textContent, '', `${retirement}: a retired result cannot repaint`);
      assert.equal(get('#polaris-action-announcement').textContent, '');
    });
  }
  console.log('Polaris accepted secret flags, blank/library saves, local diagnostic/sync states, receipts, refresh failures, uncertainty and retirement passed');
})().catch(error => { console.error(error); process.exitCode = 1; });
