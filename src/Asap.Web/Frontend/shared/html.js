export function escapeHtml(str) {
  return String(str || '')
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

export function sanitizeHtml(html) {
  if (!html) return '';
  try {
    const doc = new DOMParser().parseFromString(html, 'text/html');
    const safeTags = ['P', 'BR', 'B', 'I', 'STRONG', 'EM', 'DIV', 'SPAN', 'A', 'UL', 'OL', 'LI', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'BLOCKQUOTE', 'TABLE', 'THEAD', 'TBODY', 'TR', 'TH', 'TD', 'U', 'S', 'HR'];
    const safeAttrs = ['href', 'target', 'rel', 'title', 'class', 'id', 'aria-label', 'aria-hidden'];

    function walk(parent) {
      Array.from(parent.childNodes).forEach(node => {
        if (node.nodeType !== 1) return;

        if (!safeTags.includes(node.tagName)) {
          parent.replaceChild(document.createTextNode(node.textContent), node);
          return;
        }

        for (let i = node.attributes.length - 1; i >= 0; i--) {
          const name = node.attributes[i].name.toLowerCase();
          const value = node.attributes[i].value.trim().toLowerCase();
          const cleanValue = value.replace(/[\s\u0000-\u0020]/g, '');
          const unsafeHref = name === 'href' && (cleanValue.startsWith('javascript:') || cleanValue.startsWith('data:') || cleanValue.startsWith('vbscript:'));
          if (!safeAttrs.includes(name) || unsafeHref) {
            node.removeAttribute(name);
          }
        }
        walk(node);
      });
    }

    walk(doc.body);
    return doc.body.innerHTML;
  } catch (err) {
    return escapeHtml(html);
  }
}

export function sanitizedHtmlFragment(html) {
  const doc = new DOMParser().parseFromString(sanitizeHtml(html), 'text/html');
  const fragment = document.createDocumentFragment();
  for (const child of Array.from(doc.body.childNodes)) {
    fragment.append(document.importNode(child, true));
  }
  return fragment;
}

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
