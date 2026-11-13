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
  fs.copyFileSync(path.join(frontend, 'staff', 'js', 'placeholder-editing.js'), path.join(temporary, 'placeholder-editing.js'));
  fs.writeFileSync(path.join(temporary, 'package.json'), '{"type":"module"}');
  const dom = new JSDOM(fs.readFileSync(path.join(frontend, 'staff', 'index.html'), 'utf8'));
  try {
    global.window = dom.window;
    global.document = dom.window.document;
    const { PLACEHOLDERS, supportedPlaceholderKeys, exampleTemplateText, createEmailTemplatePlaceholders } =
      await import(pathToFileURL(path.join(temporary, 'helper.mjs')).href);
    const keys = ['name', 'title', 'author', 'format', 'barcode', 'firstName', 'lastName'];
    assert.deepEqual(PLACEHOLDERS.map(item => item.key), keys);
    for (const key of ['suggestion_submitted', 'purchase_approved', 'already_owned', 'rejected']) {
      assert.deepEqual(supportedPlaceholderKeys(key, 'builtin'), keys);
      assert.deepEqual(supportedPlaceholderKeys(key, 'additional'), []);
    }
    assert.deepEqual(supportedPlaceholderKeys('suggestion_submitted'), [], 'A key alone cannot establish sending usage.');
    assert.deepEqual(supportedPlaceholderKeys('rejection:timeout', 'additional'), ['title']);
    assert.deepEqual(supportedPlaceholderKeys('hold_placed', 'builtin'), []);
    assert.deepEqual(supportedPlaceholderKeys('unknown', 'additional'), []);
    assert.equal(exampleTemplateText('{{name}}\n{{title}} {{unknown}} {{Title}}', 'suggestion_submitted', 'builtin'),
      'Alex Reader\nExample title {{unknown}} {{Title}}');
    assert.equal(exampleTemplateText('{{name}} {{title}}', 'rejection:timeout', 'additional'), '{{name}} Example title');
    assert.equal(exampleTemplateText('{{title}}', 'hold_placed', 'builtin'), '{{title}}');
    const root = document.getElementById('settings-view');
    const panel = document.getElementById('settings-templates');
    root.hidden = false;
    document.getElementById('workspace').hidden = false;
    document.getElementById('settings-form').hidden = false;
    panel.hidden = false;
    let generation = 0, scope = 'system', actor = '1', active = true, inputCount = 0;
    const context = () => `${generation}/${scope}/${actor}`;
    const helper = createEmailTemplatePlaceholders({ root, panel,
      listen: (target, name, callback, capture) => target.addEventListener(name, callback, capture),
      captureContext: context, isContextCurrent: captured => captured === context(), isActive: () => active });
    panel.addEventListener('input', () => inputCount++);
    helper.populate(keys);
    assert.equal(panel.querySelector('#template-placeholder-help'), null);
    assert.equal(panel.querySelectorAll('.settings-template[data-template-audience="patron"] [data-template-toggle][aria-expanded="false"]').length, 5);
    assert.equal(panel.querySelectorAll('.settings-template[data-template-audience="patron"] .settings-template-details[hidden]').length, 5);
    const owner = control => control.closest('.settings-template-card');
    const open = control => {
      const toggle = owner(control).querySelector('[data-template-toggle]');
      if (toggle.getAttribute('aria-expanded') === 'false') toggle.click();
    };
    const trigger = control => control.closest('.settings-template-field').querySelector('.template-placeholder-trigger');
    const popup = control => document.getElementById(trigger(control).getAttribute('aria-controls'));
    const pick = (control, key) => {
      trigger(control).click();
      const button = popup(control).querySelector(`[data-placeholder-key="${key}"]`);
      button.focus(); button.click();
    };
    const focus = (control, value, start, end = start) => {
      open(control);
      if (value !== undefined) control.value = value;
      control.focus(); control.setSelectionRange(start, end);
    };
    const subject = document.getElementById('email-submit-subject');
    const body = document.getElementById('email-purchase-approved-body');
    const before = [...document.getElementById('settings-form').elements]
      .filter(control => !control.closest('[data-settings-ignore="true"]')).map(control => [control.id, control.value]);
    open(subject); open(body);
    owner(subject).querySelector('[data-template-toggle]').click();
    assert.equal(owner(body).querySelector('[data-template-toggle]').getAttribute('aria-expanded'), 'true');
    assert.deepEqual([...document.getElementById('settings-form').elements]
      .filter(control => !control.closest('[data-settings-ignore="true"]')).map(control => [control.id, control.value]), before);
    assert.equal(inputCount, 0, 'Disclosure alone never mutates the settings draft.');
    focus(subject, 'É & one two!', 4, 7);
    pick(subject, 'title');
    assert.equal(subject.value, 'É & {{title}} two!');
    assert.equal(document.activeElement, subject);
    assert.equal(subject.selectionStart, 13);
    pick(subject, 'author');
    assert.equal(subject.value, 'É & {{title}}{{author}} two!');
    assert.equal(inputCount, 2);
    focus(body, 'First\nSecond & é', 6, 12);
    subject.dispatchEvent(new window.Event('select', { bubbles: true }));
    subject.dispatchEvent(new window.Event('keyup', { bubbles: true }));
    pick(body, 'name');
    assert.equal(body.value, 'First\n{{name}} & é', 'A contextual field owns insertion despite stale events elsewhere.');
    body.value = '<img src=x onerror=alert(1)>\n{{name}} {{unknown}}';
    body.dispatchEvent(new window.Event('input', { bubbles: true }));
    assert.equal(owner(body).querySelector('.settings-template-preview-body').textContent,
      '<img src=x onerror=alert(1)>\nAlex Reader {{unknown}}');
    assert.equal(owner(body).querySelector('.settings-template-preview img'), null, 'Preview uses text, never HTML.');
    assert.match(owner(body).querySelector('.settings-template-body-excerpt').textContent, /{{unknown}}/);
    focus(body, 'Native selected after', 7, 15);
    let nativeCalls = 0;
    document.execCommand = (command, ui, token) => {
      nativeCalls++;
      assert.equal(command, 'insertText');
      document.activeElement.setRangeText(token, document.activeElement.selectionStart, document.activeElement.selectionEnd, 'end');
      document.activeElement.dispatchEvent(new window.Event('input', { bubbles: true }));
      return true;
    };
    body.scrollTop = 22; body.scrollLeft = 3;
    const countBeforeNative = inputCount;
    pick(body, 'title');
    assert.equal(nativeCalls, 1);
    assert.equal(inputCount, countBeforeNative + 1, 'Native input is not dispatched twice.');
    assert.equal(body.value, 'Native {{title}} after');
    assert.equal(body.scrollTop, 22); assert.equal(body.scrollLeft, 3);
    focus(body, 'Native selected after', 7, 15);
    body.scrollTop = 988;
    trigger(body).focus(); trigger(body).click();
    const beforeBodyScroll = inputCount;
    body.scrollTop = 80;
    body.dispatchEvent(new window.Event('scroll'));
    assert.equal(trigger(body).getAttribute('aria-expanded'), 'false');
    assert.equal(document.activeElement, trigger(body));
    assert.equal(body.value, 'Native selected after');
    assert.equal(inputCount, beforeBodyScroll, 'Scrolling and dismissing leave the Body draft unchanged.');
    trigger(body).click();
    popup(body).querySelector('[data-placeholder-key="title"]').click();
    assert.equal(body.value, 'Native {{title}} after');
    assert.equal(body.scrollTop, 80, 'Reopening uses the current Body view after a scroll dismissed its picker.');
    focus(subject, 'Native selected after', 7, 15);
    subject.scrollLeft = 47;
    trigger(subject).focus();
    subject.scrollLeft = 0; // Chromium resets a single-line view after blur.
    trigger(subject).click();
    subject.dispatchEvent(new window.Event('scroll'));
    assert.equal(trigger(subject).getAttribute('aria-expanded'), 'true',
      'The queued blur scroll already matches the newly opened picker anchor.');
    popup(subject).querySelector('[data-placeholder-key="title"]').click();
    assert.equal(subject.value, 'Native {{title}} after');
    assert.equal(subject.scrollLeft, 47, 'The native edit starts in its owned pre-blur horizontal view.');
    focus(subject, 'Native selected after', 7, 15); subject.scrollLeft = 47; trigger(subject).focus();
    subject.scrollLeft = 0; // Retain the native single-line blur-reset setup.
    trigger(subject).click();
    const beforeSubjectScroll = inputCount;
    subject.scrollLeft = 100;
    subject.dispatchEvent(new window.Event('scroll'));
    assert.equal(trigger(subject).getAttribute('aria-expanded'), 'false');
    assert.equal(document.activeElement, trigger(subject));
    assert.equal(subject.value, 'Native selected after');
    assert.equal(inputCount, beforeSubjectScroll, 'A genuine Subject scroll only dismisses its picker.');
    trigger(subject).click();
    popup(subject).querySelector('[data-placeholder-key="title"]').click();
    assert.equal(subject.value, 'Native {{title}} after');
    assert.equal(subject.scrollLeft, 100, 'A genuine later Subject scroll retires its earlier pre-blur view.');
    focus(subject, 'Native selected after', 7, 15); subject.scrollLeft = 47; trigger(subject).focus();
    subject.scrollLeft = 0;
    helper.populate(keys); open(subject);
    trigger(subject).click(); popup(subject).querySelector('[data-placeholder-key="title"]').click();
    assert.equal(subject.scrollLeft, 0, 'Accepted population retires an earlier pre-blur scroll snapshot.');
    delete document.execCommand;
    open(body);

    const saveBar = root.querySelector('.settings-save-bar');
    const sender = document.getElementById('email-template-from-name');
    const focusEnvironment = { scrollBy: window.scrollBy, requestAnimationFrame: window.requestAnimationFrame,
      visualViewport: window.visualViewport, innerHeight: window.innerHeight };
    const revealFrames = [], pageScrolls = [];
    let fieldTop = 254;
    window.innerHeight = 402;
    window.visualViewport = { offsetTop: 0, height: 402 };
    window.requestAnimationFrame = callback => revealFrames.push(callback);
    window.scrollBy = options => { pageScrolls.push(options); fieldTop -= options.top; };
    body.getBoundingClientRect = () => ({ top: fieldTop, bottom: fieldTop + 180, width: 582, height: 180 });
    saveBar.getBoundingClientRect = () => ({ top: 270, bottom: 402, height: 132 });
    saveBar.style.position = 'sticky';
    saveBar.classList.add('attention');
    const flushReveals = () => revealFrames.splice(0).forEach(callback => callback());
    sender.focus();
    body.value = 'First\nSecond & é'; body.setSelectionRange(6, 12);
    body.scrollTop = 22; body.scrollLeft = 3;
    const inputsBeforeReveal = inputCount;
    body.focus();
    assert.deepEqual(pageScrolls, [{ top: 170, behavior: 'instant' }],
      'Keyboard focus reveals the complete editor above the actual wrapped save bar with focus-ring clearance.');
    assert.equal(body.getBoundingClientRect().bottom, 264);
    assert.deepEqual([body.selectionStart, body.selectionEnd, body.scrollTop, body.scrollLeft], [6, 12, 22, 3]);
    assert.equal(inputCount, inputsBeforeReveal, 'Revealing focus cannot dirty the draft.');
    fieldTop = 254;
    flushReveals();
    assert.equal(pageScrolls.length, 2, 'A settled-focus frame corrects a later native focus scroll.');
    assert.equal(body.getBoundingClientRect().bottom, 264);
    sender.focus(); fieldTop = -20; body.focus();
    assert.deepEqual(pageScrolls.at(-1), { top: -26, behavior: 'instant' }, 'An editor above the viewport is revealed too.');
    flushReveals();
    sender.focus(); fieldTop = 254; body.focus(); fieldTop = 254;
    const scrollsBeforeStaleScope = pageScrolls.length;
    scope = 'retired-focus-scope';
    flushReveals();
    assert.equal(pageScrolls.length, scrollsBeforeStaleScope, 'A queued reveal cannot scroll a replacement Settings context.');
    scope = 'system';
    sender.focus(); fieldTop = 254; body.focus();
    const scrollsBeforeReturn = pageScrolls.length;
    trigger(body).click();
    fieldTop = 254;
    assert.equal(popup(body).contains(document.activeElement), true);
    assert.equal(pageScrolls.length, scrollsBeforeReturn, 'Picker-button focus does not reveal an editor.');
    popup(body).querySelector('[data-placeholder-key="title"]').click();
    assert.equal(document.activeElement, body);
    assert.equal(body.value, 'First\n{{title}} & é');
    flushReveals();
    assert.equal(pageScrolls.length, scrollsBeforeReturn,
      'Focus away into the picker and insertion back into the same Body retire every older queued reveal.');
    assert.deepEqual([body.scrollTop, body.scrollLeft], [22, 3], 'Insertion preserves both editor scroll offsets.');
    assert.equal(inputCount, inputsBeforeReveal + 1, 'Only insertion edits the draft during focus restoration.');
    sender.focus(); fieldTop = 254; body.focus(); fieldTop = 254;
    const scrollsBeforePopulation = pageScrolls.length;
    helper.populate(keys); open(body);
    flushReveals();
    assert.equal(pageScrolls.length, scrollsBeforePopulation, 'Population retires an earlier reveal even if the same editor opens again.');
    saveBar.style.position = 'static';
    window.innerHeight = 201; window.visualViewport = { offsetTop: 0, height: 201 };
    sender.focus(); fieldTop = 100; body.focus();
    assert.deepEqual(pageScrolls.at(-1), { top: 85, behavior: 'instant' }, 'Native 400% focus also respects a static bar and the short viewport.');
    fieldTop = 160;
    body.dispatchEvent(new window.Event('input', { bubbles: true }));
    assert.deepEqual(pageScrolls.at(-1), { top: 145, behavior: 'instant' }, 'Ordinary typing reveals Body after its header excerpt grows.');
    flushReveals();
    trigger(body).click(); fieldTop = 160;
    const scrollsBeforeStaticInsertion = pageScrolls.length;
    popup(body).querySelector('[data-placeholder-key="title"]').click();
    flushReveals();
    assert.equal(pageScrolls.length, scrollsBeforeStaticInsertion, 'Placeholder insertion still suppresses measured reveal and preserves exact scroll.');
    trigger(body).focus(); fieldTop = 160; trigger(body).click();
    assert.deepEqual(pageScrolls.at(-1), { top: 55, behavior: 'instant' }, 'Opening Body at native 400% reveals its center while its trigger remains above it.');
    const scrollsBeforeQueuedEvent = pageScrolls.length;
    window.dispatchEvent(new window.Event('scroll'));
    assert.equal(trigger(body).getAttribute('aria-expanded'), 'true', 'An already-applied focus scroll cannot dismiss the newly anchored picker.');
    popup(body).querySelector('[data-placeholder-key="title"]').click(); flushReveals();
    assert.equal(pageScrolls.length, scrollsBeforeQueuedEvent, 'Direct picker insertion preserves its post-opening page scroll.');
    delete body.getBoundingClientRect;
    delete saveBar.getBoundingClientRect;
    saveBar.style.removeProperty('position'); saveBar.classList.remove('attention');
    Object.assign(window, focusEnvironment);

    for (const failure of ['unrelated', 'outside', 'blur', 'window-blur', 'readOnly', 'disabled', 'fieldset', 'hidden',
      'scope', 'actor', 'generation', 'inactive', 'value', 'collapse', 'refresh']) {
      body.readOnly = false; body.disabled = false; body.closest('fieldset').disabled = false;
      body.closest('.settings-template-field').hidden = false; active = true;
      focus(body, 'Unchanged', 0);
      trigger(body).click();
      const staleButton = popup(body).querySelector('[data-placeholder-key="title"]');
      staleButton.focus();
      if (failure === 'unrelated') document.getElementById('email-template-from-name').focus();
      if (failure === 'outside') document.getElementById('request-search').focus();
      if (failure === 'blur') { staleButton.blur(); staleButton.focus(); }
      if (failure === 'window-blur') window.dispatchEvent(new window.Event('blur'));
      if (failure === 'readOnly') body.readOnly = true;
      if (failure === 'disabled') body.disabled = true;
      if (failure === 'fieldset') body.closest('fieldset').disabled = true;
      if (failure === 'hidden') body.closest('.settings-template-field').hidden = true;
      if (failure === 'scope') scope = '2';
      if (failure === 'actor') actor = '2';
      if (failure === 'generation') generation++;
      if (failure === 'inactive') active = false;
      if (failure === 'value') body.value = 'Changed elsewhere';
      if (failure === 'collapse') owner(body).querySelector('[data-template-toggle]').click();
      if (failure === 'refresh') helper.refresh();
      staleButton.click();
      assert.equal(body.value, failure === 'value' ? 'Changed elsewhere' : 'Unchanged', `${failure} prevents stale insertion.`);
    }
    body.closest('.settings-template-field').hidden = false;
    body.closest('fieldset').disabled = false; body.disabled = false; active = true;
    focus(body, 'Escape', 0);
    trigger(body).click();
    document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    assert.equal(document.activeElement, trigger(body));
    assert.equal(trigger(body).getAttribute('aria-expanded'), 'false');
    for (const outside of [window, document, panel, body]) {
      focus(body, 'Scroll keeps this draft', 7);
      const inputsBeforeScroll = inputCount;
      trigger(body).click();
      const option = popup(body).querySelector('[data-placeholder-key="title"]');
      option.focus();
      for (const inside of [popup(body), option.querySelector('small')]) {
        inside.dispatchEvent(new window.Event('scroll'));
        assert.equal(trigger(body).getAttribute('aria-expanded'), 'true', 'Scrolling the picker options keeps it open.');
        assert.equal(document.activeElement, option);
      }
      if (outside === window || outside === document) window.scrollY++;
      if (outside === body) body.scrollTop++;
      outside.dispatchEvent(new window.Event('scroll'));
      assert.equal(trigger(body).getAttribute('aria-expanded'), 'false', 'Later page/editor/ancestor scroll dismisses the anchored picker.');
      assert.equal(popup(body).hidden, true);
      assert.equal(document.activeElement, trigger(body), 'Dismissal returns focus without changing the remembered editor.');
      assert.equal(body.value, 'Scroll keeps this draft');
      assert.equal(inputCount, inputsBeforeScroll, 'Scrolling and dismissing cannot dirty or insert into the draft.');
      option.click();
      assert.equal(body.value, 'Scroll keeps this draft', 'A dismissed option cannot activate its retired selection.');
    }
    const hold = document.getElementById('email-hold-subject');
    open(hold);
    assert.equal(trigger(hold).disabled, true);
    assert.match(owner(hold).querySelector('.settings-template-preview-note').textContent, /Editing preview only/);
    subject.value = ''; body.value = '';
    owner(subject).querySelector('[data-template-toggle]').click();
    assert.throws(() => helper.validate([{ templateKey: 'suggestion_submitted', subject: '', body: '' }], true), /Subject is required/);
    assert.equal(document.activeElement, subject);
    assert.equal(subject.getAttribute('aria-invalid'), 'true');
    assert.equal(owner(subject).querySelector('.settings-template-details').hidden, false);
    assert.doesNotThrow(() => helper.validate([], true), 'Untouched blank templates are not proposed operations.');
    assert.doesNotThrow(() => helper.validate([{ templateKey: 'suggestion_submitted', subject: '', body: '' }], false),
      'Explicit blank lineage overrides retain their runtime fallback contract.');
    owner(subject).querySelector('[data-template-toggle]').click();
    subject.required = true;
    subject.checkValidity();
    assert.equal(owner(subject).querySelector('.settings-template-details').hidden, false, 'Native invalid also reveals its mounted field.');
    subject.required = false;
    const senderInvalid = new window.Event('invalid', { cancelable: true });
    document.getElementById('email-template-from-address').dispatchEvent(senderInvalid);
    assert.equal(senderInvalid.defaultPrevented, false, 'Sender validation keeps its native error/focus behavior.');
    let retiredNotices = 0;
    helper.showError(subject, 'Scoped Subject error');
    helper.ownValidationNotice(subject, 'Scoped Subject error', () => retiredNotices++);
    helper.showError(body, 'Another card error');
    body.value = 'Correcting another card';
    body.dispatchEvent(new window.Event('input', { bubbles: true }));
    assert.equal(retiredNotices, 0, 'Another card cannot retire the owned Subject notice.');
    scope = 'replacement-scope';
    helper.refresh();
    assert.equal(retiredNotices, 1, 'A retired Settings context releases its own validation notice.');
    subject.value = 'Late old-context edit';
    subject.dispatchEvent(new window.Event('input', { bubbles: true }));
    assert.equal(retiredNotices, 1, 'A late edit cannot clear another notice after its owner retires.');
    helper.showError(subject, 'Population Subject error');
    helper.ownValidationNotice(subject, 'Population Subject error', () => retiredNotices++);
    helper.populate(['title']);
    assert.equal(retiredNotices, 2, 'Accepted population retires the matching notice once.');
    assert.equal(subject.hasAttribute('aria-invalid'), false, 'Accepted populations clear prior validation state.');
    open(subject);
    trigger(subject).click();
    assert.deepEqual([...popup(subject).querySelectorAll('[data-placeholder-key]')].map(node => node.dataset.placeholderKey), ['title']);
    for (const control of [subject, body]) {
      open(control);
      if (trigger(control).getAttribute('aria-expanded') !== 'true') trigger(control).click();
      assert.equal(popup(control).querySelector('details, summary, dl'), null);
      assert.doesNotMatch(popup(control).textContent, /What (each placeholder|this) means/);
      const button = popup(control).querySelector('[data-placeholder-key="title"]');
      assert.equal(button.getAttribute('aria-label'), 'Insert Requested title — {{title}}');
      assert.equal(button.querySelector('span').textContent, 'Requested title');
      assert.equal(button.querySelector('code').textContent, '{{title}}');
      const description = document.getElementById(button.getAttribute('aria-describedby'));
      assert.equal(button.contains(description), true);
      assert.equal(description.textContent, 'Requested title. Example: Example title');
      document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    }
    console.log('Email accordion, safe previews, contextual capabilities, native insertion, stale ownership and collapsed validation passed');
  } finally {
    dom.window.close();
    fs.rmSync(temporary, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exit(1); });
