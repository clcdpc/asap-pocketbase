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
      mode: 'Pickup location mode for Book (book)', labelOverride: 'Pickup location label override for Book (book)'
    });
    const retiredRule = document.querySelector('#format-rules-editor [data-custom-rule-key="retired_type"]');
    assert.deepStrictEqual(ruleControlNames(retiredRule), {
      mode: 'Retired type mode for Book (book)', labelOverride: 'Retired type label override for Book (book)'
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
      mode: 'New field mode for Book (book)', labelOverride: 'New field label override for Book (book)'
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
      mode: 'New field mode for Book (book)', labelOverride: 'New field label override for Book (book)'
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

    const accordionData = JSON.parse(JSON.stringify(data));
    const extraFormats = ['digital', 'unusual\"] #code'].map((code, index) => ({ ...systemFormats[0],
      id: String(90 + index), code, label: index ? 'Long configured name '.repeat(8) : 'Digital edition', customFields: {} }));
    const accordionFormats = [...systemFormats, ...extraFormats];
    accordionData.stored.configuredSystem.formats = accordionFormats;
    accordionData.stored.formats = accordionFormats;
    accordionData.effective.formats = accordionFormats;
    let accordionChanges = 0;
    const accordion = module.createSettingsDomainEditors({ root, onChange: () => accordionChanges++ });
    accordion.populate(accordionData, false);
    const rows = () => [...root.querySelectorAll('#format-rules-editor [data-rule-code]')];
    const card = code => rows().find(row => row.dataset.ruleCode === code);
    const toggle = code => card(code).querySelector('[data-rule-toggle]');
    const panel = code => document.getElementById(toggle(code).getAttribute('aria-controls'));
    const summary = () => card('book').querySelector('[data-rule-summary]').textContent;
    const change = (control, value) => {
      control.value = value;
      control.dispatchEvent(new window.Event('change', { bubbles: true }));
    };
    const accordionBaseline = accordion.snapshot();
    for (const row of rows()) {
      const button = row.querySelector('[data-rule-toggle]');
      assert.strictEqual(button.tagName, 'BUTTON');
      assert.strictEqual(button.type, 'button');
      assert.strictEqual(button.getAttribute('aria-expanded'), 'false');
      assert.strictEqual(panel(row.dataset.ruleCode).hidden, true);
      assert.strictEqual(row.querySelectorAll('[data-format-field]').length, 4);
      assert.strictEqual(row.querySelectorAll('[data-format-label]').length, 4);
      assert.strictEqual(button.querySelector('button'), null);
      assert.match(button.getAttribute('aria-controls'), /^format-rule-panel-\d+$/);
    }
    assert.match(toggle('digital').textContent, /Digital edition.*digital/);
    assert.strictEqual(summary(), '2 required · 3 optional · 0 hidden', 'Disabled and retired rules never enter patron-visible counts.');
    assert.match(card('book').textContent, /Disabled field — not shown to patrons/);
    toggle('book').click(); toggle('digital').click(); toggle('book').click();
    assert.strictEqual(panel('book').hidden, true);
    assert.strictEqual(panel('digital').hidden, false, 'Disclosures are independent.');
    assert.strictEqual(accordion.snapshot(), accordionBaseline);
    assert.strictEqual(accordion.collect().changed, false);
    assert.strictEqual(accordionChanges, 0, 'Disclosures never notify dirty tracking.');
    toggle('book').click();
    const titleLabel = card('book').querySelector('[data-format-label="title"]');
    titleLabel.value = 'Editable title draft';
    titleLabel.focus(); titleLabel.setSelectionRange(2, 7);
    change(card('book').querySelector('[data-format-field="title"]'), 'optional');
    assert.strictEqual(summary(), '1 required · 4 optional · 0 hidden');
    assert.strictEqual(card('book').querySelector('[data-format-label="title"]'), titleLabel, 'Summary updates never rebuild controls.');
    assert.strictEqual(document.activeElement, titleLabel);
    assert.strictEqual(titleLabel.selectionStart, 2);
    assert.strictEqual(titleLabel.selectionEnd, 7);
    change(card('book').querySelector('[data-format-field="author"]'), 'hidden');
    assert.strictEqual(summary(), '1 required · 3 optional · 1 hidden');
    change(card('book').querySelector('[data-custom-rule-key="pickup_location"] select'), 'hidden');
    assert.strictEqual(summary(), '0 required · 3 optional · 2 hidden');
    assert.deepStrictEqual([...card('book').querySelectorAll('[data-format-field]')].map(node => node.dataset.formatField),
      ['title', 'author', 'identifier', 'publication']);
    change(card('book').querySelector('[data-rule-property="messageBehavior"]'), 'eaudiobookMessage');
    card('book').querySelector('[data-rule-property="message"]').value = 'Temporarily hidden message draft';
    change(card('book').querySelector('[data-rule-property="messageBehavior"]'), 'none');
    const bookMove = card('book').querySelector('[aria-label="Move Book (book) down"]');
    bookMove.focus(); bookMove.click();
    assert.strictEqual(document.activeElement.getAttribute('aria-label'), 'Move Book (book) down');
    assert.deepStrictEqual(rows().map(row => row.dataset.ruleCode), ['digital', 'book', extraFormats[1].code]);
    assert.strictEqual(panel('book').hidden, false);
    assert.strictEqual(panel('digital').hidden, false);
    assert.strictEqual(panel(extraFormats[1].code).hidden, true);
    root.querySelector('#add-custom-field').click();
    assert.strictEqual(document.activeElement.closest('[data-field-id]'), root.querySelector('#additional-fields-editor').lastElementChild);
    assert.strictEqual(summary(), '0 required · 3 optional · 3 hidden');
    let addedDefinition = root.querySelector('#additional-fields-editor').lastElementChild;
    change(addedDefinition.querySelector('input'), 'first_key');
    change(addedDefinition.querySelector('input'), 'second_key');
    assert.ok(card('book').querySelector('[data-custom-rule-key="second_key"]'));
    assert.strictEqual(card('book').querySelector('[data-custom-rule-key="first_key"]'), null);
    change(card('book').querySelector('[data-custom-rule-key="second_key"] select'), 'required');
    assert.strictEqual(summary(), '1 required · 3 optional · 2 hidden');
    addedDefinition.querySelector('input[type="checkbox"]').click();
    assert.strictEqual(summary(), '0 required · 3 optional · 2 hidden');
    change(addedDefinition.querySelectorAll('input')[1], 'Renamed field label');
    assert.match(card('book').textContent, /Renamed field label/);
    addedDefinition.querySelector('input[type="checkbox"]').click();
    const fieldMove = addedDefinition.querySelector('[aria-label="Move up"]');
    fieldMove.focus(); fieldMove.click();
    assert.strictEqual(document.activeElement.getAttribute('aria-label'), 'Move up');
    addedDefinition = [...root.querySelectorAll('#additional-fields-editor [data-field-id]')]
      .find(row => row.querySelector('input').value === 'second_key');
    addedDefinition.querySelector('[aria-label="Delete"]').click();
    assert.strictEqual(document.activeElement.isConnected, true);
    assert.strictEqual(document.activeElement.tagName, 'INPUT');
    assert.strictEqual(panel('book').hidden, false);
    const preservedDraft = accordion.collect().values.rules.find(rule => rule.code === 'book');
    assert.strictEqual(preservedDraft.title.label, 'Editable title draft');
    assert.strictEqual(preservedDraft.author.mode, 'hidden');
    assert.strictEqual(preservedDraft.message, 'Temporarily hidden message draft');
    assert.deepStrictEqual(preservedDraft.customFields.retired_type, { mode: 'optional', labelOverride: 'Historical field label' });
    const deleteDigital = card('digital').querySelector('[aria-label="Delete Digital edition (digital) format rule"]');
    deleteDigital.focus(); deleteDigital.click();
    assert.strictEqual(document.activeElement, toggle('book'));
    root.querySelector('#add-format-rule').click();
    assert.strictEqual(panel('digital').hidden, false, 'Adding a missing rule expands it.');
    assert.strictEqual(document.activeElement, toggle('digital'));
    assert.strictEqual(accordion.collect().values.rules.find(rule => rule.code === 'book').message, preservedDraft.message);
    const fullRoster = rows().length;
    root.querySelector('#add-format-rule').click();
    assert.strictEqual(rows().length, fullRoster, 'Adding cannot duplicate a configured rule.');
    toggle('book').click();
    assert.strictEqual(accordion.collect().values.rules.find(rule => rule.code === 'book').title.label, 'Editable title draft');
    accordion.populate(accordionData, false);
    assert.ok(rows().every(row => panel(row.dataset.ruleCode).hidden), 'A new snapshot resets disclosure state.');
    assert.strictEqual(accordion.collect().changed, false);
    for (const selector of ['[data-format-label="author"]', '[data-format-field="publication"]',
      '[data-rule-property="message"]', '[data-custom-rule-key="retired_type"] [data-custom-rule-property="mode"]']) {
      accordion.populate(accordionData, false);
      card('book').querySelector(selector).remove();
      assert.throws(() => accordion.collect(), /incomplete|missing a required control/, 'Hidden incomplete controls fail closed.');
    }
    for (const mutate of [
      () => root.querySelector('#add-custom-field').click(),
      () => root.querySelector('#add-format-rule').click(),
      () => card('book').querySelector('[aria-label="Move Book (book) down"]').click(),
      () => card('digital').querySelector('[aria-label="Delete Digital edition (digital) format rule"]').click(),
      () => root.querySelector('#additional-fields-editor [aria-label="Move down"]').click(),
      () => root.querySelector('#additional-fields-editor [aria-label="Delete"]').click(),
      () => change(root.querySelector('#additional-fields-editor input'), 'renamed_retired')
    ]) {
      accordion.populate(accordionData, false);
      const missing = '[data-custom-rule-key="retired_type"] [data-custom-rule-property="mode"]';
      card('book').querySelector(missing).remove();
      mutate();
      assert.strictEqual(card('book').querySelector(missing), null, 'A redraw cannot repair a missing hidden control with a default.');
      assert.throws(() => accordion.collect(), /incomplete|missing a required control/,
        'A redraw must not silently replace the missing control\'s saved value.');
    }
    accordion.dispose();

    for (const system of [true, false]) {
      const malformed = module.createSettingsDomainEditors({ root });
      for (const selector of ['[data-rule-property="messageBehavior"]', '[data-format-field="author"]',
        '[data-custom-rule-key="retired_type"] [data-custom-rule-property="mode"]']) {
        for (const corrupt of [
          control => { control.value = 'unsupported'; },
          control => { control.after(control.cloneNode(true)); },
          control => {
            const replacement = document.createElement('input');
            for (const attribute of control.attributes) replacement.setAttribute(attribute.name, attribute.value);
            replacement.value = control.value;
            control.replaceWith(replacement);
          }
        ]) {
          malformed.populate(accordionData, system);
          const row = card('book');
          corrupt(row.querySelector(selector));
          assert.throws(() => malformed.collect(), /incomplete|missing a required control/,
            'Malformed collapsed controls must fail before collection, in either scope.');
          row.querySelector('.settings-row-actions button:nth-child(2)').click();
          assert.strictEqual(card('book'), row, 'A redraw must not default or repair malformed controls.');
          assert.throws(() => malformed.collect(), /incomplete|missing a required control/);
        }
      }
      malformed.dispose();
    }

    for (const system of [true, false]) {
      const names = module.createSettingsDomainEditors({ root });
      const renameData = JSON.parse(JSON.stringify(accordionData));
      renameData.effective.formats[1].label = 'Book';
      renameData.stored.configuredSystem.formats[1].label = 'Book';
      renameData.stored.formats[1].label = 'Book';
      names.populate(renameData, system);
      const assertActionNames = (code, label) => {
        const context = `${label} (${code})`;
        assert.deepStrictEqual([...card(code).querySelectorAll('.settings-row-actions button')]
          .map(button => [button.title, button.getAttribute('aria-label')]),
        [`Move ${context} up`, `Move ${context} down`, `Delete ${context} format rule`].map(name => [name, name]));
      };
      assertActionNames('book', 'Book');
      assertActionNames('digital', 'Book');
      const formatRows = [...root.querySelectorAll('#material-formats-editor [data-domain-row]')];
      const formatLabel = index => formatRows[index].querySelectorAll('input')[1];
      if (!system) {
        for (const row of formatRows) row.querySelector('.settings-domain-override-toggle').click();
      }
      const rulesBeforeRename = names.collect().values.rules;
      const originalCards = rows();
      const controls = originalCards.map(row => [...row.querySelectorAll('input, select, textarea, button')]);
      toggle('book').click();
      const focusedLabel = formatLabel(0);
      focusedLabel.focus();
      focusedLabel.value = 'Book renamed <draft>';
      focusedLabel.setSelectionRange(2, 9);
      focusedLabel.dispatchEvent(new window.Event('input', { bubbles: true }));
      assert.strictEqual(document.activeElement, focusedLabel);
      assert.strictEqual(focusedLabel.selectionStart, 2);
      assert.strictEqual(focusedLabel.selectionEnd, 9);
      assert.strictEqual(toggle('book').getAttribute('aria-label'), 'Collapse Book renamed <draft> (book) format rules');
      assert.strictEqual(card('book').querySelector('.settings-rule-identity strong').textContent, 'Book renamed <draft>');
      assert.strictEqual(card('book').querySelector('draft'), null, 'Configured labels remain plain text.');
      assert.strictEqual(toggle('digital').getAttribute('aria-label'), 'Expand Book (digital) format rules',
        'Equal display labels never share an identity.');
      change(formatLabel(1), 'Book renamed <draft>');
      const unusualCode = extraFormats[1].code;
      change(formatLabel(2), '   ');
      assert.strictEqual(card(unusualCode).querySelector('.settings-rule-identity strong').textContent, unusualCode);
      assert.strictEqual(toggle(unusualCode).getAttribute('aria-label'), `Expand ${unusualCode} (${unusualCode}) format rules`);
      assert.strictEqual(panel(unusualCode).hidden, true);
      assert.strictEqual(panel('book').hidden, false);
      for (const [index, row] of rows().entries()) {
        assert.strictEqual(row, originalCards[index]);
        assert.deepStrictEqual([...row.querySelectorAll('input, select, textarea, button')], controls[index]);
      }
      for (const code of ['book', 'digital', unusualCode]) {
        const label = code === unusualCode ? unusualCode : 'Book renamed <draft>';
        const context = `${label} (${code})`;
        const row = card(code);
        assert.strictEqual(row.querySelector('[data-rule-property="messageBehavior"]').getAttribute('aria-label'), `Message behavior for ${context}`);
        assert.strictEqual(row.querySelector('[data-rule-property="message"]').getAttribute('aria-label'), `Message for ${context}`);
        for (const name of ['title', 'author', 'identifier', 'publication']) {
          const readable = name.charAt(0).toUpperCase() + name.slice(1);
          assert.strictEqual(row.querySelector(`[data-format-field="${name}"]`).getAttribute('aria-label'), `${readable} requirement for ${context}`);
          assert.strictEqual(row.querySelector(`[data-format-label="${name}"]`).getAttribute('aria-label'), `${readable} patron-facing label for ${context}`);
        }
        assert.strictEqual(row.querySelector('[data-custom-rule-key="pickup_location"] select').getAttribute('aria-label'), `Pickup location mode for ${context}`);
        assertActionNames(code, label);
      }
      const actionNames = rows().flatMap(row => [...row.querySelectorAll('.settings-row-actions button')]
        .map(button => button.getAttribute('aria-label')));
      assert.strictEqual(new Set(actionNames).size, actionNames.length, 'Duplicate labels retain unique header action names.');
      assert.deepStrictEqual(names.collect().values.rules, rulesBeforeRename);
      assert.strictEqual(names.collect().domainsChanged.formats, true);
      assert.strictEqual(names.collect().domainsChanged.rules, false, 'Renaming never dirties formatRules.');
      toggle('book').click(); toggle('book').click();
      assert.strictEqual(toggle('book').getAttribute('aria-label'), 'Collapse Book renamed <draft> (book) format rules',
        'Disclosure handlers use the current draft name.');
      change(card('digital').querySelector('[data-format-label="author"]'), 'Unrelated author draft');
      change(card('book').querySelector('[data-rule-property="messageBehavior"]'), 'message');
      change(card('book').querySelector('[data-rule-property="message"]'), 'Unsaved message');
      const drafts = names.collect().values.rules;
      const draftControl = card('digital').querySelector('[data-format-label="author"]');
      draftControl.focus(); draftControl.setSelectionRange(1, 6);
      change(formatLabel(1), 'Another name');
      assert.strictEqual(document.activeElement, draftControl);
      assert.strictEqual(draftControl.selectionStart, 1);
      assert.strictEqual(draftControl.selectionEnd, 6);
      assert.deepStrictEqual(names.collect().values.rules, drafts, 'Every unrelated rule draft survives name synchronization.');
      names.populate(renameData, system);
      assert.strictEqual(names.collect().changed, false, 'Discard/reload restores the accepted snapshot.');
      assert.ok(rows().every(row => panel(row.dataset.ruleCode).hidden));
      if (!system) assert.ok([...root.querySelectorAll('#material-formats-editor input[type="text"]')].every(input => input.disabled));
      names.dispose();
    }

    const draftIdentity = module.createSettingsDomainEditors({ root });
    draftIdentity.populate(accordionData, false);
    root.querySelector('#add-material-format').click();
    const customFormat = root.querySelector('#material-formats-editor').lastElementChild;
    const customCode = customFormat.querySelector('input');
    const initialCode = customCode.value;
    assert.strictEqual(customCode.readOnly, false);
    root.querySelector('#add-format-rule').click();
    const customCard = card(initialCode);
    const customPanel = panel(initialCode);
    change(customCard.querySelector('[data-format-label="title"]'), 'Custom unsaved title');
    const customControls = [...customCard.querySelectorAll('input, select, textarea, button')];
    customCode.focus(); customCode.setSelectionRange(1, 3);
    change(customCode, 'new\"] #draft');
    const nextCode = customCode.value;
    assert.strictEqual(card(initialCode), undefined, 'The old unsaved rule identity is removed.');
    assert.strictEqual(card(nextCode), customCard);
    assert.strictEqual(panel(nextCode), customPanel);
    assert.strictEqual(customPanel.hidden, false);
    assert.strictEqual(document.activeElement, customCode);
    assert.deepStrictEqual([...customCard.querySelectorAll('input, select, textarea, button')], customControls);
    assert.strictEqual(customCard.querySelector('.settings-rule-code').textContent, nextCode);
    assert.deepStrictEqual([...customCard.querySelectorAll('.settings-row-actions button')].map(button => button.getAttribute('aria-label')),
      [`Move New format (${nextCode}) up`, `Move New format (${nextCode}) down`, `Delete New format (${nextCode}) format rule`]);
    toggle(nextCode).click(); toggle(nextCode).click();
    assert.strictEqual(customPanel.hidden, false, 'Disclosure state follows the new draft code.');
    assert.strictEqual(toggle(nextCode).getAttribute('aria-label'), `Collapse New format (${nextCode}) format rules`);
    const renamedRule = draftIdentity.collect().values.rules.find(rule => rule.code === nextCode);
    assert.strictEqual(renamedRule.title.label, 'Custom unsaved title');
    assert.ok(!draftIdentity.collect().values.rules.some(rule => rule.code === initialCode));
    root.querySelector('#add-format-rule').click();
    assert.strictEqual(rows().length, accordionFormats.length + 1, 'Renaming cannot produce a duplicate missing-rule card.');
    change(customCode, 'book');
    assert.strictEqual(card(nextCode), customCard, 'A colliding code cannot overwrite another rule identity.');
    change(customCode, 'unique_draft');
    assert.strictEqual(card('unique_draft'), customCard);
    const customLabel = customFormat.querySelectorAll('input')[1];
    change(customLabel, 'Book');
    assert.deepStrictEqual([...customCard.querySelectorAll('.settings-row-actions button')].map(button => [button.title, button.getAttribute('aria-label')]),
      ['Move Book (unique_draft) up', 'Move Book (unique_draft) down', 'Delete Book (unique_draft) format rule'].map(name => [name, name]));
    assert.strictEqual(root.querySelector('#material-formats-editor input').readOnly, true);
    const bookBeforeRedraw = card('book');
    change(customCode, 'book');
    customFormat.querySelector('[aria-label="Move up"]').click();
    const redrawnCode = root.querySelector('#material-formats-editor [data-format-id=""] input');
    change(redrawnCode, 'recovered_after_reorder');
    assert.strictEqual(card('book'), bookBeforeRedraw);
    assert.strictEqual(card('recovered_after_reorder'), customCard,
      'A draft format retains its own rule identity across an invalid code and a Material formats redraw.');
    assert.strictEqual(customCard.querySelector('[data-format-label="title"]').value, 'Custom unsaved title');
    assert.ok(!draftIdentity.collect().values.rules.some(rule => rule.code === 'unique_draft'));
    draftIdentity.populate(accordionData, false);
    assert.strictEqual(draftIdentity.collect().changed, false);
    draftIdentity.dispose();

    for (const [savedCode, savedKey] of [['custom_3', 'field_3'], ['CUSTOM_3', 'FIELD_3']]) {
      const collisionData = JSON.parse(JSON.stringify(data));
      const savedFormat = { ...collisionData.stored.configuredSystem.formats[0], id: '91',
        code: savedCode, ownerOrganizationId: 2, label: 'Saved custom format',
        customFields: { [savedKey]: { mode: 'required', labelOverride: 'Saved rule witness' } } };
      collisionData.stored.libraryOverride.formats = [savedFormat];
      collisionData.stored.formats.push(savedFormat);
      collisionData.effective.formats.push(savedFormat);
      collisionData.stored.customFields[1].key = savedKey;
      collisionData.effective.customFields = [collisionData.stored.customFields[1]];
      for (const format of [collisionData.stored.configuredSystem.formats[0],
        collisionData.stored.formats[0], collisionData.effective.formats[0]]) {
        format.customFields[savedKey] = format.customFields.pickup_location;
        delete format.customFields.pickup_location;
      }
      collisionData.stored.formatRules[0].customFields[savedKey] = collisionData.stored.formatRules[0].customFields.pickup_location;
      delete collisionData.stored.formatRules[0].customFields.pickup_location;
      collisionData.stored.formatRules.push({ code: savedCode, customFields: savedFormat.customFields });
      const collisions = module.createSettingsDomainEditors({ root });
      collisions.populate(collisionData, false);
      toggle(savedCode).click();
      change(card(savedCode).querySelector('[data-format-label="title"]'), 'Existing unsaved title');
      const existingRules = collisions.collect().values.rules;
      const savedCard = card(savedCode);
      root.querySelector('#add-material-format').click();
      let newFormat = root.querySelector('#material-formats-editor').lastElementChild;
      assert.notStrictEqual(newFormat.querySelector('input').value.toLowerCase(), savedCode.toLowerCase(),
        'Generated codes must avoid existing formats, including case-insensitive collisions.');
      change(newFormat.querySelector('input'), 'new_unique_format');
      assert.strictEqual(card(savedCode), savedCard, 'Naming a new format must not take an existing rule card.');
      assert.deepStrictEqual(collisions.collect().values.rules, existingRules);
      root.querySelector('#add-format-rule').click();
      assert.strictEqual(panel('new_unique_format').hidden, false);
      assert.deepStrictEqual(collisions.collect().values.rules.find(rule => rule.code === savedCode),
        existingRules.find(rule => rule.code === savedCode));

      root.querySelector('#add-custom-field').click();
      let newField = root.querySelector('#additional-fields-editor').lastElementChild;
      const newKey = newField.querySelector('input').value;
      assert.notStrictEqual(newKey.toLowerCase(), savedKey.toLowerCase(),
        'Generated keys must avoid saved definitions and their rule identities.');
      newField.querySelector('input[type="checkbox"]').click();
      assert.strictEqual(card('book').querySelector(`[data-custom-rule-key="${newKey}"] .settings-custom-rule-status`).textContent,
        'Disabled field — not shown to patrons', 'A never-saved field must not claim that a saved rule was retained.');
      change(newField.querySelector('input'), 'new_unique_field');
      assert.ok(collisions.collect().values.fields.some(field => field.key === 'new_unique_field'));
      assert.deepStrictEqual(collisions.collect().values.rules.find(rule => rule.code === savedCode).customFields[savedKey],
        { mode: 'required', labelOverride: 'Saved rule witness' });
      newField.querySelector('[aria-label="Delete"]').click();
      assert.strictEqual(collisions.collect().values.fields.length, 2, 'A new field remains removable without discarding other drafts.');
      assert.strictEqual(collisions.collect().values.rules.find(rule => rule.code === savedCode).title.label, 'Existing unsaved title');

      // Recover a duplicate entered by hand after another action redraws Material formats.
      collisions.populate(collisionData, false);
      const originalRules = collisions.collect().values.rules;
      const originalCard = card(savedCode);
      root.querySelector('#add-material-format').click();
      newFormat = root.querySelector('#material-formats-editor').lastElementChild;
      change(newFormat.querySelector('input'), savedCode);
      root.querySelector('#add-material-format').click();
      newFormat = root.querySelectorAll('#material-formats-editor [data-domain-row]')[2];
      change(newFormat.querySelector('input'), 'recovered_format');
      assert.strictEqual(card(savedCode), originalCard, 'A duplicate draft code never owns another format\'s existing rule.');
      assert.deepStrictEqual(collisions.collect().values.rules, originalRules);
      assert.strictEqual(collisions.collect().domainsChanged.rules, false);
      collisions.dispose();
    }

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
