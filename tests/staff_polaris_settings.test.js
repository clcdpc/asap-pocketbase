const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

function assertBackendShape(expected, actual, path = '$') {
  if (Array.isArray(expected)) {
    assert.ok(Array.isArray(actual), `${path} must be an array`);
    if (expected.length > 0 && actual.length > 0 && expected[0] && typeof expected[0] === 'object') {
      assertBackendShape(expected[0], actual[0], `${path}[0]`);
    }
    return;
  }
  if (expected && typeof expected === 'object') {
    assert.ok(actual && typeof actual === 'object' && !Array.isArray(actual), `${path} must be an object`);
    assert.deepStrictEqual(Object.keys(actual).sort(), Object.keys(expected).sort(),
      `${path} must contain exactly the current .NET Settings DTO properties`);
    for (const [key, value] of Object.entries(expected)) {
      assert.ok(Object.hasOwn(actual, key), `${path}.${key} must be returned by the current .NET Settings DTO`);
      assertBackendShape(value, actual[key], `${path}.${key}`);
    }
    return;
  }
  if (expected !== null && actual !== null) {
    assert.strictEqual(typeof actual, typeof expected, `${path} must retain the current .NET Settings DTO value kind`);
  }
}

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
    const formatRules = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'format-rules.js')).href);
    const formPopulation = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'form-population.js')).href);
    const settingsTemplates = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings-templates.js')).href);
    const serializer = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'serialize-save.js')).href);
    const saveController = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'save-controller.js')).href);
    const settingsRefresh = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'refresh.js')).href);
    const settingsLoader = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'loader.js')).href);
    const patronCodes = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings', 'patron-codes.js')).href);
    const http = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'http.js')).href);
    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'test-antiforgery-token',
      staff: { role: 'super_admin', userPrincipalName: 'admin@example.org' }
    });
    const explicitEmptyEcontentMessages = formatRules.normalizePatronFormatRules({
      ebook: { messageBehavior: 'ebookMessage', message: '' },
      eaudiobook: { messageBehavior: 'eaudiobookMessage', message: null }
    });
    assert.strictEqual(explicitEmptyEcontentMessages.ebook.message, '',
      'an explicit empty backend message must not be replaced by a shipped eBook default');
    assert.strictEqual(explicitEmptyEcontentMessages.eaudiobook.message, '',
      'an explicit null backend message must not be replaced by a shipped eAudiobook default');
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
      outstandingTimeoutRejectionTemplateId: '906',
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
    const canonicalLibraryResponse = JSON.parse(fs.readFileSync(
      path.join(repositoryRoot, 'tests', 'fixtures', 'settings', 'canonical-library-settings-response.json'), 'utf8'));
    const canonicalSystemResponse = JSON.parse(fs.readFileSync(
      path.join(repositoryRoot, 'tests', 'fixtures', 'settings', 'canonical-system-settings-response.json'), 'utf8'));
    const systemTemplateRows = canonicalLibraryResponse.stored.configuredSystem.templates;
    const systemPatron = canonicalLibraryResponse.stored.configuredSystem.patron;
    const systemEmail = { fromAddress: 'suggestions@example.org', fromName: 'System Suggestions', hasPostmarkToken: true, version: 'AQIDBAUGBg0=' };
    const systemBranding = canonicalLibraryResponse.stored.configuredSystem.branding;
    const systemPublicationOptions = [
      { id: 'forthcoming', label: 'Forthcoming', enabled: true, sortOrder: 17 },
      { id: 'backlist', label: 'Backlist', enabled: false, sortOrder: 41 }
    ];
    const systemPublicationOptionKeys = systemPublicationOptions.map(option => option.id);
    const systemEffectiveFormats = persistedFormats.map(format => ({
      id: format.id, code: format.code, label: format.label, sortOrder: format.sortOrder,
      isEnabled: format.isEnabled, messageBehavior: format.messageBehavior, message: format.message,
      title: { mode: format.titleMode, label: format.titleLabel },
      author: { mode: format.authorMode, label: format.authorLabel },
      identifier: { mode: format.identifierMode, label: format.identifierLabel },
      publication: { mode: format.publicationMode, label: format.publicationLabel },
      customFields: {}
    }));
    const systemSettingsResponse = {
      orgId: 'system',
      organization: { id: 1, name: 'System Defaults', abbreviation: 'SYS', active: true, version: 'AQIDBAUGBgE=' },
      isOverride: false,
      hasOverrides: false,
      version: 'system-settings-version',
      stored: {
        systemSettings: { ...currentSystemSettings,
          systemNotEnabledMessage: 'Service not enabled for this system.',
          misconfiguredMessage: 'System configuration needs attention.', version: 'AQIDBAUGBgI=' },
        polaris: { ...persisted, version: 'AQIDBAUGBgM=' },
        configuredSystem: {
          workflow: { ...persistedWorkflow, version: 'AQIDBAUGBgQ=' },
          patron: systemPatron,
          email: systemEmail,
          publicationOptions: { exists: true, values: systemPublicationOptions },
          commonCreators: { exists: true, values: persistedCommonCreators.map((value, index) => ({ value, sortOrder: (index + 1) * 10 })) },
          allowedPatronCodeIds: { exists: true, values: persistedAllowedPatronCodeIds },
          providers: persistedProviders.map(({ sortOrder, ...provider }) => ({ kind: 'system', ...provider, version: 'AQIDBAUGBgU=' })),
          formats: persistedFormats.map(format => ({ kind: 'system', materialFormatId: null, ...format, version: 'AQIDBAUGBgY=' })),
          templates: systemTemplateRows,
          branding: systemBranding
        },
        libraryOverride: null,
        workflow: { ...persistedWorkflow, version: 'AQIDBAUGBgQ=' },
        patron: systemPatron,
        email: systemEmail,
        origins: currentSystemSettings.patronEmbedAllowedOrigins,
        publicationOptions: systemPublicationOptions.map(option => ({ organizationId: 1, ...option })),
        commonCreators: persistedCommonCreators,
        allowedPatronCodeIds: persistedAllowedPatronCodeIds,
        providers: persistedProviders.map(provider => ({
          key: provider.key, id: provider.id, isEnabled: provider.isEnabled, label: provider.label,
          urlTemplate: provider.urlTemplate,
          system: { isEnabled: provider.isEnabled, label: provider.label, urlTemplate: provider.urlTemplate },
          overridden: false
        })),
        formats: persistedFormats.map(format => ({ ...format, overridden: false, version: 'AQIDBAUGBgc=' })),
        customFields: [],
        templates: systemTemplateRows,
        autoClaimRules: [],
        branding: systemBranding
      },
      emails: { fromAddress: systemEmail.fromAddress, fromName: systemEmail.fromName,
        hasPostmarkToken: systemEmail.hasPostmarkToken, templates: systemTemplateRows,
        submissionTemplate: {
          templateKey: 'suggestion_submitted',
          subjectTemplate: systemTemplateRows.find(template => template.templateKey === 'suggestion_submitted').subject,
          bodyTemplate: systemTemplateRows.find(template => template.templateKey === 'suggestion_submitted').body
        } },
      ui_text: {
        ...canonicalSystemResponse.ui_text,
        pageTitle: 'System patron portal custom title',
        barcodeLabel: systemPatron.barcodeLabel,
        pinLabel: systemPatron.pinLabel,
        loginPrompt: systemPatron.loginPrompt,
        loginNote: systemPatron.loginNote,
        suggestionFormNote: systemPatron.suggestionFormNote,
        noEmailMessage: systemPatron.noEmailMessage,
        successTitle: systemPatron.successTitle,
        successMessage: systemPatron.successMessage,
        alreadySubmittedMessage: systemPatron.alreadySubmittedMessage,
        duplicateStatusLabels: {
          ...canonicalSystemResponse.ui_text.duplicateStatusLabels,
          suggestion: 'Received at the system desk',
          'Silently Closed': 'System staff reviewed this request'
        },
        ebookMessage: systemPatron.ebookMessage,
        eaudiobookMessage: systemPatron.eaudiobookMessage,
        systemNotEnabledMessage: 'Service not enabled for this system.',
        misconfiguredMessage: 'System configuration needs attention.',
        publicationOptions: systemPublicationOptionKeys
      },
      effective: {
        ...canonicalLibraryResponse.effective,
        organizationId: 1,
        organizationName: 'System Defaults',
        isActive: true,
        duplicateStatusLabels: canonicalSystemResponse.effective.duplicateStatusLabels,
        workflow: persistedWorkflow,
        commonCreators: persistedCommonCreators,
        allowedPatronCodeIds: persistedAllowedPatronCodeIds,
        externalSearchProviders: persistedProviders,
        publicationOptions: systemPublicationOptionKeys,
        formats: systemEffectiveFormats,
        customFields: [],
        email: { fromAddress: systemEmail.fromAddress, fromName: systemEmail.fromName, hasServerToken: true },
        submissionTemplate: { templateKey: 'suggestion_submitted', subjectTemplate: systemTemplateRows[0].subject,
          bodyTemplate: systemTemplateRows[0].body },
        hasLogo: systemBranding.hasLogo,
        logoAltText: systemBranding.altText
      },
      autoClaimStaff: [],
      workflow: { ...compatibilityWorkflow, system: true },
      formatClaimRules: [],
      originsForEditor: currentSystemSettings.patronEmbedAllowedOrigins
    };
    assertBackendShape(canonicalSystemResponse, systemSettingsResponse, '$systemSettings');
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
    assert.strictEqual(document.getElementById('outstanding-timeout-rejection-template-id').value, '906');
    assert.strictEqual(document.getElementById('email-submit-subject').value, systemTemplateRows[0].subject,
      'the system form must display the customized current .NET standard-template DTO value');
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
    assert.strictEqual(document.querySelectorAll('#format-settings-container .btn-remove-format').length, 0,
      'system-owned formats must not expose the destructive custom-format removal control');

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
    assert.strictEqual(Object.hasOwn(workflowPayload.emails, 'suggestion_submitted'), false,
      'an unchanged customized backend template must be omitted from a system no-edit save');
    for (const field of Object.keys(persistedWorkflow)) {
      assert.strictEqual(Object.hasOwn(workflowPayload, field), false,
        `unchanged ${field} must be omitted from a system no-edit save`);
    }
    assert.strictEqual(Object.hasOwn(workflowPayload, 'commonAuthorsList'), false,
      'an unchanged system whole-set must not be submitted');
    assert.strictEqual(Object.hasOwn(workflowPayload, 'allowedPatronCodeIds'), false,
      'an unchanged system patron-code set must not be submitted');
    assert.strictEqual(workflowPayload.formatIconUrlPattern, currentSystemSettings.formatIconUrlPattern);
    assert.deepStrictEqual(workflowPayload.enabledLibraryOrgIds, ['2', '4']);
    assert.deepStrictEqual(workflowPayload.providers, persistedProviders.map(provider => ({ ...provider, overridden: false })));
    assert.strictEqual(Object.keys(workflowPayload).some(key => key.startsWith('externalSearch4')), false);
    assert.deepStrictEqual(workflowPayload.formats.map(format => ({
      id: format.id, code: format.code, label: format.label, sortOrder: format.sortOrder, isEnabled: format.isEnabled
    })), persistedFormats.map(format => ({
      id: format.id, code: format.code, label: format.label, sortOrder: format.sortOrder, isEnabled: format.isEnabled
    })));
    assert.strictEqual(Object.hasOwn(workflowPayload.ui_text, 'publicationOptions'), false,
      'an unchanged system publication-option set must not be submitted');

    const systemSubmitSubject = document.getElementById('email-submit-subject');
    const loadedSystemSubmitSubject = systemSubmitSubject.value;
    systemSubmitSubject.value = 'System approved this request: {{title}}';
    systemSubmitSubject.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    const systemTemplateEditPayload = serializer.buildSettingsPayload().emails;
    assert.deepStrictEqual(Object.keys(systemTemplateEditPayload).filter(key =>
      key !== 'postmarkToken' && key !== 'clearPostmarkToken'), ['suggestion_submitted'],
      'an intentional system template edit must submit only the changed template');
    assert.strictEqual(systemTemplateEditPayload.postmarkToken, '');
    assert.strictEqual(systemTemplateEditPayload.clearPostmarkToken, false);
    assert.deepStrictEqual(systemTemplateEditPayload.suggestion_submitted, {
      templateKey: 'suggestion_submitted', subject: 'System approved this request: {{title}}', enabled: true
    }, 'an unchanged body must remain omitted from the sparse system template edit');
    systemSubmitSubject.value = loadedSystemSubmitSubject;
    systemSubmitSubject.dispatchEvent(new dom.window.Event('input', { bubbles: true }));

    const ordered = [];
    const sentSettingsPayloads = [];
    let saveCompleted = false;
    let nextSettingsSaveIsStale = false;
    let deferNextSettingsSave = false;
    let releaseDeferredSettingsSave = null;
    let settingsSaveAttempts = 0;
    let librarySettingsLoadCount = 0;
    let failNextSettingsRead = false;
    const customFormatDeleteRequests = [];
    let deferredFormatDeleteId = null;
    let releaseDeferredFormatDelete = null;
    global.fetch = async (request, options = {}) => {
      const url = String(request);
      const method = String(options.method || 'GET').toUpperCase();
      if (url === '/api/asap/staff/settings/library' && method === 'POST') {
        settingsSaveAttempts++;
        if (nextSettingsSaveIsStale) {
          nextSettingsSaveIsStale = false;
          return { ok: false, status: 409, statusText: 'Conflict', json: async () => ({
            code: 'stale_version', message: 'The library settings are stale.'
          }) };
        }
        const body = JSON.parse(options.body);
        sentSettingsPayloads.push(body);
        if (deferNextSettingsSave) {
          deferNextSettingsSave = false;
          return new Promise(resolve => {
            releaseDeferredSettingsSave = () => resolve({
              ok: true, status: 200, statusText: 'OK', json: async () => ({ code: 'saved' })
            });
          });
        }
        systemSettingsResponse.stored.workflow = { ...systemSettingsResponse.stored.workflow, ...body.workflow };
        systemSettingsResponse.effective.workflow = { ...systemSettingsResponse.effective.workflow, ...body.workflow };
        if (Object.hasOwn(body.workflow, 'commonAuthorsList')) {
          const values = body.workflow.commonAuthorsList.split('\n').filter(Boolean);
          systemSettingsResponse.stored.commonCreators = values;
          systemSettingsResponse.stored.configuredSystem.commonCreators = {
            exists: true,
            values: values.map((value, index) => ({ value, sortOrder: (index + 1) * 10 }))
          };
          systemSettingsResponse.effective.commonCreators = values;
        }
        if (Object.hasOwn(body.workflow, 'allowedPatronCodeIds')) {
          systemSettingsResponse.stored.allowedPatronCodeIds = [...body.workflow.allowedPatronCodeIds];
          systemSettingsResponse.stored.configuredSystem.allowedPatronCodeIds = {
            exists: true, values: [...body.workflow.allowedPatronCodeIds]
          };
          systemSettingsResponse.effective.allowedPatronCodeIds = [...body.workflow.allowedPatronCodeIds];
        }
        if (Object.hasOwn(body.ui_text, 'publicationOptions')) {
          systemSettingsResponse.ui_text.publicationOptions = body.ui_text.publicationOptions;
          systemSettingsResponse.effective.publicationOptions = body.ui_text.publicationOptions;
        }
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
        if (failNextSettingsRead) {
          failNextSettingsRead = false;
          return { ok: false, status: 503, statusText: 'Service Unavailable', json: async () => ({
            code: 'settings_unavailable', message: 'Settings temporarily unavailable.'
          }) };
        }
        if (state.currentLibraryContextOrgId === '2') librarySettingsLoadCount++;
        const currentResponse = state.currentLibraryContextOrgId === '2'
          ? librarySettingsResponse
          : systemSettingsResponse;
        return { ok: true, status: 200, statusText: 'OK', json: async () => currentResponse };
      }
      if (url.startsWith('/api/asap/staff/settings/formats/') && method === 'DELETE') {
        const parsed = new URL(url, 'http://localhost');
        const formatId = decodeURIComponent(parsed.pathname.split('/').at(-1));
        const version = parsed.searchParams.get('version');
        customFormatDeleteRequests.push({ formatId, version });
        if (formatId === deferredFormatDeleteId) {
          deferredFormatDeleteId = null;
          return new Promise(resolve => {
            releaseDeferredFormatDelete = () => resolve({
              ok: true, status: 200, statusText: 'OK', json: async () => ({ code: 'format_deleted' })
            });
          });
        }
        if (formatId === '9007199254740994') {
          return { ok: false, status: 409, statusText: 'Conflict', json: async () => ({
            code: 'format_referenced', message: 'This custom format is still referenced by workflow data.'
          }) };
        }
        return { ok: true, status: 200, statusText: 'OK', json: async () => ({ code: 'format_deleted' }) };
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
    for (const field of Object.keys(persistedWorkflow)) {
      assert.strictEqual(Object.hasOwn(saveAndTestPayload.workflow, field), false,
        `Save & test must omit unchanged workflow.${field}`);
    }
    assert.strictEqual(Object.hasOwn(saveAndTestPayload.workflow, 'commonAuthorsList'), false,
      'Polaris-only Save & test must not replace an unchanged creator set');
    assert.strictEqual(Object.hasOwn(saveAndTestPayload.workflow, 'allowedPatronCodeIds'), false,
      'Polaris-only Save & test must not replace an unchanged patron-code set');
    assert.strictEqual(Object.hasOwn(saveAndTestPayload.ui_text, 'publicationOptions'), false,
      'Polaris-only Save & test must not replace unchanged publication options');
    assert.strictEqual(saveAndTestPayload.formatIconUrlPattern, currentSystemSettings.formatIconUrlPattern);
    assert.deepStrictEqual(saveAndTestPayload.enabledLibraryOrgIds, ['2', '4']);
    assert.strictEqual(Object.hasOwn(saveAndTestPayload.workflow, 'enabledLibraryOrgIds'), false);
    assert.deepStrictEqual(saveAndTestPayload.providers,
      persistedProviders.map(provider => ({ ...provider, overridden: false })));
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
    for (const field of Object.keys(persistedWorkflow)) {
      assert.strictEqual(Object.hasOwn(ordinarySavePayload.workflow, field), false,
        `ordinary no-edit save must omit unchanged workflow.${field}`);
    }
    assert.strictEqual(Object.hasOwn(ordinarySavePayload.workflow, 'commonAuthorsList'), false,
      'an ordinary no-edit save must not replace the system creator set');
    assert.strictEqual(Object.hasOwn(ordinarySavePayload.workflow, 'allowedPatronCodeIds'), false,
      'an ordinary no-edit save must not replace the system patron-code set');
    assert.strictEqual(Object.hasOwn(ordinarySavePayload.ui_text, 'publicationOptions'), false,
      'an ordinary no-edit save must not replace the system publication-option set');
    assert.strictEqual(ordinarySavePayload.formatIconUrlPattern, currentSystemSettings.formatIconUrlPattern);
    assert.deepStrictEqual(ordinarySavePayload.enabledLibraryOrgIds, ['2', '4']);

    await settleAsyncRendering();
    document.getElementById('pending-hold-timeout-days').value = '46';
    const intentionalEdit = serializer.buildSettingsPayload();
    assert.strictEqual(intentionalEdit.pendingHoldTimeoutDays, 46,
      'an intentional workflow edit must override the loaded stored value');
    assert.strictEqual(Object.hasOwn(intentionalEdit, 'outstandingTimeoutDays'), false,
      'an unchanged workflow scalar must not be submitted alongside an intentional edit');
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
    assert.strictEqual(Object.hasOwn(partialLoadPayload, 'allowedPatronCodeIds'), false,
      'an unavailable patron-code list must omit the whole-set replacement');
    ordered.length = 0;
    saveCompleted = false;
    assert.strictEqual(await saveController.saveSettings({ clearDelay: 0 }), true);
    const partialLoadPost = sentSettingsPayloads[4];
    assert.strictEqual(Object.hasOwn(partialLoadPost, 'enabledLibraryOrgIds'), false);
    assert.strictEqual(Object.hasOwn(partialLoadPost.workflow, 'enabledLibraryOrgIds'), false);
    assert.strictEqual(Object.hasOwn(partialLoadPost.workflow, 'allowedPatronCodeIds'), false);
    assert.deepStrictEqual(organizations.filter(item => item.id !== 1 && item.active).map(item => item.id), [2, 3]);
    state.setOrganizationsStatus('loaded');
    patronCodes.updatePatronCodesStatusUi('loaded', 'Patron codes loaded.');

    state.setCurrentLibraryContextOrgId('2');
    const librarySettingsResponse = JSON.parse(fs.readFileSync(
      path.join(repositoryRoot, 'tests', 'fixtures', 'settings', 'canonical-library-settings-response.json'), 'utf8'));
    async function refreshLibrarySettingsForm() {
      const response = state.currentLibraryContextOrgId === '2' ? librarySettingsResponse : systemSettingsResponse;
      if (state.currentLibraryContextOrgId === '2') librarySettingsLoadCount++;
      formPopulation.applyLibrarySettingsToForm(response);
      serializer.rememberLastSavedLibrarySettings(response);
    }
    settingsRefresh.registerSettingsRefreshHandlers({
      refreshSettingsView: refreshLibrarySettingsForm,
      loadStaffConfig: async () => {}
    });
    formPopulation.applyLibrarySettingsToForm(librarySettingsResponse);
    serializer.rememberLastSavedLibrarySettings(librarySettingsResponse);
    await settleAsyncRendering();
    assert.strictEqual(document.getElementById('outstanding-timeout-days').value, '72',
      'library context must continue to populate effective/inherited workflow values');
    assert.strictEqual(document.getElementById('pending-hold-timeout-days').value, '19');
    assert.strictEqual(document.getElementById('polaris-auto-promote').checked, false);
    assert.strictEqual(document.getElementById('outstanding-timeout-rejection-template-id').value, '906',
      'the workflow-selected rejection template ID must remain selected as a string');
    assert.strictEqual(document.getElementById('email-submit-subject').value, 'Harbor request received: {{title}}');
    assert.strictEqual(document.getElementById('email-purchase-approved-subject').value, 'Harbor will order {{title}}');
    assert.strictEqual(document.getElementById('email-owned-subject').value, 'Harbor already has {{title}}');
    assert.strictEqual(document.getElementById('email-rejected-subject').value, 'Harbor cannot order {{title}}');
    assert.strictEqual(document.getElementById('email-hold-subject').value, 'A Harbor hold is ready');
    assert.strictEqual(document.getElementById('postmark-token').value, '', 'Postmark tokens remain write-only');
    assert.strictEqual(document.getElementById('postmark-token-status').classList.contains('hidden'), false,
      'the non-secret Postmark configured indicator must remain visible');
    assert.strictEqual(document.getElementById('wf-common-authors-list').value, 'N. K. Jemisin\nOctavia E. Butler');
    assert.strictEqual(document.getElementById('wf-external-search-2-label').value, 'Harbor Research Index');
    assert.strictEqual(document.querySelector('.format-setting-row[data-key="zine"] .format-label-input').value, 'Community zine');
    assert.strictEqual(document.querySelector('.format-setting-row[data-key="zine"] .format-claim-staff-select').value,
      '9007199254740993');
    assert.strictEqual(document.querySelector('.additional-field-row').getAttribute('data-field-key'), 'audience_note');
    let customFieldLabel = document.querySelector('.format-rule-custom-field-label[data-format="zine"][data-field="audience_note"]');
    assert.strictEqual(customFieldLabel.value, 'Zine audience',
      'the backend label override must populate the actual format-rule input');
    assert.ok(document.querySelector('.format-setting-row[data-key="zine"] .btn-remove-format'),
      'a library-owned custom format must expose the explicit remove action');

    const libraryRoundTrip = serializer.buildSettingsPayload();
    assert.strictEqual(Object.hasOwn(libraryRoundTrip, 'commonAuthorsList'), false,
      'inherited common creators must stay absent on an unrelated library save');
    assert.strictEqual(Object.hasOwn(libraryRoundTrip, 'allowedPatronCodeIds'), false,
      'an absent local patron-code set must stay absent on an unrelated library save');
    assert.strictEqual(libraryRoundTrip.providers.length, 3);
    assert.strictEqual(libraryRoundTrip.providers.find(provider => provider.key === 'external_search_2').label,
      'Harbor Research Index');
    assert.strictEqual(Object.keys(libraryRoundTrip).some(key => key.startsWith('externalSearch4')), false);
    assert.deepStrictEqual(libraryRoundTrip.formats.map(format => [format.code, format.sortOrder, format.isEnabled]),
      [['book', 11, true], ['dvd', 28, false], ['zine', 57, true]]);
    assert.strictEqual(Object.hasOwn(libraryRoundTrip.ui_text, 'publicationOptions'), false,
      'the absent local publication set must not be materialized by an unrelated save');
    assert.strictEqual(libraryRoundTrip.formats.find(format => format.code === 'book').identifier.mode, 'hidden');
    assert.strictEqual(libraryRoundTrip.formats.find(format => format.code === 'zine').message,
      'Bring a local zine to the Harbor desk.');
    assert.strictEqual(libraryRoundTrip.customFields[0].id, '9007199254741009');
    assert.strictEqual(libraryRoundTrip.customFields[0].key, 'audience_note');
    assert.strictEqual(libraryRoundTrip.customFields[0].helpText, 'Choose the intended audience.');
    assert.deepStrictEqual(libraryRoundTrip.customFields[0].options.map(option => [option.id, option.enabled, option.sortOrder]),
      [['general', true, 13], ['specialist', false, 31]]);
    assert.deepStrictEqual(libraryRoundTrip.formatClaimRules, [{
      materialFormatId: '9007199254741003', staffUserId: '9007199254740993', active: true
    }]);
    assert.strictEqual(libraryRoundTrip.formats.find(format => format.code === 'zine')
      .customFields.audience_note.labelOverride, 'Zine audience',
    'a no-edit save must retain the backend labelOverride through the actual DOM serializer');
    assert.strictEqual(libraryRoundTrip.formatClaimRules.length, 1,
      'inactive historical assignments must not be presented as current assignments');
    assert.strictEqual(Object.hasOwn(libraryRoundTrip.emails, 'suggestion_submitted'), false,
      'an unchanged sparse standard-template override must remain untouched');
    assert.strictEqual(Object.hasOwn(libraryRoundTrip.emails, 'purchase_approved'), false,
      'an inherited standard template with no override must remain absent');
    assert.strictEqual(Object.hasOwn(libraryRoundTrip.emails, 'rejection_templates'), false,
      'an unrelated save must not rewrite or recreate existing rejection templates');
    const libraryCustomName = [...document.querySelectorAll('#rejection-templates-accordion-container input[data-field="name"]')]
      .find(input => input.value === 'Local budget review');
    assert.ok(libraryCustomName, 'the current backend custom-template display name must populate the actual editor');
    libraryCustomName.value = 'Harbor local budget decision';
    libraryCustomName.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    const renamedCustom = serializer.buildSettingsPayload().emails.rejection_templates.find(template =>
      template.templateKey === 'rejection:local-budget');
    assert.strictEqual(renamedCustom.displayName, 'Harbor local budget decision',
      'an intentional custom-template name edit must use the current DTO display name');
    libraryCustomName.value = 'Local budget review';
    libraryCustomName.dispatchEvent(new dom.window.Event('input', { bubbles: true }));

    document.getElementById('btn-add-rejection-template').click();
    await settleAsyncRendering();
    const createdTemplate = serializer.buildSettingsPayload().emails.rejection_templates.find(template =>
      template.isCustom === true && template.templateKey.startsWith('rejection:custom_'));
    assert.ok(createdTemplate, 'an explicitly added library rejection template must serialize for creation');
    state.setCurrentRejectionTemplates(state.currentRejectionTemplates.filter(template => template.isNew !== true));
    state.setDeletedSettingsTemplates([]);
    formPopulation.applyLibrarySettingsToForm(librarySettingsResponse);
    serializer.rememberLastSavedLibrarySettings(librarySettingsResponse);
    await settleAsyncRendering();
    customFieldLabel = document.querySelector('.format-rule-custom-field-label[data-format="zine"][data-field="audience_note"]');

    customFieldLabel.value = 'Community audience';
    customFieldLabel.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    const changedFormatRule = serializer.buildSettingsPayload().formats.find(format => format.code === 'zine');
    assert.strictEqual(changedFormatRule.customFields.audience_note.labelOverride, 'Community audience',
      'an intentional custom-field label edit must be serialized');
    customFieldLabel.value = 'Zine audience';
    customFieldLabel.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    const submitSubject = document.getElementById('email-submit-subject');
    submitSubject.value = 'Harbor approved this request: {{title}}';
    submitSubject.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    const editedTemplate = serializer.buildSettingsPayload().emails.suggestion_submitted;
    assert.strictEqual(editedTemplate.sourceTemplateId, '9007199254740999');
    assert.strictEqual(editedTemplate.subject, 'Harbor approved this request: {{title}}');
    submitSubject.value = 'Harbor request received: {{title}}';
    submitSubject.dispatchEvent(new dom.window.Event('input', { bubbles: true }));

    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'test-antiforgery-token',
      staff: { role: 'admin', userPrincipalName: 'library-admin@example.org', organizationId: '2', organizationName: 'Harbor City Library' }
    });
    ordered.length = 0;
    saveCompleted = false;
    assert.strictEqual(await saveController.saveSettings({ clearDelay: 0 }), true);
    const libraryPost = sentSettingsPayloads[5];
    assert.strictEqual(libraryPost.orgId, '2');
    assert.strictEqual(Object.keys(libraryPost.workflow).some(key => key.startsWith('externalSearch')), false);
    assert.strictEqual(libraryPost.providers.length, 3);
    assert.strictEqual(libraryPost.formats.find(format => format.code === 'zine').ownerOrganizationId, '2');
    assert.strictEqual(libraryPost.formats.find(format => format.code === 'zine')
      .customFields.audience_note.labelOverride, 'Zine audience',
    'the actual Settings POST must preserve the format custom-field label override');
    assert.strictEqual(libraryPost.customFields[0].key, 'audience_note');
    assert.strictEqual(libraryPost.formatClaimRules[0].staffUserId, '9007199254740993');
    assert.strictEqual(Object.hasOwn(libraryPost.emails, 'suggestion_submitted'), false,
      'the actual no-edit Settings POST must leave the sparse template override untouched');
    assert.strictEqual(Object.hasOwn(libraryPost.emails, 'rejection_templates'), false);

    state.setDeletedSettingsFormats([
      { id: '9007199254740993', version: 'rowversion-A' },
      { id: '9007199254740994', version: 'rowversion-B' }
    ]);
    assert.strictEqual(await saveController.saveSettings({ clearDelay: 0 }), false,
      'a secondary format deletion failure must report partial success');
    assert.deepStrictEqual(customFormatDeleteRequests.map(item => [item.formatId, item.version]), [
      ['9007199254740993', 'rowversion-A'],
      ['9007199254740994', 'rowversion-B']
    ], 'custom-format deletions must preserve bigint IDs as strings and submit each rowversion');
    assert.match(document.getElementById('settings-msg').textContent,
      /settings were saved, but custom format removal did not complete/i,
      'a failed secondary deletion must not be described as a failed primary Settings save');
    assert.strictEqual(document.getElementById('settings-msg').classList.contains('text-warning'), true);

    const refreshesBeforeMainSaveScopeSwitch = librarySettingsLoadCount;
    const deleteAttemptsBeforeMainSaveScopeSwitch = customFormatDeleteRequests.length;
    state.setDeletedSettingsFormats([{ id: '9007199254740993', version: 'rowversion-old-scope' }]);
    deferNextSettingsSave = true;
    const pendingOldScopeSave = saveController.saveSettings({ clearDelay: 0 });
    assert.strictEqual(typeof releaseDeferredSettingsSave, 'function', 'the old-scope primary Settings save must be pending');
    state.setCurrentLibraryContextOrgId('3');
    state.incrementLibraryContextLoadSerial();
    document.getElementById('ui-login-note').value = 'Library three current draft';
    releaseDeferredSettingsSave();
    assert.strictEqual(await pendingOldScopeSave, false,
      'a save response for a superseded library context must be ignored');
    assert.strictEqual(customFormatDeleteRequests.length, deleteAttemptsBeforeMainSaveScopeSwitch,
      'a superseded primary save must not start queued custom-format deletes');
    assert.strictEqual(librarySettingsLoadCount, refreshesBeforeMainSaveScopeSwitch,
      'a superseded primary save must not refresh over the newly selected library');
    assert.strictEqual(document.getElementById('ui-login-note').value, 'Library three current draft',
      'a superseded primary save must not overwrite the current library form');

    state.setCurrentLibraryContextOrgId('2');
    state.incrementLibraryContextLoadSerial();
    formPopulation.applyLibrarySettingsToForm(librarySettingsResponse);
    serializer.rememberLastSavedLibrarySettings(librarySettingsResponse);
    await settleAsyncRendering();

    const deleteAttemptsBeforeSecondaryScopeSwitch = customFormatDeleteRequests.length;
    const refreshesBeforeSecondaryScopeSwitch = librarySettingsLoadCount;
    state.setDeletedSettingsFormats([
      { id: '9007199254740993', version: 'rowversion-A' },
      { id: '9007199254740994', version: 'rowversion-B' }
    ]);
    deferredFormatDeleteId = '9007199254740993';
    const pendingOldScopeDelete = saveController.saveSettings({ clearDelay: 0 });
    await settleAsyncRendering();
    assert.strictEqual(typeof releaseDeferredFormatDelete, 'function', 'the first custom-format delete must be pending');
    state.setCurrentLibraryContextOrgId('3');
    state.incrementLibraryContextLoadSerial();
    document.getElementById('ui-login-note').value = 'Library three stays visible';
    releaseDeferredFormatDelete();
    assert.strictEqual(await pendingOldScopeDelete, false,
      'a custom-format delete sequence must stop after its library context is superseded');
    assert.deepStrictEqual(customFormatDeleteRequests.slice(deleteAttemptsBeforeSecondaryScopeSwitch).map(item => item.formatId),
      ['9007199254740993'], 'queued deletes must not continue in the old context after a scope change');
    assert.deepStrictEqual(state.deletedSettingsFormats.map(item => item.id), ['9007199254740994'],
      'the successful delete leaves only the unattempted format queued for explicit review');
    assert.strictEqual(librarySettingsLoadCount, refreshesBeforeSecondaryScopeSwitch,
      'the old save must not reload settings after a scope change during a direct delete');
    assert.strictEqual(document.getElementById('ui-login-note').value, 'Library three stays visible');
    state.setDeletedSettingsFormats([]);

    state.setDeletedSettingsFormats([]);
    state.setCurrentLibraryContextOrgId('2');
    state.incrementLibraryContextLoadSerial();
    formPopulation.applyLibrarySettingsToForm(librarySettingsResponse);
    serializer.rememberLastSavedLibrarySettings(librarySettingsResponse);
    await settleAsyncRendering();
    failNextSettingsRead = true;
    settingsRefresh.registerSettingsRefreshHandlers({
      refreshSettingsView: settingsLoader.loadSettings,
      loadStaffConfig: async () => {}
    });
    assert.strictEqual(await saveController.saveSettings({ clearDelay: 0 }), true,
      'a successful settings save remains successful when its follow-up read fails');
    assert.strictEqual(failNextSettingsRead, false, 'the refresh failure must come from the settings read');
    assert.match(document.getElementById('settings-msg').textContent,
      /settings were saved, but the current values could not be reloaded/i,
      'a failed follow-up read must be reported as a successful save with an incomplete refresh');
    assert.strictEqual(document.getElementById('settings-msg').classList.contains('text-warning'), true,
      'the refresh warning must not be presented as a failed settings save');
    settingsRefresh.registerSettingsRefreshHandlers({
      refreshSettingsView: refreshLibrarySettingsForm,
      loadStaffConfig: async () => {}
    });

    const refreshesBeforeConflict = librarySettingsLoadCount;
    const attemptsBeforeConflict = settingsSaveAttempts;
    librarySettingsResponse.version = 'library-version-2';
    librarySettingsResponse.effective.loginNote = 'Authoritative value after another session changed settings';
    librarySettingsResponse.ui_text.loginNote = 'Authoritative value after another session changed settings';
    nextSettingsSaveIsStale = true;
    assert.strictEqual(await saveController.saveSettings({ clearDelay: 0 }), false,
      'a stale Settings version must not be automatically retried');
    assert.strictEqual(settingsSaveAttempts, attemptsBeforeConflict + 1,
      'stale Settings handling must send only the original mutation');
    assert.strictEqual(librarySettingsLoadCount, refreshesBeforeConflict + 1,
      'a stale Settings result must reload the current library response');
    assert.strictEqual(document.getElementById('ui-login-note').value,
      'Authoritative value after another session changed settings',
      'the stale conflict must replace the form with authoritative current values');
    assert.match(document.getElementById('settings-msg').textContent, /changed in another session.*reloaded/i,
      'the UI must explain that the stale Settings values were reloaded');

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

    state.setCurrentLibraryContextOrgId('2');
    formPopulation.applyLibrarySettingsToForm({ stored: {}, effective: {} });
    assert.throws(() => serializer.buildSettingsPayload(), /could not be loaded completely/,
      'an incomplete library response must not serialize fallback values into a save');
    assert.throws(() => serializer.buildEmailSettingsPayload({ includeTemplates: false }), /could not be loaded completely/,
      'an incomplete library response must not submit an email save either');

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

    state.setCurrentLibraryContextOrgId('system');
    const partialTemplateResponse = structuredClone(systemSettingsResponse);
    const onlyPersistedTemplate = row => row.templateKey === 'suggestion_submitted';
    partialTemplateResponse.stored.templates = partialTemplateResponse.stored.templates.filter(onlyPersistedTemplate);
    partialTemplateResponse.stored.configuredSystem.templates =
      partialTemplateResponse.stored.configuredSystem.templates.filter(onlyPersistedTemplate);
    partialTemplateResponse.emails.templates = partialTemplateResponse.emails.templates.filter(onlyPersistedTemplate);
    formPopulation.applyLibrarySettingsToForm(partialTemplateResponse);
    await settleAsyncRendering();
    const partialTemplateNoEdit = serializer.buildSettingsPayload().emails;
    for (const key of ['purchase_approved', 'already_owned', 'rejected', 'hold_placed']) {
      assert.strictEqual(Object.hasOwn(partialTemplateNoEdit, key), false,
        `an absent backend ${key} template must not be created from its JavaScript form default`);
    }
    const purchaseSubject = document.getElementById('email-purchase-approved-subject');
    purchaseSubject.value = `${purchaseSubject.value} (edited)`;
    const missingTemplateEdit = serializer.buildSettingsPayload().emails.purchase_approved;
    assert.strictEqual(missingTemplateEdit.subject, purchaseSubject.value);
    assert.ok(missingTemplateEdit.body,
      'an intentional system edit to a missing standard template must include content required by the backend create contract');

    const nullWorkflowLibraryResponse = structuredClone(canonicalLibraryResponse);
    const nullSystemWorkflowFields = Object.keys(nullWorkflowLibraryResponse.stored.configuredSystem.workflow)
      .filter(key => key !== 'version');
    for (const key of nullSystemWorkflowFields) {
      nullWorkflowLibraryResponse.stored.configuredSystem.workflow[key] = null;
      nullWorkflowLibraryResponse.stored.workflow[key] = null;
      nullWorkflowLibraryResponse.effective.workflow[key] = null;
    }
    nullWorkflowLibraryResponse.stored.libraryOverride.workflow = null;
    state.setCurrentLibraryContextOrgId('2');
    formPopulation.applyLibrarySettingsToForm(nullWorkflowLibraryResponse);
    await settleAsyncRendering();
    assert.strictEqual(document.getElementById('suggestion-limit').value, '',
      'the null API value remains visually empty; the serializer applies its legacy numeric fallback');
    assert.strictEqual(document.getElementById('outstanding-timeout-days').value, '',
      'the null API value remains visually empty; the serializer applies its legacy integer fallback');
    assert.strictEqual(document.getElementById('wf-common-authors-label').value, 'Popular Creators');
    assert.strictEqual(document.getElementById('wf-common-authors-help').value,
      'See if this is a creator we already collect.');
    const nullWorkflowNoEditPayload = serializer.buildSettingsPayload();
    for (const key of nullSystemWorkflowFields) {
      assert.strictEqual(Object.hasOwn(nullWorkflowNoEditPayload.workflow || {}, key), false,
        'A no-edit library save must leave nullable system workflow.' + key + ' inherited.');
    }

    const nullPatronLibraryResponse = structuredClone(canonicalLibraryResponse);
    const nullSystemPatronFields = [
      'pageTitle', 'barcodeLabel', 'pinLabel', 'loginPrompt', 'loginNote', 'suggestionFormNote',
      'noEmailMessage', 'successTitle', 'successMessage', 'alreadySubmittedMessage'
    ];
    const nullSystemStatusFields = [
      'suggestionStatusLabel', 'outstandingPurchaseStatusLabel', 'pendingHoldStatusLabel',
      'holdPlacedStatusLabel', 'closedStatusLabel', 'rejectedStatusLabel', 'holdCompletedStatusLabel',
      'holdNotPickedUpStatusLabel', 'manualStatusLabel', 'silentStatusLabel'
    ];
    for (const key of nullSystemPatronFields) {
      nullPatronLibraryResponse.stored.configuredSystem.patron[key] = null;
      nullPatronLibraryResponse.stored.patron[key] = null;
    }
    for (const key of nullSystemStatusFields) {
      nullPatronLibraryResponse.stored.configuredSystem.patron[key] = null;
      nullPatronLibraryResponse.stored.patron[key] = null;
    }
    nullPatronLibraryResponse.stored.libraryOverride.patron = null;
    state.setCurrentLibraryContextOrgId('2');
    formPopulation.applyLibrarySettingsToForm(nullPatronLibraryResponse);
    await settleAsyncRendering();
    const patronControlIds = {
      pageTitle: 'ui-patron-page-title',
      barcodeLabel: 'ui-barcode-label',
      pinLabel: 'ui-pin-label',
      loginPrompt: 'ui-login-prompt',
      loginNote: 'ui-login-note',
      suggestionFormNote: 'ui-suggestion-note',
      noEmailMessage: 'ui-no-email-msg',
      successTitle: 'ui-success-title',
      successMessage: 'ui-success-msg',
      alreadySubmittedMessage: 'ui-already-submitted-msg'
    };
    for (const key of nullSystemPatronFields) {
      const expected = nullPatronLibraryResponse.ui_text[key] ||
        (key === 'pageTitle' || key === 'barcodeLabel' || key === 'pinLabel' ? '' : undefined);
      assert.strictEqual(document.getElementById(patronControlIds[key]).value, expected,
        'A null system PatronSettings value must show the inherited effective value for ' + key + '.');
    }
    const statusControlKeys = {
      suggestionStatusLabel: 'suggestion',
      outstandingPurchaseStatusLabel: 'outstanding_purchase',
      pendingHoldStatusLabel: 'pending_hold',
      holdPlacedStatusLabel: 'hold_placed',
      closedStatusLabel: 'closed',
      rejectedStatusLabel: 'rejected',
      holdCompletedStatusLabel: 'hold_completed',
      holdNotPickedUpStatusLabel: 'hold_not_picked_up',
      manualStatusLabel: 'manual',
      silentStatusLabel: 'silent'
    };
    const statusDefaults = {
      suggestion: 'Received',
      outstanding_purchase: 'Under review',
      pending_hold: 'Being prepared',
      hold_placed: 'Hold placed',
      closed: 'Completed',
      rejected: 'Not selected for purchase',
      hold_completed: 'Completed',
      hold_not_picked_up: 'Closed',
      manual: 'Closed',
      silent: 'Closed'
    };
    for (const [field, key] of Object.entries(statusControlKeys)) {
      assert.strictEqual(document.getElementById('duplicate-status-' + key).value,
        nullPatronLibraryResponse.ui_text.duplicateStatusLabels[key] || statusDefaults[key],
        'A null system PatronSettings value must show the inherited status label for ' + field + '.');
    }
    const nullPatronNoEditPayload = serializer.buildSettingsPayload();
    for (const key of nullSystemPatronFields) {
      assert.strictEqual(Object.hasOwn(nullPatronNoEditPayload.ui_text, key), false,
        'A no-edit library save must not materialize the inherited default for ' + key + '.');
    }
    assert.strictEqual(Object.hasOwn(nullPatronNoEditPayload.ui_text, 'duplicateStatusLabels'), false,
      'A no-edit library save must not materialize null system status-label defaults.');

    const whitespacePatronResponse = structuredClone(nullPatronLibraryResponse);
    whitespacePatronResponse.stored.configuredSystem.patron.pageTitle = '   ';
    whitespacePatronResponse.stored.patron.pageTitle = '   ';
    whitespacePatronResponse.ui_text.pageTitle = '   ';
    formPopulation.applyLibrarySettingsToForm(whitespacePatronResponse);
    await settleAsyncRendering();
    assert.strictEqual(document.getElementById('ui-patron-page-title').value, '   ',
      'Whitespace is a configured value because the current form displays it rather than applying a fallback.');
    assert.strictEqual(Object.hasOwn(serializer.buildSettingsPayload().ui_text, 'pageTitle'), false,
      'A no-edit save must retain the configured whitespace value without treating it as a form default.');

    const nullSystemMessagesResponse = structuredClone(systemSettingsResponse);
    nullSystemMessagesResponse.stored.systemSettings.systemNotEnabledMessage = null;
    nullSystemMessagesResponse.stored.systemSettings.misconfiguredMessage = null;
    state.setStaffSession({
      authenticated: true,
      accessAllowed: true,
      antiforgeryToken: 'test-antiforgery-token',
      staff: { role: 'super_admin', userPrincipalName: 'admin@example.org' }
    });
    state.setCurrentLibraryContextOrgId('system');
    formPopulation.applyLibrarySettingsToForm(nullSystemMessagesResponse);
    await settleAsyncRendering();
    const nullSystemMessagesPayload = serializer.buildSettingsPayload();
    for (const key of ['systemNotEnabledMessage', 'misconfiguredMessage']) {
      assert.strictEqual(Object.hasOwn(nullSystemMessagesPayload.ui_text, key), false,
        'A no-edit system save must preserve nullable ' + key + ' without materializing its form default.');
    }
    const intentionalSystemMessage = document.getElementById('ui-system-not-enabled-msg');
    intentionalSystemMessage.value += ' Updated';
    assert.strictEqual(serializer.buildSettingsPayload().ui_text.systemNotEnabledMessage, intentionalSystemMessage.value,
      'An intentional system-only message edit must still be submitted.');

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
