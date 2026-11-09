export function byId(id) {
  return document.getElementById(id);
}

export function setText(id, value) {
  const el = typeof id === 'string' ? byId(id) : id;
  if (el) el.textContent = value == null ? '' : String(value);
}

export function setVisible(id, visible) {
  const el = typeof id === 'string' ? byId(id) : id;
  if (el) el.classList.toggle('hidden', !visible);
}

export function setDisabled(id, disabled) {
  const el = typeof id === 'string' ? byId(id) : id;
  if (el) el.disabled = Boolean(disabled);
}

export function replaceChildren(idOrElement, ...children) {
  const el = typeof idOrElement === 'string' ? byId(idOrElement) : idOrElement;
  if (el) el.replaceChildren(...children);
}

export function setLabel(labelEl, label, required) {
  if (!labelEl) return;
  labelEl.textContent = '';
  labelEl.appendChild(document.createTextNode(String(label || '') + (required ? ' ' : '')));
  if (required) {
    const marker = document.createElement('span');
    marker.setAttribute('aria-hidden', 'true');
    marker.textContent = '*';
    labelEl.appendChild(marker);
  }
}

export function optionNode(value, label) {
  const option = document.createElement('option');
  option.value = value == null ? '' : String(value);
  option.textContent = label == null ? '' : String(label);
  return option;
}
