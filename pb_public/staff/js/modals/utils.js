export function workflowStatusLabel(status) {
  const labels = {
    suggestion: 'Suggestions',
    outstanding_purchase: 'Pending purchase',
    pending_hold: 'Pending hold',
    hold_placed: 'Hold placed',
    closed: 'Closed'
  };
  return labels[status] || status || 'current status';
}

export function duplicateOpenRequestMessage(data) {
  const duplicate = data && data.duplicate ? data.duplicate : {};
  const message = String((data && data.message) || 'This patron already has an open request for this BIB ID.').trim();
  const title = String(duplicate.title || '').trim();
  const status = workflowStatusLabel(duplicate.status || '');
  const lines = [message];

  if (title) {
    lines.push(`Existing request: ${title}${status ? ` - ${status}.` : '.'}`);
  } else if (status) {
    lines.push(`Existing request status: ${status}.`);
  }

  lines.push('This request was flagged. Choose another BIB, or close this request as duplicate if it should not continue.');
  return lines.join('\n\n');
}

export function actionErrorMessage(status, data, raw) {
  if (data && data.code === 'duplicate_open_request') {
    return duplicateOpenRequestMessage(data);
  }
  const detail = (data && data.message) || String(raw || '').trim();
  return detail ? `Error updating suggestion (${status}): ${detail}` : `Error updating suggestion (${status})`;
}

export function staffProfileEmail(pb) {
  const model = pb.authStore.model || {};
  return String(model.weekly_action_summary_email || '').trim();
}

export function hasOwn(obj, key) {
  return Object.prototype.hasOwnProperty.call(obj || {}, key);
}

export function basicPolarisSearchText(value) {
  return String(value || '')
    .replace(/\([^()]*\)/g, ' ')
    .replace(/[\u201c\u201d]/g, '"')
    .replace(/[\u2018\u2019]/g, "'")
    .replace(/[\/:;,.]+/g, ' ')
    .replace(/\s+-\s+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

export function looksLikeCatalogWrappedValue(prefix) {
  prefix = String(prefix || '').trim();
  if (!prefix) return false;
  return prefix.includes(' / ') ||
    /[.;:]$/.test(prefix) ||
    /,\s*\d{4}/.test(prefix) ||
    /\b(author|editor|illustrator|director|producer)\.?$/i.test(prefix);
}

export function fallbackPolarisSearchValue(value) {
  const text = String(value || '').replace(/\s+/g, ' ').trim();
  const wrapped = text.match(/^(.*)\(([^()]*)\)\s*$/);
  if (wrapped) {
    const prefix = String(wrapped[1] || '').trim();
    const original = String(wrapped[2] || '').trim();
    if (looksLikeCatalogWrappedValue(prefix)) {
      return basicPolarisSearchText(original);
    }
  }
  return basicPolarisSearchText(text);
}

export function polarisSearchValueForRow(row, mode) {
  if (mode === 'author') {
    return hasOwn(row, 'polarisSearchAuthor')
      ? String(row.polarisSearchAuthor || '').trim()
      : fallbackPolarisSearchValue(row.author);
  }
  if (mode === 'identifier') {
    return String(row.identifier || '').trim();
  }
  return hasOwn(row, 'polarisSearchTitle')
    ? String(row.polarisSearchTitle || '').trim()
    : fallbackPolarisSearchValue(row.title);
}

export function looksLikeCatalogPublicationDate(value) {
  value = String(value || '').trim();
  return /^\d{4}$/.test(value) || /^\d{4}[-/]\d{1,2}([-/]\d{1,2})?$/.test(value);
}

export function normalizedAdditionalCopyPublication(value, isAdditionalCopy, publicationOptions) {
  value = String(value || '').trim();
  if (isAdditionalCopy && value && !publicationOptions.includes(value) && looksLikeCatalogPublicationDate(value)) {
    return publicationOptions[0] || '';
  }
  return value;
}
