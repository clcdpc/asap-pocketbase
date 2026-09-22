const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const staffRoot = path.join(frontendRoot, 'staff');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-staff-polaris-settings-'));
  fs.cpSync(staffRoot, path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const dom = new JSDOM(fs.readFileSync(path.join(staffRoot, 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Event = dom.window.Event;
    global.CustomEvent = dom.window.CustomEvent;
    global.Option = dom.window.Option;
    global.requestAnimationFrame = callback => callback();
    global.fetch = async request => ({
      ok: true,
      status: 200,
      statusText: 'OK',
      json: async () => String(request).includes('/email-status') ? { enabled: false } : []
    });

    const fields = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'polaris-fields.js')).href);
    const sequencing = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'polaris-test.js')).href);
    const state = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'state.js')).href);
    const formPopulation = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'form-population.js')).href);
    const serializer = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'serialize-save.js')).href);
    const saveController = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'save-controller.js')).href);
    const http = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'http.js')).href);
    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'test-antiforgery-token',
      staff: { role: 'super_admin', userPrincipalName: 'admin@example.org' }
    });
    const persisted = {
      host: 'https://polaris.example.org',
      accessId: 'access-42',
      staffDomain: 'LIBRARY',
      adminUser: 'asap-service',
      workstationId: 73,
      systemPolarisUserId: 4201,
      organizationIdForRequests: 731,
      pickupOrganizationId: 910,
      hasApiKey: true,
      hasAdminPassword: true
    };

    const currentSystemSettings = {
      staffUrl: 'https://staff.example.org',
      leapBibUrlPattern: 'https://catalog.example.org/bib/{{bibid}}',
      leapPatronUrlPattern: 'https://catalog.example.org/patron/{{patron-id}}',
      formatIconUrlPattern: 'https://cdn.example.org/formats/{format}.svg',
      patronEmbedAllowedOrigins: ['https://library.example.org', 'https://branch.example.org']
    };
    const persistedWorkflow = {
      suggestionLimit: 11,
      suggestionLimitMessage: 'Deliberately non-default suggestion limit',
      outstandingTimeoutEnabled: true,
      outstandingTimeoutDays: 61,
      outstandingTimeoutSendEmail: true,
      outstandingTimeoutRejectionTemplateId: '917',
      holdPickupTimeoutEnabled: true,
      holdPickupTimeoutDays: 27,
      pendingHoldTimeoutEnabled: true,
      pendingHoldTimeoutDays: 45,
      additionalCopyTimeoutEnabled: true,
      additionalCopyTimeoutDays: 33,
      autoPromote: true,
      commonAuthorsEnabled: true,
      commonAuthorsLabel: 'Stored popular creators',
      commonAuthorsHelp: 'Stored creator help',
      commonAuthorsMessage: 'Stored creator message',
      allowPatronAutoholdOptOut: true,
      allowAnyRegisteredCardLogin: true,
      patronCodeEligibilityEnabled: true,
      patronCodeEligibilityMessage: 'Stored patron-code message'
    };
    const compatibilityWorkflow = {
      suggestionLimit: 2,
      suggestionLimitMessage: 'Incomplete compatibility workflow',
      commonAuthorsEnabled: false,
      commonAuthorsLabel: 'Compatibility creator label',
      commonAuthorsHelp: 'Compatibility creator help',
      commonAuthorsMessage: 'Compatibility creator message',
      allowPatronAutoholdOptOut: false,
      allowAnyRegisteredCardLogin: false,
      patronCodeEligibilityEnabled: false,
      patronCodeEligibilityMessage: 'Compatibility patron-code message'
    };
    const systemSettingsResponse = {
      stored: {
        systemSettings: currentSystemSettings,
        polaris: persisted,
        workflow: persistedWorkflow
      },
      emails: {
        rejection_templates: [{ id: '917', name: 'Stored timeout rejection' }]
      },
      ui_text: {},
      workflow: compatibilityWorkflow
    };
    formPopulation.applyLibrarySettingsToForm(systemSettingsResponse);
    function restoreAllowedPatronCodes() {
      let allowedPatronCodeIds = document.getElementById('allowed-patron-code-ids');
      if (!allowedPatronCodeIds) {
        allowedPatronCodeIds = document.createElement('input');
        allowedPatronCodeIds.type = 'hidden';
        allowedPatronCodeIds.id = 'allowed-patron-code-ids';
        document.getElementById('allowed-patron-code-container').appendChild(allowedPatronCodeIds);
      }
      allowedPatronCodeIds.value = '31,47';
    }
    restoreAllowedPatronCodes();
    assert.strictEqual(document.getElementById('system-staff-url').value, currentSystemSettings.staffUrl);
    assert.strictEqual(document.getElementById('leap-bib-url-pattern').value, currentSystemSettings.leapBibUrlPattern);
    assert.strictEqual(document.getElementById('leap-patron-url-pattern').value, currentSystemSettings.leapPatronUrlPattern);
    assert.strictEqual(document.getElementById('format-icon-url-pattern').value, currentSystemSettings.formatIconUrlPattern);
    assert.strictEqual(document.getElementById('patron-embed-allowed-origins').value,
      currentSystemSettings.patronEmbedAllowedOrigins.join('\n'));
    assert.strictEqual(document.getElementById('suggestion-limit').value, '11');
    assert.strictEqual(document.getElementById('suggestion-limit-msg').value, persistedWorkflow.suggestionLimitMessage);
    assert.strictEqual(document.getElementById('outstanding-timeout-enabled').checked, true);
    assert.strictEqual(document.getElementById('outstanding-timeout-days').value, '61');
    assert.strictEqual(document.getElementById('outstanding-timeout-send-email').checked, true);
    assert.strictEqual(document.getElementById('outstanding-timeout-rejection-template-id').value, '917');
    assert.strictEqual(document.getElementById('hold-pickup-timeout-enabled').checked, true);
    assert.strictEqual(document.getElementById('hold-pickup-timeout-days').value, '27');
    assert.strictEqual(document.getElementById('pending-hold-timeout-enabled').checked, true);
    assert.strictEqual(document.getElementById('pending-hold-timeout-days').value, '45');
    assert.strictEqual(document.getElementById('additional-copy-timeout-enabled').checked, true);
    assert.strictEqual(document.getElementById('additional-copy-timeout-days').value, '33');
    assert.strictEqual(document.getElementById('polaris-auto-promote').checked, true);
    assert.strictEqual(document.getElementById('wf-common-authors-enabled').checked, true);
    assert.strictEqual(document.getElementById('wf-common-authors-label').value, persistedWorkflow.commonAuthorsLabel);
    assert.strictEqual(document.getElementById('wf-common-authors-help').value, persistedWorkflow.commonAuthorsHelp);
    assert.strictEqual(document.getElementById('wf-common-authors-message').value, persistedWorkflow.commonAuthorsMessage);
    assert.strictEqual(document.getElementById('allow-patron-autohold-opt-out').checked, true);
    assert.strictEqual(document.getElementById('allow-any-registered-card-login').checked, true);
    assert.strictEqual(document.getElementById('patron-code-eligibility-message').value,
      persistedWorkflow.patronCodeEligibilityMessage);

    fields.populatePolarisSettingsForm(persisted);
    assert.strictEqual(document.getElementById('polaris-api-key').value, '');
    assert.strictEqual(document.getElementById('polaris-admin-pass').value, '');
    assert.strictEqual(document.getElementById('polaris-api-key-status').classList.contains('hidden'), false);
    assert.strictEqual(document.getElementById('polaris-admin-pass-status').classList.contains('hidden'), false);
    assert.strictEqual(document.getElementById('polaris-system-user-id').value, '4201');
    assert.strictEqual(document.getElementById('polaris-requesting-org-id').value, '731');
    assert.strictEqual(document.getElementById('polaris-pickup-org-id').value, '910');

    document.getElementById('polaris-pickup-org-id').value = '0';
    assert.strictEqual(fields.collectSettingsPolaris(true).pickupOrganizationId, 0,
      'the shipped pickup-organization fallback sentinel must round-trip');
    document.getElementById('polaris-pickup-org-id').value = '910';

    const roundTrip = fields.collectSettingsPolaris(true);
    assert.deepStrictEqual(roundTrip, {
      host: persisted.host,
      accessId: persisted.accessId,
      staffDomain: persisted.staffDomain,
      adminUser: persisted.adminUser,
      workstationId: persisted.workstationId,
      systemPolarisUserId: persisted.systemPolarisUserId,
      organizationIdForRequests: persisted.organizationIdForRequests,
      pickupOrganizationId: persisted.pickupOrganizationId,
      clearApiKey: false,
      clearAdminPassword: false
    });
    for (const obsolete of ['userId', 'requestingOrgId', 'pickupOrgId', 'langId', 'appId', 'orgId']) {
      assert.strictEqual(Object.hasOwn(roundTrip, obsolete), false, `${obsolete} must not be serialized`);
    }

    const workflowPayload = serializer.buildSettingsPayload();
    for (const [field, expected] of Object.entries(persistedWorkflow)) {
      assert.strictEqual(workflowPayload[field], expected, `${field} must serialize from stored.workflow`);
    }

    const ordered = [];
    const sentSettingsPayloads = [];
    let saveCompleted = false;
    global.fetch = async (request, options = {}) => {
      const url = String(request);
      const method = String(options.method || 'GET').toUpperCase();
      if (url === '/api/asap/staff/settings/library' && method === 'POST') {
        const body = JSON.parse(options.body);
        sentSettingsPayloads.push(body);
        systemSettingsResponse.stored.workflow = { ...body.workflow };
        saveCompleted = true;
        ordered.push('save');
        return { ok: true, status: 200, statusText: 'OK', json: async () => ({ code: 'saved' }) };
      }
      if (url === '/api/asap/staff/polaris/test' && method === 'POST') {
        assert.strictEqual(saveCompleted, true, 'Polaris test must not run before the settings save completes');
        ordered.push('test');
        return { ok: true, status: 200, statusText: 'OK', json: async () => ({ code: 'polaris_connected' }) };
      }
      if (url.startsWith('/api/asap/staff/settings/library') && method === 'GET') {
        return { ok: true, status: 200, statusText: 'OK', json: async () => systemSettingsResponse };
      }
      if (url.startsWith('/api/asap/staff/polaris/patron-codes')) {
        return {
          ok: true,
          status: 200,
          statusText: 'OK',
          json: async () => ({ data: [
            { id: '31', description: 'Adult' },
            { id: '47', description: 'Young adult' }
          ] })
        };
      }
      if (url.startsWith('/api/asap/staff/users')) {
        return { ok: true, status: 200, statusText: 'OK', json: async () => ({ users: [] }) };
      }
      if (url.startsWith('/api/asap/staff/organizations')) {
        return { ok: true, status: 200, statusText: 'OK', json: async () => ({ data: [] }) };
      }
      return { ok: true, status: 200, statusText: 'OK', json: async () => ({}) };
    };
    const success = await sequencing.saveThenTestPolaris(
      () => saveController.saveSettings({ clearDelay: 0 }),
      () => http.authorizedJson('/api/asap/staff/polaris/test', { method: 'POST' })
    );
    assert.deepStrictEqual(ordered, ['save', 'test']);
    assert.strictEqual(success.saved, true);
    assert.strictEqual(success.tested, true);
    const saveAndTestPayload = sentSettingsPayloads[0];
    assert.strictEqual(saveAndTestPayload.staffUrl, `${currentSystemSettings.staffUrl}/`,
      'Save & test must retain the loaded current system settings contract');
    assert.strictEqual(saveAndTestPayload.polaris.systemPolarisUserId, persisted.systemPolarisUserId);
    assert.strictEqual(saveAndTestPayload.polaris.organizationIdForRequests, persisted.organizationIdForRequests);
    assert.strictEqual(saveAndTestPayload.polaris.pickupOrganizationId, persisted.pickupOrganizationId);
    for (const [field, expected] of Object.entries(persistedWorkflow)) {
      assert.strictEqual(saveAndTestPayload.workflow[field], expected,
        `Save & test must preserve workflow.${field}`);
    }

    restoreAllowedPatronCodes();
    ordered.length = 0;
    saveCompleted = false;
    assert.strictEqual(await saveController.saveSettings({ clearDelay: 0 }), true);
    assert.deepStrictEqual(ordered, ['save']);
    const ordinarySavePayload = sentSettingsPayloads[1];
    for (const [field, expected] of Object.entries(persistedWorkflow)) {
      assert.strictEqual(ordinarySavePayload.workflow[field], expected,
        `ordinary save must preserve workflow.${field}`);
    }

    restoreAllowedPatronCodes();
    document.getElementById('pending-hold-timeout-days').value = '46';
    const intentionalEdit = serializer.buildSettingsPayload();
    assert.strictEqual(intentionalEdit.pendingHoldTimeoutDays, 46,
      'an intentional workflow edit must override the loaded stored value');
    assert.strictEqual(intentionalEdit.outstandingTimeoutDays, persistedWorkflow.outstandingTimeoutDays);
    ordered.length = 0;
    saveCompleted = false;
    assert.strictEqual(await saveController.saveSettings({ clearDelay: 0 }), true);
    assert.deepStrictEqual(ordered, ['save']);
    assert.strictEqual(sentSettingsPayloads[2].workflow.pendingHoldTimeoutDays, 46);
    assert.strictEqual(document.getElementById('pending-hold-timeout-days').value, '46',
      'the intended workflow edit must survive save and reload');
    assert.strictEqual(document.getElementById('outstanding-timeout-days').value, '61');

    state.setCurrentLibraryContextOrgId('2');
    const effectiveLibraryWorkflow = {
      ...persistedWorkflow,
      outstandingTimeoutDays: 72,
      pendingHoldTimeoutDays: 19,
      autoPromote: false
    };
    formPopulation.applyLibrarySettingsToForm({
      stored: { workflow: persistedWorkflow },
      emails: {},
      ui_text: {},
      workflow: effectiveLibraryWorkflow
    });
    assert.strictEqual(document.getElementById('outstanding-timeout-days').value, '72',
      'library context must continue to populate effective/inherited workflow values');
    assert.strictEqual(document.getElementById('pending-hold-timeout-days').value, '19');
    assert.strictEqual(document.getElementById('polaris-auto-promote').checked, false);
    state.setCurrentLibraryContextOrgId('system');

    document.getElementById('polaris-requesting-org-id').value = '732';
    const oneChange = fields.collectSettingsPolaris(true);
    assert.strictEqual(oneChange.organizationIdForRequests, 732);
    assert.strictEqual(oneChange.systemPolarisUserId, 4201);
    assert.strictEqual(oneChange.pickupOrganizationId, 910);

    document.getElementById('polaris-pickup-org-id').remove();
    const missingControl = fields.collectSettingsPolaris(true);
    assert.strictEqual(Object.hasOwn(missingControl, 'pickupOrganizationId'), false,
      'a missing form control must not clear a persisted value');

    fields.bindPolarisSecretControls();
    const apiKey = document.getElementById('polaris-api-key');
    const clearApiKey = document.getElementById('polaris-clear-api-key');
    const adminPassword = document.getElementById('polaris-admin-pass');
    const clearAdminPassword = document.getElementById('polaris-clear-admin-pass');
    apiKey.value = 'replacement-api-key';
    clearApiKey.checked = true;
    clearApiKey.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.strictEqual(apiKey.value, '');
    assert.strictEqual(adminPassword.value, '');
    assert.strictEqual(clearAdminPassword.checked, false);
    adminPassword.value = 'replacement-password';
    adminPassword.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.strictEqual(clearAdminPassword.checked, false);
    assert.strictEqual(clearApiKey.checked, true, 'editing the admin password must not change the API-key clear choice');
    const secretChange = fields.collectSettingsPolaris(true);
    assert.strictEqual(Object.hasOwn(secretChange, 'apiKey'), false);
    assert.strictEqual(secretChange.clearApiKey, true);
    assert.strictEqual(secretChange.adminPassword, 'replacement-password');
    assert.strictEqual(secretChange.clearAdminPassword, false);

    assert.strictEqual(fields.isPolarisConfigured(persisted), true);
    assert.strictEqual(fields.isPolarisConfigured({ ...persisted, hasApiKey: false }), false);
    assert.strictEqual(fields.isPolarisConfigured({ ...persisted, adminUser: '' }), false);
    assert.strictEqual(fields.isPolarisConfigured({ ...persisted, apiKey: '', adminPassword: '' }), true,
      'configured detection must not require disclosed secrets');

    let testsAfterFailure = 0;
    const failedSave = await sequencing.saveThenTestPolaris(
      async () => false,
      async () => { testsAfterFailure++; }
    );
    assert.deepStrictEqual(failedSave, { saved: false, tested: false });
    assert.strictEqual(testsAfterFailure, 0);

    let savedBeforeTestFailure = false;
    await assert.rejects(() => sequencing.saveThenTestPolaris(
      async () => { savedBeforeTestFailure = true; return true; },
      async () => { throw new Error('Polaris unavailable'); }
    ), /Polaris unavailable/);
    assert.strictEqual(savedBeforeTestFailure, true, 'a test failure occurs after persistence succeeds');

    const index = fs.readFileSync(path.join(staffRoot, 'index.html'), 'utf8');
    const polarisSource = fs.readFileSync(path.join(staffRoot, 'js', 'settings-polaris.js'), 'utf8');
    const collectorSource = fs.readFileSync(path.join(staffRoot, 'js', 'settings', 'polaris-fields.js'), 'utf8');
    const loaderSource = fs.readFileSync(path.join(staffRoot, 'js', 'settings', 'loader.js'), 'utf8');
    const saveControllerSource = fs.readFileSync(path.join(staffRoot, 'js', 'settings', 'save-controller.js'), 'utf8');
    const serializerSource = fs.readFileSync(path.join(staffRoot, 'js', 'settings', 'serialize-save.js'), 'utf8');
    assert.doesNotMatch(index + polarisSource, /btn-sync-material-types|material-types-sync-result|Material type cache/);
    assert.doesNotMatch(index + polarisSource + collectorSource + serializerSource, /overridePassword|polaris-override-pass/);
    assert.doesNotMatch(collectorSource, /\b(?:userId|requestingOrgId|pickupOrgId)\s*:/);
    assert.match(loaderSource, /stored && loadedLibrarySettings\.stored\.polaris/);
    assert.match(loaderSource, /isPolarisConfigured\(polaris\)/);
    assert.match(saveControllerSource, /saveContextSerial !== libraryContextLoadSerial/);
    assert.match(serializerSource, /postmarkToken: getFieldValue\('postmark-token'\)\.trim\(\)/);
    assert.match(serializerSource, /clearPostmarkToken: getFieldChecked\('postmark-clear-token'\)/);
    assert.match(index, /id="postmark-token-status"/);
    assert.match(index, /Leave blank to keep the saved token/);

    dom.window.close();
    console.log('Primary staff Polaris settings contract checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
