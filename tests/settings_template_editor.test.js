const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-templates-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const module = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings-domains.js')).href);
    const html = fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8');
    const dom = new JSDOM(html, { url: 'http://localhost/staff/' });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Node = dom.window.Node;

    const systemTemplate = {
      id: '9007199254740993',
      organizationId: '1',
      templateKey: 'rejection:system-long',
      displayName: 'System rejection',
      subject: 'System subject',
      body: 'System body',
      enabled: true,
      isCustom: false,
      version: 'system-version'
    };
    const emptySet = { exists: false, values: [] };
    const completeSystem = {
      publicationOptions: emptySet,
      commonCreators: emptySet,
      allowedPatronCodeIds: emptySet,
      providers: [],
      formats: [],
      templates: [systemTemplate]
    };
    const storedTemplates = [
      systemTemplate,
      {
        id: '21',
        organizationId: '2',
        sourceTemplateId: systemTemplate.id,
        templateKey: systemTemplate.templateKey,
        displayName: null,
        subject: null,
        body: 'Library body',
        enabled: true,
        isCustom: false,
        version: 'library-version'
      },
      {
        id: '22',
        organizationId: '2',
        templateKey: 'rejection:library-custom',
        displayName: 'Library custom',
        subject: 'Custom subject',
        body: 'Custom body',
        enabled: true,
        isCustom: true,
        version: 'custom-version'
      }
    ];
    const data = {
      orgId: '2',
      stored: {
        configuredSystem: completeSystem,
        libraryOverride: {
          publicationOptions: emptySet,
          commonCreators: emptySet,
          allowedPatronCodeIds: emptySet,
          providers: [],
          formats: [],
          templates: []
        },
        origins: [],
        providers: [],
        formats: [],
        customFields: [],
        formatRules: [],
        autoClaimRules: [],
        publicationOptions: [],
        commonCreators: [],
        allowedPatronCodeIds: [],
        templates: storedTemplates
      },
      effective: { formats: [], externalSearchProviders: [], customFields: [] },
      patronCodeChoices: [],
      autoClaimStaff: []
    };
    const root = document.getElementById('settings-view');
    const editors = module.createSettingsDomainEditors({ root });
    editors.populate(data, false);

    const rows = [...document.querySelectorAll('#email-templates-editor [data-domain-row]')];
    assert.strictEqual(rows.length, 2);
    let lineage = rows.find(row => row.dataset.templateKind === 'lineage');
    assert.strictEqual(lineage.dataset.templateSourceId, systemTemplate.id);
    assert.strictEqual(lineage.querySelector('.template-subject').value, 'System subject');
    assert.strictEqual(lineage.querySelector('.template-body').value, 'Library body');
    assert.strictEqual(lineage.querySelector('.template-subject').disabled, false);

    const priorLineage = editors.collect().values.templates.find(item => item.sourceTemplateId === systemTemplate.id);
    document.getElementById('add-email-template').click();
    assert.deepStrictEqual(editors.collect().values.templates.find(item => item.sourceTemplateId === systemTemplate.id), priorLineage,
      'Adding a custom row must not turn untouched inherited fields into explicit library edits.');
    lineage = document.querySelector('#email-templates-editor [data-template-kind="lineage"]');
    lineage.querySelector('[aria-label="Move down"]').click();
    assert.deepStrictEqual(editors.collect().values.templates.find(item => item.sourceTemplateId === systemTemplate.id), priorLineage,
      'Reordering rows must preserve sparse field baselines too.');
    document.querySelector('#email-templates-editor [data-template-id=""] [aria-label="Delete"]').click();
    assert.deepStrictEqual(editors.collect().values.templates.find(item => item.sourceTemplateId === systemTemplate.id), priorLineage,
      'Removing a different row must preserve existing override identity and field intent.');
    lineage = document.querySelector('#email-templates-editor [data-template-kind="lineage"]');

    lineage.querySelector('.settings-template-override').checked = false;
    lineage.querySelector('.settings-template-override').dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    const reset = editors.collect();
    assert.ok(reset.templates.some(item => item.reset && item.sourceTemplateId === systemTemplate.id));
    assert.strictEqual(reset.templates.find(item => item.reset).sourceTemplateId, systemTemplate.id);

    const overriddenData = { ...data, stored: { ...data.stored, templates: storedTemplates.map(item =>
      item.id === '21' ? { ...item, displayName: 'Library rejection', subject: 'Library subject',
        body: 'Library body', enabled: false } : item) } };
    for (const property of ['subject', 'body', 'displayName', 'enabled']) {
      editors.populate(overriddenData, false);
      let current = document.querySelector('#email-templates-editor [data-template-kind="lineage"]');
      const controlClass = { subject: 'subject', body: 'body', displayName: 'display-name', enabled: 'enabled' }[property];
      const control = current.querySelector(`.template-${controlClass}`);
      if (property === 'enabled') control.checked = systemTemplate.enabled;
      else control.value = systemTemplate[property];
      control.dispatchEvent(new dom.window.Event(property === 'enabled' ? 'change' : 'input', { bubbles: true }));
      document.getElementById('add-email-template').click();
      document.querySelector('#email-templates-editor [data-template-id=""] [aria-label="Delete"]').click();
      const edited = editors.collect().templates.find(item => item.sourceTemplateId === systemTemplate.id);
      assert.strictEqual(edited[property], systemTemplate[property]);
      for (const field of ['subject', 'body', 'displayName', 'enabled']) {
        assert.strictEqual(edited[`${field}Changed`], field === property,
          `Returning ${property} to its system value must submit that edit alone after row rebuilds.`);
      }
    }

    const systemRoot = document.getElementById('settings-view');
    editors.dispose();
    const systemEditors = module.createSettingsDomainEditors({ root: systemRoot });
    systemEditors.populate({
      orgId: 'system',
      stored: {
        configuredSystem: completeSystem,
        libraryOverride: {},
        origins: [],
        providers: [],
        formats: [],
        customFields: [],
        formatRules: [],
        autoClaimRules: [],
        templates: [systemTemplate]
      },
      effective: { formats: [], externalSearchProviders: [], customFields: [] },
      patronCodeChoices: [],
      autoClaimStaff: []
    }, true);
    assert.strictEqual(document.querySelectorAll('#email-templates-editor [data-domain-row]').length, 1);
    assert.strictEqual(document.getElementById('add-email-template').hidden, false);
    document.getElementById('add-email-template').click();
    assert.ok(systemEditors.collect().templates.some(item => item.templateKey.startsWith('rejection:')));

    const collisionRows = ['rejection:template_1', 'rejection:template_3'].map((templateKey, index) => ({
      ...systemTemplate, id: String(100 + index), templateKey
    }));
    systemEditors.populate({ ...data, orgId: 'system', stored: { ...data.stored,
      configuredSystem: { ...completeSystem, templates: collisionRows }, templates: collisionRows } }, true);
    document.getElementById('add-email-template').click();
    const newRow = document.querySelector('#email-templates-editor [data-template-id=""]');
    const newKey = newRow.querySelector('.template-key');
    assert.strictEqual(newKey.value, 'rejection:template_2', 'Add must choose an unused key from nonsequential saved identities.');
    assert.strictEqual(newKey.readOnly, false);
    assert.strictEqual(newKey.hasAttribute('readonly'), false, 'Boolean false must not create a readonly attribute.');
    newKey.value = 'rejection:editable_new_key';
    assert.ok(systemEditors.collect().templates.some(item => item.templateKey === newKey.value));
    for (const row of document.querySelectorAll('#email-templates-editor [data-template-id]:not([data-template-id=""])')) {
      assert.strictEqual(row.querySelector('.template-key').readOnly, true);
    }
    const casedKeys = collisionRows.map((item, index) => ({ ...item,
      templateKey: index === 0 ? 'REJECTION:TEMPLATE_1' : 'rejection:template_3' }));
    systemEditors.populate({ ...data, orgId: 'system', stored: { ...data.stored,
      configuredSystem: { ...completeSystem, templates: casedKeys }, templates: casedKeys } }, true);
    document.getElementById('add-email-template').click();
    assert.strictEqual(document.querySelector('#email-templates-editor [data-template-id=""] .template-key').value, 'rejection:template_2');
    systemEditors.dispose();

    const provider = { id: '31', key: 'persisted_provider', label: 'Provider', urlTemplate: 'https://example.org/?q={query}',
      isEnabled: true, sortOrder: 10, overridden: false };
    const format = { id: '32', code: 'owned_format', ownerOrganizationId: 2, version: 'format-version', label: 'Owned format',
      isEnabled: true, sortOrder: 10, overridden: true };
    const identityEditors = module.createSettingsDomainEditors({ root: systemRoot });
    identityEditors.populate({ ...data, effective: { ...data.effective, formats: [format] },
      stored: { ...data.stored, formats: [format], configuredSystem: { ...completeSystem, providers: [provider] } } }, false);
    assert.strictEqual(document.querySelector('#material-formats-editor input').readOnly, true,
      'Persisted library-owned material-format codes are immutable too.');
    identityEditors.populate({ ...data, orgId: 'system', stored: { ...data.stored,
      configuredSystem: { ...completeSystem, providers: [provider] } },
      effective: { ...data.effective, externalSearchProviders: [provider] } }, true);
    assert.strictEqual(document.querySelector('#external-search-provider-editor input').readOnly, true,
      'Persisted System provider keys must remain immutable after Boolean construction is fixed.');
    document.getElementById('add-provider').click();
    assert.strictEqual([...document.querySelectorAll('#external-search-provider-editor [data-domain-row]')].at(-1).querySelector('input').readOnly, false);
    identityEditors.dispose();

    dom.window.close();
    console.log('Settings template lineage, hiding/reset, system rejection, and bigint identity controls passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
