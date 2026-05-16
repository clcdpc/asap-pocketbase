import { pb } from './state.js';

const dateRangeLabels = {
  last30: 'Last 30 days',
  thisMonth: 'This month',
  last90: 'Last 90 days'
};

const stageLabels = {
  suggestion: 'Suggestions',
  outstanding_purchase: 'Pending purchase',
  pending_hold: 'Pending hold',
  hold_placed: 'Hold placed',
  closed: 'Closed',
  additional_copies: 'Additional copies'
};

const reasonLabels = {
  rejected: 'Rejected',
  hold_completed: 'Hold completed',
  hold_not_picked_up: 'Hold not picked up',
  duplicate_hold: 'Duplicate hold / request',
  manual: 'Manual close',
  purchased_no_hold: 'Purchased, no hold',
  'Silently Closed': 'Silent close'
};

let analyticsScope = '';
let analyticsRange = 'last30';

function escapeHtml(value) {
  return String(value || '').replace(/[&<>"']/g, ch => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
  })[ch]);
}

function formatCount(value) {
  const number = Number(value || 0);
  return Number.isFinite(number) ? number.toLocaleString() : '0';
}

function formatDays(value) {
  const number = Number(value || 0);
  if (!number) return 'N/A';
  return number.toFixed(number >= 10 ? 0 : 1);
}

async function authorizedJson(path) {
  const headers = {};
  if (pb.authStore.token) {
    headers.Authorization = pb.authStore.token;
  }
  const res = await fetch(path, { headers, cache: 'no-store' });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    throw new Error(data.message || 'Analytics could not be loaded.');
  }
  return data;
}

function analyticsUrl() {
  const params = new URLSearchParams();
  params.set('range', analyticsRange);
  if (analyticsScope) {
    params.set('scope', analyticsScope);
  }
  params.set('_', String(Date.now()));
  return '/api/asap/staff/analytics?' + params.toString();
}

export async function loadAnalytics(container) {
  if (!container) return;
  container.innerHTML = '<div class="alert alert-light border">Loading analytics...</div>';

  try {
    const data = await authorizedJson(analyticsUrl());
    analyticsScope = data.scope && data.scope.mode === 'all' ? 'all' : (data.scope && data.scope.libraryOrgId) || analyticsScope;
    renderAnalytics(container, data);
  } catch (err) {
    container.innerHTML = `<div class="alert alert-danger">${escapeHtml(err.message || 'Analytics could not be loaded.')}</div>`;
  }
}

function renderAnalytics(container, data) {
  container.innerHTML = `
    <section class="analytics-shell" aria-labelledby="analytics-title">
      <div class="analytics-header">
        <div>
          <h2 id="analytics-title" class="h4 mb-1">Analytics</h2>
          <p class="text-muted mb-0">Operational summary for ${escapeHtml(data.scope.label)} and ${escapeHtml(dateRangeLabels[data.dateRange.key] || data.dateRange.key)}.</p>
        </div>
        <div class="analytics-controls">
          ${renderScopeControl(data)}
          ${renderDateRangeControl(data.dateRange.key)}
        </div>
      </div>
      ${renderSummaryCards(data.summary)}
      <div class="analytics-grid">
        ${renderStageCounts(data.stageCounts)}
        ${renderAging(data.aging)}
        ${renderClosedReasons(data.closedReasons)}
        ${renderExceptions(data.exceptions)}
      </div>
    </section>
  `;

  bindAnalyticsControls(container);
}

function renderScopeControl(data) {
  if (!data.scope.superAdmin) {
    return `
      <div class="analytics-control">
        <span class="analytics-control-label">Scope</span>
        <strong>${escapeHtml(data.scope.label)}</strong>
      </div>
    `;
  }

  const libraries = (data.availableLibraries || []).slice();
  if (data.scope.mode === 'library' && data.scope.libraryOrgId && !libraries.some(library => library.orgId === data.scope.libraryOrgId)) {
    libraries.push({ orgId: data.scope.libraryOrgId, name: data.scope.label || 'Current library' });
  }

  const options = [
    `<option value="all"${data.scope.mode === 'all' ? ' selected' : ''}>All libraries</option>`,
    ...libraries.map(library => {
      const selected = data.scope.mode === 'library' && data.scope.libraryOrgId === library.orgId ? ' selected' : '';
      return `<option value="${escapeHtml(library.orgId)}"${selected}>${escapeHtml(library.name)} (ID ${escapeHtml(library.orgId)})</option>`;
    })
  ].join('');

  return `
    <label class="analytics-control">
      <span class="analytics-control-label">Scope</span>
      <select id="analytics-scope" class="form-control form-control-sm">${options}</select>
    </label>
  `;
}

function renderDateRangeControl(selected) {
  const options = Object.entries(dateRangeLabels).map(([value, label]) => (
    `<option value="${value}"${value === selected ? ' selected' : ''}>${escapeHtml(label)}</option>`
  )).join('');

  return `
    <label class="analytics-control">
      <span class="analytics-control-label">Date range</span>
      <select id="analytics-date-range" class="form-control form-control-sm">${options}</select>
    </label>
  `;
}

function renderSummaryCards(summary) {
  return `
    <div class="analytics-summary" aria-label="Summary metrics">
      ${renderSummaryCard('New suggestions', formatCount(summary.newSuggestions), 'Created in selected period')}
      ${renderSummaryCard('Open requests', formatCount(summary.openRequests), 'Current non-closed requests')}
      ${renderSummaryCard('Closed requests', formatCount(summary.closedRequests), 'Closed and updated in selected period')}
      ${renderSummaryCard('Avg days to hold', formatDays(summary.averageDaysToHold), 'Created to first Polaris hold placement')}
    </div>
  `;
}

function renderSummaryCard(label, value, hint) {
  return `
    <article class="analytics-card">
      <div class="analytics-card-label">${escapeHtml(label)}</div>
      <div class="analytics-card-value">${escapeHtml(value)}</div>
      <div class="analytics-card-hint">${escapeHtml(hint)}</div>
    </article>
  `;
}

function renderStageCounts(stageCounts) {
  return renderPanel('Requests by stage', 'Current workflow state counts.', renderRows(
    Object.keys(stageLabels).map(status => ({
      label: stageLabels[status],
      value: formatCount(stageCounts[status])
    }))
  ));
}

function renderAging(aging) {
  const rows = (aging.averageAgeByStage || []).map(row => ({
    label: stageLabels[row.status] || row.status,
    value: row.count ? `${formatDays(row.averageAgeDays)} days avg (${formatCount(row.count)})` : 'No open requests'
  }));
  return renderPanel(
    'Open request aging',
    `Open requests over ${formatCount(aging.thresholdDays)} days: ${formatCount(aging.openOlderThanThreshold)}`,
    renderRows(rows)
  );
}

function renderClosedReasons(closedReasons) {
  const total = (closedReasons || []).reduce((sum, row) => sum + Number(row.count || 0), 0);
  const rows = total
    ? closedReasons.map(row => ({
        label: reasonLabels[row.reason] || row.reason || 'No reason recorded',
        value: `${formatCount(row.count)} (${Math.round((Number(row.count || 0) / total) * 100)}%)`
      }))
    : [{ label: 'No closed outcomes in this range', value: '' }];
  return renderPanel('Closed outcomes', 'Selected date range.', renderRows(rows));
}

function renderExceptions(exceptions) {
  const rows = [
    { label: 'Hold failures', value: formatCount(exceptions.holdFailures) },
    { label: 'Identifier failures', value: formatCount(exceptions.identifierFailures) }
  ];
  return renderPanel('Exceptions', 'Current records with reliable exception signals.', renderRows(rows));
}

function renderPanel(title, hint, body) {
  return `
    <article class="analytics-panel">
      <div class="analytics-panel-header">
        <h3 class="h6 mb-1">${escapeHtml(title)}</h3>
        <p class="text-muted small mb-0">${escapeHtml(hint)}</p>
      </div>
      ${body}
    </article>
  `;
}

function renderRows(rows) {
  return `
    <div class="analytics-table" role="table">
      ${(rows || []).map(row => `
        <div class="analytics-row" role="row">
          <div role="cell">${escapeHtml(row.label)}</div>
          <strong role="cell">${escapeHtml(row.value)}</strong>
        </div>
      `).join('')}
    </div>
  `;
}

function bindAnalyticsControls(container) {
  const scopeSelect = container.querySelector('#analytics-scope');
  if (scopeSelect) {
    scopeSelect.addEventListener('change', () => {
      analyticsScope = scopeSelect.value || 'all';
      loadAnalytics(container);
    });
  }

  const rangeSelect = container.querySelector('#analytics-date-range');
  if (rangeSelect) {
    rangeSelect.addEventListener('change', () => {
      analyticsRange = rangeSelect.value || 'last30';
      loadAnalytics(container);
    });
  }
}
