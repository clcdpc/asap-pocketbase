const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { pathToFileURL } = require('url');
const { JSDOM } = require('jsdom');

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
  await Promise.resolve();
}

(async () => {
  const repositoryRoot = path.join(__dirname, '..');
  const frontendRoot = path.join(repositoryRoot, 'src', 'Asap.Web', 'Frontend');
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'asap-email-placeholders-'));
  fs.cpSync(path.join(frontendRoot, 'staff'), path.join(temporary, 'staff'), { recursive: true });
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}\n');

  try {
    const module = await import(pathToFileURL(
      path.join(temporary, 'staff', 'js', 'email-template-placeholders.js')
    ).href);

    assert.deepStrictEqual(
      module.PLACEHOLDER_CATALOG.map(item => item.token),
      ['{{name}}', '{{title}}', '{{author}}', '{{format}}', '{{barcode}}', '{{firstName}}', '{{lastName}}']
    );
    assert.deepStrictEqual(
      module.getTemplatePlaceholderTokens('suggestion_submitted').map(item => item.key),
      ['name', 'title', 'author', 'format', 'barcode', 'firstName', 'lastName']
    );
    assert.deepStrictEqual(
      module.getTemplatePlaceholderTokens('rejection:timeout').map(item => item.key),
      ['title']
    );
    assert.deepStrictEqual(module.getTemplatePlaceholderTokens('purchase_approved'), []);
    assert.strictEqual(module.getTemplatePlaceholderCapability('future-template').status, 'unknown');

    const dom = new JSDOM(fs.readFileSync(path.join(frontendRoot, 'staff', 'index.html'), 'utf8'), {
      url: 'http://localhost/staff/'
    });
    global.window = dom.window;
    global.document = dom.window.document;
    global.Node = dom.window.Node;
    global.Event = dom.window.Event;

    document.getElementById('workspace').hidden = false;
    document.getElementById('settings-view').hidden = false;
    const panel = document.getElementById('settings-templates');
    panel.hidden = false;
    let scope = 'system';
    const helper = module.createEmailTemplatePlaceholderHelper({
      panel,
      getContextKey: () => scope
    });
    const buttons = () => [...document.querySelectorAll('#email-template-placeholder-buttons button')];
    const buttonFor = key => document.querySelector(`[data-placeholder-token="${key}"]`);

    assert.strictEqual(buttons().length, 7);
    assert.ok(buttons().every(button => button.disabled));

    const dirtyEvents = [];
    const subject = document.getElementById('email-submit-subject');
    subject.addEventListener('input', () => dirtyEvents.push(subject.value));
    subject.value = 'prefix / suffix';
    subject.focus();
    subject.setSelectionRange(9, 9);
    subject.dispatchEvent(new dom.window.Event('select', { bubbles: true }));
    assert.match(document.getElementById('email-template-placeholder-target').textContent, /Submission confirmation · Subject/);
    assert.strictEqual(buttons().length, 7);
    assert.ok(buttonFor('title').getAttribute('aria-label').includes('Insert {{title}}'));
    buttonFor('title').click();
    assert.strictEqual(subject.value, 'prefix / {{title}}suffix');
    assert.strictEqual(subject.selectionStart, 18);
    assert.strictEqual(subject.selectionEnd, 18);
    assert.strictEqual(dirtyEvents.length, 1);

    subject.setSelectionRange(0, subject.value.length);
    subject.dispatchEvent(new dom.window.Event('select', { bubbles: true }));
    buttonFor('author').click();
    assert.strictEqual(subject.value, '{{author}}');
    assert.strictEqual(subject.selectionStart, '{{author}}'.length);
    assert.strictEqual(subject.selectionEnd, '{{author}}'.length);
    assert.strictEqual(dirtyEvents.length, 2);

    const body = document.getElementById('email-submit-body');
    body.value = 'Café & friends';
    body.focus();
    body.setSelectionRange('Café & '.length, 'Café & '.length);
    body.dispatchEvent(new dom.window.Event('select', { bubbles: true }));
    buttonFor('format').click();
    assert.strictEqual(body.value, 'Café & {{format}}friends');

    const purchaseSubject = document.getElementById('email-purchase-approved-subject');
    purchaseSubject.focus();
    purchaseSubject.setSelectionRange(0, 0);
    purchaseSubject.dispatchEvent(new dom.window.Event('select', { bubbles: true }));
    assert.strictEqual(buttons().length, 0);
    assert.match(document.getElementById('email-template-placeholder-status').textContent, /No ASP\.NET sending path/);
    purchaseSubject.value = 'unchanged';
    helper.refresh();
    assert.strictEqual(purchaseSubject.value, 'unchanged');

    const dynamicRow = document.createElement('div');
    dynamicRow.className = 'settings-editor-row settings-template-row';
    const dynamicKey = document.createElement('input');
    dynamicKey.className = 'template-key';
    dynamicKey.value = 'rejection:timeout';
    const dynamicSubject = document.createElement('input');
    dynamicSubject.className = 'template-subject';
    dynamicSubject.dataset.placeholderTarget = 'true';
    dynamicSubject.dataset.templateField = 'subject';
    dynamicSubject.value = 'before';
    dynamicRow.append(dynamicKey, dynamicSubject);
    document.getElementById('email-templates-editor').append(dynamicRow);
    dynamicSubject.focus();
    dynamicSubject.setSelectionRange(dynamicSubject.value.length, dynamicSubject.value.length);
    dynamicSubject.dispatchEvent(new dom.window.Event('select', { bubbles: true }));
    assert.strictEqual(buttons().length, 1);
    assert.strictEqual(buttons()[0].textContent, '{{title}}');
    buttons()[0].click();
    assert.strictEqual(dynamicSubject.value, 'before{{title}}');

    dynamicSubject.setSelectionRange(dynamicSubject.value.length, dynamicSubject.value.length);
    dynamicSubject.dispatchEvent(new dom.window.Event('select', { bubbles: true }));
    dynamicSubject.disabled = true;
    dynamicSubject.dispatchEvent(new dom.window.Event('change', { bubbles: true }));
    assert.match(document.getElementById('email-template-placeholder-status').textContent, /Enable the template override/);
    dynamicSubject.disabled = false;
    dynamicSubject.focus();
    dynamicSubject.setSelectionRange(dynamicSubject.value.length, dynamicSubject.value.length);
    dynamicSubject.dispatchEvent(new dom.window.Event('select', { bubbles: true }));
    dynamicRow.remove();
    await flush();
    assert.ok(buttons().every(button => button.disabled));

    subject.focus();
    subject.setSelectionRange(subject.value.length, subject.value.length);
    subject.dispatchEvent(new dom.window.Event('select', { bubbles: true }));
    scope = 'library-2';
    helper.refresh();
    assert.ok(buttons().every(button => button.disabled));
    assert.match(document.getElementById('email-template-placeholder-status').textContent, /current settings context/);

    helper.destroy();
    dom.window.close();
    console.log('Email placeholder catalog, capability filtering, selection insertion, dynamic rows, and context invalidation passed');
  } finally {
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exit(1);
});
