const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');

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
  await fixture(async ({ load, dom }) => {
  let controller;
  try {
    const { createSettingsController } = await load('settings');

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
        return response(200, { code: 'saved', data: { orgId: 'system', version: 'settings-v2' } });
      }
      throw new Error(`Unexpected request: ${requestUrl}`);
    };

    controller = createSettingsController({
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
  } finally {
    controller?.dispose();
  }
  });
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
