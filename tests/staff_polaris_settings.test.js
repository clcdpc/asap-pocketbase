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
    const patronCodes = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'patron-codes.js')).href);
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
    const persistedCommonCreators = ['Octavia E. Butler', 'N. K. Jemisin'];
    const persistedAllowedPatronCodeIds = ['31', '47'];
    const persistedProviders = [
      {
        id: '201', sortOrder: 13,
        key: 'external_search_1', isEnabled: true, label: 'Search Local Discovery',
        urlTemplate: 'https://discovery.example.org/search?title={{title}}'
      },
      {
        id: '202', sortOrder: 29,
        key: 'external_search_2', isEnabled: false, label: 'Disabled Research Index',
        urlTemplate: 'https://research.example.org/find/{{isbn}}'
      },
      {
        id: '203', sortOrder: 44,
        key: 'external_search_3', isEnabled: true, label: 'Search Regional Catalog',
        urlTemplate: 'https://regional.example.org/?q={{title}}+{{author}}'
      }
    ];
    const persistedFormats = [
      {
        id: '102', code: 'dvd', ownerOrganizationId: '1', label: 'Disc and Video', sortOrder: 17,
        isEnabled: false, messageBehavior: 'message', message: 'Ask staff about video purchases.',
        titleMode: 'required', titleLabel: 'Video title', authorMode: 'hidden', authorLabel: 'Director',
        identifierMode: 'optional', identifierLabel: 'UPC', publicationMode: 'required', publicationLabel: 'Release timing'
      },
      {
        id: '101', code: 'book', ownerOrganizationId: '1', label: 'Printed Books', sortOrder: 43,
        isEnabled: true, messageBehavior: 'none', message: '',
        titleMode: 'required', titleLabel: 'Book title', authorMode: 'optional', authorLabel: 'Creator',
        identifierMode: 'required', identifierLabel: 'ISBN', publicationMode: 'hidden', publicationLabel: 'Publication timing'
      }
    ];
    const organizations = [
      { id: 1, displayName: 'System', active: true },
      { id: 2, displayName: 'Central', active: true },
      { id: 3, displayName: 'North', active: false },
      { id: 4, displayName: 'South', active: true }
    ];
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
        workflow: persistedWorkflow,
        commonCreators: persistedCommonCreators,
        allowedPatronCodeIds: persistedAllowedPatronCodeIds,
        providers: persistedProviders,
        formats: persistedFormats,
        customFields: [],
        autoClaimRules: []
      },
      emails: {
        rejection_templates: [{ id: '917', name: 'Stored timeout rejection' }]
      },
      ui_text: {
        publicationOptions: [
          { id: 'forthcoming', label: 'Forthcoming', enabled: true, sortOrder: 17 },
          { id: 'backlist', label: 'Backlist', enabled: false, sortOrder: 41 }
        ]
      },
      effective: {
        workflow: persistedWorkflow,
        commonCreators: persistedCommonCreators,
        allowedPatronCodeIds: persistedAllowedPatronCodeIds,
        externalSearchProviders: persistedProviders,
        publicationOptions: [
          { id: 'forthcoming', label: 'Forthcoming', enabled: true, sortOrder: 17 },
          { id: 'backlist', label: 'Backlist', enabled: false, sortOrder: 41 }
        ],
        formats: persistedFormats.map(format => ({
          id: format.id, code: format.code, label: format.label, sortOrder: format.sortOrder,
          isEnabled: format.isEnabled, messageBehavior: format.messageBehavior, message: format.message,
          title: { mode: format.titleMode, label: format.titleLabel },
          author: { mode: format.authorMode, label: format.authorLabel },
          identifier: { mode: format.identifierMode, label: format.identifierLabel },
          publication: { mode: format.publicationMode, label: format.publicationLabel },
          customFields: {}
        })),
        customFields: []
      },
      autoClaimStaff: [],
      workflow: compatibilityWorkflow
    };
    global.fetch = async (request, options = {}) => {
      const url = String(request);
      if (url.startsWith('/api/asap/staff/polaris/patron-codes')) {
        return {
          ok: true,
          status: 200,
          statusText: 'OK',
          json: async () => [
            { id: '31', description: 'Adult' },
            { id: '47', description: 'Young adult' },
            { id: '62', description: 'Educator' }
          ]
        };
      }
      if (url.startsWith('/api/asap/staff/organizations')) {
        return { ok: true, status: 200, statusText: 'OK', json: async () => organizations };
      }
      return {
        ok: true,
        status: 200,
        statusText: 'OK',
        json: async () => url.includes('/email-status') ? { enabled: false } : {}
      };
    };
    formPopulation.applyLibrarySettingsToForm(systemSettingsResponse);
    async function settleAsyncRendering() {
      for (let index = 0; index < 4; index++) {
        await new Promise(resolve => setImmediate(resolve));
      }
    }
    await settleAsyncRendering();
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
    assert.strictEqual(document.getElementById('wf-common-authors-list').value,
      persistedCommonCreators.join('\n'));
    assert.strictEqual(document.getElementById('allowed-patron-code-ids').value,
      persistedAllowedPatronCodeIds.join(','));
    persistedProviders.forEach((provider, index) => {
      const number = index + 1;
      assert.strictEqual(document.getElementById(`wf-external-search-${number}-enabled`).checked,
        provider.isEnabled);
      assert.strictEqual(document.getElementById(`wf-external-search-${number}-label`).value,
        provider.label);
      assert.strictEqual(document.getElementById(`wf-external-search-${number}-url-template`).value,
        provider.urlTemplate);
    });
    assert.strictEqual(document.getElementById('wf-external-search-4-enabled').closest('.form-row').classList.contains('hidden'), true,
      'a legacy provider slot without an authoritative provider must be hidden');
    assert.deepStrictEqual(Array.from(document.querySelectorAll('.format-setting-row')).map(row => row.getAttribute('data-key')), ['dvd', 'book']);
    assert.strictEqual(document.querySelector('.format-setting-row[data-key="dvd"] .format-enabled-check').checked, false);
    assert.strictEqual(document.querySelector('.format-setting-row[data-key="book"] .format-label-input').value, 'Printed Books');
    assert.deepStrictEqual(
      Array.from(document.querySelectorAll('.lib-participation-cb:checked')).map(item => item.value),
      ['2', '4']);
    assert.strictEqual(document.getElementById('lib-p-1'), null,
      'the system organization is not a selectable participating library');

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
    assert.strictEqual(workflowPayload.commonAuthorsList, persistedCommonCreators.join('\n'));
    assert.deepStrictEqual(workflowPayload.allowedPatronCodeIds, persistedAllowedPatronCodeIds);
    assert.strictEqual(workflowPayload.formatIconUrlPattern, currentSystemSettings.formatIconUrlPattern);
    assert.deepStrictEqual(workflowPayload.enabledLibraryOrgIds, ['2', '4']);
    assert.deepStrictEqual(workflowPayload.providers, persistedProviders.map(provider => ({ ...provider })));
    assert.strictEqual(Object.keys(workflowPayload).some(key => key.startsWith('externalSearch4')), false);
    assert.deepStrictEqual(workflowPayload.formats.map(format => ({
      id: format.id, code: format.code, label: format.label, sortOrder: format.sortOrder, isEnabled: format.isEnabled
    })), persistedFormats.map(format => ({
      id: format.id, code: format.code, label: format.label, sortOrder: format.sortOrder, isEnabled: format.isEnabled
    })));
    assert.deepStrictEqual(workflowPayload.ui_text.publicationOptions.map(option => [option.id, option.enabled, option.sortOrder]),
      [['forthcoming', true, 17], ['backlist', false, 41]]);

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
        systemSettingsResponse.stored.commonCreators = body.workflow.commonAuthorsList
          .split('\n').filter(Boolean);
        systemSettingsResponse.stored.allowedPatronCodeIds = [...body.workflow.allowedPatronCodeIds];
        systemSettingsResponse.stored.providers = body.providers.map(provider => ({ ...provider }));
        systemSettingsResponse.effective.externalSearchProviders = body.providers.map(provider => ({ ...provider }));
        systemSettingsResponse.stored.formats = body.formats.map(format => ({ ...format }));
        systemSettingsResponse.effective.formats = body.formats.map(format => ({ ...format }));
        systemSettingsResponse.stored.systemSettings.formatIconUrlPattern = body.formatIconUrlPattern;
        if (Object.hasOwn(body, 'enabledLibraryOrgIds')) {
          const enabled = new Set(body.enabledLibraryOrgIds.map(String));
          organizations.filter(organization => organization.id !== 1).forEach(organization => {
            organization.active = enabled.has(String(organization.id));
          });
        }
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
          json: async () => [
            { id: '31', description: 'Adult' },
            { id: '47', description: 'Young adult' },
            { id: '62', description: 'Educator' }
          ]
        };
      }
      if (url.startsWith('/api/asap/staff/users')) {
        return { ok: true, status: 200, statusText: 'OK', json: async () => ({ users: [] }) };
      }
      if (url.startsWith('/api/asap/staff/organizations')) {
        return { ok: true, status: 200, statusText: 'OK', json: async () => organizations };
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
    assert.strictEqual(saveAndTestPayload.workflow.commonAuthorsList, persistedCommonCreators.join('\n'));
    assert.deepStrictEqual(saveAndTestPayload.workflow.allowedPatronCodeIds, ['31', '47']);
    assert.strictEqual(saveAndTestPayload.formatIconUrlPattern, currentSystemSettings.formatIconUrlPattern);
    assert.deepStrictEqual(saveAndTestPayload.enabledLibraryOrgIds, ['2', '4']);
    assert.strictEqual(Object.hasOwn(saveAndTestPayload.workflow, 'enabledLibraryOrgIds'), false);
    assert.deepStrictEqual(saveAndTestPayload.providers, persistedProviders);
    assert.strictEqual(Object.keys(saveAndTestPayload.workflow).some(key => key.startsWith('externalSearch')), false);
    assert.deepStrictEqual(saveAndTestPayload.formats.map(format => format.code), ['dvd', 'book']);
    assert.strictEqual(saveAndTestPayload.formats.find(format => format.code === 'dvd').isEnabled, false);
    assert.strictEqual(saveAndTestPayload.formats.find(format => format.code === 'book').sortOrder, 43);

    await settleAsyncRendering();
    ordered.length = 0;
    saveCompleted = false;
    assert.strictEqual(await saveController.saveSettings({ clearDelay: 0 }), true);
    assert.deepStrictEqual(ordered, ['save']);
    const ordinarySavePayload = sentSettingsPayloads[1];
    for (const [field, expected] of Object.entries(persistedWorkflow)) {
      assert.strictEqual(ordinarySavePayload.workflow[field], expected,
        `ordinary save must preserve workflow.${field}`);
    }
    assert.strictEqual(ordinarySavePayload.workflow.commonAuthorsList, persistedCommonCreators.join('\n'));
    assert.deepStrictEqual(ordinarySavePayload.workflow.allowedPatronCodeIds, ['31', '47']);
    assert.strictEqual(ordinarySavePayload.formatIconUrlPattern, currentSystemSettings.formatIconUrlPattern);
    assert.deepStrictEqual(ordinarySavePayload.enabledLibraryOrgIds, ['2', '4']);

    await settleAsyncRendering();
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

    await settleAsyncRendering();
    document.getElementById('wf-common-authors-list').value = 'Ursula K. Le Guin\nJames Baldwin';
    document.getElementById('patron-code-choice-0').checked = false;
    document.getElementById('patron-code-choice-0').dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    document.getElementById('wf-external-search-2-label').value = 'Edited Research Index';
    document.getElementById('format-icon-url-pattern').value = 'https://new.example.org/icons/{format}.png';
    document.getElementById('lib-p-3').checked = true;
    document.getElementById('lib-p-4').checked = false;
    const targetedEdit = serializer.buildSettingsPayload();
    assert.strictEqual(targetedEdit.commonAuthorsList, 'Ursula K. Le Guin\nJames Baldwin');
    assert.deepStrictEqual(targetedEdit.allowedPatronCodeIds, ['47']);
    assert.strictEqual(targetedEdit.providers.find(provider => provider.key === 'external_search_2').label, 'Edited Research Index');
    assert.strictEqual(targetedEdit.formatIconUrlPattern, 'https://new.example.org/icons/{format}.png');
    assert.deepStrictEqual(targetedEdit.enabledLibraryOrgIds, ['2', '3']);
    assert.strictEqual(targetedEdit.providers.find(provider => provider.key === 'external_search_1').label, persistedProviders[0].label);
    ordered.length = 0;
    saveCompleted = false;
    assert.strictEqual(await saveController.saveSettings({ clearDelay: 0 }), true);
    const targetedPost = sentSettingsPayloads[3];
    assert.strictEqual(targetedPost.workflow.commonAuthorsList, targetedEdit.commonAuthorsList);
    assert.deepStrictEqual(targetedPost.workflow.allowedPatronCodeIds, targetedEdit.allowedPatronCodeIds);
    assert.strictEqual(targetedPost.providers.find(provider => provider.key === 'external_search_2').label,
      targetedEdit.providers.find(provider => provider.key === 'external_search_2').label);
    assert.strictEqual(targetedPost.formatIconUrlPattern, targetedEdit.formatIconUrlPattern);
    assert.deepStrictEqual(targetedPost.enabledLibraryOrgIds, targetedEdit.enabledLibraryOrgIds);
    assert.strictEqual(Object.hasOwn(targetedPost.workflow, 'enabledLibraryOrgIds'), false);
    await settleAsyncRendering();
    assert.strictEqual(document.getElementById('wf-common-authors-list').value, targetedEdit.commonAuthorsList);
    assert.strictEqual(document.getElementById('allowed-patron-code-ids').value, targetedEdit.allowedPatronCodeIds.join(','));
    assert.strictEqual(document.getElementById('wf-external-search-2-label').value,
      targetedEdit.providers.find(provider => provider.key === 'external_search_2').label);
    assert.strictEqual(document.getElementById('format-icon-url-pattern').value, targetedEdit.formatIconUrlPattern);
    assert.deepStrictEqual(
      Array.from(document.querySelectorAll('.lib-participation-cb:checked')).map(item => item.value),
      ['2', '3']);

    state.setOrganizationsStatus('error');
    const participationContainer = document.getElementById('enabled-libraries-checkbox-container');
    participationContainer.removeAttribute('data-loaded');
    await fields.renderLibraryParticipationCheckboxes();
    assert.strictEqual(fields.collectEnabledLibraryIds(), undefined);
    patronCodes.updatePatronCodesStatusUi('error', 'Simulated patron-code load failure');
    const partialLoadPayload = serializer.buildSettingsPayload();
    assert.strictEqual(Object.hasOwn(partialLoadPayload, 'enabledLibraryOrgIds'), false,
      'an unavailable organization list must omit participation instead of disabling every library');
    assert.deepStrictEqual(partialLoadPayload.allowedPatronCodeIds, ['47'],
      'an unavailable patron-code list must retain the authoritative selection');
    ordered.length = 0;
    saveCompleted = false;
    assert.strictEqual(await saveController.saveSettings({ clearDelay: 0 }), true);
    const partialLoadPost = sentSettingsPayloads[4];
    assert.strictEqual(Object.hasOwn(partialLoadPost, 'enabledLibraryOrgIds'), false);
    assert.strictEqual(Object.hasOwn(partialLoadPost.workflow, 'enabledLibraryOrgIds'), false);
    assert.deepStrictEqual(partialLoadPost.workflow.allowedPatronCodeIds, ['47']);
    assert.deepStrictEqual(organizations.filter(item => item.id !== 1 && item.active).map(item => item.id), [2, 3]);
    state.setOrganizationsStatus('loaded');
    patronCodes.updatePatronCodesStatusUi('loaded', 'Patron codes loaded.');

    state.setCurrentLibraryContextOrgId('2');
    const effectiveLibraryWorkflow = {
      ...persistedWorkflow,
      outstandingTimeoutDays: 72,
      pendingHoldTimeoutDays: 19,
      autoPromote: false
    };
    const libraryFormats = [
      {
        ...persistedFormats.find(format => format.code === 'book'),
        label: 'Library Books', sortOrder: 23, identifierMode: 'hidden', identifierLabel: 'Local ISBN', overridden: true
      },
      {
        id: '301', code: 'zine', ownerOrganizationId: '2', label: 'Community Zine', sortOrder: 57,
        isEnabled: true, messageBehavior: 'message', message: 'Bring local zines to the desk.',
        titleMode: 'required', titleLabel: 'Zine title', authorMode: 'optional', authorLabel: 'Maker',
        identifierMode: 'hidden', identifierLabel: 'Identifier', publicationMode: 'optional', publicationLabel: 'Issue date'
      }
    ];
    const libraryCustomFields = [{
      id: '401', key: 'audience_note', type: 'select', label: 'Audience note', helpText: 'Choose the intended audience.',
      enabled: true, sortOrder: 37,
      options: [
        { id: 'general', label: 'General readers', enabled: true, sortOrder: 13 },
        { id: 'specialist', label: 'Specialists', enabled: false, sortOrder: 31 }
      ]
    }];
    const libraryProviders = persistedProviders.map(provider => provider.key === 'external_search_2'
      ? { ...provider, label: 'Library Research Index', overridden: true }
      : { ...provider, overridden: false });
    const librarySettingsResponse = {
      version: 'library-version-1',
      isOverride: true,
      stored: {
        workflow: effectiveLibraryWorkflow,
        commonCreators: ['Library Creator B', 'Library Creator A'],
        allowedPatronCodeIds: ['47'],
        providers: libraryProviders,
        formats: libraryFormats,
        customFields: libraryCustomFields,
        autoClaimRules: [{ id: '501', materialFormatId: '301', staffUserId: '9007199254740993', active: true }],
        libraryOverride: {
          commonCreators: { exists: true, values: [{ value: 'Library Creator B' }, { value: 'Library Creator A' }] },
          allowedPatronCodeIds: { exists: true, values: ['47'] },
          providers: [{ id: '202', label: 'Library Research Index' }],
          formats: [{ kind: 'systemOverride', materialFormatId: '101', label: 'Library Books' }]
        }
      },
      emails: {},
      ui_text: { publicationOptions: [{ id: 'local', label: 'Local publication', enabled: true, sortOrder: 19 }] },
      effective: {
        workflow: effectiveLibraryWorkflow,
        commonCreators: ['Library Creator B', 'Library Creator A'],
        allowedPatronCodeIds: ['47'],
        externalSearchProviders: libraryProviders,
        publicationOptions: [{ id: 'local', label: 'Local publication', enabled: true, sortOrder: 19 }],
        formats: libraryFormats.map(format => ({
          id: format.id, code: format.code, label: format.label, sortOrder: format.sortOrder,
          isEnabled: format.isEnabled, messageBehavior: format.messageBehavior, message: format.message,
          title: { mode: format.titleMode, label: format.titleLabel },
          author: { mode: format.authorMode, label: format.authorLabel },
          identifier: { mode: format.identifierMode, label: format.identifierLabel },
          publication: { mode: format.publicationMode, label: format.publicationLabel },
          customFields: format.code === 'zine' ? { audience_note: { mode: 'required', labelOverride: 'Zine audience' } } : {}
        })),
        customFields: libraryCustomFields
      },
      autoClaimStaff: [{ id: '9007199254740993', label: 'Large-ID Librarian' }]
    };
    formPopulation.applyLibrarySettingsToForm(librarySettingsResponse);
    serializer.rememberLastSavedLibrarySettings(librarySettingsResponse);
    await settleAsyncRendering();
    assert.strictEqual(document.getElementById('outstanding-timeout-days').value, '72',
      'library context must continue to populate effective/inherited workflow values');
    assert.strictEqual(document.getElementById('pending-hold-timeout-days').value, '19');
    assert.strictEqual(document.getElementById('polaris-auto-promote').checked, false);
    assert.strictEqual(document.getElementById('wf-common-authors-list').value, 'Library Creator B\nLibrary Creator A');
    assert.strictEqual(document.getElementById('wf-external-search-2-label').value, 'Library Research Index');
    assert.strictEqual(document.querySelector('.format-setting-row[data-key="zine"] .format-label-input').value, 'Community Zine');
    assert.strictEqual(document.querySelector('.format-setting-row[data-key="zine"] .format-claim-staff-select').value,
      '9007199254740993');
    assert.strictEqual(document.querySelector('.additional-field-row').getAttribute('data-field-key'), 'audience_note');

    const libraryRoundTrip = serializer.buildSettingsPayload();
    assert.strictEqual(libraryRoundTrip.commonAuthorsList, 'Library Creator B\nLibrary Creator A',
      'library creator order must not be rewritten by a no-edit save');
    assert.deepStrictEqual(libraryRoundTrip.allowedPatronCodeIds, ['47']);
    assert.strictEqual(libraryRoundTrip.providers.length, 3);
    assert.strictEqual(libraryRoundTrip.providers.find(provider => provider.key === 'external_search_2').label,
      'Library Research Index');
    assert.strictEqual(Object.keys(libraryRoundTrip).some(key => key.startsWith('externalSearch4')), false);
    assert.deepStrictEqual(libraryRoundTrip.formats.map(format => [format.code, format.sortOrder, format.isEnabled]),
      [['book', 23, true], ['zine', 57, true]]);
    assert.deepStrictEqual(libraryRoundTrip.ui_text.publicationOptions,
      [{ id: 'local', label: 'Local publication', enabled: true, sortOrder: 19 }]);
    assert.strictEqual(libraryRoundTrip.formats.find(format => format.code === 'book').identifier.mode, 'hidden');
    assert.strictEqual(libraryRoundTrip.formats.find(format => format.code === 'zine').message, 'Bring local zines to the desk.');
    assert.strictEqual(libraryRoundTrip.customFields[0].id, '401');
    assert.strictEqual(libraryRoundTrip.customFields[0].key, 'audience_note');
    assert.strictEqual(libraryRoundTrip.customFields[0].helpText, 'Choose the intended audience.');
    assert.deepStrictEqual(libraryRoundTrip.customFields[0].options.map(option => [option.id, option.enabled, option.sortOrder]),
      [['general', true, 13], ['specialist', false, 31]]);
    assert.deepStrictEqual(libraryRoundTrip.formatClaimRules, [{
      materialFormatId: '301', staffUserId: '9007199254740993', active: true
    }]);

    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'test-antiforgery-token',
      staff: { role: 'admin', userPrincipalName: 'library-admin@example.org', organizationId: '2', organizationName: 'Central' }
    });
    ordered.length = 0;
    saveCompleted = false;
    assert.strictEqual(await saveController.saveSettings({ clearDelay: 0 }), true);
    const libraryPost = sentSettingsPayloads[5];
    assert.strictEqual(libraryPost.orgId, '2');
    assert.strictEqual(Object.keys(libraryPost.workflow).some(key => key.startsWith('externalSearch')), false);
    assert.strictEqual(libraryPost.providers.length, 3);
    assert.strictEqual(libraryPost.formats.find(format => format.code === 'zine').ownerOrganizationId, '2');
    assert.strictEqual(libraryPost.customFields[0].key, 'audience_note');
    assert.strictEqual(libraryPost.formatClaimRules[0].staffUserId, '9007199254740993');

    formPopulation.applyLibrarySettingsToForm({ ...librarySettingsResponse, autoClaimStaff: undefined });
    await settleAsyncRendering();
    const missingStaffChoices = serializer.buildSettingsPayload();
    assert.strictEqual(Object.hasOwn(missingStaffChoices, 'formatClaimRules'), false,
      'unavailable auto-claim staff choices must not clear the stored assignment set');
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
