const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');
const { fixture } = require('./helpers/staff-controller.cjs');
const { patronUiText } = require('./helpers/patron-session-fixture.js');
const flush = () => new Promise(resolve => setImmediate(resolve));
const actor = { id: '20', tenantId: 'a', authenticationEmail: 'a@example.org', role: 'super_admin', organizationId: 1 };
const fields = [
  { key: 'audience', label: 'Audience', type: 'text', enabled: true },
  { key: 'description', label: 'Description', type: 'textarea', enabled: true },
  { key: 'language', label: 'Language', type: 'select', enabled: true,
    options: [{ id: 'en', label: 'English' }, { id: 'es', label: 'Spanish' }] }
];
const rules = Object.fromEntries(fields.map(field => [field.key, { mode: 'optional' }]));
const configuration = { availableFormats: ['book', 'dvd'], additionalFieldDefinitions: fields,
  formatRules: { book: { customFields: rules }, dvd: { customFields: rules } } };
const draft = { audience: 'New audience', description: 'New multiline\ndescription', language: 'es' };
const historical = { audience: 'Accepted audience', description: 'Accepted description', language: 'en' };

function editFields(container, dom, values) {
  for (const [key, value] of Object.entries(values)) {
    const input = container.querySelector(`[aria-label="${fields.find(field => field.key === key).label}"]`);
    input.value = value;
    input.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
  }
}

(async () => {
  for (const values of [draft, { audience: '', description: '', language: '' }]) {
    await fixture(async ({ load, get, dom }) => {
      const { createDraftScope } = await load('draft-scope');
      const { createRequestEditor } = await load('request-editor');
      const root = get('#request-dialog-body');
      const scope = createDraftScope();
      const original = { id: '200', version: 'v1', title: 'A title', format: 'book',
        customFields: Object.fromEntries(fields.map(field => [field.key,
          { label: field.label, type: field.type, value: historical[field.key] }])),
        capabilities: { canEditIdentifier: true, canChangeBib: true, canChangeWorkflowState: true } };
      let payload;
      const editor = createRequestEditor({ configuration, announce() {}, request: async () => ({}),
        polarisLookup: { open() {}, close() {}, invalidate() {} },
        context: { request: original, actor, root, scope, isCurrent: () => true, isPending: () => false,
          admit: declaration => scope.admit(declaration).allowed, confirm: () => true,
          mutate: async (_path, body) => { payload = body; } } });
      try {
        root.replaceChildren(editor.build());
        editFields(root.querySelector('.custom-fields'), dom, values);
        const format = root.querySelector('select[aria-label="Format"]');
        for (const next of ['dvd', 'book']) {
          format.value = next;
          format.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
          for (const field of fields) {
            assert.equal(root.querySelector(`[aria-label="${field.label}"]`).value, values[field.key],
              `${field.type} edit must survive a format change while still applicable.`);
          }
        }
        assert.equal(scope.isDirty(), true);
        root.querySelector('.edit-form').dispatchEvent(new dom.window.Event('submit', { bubbles: true, cancelable: true }));
        await flush();
        for (const field of fields) {
          assert.equal(payload.customFields[field.key]?.value ?? null, values[field.key] || null,
            'The saved payload must carry the visible edit, including explicit optional clears.');
          assert.equal(original.customFields[field.key].value, historical[field.key]);
        }
        root.replaceChildren(editor.build());
        assert.equal(root.querySelector('[aria-label="Audience"]').value, historical.audience,
          'A new editor starts from accepted state, not a retired form draft.');
      } finally { editor.dispose(); scope.dispose(); }
    });
  }

  await fixture(async ({ load, get, dom }) => {
    const { createDraftScope } = await load('draft-scope');
    const { createRequestEditor } = await load('request-editor');
    const root = get('#request-dialog-body');
    const scope = createDraftScope();
    const original = { id: '201', version: 'v1', title: 'History', format: 'book',
      customFields: { audience: { label: 'Audience', type: 'text', value: historical.audience } }, capabilities: {} };
    const editor = createRequestEditor({ configuration: { ...configuration,
      formatRules: { book: { customFields: { audience: { mode: 'hidden' } } } } }, announce() {},
      request: async () => ({}), polarisLookup: { close() {}, invalidate() {} },
      context: { request: original, actor, root, scope, isCurrent: () => true, isPending: () => false,
        admit: declaration => scope.admit(declaration).allowed, confirm: () => true, mutate: async () => {} } });
    try {
      root.replaceChildren(editor.build());
      const historicalInput = root.querySelector('[aria-label="Audience"]');
      assert.equal(historicalInput.value, historical.audience);
      assert.equal(historicalInput.disabled, true, 'An unavailable historical value cannot invite an edit the server will ignore.');
      assert.equal(scope.isDirty(), false);
    } finally { editor.dispose(); scope.dispose(); }
  });

  await fixture(async ({ load, get, dom }) => {
    const { createSessionIdentity } = await load('session-identity');
    const { createSuggestionController } = await load('suggestion-controller');
    const identity = createSessionIdentity(); identity.accept(actor);
    const root = get('#staff-suggestion-dialog');
    const options = { root, trigger: get('#new-suggestion'), sessionIdentity: identity,
      polarisLookup: { close() {}, invalidate() {} }, announce() {}, getLibraries: () => [{ id: '2', name: 'A' }],
      beforeOpen: () => true, beforeClose: () => true, openCreatedTitle: async () => null,
      openExistingTitle: async () => false, onReceipt() {}, clearReceipt() {},
      request: async target => target.endsWith('/patron-lookup') ? { status: 'verified', libraryOrgId: 2, patron: {
        patron: { barcode: '20000000000001', name: 'Patron' }, pickupBranches: [{ id: 101, label: 'Main' }],
        currentPreferredPickupBranchId: 101 } } : { libraryOrgId: 2, configuration } };
    let controller = createSuggestionController(options);
    const verify = async () => {
      await controller.open();
      const library = root.querySelector('select'); library.value = '2';
      library.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      root.querySelector('input[type="search"]').value = 'Patron';
      get('#staff-suggestion-form').dispatchEvent(new dom.window.Event('submit', { cancelable: true }));
      await flush(); await flush();
    };
    try {
      await verify();
      editFields(root.querySelector('.custom-fields'), dom, draft);
      const format = root.querySelector('select[aria-label="Material format"]');
      for (const next of ['dvd', 'book']) {
        format.value = next; format.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
        for (const field of fields) assert.equal(root.querySelector(`[aria-label="${field.label}"]`).value, draft[field.key]);
      }
      assert.equal(controller.isDirty(), true, 'Returning to the initial format cannot silently retire custom-field edits.');
      controller.dispose();
      identity.clear(); identity.accept({ ...actor, id: '21', authenticationEmail: 'b@example.org' });
      controller = createSuggestionController(options);
      await verify();
      assert.equal(root.querySelector('[aria-label="Audience"]').value, '', 'Another actor cannot inherit the retired draft.');
    } finally { controller.dispose(); }
  });

  const frontend = path.join(__dirname, '../src/Asap.Web/Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-patron-field-drafts-'));
  const dom = new JSDOM(fs.readFileSync(path.join(frontend, 'patron/index.html'), 'utf8'), { url: 'https://localhost/patron/?libraryOrgId=2' });
  try {
    fs.cpSync(path.join(frontend, 'patron'), path.join(temporary, 'patron'), { recursive: true });
    fs.cpSync(path.join(frontend, 'shared'), path.join(temporary, 'shared'), { recursive: true });
    fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
    Object.assign(global, { window: dom.window, document: dom.window.document, localStorage: dom.window.localStorage,
      sessionStorage: dom.window.sessionStorage, FormData: dom.window.FormData, Option: dom.window.Option, Event: dom.window.Event });
    const config = await import(pathToFileURL(path.join(temporary, 'patron/js/config.js')));
    const formUi = await import(pathToFileURL(path.join(temporary, 'patron/js/form-ui.js')));
    const customFields = await import(pathToFileURL(path.join(temporary, 'patron/js/custom-fields.js')));
    const settings = patronUiText('Draft ownership', { availableFormats: ['book', 'dvd'], additionalFieldDefinitions: fields });
    settings.formatRules.book.customFields = rules; settings.formatRules.dvd.customFields = rules;
    config.applyLoadedUiText(settings); formUi.applyUiConfig(); formUi.bindFormEvents();
    const format = document.getElementById('format');
    for (const field of fields) document.getElementById(`custom-field-${field.key}`).value = draft[field.key];
    format.value = 'dvd'; format.dispatchEvent(new Event('change', { bubbles: true }));
    assert.deepEqual(customFields.collectCustomFieldValues(), draft, 'Patron format changes preserve all still-applicable field types.');
    formUi.resetSuggestionFormUi();
    assert.deepEqual(customFields.collectCustomFieldValues(), { audience: '', description: '', language: '' });
    document.getElementById('custom-field-audience').value = 'Retired actor draft';
    formUi.applyUiConfig();
    assert.equal(document.getElementById('custom-field-audience').value, '', 'Authoritative configuration population remains a reset.');
  } finally {
    dom.window.close();
    const resolved = path.resolve(temporary);
    if (!resolved.startsWith(path.resolve(os.tmpdir()) + path.sep)) throw new Error('Unsafe fixture cleanup');
    fs.rmSync(resolved, { recursive: true, force: true });
  }
  console.log('Request custom-field edits survive format changes within their form owner; unavailable history stays read-only');
})().catch(error => { console.error(error); process.exitCode = 1; });
