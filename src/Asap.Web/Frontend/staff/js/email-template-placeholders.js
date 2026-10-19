// Patron-renderer contexts use these seven case-sensitive tokens. Named
// rejection templates also serve timeout mail, whose shared capability is title.
// Hold placement currently uses fixed content, not the configured hold template.
export const PLACEHOLDERS = [
  { key: 'name', description: 'Patron’s full name; Library Patron when unavailable.', example: 'Alex Reader' },
  { key: 'title', description: 'Requested title.', example: 'Example title' },
  { key: 'author', description: 'Request author or creator; may be blank.', example: 'Example creator' },
  { key: 'format', description: 'Requested material-format label.', example: 'Video Game' },
  { key: 'barcode', description: 'Library-card barcode; use only when needed.', example: '00000000000000' },
  { key: 'firstName', description: 'Patron’s first name, when available.', example: 'Alex' },
  { key: 'lastName', description: 'Patron’s last name, when available.', example: 'Reader' }
];
const PATRON_CONTEXTS = new Set(['suggestion_submitted', 'purchase_approved', 'already_owned', 'rejected']);

export function supportedPlaceholderKeys(templateKey, usage) {
  if (usage === 'builtin' && PATRON_CONTEXTS.has(templateKey)) return PLACEHOLDERS.map(item => item.key);
  if (usage === 'additional' && /^rejection:/i.test(templateKey || '')) return ['title'];
  return [];
}

export function createEmailTemplatePlaceholders({ root, panel, listen, captureContext, isContextCurrent, isActive }) {
  const document = root.ownerDocument;
  const help = panel.querySelector('#template-placeholder-help');
  const list = panel.querySelector('#template-placeholder-list');
  const reference = panel.querySelector('#template-placeholder-reference');
  const targetLabel = panel.querySelector('#template-placeholder-target');
  const guidance = panel.querySelector('#template-placeholder-guidance');
  const status = panel.querySelector('#template-placeholder-status');
  let selection = null;
  let advertised = new Set();
  let renderedKeys = null;

  function identity(control) {
    const row = control.closest('.settings-template-row');
    const fieldset = control.closest('[data-template-key]');
    const key = row?.querySelector('.template-key')?.value.trim() || fieldset?.dataset.templateKey || '';
    const name = row?.querySelector('.template-display-name')?.value.trim() ||
      fieldset?.querySelector('legend')?.firstChild?.textContent.trim() || key;
    // An additional/custom row may use a built-in-looking key without being
    // consumed by that built-in sender. Usage comes from the owning editor.
    const usage = row ? 'additional' : fieldset?.matches('fieldset.settings-template') ? 'builtin' : '';
    return { key, name, usage, kind: row?.dataset.templateKind || '', role: control.dataset.templateField };
  }

  function marked(control) {
    return control?.matches?.('input[data-template-field], textarea[data-template-field]') && panel.contains(control);
  }

  function editable(control) {
    if (!marked(control) || !control.isConnected || control.disabled || control.readOnly ||
        control.matches(':disabled') || control.closest('fieldset[disabled], [inert], [hidden]') || !isActive()) return false;
    for (let ancestor = control; ancestor; ancestor = ancestor.parentElement) {
      const style = window.getComputedStyle(ancestor);
      if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
    }
    const owner = control.closest('.settings-template-row, [data-template-key]');
    const override = owner?.querySelector('.settings-template-override');
    return !override || override.checked;
  }

  function validTarget() {
    if (!selection || !isContextCurrent(selection.context) || !editable(selection.control)) return false;
    const target = identity(selection.control);
    return target.key === selection.key && target.usage === selection.usage && target.kind === selection.kind;
  }

  function current() {
    return validTarget() && (document.activeElement === selection.control || help.contains(document.activeElement));
  }

  function render() {
    const usable = current();
    const target = usable ? identity(selection.control) : null;
    const supported = target ? supportedPlaceholderKeys(target.key, target.usage) : [];
    targetLabel.textContent = target
      ? `Inserting into: ${target.name || target.key} · ${target.role === 'body' ? 'Body' : 'Subject'}`
      : 'No editable Subject or Body selected.';
    guidance.textContent = target
      ? supported.length
        ? target.usage === 'additional'
          ? 'Only {{title}} is supported by every sending path for named rejection templates, including timeouts.'
          : 'Choose a placeholder to insert it at the remembered cursor or selection.'
        : 'Placeholder support is not verified for this template. You can still edit its text manually.'
      : 'Select an editable Subject or Body field first. Enable its existing Override control to edit inherited content.';
    const items = PLACEHOLDERS.filter(item => advertised.has(item.key) && (!target || supported.includes(item.key)));
    const keys = items.map(item => item.key).join('/');
    if (keys !== renderedKeys) {
      renderedKeys = keys;
      list.replaceChildren();
      for (const item of items) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'secondary-button template-placeholder-button';
        button.dataset.placeholderKey = item.key;
        button.textContent = `{{${item.key}}}`;
        button.setAttribute('aria-label', `Insert {{${item.key}}}: ${item.description}`);
        button.setAttribute('aria-describedby', `template-placeholder-description-${item.key}`);
        list.append(button);
      }
    }
    for (const button of list.querySelectorAll('button')) {
      button.setAttribute('aria-disabled', String(!usable));
    }
  }

  function clear() {
    selection = null;
    status.textContent = '';
    render();
  }

  function remember(control) {
    // Focus or a fresh user editing gesture owns the target. Late selection
    // events may update neither a different target nor a cleared population.
    if (document.activeElement !== control || !editable(control)) return;
    const { key, usage, kind } = identity(control);
    const changedTarget = selection?.control !== control || selection.key !== key || selection.usage !== usage ||
      selection.kind !== kind || !isContextCurrent(selection.context);
    selection = {
      control, key, usage, kind, context: captureContext(),
      start: control.selectionStart ?? control.value.length,
      end: control.selectionEnd ?? control.value.length
    };
    if (changedTarget) render();
  }

  listen(root.ownerDocument, 'focusin', event => {
    if (marked(event.target)) {
      if (editable(event.target)) remember(event.target);
      else clear();
    } else if (!help.contains(event.target)) clear();
  });
  for (const name of ['select', 'keyup', 'mouseup', 'input']) {
    listen(panel, name, event => {
      if (selection?.control === event.target && document.activeElement === event.target && marked(event.target)) remember(event.target);
    });
  }
  for (const name of ['pointerdown', 'keydown']) {
    listen(panel, name, event => {
      if (event.isTrusted && marked(event.target) && document.activeElement === event.target) remember(event.target);
    });
  }
  listen(panel, 'focusout', event => {
    if (selection && help.contains(event.target)) {
      if (!help.contains(event.relatedTarget) && event.relatedTarget !== selection.control) clear();
      return;
    }
    if (selection?.control !== event.target) return;
    if (help.contains(event.relatedTarget) && validTarget()) {
      selection.start = event.target.selectionStart ?? event.target.value.length;
      selection.end = event.target.selectionEnd ?? event.target.value.length;
    } else clear();
  });
  listen(document.defaultView, 'blur', clear);
  // Native change can fire between blur and the helper receiving focus. At
  // that boundary validate edit ownership; activation validates focus as well.
  listen(panel, 'change', () => { if (selection && !validTarget()) clear(); });
  listen(list, 'click', event => {
    const button = event.target.closest('[data-placeholder-key]');
    if (!button || !list.contains(button)) return;
    const key = button.dataset.placeholderKey;
    if (!current() || !advertised.has(key) || !supportedPlaceholderKeys(selection.key, selection.usage).includes(key)) {
      clear();
      status.textContent = 'Select an editable Subject or Body field first.';
      return;
    }
    const { control, start, end } = selection;
    const token = `{{${key}}}`;
    const { scrollTop, scrollLeft } = control;
    control.setRangeText(token, start, end, 'end');
    control.focus({ preventScroll: true });
    remember(control);
    control.dispatchEvent(new window.Event('input', { bubbles: true }));
    control.scrollTop = scrollTop;
    control.scrollLeft = scrollLeft;
    status.textContent = `${token} inserted.`;
  });

  return {
    clear,
    populate(keys) {
      advertised = new Set(Array.isArray(keys) ? keys : []);
      reference.replaceChildren();
      for (const item of PLACEHOLDERS.filter(item => advertised.has(item.key))) {
        const term = document.createElement('dt');
        term.textContent = `{{${item.key}}}`;
        const definition = document.createElement('dd');
        definition.id = `template-placeholder-description-${item.key}`;
        definition.textContent = `${item.description} Example: ${item.example}`;
        reference.append(term, definition);
      }
      clear();
    }
  };
}
