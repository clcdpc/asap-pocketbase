export { escapeHtml, sanitizeHtml } from '../../shared/html.js';

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
