// These fields call applyPatronTextPlaceholders in patron/form-ui.js. Status,
// barcode and PIN labels themselves are literal. Duplicate data is resolved by
// PatronSuggestionService.Persistence; legacy aliases remain manual options.
const GENERAL = [
  { key: 'barcode_label', name: 'Library card label', description: 'The configured barcode or library card label.' },
  { key: 'pin_label', name: 'PIN label', description: 'The configured PIN label.' }
];
const DUPLICATE = [
  { key: 'duplicate_date', name: 'Previous submission date', description: 'The previous suggestion’s local submission date.' },
  { key: 'duplicate_status', name: 'Previous status', description: 'The patron-facing status of the previous suggestion.' },
  { key: 'duplicate_title', name: 'Previous title', description: 'The previous suggestion’s title.' },
  { key: 'duplicate_author', name: 'Previous author', description: 'The previous suggestion’s author or creator.' },
  { key: 'duplicate_format', name: 'Previous format', description: 'The previous suggestion’s material format.' },
  { key: 'duplicate_match_type', name: 'Duplicate match', description: 'How the previous suggestion was identified.' }
];
const FIELDS = new Set(['pageTitle', 'loginPrompt', 'loginNote', 'suggestionFormNote', 'noEmailMessage',
  'successTitle', 'successMessage', 'alreadySubmittedMessage', 'ebookMessage', 'eaudiobookMessage']);

export function patronPlaceholderGroups(key) {
  if (!FIELDS.has(key)) return [];
  return [{ name: 'General', items: GENERAL }, ...(key === 'alreadySubmittedMessage'
    ? [{ name: 'Previous suggestion', items: DUPLICATE }] : [])];
}

export function createPatronTextPlaceholders({ root, panel, listen, captureContext, isContextCurrent, isActive }) {
  const document = root.ownerDocument;
  let opened = null;
  const entries = [];
  const status = document.createElement('span');
  status.className = 'sr-only';
  status.setAttribute('role', 'status');
  panel.append(status);

  function editable(entry) {
    const control = entry.control;
    if (!isActive() || !control.isConnected || !panel.contains(control) || control.readOnly ||
        control.disabled || control.matches(':disabled') || control.closest('[hidden], [inert]')) return false;
    for (let ancestor = control; ancestor; ancestor = ancestor.parentElement) {
      const style = window.getComputedStyle(ancestor);
      if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
    }
    return true;
  }

  function clear() {
    const prior = opened;
    opened = null;
    status.textContent = '';
    if (!prior) return;
    prior.entry.trigger.setAttribute('aria-expanded', 'false');
    if (prior.entry.popup.hidePopover && prior.entry.popup.isConnected) prior.entry.popup.hidePopover();
    else prior.entry.popup.hidden = true;
  }

  function refresh() {
    clear();
    for (const entry of entries) entry.trigger.disabled = !editable(entry);
  }

  for (const field of panel.querySelectorAll('[data-setting-section="patron"][data-setting-key]')) {
    const groups = patronPlaceholderGroups(field.dataset.settingKey);
    if (!groups.length) continue;
    const control = field.querySelector('input, textarea');
    const heading = field.querySelector('.patron-text-label');
    const label = heading.querySelector('label').textContent;
    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'patron-placeholder-trigger secondary-button';
    trigger.textContent = 'Insert placeholder ▾';
    trigger.setAttribute('aria-label', `Insert placeholder into ${label}`);
    trigger.setAttribute('aria-expanded', 'false');
    const popup = document.createElement('div');
    popup.className = 'patron-placeholder-picker';
    popup.id = `${control.id}-placeholders`;
    popup.setAttribute('popover', 'auto');
    popup.setAttribute('role', 'group');
    popup.setAttribute('aria-label', `Placeholders for ${label}`);
    trigger.setAttribute('aria-controls', popup.id);
    if (!popup.showPopover) popup.hidden = true;
    const entry = { control, trigger, popup };
    entries.push(entry);
    for (const group of groups) {
      const title = document.createElement('strong');
      title.textContent = group.name;
      popup.append(title);
      for (const item of group.items) {
        const button = document.createElement('button');
        button.type = 'button';
        button.dataset.patronPlaceholder = item.key;
        const name = document.createElement('span');
        name.textContent = item.name;
        const token = document.createElement('code');
        token.textContent = `{{${item.key}}}`;
        const description = document.createElement('small');
        description.id = `${control.id}-${item.key}-description`;
        description.textContent = item.description;
        button.setAttribute('aria-describedby', description.id);
        button.append(name, token, description);
        popup.append(button);
      }
    }
    heading.append(trigger);
    field.append(popup);
    listen(trigger, 'click', () => {
      if (opened?.entry === entry) { clear(); return; }
      clear();
      if (!editable(entry)) return;
      opened = { entry, context: captureContext(), start: control.selectionStart ?? control.value.length,
        end: control.selectionEnd ?? control.value.length, value: control.value };
      trigger.setAttribute('aria-expanded', 'true');
      if (popup.showPopover) popup.showPopover(); else popup.hidden = false;
      const rect = trigger.getBoundingClientRect();
      const width = Math.min(384, window.innerWidth - 24);
      const height = Math.min(320, window.innerHeight - 24);
      popup.style.width = `${width}px`;
      popup.style.maxHeight = `${height}px`;
      popup.style.left = `${Math.max(12, Math.min(rect.left, window.innerWidth - width - 12))}px`;
      popup.style.top = `${Math.max(12, Math.min(rect.bottom + 4, window.innerHeight - popup.offsetHeight - 12))}px`;
      popup.querySelector('button')?.focus({ preventScroll: true });
    });
    listen(popup, 'click', event => {
      const button = event.target.closest('[data-patron-placeholder]');
      const saved = opened;
      if (!button || !popup.contains(button) || saved?.entry !== entry) return;
      if (!isContextCurrent(saved.context) || !editable(entry) || control.value !== saved.value ||
          !popup.contains(document.activeElement)) { clear(); return; }
      const token = `{{${button.dataset.patronPlaceholder}}}`;
      const { scrollTop, scrollLeft } = control;
      control.setRangeText(token, saved.start, saved.end, 'end');
      clear();
      control.focus({ preventScroll: true });
      control.dispatchEvent(new window.Event('input', { bubbles: true }));
      control.scrollTop = scrollTop; control.scrollLeft = scrollLeft;
      status.textContent = `${token} inserted into ${label}.`;
    });
    listen(popup, 'toggle', event => {
      if (event.newState === 'closed' && opened?.entry === entry) clear();
    });
  }
  listen(document, 'keydown', event => {
    if (event.key !== 'Escape' || !opened) return;
    const trigger = opened.entry.trigger;
    event.preventDefault(); event.stopPropagation(); clear(); trigger.focus({ preventScroll: true });
  });
  listen(document, 'focusin', event => {
    if (opened && event.target !== opened.entry.trigger && !opened.entry.popup.contains(event.target)) clear();
  });
  listen(panel, 'change', refresh);
  listen(window, 'resize', clear);
  listen(window, 'blur', clear);
  return { clear, refresh };
}
