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
      ['common-creators-use-system', 'common-creators-editor', 'add-common-creator'],
      ['patron-codes-use-system', 'patron-codes-editor', 'add-patron-code']
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

    assert.ok(changes.length >= 5);

    const codeEditor = document.getElementById('patron-codes-editor');
    assert.strictEqual(codeEditor.querySelector('select').value, '2');
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
    document.getElementById('add-auto-claim-rule').click();
    const incompleteClaimRow = document.querySelector('#format-claim-rules-editor [data-domain-row]');
    assert.ok(incompleteClaimRow, 'An incomplete auto-claim draft must remain visible for correction or deletion.');
    assert.strictEqual(incompleteClaimRow.querySelectorAll('select')[1].options.length, 0,
      'The scenario must represent a library with no eligible auto-claim staff.');
    assert.throws(() => editors.collect(), /eligible staff user/,
      'An incomplete visible auto-claim row must block settings collection and the save request.');
    editors.dispose();

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

    dom.window.close();
    console.log('Settings domain editor controls and inheritance checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
