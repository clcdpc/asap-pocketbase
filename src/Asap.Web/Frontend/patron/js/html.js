export { escapeHtml, sanitizeHtml } from '../../shared/html.js';

// Staff-authored patron messages are plain text. Only explicit HTTP(S) URLs
// become links; markup and placeholder values are always DOM text.
export function renderPatronText(container, value) {
  const text = String(value ?? '');
  const document = container.ownerDocument;
  const nodes = [];
  let offset = 0;
  for (const match of text.matchAll(/https?:\/\/[^\s<>"']+/gi)) {
    let url = match[0].replace(/[.,!?;:]+$/, '');
    for (const [open, close] of [['(', ')'], ['[', ']'], ['{', '}']]) {
      while (url.endsWith(close) && url.split(close).length > url.split(open).length) url = url.slice(0, -1);
    }
    nodes.push(document.createTextNode(text.slice(offset, match.index)));
    const link = document.createElement('a');
    link.href = url;
    link.textContent = url;
    link.target = '_blank';
    link.rel = 'noopener noreferrer';
    nodes.push(link);
    offset = match.index + url.length;
  }
  nodes.push(document.createTextNode(text.slice(offset)));
  container.replaceChildren(...nodes);
  container.classList.add('patron-message');
}

export function patronTextPlaceholders(uiText) {
  const barcodeLabel = String(uiText.barcodeLabel || 'Library Card').trim() || 'Library Card';
  const pinLabel = String(uiText.pinLabel || 'PIN').trim() || 'PIN';

  return {
    barcode_label: barcodeLabel,
    pin_label: pinLabel,
    barcodeLabel,
    pinLabel
  };
}

export function applyPatronTextPlaceholders(value, uiText) {
  const replacements = patronTextPlaceholders(uiText || {});
  return String(value || '').replace(/\{\{\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*\}\}/g, (match, key) => {
    return Object.prototype.hasOwnProperty.call(replacements, key)
      ? replacements[key]
      : match;
  });
}
