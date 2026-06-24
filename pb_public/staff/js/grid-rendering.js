import { escapeAttr, formatPublication, formatNote } from './grid-utils.js';
import {
  applyClaimFilter,
  applySimilarRequestFilter,
  applyTagFilter,
  applyTypeFilter,
  isUnclaimed,
  renderWorkflowTags,
  renderDuplicateSummary
} from './grid-filters.js';
import { getGridColumns } from './grid-columns.js';

export function renderBibIdCell(row, ctx) {
  const bibId = String(row?.bibid || '').trim();
  if (!bibId) return '';
  const url = ctx.leapBibUrl(bibId);
  if (!url || !/^https?:\/\//i.test(url)) return escapeAttr(bibId);
  return gridjs.html(`<a href="${escapeAttr(url)}" target="_blank" rel="noopener noreferrer" data-no-row-edit="true">${escapeAttr(bibId)}</a>`);
}

export function rowMarker(row) {
  return `<span class="asap-row-marker" data-suggestion-id="${escapeAttr(row.id)}" hidden></span>`;
}

export function renderBarcodeCell(row, ctx) {
  const barcode = escapeAttr(row.barcode || '');
  const patronUrl = ctx.leapPatronUrl(row.polarisPatronId || '');
  const barcodeMarkup = patronUrl && /^https?:\/\//i.test(patronUrl)
    ? `<a href="${escapeAttr(patronUrl)}" target="_blank" rel="noopener noreferrer" data-no-row-edit="true">${barcode}</a>`
    : barcode;
  const name = [row.nameFirst, row.nameLast].filter(Boolean).join(' ').trim();
  const nameHtml = name ? `<div class="barcode-patron-name text-muted small">${escapeAttr(name)}</div>` : '';
  const isAdditionalCopy = row.type === 'additional_copy';
  const typeBadge = isAdditionalCopy ? '<div class="flag-badge flag-info mb-1" style="font-size: var(--asap-font-size-xs); cursor: default;">Additional Copy</div>' : '';

  return gridjs.html(`
    <div class="barcode-cell">
      <div class="barcode-content">
        ${typeBadge}
        <div class="barcode-text">${barcodeMarkup}</div>
        ${nameHtml}
      </div>
      <button type="button" class="btn btn-link btn-sm p-0 ml-1 quick-new-suggestion"
              data-barcode="${barcode}"
              data-no-row-edit="true"
              title="New suggestion for this patron"
              aria-label="New suggestion for this patron">
        <i class="fa fa-plus-circle" aria-hidden="true"></i>
      </button>
    </div>
  `);
}

export function renderAdditionalCopySourceCell(row) {
  const sourceId = String(row?.sourceTitleRequest || '').trim();
  if (!sourceId) return '';
  const url = new URL(window.location.href);
  url.searchParams.set('stage', row.sourceStatus || 'pending_hold');
  url.searchParams.set('request', sourceId);
  const label = sourceId.slice(0, 8);
  const statusText = row.sourceStatus ? ` (${row.sourceStatus.replace(/_/g, ' ')})` : '';
  const wrapper = document.createElement('div');
  const link = document.createElement('a');
  link.href = url.pathname + url.search;
  link.dataset.noRowEdit = 'true';
  link.textContent = label;
  const status = document.createElement('div');
  status.className = 'small text-muted';
  status.textContent = statusText;
  wrapper.append(link, status);
  return wrapper;
}

export function renderPolarisRowSearchButton(row, mode, ctx) {
  if (row.status === 'hold_placed') return '';
  const value = ctx.polarisSearchValueForRow(row, mode);
  if (!value) return '';
  return ctx.renderPolarisSearchButtonMarkup(mode, {
    'data-no-row-edit': 'true',
    'data-polaris-search-mode': mode,
    'data-suggestion-id': row.id
  });
}

export function renderTitleCell(row, ctx) {
  return gridjs.html(`
    <div class="staff-title-cell searchable-cell">
      ${rowMarker(row)}
      <div class="searchable-cell-text">
        ${renderDuplicateSummary(row, ctx)}
        <div class="staff-title-main" title="${escapeAttr(row.title || '')}">${escapeAttr(row.title || '')}</div>
        ${renderWorkflowTags(row.workflowTags, row, ctx)}
      </div>
      <div class="searchable-cell-action">
        ${renderPolarisRowSearchButton(row, 'title', ctx)}
      </div>
    </div>
  `);
}

export function renderAuthorCell(row, ctx) {
  const author = (row.author || '').trim();
  if (!author) return '';
  return gridjs.html(`
    <div class="searchable-cell">
      <div class="searchable-cell-text" title="${escapeAttr(author)}">
        <span class="staff-author-text">${escapeAttr(author)}</span>
      </div>
      <div class="searchable-cell-action">
        ${renderPolarisRowSearchButton(row, 'author', ctx)}
      </div>
    </div>
  `);
}

export function renderClaimCell(row, ctx) {
  if (isUnclaimed(row)) {
    return gridjs.html('<span class="claim-badge claim-badge--unclaimed">Unclaimed</span>');
  }
  const source = row.claimType === 'automatic_format_rule' ? 'Auto-assigned by format rule' : 'Manual claim';
  if (ctx.isClaimedByCurrentUser(row)) {
    return gridjs.html(`<div><span class="claim-badge claim-badge--mine" title="${escapeAttr(source)}">Mine</span><div class="small text-muted">${escapeAttr(source)}</div></div>`);
  }
  const name = row.claimedByDisplayName || 'Staff';
  return gridjs.html(`<div><span class="claim-badge claim-badge--claimed" title="Claimed by ${escapeAttr(name)}. ${escapeAttr(source)}">Claimed by ${escapeAttr(name)}</span><div class="small text-muted">${escapeAttr(source)}</div></div>`);
}

export function renderIdentifierCell(row) {
  return escapeAttr(row.identifier || '');
}

export function renderPublicationCell(row) {
  return escapeAttr(formatPublication(row.publication));
}

export { formatNote };

export function normalizedSortText(value) {
  return String(value || '')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();
}

export function dateSortValue(value) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return normalizedSortText(value);
  return date.toISOString();
}

export function bibSortValue(value) {
  const text = String(value || '').trim();
  const num = Number(text);
  return Number.isFinite(num) ? String(num).padStart(20, '0') : normalizedSortText(text);
}

export function claimSortValue(row) {
  if (!row || !String(row.claimedByStaffUserId || '').trim()) return 'zz_unclaimed';
  return normalizedSortText(row.claimedByDisplayName || 'claimed');
}

export function getGridDataRow(row, status, ctx) {
  if (status === 'additional_copies') {
    return {
      id: row.id,
      title: normalizedSortText(row.title),
      author: normalizedSortText(row.author),
      bibid: bibSortValue(row.bibid),
      format: normalizedSortText(ctx.formatMap[row.format] || row.format),
      sourceTitleRequest: normalizedSortText(row.sourceTitleRequest),
      sourceStatus: normalizedSortText(row.sourceStatus),
      createdBy: normalizedSortText(row.createdByUsername),
      created: dateSortValue(row.created),
      notes: normalizedSortText(row.notes),
      actions: row.id
    };
  }

  const base = {
    id: row.id,
    barcode: normalizedSortText(row.barcode),
    title: normalizedSortText(row.title),
    author: normalizedSortText(row.author),
    identifier: normalizedSortText(row.identifier),
    bibid: bibSortValue(row.bibid),
    format: normalizedSortText(ctx.formatMap[row.format] || row.format),
    publication: normalizedSortText(formatPublication(row.publication)),
    submitted: dateSortValue(row.created),
    claimedBy: claimSortValue(row),
    notes: normalizedSortText(row.notes),
    actions: row.id
  };

  if (status === 'closed') {
    base.closeReason = normalizedSortText(ctx.closeReasonMap[row.closeReason] || row.closeReason);
  }

  return base;
}

export function emptyFilteredGridMessage(ctx) {
  if (ctx.currentSimilarRequestFilter === 'similar') {
    return 'No records with similar requests elsewhere match the current filters.';
  }
  if (ctx.currentSimilarRequestFilter === 'unique') {
    return 'No unique records match the current filters.';
  }
  if (ctx.activeTagFilter && ctx.currentClaimFilter !== 'all') {
    return 'No suggestions match this workflow flag and claim filter.';
  }
  if (ctx.activeTagFilter) {
    return 'No suggestions match this workflow flag.';
  }
  if (ctx.currentClaimFilter === 'mine') {
    return 'No requests in this stage are claimed by you.';
  }
  if (ctx.currentClaimFilter === 'unclaimed') {
    return 'No unclaimed requests in this stage.';
  }
  if (ctx.currentClaimFilter === 'mine_unclaimed') {
    return 'No requests in this stage are claimed by you or unclaimed.';
  }
  return 'No suggestions found.';
}

export function renderCurrentGrid(status = null, ctx) {
  const gridStatus = status || ctx.currentStatus;
  ctx.resetGrid();

  if (ctx.gridSearchInput) {
    ctx.gridSearchInput.value = ctx.gridSearchKeyword;
  }

  const visibleRecords = applyClaimFilter(
    applySimilarRequestFilter(
      applyTypeFilter(
        applyTagFilter(ctx.currentSuggestions, ctx),
        ctx
      ),
      ctx
    ),
    ctx.currentClaimFilter,
    ctx.currentStaffId()
  );

  if (!visibleRecords.length) {
    ctx.gridContainer.replaceChildren();
    const empty = document.createElement('div');
    empty.className = 'alert alert-light border';
    empty.textContent = emptyFilteredGridMessage(ctx);
    ctx.gridContainer.appendChild(empty);
    return;
  }

  const rowById = new Map();
  visibleRecords.forEach(row => rowById.set(row.id, row));

  const g = new gridjs.Grid({
    columns: getGridColumns(gridStatus, rowById, ctx),
    data: visibleRecords.map(row => getGridDataRow(row, gridStatus, ctx)),
    search: {
      placeholder: 'Search...',
      keyword: ctx.gridSearchKeyword
    },
    pagination: { limit: 25 },
    sort: true,
    width: '100%'
  });

  ctx.setGrid(g);
  g.render(ctx.gridContainer);
}
