export function normalizeStatus(value) {
  return String(value || '').trim();
}

export function normalizeLabel(label) {
  const clean = String(label || '').trim();
  if (!clean) return '';
  const lower = clean.toLowerCase();
  if (lower === 'dupe found in polaris' || lower === 'identifier found' || lower === 'identifier number found') {
    return 'Identifier found';
  }
  if (lower === 'isbn not found in system' || lower === 'identifier number not found in system' || lower === 'identifier number not found') {
    return 'Identifier number not found in system';
  }
  return clean;
}

export const flagDisplayMap = {
  'Dup (Suggestion)': {
    label: 'Also in Suggestions',
    className: 'flag-related'
  },
  'Duplicate suggestion': {
    label: 'Also in Suggestions',
    className: 'flag-related'
  },
  'Dup (Closed)': {
    label: 'Also in Closed',
    className: 'flag-related'
  },
  'Dup (Closed x2)': {
    label: 'Also in Closed x2',
    className: 'flag-related'
  },
  'Hold exists (same patron)': {
    label: 'Patron already has hold',
    className: 'flag-success'
  },
  'No holdable items': {
    label: 'No holdable items',
    className: 'flag-warning'
  },
  'Hold placed': {
    label: 'Hold placed',
    className: 'flag-success'
  },
  'Hold failed': {
    label: 'Hold failed',
    className: 'flag-error'
  },
  '! Hold failed': {
    label: 'Hold failed',
    className: 'flag-error'
  },
  'Identifier found': {
    label: 'Identifier present',
    className: 'flag-info'
  },
  'Identifier number found': {
    label: 'Identifier present',
    className: 'flag-info'
  },
  'Identifier number not found in system': {
    label: 'Identifier not found in catalog',
    className: 'flag-warning'
  },
  'Identifier number not found': {
    label: 'Identifier not found in catalog',
    className: 'flag-warning'
  },
  'No hold requested': {
    label: 'No auto-hold',
    className: 'flag-muted'
  }
};

export function getFlagDisplay(rawFlag) {
  const raw = String(rawFlag || '').trim();
  if (flagDisplayMap[raw]) return flagDisplayMap[raw];

  const duplicateMatch = raw.match(/^Dup \((.+)\)$/);
  if (duplicateMatch) {
    return {
      label: `Also in ${duplicateMatch[1].trim()}`,
      className: 'flag-related'
    };
  }

  if (/^!?\s*Hold failed/i.test(raw)) {
    return {
      label: 'Hold failed',
      className: 'flag-error'
    };
  }

  return {
    label: raw,
    className: 'flag-info'
  };
}

function isSimilarRequestFlag(flag) {
  const raw = String(flag || '').trim();
  return raw === 'Duplicate suggestion' || /^Dup \(/.test(raw);
}

export function normalizeWorkflowTagLabel(tag) {
  return normalizeLabel(tag);
}

export function cleanWorkflowTags(tags) {
  if (!Array.isArray(tags)) return [];
  const seen = new Set();
  const clean = [];
  tags.forEach(tag => {
    const label = normalizeWorkflowTagLabel(tag);
    if (!label || /^\d+$/.test(label) || seen.has(label)) return;
    seen.add(label);
    clean.push(label);
  });
  return clean;
}

export function effectiveWorkflowFlagsForRow(row, tags = row?.workflowTags) {
  let clean = cleanWorkflowTags(tags).filter(flag => !isSimilarRequestFlag(flag));

  if (normalizeStatus(row?.status) === 'hold_placed' || clean.includes('Hold placed')) {
    clean = clean.filter(flag => flag !== 'No holdable items' && flag !== 'Hold failed' && flag !== '! Hold failed');
  }

  const hasIdentifierFound = clean.includes('Identifier found');
  const hasIdentifierNotFound = clean.includes('Identifier number not found in system');

  if (!hasIdentifierFound && !hasIdentifierNotFound) {
    return clean;
  }

  const flags = clean.filter(flag => flag !== 'Identifier found' && flag !== 'Identifier number not found in system');
  const status = typeof row?.isbnCheckStatus === 'string' ? row.isbnCheckStatus : '';
  const bibid = String(row?.bibid || '').trim();

  if (bibid || status === 'found' || (hasIdentifierFound && status !== 'not_found')) {
    flags.push('Identifier found');
  } else {
    flags.push('Identifier number not found in system');
  }

  return flags;
}

export function getIsbnCheckLabel(row) {
  const status = typeof row?.isbnCheckStatus === 'string' ? row.isbnCheckStatus : '';
  const isbnStatusLabels = {
    pending: 'New / identifier number check in progress',
    found: 'Identifier number found',
    not_found: 'Identifier number not found',
    error_max_retries: 'Identifier number check retry limit reached'
  };
  const label = isbnStatusLabels[status];
  if (!label) return '';

  if (status === 'found' && effectiveWorkflowFlagsForRow(row).includes('Identifier found')) return '';
  if (status === 'not_found' && effectiveWorkflowFlagsForRow(row).includes('Identifier number not found in system')) return '';

  return label;
}

export function getFilterableLabelsForRow(row) {
  const flags = new Set();

  effectiveWorkflowFlagsForRow(row).forEach(flag => flags.add(normalizeLabel(flag)));

  if (row?.autohold === false) {
    flags.add('No hold requested');
  }

  const isbnLabel = getIsbnCheckLabel(row);
  if (isbnLabel) flags.add(normalizeLabel(isbnLabel));

  return Array.from(flags).filter(Boolean).sort((a, b) => {
    const aDisplay = getFlagDisplay(a).label;
    const bDisplay = getFlagDisplay(b).label;
    return aDisplay.localeCompare(bDisplay) || a.localeCompare(b);
  });
}

export function tagCountsForRecords(records) {
  const counts = new Map();
  (records || []).forEach(record => {
    getFilterableLabelsForRow(record).forEach(flag => {
      counts.set(flag, (counts.get(flag) || 0) + 1);
    });
  });
  return Array.from(counts.entries()).sort((a, b) => {
    const aDisplay = getFlagDisplay(a[0]).label;
    const bDisplay = getFlagDisplay(b[0]).label;
    return aDisplay.localeCompare(bDisplay) || a[0].localeCompare(b[0]);
  });
}
