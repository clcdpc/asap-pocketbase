const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');
const flush = () => new Promise(resolve => setImmediate(resolve));
const response = (status, data) => ({ ok: status < 400, status, json: async () => data });

function settings(version) {
  const empty = { exists: false, values: [] };
  const configured = { workflow: {}, patron: { loginNote: 'Saved note' }, email: {},
    publicationOptions: empty, commonCreators: empty, allowedPatronCodeIds: empty,
    providers: [], formats: [], templates: [], branding: {} };
  return { orgId: '2', version, organization: { id: 2, name: 'Library', active: true },
    stored: { systemSettings: { enabledLibraryOrgIds: [2], libraryOrgIds: [2] }, polaris: {},
      configuredSystem: configured, libraryOverride: configured, providers: [], formats: [],
      templates: [], customFields: [], formatRules: [], autoClaimRules: [], origins: [] },
    effective: { loginNote: 'Saved note', email: {}, formats: [], customFields: [], externalSearchProviders: [] },
    autoClaimStaff: [], ui_text: { loginNote: 'Saved note' } };
}

fixture(async ({ dom, load, get }) => {
  let reads = 0, posts = 0, complete;
  global.fetch = async (url, options = {}) => {
    if (url.endsWith('/session')) return response(200, { authenticated: true, antiforgeryToken: 'token' });
    if (url.includes('/settings?')) { reads++; return response(200, settings(`v${reads}`)); }
    if (url.endsWith('/organizations')) return response(200, { data: [
      { id: 1, name: 'System', abbreviation: null, parentOrganizationId: null, organizationCodeId: 1, isActive: true, version: 'o1' },
      { id: 2, name: 'Library', abbreviation: null, parentOrganizationId: 1, organizationCodeId: 2, isActive: true, version: 'o2' }
    ] });
    if (url.includes('/patron-codes?')) return response(200, { data: [] });
    if (url.endsWith('/settings') && options.method === 'POST') {
      posts++;
      return new Promise(resolve => { complete = () => resolve(response(409, { code: 'stale_version' })); });
    }
    throw new Error(`Unexpected request ${url}`);
  };
  const controller = (await load('settings')).createSettingsController({ root: get('#settings-view'),
    tab: get('#settings-view-tab'), announce() {} });
  const actor = { id: '20', tenantId: 'tenant', objectId: 'actor', role: 'admin', organizationId: 2 };
  controller.bind(); controller.setStaff(actor); await controller.activate();
  assert.equal(reads, 1);
  const note = get('#patron-login-note');
  const edit = text => { note.value = text; note.dispatchEvent(new dom.window.Event('input', { bubbles: true })); };
  const submit = () => get('#settings-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
  edit('Recover my exact\n  draft'); submit();
  for (let i = 0; i < 30 && !complete; i++) await flush();
  assert.equal(posts, 1, get('#settings-message').textContent);
  complete();
  for (let i = 0; i < 30 && get('#settings-form').inert; i++) await flush();
  assert.equal(reads, 1);
  assert.equal(note.value, 'Recover my exact\n  draft');
  assert.equal(get('#settings-version').value, 'v1');
  assert.equal(controller.isDirty(), true);
  assert.equal(get('#settings-save-title').textContent, 'Conflict; edits retained');
  assert.equal(get('#settings-save').disabled, true);
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

  // A late conflict belongs to the retired actor and cannot mark the new owner conflicted.
  complete = null; edit('Retired draft'); submit();
  for (let i = 0; i < 30 && !complete; i++) await flush();
  assert.equal(posts, 2);
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
