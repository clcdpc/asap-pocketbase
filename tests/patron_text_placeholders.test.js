const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

(async () => {
  const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-patron-placeholders-'));
  fs.copyFileSync(path.join(frontend, 'staff', 'js', 'patron-text-placeholders.js'), path.join(temporary, 'helper.mjs'));
  const dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'));
  try {
    global.window = dom.window; global.document = dom.window.document;
    const { patronPlaceholderGroups, createPatronTextPlaceholders } = await import(pathToFileURL(path.join(temporary, 'helper.mjs')).href);
    const general = ['barcode_label', 'pin_label'];
    const keys = field => patronPlaceholderGroups(field).flatMap(group => group.items.map(item => item.key));
    for (const key of ['pageTitle', 'loginPrompt', 'loginNote', 'suggestionFormNote', 'noEmailMessage', 'successTitle', 'successMessage', 'ebookMessage', 'eaudiobookMessage']) {
      assert.deepEqual(keys(key), general, key);
    }
    assert.deepEqual(keys('alreadySubmittedMessage'), [...general, 'duplicate_date', 'duplicate_status', 'duplicate_title', 'duplicate_author', 'duplicate_format', 'duplicate_match_type']);
    for (const field of ['barcodeLabel', 'pinLabel', 'suggestionStatusLabel', 'unknown']) assert.deepEqual(keys(field), []);
    const root = document.getElementById('settings-view'), panel = document.getElementById('settings-patron');
    for (const node of [root, panel, document.getElementById('workspace'), document.getElementById('settings-form')]) node.hidden = false;
    let generation = 0, active = true, count = 0;
    panel.addEventListener('input', () => count++);
    const helper = createPatronTextPlaceholders({ root, panel,
      listen: (target, name, listener) => target.addEventListener(name, listener),
      captureContext: () => generation, isContextCurrent: value => value === generation, isActive: () => active });
    helper.refresh();
    assert.equal(panel.querySelectorAll('.patron-placeholder-trigger').length, 10);
    assert.equal(panel.textContent.includes('Rendered patron text supports'), false);
    const field = key => panel.querySelector(`[data-setting-key="${key}"]`);
    const success = field('successMessage').querySelector('textarea');
    const duplicate = field('alreadySubmittedMessage').querySelector('textarea');
    const trigger = control => control.closest('.settings-field').querySelector('.patron-placeholder-trigger');
    const choose = (control, key) => control.closest('.settings-field').querySelector(`[data-patron-placeholder="${key}"]`).click();
    success.value = 'É & first\nsecond!'; success.focus(); success.setSelectionRange(4, 9);
    trigger(success).click();
    assert.equal(count, 0, 'Opening/browsing the popup must remain clean.');
    choose(success, 'barcode_label');
    assert.equal(success.value, 'É & {{barcode_label}}\nsecond!');
    assert.equal(success.selectionStart, 21); assert.equal(document.activeElement, success);
    trigger(success).click(); choose(success, 'pin_label');
    assert.equal(success.value, 'É & {{barcode_label}}{{pin_label}}\nsecond!');
    assert.equal(count, 2);
    duplicate.value = 'Previous: '; duplicate.setSelectionRange(10, 10);
    trigger(duplicate).click();
    success.dispatchEvent(new window.Event('select', { bubbles: true }));
    choose(duplicate, 'duplicate_title');
    assert.equal(duplicate.value, 'Previous: {{duplicate_title}}');
    assert.equal(count, 3, 'A late selection event cannot redirect the field-owned popup.');
    trigger(duplicate).click();
    document.activeElement.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    assert.equal(document.activeElement, trigger(duplicate));
    assert.equal(trigger(duplicate).getAttribute('aria-expanded'), 'false');
    for (const failure of ['scope', 'readonly', 'disabled', 'fieldset', 'hidden', 'inactive', 'replaced', 'changed-value', 'focus-away']) {
      const before = success.value;
      trigger(success).click();
      const owner = success.closest('.settings-field');
      let fieldset;
      if (failure === 'scope') generation++;
      if (failure === 'readonly') success.readOnly = true;
      if (failure === 'disabled') success.disabled = true;
      if (failure === 'fieldset') { fieldset = success.closest('fieldset'); fieldset.disabled = true; }
      if (failure === 'hidden') owner.hidden = true;
      if (failure === 'inactive') active = false;
      if (failure === 'replaced') owner.replaceWith(owner.cloneNode(true));
      if (failure === 'changed-value') success.value += ' changed';
      if (failure === 'focus-away') document.getElementById('patron-barcode-label').focus();
      choose(success, 'pin_label');
      assert.equal(success.value, before + (failure === 'changed-value' ? ' changed' : ''), failure);
      if (failure === 'replaced') field('successMessage').replaceWith(owner);
      success.readOnly = false; success.disabled = false; owner.hidden = false; active = true;
      if (fieldset) fieldset.disabled = false;
      success.value = before; helper.refresh();
    }
    success.disabled = true; helper.refresh(); assert.equal(trigger(success).disabled, true);
    success.disabled = false; helper.refresh(); assert.equal(trigger(success).disabled, false);
    assert.equal(count, 3);
    helper.clear();
    console.log('Patron field capabilities, insertion, keyboard, drafts and scope ownership passed');
  } finally { dom.window.close(); fs.rmSync(temporary, { recursive: true, force: true }); }
})().catch(error => { console.error(error); process.exitCode = 1; });
