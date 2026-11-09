const assert = require('node:assert/strict');
const { fixture } = require('./helpers/staff-controller.cjs');

fixture(async ({ load }) => {
  let editors;
  try {
    const { createSettingsDomainEditors } = await load('settings-domains');
    const root = document.getElementById('settings-view'); root.hidden = false;
    document.getElementById('common-authors-options').hidden = false;
    document.getElementById('common-authors-options').disabled = false;
    const set = values => ({ exists: true, values });
    const absent = { exists: false, values: [] };
    const systemFormat = { id: '10', ownerOrganizationId: '1', code: 'book', label: 'Book', isEnabled: true,
      sortOrder: 0, version: 'system-version', overridden: false, customFields: {}, messageBehavior: 'message', message: 'System message' };
    const owned = { ...systemFormat, id: '11', code: 'custom', ownerOrganizationId: '2', label: 'Owned format', version: 'owned' };
    const provider = { id: '20', key: 'catalog', label: 'System catalog', urlTemplate: 'https://catalog.example.org?q={query}', isEnabled: true, sortOrder: 0 };
    const field = { id: '30', key: 'owned_field', label: 'Owned field', type: 'text', enabled: true, options: [] };
    const source = { id: '40', organizationId: '1', templateKey: 'rejection:source', displayName: 'System rejection',
      subject: 'System {{title}}', body: 'System body', enabled: true, isCustom: false, version: 'source', sortOrder: 0 };
    const lineage = { ...source, id: '41', organizationId: '2', sourceTemplateId: '40', displayName: null,
      subject: null, body: 'Library\r\nbody', version: 'lineage' };
    const customTemplate = { ...source, id: '42', organizationId: '2', templateKey: 'rejection:owned', isCustom: true, sourceTemplateId: null };
    const configured = { formats: [systemFormat], providers: [provider], templates: [source],
      publicationOptions: set([{ id: 'historical', label: 'Any year', enabled: true, sortOrder: 0 }]),
      commonCreators: set(['System creator']), allowedPatronCodeIds: set([1]) };
    const override = { formats: [{ kind: 'systemOverride', id: '12', code: null, materialFormatId: '10', ownerOrganizationId: '1',
      label: 'Book', sortOrder: null, isEnabled: false, messageBehavior: null, message: '', authorLabel: '  ', version: 'library-version' }, owned],
      providers: [{ id: '20', kind: 'libraryOverride', label: '', isEnabled: false, urlTemplate: null, version: 'provider-version' }],
      templates: [lineage, customTemplate], publicationOptions: set([]), commonCreators: absent, allowedPatronCodeIds: absent };
    const effectiveFormat = { ...systemFormat, label: 'Book', isEnabled: false, overridden: true, version: 'library-version', message: '',
      customFields: { owned_field: { mode: 'required', labelOverride: 'Owned rule' } } };
    const data = { orgId: '2', stored: { configuredSystem: configured, libraryOverride: override, formats: [effectiveFormat, owned],
      providers: [{ ...provider, label: '', isEnabled: false, overridden: true }], templates: [source, lineage, customTemplate],
      customFields: [field], formatRules: [{ code: 'book', customFields: effectiveFormat.customFields }], origins: [], autoClaimRules: [] },
      effective: { formats: [effectiveFormat, owned], externalSearchProviders: [{ ...provider, label: '', isEnabled: false, overridden: true }], customFields: [field] },
      patronCodeChoices: [{ id: 1, description: 'Adult' }, { id: 2, description: 'Child' }], autoClaimStaff: [] };
    editors = createSettingsDomainEditors({ root });
    const edit = (control, value) => { if (control.type === 'checkbox') control.checked = value; else control.value = value;
      control.dispatchEvent(new window.Event('input', { bubbles: true })); };
    const providerRow = () => root.querySelector('[data-provider-id="20"]');
    const material = () => root.querySelector('[data-format-code="book"]');
    const rule = () => root.querySelector('[data-rule-code="book"]');
    const template = () => root.querySelector('[data-template-source-id="40"]');
    editors.populate(data, false);
    assert.equal(editors.collect().changed, false);
    const accepted = editors.snapshot();
    providerRow().querySelector('input').focus();
    assert.equal(editors.snapshot(), accepted, 'Focus does not pin an inherited nullable column.');
    assert.equal(root.querySelectorAll('.settings-domain-override-toggle, .settings-template-override, [id$="-use-system"]').length, 0);
    assert.match(material().textContent, /Library setting/);
    assert.match(root.querySelector('#common-creators-inherit-field').textContent, /Using system value/);
    assert.match(root.querySelector('#publication-options-inherit-field').textContent, /Library setting/);
    assert.equal(root.querySelector('#add-publication-option').disabled, false);
    edit(providerRow().querySelector('input'), 'temporary'); edit(providerRow().querySelector('input'), '');
    assert.deepEqual(editors.collect().writes.providers, [{ id: '20', key: 'catalog', pinOverrides: true, label: '' }],
      'Explicit blank pins only the edited nullable column; false and null siblings stay untouched.');
    edit(rule().querySelector('[data-format-field="author"]'), 'required');
    edit(rule().querySelector('[data-format-field="author"]'), 'optional');
    const postedRule = editors.collect().writes.rules.find(value => value.code === 'book');
    assert.deepEqual(postedRule.author, { mode: 'optional' });
    assert.equal(Object.hasOwn(postedRule, 'message'), false, 'Untouched meaningful blank is never rewritten.');
    assert.deepEqual(postedRule.customFields, effectiveFormat.customFields);
    assert.match(rule().querySelector('[data-rule-inheritance="author.mode"]').textContent, /Library setting · Matches system · Unsaved/);
    edit(template().querySelector('.template-subject'), 'Draft subject');
    const editedTemplate = editors.collect().templates.find(value => value.sourceTemplateId === '40');
    assert.equal(editedTemplate.subjectChanged, true); assert.equal(editedTemplate.bodyChanged, false);
    assert.equal(editedTemplate.body, 'Library\nbody', 'Native newline display does not become write intent.');
    template().querySelector('[data-template-status="subject"] .settings-use-system').click();
    assert.equal(editors.collect().templates.find(value => value.sourceTemplateId === '40').fieldIntents.subject, 'reset');
    assert.equal(template().querySelector('.template-subject').value, source.subject);
    rule().querySelector('[data-rule-inheritance="author.mode"] .settings-use-system').click();
    assert.deepEqual(editors.collect().writes.rules.find(value => value.code === 'book').author, { mode: null });
    material().querySelector(':scope > .settings-inheritance .settings-use-system').click();
    assert.equal(editors.collect().writes.formats.find(value => value.id === '10').reset, true);
    assert.deepEqual(editors.collect().writes.rules.find(value => value.code === 'book').customFields, effectiveFormat.customFields);
    assert.equal(editors.collect().values.formats.find(value => value.id === '11').label, owned.label);
    root.querySelector('#add-common-creator').click();
    assert.equal(editors.collect().values.creatorsUseSystem, false);
    root.querySelector('#common-creators-inherit-field .settings-use-system').click(); await Promise.resolve();
    assert.equal(editors.collect().values.creatorsUseSystem, true);
    assert.deepEqual(editors.collect().values.creators, ['System creator']);
    assert.equal(editors.collect().values.templates.find(value => value.id === '42').templateKey, customTemplate.templateKey);
    const retiredReset = providerRow().querySelector('.settings-use-system');
    editors.populate(data, false);
    assert.equal(editors.collect().changed, false, 'Discard replaces pin/reset intent with accepted raw ownership.');
    retiredReset.click(); assert.equal(editors.collect().changed, false, 'A retained reset from a retired render cannot alter accepted state.');
    const missingLineage = { ...data, stored: { ...data.stored, templates: [source, customTemplate],
      libraryOverride: { ...override, templates: [customTemplate] } } };
    editors.populate(missingLineage, false);
    edit(template().querySelector('.template-subject'), 'Temporary inherited subject');
    template().querySelector('[data-template-status="subject"] .settings-use-system').click();
    assert.match(template().querySelector('[data-template-item-status]').textContent, /Using system value · Unsaved/,
      'Reset-only field intent on an inherited template does not own a new lineage draft.');
    assert.equal(editors.collect().templates.find(value => value.sourceTemplateId === '40').overridden, false);
    edit(template().querySelector('.template-body'), 'Genuine sibling pin');
    assert.match(template().querySelector('[data-template-item-status]').textContent, /Library setting/);
    assert.equal(editors.collect().templates.find(value => value.sourceTemplateId === '40').overridden, true);
    assert.deepEqual(override.providers[0], { id: '20', kind: 'libraryOverride', label: '', isEnabled: false, urlTemplate: null, version: 'provider-version' });
    editors.populate({ ...data, stored: { ...data.stored, libraryOverride: { ...override,
      publicationOptions: set([{ id: 'different-library-choice', label: 'Any year', enabled: true, sortOrder: 0 }]) } } }, false);
    assert.doesNotMatch(root.querySelector('#publication-options-inherit-field').textContent, /Matches system/,
      'Equal visible labels with different public selection IDs are different whole-list values.');
    const creatorStatus = () => root.querySelector('#common-creators-inherit-field');
    const emptyCreators = { ...data, stored: { ...data.stored,
      libraryOverride: { ...override, commonCreators: set([]) } } };
    editors.populate(emptyCreators, false);
    const emptyAccepted = JSON.parse(editors.snapshot()).creators;
    assert.equal(editors.collect().changed, false);
    assert.deepEqual(editors.collect().values.creators, []);
    assert.equal(editors.collect().values.creatorsUseSystem, false);
    assert.match(creatorStatus().textContent, /Library setting/);
    assert.equal(creatorStatus().querySelectorAll('.settings-use-system').length, 1,
      'Reloading a saved empty creator override retains its ownership and reset action.');
    creatorStatus().querySelector('.settings-use-system').click(); await Promise.resolve();
    assert.equal(editors.collect().changed, true);
    assert.equal(editors.collect().values.creatorsUseSystem, true);
    assert.deepEqual(editors.collect().values.creators, ['System creator']);
    assert.match(creatorStatus().textContent, /Using system value · Unsaved/);
    assert.equal(editors.collect().values.formats.find(value => value.id === owned.id).label, owned.label);
    assert.equal(editors.collect().values.templates.find(value => value.id === customTemplate.id).templateKey, customTemplate.templateKey);
    editors.populate(emptyCreators, false);
    assert.deepEqual(JSON.parse(editors.snapshot()).creators, emptyAccepted, 'Discard restores the accepted explicit empty override.');
    assert.equal(editors.collect().changed, false);
    const lastCreator = { ...emptyCreators, stored: { ...emptyCreators.stored,
      configuredSystem: { ...configured, commonCreators: set([]) },
      libraryOverride: { ...override, commonCreators: set(['Library only']) } } };
    editors.populate(lastCreator, false);
    const lastAccepted = JSON.parse(editors.snapshot()).creators;
    root.querySelector('#common-creators-editor [data-row-action="delete"]').click();
    assert.deepEqual(editors.collect().values.creators, []);
    assert.equal(editors.collect().values.creatorsUseSystem, false);
    assert.match(creatorStatus().textContent, /Library setting · Matches system · Unsaved/,
      'Deleting the last creator refreshes the explicit empty draft comparison.');
    assert.equal(document.activeElement, root.querySelector('#add-common-creator'));
    creatorStatus().querySelector('.settings-use-system').click(); await Promise.resolve();
    assert.equal(editors.collect().values.creatorsUseSystem, true);
    assert.deepEqual(editors.collect().values.creators, []);
    assert.match(creatorStatus().textContent, /Using system value · Unsaved/);
    editors.populate(lastCreator, false);
    assert.deepEqual(JSON.parse(editors.snapshot()).creators, lastAccepted, 'Discard restores the last accepted creator after an empty reset draft.');
    assert.equal(editors.collect().changed, false);
    const longReference = 'System reference '.repeat(12);
    editors.populate({ ...data, stored: { ...data.stored,
      configuredSystem: { ...configured, formats: [{ ...systemFormat, message: longReference }],
        providers: [{ ...provider, urlTemplate: provider.urlTemplate + longReference.replaceAll(' ', '') }],
        templates: [{ ...source, body: longReference }] },
      templates: [{ ...source, body: longReference }, lineage, customTemplate] } }, false);
    const providerUrl = providerRow().querySelector('input[type="url"]');
    edit(providerUrl, 'https://library.example.org?q={query}');
    for (const [control, status] of [
      [providerUrl, () => providerRow().querySelector('[data-provider-status="urlTemplate"]')],
      [rule().querySelector('[data-rule-property="message"]'), () => rule().querySelector('[data-rule-inheritance="message"]')],
      [template().querySelector('.template-body'), () => template().querySelector('[data-template-status="body"]')]
    ]) {
      const beforeExpansion = editors.snapshot();
      status().querySelector('details').open = true; control.focus();
      assert.equal(editors.snapshot(), beforeExpansion, 'Comparison expansion and focus do not change draft ownership.');
      edit(control, control.value + 'x');
      assert.equal(status().querySelector('details').open, true, 'Dynamic field comparisons stay expanded while typing.');
      assert.equal(document.activeElement, control);
    }
    const systemProvider = { ...provider, label: null, urlTemplate: null, isEnabled: false, overridden: false };
    const systemData = { ...data, orgId: 'system', stored: { ...data.stored,
      configuredSystem: { ...configured, providers: [systemProvider] }, libraryOverride: null,
      providers: [systemProvider], formats: [systemFormat], customFields: [], formatRules: [], templates: [source] },
      effective: { formats: [systemFormat], externalSearchProviders: [systemProvider], customFields: [] } };
    editors.populate(systemData, true);
    root.querySelector('#add-provider').click();
    const newProvider = root.querySelector('[data-provider-id=""]');
    edit(newProvider.querySelector('input[type="text"]'), 'Added provider');
    edit(newProvider.querySelector('input[type="url"]'), 'https://catalog.example.org/new?q={query}');
    const systemWrites = editors.collect().writes;
    assert.deepEqual(systemWrites.providers.find(value => value.id === '20'), { id: '20', key: provider.key },
      'Adding a System provider omits untouched raw nullable display text.');
    assert.deepEqual(systemWrites.rules, [{ code: 'book' }], 'Unchanged effective rule defaults are never materialized.');
    console.log('Direct scalar-independent inheritance: nullable pins, per-field templates/rules, group resets, owned records, discard and retired targets passed');
  } finally { editors?.dispose(); }
}).catch(error => { console.error(error); process.exitCode = 1; });
