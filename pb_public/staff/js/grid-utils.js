export function escapeAttr(value) {
  return String(value || "")
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

export function formatStandardDate(d) {
  if (!d) return '';
  const date = (d instanceof Date) ? d : new Date(d);
  return date.toLocaleDateString('en-US');
}

export function formatDateTime(value) {
  if (!value) return '';
  const date = new Date(value);
  return formatStandardDate(date) + ' ' + date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function formatPublication(value) {
  return String(value || '').trim();
}

export function formatNote(row) {
  const note = row?.notes;
  const text = String(note || '').trim();
  if (!text) return '';
  return gridjs.html(`<button type="button" class="truncate-note" data-note-record-id="${escapeAttr(row?.id || '')}" data-notes-action="true" data-no-row-edit="true" title="View notes and activity" aria-label="View notes and activity"><i class="fa fa-commenting-o" aria-hidden="true"></i></button>`);
}

export function sanitizeHtml(html) {
  if (!html) return "";
  try {
    const doc = new DOMParser().parseFromString(html, "text/html");
    const safeTags = ["P", "BR", "B", "I", "STRONG", "EM", "DIV", "SPAN", "A", "UL", "OL", "LI", "H1", "H2", "H3", "H4", "H5", "H6", "BLOCKQUOTE", "TABLE", "THEAD", "TBODY", "TR", "TH", "TD", "U", "S", "HR"];
    const safeAttrs = ["href", "target", "rel", "title", "class", "id", "aria-label", "aria-hidden"];

    function walk(parent) {
      const children = Array.from(parent.childNodes);
      children.forEach(node => {
        if (node.nodeType === 1) {
          if (!safeTags.includes(node.tagName)) {
            const text = document.createTextNode(node.textContent);
            parent.replaceChild(text, node);
          } else {
            const attrs = node.attributes;
            for (let i = attrs.length - 1; i >= 0; i--) {
              const name = attrs[i].name.toLowerCase();
              const value = attrs[i].value.trim().toLowerCase();

              const cleanValue = value.replace(/[\s\u0000-\u0020]/g, '');
              if (!safeAttrs.includes(name) || (name === "href" && (cleanValue.startsWith("javascript:") || cleanValue.startsWith("data:") || cleanValue.startsWith("vbscript:")))) {
                node.removeAttribute(name);
              }
            }
            walk(node);
          }
        }
      });
    }

    walk(doc.body);
    return doc.body.innerHTML;
  } catch (err) {
    return escapeAttr(html);
  }
}
