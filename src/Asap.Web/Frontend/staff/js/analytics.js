import { authorizedJson, isAbortError, latestLoads } from './http.js';

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
  'Silently Closed': 'Silent close',
  unrecorded: 'No reason recorded'
};

let analyticsScope = '';
let analyticsRange = 'lastMonth';

function formatDate(value) {
  if (!value) return '';
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? String(value)
    : date.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric' });
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
  if (analyticsScope) params.set('scope', analyticsScope);
  params.set('_', String(Date.now()));
  return `/api/asap/staff/analytics?${params.toString()}`;
}

function renderStatus(container, className, message) {
  const node = document.createElement('p');
  node.className = className;
  node.setAttribute('role', 'status');
  node.textContent = message;
  container.replaceChildren(node);
}

function restoreAnalyticsFocus(container, focusedControlId) {
  if (!['analytics-scope', 'analytics-date-range'].includes(focusedControlId)) return;
  container.querySelector(`#${focusedControlId}`)?.focus();
}

export async function loadAnalytics(
  container,
  allowScopeRecovery = true,
  focusedControlId = document.activeElement?.id || '') {
  if (!container) return;
  const load = latestLoads.begin('analytics');
  renderStatus(container, 'analytics-status', 'Loading analytics...');
  try {
    const data = await authorizedJson(analyticsUrl(), { signal: load.signal });
    if (!load.isCurrent()) return;
    analyticsScope = data.scope?.mode === 'all'
      ? 'all'
      : data.scope?.libraryOrgId || analyticsScope;
    analyticsRange = data.dateRange?.key || analyticsRange;
    renderAnalytics(container, data);
    restoreAnalyticsFocus(container, focusedControlId);
  } catch (error) {
    if (isAbortError(error) || !load.isCurrent()) return;
    if (allowScopeRecovery &&
        error?.status === 400 &&
        error.response?.code === 'invalid_scope' &&
        analyticsScope &&
        analyticsScope !== 'all' &&
        analyticsScope !== 'system') {
      analyticsScope = 'all';
      await loadAnalytics(container, false, focusedControlId);
      return;
    }
    renderStatus(container, 'analytics-status error', error.message || 'Analytics could not be loaded.');
  } finally {
    latestLoads.finish('analytics', load.token);
  }
}

export function refreshAnalyticsView(container) {
  return loadAnalytics(container);
}

export function resetAnalytics() {
  latestLoads.begin('analytics').abort();
  analyticsScope = '';
  analyticsRange = 'lastMonth';
}

function renderAnalytics(container, data) {
  const shell = document.createElement('section');
  shell.className = 'analytics-shell';
  shell.setAttribute('aria-label', 'Analytics results');
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

  const description = document.createElement('p');
  description.className = 'analytics-range-summary';
  description.textContent = `${data.scope.label}: ${formatDate(data.dateRange.start)} through ${formatDate(data.dateRange.end)}`;

  const controls = document.createElement('div');
  controls.className = 'analytics-controls';
  controls.append(renderScopeControl(data), renderDateRangeControl(data.dateRange.key));
  header.append(description, controls);
  return header;
}

function renderScopeControl(data) {
  const label = document.createElement('label');
  label.className = 'analytics-control';
  const labelText = document.createElement('span');
  labelText.className = 'analytics-control-label';
  labelText.textContent = 'Scope';

  if (!data.scope.superAdmin) {
    const value = document.createElement('strong');
    value.textContent = data.scope.label;
    label.append(labelText, value);
    return label;
  }

  const select = document.createElement('select');
  select.id = 'analytics-scope';
  select.setAttribute('aria-label', 'Analytics scope');
  const allOption = document.createElement('option');
  allOption.value = 'all';
  allOption.textContent = 'All libraries';
  select.append(allOption);

  const libraries = (data.availableLibraries || []).slice();
  if (data.scope.mode === 'library' && data.scope.libraryOrgId &&
      !libraries.some(item => item.orgId === data.scope.libraryOrgId)) {
    libraries.push({ orgId: data.scope.libraryOrgId, name: data.scope.label || 'Current library' });
  }
  for (const library of libraries) {
    const option = document.createElement('option');
    option.value = library.orgId;
    option.textContent = `${library.name} (ID ${library.orgId})`;
    select.append(option);
  }
  select.value = data.scope.mode === 'all' ? 'all' : data.scope.libraryOrgId;
  label.append(labelText, select);
  return label;
}

function renderDateRangeControl(selected) {
  const label = document.createElement('label');
  label.className = 'analytics-control';
  const labelText = document.createElement('span');
  labelText.className = 'analytics-control-label';
  labelText.textContent = 'Date range';
  const select = document.createElement('select');
  select.id = 'analytics-date-range';
  select.setAttribute('aria-label', 'Analytics date range');
  for (const [value, text] of Object.entries(dateRangeLabels)) {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = text;
    option.selected = value === selected;
    select.append(option);
  }
  label.append(labelText, select);
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
    renderSummaryCard('Avg days to hold', formatDays(summary.averageDaysToHold), 'Created to first hold placement')
  );
  return container;
}

function renderSummaryCard(label, value, hint) {
  const article = document.createElement('article');
  article.className = 'analytics-card';
  const name = document.createElement('div');
  name.className = 'analytics-card-label';
  name.textContent = label;
  const number = document.createElement('div');
  number.className = 'analytics-card-value';
  number.textContent = value;
  const detail = document.createElement('div');
  detail.className = 'analytics-card-hint';
  detail.textContent = hint;
  article.append(name, number, detail);
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
    Object.keys(stageLabels).map(status => ({ label: stageLabels[status], value: formatCount(stageCounts[status]) }))
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

function renderClosedReasons(reasons) {
  const total = (reasons || []).reduce((sum, row) => sum + Number(row.count || 0), 0);
  const rows = total
    ? reasons.map(row => ({
        label: reasonLabels[row.reason] || row.reason || 'No reason recorded',
        value: `${formatCount(row.count)} (${Math.round((Number(row.count || 0) / total) * 100)}%)`
      }))
    : [{ label: 'No closed outcomes in this range', value: '' }];
  return renderPanel('Closed outcomes', 'Selected date range.', renderRows(rows));
}

function renderExceptions(exceptions) {
  return renderPanel('Exceptions', 'Current records with reliable exception signals.', renderRows([
    { label: 'Hold failures', value: formatCount(exceptions.holdFailures) },
    { label: 'Identifier failures', value: formatCount(exceptions.identifierFailures) }
  ]));
}

function renderPanel(title, hint, body) {
  const article = document.createElement('article');
  article.className = 'analytics-panel';
  const heading = document.createElement('div');
  heading.className = 'analytics-panel-header';
  const h2 = document.createElement('h2');
  h2.textContent = title;
  const p = document.createElement('p');
  p.textContent = hint;
  heading.append(h2, p);
  article.append(heading, body);
  return article;
}

function renderRows(rows) {
  const table = document.createElement('div');
  table.className = 'analytics-table';
  table.setAttribute('role', 'table');
  for (const row of rows || []) {
    const rowNode = document.createElement('div');
    rowNode.className = 'analytics-row';
    rowNode.setAttribute('role', 'row');
    const label = document.createElement('div');
    label.setAttribute('role', 'cell');
    label.textContent = row.label;
    const value = document.createElement('strong');
    value.setAttribute('role', 'cell');
    value.textContent = row.value;
    rowNode.append(label, value);
    table.append(rowNode);
  }
  return table;
}

function bindAnalyticsControls(container) {
  const scope = container.querySelector('#analytics-scope');
  if (scope) scope.addEventListener('change', () => {
    analyticsScope = scope.value || 'all';
    loadAnalytics(container);
  });
  const range = container.querySelector('#analytics-date-range');
  if (range) range.addEventListener('change', () => {
    analyticsRange = range.value || 'lastMonth';
    loadAnalytics(container);
  });
}
