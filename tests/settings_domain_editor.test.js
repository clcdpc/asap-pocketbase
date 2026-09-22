const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

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
        label: 'Book',
        isEnabled: true,
        messageBehavior: 'none',
        fields: {}
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
          allowedPatronCodeIds: { exists: true, values: ['1'] },
          providers: [],
          formats: systemFormats
        },
        libraryOverride: {
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
        customFields: [],
        autoClaimRules: [],
        templates: []
      },
      effective: {
        externalSearchProviders: [],
        formats: systemFormats,
        customFields: []
      },
      patronCodeChoices: [{ id: '1', description: 'Adult' }],
      autoClaimStaff: []
    };

    const root = document.getElementById('settings-view');
    const changes = [];
    const editors = module.createSettingsDomainEditors({
      root,
      onChange: () => changes.push(true)
    });
    editors.populate(data, false);

    const existingRule = document.querySelector('#format-rules-editor [data-domain-row]');
    const existingMessageBehavior = existingRule.querySelector('[data-rule-property="messageBehavior"]');
    const existingMessageField = existingRule.querySelector('[data-rule-message-field]');
    const existingMessage = existingMessageField.querySelector('[data-rule-property="message"]');
    assert.strictEqual(existingMessageField.hidden, true);
    assert.strictEqual(existingMessageField.getAttribute('aria-hidden'), 'true');
    assert.strictEqual(existingMessage.disabled, true);
    assert.strictEqual(existingMessage.required, false);

    existingMessageBehavior.value = 'message';
    existingMessageBehavior.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    existingMessage.value = 'Draft message preserved while editing';
    assert.strictEqual(existingMessageField.hidden, false);
    assert.strictEqual(existingMessage.disabled, false);

    existingMessageBehavior.value = 'none';
    existingMessageBehavior.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.strictEqual(existingMessageField.hidden, true);
    assert.strictEqual(existingMessage.disabled, true);
    assert.strictEqual(existingMessage.value, 'Draft message preserved while editing');

    existingMessageBehavior.value = 'ebookMessage';
    existingMessageBehavior.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.strictEqual(existingMessageField.hidden, false);
    assert.strictEqual(existingMessage.disabled, false);
    assert.strictEqual(existingMessage.value, 'Draft message preserved while editing');

    document.getElementById('add-format-rule').click();
    const addedRule = document.querySelector('#format-rules-editor [data-domain-row]:last-child');
    const addedMessageBehavior = addedRule.querySelector('[data-rule-property="messageBehavior"]');
    const addedMessageField = addedRule.querySelector('[data-rule-message-field]');
    assert.strictEqual(addedMessageField.hidden, true);
    assert.strictEqual(addedMessageField.querySelector('[data-rule-property="message"]').disabled, true);
    addedMessageBehavior.value = 'eaudiobookMessage';
    addedMessageBehavior.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.strictEqual(addedMessageField.hidden, false);
    assert.strictEqual(addedMessageField.querySelector('[data-rule-property="message"]').disabled, false);
    assert.doesNotThrow(() => editors.collect());

    assert.strictEqual(root.querySelectorAll('textarea[data-settings-json]').length, 0);
    assert.strictEqual(root.querySelector('[data-setting-key="postmarkToken"]').dataset.systemOnly, undefined);
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
    dom.window.close();
    console.log('Settings domain editor controls and inheritance checks passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
