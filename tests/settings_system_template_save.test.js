const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

const BUILTIN_TEMPLATES = [
  ['suggestion_submitted', 'Submission subject', 'Submission body'],
  ['purchase_approved', 'Purchase subject', 'Purchase body'],
  ['already_owned', 'Owned subject', 'Owned body'],
  ['hold_placed', 'Hold subject', 'Hold body'],
  ['rejected', 'Rejected subject', 'Rejected body']
].map(([templateKey, subject, body], index) => ({
  id: String(index + 101),
  organizationId: 1,
  templateKey,
  subject,
  body,
  enabled: true,
  isCustom: false,
  version: `builtin-${index + 1}`
}));

const EMPTY_SET = { exists: false, values: [] };

function response(status, body) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: 'OK',
    json: async () => body
  };
}

function settingsData(scope, version, additionalTemplate = null) {
  const system = scope === 'system';
  const organizationId = system ? 1 : 2;
  const templates = additionalTemplate ? [...BUILTIN_TEMPLATES, additionalTemplate] : [...BUILTIN_TEMPLATES];
  const configuredSystem = {
    workflow: {},
    patron: {},
    email: { fromAddress: 'system@example.org', fromName: 'System' },
    publicationOptions: EMPTY_SET,
    commonCreators: EMPTY_SET,
    allowedPatronCodeIds: EMPTY_SET,
    providers: [],
    formats: [],
    templates: additionalTemplate ? [additionalTemplate] : [],
    branding: { hasLogo: false, altText: 'System alt' }
  };
  return {
    orgId: scope,
    version,
    organization: { id: organizationId, name: system ? 'System' : 'Library Two', active: true },
    stored: {
      systemSettings: { patronEmbedAllowedOrigins: [] },
      polaris: {
        accessId: 'SuggestAPI',
        workstationId: 1,
        systemPolarisUserId: 1,
        organizationIdForRequests: 3,
        pickupOrganizationId: 0
      },
      configuredSystem,
      libraryOverride: system ? null : {
        workflow: null,
        patron: null,
        email: null,
        publicationOptions: EMPTY_SET,
        commonCreators: EMPTY_SET,
        allowedPatronCodeIds: EMPTY_SET,
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
      templates,
      autoClaimRules: [],
      branding: { hasLogo: false, altText: system ? 'System alt' : null }
    },
    effective: {
      allowedPatronCodeIds: [],
      publicationOptions: [],
      commonCreators: [],
      externalSearchProviders: [],
      formats: [],
      customFields: [],
      email: { fromAddress: 'system@example.org', fromName: 'System' },
      logoAltText: system ? 'System alt' : null
    },
    workflow: {},
    ui_text: {},
    emails: {
      fromAddress: 'system@example.org',
      fromName: 'System',
      templates
    },
    autoClaimStaff: [],
    formatClaimRules: []
  };
}

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
  await Promise.resolve();
}

async function waitFor(predicate, message) {
  for (let attempt = 0; attempt < 40; attempt++) {
    if (predicate()) return;
    await flush();
  }
  assert.fail(message);
}

async function createScenario(settingsModule, frontendRoot, scope) {
  const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
    url: 'http://localhost/staff/'
  });
  global.window = dom.window;
  global.document = dom.window.document;
  global.FormData = dom.window.FormData;
  global.Node = dom.window.Node;
  global.Event = dom.window.Event;
  dom.window.confirm = () => true;

  let savedBody = null;
  let savedAdditionalTemplate = null;
  let settingsRequests = 0;
  const savedScope = scope;
  global.fetch = async (url, options = {}) => {
    const requestUrl = String(url);
    if (requestUrl.endsWith('/api/asap/staff/session')) {
      return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
    }
    if (requestUrl.includes(`/api/asap/staff/settings?orgId=${encodeURIComponent(savedScope)}`)) {
      settingsRequests += 1;
      return response(200, settingsData(savedScope, savedBody ? 'after-save' : 'before-save', savedAdditionalTemplate));
    }
    if (requestUrl.endsWith('/api/asap/staff/organizations')) {
      return response(200, [{ id: 2, name: 'Library Two', abbreviation: 'TWO', active: true }]);
    }
    if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) {
      return response(200, { code: 'ok', data: [] });
    }
    if (requestUrl.endsWith('/api/asap/staff/settings')) {
      savedBody = JSON.parse(options.body);
      const added = savedBody.templates?.find(item =>
        item.templateKey?.startsWith('rejection:') && item.subject && item.body &&
        !BUILTIN_TEMPLATES.some(template => template.templateKey === item.templateKey));
      if (added) {
        savedAdditionalTemplate = {
          ...added,
          id: '901',
          organizationId: 1,
          isCustom: false,
          version: 'additional-template-version'
        };
      }
      return response(200, { code: 'saved', data: { version: 'after-save' } });
    }
    throw new Error(`Unexpected request: ${requestUrl}`);
  };

  const controller = settingsModule.createSettingsController({
    root: document.getElementById('settings-view'),
    tab: document.getElementById('settings-view-tab'),
    announce: () => {},
    getStaff: () => scope === 'system'
      ? { role: 'super_admin', organizationId: 1 }
      : { role: 'admin', organizationId: 2 }
  });
  controller.bind();
  controller.setStaff(scope === 'system'
    ? { id: '1', role: 'super_admin', organizationId: 1 }
    : { id: '2', role: 'admin', organizationId: 2 });
  await controller.activate();

  return { dom, controller, getSavedBody: () => savedBody, getSettingsRequests: () => settingsRequests };
}

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-system-template-save-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const settingsModule = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings.js')).href);

    const system = await createScenario(settingsModule, frontendRoot, 'system');
    const systemForm = document.getElementById('settings-form');
    const pickup = document.getElementById('polaris-pickup-org-id');
    assert.strictEqual(pickup.value, '0');
    assert.strictEqual(pickup.validity.valid, true, 'The zero pickup sentinel must satisfy the number constraint.');
    assert.strictEqual(systemForm.checkValidity(), true, 'A hidden Polaris panel must not invalidate a system save.');

    document.getElementById('settings-nav-templates').click();
    document.getElementById('add-email-template').click();
    const addedRow = document.querySelector('#email-templates-editor [data-domain-row]');
    assert.ok(addedRow, 'System scope should render a new additional-template row.');
    addedRow.querySelector('.template-key').value = 'rejection:browser-regression';
    addedRow.querySelector('.template-display-name').value = 'Browser regression template';
    addedRow.querySelector('.template-subject').value = 'Regression subject';
    addedRow.querySelector('.template-body').value = 'Regression body';
    addedRow.querySelector('.template-body').dispatchEvent(new system.dom.window.Event('input', { bubbles: true }));
    systemForm.requestSubmit();
    await waitFor(() => system.getSavedBody() !== null, 'System template save did not reach the request helper.');
    await waitFor(() => system.getSettingsRequests() === 2, 'System template save did not refresh settings.');
    assert.strictEqual(system.getSavedBody().orgId, 'system');
    assert.ok(system.getSavedBody().templates.some(item => item.templateKey === 'rejection:browser-regression'));
    assert.ok(
      [...document.querySelectorAll('#email-templates-editor .template-key')]
        .some(input => input.value === 'rejection:browser-regression'),
      'The saved additional template should remain after the settings refresh.'
    );
    assert.strictEqual(system.controller.isDirty(), false, 'A successful system template save should refresh the baseline.');
    system.dom.window.close();

    const library = await createScenario(settingsModule, frontendRoot, '2');
    const libraryForm = document.getElementById('settings-form');
    const libraryPickup = document.getElementById('polaris-pickup-org-id');
    assert.strictEqual(libraryPickup.disabled, true, 'Polaris must remain disabled in library scope.');
    assert.strictEqual(libraryForm.checkValidity(), true);
    const suggestionLimitOverride = document.querySelector(
      '[data-setting-section="workflow"][data-setting-key="suggestionLimit"] .settings-override-toggle'
    );
    suggestionLimitOverride.checked = true;
    suggestionLimitOverride.dispatchEvent(new library.dom.window.Event('change', { bubbles: true }));
    const suggestionLimit = document.getElementById('suggestion-limit');
    suggestionLimit.value = '0';
    suggestionLimit.dispatchEvent(new library.dom.window.Event('input', { bubbles: true }));
    assert.strictEqual(
      libraryForm.checkValidity(),
      false,
      'Library-scoped number constraints must continue to reject invalid values.'
    );
    libraryForm.requestSubmit();
    await flush();
    assert.strictEqual(library.getSavedBody(), null, 'Invalid library settings must not be submitted.');

    suggestionLimit.value = '1';
    suggestionLimit.dispatchEvent(new library.dom.window.Event('input', { bubbles: true }));
    const override = document.querySelector('[data-setting-section="patron"][data-setting-key="loginNote"] .settings-override-toggle');
    override.checked = true;
    override.dispatchEvent(new library.dom.window.Event('change', { bubbles: true }));
    const loginNote = document.getElementById('patron-login-note');
    loginNote.value = 'Library-specific note';
    loginNote.dispatchEvent(new library.dom.window.Event('input', { bubbles: true }));
    libraryForm.requestSubmit();
    await waitFor(() => library.getSavedBody() !== null, 'Library save did not reach the request helper.');
    assert.strictEqual(Object.prototype.hasOwnProperty.call(library.getSavedBody(), 'polaris'), false);
    assert.strictEqual(Object.prototype.hasOwnProperty.call(library.getSavedBody(), 'systemSettings'), false);
    library.dom.window.close();

    console.log('System template requestSubmit and library Polaris-scope regressions passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
