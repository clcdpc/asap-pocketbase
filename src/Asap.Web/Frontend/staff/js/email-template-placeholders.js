import { insertPlaceholderText } from './placeholder-editing.js';

// Patron-renderer contexts use these seven case-sensitive tokens. Named
// rejection templates also serve timeout mail, whose shared capability is title.
// Hold placement currently uses fixed content, not the configured hold template.
export const PLACEHOLDERS = [
  { key: 'name', name: 'Patron name', description: 'Patron’s full name; Library Patron when unavailable.', example: 'Alex Reader' },
  { key: 'title', name: 'Requested title', description: 'Requested title.', example: 'Example title' },
  { key: 'author', name: 'Author or creator', description: 'Request author or creator; may be blank.', example: 'Example creator' },
  { key: 'format', name: 'Material format', description: 'Requested material-format label.', example: 'Video Game' },
  { key: 'barcode', name: 'Library card barcode', description: 'Library-card barcode; use only when needed.', example: '00000000000000' },
  { key: 'firstName', name: 'Patron first name', description: 'Patron’s first name, when available.', example: 'Alex' },
  { key: 'lastName', name: 'Patron last name', description: 'Patron’s last name, when available.', example: 'Reader' }
];
const PATRON_CONTEXTS = new Set(['suggestion_submitted', 'purchase_approved', 'already_owned', 'rejected']);
const EVENTS = {
  suggestion_submitted: 'Sent when a suggestion is received',
  purchase_approved: 'Sent when a purchase is approved',
  already_owned: 'Sent when a suggested title is already owned',
  hold_placed: 'Configured text for editing; no verified sending path',
  rejected: 'Sent when a suggestion is rejected'
};
let nextCardId = 0;

export function supportedPlaceholderKeys(templateKey, usage) {
  if (usage === 'builtin' && PATRON_CONTEXTS.has(templateKey)) return PLACEHOLDERS.map(item => item.key);
  if (usage === 'additional' && /^rejection:/i.test(templateKey || '')) return ['title'];
  return [];
}

export function exampleTemplateText(text, templateKey, usage, advertised = PLACEHOLDERS.map(item => item.key)) {
  const supported = new Set(supportedPlaceholderKeys(templateKey, usage).filter(key => advertised.includes(key)));
  const examples = new Map(PLACEHOLDERS.map(item => [item.key, item.example]));
  return String(text ?? '').replace(/{{(\w+)}}/g, (token, key) => supported.has(key) ? examples.get(key) : token);
}

export function createEmailTemplatePlaceholders({ root, panel, listen, captureContext, isContextCurrent, isActive,
  onReveal = () => {}, onValidationError = () => {} }) {
  const document = root.ownerDocument;
  const view = document.defaultView;
  const cards = new WeakMap();
  let advertised = [];
  let opened = null;
  let validationNotice = null;
  let focusSequence = 0, restoringEditorFocus = false;
  const status = make('span', 'sr-only');
  status.setAttribute('role', 'status');
  status.setAttribute('aria-live', 'polite');
  panel.append(status);

  function make(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function identity(owner) {
    const additional = owner.matches('.settings-template-row');
    const key = additional ? owner.querySelector('.template-key')?.value.trim() || '' : owner.dataset.templateKey;
    const name = additional ? owner.querySelector('.template-display-name')?.value.trim() || 'New template'
      : owner.querySelector('legend')?.textContent.trim() || key;
    return { key, name, usage: additional ? 'additional' : 'builtin', kind: owner.dataset.templateKind || '',
      id: owner.dataset.templateUiId || key };
  }

  function editable(entry) {
    const { control, card } = entry;
    if (!isActive() || !root.isConnected || !control.isConnected || !panel.contains(card.owner) ||
        !card.owner.contains(control) || control.disabled || control.readOnly || control.matches(':disabled') ||
        control.closest('[hidden], [inert]') || root.querySelector('#settings-form')?.inert) return false;
    for (let ancestor = control; ancestor; ancestor = ancestor.parentElement) {
      const style = view.getComputedStyle(ancestor);
      if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
    }
    const override = card.owner.querySelector('.settings-template-override');
    return !override || override.checked;
  }

  function supported(card) {
    const target = identity(card.owner);
    return supportedPlaceholderKeys(target.key, target.usage).filter(key => advertised.includes(key));
  }

  function clear() {
    const prior = opened;
    opened = null;
    status.textContent = '';
    if (!prior) return;
    prior.entry.trigger.setAttribute('aria-expanded', 'false');
    const popup = prior.entry.popup;
    if (popup.hidePopover && popup.isConnected && popup.matches(':popover-open')) popup.hidePopover();
    else if (!popup.showPopover) popup.hidden = true;
  }

  function retireValidationNotice() {
    const notice = validationNotice;
    validationNotice = null;
    notice?.clear();
  }

  function revealFocusedControl(control, context, sequence, focusOwner = control) {
    if (restoringEditorFocus || sequence !== focusSequence || !isContextCurrent(context) || !isActive() ||
        document.activeElement !== focusOwner || !root.isConnected || !control.isConnected ||
        !panel.contains(control) || control.closest('[hidden], [inert]') ||
        root.querySelector('#settings-form')?.inert || control.disabled || control.matches(':disabled')) return;
    const bar = root.querySelector('.settings-save-bar.attention');
    const viewport = view.visualViewport;
    const top = (viewport?.offsetTop || 0) + 6;
    const viewportBottom = (viewport?.offsetTop || 0) + (viewport?.height || view.innerHeight) - 6;
    const field = control.getBoundingClientRect();
    if (!field.width || !field.height) return;
    const footer = bar && view.getComputedStyle(bar).position === 'sticky' ? bar.getBoundingClientRect() : null;
    const bottom = footer?.height && footer.top < viewportBottom && footer.bottom > top
      ? Math.min(viewportBottom, footer.top - 6) : viewportBottom;
    // At native 400%, Body plus its picker heading can exceed the viewport.
    // Opening reveals the editor's center while retaining its trigger above it
    // for Escape. Actual editor focus/ordinary typing reveals the full field.
    const revealBottom = focusOwner !== control &&
      field.height + focusOwner.getBoundingClientRect().height + 12 > bottom - top
      ? (field.top + field.bottom) / 2 : field.bottom;
    const scroll = field.top < top ? field.top - top : Math.max(0, revealBottom - bottom);
    if (scroll) view.scrollBy({ top: scroll, behavior: 'instant' });
  }

  function setExpanded(card, expanded) {
    if (!expanded && opened?.entry.card === card) clear();
    card.toggle.setAttribute('aria-expanded', String(expanded));
    card.details.hidden = !expanded;
    refreshCard(card);
  }

  function effectiveText(card, role) {
    const control = card.owner.querySelector(`[data-template-field="${role}"]`);
    const override = card.owner.querySelector('.settings-template-override');
    if (override && (!override.checked || !control.value.trim())) {
      return card.owner.dataset[role === 'subject' ? 'templateInheritedSubject' : 'templateInheritedBody'] ?? control.value;
    }
    return control.value;
  }

  function refreshCard(card) {
    const target = identity(card.owner);
    card.title.textContent = target.name;
    card.event.textContent = target.usage === 'builtin' ? EVENTS[target.key] || '' : 'Named template';
    const subject = effectiveText(card, 'subject');
    const body = effectiveText(card, 'body');
    const excerpt = value => {
      const text = value.replace(/\s+/g, ' ').trim() || '(blank)';
      return text.length > 120 ? `${text.slice(0, 119)}…` : text;
    };
    card.subjectExcerpt.textContent = `Subject: ${excerpt(subject)}`;
    card.bodyExcerpt.textContent = `Body: ${excerpt(body)}`;
    card.previewSubject.textContent = exampleTemplateText(subject, target.key, target.usage, advertised);
    card.previewBody.textContent = exampleTemplateText(body, target.key, target.usage, advertised);
    const keys = supported(card);
    card.note.textContent = !keys.length
      ? 'Editing preview only. Placeholder support and a sending path are not verified for this configured template. Unresolved placeholders remain literal. No email sent.'
      : target.usage === 'additional'
        ? 'Example only. Named rejection templates, including timeouts, share support for {{title}} only. Other placeholders remain literal. No email sent.'
        : 'Example only, using synthetic data. Unresolved placeholders remain literal; actual values may be blank. No email sent.';
    for (const entry of card.entries) {
      entry.trigger.disabled = !editable(entry) || !keys.length;
      entry.trigger.setAttribute('aria-label', `Insert placeholder into ${target.name} ${entry.role === 'body' ? 'Body' : 'Subject'}`);
    }
  }

  function renderPicker(entry) {
    const keys = supported(entry.card);
    entry.popup.replaceChildren();
    for (const item of PLACEHOLDERS.filter(item => keys.includes(item.key))) {
      const button = make('button');
      button.type = 'button';
      button.dataset.placeholderKey = item.key;
      button.setAttribute('aria-label', `Insert ${item.name} — {{${item.key}}}`);
      const description = make('small', '', item.description);
      description.id = `${entry.control.id}-${item.key}-description`;
      button.setAttribute('aria-describedby', description.id);
      button.append(make('span', '', item.name), make('code', '', `{{${item.key}}}`), description);
      entry.popup.append(button);
    }
    const reference = make('details', 'template-placeholder-reference');
    reference.append(make('summary', '', 'What each placeholder means'));
    const definitions = make('dl');
    for (const item of PLACEHOLDERS.filter(item => keys.includes(item.key))) {
      definitions.append(make('dt', '', `{{${item.key}}}`), make('dd', '', `${item.description} Example: ${item.example}`));
    }
    reference.append(definitions);
    entry.popup.append(reference);
  }

  function createEntry(card, control) {
    const role = control.dataset.templateField;
    if (!control.id) control.id = `${card.details.id}-${role}`;
    const oldLabel = control.parentElement;
    const field = make('div', 'settings-template-field');
    const heading = make('div', 'settings-template-field-heading');
    const label = make('label', '', role === 'body' ? 'Body' : 'Subject');
    label.htmlFor = control.id;
    const tools = make('span', 'template-placeholder-tools');
    tools.dataset.settingsIgnore = 'true';
    const trigger = make('button', 'secondary-button template-placeholder-trigger', 'Insert placeholder ▾');
    trigger.type = 'button';
    trigger.setAttribute('aria-expanded', 'false');
    const popup = make('div', 'template-placeholder-picker');
    popup.id = `${control.id}-placeholders`;
    popup.setAttribute('popover', 'auto');
    popup.setAttribute('role', 'group');
    popup.setAttribute('aria-label', `Placeholders for ${role === 'body' ? 'Body' : 'Subject'}`);
    trigger.setAttribute('aria-controls', popup.id);
    if (!popup.showPopover) popup.hidden = true;
    tools.append(trigger, popup);
    heading.append(label, tools);
    oldLabel.replaceWith(field);
    field.append(heading, control);
    const entry = { card, control, trigger, popup, role };
    listen(control, 'blur', () => {
      // blur runs before Chromium resets a single-line input's scrollLeft;
      // focusout runs after that reset. Keep this view owned by the same draft.
      entry.scrollBeforeBlur = control.localName === 'input' && editable(entry) ? { context: captureContext(), value: control.value,
        start: control.selectionStart, end: control.selectionEnd, scrollTop: control.scrollTop, scrollLeft: control.scrollLeft } : null;
    });
    listen(control, 'input', () => { entry.scrollBeforeBlur = null; });
    listen(trigger, 'click', () => {
      if (opened?.entry === entry) { clear(); return; }
      clear();
      if (!editable(entry) || !supported(card).length) { refreshCard(card); return; }
      revealFocusedControl(control, captureContext(), focusSequence, trigger);
      const start = control.selectionStart ?? control.value.length, end = control.selectionEnd ?? control.value.length;
      const prior = entry.scrollBeforeBlur;
      const scroll = document.activeElement !== control && prior && isContextCurrent(prior.context) &&
        prior.value === control.value && prior.start === start && prior.end === end ? prior : control;
      opened = { entry, context: captureContext(), ...identity(card.owner),
        pageX: view.scrollX, pageY: view.scrollY,
        anchorScrollTop: control.scrollTop, anchorScrollLeft: control.scrollLeft,
        scrollTop: scroll.scrollTop, scrollLeft: scroll.scrollLeft,
        value: control.value, override: card.owner.querySelector('.settings-template-override')?.checked,
        start, end };
      renderPicker(entry);
      trigger.setAttribute('aria-expanded', 'true');
      if (popup.showPopover) popup.showPopover(); else popup.hidden = false;
      const bounds = trigger.getBoundingClientRect();
      const width = Math.min(384, Math.max(160, view.innerWidth - 24));
      popup.style.width = `${width}px`;
      popup.style.maxHeight = `${Math.max(120, view.innerHeight - 24)}px`;
      popup.style.left = `${Math.max(12, Math.min(bounds.left, view.innerWidth - width - 12))}px`;
      popup.style.top = `${Math.max(12, Math.min(bounds.bottom + 4, view.innerHeight - popup.getBoundingClientRect().height - 12))}px`;
      popup.querySelector('button')?.focus({ preventScroll: true });
    });
    listen(popup, 'click', event => {
      const button = event.target.closest('[data-placeholder-key]');
      const saved = opened;
      if (!button || !popup.contains(button) || saved?.entry !== entry) return;
      const target = identity(card.owner);
      const key = button.dataset.placeholderKey;
      if (!isContextCurrent(saved.context) || !editable(entry) || control.value !== saved.value ||
          target.key !== saved.key || target.usage !== saved.usage || target.kind !== saved.kind || target.id !== saved.id ||
          card.owner.querySelector('.settings-template-override')?.checked !== saved.override ||
          !supported(card).includes(key) || !popup.contains(document.activeElement)) { clear(); return; }
      const token = `{{${key}}}`;
      clear();
      restoringEditorFocus = true;
      try {
        insertPlaceholderText(control, token, saved.start, saved.end, saved);
      } finally {
        restoringEditorFocus = false;
      }
      status.textContent = `${token} inserted into ${target.name} ${role === 'body' ? 'Body' : 'Subject'}.`;
    });
    listen(popup, 'toggle', event => { if (event.newState === 'closed' && opened?.entry === entry) clear(); });
    listen(popup, 'focusout', event => {
      if (opened?.entry === entry && event.relatedTarget !== trigger && !popup.contains(event.relatedTarget)) clear();
    });
    return entry;
  }

  function ensureCard(owner) {
    if (cards.has(owner)) return cards.get(owner);
    const card = { owner, entries: [] };
    owner.classList.add('settings-template-card');
    const legend = owner.querySelector('legend');
    if (legend) legend.classList.add('sr-only');
    const children = [...owner.children].filter(child => child !== legend);
    card.toggle = make('button', 'settings-template-toggle');
    card.toggle.type = 'button';
    card.toggle.dataset.templateToggle = 'true';
    card.toggle.dataset.settingsIgnore = 'true';
    card.toggle.setAttribute('aria-expanded', 'false');
    const chevron = make('span', 'settings-template-chevron', '›');
    chevron.setAttribute('aria-hidden', 'true');
    card.title = make('strong', 'settings-template-title');
    card.event = make('span', 'settings-template-event');
    const excerpt = make('span', 'settings-template-excerpts');
    card.subjectExcerpt = make('span', 'settings-template-subject-excerpt');
    card.bodyExcerpt = make('span', 'settings-template-body-excerpt');
    excerpt.append(card.subjectExcerpt, card.bodyExcerpt);
    card.toggle.append(chevron, card.title, card.event, excerpt);
    card.details = make('div', 'settings-template-details');
    card.details.id = `email-template-card-${++nextCardId}`;
    card.details.hidden = true;
    card.toggle.setAttribute('aria-controls', card.details.id);
    const admin = make('div', 'settings-template-admin');
    const layout = make('div', 'settings-template-layout');
    const editor = make('div', 'settings-template-fields');
    for (const child of children) (child.querySelector('[data-template-field]') ? editor : admin).append(child);
    const preview = make('aside', 'settings-template-preview');
    preview.dataset.settingsIgnore = 'true';
    const previewHeading = make('h3', '', 'Preview (example data)');
    previewHeading.id = `${card.details.id}-preview`;
    preview.setAttribute('aria-labelledby', previewHeading.id);
    card.previewSubject = make('p', 'settings-template-preview-subject');
    card.previewBody = make('p', 'settings-template-preview-body');
    card.note = make('p', 'field-help settings-template-preview-note');
    preview.append(previewHeading, make('strong', '', 'Subject'), card.previewSubject,
      make('strong', '', 'Body'), card.previewBody, card.note);
    card.error = make('p', 'settings-template-error');
    card.error.id = `${card.details.id}-error`;
    card.error.setAttribute('role', 'alert');
    layout.append(editor, preview);
    card.details.append(admin, layout, card.error);
    owner.append(card.toggle, card.details);
    for (const control of editor.querySelectorAll('[data-template-field]')) card.entries.push(createEntry(card, control));
    cards.set(owner, card);
    listen(card.toggle, 'click', () => setExpanded(card, card.details.hidden));
    return card;
  }

  function refreshCards(reset = false) {
    clear();
    if (reset) focusSequence++;
    if (reset || validationNotice && (!validationNotice.control.isConnected || !isContextCurrent(validationNotice.context))) {
      retireValidationNotice();
    }
    for (const owner of panel.querySelectorAll('fieldset.settings-template, .settings-template-row')) {
      const card = ensureCard(owner);
      // Override controls are populated separately from the static built-in fields.
      const override = owner.querySelector('.settings-template-override-control');
      if (override && !card.details.contains(override)) card.details.querySelector('.settings-template-admin').append(override);
      if (reset) {
        setExpanded(card, false);
        card.error.textContent = '';
        card.invalidControl = null;
        card.validationContext = null;
        for (const entry of card.entries) entry.scrollBeforeBlur = null;
        for (const control of owner.querySelectorAll('[aria-invalid]')) control.removeAttribute('aria-invalid');
      }
      refreshCard(card);
    }
  }

  function showError(control, message = control.validationMessage) {
    const owner = control.closest('fieldset.settings-template, .settings-template-row');
    if (!owner) return;
    onReveal();
    const card = ensureCard(owner);
    setExpanded(card, true);
    if (validationNotice?.control === card.invalidControl) retireValidationNotice();
    if (card.invalidControl !== control) card.invalidControl?.removeAttribute('aria-invalid');
    card.invalidControl = control;
    card.validationContext = captureContext();
    card.error.textContent = message;
    control.setAttribute('aria-invalid', 'true');
    const described = new Set((control.getAttribute('aria-describedby') || '').split(/\s+/).filter(Boolean));
    described.add(card.error.id);
    control.setAttribute('aria-describedby', [...described].join(' '));
    control.focus();
    onValidationError(control, message);
  }

  listen(panel, 'input', event => {
    const owner = event.target.closest('fieldset.settings-template, .settings-template-row');
    if (!owner) return;
    if (opened) clear();
    const card = ensureCard(owner);
    if (card.invalidControl === event.target) {
      if (validationNotice?.control === event.target) retireValidationNotice();
      event.target.removeAttribute('aria-invalid');
      card.error.textContent = '';
      card.invalidControl = null;
      card.validationContext = null;
    }
    refreshCard(card);
    if (!restoringEditorFocus && event.target.matches('input, textarea, select')) {
      const sequence = ++focusSequence, context = captureContext();
      // Updating the collapsed excerpt can grow the header above a focused
      // Body at native zoom. Reveal that ordinary edit after the layout settles;
      // placeholder restoration deliberately retains its exact page scroll.
      revealFocusedControl(event.target, context, sequence);
      view.requestAnimationFrame?.(() => revealFocusedControl(event.target, context, sequence));
    }
  });
  listen(panel, 'change', () => refreshCards());
  listen(document, 'focusin', event => {
    const sequence = ++focusSequence;
    if (opened && event.target !== opened.entry.trigger && !opened.entry.popup.contains(event.target)) clear();
    const control = event.target;
    if (restoringEditorFocus || !panel.contains(control) || !control.matches('input, textarea, select') ||
        !control.closest('.settings-template-card')) return;
    const context = captureContext();
    revealFocusedControl(control, context, sequence);
    // Native focus can finish scrolling after focusin. Recheck the same focus
    // owner on the next frame without moving a newer focus or insertion restore.
    view.requestAnimationFrame?.(() => revealFocusedControl(control, context, sequence));
  });
  listen(document, 'keydown', event => {
    if (event.key !== 'Escape' || !opened) return;
    const trigger = opened.entry.trigger;
    event.preventDefault(); event.stopPropagation(); clear(); trigger.focus({ preventScroll: true });
  });
  // invalid does not bubble; capture it before native validation tries to focus a hidden editor.
  listen(panel, 'invalid', event => {
    if (!event.target.closest('fieldset.settings-template, .settings-template-row')) return;
    event.preventDefault(); showError(event.target);
  }, true);
  listen(view, 'scroll', event => {
    const saved = opened;
    if (!saved || event.target instanceof view.Node && saved.entry.popup.contains(event.target)) return;
    // Native focus/reveal and single-line blur may queue scroll events before
    // opening. The anchor already uses those positions; later page, editor or
    // ancestor movement still dismisses it and returns focus to the trigger.
    if ((event.target === view || event.target === document) &&
        view.scrollX === saved.pageX && view.scrollY === saved.pageY) return;
    if (event.target === saved.entry.control && saved.entry.control.scrollTop === saved.anchorScrollTop &&
        saved.entry.control.scrollLeft === saved.anchorScrollLeft) return;
    if (event.target === saved.entry.control) saved.entry.scrollBeforeBlur = null;
    const pickerHadFocus = saved.entry.popup.contains(document.activeElement);
    clear();
    if (pickerHadFocus && editable(saved.entry)) saved.entry.trigger.focus({ preventScroll: true });
  }, true);
  listen(view, 'resize', clear);
  listen(view, 'blur', clear);

  return { clear, refresh: refreshCards, showError, retireValidationNotice,
    ownValidationNotice(control, message, clearNotice) {
      const card = cards.get(control.closest('fieldset.settings-template, .settings-template-row'));
      if (!card || card.invalidControl !== control || card.error.textContent !== message ||
          !control.isConnected || !isContextCurrent(card.validationContext)) {
        clearNotice();
        return;
      }
      retireValidationNotice();
      validationNotice = { control, context: card.validationContext, clear: clearNotice };
    },
    populate(keys) { advertised = Array.isArray(keys) ? keys : []; refreshCards(true); },
    validate(operations, system, editor) {
      for (const operation of operations) {
        if (operation.reset || !system && !operation.isCustom) continue;
        const owner = editor || [...panel.querySelectorAll('fieldset.settings-template, .settings-template-row')]
          .find(candidate => identity(candidate).key === operation.templateKey &&
            (Boolean(operation.isCustom) === (candidate.dataset.templateKind === 'custom')));
        if (!owner) continue;
        for (const role of ['subject', 'body']) {
          const control = owner.querySelector(`[data-template-field="${role}"]`);
          if (control.value.trim()) continue;
          const message = `${identity(owner).name}: ${role === 'body' ? 'Body' : 'Subject'} is required.`;
          showError(control, message);
          throw new Error(message);
        }
      }
    }
  };
}
