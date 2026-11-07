const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');

const set = values => ({ exists: true, values });
const absent = { exists: false, values: [] };
const format = { id: '10', code: 'book', ownerOrganizationId: '1', label: 'Book', isEnabled: true,
  version: 'format-v1', overridden: false, sortOrder: 0, messageBehavior: 'none', message: null, customFields: {} };
const provider = { id: '20', key: 'catalog', label: 'Catalog', urlTemplate: 'https://catalog.example.org?q={query}',
  isEnabled: true, overridden: false, sortOrder: 0 };
const field = { id: '30', key: 'owned_field', label: 'Owned field', type: 'text', enabled: true, options: [] };
const template = { id: '40', organizationId: '1', templateKey: 'rejection:source', displayName: 'Named rejection',
  subject: 'Accepted subject', body: 'Accepted body', version: 'template-v1', enabled: true, isCustom: false, sortOrder: 0 };
const publication = [{ id: 'accepted-option', label: 'Any year', enabled: true, sortOrder: 0 }];
const workflow = { suggestionLimit: 5, commonAuthorsEnabled: true, patronCodeEligibilityEnabled: true };
const email = { fromAddress: 'sender@example.org', fromName: 'Sender' };
const users = [2, 3].map(id => ({ id: String(id), userPrincipalName: `staff${id}@example.org`,
  notificationEmail: `staff${id}@example.org`, displayName: `Accepted staff ${id}`, role: 'staff', organizationId: 2,
  active: true, version: `staff-${id}-v1` }));
const codes = [{ id: 1, description: 'Adult' }, { id: 2, description: 'Child' }];
const data = {
  orgId: '2', version: 'settings-v1', organization: { id: 2, name: 'Fixture library', active: true }, workflow, emails: { ...email, templates: [] }, ui_text: {},
  stored: {
    systemSettings: { enabledLibraryOrgIds: [2], libraryOrgIds: [2] }, polaris: {},
    configuredSystem: { workflow, patron: {}, email, publicationOptions: set(publication), commonCreators: set(['Accepted creator']),
      allowedPatronCodeIds: set([1]), providers: [provider], formats: [format], templates: [template], branding: { hasLogo: false, altText: '' } },
    libraryOverride: { workflow: {}, patron: {}, email: {}, publicationOptions: absent, commonCreators: absent, allowedPatronCodeIds: absent,
      providers: [], formats: [], templates: [], branding: { hasLogo: false, altText: null } },
    workflow: {}, patron: {}, email: {}, origins: [], publicationOptions: publication, commonCreators: ['Accepted creator'], allowedPatronCodeIds: [1],
    providers: [provider], formats: [format], customFields: [field], formatRules: [{ code: 'book', customFields: {} }], templates: [template],
    autoClaimRules: [{ materialFormatId: '10', staffUserId: '2', active: true }], branding: { hasLogo: false, altText: null }
  },
  effective: { formats: [format], externalSearchProviders: [provider], customFields: [field], publicationOptions: publication,
    commonCreators: ['Accepted creator'], allowedPatronCodeIds: [1], email, logoAltText: '' },
  autoClaimStaff: users.map(user => ({ id: user.id, label: user.displayName })), patronCodeChoices: codes
};
const response = (status, body) => ({ ok: status < 400, status, statusText: 'OK', json: async () => structuredClone(body) });
const flush = () => new Promise(resolve => setImmediate(resolve));
async function until(predicate) {
  for (let attempt = 0; attempt < 40; attempt++) { if (predicate()) return; await flush(); }
  assert.ok(predicate(), 'Expected the owning Settings/Staff load to settle.');
}

fixture(async ({ load, get }) => {
  get('#workspace').hidden = false; get('#settings-view').hidden = false;
  const mutations = [], announcements = [];
  global.fetch = async (url, options = {}) => {
    if (options.method && options.method !== 'GET') {
      mutations.push({ url, method: options.method }); return response(400, { message: 'Unexpected fixture mutation' });
    }
    if (url.endsWith('/session')) return response(200, { authenticated: true, antiforgeryToken: 'test-token' });
    if (url.includes('/settings?')) return response(200, data);
    if (url.endsWith('/organizations')) return response(200, { data: [
      { id: 1, name: 'System', organizationCodeId: 1, parentOrganizationId: null, isActive: true, version: 'org-1' },
      { id: 2, name: 'Fixture library', organizationCodeId: 2, parentOrganizationId: 1, isActive: true, version: 'org-2' }
    ] });
    if (url.includes('/patron-codes?')) return response(200, { data: codes });
    if (url.startsWith('/api/asap/staff/users')) return response(200, { users, canAssignSuperAdmin: false });
    throw new Error(`Unexpected fixture read: ${url}`);
  };
  const { createSettingsController } = await load('settings');
  const controller = createSettingsController({ root: get('#settings-view'), tab: get('#settings-view-tab'),
    announce: message => { announcements.push(message); } });
  const edit = (selector, value) => {
    const control = get(selector); assert.ok(control, `${selector}: ${get('#settings-message').textContent}; ready=${controller.isReady()}`);
    assert.equal(control.matches(':disabled'), false, `${selector} must be editable.`);
    assert.equal(control.closest('[hidden]'), null, `${selector} must be exposed before editing.`);
    control.value = value; control.dispatchEvent(new window.Event(control.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true }));
  };
  const values = () => [...get('#settings-form').querySelectorAll('input, select, textarea')].map(control => [control.value, control.checked]);
  const staffRow = () => get('.settings-staff-row[data-staff-id="2"]');
  const openStaff = async () => {
    controller.activatePanel('staff'); await until(() => staffRow() && !get('#staff-refresh').disabled);
    const toggle = staffRow().querySelector('.settings-staff-summary');
    if (toggle.getAttribute('aria-expanded') === 'false') toggle.click();
  };
  const publicationLabel = '#publication-options-editor [data-publication-field="label"]';
  const creator = '#common-creators-editor input';
  const providerLabel = '#external-search-provider-editor [data-provider-id="20"] input[type="text"]';
  const formatLabel = '#material-formats-editor [data-format-code="book"] [data-material-field="label"]';
  const fieldLabel = '#additional-fields-editor [data-field-key="owned_field"] [data-custom-field="label"]';
  const claimStaff = '#format-claim-rules-editor select:nth-of-type(1)';
  const additional = '#email-templates-editor [data-template-source-id="40"]';
  const matrix = [
    { label: 'Publication options', panel: 'patron', edit: () => edit(publicationLabel, 'private publication'), target: publicationLabel },
    { label: 'Common creator list', panel: 'workflow', edit: () => edit(creator, 'private creator'), target: creator },
    { label: 'Allowed patron codes', panel: 'workflow', edit: () => get('#patron-codes-editor button').click(), target: '#patron-code-picker' },
    { label: 'External search providers', panel: 'workflow', edit: () => edit(providerLabel, 'private provider'), target: providerLabel },
    { label: 'Material formats', panel: 'patron', edit: () => edit(formatLabel, 'private format'), target: formatLabel },
    { label: 'Custom fields', panel: 'patron', edit: () => edit(fieldLabel, 'private field'), target: fieldLabel },
    { label: 'Auto-claim rules', panel: 'patron', edit: () => {
      const selector = '#format-claim-rules-editor [data-domain-row] label:nth-child(2) select'; edit(selector, '3');
    }, target: claimStaff },
    { label: 'Additional templates', panel: 'templates', edit: () => {
      get(`${additional} [data-template-toggle]`).click(); edit(`${additional} .template-body`, 'private template');
      get(`${additional} [data-template-toggle]`).click();
    }, target: `${additional} [data-template-toggle]` }
  ];
  try {
    controller.bind(); controller.setStaff({ id: '1', tenantId: 'fixture', authenticationEmail: 'actor@example.org', role: 'admin', organizationId: 2 });
    await controller.activate('workflow'); assert.equal(controller.isReady(), true); await openStaff();
    assert.equal(controller.isDirty(), false);
    for (const cell of matrix) {
      controller.discardDraft(); controller.activatePanel(cell.panel); cell.edit();
      assert.equal(controller.isDirty(), true, `${cell.label}: the actual edit owns a Settings draft.`);
      await openStaff(); edit('.settings-staff-row[data-staff-id="2"] input[type="text"]', 'private retained Staff draft');
      [...staffRow().querySelectorAll('button')].find(button => button.textContent === 'Save changes').click(); await flush();
      assert.match(get('#settings-message').textContent, /1 unsaved draft is blocking this action/);
      const action = [...get('#settings-draft-blockers').querySelectorAll('button')].find(button => button.textContent.endsWith(`— ${cell.label}`));
      assert.ok(action, `${cell.label}: the real competing Settings owner exposes this destination.`);
      assert.doesNotMatch(get('#settings-draft-blockers').textContent, /private|@example|Symbol/);
      const draftsBefore = values(); action.click();
      assert.equal(get(`#settings-${cell.panel}`).hidden, false, `${cell.label}: correct section.`);
      assert.equal(document.activeElement, get(cell.target), `${cell.label}: meaningful owning editor/control focus.`);
      if (cell.label === 'Additional templates') assert.equal(get(cell.target).getAttribute('aria-expanded'), 'true');
      assert.deepEqual(values(), draftsBefore, `${cell.label}: navigation retains every blocked and blocking control value.`);
      assert.match(announcements.at(-1), new RegExp(`Showing ${cell.label}.*Unsaved changes are preserved`));
      assert.equal(mutations.length, 0, `${cell.label}: neither rejected Staff Save nor navigation dispatches a mutation.`);
      console.log(`PASS blocking Settings destination: ${cell.label}`);
    }
    controller.discardDraft(); assert.equal(controller.isDirty(), false);
    assert.equal(mutations.length, 0);
    console.log(`${matrix.length} Settings domain blocker destinations preserved both drafts and made no mutation.`);
  } finally { controller.dispose(); }
}).catch(error => { console.error(error); process.exitCode = 1; });
