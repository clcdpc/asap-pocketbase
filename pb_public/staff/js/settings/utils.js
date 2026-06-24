export function normalizeExternalSearchUrlTemplate(value) {
  const text = String(value || '').trim();
  if (!text) return '';
  return /^https?:\/\//i.test(text) ? text : `https://${text}`;
}

export function sortAuthorsByLastName(authorsListStr) {
  if (!authorsListStr) return '';
  const authors = authorsListStr.split('\n').map(s => s.trim()).filter(s => s.length > 0);
  authors.sort((a, b) => {
    const getLastName = (name) => {
      if (name.includes(',')) return name.split(',')[0].trim();
      const parts = name.split(' ');
      return parts[parts.length - 1].trim();
    };
    const lastA = getLastName(a).toLowerCase();
    const lastB = getLastName(b).toLowerCase();
    return lastA.localeCompare(lastB);
  });
  return authors.join('\n');
}
