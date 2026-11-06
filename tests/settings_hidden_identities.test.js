const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

(async () => {
  const frontend = path.resolve(__dirname, '../src/Asap.Web/Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-hidden-identities-'));
  fs.cpSync(path.join(frontend, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');
  try {
    const { createSettingsDomainEditors } = await import(pathToFileURL(path.join(temporary, 'staff/js/settings-domains.js')));
    const dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff/index.html'), 'utf8'), { url: 'https://localhost/staff/' });
    global.window = dom.window; global.document = dom.window.document; global.Node = dom.window.Node;
    const root = document.getElementById('settings-view');
    const uuid = number => number.toString(16).padStart(32, '0');
    const empty = { exists: false, values: [] };
    const format = { id: '10', code: 'CUSTOM_' + uuid(1), label: 'Configured system label', ownerOrganizationId: 1,
      isEnabled: true, version: 'format-version', overridden: false, customFields: {} };
    const field = { id: '11', key: 'FIELD_' + uuid(1), label: 'Saved field', type: 'select', enabled: true,
      options: [{ id: 'OPTION_' + uuid(1), label: 'Saved selection', enabled: true }] };
    const template = { id: '12', organizationId: '2', templateKey: 'REJECTION:CUSTOM_TEMPLATE_' + uuid(1),
      displayName: 'Saved template', subject: 'Saved {{title}}', body: 'Saved body', enabled: true, isCustom: true, version: 'template-version' };
    const config = { publicationOptions: { exists: true, values: [{ id: 'OPTION_' + uuid(1), label: 'Saved publication', enabled: true }] },
      commonCreators: empty, allowedPatronCodeIds: empty, formats: [format], providers: [], templates: [] };
    const data = { orgId: '2', stored: { configuredSystem: config,
      libraryOverride: { ...config, formats: [], templates: [], publicationOptions: config.publicationOptions },
      formats: [format], providers: [], templates: [template], customFields: [field],
      formatRules: [{ code: format.code, customFields: { [field.key]: { mode: 'required', labelOverride: 'Saved rule' } } }],
      origins: [], autoClaimRules: [] }, effective: { formats: [format], externalSearchProviders: [], customFields: [field] },
      patronCodeChoices: [], autoClaimStaff: [] };
    const editors = createSettingsDomainEditors({ root });
    const click = id => root.querySelector('#' + id).click();
    const last = id => root.querySelector('#' + id).lastElementChild;
    const change = (control, value) => { control.value = value; control.dispatchEvent(new window.Event('input', { bubbles: true })); };
    let sequence = 2, forced = [];
    Object.defineProperty(window.crypto, 'randomUUID', { value: () => forced.length ? forced.shift() : uuid(sequence++), configurable: true });
    editors.populate(data, false);
    assert.equal(editors.collect().changed, false);
    const accepted = editors.collect().values;
    assert.equal(accepted.formats[0].code, format.code);
    assert.equal(accepted.fields[0].key, field.key);
    assert.equal(accepted.fields[0].options[0].id, field.options[0].id);
    assert.equal(accepted.templates[0].templateKey, template.templateKey);
    for (const text of ['Stable ID', 'Stable key', 'Option stable ID', 'Template key']) assert.ok(!root.textContent.includes(text));
    assert.equal(root.querySelector('#material-formats-editor').textContent.includes('Code'), false);
    assert.equal(root.querySelector('.template-key'), null);

    for (const [add, editor, attribute, selector, prefix] of [
      ['add-publication-option', 'publication-options-editor', 'optionId', '[data-publication-field="label"]', 'option_'],
      ['add-material-format', 'material-formats-editor', 'formatCode', '[data-material-field="label"]', 'custom_'],
      ['add-custom-field', 'additional-fields-editor', 'fieldKey', '[data-custom-field="label"]', 'field_'],
      ['add-email-template', 'email-templates-editor', 'templateKey', '.template-display-name', 'rejection:custom_template_']
    ]) {
      forced = [uuid(1), uuid(sequence++)]; // Collide with an accepted differently-cased identity first.
      click(add);
      let row = last(editor);
      const key = row.dataset[attribute];
      assert.ok(key.startsWith(prefix) && key.length <= 100);
      assert.notEqual(key.toLowerCase(), prefix + uuid(1));
      change(row.querySelector(selector), 'Identical label');
      row.querySelector('[data-row-action="up"]').click();
      row = [...root.querySelectorAll('#' + editor + ' [data-domain-row]')].find(item => item.dataset[attribute] === key);
      assert.equal(row.querySelector(selector).value, 'Identical label');
      const firstRetry = editors.collect();
      assert.deepEqual(editors.collect(), firstRetry, 'Repeated collection/retry retains every allocated identity.');
      row.querySelector('[data-row-action="delete"]').click();
      forced = [key.slice(prefix.length), uuid(sequence++)]; // Deleted draft allocations stay reserved too.
      click(add);
      row = last(editor);
      assert.notEqual(row.dataset[attribute], key);
      change(row.querySelector(selector), 'Identical label');
      const replacementKey = row.dataset[attribute];
      click(add);
      change(last(editor).querySelector(selector), 'Identical label');
      assert.notEqual(last(editor).dataset[attribute], replacementKey, 'Identical labels have distinct identities.');
    }

    const definition = [...root.querySelectorAll('#additional-fields-editor [data-domain-row]')].find(row => row.dataset.fieldKey === field.key);
    forced = [uuid(1), uuid(sequence++)];
    definition.querySelector('.secondary-button').click();
    const optionRows = () => [...root.querySelectorAll('[data-field-key="' + field.key + '"] [data-option-row]')];
    const optionKey = optionRows().at(-1).dataset.optionId;
    assert.notEqual(optionKey.toLowerCase(), field.options[0].id.toLowerCase());
    change(optionRows().at(-1).querySelector('[data-option-field="label"]'), 'Renamed selection');
    const selections = editors.collect().values.fields.find(item => item.key === field.key).options;
    assert.equal(selections[0].id, field.options[0].id, 'Existing patron selections retain their stored option value.');
    assert.equal(selections.at(-1).id, optionKey);
    optionRows().at(-1).querySelector('button').click();
    forced = [optionKey.slice('option_'.length), uuid(sequence++)];
    root.querySelector('[data-field-key="' + field.key + '"] .secondary-button').click();
    assert.notEqual(optionRows().at(-1).dataset.optionId, optionKey);
    assert.deepEqual(editors.collect().values.rules[0].customFields[field.key], { mode: 'required', labelOverride: 'Saved rule' });

    root.querySelector('#publication-options-inherit-field .settings-use-system').click();
    await Promise.resolve();
    assert.equal(editors.collect().values.publicationUseSystem, true);
    assert.equal(editors.collect().values.publication[0].id, config.publicationOptions.values[0].id);
    root.querySelector('#add-publication-option').click();
    assert.equal(editors.collect().values.publicationUseSystem, false);
    assert.equal(editors.collect().values.publication[0].id, config.publicationOptions.values[0].id);
    editors.populate(data, false); // Global Discard/reload uses the accepted snapshot.
    assert.equal(editors.collect().changed, false);
    assert.equal(editors.collect().values.templates[0].templateKey, template.templateKey);
    for (const selector of ['[data-publication-field="enabled"]', '[data-material-field="label"]',
      '[data-custom-field="type"]', '[data-option-field="label"]', '.template-display-name']) {
      editors.populate(data, false);
      root.querySelector(selector).remove();
      assert.throws(() => editors.collect(), /incomplete/, 'Missing explicit controls cannot silently shift/default serialized fields.');
    }
    editors.dispose();
    dom.window.close();
    console.log('Hidden editor identities, forced collisions, stable retries, rename/reorder, delete/re-add and reset/discard passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
