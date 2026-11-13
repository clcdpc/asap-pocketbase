const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const flush = () => new Promise(resolve => setImmediate(resolve));
const response = (status, data) => ({ ok: status < 400, status, json: async () => data });

function settings(version) {
  const empty = { exists: false, values: [] };
  const configured = { workflow: {}, patron: { loginNote: 'Saved note' }, email: {},
    publicationOptions: empty, commonCreators: empty, allowedPatronCodeIds: empty,
    providers: [], formats: [], templates: [], branding: {},
    catalog: { pacBaseUrl: 'https://system.example/polaris', formatIconUrlPattern: '{pacBaseUrl}/icons/{id2}.gif' } };
  return { orgId: '2', version, organization: { id: 2, name: 'Library', active: true },
    stored: { systemSettings: { enabledLibraryOrgIds: [2], libraryOrgIds: [2] }, polaris: {},
      configuredSystem: configured, libraryOverride: { ...configured, catalog: { pacBaseUrl: null, formatIconUrlPattern: null, pacOrganizationId: 27 } }, providers: [], formats: [],
      templates: [], customFields: [], formatRules: [], autoClaimRules: [], origins: [] },
    effective: { loginNote: 'Saved note', email: {}, formats: [], customFields: [], externalSearchProviders: [] },
    autoClaimStaff: [], ui_text: { loginNote: 'Saved note' } };
}

fixture(async ({ dom, load, get }) => {
  let reads = 0, posts = 0, complete, completePreview;
  const submitted = [], previews = [];
  global.fetch = async (url, options = {}) => {
    if (url.endsWith('/session')) return response(200, { authenticated: true, antiforgeryToken: 'token' });
    if (url.includes('/settings?')) { reads++; return response(200, settings(`v${reads}`)); }
    if (url.endsWith('/organizations')) return response(200, { data: [
      { id: 1, name: 'System', abbreviation: null, parentOrganizationId: null, organizationCodeId: 1, isActive: true, version: 'o1' },
      { id: 2, name: 'Library', abbreviation: null, parentOrganizationId: 1, organizationCodeId: 2, isActive: true, version: 'o2' }
    ] });
    if (url.includes('/patron-codes?')) return response(200, { data: [] });
    if (url.endsWith('/settings/catalog-preview')) {
      previews.push(JSON.parse(options.body));
      return new Promise(resolve => { completePreview = () => resolve(response(200, { orgId: '2', effective: {}, formatIconUrl: null })); });
    }
    if (url.endsWith('/settings') && options.method === 'POST') {
      posts++;
      submitted.push(JSON.parse(options.body));
      return new Promise(resolve => { complete = () => resolve(response(409, { code: 'stale_version' })); });
    }
    throw new Error(`Unexpected request ${url}`);
  };
  const controller = (await load('settings')).createSettingsController({ root: get('#settings-view'),
    tab: get('#settings-view-tab'), announce() {} });
  const actor = { id: '20', tenantId: 'tenant', objectId: 'actor', role: 'admin', organizationId: 2 };
  controller.bind(); controller.setStaff(actor); await controller.activate();
  assert.equal(reads, 1);
  const icon = get('#format-icon-url-pattern');
  assert.equal(icon.disabled, false, 'Catalog icon defaults are editable through library overrides.');
  assert.equal(icon.value, '{pacBaseUrl}/icons/{id2}.gif');
  assert.match(icon.closest('.settings-field').textContent, /Using system value/);
  assert.equal(get('#pac-organization-id').value, '27');
  assert.equal(get('#leap-bib-url-pattern').disabled, true, 'LEAP remains system-only.');
  get('#catalog-preview-button').click();
  for (let i = 0; i < 30 && !completePreview; i++) await flush();
  assert.deepEqual(previews.at(-1).catalog, {}, 'Preview inherits untouched settings rather than pinning displayed defaults.');
  icon.value = ''; icon.dispatchEvent(new window.Event('input', { bubbles: true }));
  completePreview(); await flush();
  assert.equal(get('#catalog-preview').textContent, '', 'An older preview cannot overwrite a newer draft.');
  completePreview = null; get('#catalog-preview-button').click();
  for (let i = 0; i < 30 && !completePreview; i++) await flush();
  assert.equal(previews.at(-1).catalog.formatIconUrlPattern, '', 'Explicit blank is a disable intent.');
  completePreview(); await flush();
  assert.match(get('#catalog-preview').textContent, /unavailable; results use a generic icon/);
  const note = get('#patron-login-note');
  const edit = text => { note.value = text; note.dispatchEvent(new dom.window.Event('input', { bubbles: true })); };
  const submit = () => get('#settings-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
  edit('Recover my exact\n  draft'); submit();
  for (let i = 0; i < 30 && !complete; i++) await flush();
  assert.equal(posts, 1, get('#settings-message').textContent);
  assert.equal(submitted[0].catalog.formatIconUrlPattern, '');
  assert.equal(Object.hasOwn(submitted[0], 'systemSettings'), false);
  complete();
  for (let i = 0; i < 30 && get('#settings-form').inert; i++) await flush();
  assert.equal(reads, 1);
  assert.equal(note.value, 'Recover my exact\n  draft');
  assert.equal(get('#settings-version').value, 'v1');
  assert.equal(controller.isDirty(), true);
  assert.equal(get('#settings-save-title').textContent, 'Conflict; edits retained');
  assert.equal(get('#settings-save').disabled, true);
  assert.equal(get('#settings-reset').disabled, true);
  assert.equal(get('#save-branding-logo').disabled, true);
  submit(); await flush(); assert.equal(posts, 1);
  require('./helpers/confirmation.cjs').answerConfirmations(dom.window, () => false);
  get('#settings-refresh').click();
  for (let i = 0; i < 10; i++) await flush();
  assert.equal(reads, 1); assert.equal(note.value, 'Recover my exact\n  draft');
  require('./helpers/confirmation.cjs').answerConfirmations(dom.window, () => true);
  get('#settings-refresh').click();
  for (let i = 0; i < 30 && controller.isDirty(); i++) await flush();
  assert.equal(reads, 2); assert.equal(controller.isDirty(), false);
  assert.equal(get('#settings-version').value, 'v2');
  assert.equal(note.value, 'Saved note');
  assert.equal(icon.value, '{pacBaseUrl}/icons/{id2}.gif');
  icon.value = ''; icon.dispatchEvent(new window.Event('input', { bubbles: true }));
  icon.closest('.settings-field').querySelector('.settings-use-system').click();
  assert.equal(icon.value, '{pacBaseUrl}/icons/{id2}.gif');

  // A late conflict belongs to the retired actor and cannot mark the new owner conflicted.
  complete = null; edit('Retired draft'); submit();
  for (let i = 0; i < 30 && !complete; i++) await flush();
  assert.equal(posts, 2);
  assert.equal(submitted[1].catalog.formatIconUrlPattern, null, 'Use system value explicitly clears the override.');
  controller.setStaff({ ...actor, id: '21', objectId: 'replacement' });
  await controller.activate();
  edit('New owner draft'); complete();
  for (let i = 0; i < 15; i++) await flush();
  assert.equal(note.value, 'New owner draft');
  assert.equal(get('#settings-version').value, 'v3');
  assert.equal(get('#settings-save-title').textContent, 'Unsaved changes');
  assert.equal(get('#settings-save').disabled, false);
  assert.equal(reads, 3);
  controller.dispose();
  console.log('Settings conflicts retain exact drafts until explicit resolution and respect owner changes');
}).catch(error => { console.error(error); process.exitCode = 1; });
