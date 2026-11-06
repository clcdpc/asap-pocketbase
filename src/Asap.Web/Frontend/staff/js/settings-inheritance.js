// Presentation only. Each editor owns presence, normalization and save intent.
export function inheritanceStatus({ owned, value, systemValue, pending = false, onReset, label, showComparison = true, matchesSystem, comparisonOpen = false }) {
  const document = globalThis.document;
  const container = document.createElement('div');
  container.className = 'settings-inheritance';
  const status = document.createElement('span');
  status.className = 'settings-inheritance-status';
  const matches = matchesSystem ?? JSON.stringify(value) === JSON.stringify(systemValue);
  status.textContent = owned ? `Library setting${matches ? ' · Matches system' : ''}` : 'Using system value';
  if (pending) status.textContent += ' · Unsaved';
  container.append(status);
  if (owned && !matches && showComparison) {
    const text = typeof systemValue === 'boolean' ? (systemValue ? 'Enabled' : 'Disabled')
      : systemValue === null || systemValue === undefined || systemValue === '' ? '(blank)'
      : typeof systemValue === 'object' ? JSON.stringify(systemValue) : String(systemValue);
    const comparison = document.createElement(text.length > 100 || text.includes('\n') ? 'details' : 'span');
    comparison.className = 'settings-system-comparison';
    if (comparison.localName === 'details') {
      comparison.open = comparisonOpen;
      const summary = document.createElement('summary');
      summary.textContent = 'System value';
      const content = document.createElement('p');
      content.textContent = text;
      comparison.append(summary, content);
    } else comparison.textContent = `System value: ${text}`;
    container.append(comparison);
  }
  if (owned) {
    const reset = document.createElement('button');
    reset.type = 'button';
    reset.className = 'settings-use-system';
    reset.textContent = 'Use system value';
    reset.setAttribute('aria-label', `Use system value for ${label}`);
    reset.addEventListener('click', event => {
      if (container.isConnected && !reset.matches(':disabled') && !container.closest('[inert]')) onReset(event);
    });
    container.append(reset);
  }
  return container;
}
