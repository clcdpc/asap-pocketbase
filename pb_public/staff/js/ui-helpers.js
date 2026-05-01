export function escapeAttr(value) {
  return String(value || '')
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

export function escapeHtml(value) {
  return escapeAttr(value);
}

export function install(env) {
  env.escapeAttr = escapeAttr;
  env.escapeHtml = escapeHtml;
}
