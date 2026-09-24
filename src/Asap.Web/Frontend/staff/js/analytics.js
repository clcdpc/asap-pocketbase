import { authorizedJson } from './http.js';
import { isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';

const dateRangeLabels = {
  last30: 'Last 30 days',
  last90: 'Last 90 days',
  lastMonth: 'Last month',
  thisMonth: 'This month'
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
let analyticsRange = 'lastMonth';
const analyticsLoads = createLatestLoad();

function formatAnalyticsDate(iso) {
  if (!iso) return '';
  var d = new Date(iso);
  return d.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric' });
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

function analyticsUrl() {
  const params = new URLSearchParams();
  params.set('range', analyticsRange);
  if (analyticsScope) {
    params.set('scope', analyticsScope);
  }
  params.set('_', String(Date.now()));
  return '/api/asap/staff/analytics?' + params.toString();
}

function renderStatus(container, className, message) {
  const node = document.createElement('div');
  node.className = className;
  node.textContent = message;
  container.replaceChildren(node);
}

export async function loadAnalytics(container) {
  if (!container) return;
  const guard = analyticsLoads.begin('analytics');
  renderStatus(container, 'alert alert-light border', 'Loading analytics...');

  try {
    const data = await authorizedJson(analyticsUrl(), {
      cache: 'no-store',
      signal: guard.signal
    });
    if (!guard.isCurrent()) return;
    analyticsScope = data.scope && data.scope.mode === 'all' ? 'all' : (data.scope && data.scope.libraryOrgId) || analyticsScope;
    renderAnalytics(container, data);
  } catch (err) {
    if (isAbortError(err) || !guard.isCurrent()) return;
    renderStatus(container, 'alert alert-danger', err.message || 'Analytics could not be loaded.');
  } finally {
    analyticsLoads.finish('analytics', guard.token);
  }
}

export function refreshAnalyticsView(container) {
  return loadAnalytics(container);
}

function renderAnalytics(container, data) {
  const shell = document.createElement('section');
  shell.className = 'analytics-shell';
  shell.setAttribute('aria-labelledby', 'analytics-title');
  shell.append(
    renderAnalyticsHeader(data),
    renderSummaryCards(data.summary),
    renderAnalyticsGrid(data)
  );
  container.replaceChildren(shell);
  bindAnalyticsControls(container);
}

function renderAnalyticsHeader(data) {
  const header = document.createElement('div');
  header.className = 'analytics-header';

  const titleGroup = document.createElement('div');
  const h2 = document.createElement('h2');
  h2.id = 'analytics-title';
  h2.className = 'h4 mb-1';
  h2.textContent = 'Analytics';
  const p = document.createElement('p');
  p.className = 'text-muted mb-0';
  p.textContent = 'Operational summary for ' + data.scope.label + ': ' + formatAnalyticsDate(data.dateRange.start) + ' through ' + formatAnalyticsDate(data.dateRange.end);
  titleGroup.append(h2, p);

  const controls = document.createElement('div');
  controls.className = 'analytics-controls';
  controls.append(
    renderScopeControl(data),
    renderDateRangeControl(data.dateRange.key)
  );

  header.append(titleGroup, controls);
  return header;
}

function renderScopeControl(data) {
  const control = document.createElement('div');
  control.className = 'analytics-control';

  if (!data.scope.superAdmin) {
    const span = document.createElement('span');
    span.className = 'analytics-control-label';
    span.textContent = 'Scope';
    const strong = document.createElement('strong');
    strong.textContent = data.scope.label;
    control.append(span, strong);
    return control;
  }

  const libraries = (data.availableLibraries || []).slice();
  if (data.scope.mode === 'library' && data.scope.libraryOrgId && !libraries.some(library => library.orgId === data.scope.libraryOrgId)) {
    libraries.push({ orgId: data.scope.libraryOrgId, name: data.scope.label || 'Current library' });
  }

  const label = document.createElement('label');
  label.className = 'analytics-control';

  const labelSpan = document.createElement('span');
  labelSpan.className = 'analytics-control-label';
  labelSpan.textContent = 'Scope';

  const select = document.createElement('select');
  select.id = 'analytics-scope';
  select.className = 'form-control form-control-sm';

  const allOption = document.createElement('option');
  allOption.value = 'all';
  allOption.textContent = 'All libraries';
  if (data.scope.mode === 'all') allOption.selected = true;
  select.appendChild(allOption);

  libraries.forEach(library => {
    const option = document.createElement('option');
    option.value = library.orgId;
    option.textContent = library.name + ' (ID ' + library.orgId + ')';
    if (data.scope.mode === 'library' && data.scope.libraryOrgId === library.orgId) option.selected = true;
    select.appendChild(option);
  });

  label.append(labelSpan, select);
  return label;
}

function renderDateRangeControl(selected) {
  const label = document.createElement('label');
  label.className = 'analytics-control';

  const span = document.createElement('span');
  span.className = 'analytics-control-label';
  span.textContent = 'Date range';

  const select = document.createElement('select');
  select.id = 'analytics-date-range';
  select.className = 'form-control form-control-sm';

  Object.entries(dateRangeLabels).forEach(([value, labelText]) => {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = labelText;
    if (value === selected) option.selected = true;
    select.appendChild(option);
  });

  label.append(span, select);
  return label;
}

function renderSummaryCards(summary) {
  const container = document.createElement('div');
  container.className = 'analytics-summary';
  container.setAttribute('aria-label', 'Summary metrics');
  container.append(
    renderSummaryCard('New suggestions', formatCount(summary.newSuggestions), 'Created in selected period'),
    renderSummaryCard('Open requests', formatCount(summary.openRequests), 'Current non-closed requests'),
    renderSummaryCard('Closed requests', formatCount(summary.closedRequests), 'Closed and updated in selected period'),
    renderSummaryCard('Avg days to hold', formatDays(summary.averageDaysToHold), 'Created to first Polaris hold placement')
  );
  return container;
}

function renderSummaryCard(label, value, hint) {
  const article = document.createElement('article');
  article.className = 'analytics-card';

  const labelDiv = document.createElement('div');
  labelDiv.className = 'analytics-card-label';
  labelDiv.textContent = label;

  const valueDiv = document.createElement('div');
  valueDiv.className = 'analytics-card-value';
  valueDiv.textContent = value;

  const hintDiv = document.createElement('div');
  hintDiv.className = 'analytics-card-hint';
  hintDiv.textContent = hint;

  article.append(labelDiv, valueDiv, hintDiv);
  return article;
}

function renderAnalyticsGrid(data) {
  const grid = document.createElement('div');
  grid.className = 'analytics-grid';
  grid.append(
    renderStageCounts(data.stageCounts),
    renderAging(data.aging),
    renderClosedReasons(data.closedReasons),
    renderExceptions(data.exceptions)
  );
  return grid;
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
  const article = document.createElement('article');
  article.className = 'analytics-panel';

  const header = document.createElement('div');
  header.className = 'analytics-panel-header';

  const h3 = document.createElement('h3');
  h3.className = 'h6 mb-1';
  h3.textContent = title;

  const p = document.createElement('p');
  p.className = 'text-muted small mb-0';
  p.textContent = hint;

  header.append(h3, p);
  article.append(header, body);
  return article;
}

function renderRows(rows) {
  const table = document.createElement('div');
  table.className = 'analytics-table';
  table.setAttribute('role', 'table');

  (rows || []).forEach(row => {
    const rowDiv = document.createElement('div');
    rowDiv.className = 'analytics-row';
    rowDiv.setAttribute('role', 'row');

    const labelCell = document.createElement('div');
    labelCell.setAttribute('role', 'cell');
    labelCell.textContent = row.label;

    const valueCell = document.createElement('strong');
    valueCell.setAttribute('role', 'cell');
    valueCell.textContent = row.value;

    rowDiv.append(labelCell, valueCell);
    table.appendChild(rowDiv);
  });

  return table;
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
      analyticsRange = rangeSelect.value || 'lastMonth';
      loadAnalytics(container);
    });
  }
}
