import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { byDisplayLabel } from './display-order.js';

const dateRangeLabels = {
  last30: 'Last 30 days',
  last90: 'Last 90 days',
  lastMonth: 'Last month',
  thisMonth: 'This month',
  custom: 'Custom date range'
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

function isCalendarDate(value) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value || '');
  if (!match) return false;
  const year = Number(match[1]), month = Number(match[2]), day = Number(match[3]);
  if (year < 1 || month < 1 || month > 12) return false;
  const leapYear = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
  const monthDays = [31, leapYear ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  return day >= 1 && day <= monthDays[month - 1];
}

function formatBusinessDate(value) {
  if (!isCalendarDate(value)) return String(value || '');
  const [year, month, day] = value.split('-').map(Number);
  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  date.setUTCHours(0, 0, 0, 0);
  return date.toLocaleDateString('en-US', {
    timeZone: 'UTC', year: 'numeric', month: 'short', day: 'numeric'
  });
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

function createControlLabel(text) {
  const label = document.createElement('label');
  label.className = 'analytics-control';
  const caption = document.createElement('span');
  caption.className = 'analytics-control-label';
  caption.textContent = text;
  label.append(caption);
  return label;
}

function renderAppliedRange(data) {
  const range = data.dateRange || {};
  const startDate = String(range.startDate || range.start || '').slice(0, 10);
  const endDate = String(range.endDate || range.end || '').slice(0, 10);
  const timezone = range.timeZoneId || 'Business timezone';
  return `${data.scope?.label || 'Selected scope'}: ${formatBusinessDate(startDate)} through ${formatBusinessDate(endDate)} (${timezone})`;
}

function createAnalyticsShell(staff, selectedScope, selectedRange) {
  const shell = document.createElement('section');
  shell.className = 'analytics-shell';
  shell.setAttribute('aria-label', 'Analytics results');

  const header = document.createElement('div');
  header.className = 'analytics-header';
  const rangeSummary = document.createElement('p');
  rangeSummary.className = 'analytics-range-summary';
  rangeSummary.setAttribute('aria-live', 'polite');
  rangeSummary.textContent = 'No analytics report has been loaded yet.';

  const controls = document.createElement('div');
  controls.className = 'analytics-controls';
  const scopeLabel = createControlLabel('Scope');
  let scopeSelect = null;
  let scopeValue = null;
  if (!staff?.role || staff.role === 'super_admin') {
    scopeSelect = document.createElement('select');
    scopeSelect.id = 'analytics-scope';
    scopeSelect.setAttribute('aria-label', 'Analytics scope');
    const all = document.createElement('option');
    all.value = 'all';
    all.textContent = 'All libraries';
    scopeSelect.append(all);
    if (selectedScope && selectedScope !== 'all') {
      const selected = document.createElement('option');
      selected.value = selectedScope;
      selected.textContent = `Selected library (ID ${selectedScope})`;
      scopeSelect.append(selected);
    }
    scopeSelect.value = selectedScope || 'all';
    scopeLabel.append(scopeSelect);
  } else {
    scopeValue = document.createElement('strong');
    scopeValue.textContent = staff.organizationName || `Library ${staff.organizationId}`;
    scopeLabel.append(scopeValue);
  }

  const rangeLabel = createControlLabel('Date range');
  const rangeSelect = document.createElement('select');
  rangeSelect.id = 'analytics-date-range';
  rangeSelect.setAttribute('aria-label', 'Analytics date range');
  for (const [value, text] of Object.entries(dateRangeLabels)) {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = text;
    rangeSelect.append(option);
  }
  rangeSelect.value = selectedRange;
  rangeLabel.append(rangeSelect);
  controls.append(scopeLabel, rangeLabel);
  header.append(rangeSummary, controls);

  const editor = document.createElement('div');
  editor.className = 'analytics-custom-range';
  editor.hidden = selectedRange !== 'custom';
  const startLabel = createControlLabel('Start date');
  const startInput = document.createElement('input');
  startInput.id = 'analytics-start-date';
  startInput.type = 'date';
  startInput.required = true;
  startInput.setAttribute('aria-label', 'Start date');
  startInput.autocomplete = 'off';
  const startError = document.createElement('p');
  startError.id = 'analytics-start-date-error';
  startError.className = 'analytics-field-error';
  startError.hidden = true;
  startLabel.append(startInput, startError);

  const endLabel = createControlLabel('End date');
  const endInput = document.createElement('input');
  endInput.id = 'analytics-end-date';
  endInput.type = 'date';
  endInput.required = true;
  endInput.setAttribute('aria-label', 'End date');
  endInput.autocomplete = 'off';
  const endError = document.createElement('p');
  endError.id = 'analytics-end-date-error';
  endError.className = 'analytics-field-error';
  endError.hidden = true;
  endLabel.append(endInput, endError);

  const apply = document.createElement('button');
  apply.id = 'analytics-apply-range';
  apply.className = 'primary-button';
  apply.type = 'button';
  apply.textContent = 'Apply';
  editor.append(startLabel, endLabel, apply);

  const status = document.createElement('div');
  status.className = 'analytics-status';
  status.setAttribute('role', 'status');
  status.setAttribute('aria-live', 'polite');
  status.setAttribute('aria-atomic', 'true');
  const statusMessage = document.createElement('p');
  status.append(statusMessage);
  const retry = document.createElement('button');
  retry.className = 'secondary-button analytics-retry';
  retry.type = 'button';
  retry.textContent = 'Retry report';
  retry.hidden = true;
  status.append(retry);

  const results = document.createElement('div');
  results.className = 'analytics-results';
  const placeholder = document.createElement('p');
  placeholder.className = 'empty-state';
  placeholder.textContent = 'Analytics results will appear here.';
  results.append(placeholder);

  shell.append(header, editor, status, results);
  return {
    shell, rangeSummary, scopeSelect, scopeValue, rangeSelect, editor, startInput, endInput,
    startError, endError, apply, status, statusMessage, retry, results
  };
}

function renderSummaryCards(summary) {
  const container = document.createElement('div');
  container.className = 'analytics-summary';
  container.setAttribute('aria-label', 'Summary metrics');
  container.append(
    renderSummaryCard('New suggestions', formatCount(summary.newSuggestions), 'Created in selected period'),
    renderSummaryCard('Open requests', formatCount(summary.openRequests), 'Current non-closed requests'),
    renderSummaryCard('Closed requests', formatCount(summary.closedRequests), 'Closed and updated in selected period'),
    renderSummaryCard('Avg days to hold', formatDays(summary.averageDaysToHold), 'First hold placements in selected period')
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
    `Current open requests over ${formatCount(aging.thresholdDays)} days: ${formatCount(aging.openOlderThanThreshold)}`,
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

export function createAnalyticsController({ root: container, sessionIdentity }) {
  const reads = createLatestLoad();
  let selectedScope = '', editorRange = 'lastMonth', draftStartDate = '', draftEndDate = '';
  let active = false, disposed = false, preferenceOwner = null, ui = null;
  let appliedSelection = null, reportData = null, activeRequest = null, lastAttemptedSelection = null;

  function initialScope(staff) {
    if (staff?.role === 'super_admin') return 'all';
    return staff?.organizationId ? String(staff.organizationId) : '';
  }

  function selectionCopy(selection) {
    return {
      range: selection.range,
      startDate: selection.range === 'custom' ? selection.startDate : null,
      endDate: selection.range === 'custom' ? selection.endDate : null,
      scope: selection.scope || ''
    };
  }

  function selectionForRefresh() {
    if (editorRange === 'custom') {
      return selectionCopy({ ...(appliedSelection || { range: 'lastMonth' }), scope: selectedScope });
    }
    return selectionCopy({ range: editorRange || 'lastMonth', scope: selectedScope });
  }

  function selectionForScopeChange() {
    const base = editorRange === 'custom'
      ? (appliedSelection || { range: 'lastMonth' })
      : { range: editorRange || 'lastMonth' };
    return selectionCopy({ ...base, scope: selectedScope });
  }

  function analyticsUrl(selection) {
    const params = new URLSearchParams();
    params.set('range', selection.range);
    if (selection.range === 'custom') {
      params.set('startDate', selection.startDate);
      params.set('endDate', selection.endDate);
    }
    if (selection.scope) params.set('scope', selection.scope);
    params.set('_', String(Date.now()));
    return `/api/asap/staff/analytics?${params.toString()}`;
  }

  function isCurrent(load, request) {
    return !disposed && active && container.isConnected && ui?.shell.isConnected &&
      activeRequest === request && load.isCurrent() && sessionIdentity.isCurrent(request.owner);
  }

  function isMountedControl(element, property) {
    return !disposed && active && container.isConnected && ui?.[property] === element &&
      element?.isConnected && container.contains(element);
  }

  function setFieldError(field, message) {
    const input = field === 'startDate' ? ui?.startInput : ui?.endInput;
    const error = field === 'startDate' ? ui?.startError : ui?.endError;
    if (!input || !error) return;
    error.textContent = message;
    error.hidden = false;
    input.setAttribute('aria-invalid', 'true');
    input.setAttribute('aria-describedby', error.id);
  }

  function clearFieldError(field) {
    if (!ui) return;
    const input = field === 'startDate' ? ui.startInput : ui.endInput;
    const error = field === 'startDate' ? ui.startError : ui.endError;
    error.textContent = '';
    error.hidden = true;
    input.removeAttribute('aria-invalid');
    input.removeAttribute('aria-describedby');
  }

  function clearFieldErrors() {
    clearFieldError('startDate');
    clearFieldError('endDate');
  }

  function showStatus(message, { error = false, retry = false } = {}) {
    if (!ui) return;
    ui.status.classList.toggle('error', error);
    ui.statusMessage.textContent = message;
    ui.retry.hidden = !retry;
  }

  function updateEditor() {
    if (!ui) return;
    if (ui.rangeSelect.value !== editorRange) ui.rangeSelect.value = editorRange;
    ui.editor.hidden = editorRange !== 'custom';
  }

  function updateScopeOptions(data) {
    if (!ui?.scopeSelect) {
      if (ui?.scopeValue && data.scope?.label) ui.scopeValue.textContent = data.scope.label;
      return;
    }
    const libraries = byDisplayLabel(data.availableLibraries || [],
      item => `${item.name} (ID ${item.orgId})`, item => item.orgId);
    const effectiveId = data.scope?.mode === 'library' ? String(data.scope.libraryOrgId || '') : '';
    if (effectiveId && !libraries.some(item => String(item.orgId) === effectiveId)) {
      libraries.push({ orgId: effectiveId, name: data.scope.label || 'Current library' });
    }
    const desired = [{ value: 'all', text: 'All libraries' }, ...libraries.map(item => ({
      value: String(item.orgId), text: `${item.name} (ID ${item.orgId})`
    }))];
    const existing = [...ui.scopeSelect.options].map(option => ({ value: option.value, text: option.textContent }));
    if (JSON.stringify(existing) !== JSON.stringify(desired)) {
      const options = desired.map(item => {
        const option = document.createElement('option');
        option.value = item.value;
        option.textContent = item.text;
        return option;
      });
      ui.scopeSelect.replaceChildren(...options);
    }
    const actualScope = data.scope?.mode === 'all' ? 'all' : effectiveId;
    if (actualScope) selectedScope = actualScope;
    if ([...ui.scopeSelect.options].some(option => option.value === selectedScope)) {
      ui.scopeSelect.value = selectedScope;
    }
  }

  function renderResults(data) {
    ui.results.replaceChildren(renderSummaryCards(data.summary || {}), renderAnalyticsGrid(data));
    ui.results.removeAttribute('aria-busy');
    ui.rangeSummary.textContent = renderAppliedRange(data);
  }

  function ensureShell(staff) {
    if (ui?.shell.isConnected) return;
    ui = createAnalyticsShell(staff, selectedScope, editorRange);
    container.replaceChildren(ui.shell);
    bindControls(ui);
    updateEditor();
  }

  function bindControls(shellUi) {
    const live = () => !disposed && active && ui === shellUi && shellUi.shell.isConnected && container.contains(shellUi.shell);
    if (shellUi.scopeSelect) shellUi.scopeSelect.addEventListener('change', () => {
      if (!live() || !isMountedControl(shellUi.scopeSelect, 'scopeSelect')) return;
      selectedScope = shellUi.scopeSelect.value || 'all';
      void loadAnalytics(selectionForScopeChange());
    });
    shellUi.rangeSelect.addEventListener('change', () => {
      if (!live() || !isMountedControl(shellUi.rangeSelect, 'rangeSelect')) return;
      editorRange = shellUi.rangeSelect.value || 'lastMonth';
      clearFieldErrors();
      updateEditor();
      if (editorRange !== 'custom') {
        void loadAnalytics({ range: editorRange, scope: selectedScope });
      }
    });
    shellUi.startInput.addEventListener('input', () => {
      if (!live() || !isMountedControl(shellUi.startInput, 'startInput')) return;
      draftStartDate = shellUi.startInput.value;
      clearFieldError('startDate');
    });
    shellUi.endInput.addEventListener('input', () => {
      if (!live() || !isMountedControl(shellUi.endInput, 'endInput')) return;
      draftEndDate = shellUi.endInput.value;
      clearFieldError('endDate');
    });
    shellUi.apply.addEventListener('click', () => {
      if (!live() || !isMountedControl(shellUi.apply, 'apply')) return;
      applyCustomRange();
    });
    shellUi.retry.addEventListener('click', () => {
      if (!live() || !isMountedControl(shellUi.retry, 'retry') || !lastAttemptedSelection) return;
      void loadAnalytics(selectionCopy(lastAttemptedSelection));
    });
  }

  function validateCustomRange(startDate, endDate) {
    clearFieldErrors();
    draftStartDate = startDate;
    draftEndDate = endDate;
    if (!isCalendarDate(startDate)) {
      setFieldError('startDate', startDate ? 'Enter a valid start date.' : 'Enter a start date.');
      ui.startInput.focus();
      return false;
    }
    if (!isCalendarDate(endDate)) {
      setFieldError('endDate', endDate ? 'Enter a valid end date.' : 'Enter an end date.');
      ui.endInput.focus();
      return false;
    }
    if (startDate > endDate) {
      setFieldError('endDate', 'End date must be on or after the start date.');
      ui.endInput.focus();
      return false;
    }
    return true;
  }

  function applyCustomRange() {
    const startDate = ui.startInput.value;
    const endDate = ui.endInput.value;
    if (!validateCustomRange(startDate, endDate)) {
      showStatus('Check the custom date range and try again.', { error: true });
      return;
    }
    void loadAnalytics({ range: 'custom', startDate, endDate, scope: selectedScope });
  }

  function applyServerDateErrors(error, request) {
    if (request.selection.range !== 'custom' ||
        draftStartDate !== request.selection.startDate || draftEndDate !== request.selection.endDate) {
      return false;
    }
    const errors = error.response?.errors || {};
    let firstField = null;
    for (const field of ['startDate', 'endDate']) {
      const message = Array.isArray(errors[field]) ? errors[field][0] : null;
      if (typeof message === 'string' && message.trim()) {
        setFieldError(field, message);
        firstField ||= field;
      }
    }
    if (firstField && document.activeElement === ui.apply) {
      (firstField === 'startDate' ? ui.startInput : ui.endInput).focus();
    }
    return Boolean(firstField);
  }

  function responseSelection(data, requested) {
    const range = data.dateRange?.key || requested.range;
    return selectionCopy({
      range,
      startDate: range === 'custom' ? data.dateRange?.startDate : null,
      endDate: range === 'custom' ? data.dateRange?.endDate : null,
      scope: data.scope?.mode === 'all' ? 'all' : data.scope?.libraryOrgId || requested.scope
    });
  }

  async function loadAnalytics(selection, allowScopeRecovery = true) {
    if (disposed || !active || !container.isConnected || !sessionIdentity.preferences()) return false;
    const owner = sessionIdentity.preferences();
    ensureShell(preferenceOwner || owner);
    const normalized = selectionCopy(selection);
    const request = { selection: normalized, owner };
    activeRequest = request;
    lastAttemptedSelection = selectionCopy(normalized);
    const load = reads.begin('analytics');
    const hasReport = Boolean(reportData);
    ui.results.setAttribute('aria-busy', 'true');
    showStatus(hasReport ? 'Updating analytics. The last successful report remains displayed.' : 'Loading analytics.');
    try {
      const data = await authorizedJson(analyticsUrl(normalized), { signal: load.signal });
      if (!isCurrent(load, request)) return false;
      reportData = data;
      appliedSelection = responseSelection(data, normalized);
      updateScopeOptions(data);
      renderResults(data);
      showStatus('Analytics report updated.');
      lastAttemptedSelection = null;
    } catch (error) {
      if (isAbortError(error) || !isCurrent(load, request)) return false;
      if (allowScopeRecovery &&
          error?.status === 400 &&
          error.response?.code === 'invalid_scope' &&
          normalized.scope && normalized.scope !== 'all' && normalized.scope !== 'system') {
        selectedScope = 'all';
        if (ui.scopeSelect) ui.scopeSelect.value = 'all';
        await loadAnalytics({ ...normalized, scope: 'all' }, false);
        return false;
      }
      ui.results.removeAttribute('aria-busy');
      const validationError = error.response?.code === 'invalid_date_range' && applyServerDateErrors(error, request);
      showStatus(error.message || 'Analytics could not be loaded.', { error: true, retry: !validationError });
    } finally {
      reads.finish('analytics', load.token);
    }
    return true;
  }

  function resetAnalytics(staff = null) {
    reads.begin('analytics').abort();
    activeRequest = null;
    selectedScope = initialScope(staff);
    editorRange = 'lastMonth';
    draftStartDate = '';
    draftEndDate = '';
    appliedSelection = null;
    reportData = null;
    lastAttemptedSelection = null;
    ui = null;
    container.replaceChildren();
    if (staff) ensureShell(staff);
  }

  return {
    inspectDeparture: () => ({
      owner: active ? JSON.stringify([selectedScope, editorRange, draftStartDate, draftEndDate]) : null,
      blocked: disposed, dirty: false
    }),
    activate() { if (!disposed) active = true; },
    refresh: () => loadAnalytics(selectionForRefresh()),
    deactivate() {
      active = false;
      activeRequest = null;
      reads.begin('analytics').abort();
    },
    setStaff(staff) {
      if (disposed) return;
      if (!sessionIdentity.sameSession(preferenceOwner, staff)) resetAnalytics(staff);
      preferenceOwner = staff;
    },
    signedOut() { active = false; preferenceOwner = null; resetAnalytics(); },
    dispose() {
      if (disposed) return;
      active = false;
      preferenceOwner = null;
      resetAnalytics();
      container.replaceChildren();
      disposed = true;
    }
  };
}
