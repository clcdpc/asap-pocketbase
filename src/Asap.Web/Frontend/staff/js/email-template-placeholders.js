const TIMEOUT_REJECTION_TOKENS = Object.freeze(['title']);

const NO_VERIFIED_PATH = 'No ASP.NET sending path currently verifies placeholders for this template.';

// Keep this catalog aligned with PatronEmailTemplateRenderer.cs and the explicit
// replacement in WorkflowProcessingService.AddTimeoutEmailAsync(). Unknown keys
// intentionally have no inferred capability.
export const PLACEHOLDER_CATALOG = Object.freeze([
  Object.freeze({
    key: 'name',
    token: '{{name}}',
    description: "Patron's full name. The existing renderer uses “Library Patron” when a name is unavailable.",
    example: 'Alex Reader'
  }),
  Object.freeze({
    key: 'title',
    token: '{{title}}',
    description: 'Requested title.',
    example: 'Example title'
  }),
  Object.freeze({
    key: 'author',
    token: '{{author}}',
    description: "Value from the request's author/creator field. It may be blank.",
    example: 'Example creator'
  }),
  Object.freeze({
    key: 'format',
    token: '{{format}}',
    description: 'Requested material-format label.',
    example: 'Video Game'
  }),
  Object.freeze({
    key: 'barcode',
    token: '{{barcode}}',
    description: "Patron's library-card barcode. Use only when the message needs it.",
    example: '00000000000000'
  }),
  Object.freeze({
    key: 'firstName',
    token: '{{firstName}}',
    description: "Patron's first name, when available.",
    example: 'Alex'
  }),
  Object.freeze({
    key: 'lastName',
    token: '{{lastName}}',
    description: "Patron's last name, when available.",
    example: 'Reader'
  })
]);

const PATRON_RENDERER_TOKENS = Object.freeze(PLACEHOLDER_CATALOG.map(item => item.key));

const VERIFIED_CAPABILITIES = Object.freeze({
  suggestion_submitted: Object.freeze({
    status: 'supported',
    context: 'patron-renderer',
    tokenKeys: PATRON_RENDERER_TOKENS
  }),
  purchase_approved: Object.freeze({
    status: 'unsupported',
    context: 'purchase-approved',
    tokenKeys: Object.freeze([]),
    reason: NO_VERIFIED_PATH
  }),
  already_owned: Object.freeze({
    status: 'unsupported',
    context: 'already-owned',
    tokenKeys: Object.freeze([]),
    reason: NO_VERIFIED_PATH
  }),
  hold_placed: Object.freeze({
    status: 'unsupported',
    context: 'hold-placed',
    tokenKeys: Object.freeze([]),
    reason: NO_VERIFIED_PATH
  }),
  rejected: Object.freeze({
    status: 'unsupported',
    context: 'rejected',
    tokenKeys: Object.freeze([]),
    reason: NO_VERIFIED_PATH
  })
});

const TIMEOUT_REJECTION_CAPABILITY = Object.freeze({
  status: 'supported',
  context: 'timeout-rejection',
  tokenKeys: TIMEOUT_REJECTION_TOKENS
});

const UNKNOWN_CAPABILITY = Object.freeze({
  status: 'unknown',
  context: 'unverified',
  tokenKeys: Object.freeze([]),
  reason: 'Placeholder support for this template has not been verified.'
});

const CATALOG_BY_KEY = new Map(PLACEHOLDER_CATALOG.map(item => [item.key, item]));

export function getTemplatePlaceholderCapability(templateKey) {
  const key = String(templateKey || '').trim();
  if (/^rejection:/i.test(key)) return TIMEOUT_REJECTION_CAPABILITY;
  return VERIFIED_CAPABILITIES[key] || UNKNOWN_CAPABILITY;
}

export function getTemplatePlaceholderTokens(templateKey) {
  const capability = getTemplatePlaceholderCapability(templateKey);
  return capability.tokenKeys.map(key => CATALOG_BY_KEY.get(key)).filter(Boolean);
}

function normalize(value) {
  return value === null || value === undefined ? '' : String(value).trim();
}

function isPlaceholderTarget(element) {
  return Boolean(element?.matches?.('[data-placeholder-target="true"]')) &&
    (element.tagName === 'INPUT' || element.tagName === 'TEXTAREA') &&
    typeof element.setRangeText === 'function';
}

function hasHiddenAncestor(element) {
  let current = element;
  while (current) {
    if (current.hidden || current.getAttribute?.('aria-hidden') === 'true') return true;
    current = current.parentElement;
  }
  return false;
}

function hasDisabledFieldset(element, boundary) {
  let current = element.parentElement;
  while (current && current !== boundary) {
    if (current.tagName === 'FIELDSET' && current.disabled) return true;
    current = current.parentElement;
  }
  return false;
}

function createTokenButton(document, item, disabled) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'email-template-placeholder-token';
  button.dataset.placeholderToken = item.key;
  button.disabled = Boolean(disabled);
  button.textContent = item.token;
  button.setAttribute('aria-label', `Insert ${item.token} (${item.description})`);
  return button;
}

function buildReference(document, container) {
  container.replaceChildren();
  const description = document.createElement('p');
  description.textContent = 'Examples are synthetic guidance only; this panel never looks up patron data.';
  container.append(description);

  const list = document.createElement('dl');
  list.className = 'email-template-placeholder-reference-list';
  for (const item of PLACEHOLDER_CATALOG) {
    const term = document.createElement('dt');
    term.textContent = item.token;
    const meaning = document.createElement('dd');
    meaning.textContent = `${item.description} Example: ${item.example}`;
    list.append(term, meaning);
  }
  container.append(list);
}

export function createEmailTemplatePlaceholderHelper({
  panel,
  getContextKey = () => ''
}) {
  const document = panel?.ownerDocument || globalThis.document;
  const helper = panel?.querySelector?.('#email-template-placeholder-helper');
  if (!panel || !helper || !document) {
    return { invalidate() {}, refresh() {}, destroy() {} };
  }

  const dom = {
    target: helper.querySelector('#email-template-placeholder-target'),
    status: helper.querySelector('#email-template-placeholder-status'),
    group: helper.querySelector('#email-template-placeholder-buttons'),
    reference: helper.querySelector('#email-template-placeholder-reference')
  };
  if (!dom.target || !dom.status || !dom.group || !dom.reference) {
    return { invalidate() {}, refresh() {}, destroy() {} };
  }

  let selected = null;
  let statusOverride = '';
  let pendingHelperFocus = false;

  buildReference(document, dom.reference);

  function contextKey() {
    try {
      return String(getContextKey() ?? '');
    } catch {
      return '';
    }
  }

  function selectionFor(element) {
    const length = String(element.value || '').length;
    const start = Number.isInteger(element.selectionStart)
      ? Math.min(Math.max(element.selectionStart, 0), length)
      : length;
    const end = Number.isInteger(element.selectionEnd)
      ? Math.min(Math.max(element.selectionEnd, start), length)
      : start;
    return { start, end };
  }

  function templateContainer(element) {
    return element.closest?.('[data-template-key], .settings-template-row') || null;
  }

  function templateKeyFor(element) {
    const container = templateContainer(element);
    if (!container) return '';
    if (container.classList.contains('settings-template-row')) {
      return normalize(container.querySelector('.template-key')?.value);
    }
    if (container.dataset.templateKey) return normalize(container.dataset.templateKey);
    return normalize(container.querySelector('.template-key')?.value);
  }

  function templateLabelFor(element) {
    const container = templateContainer(element);
    if (!container) return templateKeyFor(element) || 'Unknown template';
    if (container.dataset.templateLabel) return normalize(container.dataset.templateLabel);
    const displayName = normalize(container.querySelector('.template-display-name')?.value);
    return displayName || templateKeyFor(element) || 'Unnamed template';
  }

  function fieldLabelFor(element) {
    return element.dataset.templateField === 'body' ? 'Body' : 'Subject';
  }

  function isUsable(element) {
    return isPlaceholderTarget(element) && panel.contains(element) &&
      !hasHiddenAncestor(panel) && !hasHiddenAncestor(element) &&
      !element.disabled && !element.readOnly && !hasDisabledFieldset(element, panel);
  }

  function selectedIsUsable() {
    if (!selected || selected.contextKey !== contextKey()) return false;
    return isUsable(selected.element);
  }

  function invalidTargetMessage() {
    if (!selected) return 'Select an editable Subject or Body field first.';
    if (selected.contextKey !== contextKey()) return 'Select an editable Subject or Body field in the current settings context first.';
    if (selected.element?.disabled || selected.element?.readOnly || hasDisabledFieldset(selected.element, panel)) {
      return 'Enable the template override before inserting a placeholder.';
    }
    return 'Select an editable Subject or Body field first.';
  }

  function targetDescription() {
    if (!selectedIsUsable()) return 'No insertion target selected.';
    return `Inserting into: ${templateLabelFor(selected.element)} · ${fieldLabelFor(selected.element)}`;
  }

  function render(message = '') {
    if (selected && !selectedIsUsable()) {
      if (!message) message = invalidTargetMessage();
      selected = null;
    }

    const usable = selectedIsUsable();
    const capability = usable
      ? getTemplatePlaceholderCapability(templateKeyFor(selected.element))
      : null;
    const available = usable ? getTemplatePlaceholderTokens(templateKeyFor(selected.element)) : [];
    dom.target.textContent = targetDescription();
    dom.group.replaceChildren();

    if (!usable) {
      for (const item of PLACEHOLDER_CATALOG) dom.group.append(createTokenButton(document, item, true));
      dom.status.textContent = message || 'Select an editable Subject or Body field first.';
      return;
    }

    for (const item of available) dom.group.append(createTokenButton(document, item, false));
    if (capability.status === 'unknown') {
      dom.status.textContent = `Placeholder support for “${templateKeyFor(selected.element) || 'this template'}” has not been verified. You can continue editing manually.`;
    } else if (capability.status === 'unsupported') {
      dom.status.textContent = `${capability.reason} You can continue editing manually.`;
    } else {
      dom.status.textContent = available.length === 1
        ? 'One verified placeholder is available for this template.'
        : `${available.length} verified placeholders are available for this template.`;
    }
  }

  function remember(element) {
    if (!isUsable(element)) {
      selected = null;
      render('Select an editable Subject or Body field first.');
      return;
    }
    const selection = selectionFor(element);
    selected = {
      element,
      start: selection.start,
      end: selection.end,
      contextKey: contextKey()
    };
    statusOverride = '';
    render();
  }

  function rememberSelection(element) {
    if (selected?.element !== element) return;
    const selection = selectionFor(element);
    selected.start = selection.start;
    selected.end = selection.end;
  }

  function clear(message = 'Select an editable Subject or Body field first.') {
    selected = null;
    statusOverride = message;
    render(statusOverride);
  }

  function targetFromEvent(event) {
    return event.target;
  }

  function onFocusIn(event) {
    const element = targetFromEvent(event);
    if (isPlaceholderTarget(element)) {
      remember(element);
      return;
    }
    if (helper.contains(element)) {
      pendingHelperFocus = false;
      return;
    }
    if (panel.contains(element)) clear();
  }

  function onFocusOut(event) {
    const next = event.relatedTarget || document.activeElement;
    if (next && panel.contains(next)) return;
    if (pendingHelperFocus) return;
    if (!next || !panel.contains(next)) clear();
  }

  function onSelection(event) {
    const element = targetFromEvent(event);
    if (!isPlaceholderTarget(element)) return;
    if (selected?.element === element) {
      rememberSelection(element);
      return;
    }
    if (document.activeElement !== element) return;
    remember(element);
  }

  function onInput(event) {
    const element = targetFromEvent(event);
    if (selected?.element === element) rememberSelection(element);
    else if (panel.contains(element) && !helper.contains(element)) clear();
  }

  function onChange() {
    if (selected && !selectedIsUsable()) render();
  }

  function onPointerDown(event) {
    const button = event.target.closest?.('[data-placeholder-token]');
    if (button) {
      pendingHelperFocus = true;
      if (selected?.element) rememberSelection(selected.element);
    }
  }

  function onClick(event) {
    const button = event.target.closest?.('[data-placeholder-token]');
    if (!button) return;
    event.preventDefault();
    pendingHelperFocus = false;
    insert(button.dataset.placeholderToken);
  }

  function insert(key) {
    const item = CATALOG_BY_KEY.get(key);
    if (!item) return false;
    if (!selectedIsUsable()) {
      clear(invalidTargetMessage());
      return false;
    }
    const templateKey = templateKeyFor(selected.element);
    const capability = getTemplatePlaceholderCapability(templateKey);
    if (!capability.tokenKeys.includes(item.key)) {
      render();
      return false;
    }

    const element = selected.element;
    const length = String(element.value || '').length;
    const start = Math.min(selected.start, length);
    const end = Math.min(Math.max(selected.end, start), length);
    const scrollTop = element.scrollTop;
    const scrollLeft = element.scrollLeft;
    try {
      element.focus({ preventScroll: true });
      selected.start = start;
      selected.end = end;
      element.setSelectionRange(start, end);
      element.setRangeText(item.token, start, end, 'end');
      const EventConstructor = document.defaultView?.Event || globalThis.Event;
      element.dispatchEvent(new EventConstructor('input', { bubbles: true }));
      element.focus({ preventScroll: true });
      element.scrollTop = scrollTop;
      element.scrollLeft = scrollLeft;
    } catch {
      render('This field could not accept a placeholder insertion.');
      return false;
    }

    const selection = selectionFor(element);
    selected.start = selection.start;
    selected.end = selection.end;
    statusOverride = `Inserted ${item.token}.`;
    dom.status.textContent = statusOverride;
    return true;
  }

  const observer = document.defaultView?.MutationObserver
    ? new document.defaultView.MutationObserver(() => {
      if (selected && (!selected.element.isConnected || !panel.contains(selected.element))) clear();
    })
    : null;
  observer?.observe(panel, { childList: true, subtree: true });
  panel.addEventListener('focusin', onFocusIn, true);
  panel.addEventListener('focusout', onFocusOut, true);
  panel.addEventListener('select', onSelection, true);
  panel.addEventListener('keyup', onSelection, true);
  panel.addEventListener('mouseup', onSelection, true);
  panel.addEventListener('click', onClick);
  panel.addEventListener('pointerdown', onPointerDown, true);
  panel.addEventListener('input', onInput);
  panel.addEventListener('change', onChange);
  render();

  return {
    invalidate(message) {
      clear(message || 'Select an editable Subject or Body field first.');
    },
    refresh() {
      render();
    },
    destroy() {
      observer?.disconnect();
      panel.removeEventListener('focusin', onFocusIn, true);
      panel.removeEventListener('focusout', onFocusOut, true);
      panel.removeEventListener('select', onSelection, true);
      panel.removeEventListener('keyup', onSelection, true);
      panel.removeEventListener('mouseup', onSelection, true);
      panel.removeEventListener('click', onClick);
      panel.removeEventListener('pointerdown', onPointerDown, true);
      panel.removeEventListener('input', onInput);
      panel.removeEventListener('change', onChange);
    }
  };
}
