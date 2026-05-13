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
