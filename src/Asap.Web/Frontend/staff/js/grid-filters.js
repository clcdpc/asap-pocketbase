import { normalizeLabel, getFlagDisplay, getIsbnCheckLabel, effectiveWorkflowFlagsForRow, getFilterableLabelsForRow, cleanWorkflowTags, normalizeStatus } from './grid-policy.mjs';
import { escapeAttr } from './grid-utils.js';

export const duplicateStatusNames = {
  suggestion: 'Suggestions',
  outstanding_purchase: 'Pending purchase',
  pending_hold: 'Pending hold',
  hold_placed: 'Hold placed',
  additional_copies: 'Additional copies',
  closed: 'Closed'
};

export function normalizeMatchText(value) {
  return String(value || '')
    .toLowerCase()
    .replace(/\([^)]*\)\s*$/, '')
    .replace(/[^\w\s]/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

export function normalizeMatchIdentifier(value) {
  return String(value || '')
    .toLowerCase()
    .replace(/[^a-z0-9]/g, '')
    .trim();
}

export function duplicateMatchReasons(row, candidate) {
  const reasons = [];

  const rowId = normalizeMatchIdentifier(row.identifier);
  const candId = normalizeMatchIdentifier(candidate.identifier);
  if (rowId && candId && rowId === candId) {
    reasons.push('identifier');
  }

  const rowBib = normalizeMatchIdentifier(row.bibid);
  const candBib = normalizeMatchIdentifier(candidate.bibid);
  if (rowBib && candBib && rowBib === candBib) {
    reasons.push('BIB ID');
  }

  const rowTitle = normalizeMatchText(row.polarisSearchTitle || row.title);
  const candTitle = normalizeMatchText(candidate.polarisSearchTitle || candidate.title);
  if (rowTitle && candTitle && rowTitle === candTitle) {
    reasons.push('title');
  }

  return reasons;
}

export function getDuplicateSummary(row, ctx) {
  const allSuggestions = ctx?.allSuggestions || [];
  if (!allSuggestions.length) return null;

  const matches = allSuggestions.map(r => {
    if (r.id === row.id) return false;
    const reasons = duplicateMatchReasons(row, r);
    return reasons.length ? { row: r, reasons } : null;
  }).filter(Boolean);

  if (!matches.length) return null;

  const statusCounts = {};
  const reasonSet = new Set();
  matches.forEach(match => {
    let s = normalizeStatus(match.row.status);
    if (match.row.type === 'additional_copy' && s === 'open') {
      s = 'additional_copies';
    }

    let statusLabel = duplicateStatusNames[s] || s;
    if (match.row.libraryOrgId !== row.libraryOrgId && match.row.libraryOrgName) {
      statusLabel += ` (${match.row.libraryOrgName})`;
    }

    statusCounts[statusLabel] = (statusCounts[statusLabel] || 0) + 1;
    match.reasons.forEach(reason => reasonSet.add(reason));
  });

  return {
    count: matches.length,
    statusCounts,
    reasons: Array.from(reasonSet)
  };
}

export function getDuplicateLabels(row, ctx) {
  const summary = getDuplicateSummary(row, ctx);
  if (!summary) return [];

  const labels = [];
  for (const [status, count] of Object.entries(summary.statusCounts)) {
    const displayName = duplicateStatusNames[status] || status;
    const text = count > 1 ? `Dup (${displayName} x${count})` : `Dup (${displayName})`;
    labels.push(text);
  }
  return labels;
}

export function applyTagFilter(records, ctx) {
  const activeTagFilter = ctx?.activeTagFilter || '';
  if (!activeTagFilter) return records || [];
  return (records || []).filter(record => {
    return getFilterableLabelsForRow(record).includes(activeTagFilter);
  });
}

export function applySimilarRequestFilter(records, ctx) {
  const filter = ctx?.currentSimilarRequestFilter || 'all';
  if (filter === 'similar') {
    return (records || []).filter(record => !!getDuplicateSummary(record, ctx));
  }
  if (filter === 'unique') {
    return (records || []).filter(record => !getDuplicateSummary(record, ctx));
  }
  return records || [];
}

export function isUnclaimed(row) {
  return !String(row?.claimedByStaffUserId || '').trim();
}

export function applyClaimFilter(records, filter, staffId) {
  const currentFilter = filter || 'all';
  const currentStaffId = String(staffId || '').trim();
  if (currentFilter === 'mine') {
    return (records || []).filter(record => !!currentStaffId && String(record.claimedByStaffUserId || '').trim() === currentStaffId);
  }
  if (currentFilter === 'unclaimed') {
    return (records || []).filter(record => isUnclaimed(record));
  }
  if (currentFilter === 'mine_unclaimed') {
    return (records || []).filter(record => {
      const claimedBy = String(record.claimedByStaffUserId || '').trim();
      return !claimedBy || (!!currentStaffId && claimedBy === currentStaffId);
    });
  }
  return records || [];
}

export function applyTypeFilter(records, ctx) {
  const currentStatus = ctx?.currentStatus || '';
  const currentClosedTypeFilter = ctx?.currentClosedTypeFilter || 'all';
  if (currentStatus !== 'closed' || currentClosedTypeFilter === 'all') return records || [];
  return (records || []).filter(record => {
    if (currentClosedTypeFilter === 'suggestion') {
      return record.type !== 'additional_copy';
    }
    if (currentClosedTypeFilter === 'additional_copy') {
      return record.type === 'additional_copy';
    }
    return true;
  });
}

export function hasWorkflowTag(row, label) {
  return effectiveWorkflowFlagsForRow(row).includes(label);
}

export function getWorkflowTagPresentation(tag) {
  const label = normalizeLabel(tag);
  const display = getFlagDisplay(label);
  return {
    text: display.label,
    className: display.className
  };
}

export function getDuplicateBadgesHtml(row, ctx) {
  const flags = getDuplicateLabels(row, ctx);
  if (!flags.length) return '';
  const activeTagFilter = ctx?.activeTagFilter || '';
  return flags.map(rawFlag => {
    const normalized = normalizeLabel(rawFlag);
    const display = getFlagDisplay(rawFlag);
    const isActive = activeTagFilter === normalized;
    const title = isActive ? 'Clear filter' : 'Filter by ' + display.label;
    return ` <span class="flag-badge ${escapeAttr(display.className)} ${isActive ? 'active' : ''}" data-tag="${escapeAttr(normalized)}" role="button" title="${escapeAttr(title)}">${escapeAttr(display.label)}</span>`;
  }).join('');
}

export function renderWorkflowTags(tags, row, ctx) {
  const clean = row ? effectiveWorkflowFlagsForRow(row, tags) : cleanWorkflowTags(tags);
  if (row && row.autohold === false && !clean.includes("No hold requested")) {
    clean.push("No hold requested");
  }
  if (!clean.length) {
    return '<div class="text-muted small">No workflow flags</div>';
  }
  const activeTagFilter = ctx?.activeTagFilter || '';
  return `<div class="workflow-tag-list">${clean.map(flag => {
    const normalized = normalizeLabel(flag);
    const presentation = getWorkflowTagPresentation(flag);
    const isActive = activeTagFilter === normalized;
    const title = isActive ? 'Clear filter' : 'Filter by ' + presentation.text;
    return `<span class="flag-badge ${escapeAttr(presentation.className)} ${isActive ? 'active' : ''}" data-tag="${escapeAttr(normalized)}" role="button" title="${escapeAttr(title)}">${escapeAttr(presentation.text)}</span>`;
  }).join('')}</div>`;
}

export function getIsbnCheckBadgesHtml(row, ctx) {
  const label = getIsbnCheckLabel(row);
  if (!label) return '';
  const normalized = normalizeLabel(label);
  const activeTagFilter = ctx?.activeTagFilter || '';
  const isActive = activeTagFilter === normalized;

  const status = typeof row?.isbnCheckStatus === 'string' ? row.isbnCheckStatus : '';
  const tooltip = status === 'pending'
    ? 'Background identifier number processing is still running. This suggestion is already submitted.'
    : 'Identifier number background processing result.';
  const display = getFlagDisplay(label);

  return ` <span class="flag-badge ${escapeAttr(display.className)} ${isActive ? 'active' : ''}" data-tag="${escapeAttr(normalized)}" role="button" title="${escapeAttr(isActive ? 'Clear filter' : tooltip)}">${escapeAttr(display.label)}</span>`;
}

export function renderDuplicateSummary(row, ctx) {
  const summary = getDuplicateSummary(row, ctx);
  if (!summary) return '';

  const id = `duplicate-details-${escapeAttr(row.id || '')}`;
  const label = summary.count === 1
    ? 'Similar request elsewhere'
    : `Similar request elsewhere: ${summary.count} matches`;
  const statusLines = Object.entries(summary.statusCounts)
    .sort((a, b) => (duplicateStatusNames[a[0]] || a[0]).localeCompare(duplicateStatusNames[b[0]] || b[0]))
    .map(([status, count]) => {
      const statusName = duplicateStatusNames[status] || status;
      return `<li>${escapeAttr(String(count))} in ${escapeAttr(statusName)}</li>`;
    })
    .join('');
  const reasons = summary.reasons.length ? summary.reasons.join('/') : 'title or identifier';

  return `
    <div class="duplicate-summary">
      <button
        type="button"
        class="duplicate-summary-btn"
        aria-expanded="false"
        aria-controls="${id}"
        data-no-row-edit="true"
      >
        <span class="duplicate-summary-icon" aria-hidden="true">▸</span>
        <span>${escapeAttr(label)}</span>
      </button>
      <div id="${id}" class="duplicate-details hidden">
        <div>This title or identifier appears in another ASAP stage.</div>
        <ul>${statusLines}</ul>
        <div>Matched by: ${escapeAttr(reasons)}</div>
      </div>
    </div>
  `;
}
