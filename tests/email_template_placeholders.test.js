const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { JSDOM } = require('jsdom');

(async () => {
  const frontend = path.join(__dirname, '..', 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-placeholder-'));
  fs.copyFileSync(path.join(frontend, 'staff', 'js', 'email-template-placeholders.js'), path.join(temporary, 'helper.mjs'));
  const dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'));
  try {
    global.window = dom.window;
    global.document = dom.window.document;
    const { PLACEHOLDERS, supportedPlaceholderKeys, createEmailTemplatePlaceholders } = await import(pathToFileURL(path.join(temporary, 'helper.mjs')).href);
    const keys = ['name', 'title', 'author', 'format', 'barcode', 'firstName', 'lastName'];
    assert.deepEqual(PLACEHOLDERS.map(item => item.key), keys);
    for (const key of ['suggestion_submitted', 'purchase_approved', 'already_owned', 'rejected']) {
      assert.deepEqual(supportedPlaceholderKeys(key), keys);
    }
    assert.deepEqual(supportedPlaceholderKeys('rejection:timeout'), ['title']);
    assert.deepEqual(supportedPlaceholderKeys('hold_placed'), []);
    assert.deepEqual(supportedPlaceholderKeys('unknown'), []);
    const root = document.getElementById('settings-view');
    const panel = document.getElementById('settings-templates');
    root.hidden = false;
    document.getElementById('workspace').hidden = false;
    document.getElementById('settings-form').hidden = false;
    panel.hidden = false;
    let generation = 0, scope = 'system', actor = '1', active = true, inputCount = 0;
    const context = () => `${generation}/${scope}/${actor}`;
    const helper = createEmailTemplatePlaceholders({ root, panel,
      listen: (target, name, callback) => target.addEventListener(name, callback),
      captureContext: context, isContextCurrent: captured => captured === context(), isActive: () => active });
    panel.addEventListener('input', () => inputCount++);
    helper.populate(keys);
    const buttons = () => [...panel.querySelectorAll('[data-placeholder-key]')];
    const button = key => panel.querySelector(`[data-placeholder-key="${key}"]`);
    const subject = document.getElementById('email-submit-subject');
    const body = document.getElementById('email-purchase-approved-body');
    const focus = (control, value, start, end = start) => {
      if (value !== undefined) control.value = value;
      document.getElementById('settings-nav-templates').focus();
      control.focus();
      control.setSelectionRange(start, end);
      control.dispatchEvent(new window.Event('select', { bubbles: true }));
    };
    button('title').click();
    assert.equal(inputCount, 0, 'Browsing with no target must not dirty a settings draft.');
    focus(subject, 'É & one two!', 4, 7);
    const stableButton = button('title');
    subject.dispatchEvent(new window.Event('select', { bubbles: true }));
    assert.equal(button('title'), stableButton, 'Range updates cannot replace a button while focus is transferring to it.');
    const originalActive = Object.getOwnPropertyDescriptor(document, 'activeElement');
    Object.defineProperty(document, 'activeElement', { configurable: true, value: document.body });
    subject.dispatchEvent(new window.Event('change', { bubbles: true }));
    if (originalActive) Object.defineProperty(document, 'activeElement', originalActive);
    else delete document.activeElement;
    assert.equal(button('title'), stableButton, 'Native change during blur must not retire the pending helper focus target.');
    assert.match(panel.querySelector('#template-placeholder-target').textContent, /Submission confirmation · Subject/);
    button('title').focus();
    button('title').click();
    assert.equal(subject.value, 'É & {{title}} two!');
    assert.equal(document.activeElement, subject);
    assert.equal(subject.selectionStart, 13);
    button('author').click();
    assert.equal(subject.value, 'É & {{title}}{{author}} two!');
    assert.equal(inputCount, 2);
    focus(body, 'First\nSecond & é', 6, 12);
    subject.dispatchEvent(new window.Event('select', { bubbles: true }));
    subject.dispatchEvent(new window.Event('keyup', { bubbles: true }));
    subject.dispatchEvent(new window.Event('mouseup', { bubbles: true }));
    button('name').click();
    assert.equal(body.value, 'First\n{{name}} & é', 'Stale events from the previous editor cannot steal insertion.');
    assert.equal(subject.value, 'É & {{title}}{{author}} two!');

    for (const failure of ['unrelated', 'outside', 'blur', 'helper-blur', 'window-blur', 'readOnly', 'disabled', 'fieldset', 'hidden', 'scope', 'actor', 'generation', 'inactive']) {
      body.readOnly = false;
      body.disabled = false;
      body.closest('fieldset').disabled = false;
      body.closest('label').hidden = false;
      active = true;
      helper.clear();
      focus(body, 'Unchanged', 0);
      const staleButton = button('title');
      if (failure === 'unrelated') document.getElementById('email-template-from-name').focus();
      if (failure === 'outside') document.getElementById('request-search').focus();
      if (failure === 'blur') { body.blur(); button('title').focus(); }
      if (failure === 'helper-blur') { button('title').focus(); button('title').blur(); button('title').focus(); }
      if (failure === 'window-blur') window.dispatchEvent(new window.Event('blur'));
      if (failure === 'readOnly') body.readOnly = true;
      if (failure === 'disabled') body.disabled = true;
      if (failure === 'fieldset') body.closest('fieldset').disabled = true;
      if (failure === 'hidden') body.closest('label').hidden = true;
      if (failure === 'scope') scope = '2';
      if (failure === 'actor') actor = '2';
      if (failure === 'generation') generation++;
      if (failure === 'inactive') active = false;
      // Activation on the current list is revalidated even if DOM/context changed
      // after it was focused. A removed button itself has no insertion listener.
      (button('title') || staleButton).click();
      assert.equal(body.value, 'Unchanged', `${failure} must prevent insertion.`);
    }
    body.closest('label').hidden = false;
    body.closest('fieldset').disabled = false;
    body.disabled = false;
    active = true;
    helper.clear();
    focus(document.getElementById('email-hold-subject'), 'Hold draft', 0);
    assert.equal(buttons().length, 0, 'An unconsumed configured hold template must not advertise capabilities.');
    assert.match(panel.querySelector('#template-placeholder-guidance').textContent, /not verified/);

    const row = document.createElement('div');
    row.className = 'settings-template-row';
    const key = document.createElement('input');
    key.className = 'template-key'; key.value = 'rejection:dynamic';
    const name = document.createElement('input');
    name.className = 'template-display-name'; name.value = 'Dynamic rejection';
    const dynamic = document.createElement('textarea');
    dynamic.dataset.templateField = 'body';
    const override = document.createElement('input');
    override.type = 'checkbox'; override.className = 'settings-template-override'; override.checked = true;
    row.append(key, name, dynamic, override);
    panel.append(row);
    focus(dynamic, 'Text', 4);
    assert.deepEqual(buttons().map(item => item.textContent), ['{{title}}']);
    assert.match(panel.querySelector('#template-placeholder-target').textContent, /Dynamic rejection · Body/);
    button('title').click();
    assert.equal(dynamic.value, 'Text{{title}}');
    key.value = 'unknown-template';
    focus(dynamic, undefined, dynamic.value.length);
    assert.equal(buttons().length, 0, 'An editable new key uses its current identity, not stale row metadata.');
    key.value = 'rejection:dynamic';
    focus(dynamic, undefined, dynamic.value.length);
    override.checked = false;
    button('title').click();
    assert.equal(dynamic.value, 'Text{{title}}', 'Override ownership must be revalidated at activation.');
    override.checked = true;
    focus(dynamic, 'Removed row', 0);
    row.remove();
    button('title').click();
    assert.equal(dynamic.value, 'Removed row');
    assert.equal(inputCount, 4, 'Each successful activation emits exactly one input event.');
    assert.equal(panel.querySelectorAll('#template-placeholder-reference dt').length, 7);
    assert.match(panel.querySelector('#template-placeholder-reference').textContent, /00000000000000/);
    helper.clear();
    console.log('Contextual placeholder catalog, focus ownership, insertion ranges and stale target fences passed');
  } finally {
    dom.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exit(1); });
