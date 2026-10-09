const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

const response = (status, body) => ({
  ok: status < 400,
  status,
  statusText: 'Test response',
  json: async () => body
});

const validCatalog = [
  { id: 1, name: 'System', abbreviation: null, organizationCodeId: 1,
    parentOrganizationId: null, isActive: true, version: 'system-v1' },
  { id: 2, name: 'Library Two', abbreviation: 'TWO', organizationCodeId: 2,
    parentOrganizationId: 1, isActive: true, version: 'library-v1' }
];

function settingsSnapshot(systemSettings = { enabledLibraryOrgIds: [2], libraryOrgIds: [2] }) {
  const emptySet = { exists: false, values: [] };
  const configuredSystem = {
    workflow: {}, patron: {}, email: { fromAddress: 'system@example.org', fromName: 'System' },
    publicationOptions: emptySet, commonCreators: emptySet, allowedPatronCodeIds: emptySet,
    providers: [], formats: [], templates: [], branding: { hasLogo: false, altText: null }
  };
  return {
    orgId: 'system', version: 'settings-v1', organization: null,
    stored: {
      systemSettings, polaris: {}, configuredSystem, libraryOverride: null,
      workflow: {}, patron: {}, email: {}, origins: [], publicationOptions: [],
      commonCreators: [], allowedPatronCodeIds: [], providers: [], formats: [],
      customFields: [], formatRules: [], templates: [], autoClaimRules: [],
      branding: { hasLogo: false, altText: null }
    },
    effective: {
      allowedPatronCodeIds: [], publicationOptions: [], commonCreators: [],
      externalSearchProviders: [], formats: [], customFields: [],
      email: { fromAddress: 'system@example.org', fromName: 'System' }, logoAltText: null
    },
    workflow: {}, ui_text: {}, emails: { fromAddress: 'system@example.org', fromName: 'System', templates: [] },
    patronCodeChoices: [], autoClaimStaff: []
  };
}

async function runInvalidCatalogCase(settings, organizationBody, expectedMessage) {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-organization-catalog-guard-'));
  let dom;
  try {
    fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
    fs.cpSync(path.join(frontendRoot, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
    const { createSettingsController } = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings.js')).href);
    dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
      url: 'https://localhost/staff/'
    });
    Object.assign(global, {
      window: dom.window, document: dom.window.document, FormData: dom.window.FormData,
      Node: dom.window.Node, Event: dom.window.Event
    });
    dom.window.confirm = () => true;

    let settingsPosts = 0;
    global.fetch = async (url, init = {}) => {
      const requestUrl = String(url);
      if (requestUrl.endsWith('/api/asap/staff/session')) {
        return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
      }
      if (requestUrl.includes('/api/asap/staff/settings?orgId=')) return response(200, settings);
      if (requestUrl.endsWith('/api/asap/staff/organizations')) return response(200, organizationBody);
      if (requestUrl.includes('/api/asap/staff/polaris/patron-codes?')) return response(200, { code: 'ok', data: [] });
      if (requestUrl.endsWith('/api/asap/staff/settings')) {
        settingsPosts++;
        return response(200, { code: 'saved', data: { version: 'settings-v2' } });
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    const controller = createSettingsController({
      root: document.getElementById('settings-view'),
      tab: document.getElementById('settings-view-tab'),
      announce: () => {},
      getStaff: () => ({ role: 'super_admin', organizationId: 1 })
    });
    controller.bind();
    controller.setStaff({ id: '1', role: 'super_admin', organizationId: 1 });
    await controller.activate();

    assert.match(document.getElementById('settings-message').textContent, expectedMessage);
    const loginNote = document.getElementById('patron-login-note');
    loginNote.value = 'Attempt destructive save';
    loginNote.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    document.getElementById('settings-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
    await Promise.resolve();
    assert.equal(settingsPosts, 0, 'a malformed successful catalog response must not allow a settings POST');
    controller.dispose();
  } finally {
    dom?.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
}

(async () => {
  const partialLibrary = { ...validCatalog[1] };
  delete partialLibrary.parentOrganizationId;
  const noSystem = [validCatalog[1]];
  const activeMismatch = [validCatalog[0], { ...validCatalog[1], isActive: false }];
  const incompleteSnapshot = settingsSnapshot({ libraryOrgIds: [2] });

  await runInvalidCatalogCase(settingsSnapshot(), { organizations: validCatalog }, /organization list is incomplete/i);
  await runInvalidCatalogCase(settingsSnapshot(), { code: 'ok', data: noSystem }, /missing the system organization/i);
  await runInvalidCatalogCase(settingsSnapshot(), { code: 'ok', data: [validCatalog[0], partialLibrary] }, /malformed or incomplete/i);
  await runInvalidCatalogCase(incompleteSnapshot, { code: 'ok', data: validCatalog }, /participation snapshot is incomplete/i);
  await runInvalidCatalogCase(settingsSnapshot(), { code: 'ok', data: activeMismatch }, /missing active libraries/i);
  console.log('Incomplete and malformed successful organization catalogs block destructive Settings saves');
})().catch(error => { console.error(error); process.exitCode = 1; });
