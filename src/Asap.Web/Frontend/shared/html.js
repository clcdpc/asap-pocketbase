// Staff-authored patron messages and their staff previews are plain text. Only
// explicit HTTP(S) URLs become links; markup and placeholder values are DOM text.
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
