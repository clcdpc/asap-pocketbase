export function setFieldValue(id, value) {
  const el = document.getElementById(id);
  if (el) el.value = value;
}

export function setFieldChecked(id, checked) {
  const el = document.getElementById(id);
  if (el) el.checked = checked;
}

export function getFieldValue(id, fallback = '') {
  const el = document.getElementById(id);
  return el ? el.value : fallback;
}

export function getFieldChecked(id, fallback = false) {
  const el = document.getElementById(id);
  return el ? el.checked : fallback;
}

export function setVisible(id, visible) {
  const el = document.getElementById(id);
  if (el) el.classList.toggle('hidden', !visible);
}

export function setText(id, value) {
  const el = document.getElementById(id);
  if (el) el.textContent = value || '';
}

export function setDisabled(id, disabled) {
  const el = document.getElementById(id);
  if (el) el.disabled = !!disabled;
}

export function setInlineStatus(id, message, type) {
  const el = document.getElementById(id);
  if (!el) return;
  el.textContent = message || '';
  el.className = type ? `text-${type} font-weight-bold` : '';
}

export function setInlineResult(el, message, className) {
  if (!el) return;
  el.textContent = message;
  el.className = className;
}

export function formDataObject(form) {
  return Object.fromEntries(new FormData(form).entries());
}
