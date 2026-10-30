const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');
const axe = require('axe-core');

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-settings-domains-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const module = await import(pathToFileURL(path.join(temporary, 'staff', 'js', 'settings-domains.js')).href);
    const html = fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8');
    const dom = new JSDOM(html, { url: 'http://localhost/staff/' });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Node = dom.window.Node;

    const systemFormats = [
      {
        id: '7',
        code: 'book',
        ownerOrganizationId: 1,
        overridden: false,
        version: 'AAAAAAAAAAE=',
        label: 'Book',
        isEnabled: true,
        messageBehavior: 'none',
        fields: {},
        customFields: { pickup_location: { mode: 'required', labelOverride: 'Pickup branch' } }
      }
    ];
    const data = {
      orgId: '2',
      stored: {
        configuredSystem: {
          publicationOptions: {
            exists: true,
            values: [
              { id: 'stable-a', label: 'Coming soon', enabled: true },
              { id: 'stable-b', label: 'On order', enabled: false }
            ]
          },
          commonCreators: { exists: true, values: ['System creator'] },
          allowedPatronCodeIds: { exists: true, values: [2] },
          providers: [],
          formats: systemFormats,
          templates: []
        },
        libraryOverride: {
          formats: [],
          providers: [],
          templates: [],
          publicationOptions: {
            exists: true,
            values: [
              { id: 'library-a', label: 'Library option', enabled: true },
              { id: 'library-b', label: 'Library second option', enabled: true }
            ]
          },
          commonCreators: { exists: false, values: [] },
          allowedPatronCodeIds: { exists: false, values: [] }
        },
        customFields: [
          {
            id: '10', key: 'retired_type', type: 'select', label: 'Retired type', helpText: null,
            enabled: false, sortOrder: 10,
            options: [{ id: 'old-option', label: 'Old option', enabled: false, sortOrder: 10 }]
          },
          {
            id: '11', key: 'pickup_location', type: 'select', label: 'Pickup location', helpText: null,
            enabled: true, sortOrder: 20,
            options: [{ id: 'main', label: 'Main branch', enabled: true, sortOrder: 10 }]
          }
        ],
        formatRules: [{
          code: 'book',
          customFields: {
            retired_type: { mode: 'optional', labelOverride: 'Historical field label' },
            pickup_location: { mode: 'required', labelOverride: 'Pickup branch' }
          }
        }],
        origins: [],
        providers: [],
        formats: systemFormats,
        autoClaimRules: [],
        templates: []
      },
      effective: {
        externalSearchProviders: [],
        formats: systemFormats,
        customFields: []
      },
      patronCodeChoices: [{ id: 2, description: 'Adult' }],
      autoClaimStaff: []
    };

    const root = document.getElementById('settings-view');
    const changes = [];
    const editors = module.createSettingsDomainEditors({
      root,
      onChange: () => changes.push(true)
    });
    editors.populate(data, false);

    const rule = document.querySelector('#format-rules-editor [data-rule-code="book"]');
    const behavior = rule.querySelector('[data-rule-property="messageBehavior"]');
    const message = rule.querySelector('[data-rule-property="message"]');
    assert.strictEqual(message.closest('label').hidden, true);
    assert.strictEqual(message.disabled, true);
    assert.strictEqual(message.required, false);
    behavior.value = 'message';
    behavior.dispatchEvent(new window.Event('change', { bubbles: true }));
    assert.strictEqual(message.closest('label').hidden, false);
    assert.strictEqual(message.disabled, false);
    message.value = 'Draft & é retained';
    behavior.value = 'none';
    behavior.dispatchEvent(new window.Event('change', { bubbles: true }));
    assert.strictEqual(message.closest('label').hidden, true);
    assert.strictEqual(editors.collect().values.rules.find(item => item.code === 'book').message, 'Draft & é retained');
    behavior.value = 'ebookMessage';
    behavior.dispatchEvent(new window.Event('change', { bubbles: true }));
    assert.strictEqual(message.value, 'Draft & é retained');
    assert.strictEqual(message.closest('label').hidden, false);
    // Restore the fixture draft before the existing persistence assertions.
    behavior.value = 'none';
    behavior.dispatchEvent(new window.Event('change', { bubbles: true }));
    message.value = '';

    assert.strictEqual(root.querySelectorAll('textarea[data-settings-json]').length, 0);
    assert.strictEqual(root.querySelector('[data-setting-key="postmarkToken"]').dataset.systemOnly, 'true');
    assert.strictEqual(document.getElementById('publication-options-use-system').checked, false);
    assert.strictEqual(document.querySelectorAll('#publication-options-editor [data-domain-row]').length, 2);
    assert.strictEqual(document.getElementById('add-publication-option').disabled, false);
    assert.strictEqual(document.querySelector('#publication-options-editor [data-domain-editable]').disabled, false);

    const publicationUseSystem = document.getElementById('publication-options-use-system');
    publicationUseSystem.checked = true;
    publicationUseSystem.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.strictEqual(document.querySelector('#publication-options-editor [data-domain-row] input').value, 'stable-a');
    assert.strictEqual(document.getElementById('add-publication-option').disabled, true);
    assert.strictEqual(document.querySelector('#publication-options-editor [data-domain-editable]').disabled, true);
    assert.ok([...document.querySelectorAll('#publication-options-editor .settings-row-actions button')].every(button => button.disabled));

    publicationUseSystem.checked = false;
    publicationUseSystem.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.strictEqual(document.querySelector('#publication-options-editor [data-domain-row] input').value, 'library-a');
    assert.strictEqual(document.getElementById('add-publication-option').disabled, false);
    assert.strictEqual(document.querySelector('#publication-options-editor [data-domain-editable]').disabled, false);
    assert.ok([...document.querySelectorAll('#publication-options-editor .settings-row-actions button')].some(button => !button.disabled));

    const firstPublicationId = document.querySelector('#publication-options-editor [data-domain-row] input').value;
    document.querySelector('#publication-options-editor .settings-row-actions button[aria-label="Move down"]').click();
    assert.notStrictEqual(
      document.querySelector('#publication-options-editor [data-domain-row] input').value,
      firstPublicationId
    );
    document.getElementById('add-publication-option').click();
    assert.strictEqual(document.querySelectorAll('#publication-options-editor [data-domain-row]').length, 3);
    document.querySelector('#publication-options-editor .settings-row-actions button[aria-label="Delete"]').click();
    assert.strictEqual(document.querySelectorAll('#publication-options-editor [data-domain-row]').length, 2);

    for (const [toggleId, editorId, addId] of [
      ['common-creators-use-system', 'common-creators-editor', 'add-common-creator']
    ]) {
      const toggle = document.getElementById(toggleId);
      assert.strictEqual(toggle.checked, true);
      assert.strictEqual(document.getElementById(addId).disabled, true);
      assert.ok([...document.querySelectorAll(`#${editorId} [data-domain-editable]`)].every(control => control.disabled));
      toggle.checked = false;
      toggle.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
      assert.strictEqual(document.getElementById(addId).disabled, false);
      assert.ok([...document.querySelectorAll(`#${editorId} [data-domain-editable]`)].some(control => !control.disabled));
    }

    const codesToggle = document.getElementById('patron-codes-use-system');
    assert.strictEqual(document.getElementById('patron-code-picker').disabled, true);
    assert.ok([...document.querySelectorAll('#patron-codes-editor button')].every(button => button.disabled));
    codesToggle.click();
    assert.strictEqual(document.getElementById('patron-code-picker').disabled, false);
    assert.ok(changes.length >= 5);

    const codeEditor = document.getElementById('patron-codes-editor');
    assert.strictEqual(codeEditor.querySelector('[data-code-id]').dataset.codeId, '2');
    const existingRules = document.querySelectorAll('#format-rules-editor [data-custom-rule-key]');
    assert.strictEqual(existingRules.length, 2);
    function ruleControlNames(row) {
      axe.setup(document);
      try {
        // This DOM fixture leaves inactive panels hidden; browser journeys scan the visible panels with axe.
        return {
          mode: axe.commons.text.accessibleText(row.querySelector('[data-custom-rule-property="mode"]'), { includeHidden: true }),
          labelOverride: axe.commons.text.accessibleText(row.querySelector('[data-custom-rule-property="labelOverride"]'), { includeHidden: true })
        };
      } finally {
        axe.teardown();
      }
    }
    assert.deepStrictEqual(ruleControlNames(document.querySelector('#format-rules-editor [data-custom-rule-key="pickup_location"]')), {
      mode: 'Pickup location mode for book', labelOverride: 'Pickup location label override for book'
    });
    const retiredRule = document.querySelector('#format-rules-editor [data-custom-rule-key="retired_type"]');
    assert.deepStrictEqual(ruleControlNames(retiredRule), {
      mode: 'Retired type mode for book', labelOverride: 'Retired type label override for book'
    });
    assert.strictEqual(retiredRule.querySelector('[data-custom-rule-property="mode"]').value, 'optional');
    assert.strictEqual(retiredRule.querySelector('[data-custom-rule-property="labelOverride"]').value, 'Historical field label');
    const initialValues = editors.collect().values;
    assert.deepStrictEqual(initialValues.codes, ['2']);
    assert.strictEqual(initialValues.rules[0].customFields.retired_type.mode, 'optional');
    assert.strictEqual(initialValues.rules[0].customFields.pickup_location.labelOverride, 'Pickup branch');
    assert.strictEqual(initialValues.fields[0].options[0].id, 'old-option');

    document.getElementById('add-custom-field').click();
    assert.strictEqual(document.querySelectorAll('#format-rules-editor [data-custom-rule-key]').length, 3);
    assert.deepStrictEqual(ruleControlNames(document.querySelector('#format-rules-editor [data-custom-rule-key="field_3"]')), {
      mode: 'New field mode for book', labelOverride: 'New field label override for book'
    });
    const afterDynamicAdd = editors.collect().values;
    assert.deepStrictEqual(afterDynamicAdd.codes, ['2']);
    assert.strictEqual(afterDynamicAdd.fields[2].key, 'field_3');
    assert.strictEqual(afterDynamicAdd.rules[0].customFields.retired_type.mode, 'optional');
    assert.strictEqual(afterDynamicAdd.rules[0].customFields.field_3.mode, 'hidden');
    const addedFieldRow = [...document.querySelectorAll('#additional-fields-editor [data-domain-row]')]
      .find(row => row.querySelectorAll('input')[0].value === 'field_3');
    addedFieldRow.querySelectorAll('input')[0].value = 'selected_at';
    addedFieldRow.querySelectorAll('input')[0].dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    const afterRename = editors.collect().values;
    assert.strictEqual(afterRename.rules[0].customFields.field_3, undefined);
    assert.strictEqual(afterRename.rules[0].customFields.selected_at.mode, 'hidden');
    assert.strictEqual(afterRename.fields[2].key, 'selected_at');
    assert.deepStrictEqual(ruleControlNames(document.querySelector('#format-rules-editor [data-custom-rule-key="selected_at"]')), {
      mode: 'New field mode for book', labelOverride: 'New field label override for book'
    });

    editors.populate({ ...data, autoClaimStaff: [] }, false);
    document.getElementById('add-format-rule').click();
    const newRuleMessage = [...document.querySelectorAll('#format-rules-editor [data-rule-property="message"]')].at(-1);
    assert.strictEqual(newRuleMessage.closest('label').hidden, true, 'New no-message rules use the same conditional visibility.');
    assert.strictEqual(newRuleMessage.disabled, true);
    editors.populate({ ...data, autoClaimStaff: [] }, false);
    document.getElementById('add-auto-claim-rule').click();
    const incompleteClaimRow = document.querySelector('#format-claim-rules-editor [data-domain-row]');
    assert.ok(incompleteClaimRow, 'An incomplete auto-claim draft must remain visible for correction or deletion.');
    assert.strictEqual(incompleteClaimRow.querySelectorAll('select')[1].options.length, 0,
      'The scenario must represent a library with no eligible auto-claim staff.');
    assert.throws(() => editors.collect(), /eligible staff user/,
      'An incomplete visible auto-claim row must block settings collection and the save request.');
    editors.dispose();

    const codeData = JSON.parse(JSON.stringify(data));
    codeData.stored.configuredSystem.allowedPatronCodeIds.values = [2, 77];
    codeData.patronCodeChoices = [{ id: 2, description: 'Adult' }, { id: 3, description: 'Branch use' },
      { id: 4, description: 'eCard' }, { id: 3, description: 'Repeated catalog choice' }];
    let codeChanges = 0;
    const codeEditors = module.createSettingsDomainEditors({ root, onChange: () => codeChanges++ });
    codeEditors.populate(codeData, true);
    const picker = root.querySelector('#patron-code-picker');
    const selected = () => codeEditors.collect().values.codes;
    const options = () => [...root.querySelectorAll('#patron-code-options [role="option"]')];
    const search = value => { picker.value = value; picker.dispatchEvent(new window.Event('input', { bubbles: true })); };
    const key = value => picker.dispatchEvent(new window.KeyboardEvent('keydown', { key: value, bubbles: true, cancelable: true }));
    const count = () => root.querySelector('#patron-code-count').textContent;
    assert.deepStrictEqual(selected(), ['2', '77']);
    assert.match(root.querySelector('#patron-codes-editor').textContent, /Adult \(2\).*Unavailable code \(77\)/);
    const baseline = codeEditors.snapshot();
    root.querySelector('[aria-label="Remove Adult (2)"]').click();
    search('Adult'); key('ArrowDown'); key('Enter');
    assert.strictEqual(codeEditors.snapshot(), baseline, 'Set membership is independent of remove/re-add order.');
    assert.strictEqual(codeEditors.collect().changed, false);
    codeChanges = 0;
    codeEditors.populate(codeData, true);
    search('branch');
    assert.deepStrictEqual(options().map(option => option.textContent), ['Branch use (3)']);
    key('ArrowDown');
    assert.strictEqual(picker.getAttribute('aria-activedescendant'), options()[0].id);
    key('Escape');
    assert.strictEqual(picker.getAttribute('aria-expanded'), 'false');
    search('4'); key('Tab');
    assert.strictEqual(codeEditors.snapshot(), baseline);
    assert.strictEqual(codeChanges, 0, 'Picker search, highlight and dismissal are transient.');
    assert.strictEqual(codeEditors.collect().changed, false);
    search('3');
    const staleOption = options()[0];
    key('ArrowUp'); key('Enter'); staleOption.click();
    assert.deepStrictEqual(selected(), ['2', '77', '3']);
    assert.strictEqual(codeChanges, 1, 'A stale option cannot add a duplicate.');
    assert.strictEqual(picker.value, '');
    search('');
    assert.deepStrictEqual(options().map(option => option.textContent), ['eCard (4)']);
    search('arbitrary 2147483648'); key('Enter');
    assert.deepStrictEqual(selected(), ['2', '77', '3'], 'Arbitrary typed IDs never become selections.');
    root.querySelector('[aria-label="Remove Unavailable code (77)"]').click();
    assert.strictEqual(document.activeElement.getAttribute('aria-label'), 'Remove Branch use (3)');
    root.querySelector('[aria-label="Remove Branch use (3)"]').click();
    assert.strictEqual(count(), '1 patron code selected.');
    root.querySelector('#patron-code-eligibility-enabled').checked = true;
    root.querySelector('[aria-label="Remove Adult (2)"]').click();
    assert.strictEqual(document.activeElement, picker);
    assert.strictEqual(count(), '0 patron codes selected.');
    assert.strictEqual(root.querySelector('#patron-code-warning').hidden, false);
    assert.match(root.querySelector('#patron-code-warning').textContent, /allows all patron codes/);
    search('eCard'); key('ArrowDown'); key('Enter');
    assert.strictEqual(root.querySelector('#patron-code-eligibility-enabled').checked, true);
    root.querySelector('#patron-code-eligibility-enabled').click();
    assert.deepStrictEqual(selected(), ['4'], 'Changing the policy does not erase IDs.');

    codeEditors.populate({ ...codeData, patronCodeChoices: [], patronCodeChoicesUnavailable: true }, true);
    assert.deepStrictEqual(selected(), ['2', '77']);
    assert.strictEqual(picker.disabled, true);
    assert.match(root.querySelector('#patron-code-availability').textContent, /unavailable.*preserved/);
    while (root.querySelector('#patron-codes-editor button')) root.querySelector('#patron-codes-editor button').click();
    assert.strictEqual(document.activeElement, root.querySelector('#patron-code-retry'), 'Last unavailable chip removal focuses Retry.');
    codeEditors.populate({ ...codeData, patronCodeChoices: [], patronCodeChoicesUnavailable: true }, true);
    root.querySelector('#patron-code-retry').click();
    while (root.querySelector('#patron-codes-editor button')) root.querySelector('#patron-codes-editor button').click();
    assert.strictEqual(document.activeElement, root.querySelector('#patron-code-eligibility-enabled'), 'Pending retry still leaves a reachable focus target.');
    codeEditors.populate({ ...codeData, patronCodeChoices: [], patronCodeChoicesUnavailable: true }, true);
    const failedBaseline = codeEditors.snapshot();
    root.querySelector('[aria-label="Remove Unavailable code (2)"]').focus();
    codeEditors.setCodeChoices(codeData.patronCodeChoices);
    assert.strictEqual(document.activeElement.getAttribute('aria-label'), 'Remove Adult (2)', 'Catalog labels preserve focus on the same chip.');
    assert.strictEqual(codeEditors.snapshot(), failedBaseline, 'Catalog retry preserves the selected set and draft baseline.');
    assert.strictEqual(picker.disabled, false);
    assert.strictEqual(codeEditors.collect().changed, false);
    assert.throws(() => codeEditors.setCodeChoices([{ id: 2147483648, description: 'Invalid' }]), /invalid choices/);
    codeEditors.populate(codeData, false);
    assert.strictEqual(picker.disabled, true);
    assert.deepStrictEqual(selected(), ['2', '77']);
    root.querySelector('#patron-codes-use-system').click();
    while (root.querySelector('#patron-codes-editor button')) root.querySelector('#patron-codes-editor button').click();
    assert.deepStrictEqual(selected(), []);
    assert.strictEqual(codeEditors.collect().values.codesUseSystem, false, 'An explicit empty override remains an override.');
    root.querySelector('#patron-codes-use-system').click();
    assert.deepStrictEqual(selected(), ['2', '77']);
    assert.strictEqual(codeEditors.collect().values.codesUseSystem, true);
    root.querySelector('#patron-codes-use-system').click();
    assert.deepStrictEqual(selected(), [], 'Toggling inheritance restores the library draft, including an empty one.');
    codeEditors.populate({ ...codeData, orgId: '3', stored: { ...codeData.stored, libraryOverride: {
      ...codeData.stored.libraryOverride, allowedPatronCodeIds: { exists: true, values: [4] }
    } } }, false);
    assert.deepStrictEqual(selected(), ['4'], 'A new scope replaces the old library draft.');
    for (const values of [[0], [-1], [2147483648], ['bad'], [2, 2]]) {
      codeEditors.populate({ ...codeData, stored: { ...codeData.stored, configuredSystem: {
        ...codeData.stored.configuredSystem, allowedPatronCodeIds: { exists: true, values }
      } } }, true);
      assert.throws(() => codeEditors.collect(), /invalid or duplicate ID/);
    }
    codeEditors.dispose();

    for (const staffId of ['2147483648', '9007199254740993', '9223372036854775807']) {
      const bigintStaffData = JSON.parse(JSON.stringify(data));
      bigintStaffData.autoClaimStaff = [{ id: staffId, label: `Staff ${staffId}` }];
      bigintStaffData.stored.autoClaimRules = [{ materialFormatId: '7', staffUserId: staffId, active: true }];
      const bigintStaffRoot = root.cloneNode(true);
      const bigintStaffEditors = module.createSettingsDomainEditors({ root: bigintStaffRoot });
      bigintStaffEditors.populate(bigintStaffData, false);
      const staffSelect = bigintStaffRoot.querySelectorAll('#format-claim-rules-editor [data-domain-row] select')[1];
      assert.ok(staffSelect, `Auto-claim staff ${staffId} must have a rendered rule control.`);
      assert.equal(staffSelect.value, staffId, 'A SQL bigint staff ID must remain an exact decimal string in the editor.');
      assert.deepStrictEqual(bigintStaffEditors.collect().values.claims, [
        { materialFormatId: '7', staffUserId: staffId, active: true }
      ]);
      bigintStaffEditors.dispose();
    }

    const sortedStaffData = JSON.parse(JSON.stringify(data));
    const configuredFormats = [...systemFormats, { ...systemFormats[0], id: '8', code: 'audio', label: 'Audio' }];
    sortedStaffData.stored.configuredSystem.formats = configuredFormats;
    sortedStaffData.stored.formats = configuredFormats;
    sortedStaffData.effective.formats = configuredFormats;
    sortedStaffData.autoClaimStaff = [
      { id: '9223372036854775807', label: 'Zebra staff', displayName: 'A misleading unused name' },
      { id: '9007199254740993', label: 'Alpha staff', displayName: 'Z misleading unused name' }
    ];
    sortedStaffData.stored.autoClaimRules = [{ materialFormatId: '7', staffUserId: '9223372036854775807', active: true }];
    const sortedRoot = root.cloneNode(true);
    const sortedEditors = module.createSettingsDomainEditors({ root: sortedRoot });
    sortedEditors.populate(sortedStaffData, false);
    const sortedSelect = sortedRoot.querySelectorAll('#format-claim-rules-editor [data-domain-row] select')[1];
    assert.deepStrictEqual([...sortedRoot.querySelector('#format-claim-rules-editor select').options].map(option => option.textContent),
      ['Book (book)', 'Audio (audio)'], 'Material format options retain their configured order.');
    assert.deepStrictEqual([...sortedSelect.options].map(option => option.textContent), ['Alpha staff', 'Zebra staff']);
    assert.strictEqual(sortedSelect.value, '9223372036854775807', 'Sorting choices preserves the exact selected bigint.');
    assert.deepStrictEqual(sortedStaffData.autoClaimStaff.map(item => item.label), ['Zebra staff', 'Alpha staff'], 'Sorting must not mutate source data.');
    sortedEditors.dispose();

    for (const invalidStaff of [
      { name: 'zero auto-claim staff ID', id: '0' },
      { name: 'noncanonical auto-claim staff ID', id: '01' },
      { name: 'above-Int64 auto-claim staff ID', id: '9223372036854775808' },
      { name: 'unsafe numeric auto-claim staff ID', id: 9007199254740992 },
      { name: 'numeric auto-claim staff ID', id: 42 }
    ]) {
      const malformedStaffData = JSON.parse(JSON.stringify(data));
      malformedStaffData.autoClaimStaff = [{ id: invalidStaff.id, label: `Invalid ${invalidStaff.name}` }];
      const malformedStaffEditors = module.createSettingsDomainEditors({ root: root.cloneNode(true) });
      malformedStaffEditors.populate(malformedStaffData, false);
      assert.throws(() => malformedStaffEditors.collect(),
        /auto-claim staff snapshot is malformed/,
        `${invalidStaff.name} must be rejected without broadening the accepted SQL bigint string contract.`);
      malformedStaffEditors.dispose();
    }

    const oversizedPatronCodeData = JSON.parse(JSON.stringify(data));
    oversizedPatronCodeData.patronCodeChoices = [{ id: 2147483648, description: 'Not a Polaris Int32 code' }];
    const oversizedPatronCodeEditors = module.createSettingsDomainEditors({ root: root.cloneNode(true) });
    oversizedPatronCodeEditors.populate(oversizedPatronCodeData, false);
    assert.throws(() => oversizedPatronCodeEditors.collect(), /patron-code choices snapshot is malformed/,
      'The broader SQL bigint staff identity contract must not broaden native Int32 patron-code IDs.');
    oversizedPatronCodeEditors.dispose();

    const malformedEditors = module.createSettingsDomainEditors({ root });
    malformedEditors.populate({
      ...data,
      stored: { ...data.stored, formatRules: [{ code: 'book', customFields: [] }] }
    }, false);
    assert.throws(() => malformedEditors.collect(), /stored format-rule snapshot|customFields must be an object/);
    malformedEditors.populate({
      ...data,
      stored: { ...data.stored, formatRules: undefined }
    }, false);
    assert.throws(() => malformedEditors.collect(), /settings snapshot is incomplete/);
    malformedEditors.populate({
      ...data,
      stored: {
        ...data.stored,
        configuredSystem: {
          ...data.stored.configuredSystem,
          allowedPatronCodeIds: { exists: true, values: [2, '3'] }
        }
      }
    }, false);
    assert.throws(() => malformedEditors.collect(), /patron-code snapshot contains an invalid or duplicate ID/);
    malformedEditors.dispose();

    const malformedCollections = [
      ['publication options', invalid => { invalid.stored.configuredSystem.publicationOptions.values = [{}]; }],
      ['common creators', invalid => { invalid.stored.configuredSystem.commonCreators.values = [null]; }],
      ['provider rows', invalid => { invalid.stored.providers = [{}]; }],
      ['effective provider rows', invalid => { invalid.effective.externalSearchProviders = [{}]; }],
      ['format rows', invalid => { invalid.stored.formats = [{}]; }],
      ['template rows', invalid => { invalid.stored.templates = [{}]; }],
      ['auto-claim rows', invalid => { invalid.stored.autoClaimRules = [{}]; }],
      ['origins', invalid => { invalid.stored.origins = [{}]; }],
      ['effective custom fields', invalid => { invalid.effective.customFields = [{}]; }],
      ['patron code choices', invalid => { invalid.patronCodeChoices = [{}]; }],
      ['auto-claim staff', invalid => { invalid.autoClaimStaff = [{}]; }]
    ];
    for (const [description, corrupt] of malformedCollections) {
      const invalid = JSON.parse(JSON.stringify(data));
      corrupt(invalid);
      const invalidRoot = root.cloneNode(true);
      const invalidEditors = module.createSettingsDomainEditors({ root: invalidRoot });
      invalidEditors.populate(invalid, false);
      assert.throws(() => invalidEditors.collect(), /snapshot is malformed|snapshot is incomplete/,
        `Malformed ${description} must block settings collection.`);
      invalidEditors.dispose();
    }

    const incompleteRoot = root.cloneNode(true);
    incompleteRoot.querySelector('#format-rules-editor').remove();
    const incompleteEditors = module.createSettingsDomainEditors({ root: incompleteRoot });
    incompleteEditors.populate(data, false);
    assert.throws(() => incompleteEditors.collect(), /settings editor is incomplete/);
    incompleteEditors.dispose();

    const missingRuleControlRoot = root.cloneNode(true);
    const missingRuleControlEditors = module.createSettingsDomainEditors({ root: missingRuleControlRoot });
    missingRuleControlEditors.populate(data, false);
    missingRuleControlRoot.querySelector('#format-rules-editor [data-custom-rule-key="pickup_location"] [data-custom-rule-property="mode"]').remove();
    assert.throws(() => missingRuleControlEditors.collect(), /custom field rule editor is incomplete/);
    missingRuleControlEditors.dispose();

    const secondFormat = {
      ...systemFormats[0],
      id: '8',
      code: 'magazine',
      label: 'Magazine',
      customFields: {}
    };
    const partialSnapshotData = JSON.parse(JSON.stringify(data));
    partialSnapshotData.stored.configuredSystem.formats = [...systemFormats, secondFormat];
    partialSnapshotData.stored.formats = [...systemFormats, secondFormat];
    partialSnapshotData.stored.formatRules.push({
      code: 'magazine',
      customFields: {
        retired_type: { mode: 'required', labelOverride: 'Magazine history' }
      }
    });
    let partialSnapshotBlocked = false;
    let partialSnapshotPayloadEmitted = false;
    const partialSnapshotRoot = root.cloneNode(true);
    const partialSnapshotEditors = module.createSettingsDomainEditors({ root: partialSnapshotRoot });
    try {
      partialSnapshotEditors.populate(partialSnapshotData, false);
      partialSnapshotRoot.querySelector('#format-rules-editor [data-rule-code="book"] [data-rule-property="message"]')
        .value = 'Changed visible rule while another stored rule is absent from the effective snapshot.';
      partialSnapshotEditors.collect();
      partialSnapshotPayloadEmitted = true;
    } catch {
      partialSnapshotBlocked = true;
    }
    partialSnapshotEditors.dispose();

    const completeRuleData = JSON.parse(JSON.stringify(partialSnapshotData));
    completeRuleData.effective.formats = [...systemFormats, secondFormat];
    let missingRuleRowBlocked = false;
    let missingRuleRowPayloadEmitted = false;
    const missingRuleRowRoot = root.cloneNode(true);
    const missingRuleRowEditors = module.createSettingsDomainEditors({ root: missingRuleRowRoot });
    try {
      missingRuleRowEditors.populate(completeRuleData, false);
      missingRuleRowRoot.querySelector('#format-rules-editor [data-rule-code="book"]').remove();
      missingRuleRowRoot.querySelector('#format-rules-editor [data-rule-code="magazine"] [data-rule-property="message"]')
        .value = 'Changed visible rule after another editor row disappeared.';
      missingRuleRowEditors.collect();
      missingRuleRowPayloadEmitted = true;
    } catch {
      missingRuleRowBlocked = true;
    }
    missingRuleRowEditors.dispose();

    assert.strictEqual(partialSnapshotBlocked, true,
      'A saved rule for a format missing from the effective snapshot must block rule collection.');
    assert.strictEqual(partialSnapshotPayloadEmitted, false,
      'An incomplete effective format snapshot must not produce a replacement payload.');
    assert.strictEqual(missingRuleRowBlocked, true,
      'A disappeared complete rule row must block collection while another visible rule is edited.');
    assert.strictEqual(missingRuleRowPayloadEmitted, false,
      'A partial rendered rule roster must not produce a replacement payload.');

    const providerData = JSON.parse(JSON.stringify(data));
    const providers = [
      { id: '9007199254740993', key: 'external_search_1', label: 'One', urlTemplate: 'https://example.org/?q={{title}}', isEnabled: true, sortOrder: 10, overridden: false },
      { id: '9007199254740995', key: 'PROVIDER_1', label: 'Two', urlTemplate: 'https://example.org/?q={{author}}', isEnabled: true, sortOrder: 20, overridden: false },
      { id: '9007199254740997', key: 'provider_3', label: 'Three', urlTemplate: 'https://example.org/', isEnabled: false, sortOrder: 30, overridden: false }
    ];
    providerData.stored.configuredSystem.providers = providers;
    providerData.stored.providers = providers;
    providerData.effective.externalSearchProviders = providers;
    const providerEditors = module.createSettingsDomainEditors({ root });
    providerEditors.populate(providerData, true);
    const providerRows = () => [...root.querySelectorAll('#external-search-provider-editor [data-domain-row]')];
    assert.equal(providerRows().length, 3);
    assert.equal(providerEditors.collect().changed, false, 'Presentation alone leaves provider settings clean.');
    assert.equal(providerRows()[0].querySelector('[aria-label="Move up"]').disabled, true);
    assert.equal(providerRows().at(-1).querySelector('[aria-label="Move down"]').disabled, true);
    providerRows()[0].querySelector('[aria-label="Move down"]').click();
    assert.deepEqual(providerEditors.collect().values.providers.map(value => [value.id, value.sortOrder]), [
      ['9007199254740995', 10], ['9007199254740993', 20], ['9007199254740997', 30]
    ]);
    providerRows()[1].querySelector('[aria-label="Move up"]').click();
    assert.equal(root.querySelector('#external-search-provider-editor').textContent.includes('Provider key'), false);
    assert.equal(providerRows()[0].querySelectorAll('input[type="text"]').length, 1, 'Only the label is a visible text input.');
    providerRows()[0].querySelector('input[type="text"]').value = 'Updated one';
    providerRows()[0].querySelector('input[type="checkbox"]').checked = false;
    root.querySelector('#add-provider').click();
    assert.equal(providerRows().at(-1).dataset.providerKey, 'provider_2', 'Key generation must respect case-insensitive collisions and gaps.');
    root.querySelector('#add-provider').click();
    assert.equal(providerRows().at(-1).dataset.providerKey, 'provider_4');
    let savedProviders = providerEditors.collect().values.providers;
    assert.equal(savedProviders[0].key, 'external_search_1');
    assert.equal(savedProviders[0].id, '9007199254740993');
    assert.equal(savedProviders[0].label, 'Updated one');
    assert.equal(savedProviders[0].isEnabled, false);
    providerData.stored.configuredSystem.providers = savedProviders;
    providerData.stored.providers = savedProviders;
    providerEditors.populate(providerData, true);
    assert.deepEqual(providerRows().map(row => row.dataset.providerKey), savedProviders.map(item => item.key), 'Repopulation must never regenerate an existing key.');
    providerRows()[1].querySelector('[aria-label^="Delete"]').click();
    root.querySelector('#add-provider').click();
    assert.equal(providerRows().at(-1).dataset.providerKey, 'provider_5', 'Removing a saved row must not reuse its key or attach its library overrides to a new provider.');
    providerData.stored.configuredSystem.providers = providers;
    providerData.stored.providers = providers;
    providerData.effective.externalSearchProviders = providers;
    providerEditors.populate(providerData, false);
    assert.equal(root.querySelector('#add-provider').hidden, true);
    assert.equal(providerRows()[0].querySelector('input[type="text"]').disabled, true);
    providerRows()[0].querySelector('.settings-domain-override-toggle').click();
    providerRows()[0].querySelector('input[type="text"]').value = 'Library one';
    savedProviders = providerEditors.collect().values.providers;
    assert.equal(savedProviders[0].key, 'external_search_1');
    assert.equal(savedProviders[0].overridden, true);
    assert.equal(savedProviders[0].label, 'Library one');
    providerRows()[0].querySelector('.settings-domain-override-toggle').click();
    assert.equal(providerEditors.collect().values.providers[0].reset, true);
    providerEditors.dispose();

    dom.window.close();
    console.log('Settings domain editor controls and inheritance checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
