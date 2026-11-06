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
      sortOrder: 10,
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
        sortOrder: 20,
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
    root.hidden = false;
    document.getElementById('workspace').hidden = false;
    document.getElementById('settings-form').hidden = false;
    document.getElementById('settings-templates').hidden = false;
    const placeholderModule = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'email-template-placeholders.js')).href);
    const placeholders = placeholderModule.createEmailTemplatePlaceholders({ root, panel: document.getElementById('settings-templates'),
      listen: (target, name, callback, capture) => target.addEventListener(name, callback, capture),
      captureContext: () => 'library/actor/1', isContextCurrent: context => context === 'library/actor/1', isActive: () => true });
    const editors = module.createSettingsDomainEditors({ root, onTemplatesRendered: () => placeholders.refresh() });
    editors.populate(data, false);
    placeholders.populate(placeholderModule.PLACEHOLDERS.map(item => item.key));

    const rows = [...document.querySelectorAll('#email-templates-editor [data-domain-row]')];
    assert.strictEqual(rows.length, 2);
    let lineage = rows.find(row => row.dataset.templateKind === 'lineage');
    assert.strictEqual(lineage.dataset.templateSourceId, systemTemplate.id);
    assert.strictEqual(lineage.querySelector('.template-subject').value, 'System subject');
    assert.strictEqual(lineage.querySelector('.template-body').value, 'Library body');
    assert.strictEqual(lineage.querySelector('.template-subject').disabled, false);
    assert.strictEqual(lineage.querySelector('[data-template-toggle]').getAttribute('aria-expanded'), 'false');
    lineage.querySelector('[data-template-toggle]').click();
    assert.strictEqual(lineage.querySelector('.settings-template-preview-body').textContent, 'Library body');
    const stableLineage = lineage;
    const stableBody = lineage.querySelector('.template-body');
    const savedCustom = rows.find(row => row.dataset.templateKind === 'custom');
    savedCustom.querySelector('[data-template-toggle]').click();
    const customBody = savedCustom.querySelector('.template-body');
    const customTrigger = customBody.closest('.settings-template-field').querySelector('.template-placeholder-trigger');
    customBody.focus(); customBody.setSelectionRange(0, 0); customTrigger.click();
    const popup = document.getElementById(customTrigger.getAttribute('aria-controls'));
    assert.deepStrictEqual([...popup.querySelectorAll('[data-placeholder-key]')].map(button => button.dataset.placeholderKey), ['title']);
    const retiredButton = popup.querySelector('[data-placeholder-key="title"]');
    retiredButton.focus();
    savedCustom.dataset.templateKind = 'lineage';
    retiredButton.click();
    assert.strictEqual(customBody.value, 'Custom body', 'A changed owning row kind invalidates an open picker.');
    savedCustom.dataset.templateKind = 'custom';

    const priorLineage = editors.collect().values.templates.find(item => item.sourceTemplateId === systemTemplate.id);
    document.getElementById('add-email-template').click();
    const newCard = document.querySelector('#email-templates-editor [data-template-id=""]');
    assert.strictEqual(newCard.querySelector('[data-template-toggle]').getAttribute('aria-expanded'), 'true');
    assert.strictEqual(document.activeElement, newCard.querySelector('.template-display-name'));
    assert.strictEqual(document.querySelector('#email-templates-editor [data-template-kind="lineage"]'), stableLineage);
    assert.strictEqual(stableLineage.querySelector('.template-body'), stableBody);
    assert.deepStrictEqual(editors.collect().values.templates.find(item => item.sourceTemplateId === systemTemplate.id), priorLineage,
      'Adding a custom row must not turn untouched inherited fields into explicit library edits.');
    lineage = document.querySelector('#email-templates-editor [data-template-kind="lineage"]');
    assert.strictEqual(lineage.querySelector('[aria-label="Move down"]').disabled, true,
      'A Library content override cannot change System-owned source ordering.');
    savedCustom.querySelector('[aria-label="Move down"]').focus();
    savedCustom.querySelector('[aria-label="Move down"]').click();
    assert.strictEqual(lineage.querySelector('[data-template-toggle]').getAttribute('aria-expanded'), 'true');
    assert.strictEqual(document.activeElement, savedCustom.querySelector('[data-template-toggle]'),
      'Moving to the final row restores focus to the disclosure when Move down becomes disabled.');
    assert.deepStrictEqual(editors.collect().values.templates.find(item => item.sourceTemplateId === systemTemplate.id), priorLineage,
      'Reordering rows must preserve sparse field baselines too.');
    const deleteButton = document.querySelector('#email-templates-editor [data-template-id=""] [aria-label="Delete"]');
    deleteButton.focus(); deleteButton.click();
    assert.strictEqual(document.activeElement, savedCustom.querySelector('[data-template-toggle]'), 'Deleting a focused row restores a surviving neighbor.');
    assert.deepStrictEqual(editors.collect().values.templates.find(item => item.sourceTemplateId === systemTemplate.id), priorLineage,
      'Removing a different row must preserve existing override identity and field intent.');
    lineage = document.querySelector('#email-templates-editor [data-template-kind="lineage"]');

    lineage.querySelector('.settings-template-override').checked = false;
    lineage.querySelector('.settings-template-override').dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.strictEqual(lineage.querySelector('.template-body').value, systemTemplate.body);
    assert.strictEqual(lineage.querySelector('.settings-template-preview-body').textContent, systemTemplate.body);
    assert.strictEqual(lineage.querySelector('.template-placeholder-trigger').disabled, true);
    const reset = editors.collect();
    assert.ok(reset.templates.some(item => item.reset && item.sourceTemplateId === systemTemplate.id));
    assert.strictEqual(reset.templates.find(item => item.reset).sourceTemplateId, systemTemplate.id);
    lineage.querySelector('.settings-template-override').checked = true;
    lineage.querySelector('.settings-template-override').dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.strictEqual(lineage.querySelector('.template-body').value, 'Library body', 'Returning to Override restores the local draft.');
    lineage.querySelector('.template-body').value = '';
    lineage.querySelector('.template-body').dispatchEvent(new dom.window.Event('input', { bubbles: true }));
    assert.strictEqual(lineage.querySelector('.settings-template-preview-body').textContent, systemTemplate.body,
      'An explicit blank library override previews its inherited runtime fallback.');

    editors.populate({ ...data, stored: { ...data.stored, templates: storedTemplates.filter(item => item.id !== '21') } }, false);
    const inherited = document.querySelector('#email-templates-editor [data-template-kind="lineage"]');
    const templateOrder = () => [...document.querySelectorAll('#email-templates-editor [data-domain-row]')]
      .map(row => row.dataset.templateUiId);
    const assertInheritedActions = () => {
      assert.strictEqual(inherited.querySelector('.settings-template-override').checked, false);
      assert.strictEqual(inherited.querySelector('.template-subject').disabled, true);
      assert.strictEqual(inherited.querySelector('[aria-label="Move up"]').disabled, true);
      assert.strictEqual(inherited.querySelector('[aria-label="Move down"]').disabled, true);
      const before = templateOrder();
      inherited.querySelector('[aria-label="Move up"]').click();
      inherited.querySelector('[aria-label="Move down"]').click();
      assert.deepStrictEqual(templateOrder(), before, 'Inherited-off row actions cannot silently change the draft order.');
    };
    assertInheritedActions();
    document.getElementById('add-email-template').click();
    assert.strictEqual(document.querySelector('#email-templates-editor [data-template-kind="lineage"]'), inherited);
    assertInheritedActions();
    const addedInheritedNeighbor = document.querySelector('#email-templates-editor [data-template-id=""]');
    addedInheritedNeighbor.querySelector('[aria-label="Move up"]').click();
    addedInheritedNeighbor.querySelector('[aria-label="Move up"]').click();
    assert.strictEqual([...document.querySelectorAll('#email-templates-editor [data-domain-row]')].indexOf(inherited), 1);
    assertInheritedActions();
    addedInheritedNeighbor.querySelector('[aria-label="Delete"]').click();
    assertInheritedActions();
    assert.strictEqual(editors.collect().values.templates.find(item => item.sourceTemplateId === systemTemplate.id).overridden, false);

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
    const newKey = newRow.dataset.templateKey;
    assert.match(newKey, /^rejection:template_[a-f0-9]{32}$/);
    assert.strictEqual(newRow.querySelector('.template-key'), null, 'Identity has no routine input or accessible control.');
    newRow.querySelector('.template-display-name').value = 'Renamed template';
    assert.ok(systemEditors.collect().templates.some(item => item.templateKey === newKey && item.displayName === 'Renamed template'));
    newRow.querySelector('[data-row-action="delete"]').click();
    document.getElementById('add-email-template').click();
    assert.notStrictEqual(document.querySelector('#email-templates-editor [data-template-id=""]').dataset.templateKey, newKey,
      'A removed System draft does not lend its identity to a replacement.');
    for (const row of document.querySelectorAll('#email-templates-editor [data-template-id]:not([data-template-id=""])')) {
      assert.ok(row.dataset.templateKey);
      assert.strictEqual(row.querySelector('.template-key'), null);
    }
    const casedKeys = collisionRows.map((item, index) => ({ ...item,
      templateKey: index === 0 ? 'REJECTION:TEMPLATE_1' : 'rejection:template_3' }));
    systemEditors.populate({ ...data, orgId: 'system', stored: { ...data.stored,
      configuredSystem: { ...completeSystem, templates: casedKeys }, templates: casedKeys } }, true);
    document.getElementById('add-email-template').click();
    assert.match(document.querySelector('#email-templates-editor [data-template-id=""]').dataset.templateKey, /^rejection:template_[a-f0-9]{32}$/);
    const orderRows = ['A', 'B', 'C', 'D', 'E', 'F'].map((key, index) => ({ ...systemTemplate,
      id: String(9007199254740993n + BigInt(index)), templateKey: `rejection:order_${key}`,
      body: '  Raw line one\r\nRaw line two  ', sortOrder: index < 4 ? 0 : (index - 3) * 100 }));
    const populateOrdered = (values, system = true) => systemEditors.populate({ ...data, orgId: system ? 'system' : '2',
      stored: { ...data.stored, configuredSystem: { ...completeSystem, templates: values.filter(item => item.organizationId === '1') },
        templates: [...values].reverse() } }, system);
    const orderKeys = () => [...document.querySelectorAll('#email-templates-editor [data-domain-row]')]
      .map(row => row.dataset.templateKey);
    const orderRow = key => [...document.querySelectorAll('#email-templates-editor [data-domain-row]')]
      .find(row => row.dataset.templateKey === key);
    populateOrdered([...orderRows].reverse());
    assert.deepStrictEqual(orderKeys(), orderRows.map(item => item.templateKey), 'Tied ranks use exact bigint identities.');
    assert.strictEqual(systemEditors.collect().domainsChanged.templates, false, 'Population does not normalize stored order values.');
    orderRow('rejection:order_C').querySelector('[aria-label="Move up"]').click();
    const movedOrder = ['A', 'C', 'B', 'D', 'E', 'F'].map(key => `rejection:order_${key}`);
    assert.deepStrictEqual(orderKeys(), movedOrder);
    const reordered = systemEditors.collect().values.templates;
    assert.deepStrictEqual(reordered.filter(item => item.sortOrderChanged).map(item => item.templateKey),
      ['rejection:order_A', 'rejection:order_C'], 'An inseparable System tie changes only the necessary surrounding ranks.');
    for (const item of reordered) {
      for (const field of ['subject', 'body', 'displayName', 'enabled']) assert.strictEqual(item[`${field}Changed`], false);
      assert.strictEqual(item.id, orderRows.find(original => original.templateKey === item.templateKey).id);
    }
    const persistedOrder = orderRows.map(item => ({ ...item,
      sortOrder: reordered.find(edited => edited.id === item.id).sortOrder }));
    populateOrdered(persistedOrder);
    assert.deepStrictEqual(orderKeys(), movedOrder, 'The existing rank/ID GET contract reconstructs the saved order.');
    orderRow('rejection:order_B').querySelector('[aria-label="Move up"]').click();
    orderRow('rejection:order_B').querySelector('[aria-label="Move down"]').click();
    assert.strictEqual(systemEditors.collect().domainsChanged.templates, false,
      'Returning to the accepted order clears rank drafts before an unrelated save.');
    orderRow('rejection:order_D').querySelector('[aria-label="Move down"]').click();
    const singleMove = systemEditors.collect().values.templates.filter(item => item.sortOrderChanged);
    assert.deepStrictEqual(singleMove.map(item => [item.templateKey, item.sortOrder]), [['rejection:order_D', 101]],
      'A representable move writes only the moved row’s rank.');

    const fixedA = { ...systemTemplate, id: '9007199254740993', templateKey: 'rejection:fixed_A', sortOrder: null };
    const fixedB = { ...systemTemplate, id: '9007199254740994', templateKey: 'rejection:fixed_B' };
    delete fixedB.sortOrder;
    const tiedCustom = { ...systemTemplate, id: '9007199254740995', organizationId: '2',
      templateKey: 'rejection:tied_custom', isCustom: true, sortOrder: 0 };
    populateOrdered([fixedB, tiedCustom, fixedA], false);
    assert.deepStrictEqual(orderKeys(), ['rejection:fixed_A', 'rejection:fixed_B', 'rejection:tied_custom']);
    const tied = orderRow(tiedCustom.templateKey);
    assert.strictEqual(tied.querySelector('[aria-label="Move up"]').disabled, true,
      'A Library row cannot occupy an unrepresentable position inside fixed inherited ties.');
    assert.match(tied.querySelector('.settings-template-order-help').textContent, /inherited template order/);
    const beforeBlocked = systemEditors.snapshot();
    tied.querySelector('[aria-label="Move up"]').click();
    assert.strictEqual(systemEditors.snapshot(), beforeBlocked);
    assert.strictEqual(systemEditors.collect().domainsChanged.templates, false);
    for (const row of document.querySelectorAll('[data-template-kind="lineage"]')) {
      assert.strictEqual(row.querySelector('.settings-template-override').checked, false);
      assert.strictEqual(row.querySelector('[aria-label="Move up"]').disabled, true);
      assert.strictEqual(row.querySelector('[aria-label="Move down"]').disabled, true);
    }
    populateOrdered([{ ...fixedA, sortOrder: -2147483648 }, { ...fixedB, sortOrder: 2147483647 },
      { ...tiedCustom, sortOrder: 2147483647 }], false);
    orderRow(tiedCustom.templateKey).querySelector('[aria-label="Move up"]').click();
    assert.strictEqual(systemEditors.collect().values.templates.find(item => item.id === tiedCustom.id).sortOrder, 2147483646);
    assert.strictEqual(orderRow(tiedCustom.templateKey).querySelector('[aria-label="Move up"]').disabled, true,
      'Moves respect the existing SQL int limits without changing inherited records.');
    assert.strictEqual(systemEditors.collect().values.templates.some(item => !item.isCustom && item.overridden), false);
    populateOrdered([fixedA, fixedB, tiedCustom], false);
    document.getElementById('add-email-template').click();
    let duplicateCustom = document.querySelector('#email-templates-editor [data-template-id=""]');
    duplicateCustom.dataset.templateKey = tiedCustom.templateKey.toUpperCase();
    assert.throws(() => systemEditors.collect(), /already used/, 'New custom rows cannot upsert another owned record.');
    duplicateCustom.querySelector('[aria-label="Delete"]').click();
    document.getElementById('add-email-template').click();
    duplicateCustom = document.querySelector('#email-templates-editor [data-template-id=""]');
    duplicateCustom.dataset.templateKey = fixedA.templateKey;
    assert.doesNotThrow(() => systemEditors.collect(), 'A Library custom key may share a System source key without claiming its lineage.');
    duplicateCustom.querySelector('[aria-label="Delete"]').click();
    orderRow(tiedCustom.templateKey).querySelector('[aria-label="Delete"]').click();
    document.getElementById('add-email-template').click();
    duplicateCustom = document.querySelector('#email-templates-editor [data-template-id=""]');
    duplicateCustom.dataset.templateKey = tiedCustom.templateKey;
    assert.throws(() => systemEditors.collect(), /already used/, 'A pending delete cannot be combined with an ambiguous same-key create.');
    systemEditors.dispose();

    const provider = { id: '31', key: 'persisted_provider', label: 'Provider', urlTemplate: 'https://example.org/?q={query}',
      isEnabled: true, sortOrder: 10, overridden: false };
    const format = { id: '32', code: 'owned_format', ownerOrganizationId: 2, version: 'format-version', label: 'Owned format',
      isEnabled: true, sortOrder: 10, overridden: true };
    const identityEditors = module.createSettingsDomainEditors({ root: systemRoot });
    identityEditors.populate({ ...data, effective: { ...data.effective, formats: [format] },
      stored: { ...data.stored, formats: [format], configuredSystem: { ...completeSystem, providers: [provider] } } }, false);
    assert.strictEqual(document.querySelector('#material-formats-editor [data-domain-row]').dataset.formatCode, format.code);
    assert.strictEqual(document.querySelector('#material-formats-editor').textContent.includes('Code'), false,
      'Persisted library-owned material-format codes are retained without an editable control.');
    identityEditors.populate({ ...data, orgId: 'system', stored: { ...data.stored,
      configuredSystem: { ...completeSystem, providers: [provider] } },
      effective: { ...data.effective, externalSearchProviders: [provider] } }, true);
    const persistedProviderRow = document.querySelector('#external-search-provider-editor [data-domain-row]');
    assert.strictEqual(persistedProviderRow.dataset.providerKey, 'persisted_provider');
    assert.strictEqual([...persistedProviderRow.querySelectorAll('input')].some(input => input.value === 'persisted_provider'), false,
      'Persisted System provider keys remain internal and cannot be edited.');
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
