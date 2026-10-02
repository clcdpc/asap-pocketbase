export const STATUS_LABELS = {
  open: 'Open',
  suggestion: 'Suggestion',
  outstanding_purchase: 'Outstanding purchase',
  pending_hold: 'Pending hold',
  hold_placed: 'Hold placed',
  closed: 'Closed'
};

export function timeoutLabel(enabled, days) {
  return enabled && Number.isInteger(days) && days > 0 ? `${days} days` : 'Off';
}

export function element(tag, attributes = {}, children = []) {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attributes)) {
    if (value === null || value === undefined) continue;
    if (name === 'className') node.className = value;
    else if (name === 'text') node.textContent = value;
    else if (name === 'checked') node.checked = Boolean(value);
    else if (name === 'disabled') node.disabled = Boolean(value);
    else if (name === 'value') node.value = value;
    else if (name.startsWith('on') && typeof value === 'function') node.addEventListener(name.slice(2), value);
    else node.setAttribute(name, String(value));
  }
  for (const child of Array.isArray(children) ? children : [children]) {
    if (child === null || child === undefined) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

export function icon(name) {
  return element('i', { className: `fa fa-${name}`, 'aria-hidden': 'true' });
}

export function commandButton(label, iconName, handler, className = 'secondary-button', disabled = false) {
  return element('button', { type: 'button', className, onclick: handler, disabled }, [icon(iconName), label]);
}

export function text(value, fallback = 'Not recorded') {
  const normalized = value === null || value === undefined ? '' : String(value).trim();
  return normalized || fallback;
}

export function dateTime(value) {
  if (!value) return 'Not recorded';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? String(value) : parsed.toLocaleString();
}

export function statusLabel(value) {
  return STATUS_LABELS[value] || text(value, 'Unknown');
}

export function closeReasonLabel(value) {
  const labels = {
    rejected: 'Rejected',
    manual: 'Closed by staff',
    duplicate_hold: 'Duplicate patron hold',
    purchased_no_hold: 'Purchased, no hold',
    hold_cancelled: 'Hold cancelled',
    silent: 'Closed silently',
    'Silently Closed': 'Closed silently'
  };
  return Object.hasOwn(labels, value) ? labels[value]
    : value ? String(value).replaceAll('_', ' ') : 'No reason recorded';
}

export function addDetail(list, label, value) {
  const wrapper = element('div');
  wrapper.append(element('dt', { text: label }), element('dd', { text: text(value) }));
  list.append(wrapper);
}

export function labeledInput(label, input, className = '') {
  return element('label', { className }, [element('span', { text: label }), input]);
}
