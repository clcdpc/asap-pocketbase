import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { element, commandButton, text, dateTime } from './ui.js';
import { unconfirmedResponseError } from './mutation-outcome.js';

export function createOperationsController({ root, sessionIdentity, announce, onScopeChange,
  onReceipt, clearReceipt, request = authorizedJson }) {
  const state = { operationsScope: 'all', operationMutation: null };
  const reads = createLatestLoad();
  const events = new window.AbortController();
  let active = false;
  let disposed = false;
  const dom = {
    operationsScopeField: root.querySelector('#operations-scope-field'),
    operationsScope: root.querySelector('#operations-scope'),
    runWorkflowNow: root.querySelector('#run-workflow-now'),
    runWeeklyNow: root.querySelector('#run-weekly-now'),
    forceWeeklyNow: root.querySelector('#force-weekly-now'),
    sendTestEmail: root.querySelector('#send-test-email'),
    refreshOperations: root.querySelector('#refresh-operations'),
    queueProgressTable: root.querySelector('#queue-progress-table'),
    emailOperationsTable: root.querySelector('#email-operations-table'),
    outcome: root.querySelector('#operations-outcome'),
  };

  function setLibraries(organizations) {
      if (disposed) return;
      const operationScope = sessionIdentity.preferences()?.role === 'super_admin'
        ? state.operationsScope
        : String(sessionIdentity.preferences()?.organizationId);
      const knownScopes = new Set(['all', ...(organizations || []).map(item => String(item.id))]);
      const reconciledScope = knownScopes.has(String(operationScope)) ? String(operationScope) : 'all';
      dom.operationsScope.replaceChildren(element('option', { value: 'all', text: 'All libraries' }));
      for (const organization of organizations || []) {
        dom.operationsScope.append(element('option', {
          value: organization.id,
          text: organization.name || organization.displayName || String(organization.id)
        }));
      }
      dom.operationsScope.value = reconciledScope;
      state.operationsScope = reconciledScope;
    }

  function setStaff(staff) {
    if (disposed) return;
    reads.begin('operations').abort();
    state.operationMutation = null;
    state.operationsScope = staff?.role === 'super_admin' ? 'all' : String(staff?.organizationId || 'all');
    dom.operationsScopeField.hidden = staff?.role !== 'super_admin';
    dom.operationsScope.replaceChildren(element('option', { value: state.operationsScope,
      text: staff?.role === 'super_admin' ? 'All libraries' : 'My library' }));
    dom.operationsScope.value = state.operationsScope;
    if (staff) {
      try {
        const actorKey = operationStorageKey(staff);
        const legacyKey = `asap.staff.operation.${staff.tenantId || ''}.${staff.id || ''}`;
        const key = window.sessionStorage.getItem(actorKey) ? actorKey : legacyKey;
        const retained = JSON.parse(window.sessionStorage.getItem(key) || 'null');
        if (supportedOperation(retained) &&
            (!retained.actorKey || retained.actorKey === sessionIdentity.actor()?.key) &&
            /^\/api\/asap\/staff\/(workflow\/(run-now|weekly-summary\/run-now\?force=(true|false))|email-operations\/(test|[1-9]\d*\/retry))$/.test(retained.path)) {
          state.operationMutation = { ...retained, storageKey: key, uncertain: true, reviewed: false };
        }
      } catch { /* Invalid recovery cannot authorize dispatch. */ }
    }
    updateOperationControls();
  }

  function operationsQuery(scope = state.operationsScope) {
    return scope && scope !== 'all' ? `?organizationId=${encodeURIComponent(scope)}` : '';
  }

  function supportedOperation(value) {
    if (!value || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value.operationId || '') ||
        typeof value.message !== 'string' || !value.message ||
        !(value.scope === 'all' || /^[1-9]\d{0,9}$/.test(value.scope) && Number(value.scope) > 1 && Number(value.scope) <= 2147483647)) return false;
    if (!/^\/api\/asap\/staff\/(workflow\/(run-now|weekly-summary\/run-now\?force=(true|false))|email-operations\/(test|[1-9]\d*\/retry))$/.test(value.path)) return false;
    return value.path.endsWith('/retry')
      ? typeof value.body?.version === 'string' && Boolean(value.body.version) && Object.keys(value.body).length === 1
      : value.body === undefined || value.body === null;
  }

  function operationStorageKey(staff = sessionIdentity.preferences()) {
    return `asap.staff.operation.${staff?.tenantId || ''}.${staff?.id || ''}.${encodeURIComponent(sessionIdentity.actor()?.key || '')}`;
  }

  function storeOperation(operation, storageKey = operation?.storageKey, expectedId = operation?.operationId) {
    try {
      if (operation) window.sessionStorage.setItem(operation.storageKey, JSON.stringify(operation));
      else if (storageKey && JSON.parse(window.sessionStorage.getItem(storageKey) || 'null')?.operationId === expectedId) {
        window.sessionStorage.removeItem(storageKey);
      }
      return true;
    } catch { return false; }
  }

  function updateOperationControls() {
    if (disposed) return;
    const operation = state.operationMutation;
    const locked = Boolean(operation);
    for (const control of [dom.runWorkflowNow, dom.runWeeklyNow, dom.forceWeeklyNow, dom.sendTestEmail,
      dom.operationsScope, ...dom.emailOperationsTable.querySelectorAll('button')]) control.disabled = locked;
    const notice = dom.outcome;
    notice.hidden = !operation?.uncertain;
    notice.replaceChildren();
    if (!operation?.uncertain) return;
    notice.append(element('p', { text: `${operation.message} outcome is unconfirmed. Refresh Operations to review current work before retrying this same operation. A new operation remains blocked.` }));
    if (operation.reviewed) {
      notice.append(commandButton('Retry same operation', 'refresh', () =>
        runOperation(operation.path, operation.message, operation)));
    }
  }

  function renderOperationsTable(container, columns, rows, emptyText) {
    if (!rows.length) {
      container.replaceChildren(element('p', { className: 'operations-empty', text: emptyText }));
      return;
    }
    const table = element('table');
    const head = element('thead');
    const headerRow = element('tr');
    for (const column of columns) headerRow.append(element('th', { scope: 'col', text: column.label }));
    head.append(headerRow);
    const body = element('tbody');
    for (const row of rows) {
      const tr = element('tr');
      for (const column of columns) {
        const value = column.render ? column.render(row) : element('span', { text: text(row[column.key]) });
        tr.append(element('td', {}, value));
      }
      body.append(tr);
    }
    table.append(head, body);
    container.replaceChildren(table);
  }

  function renderOperations(data) {
    renderOperationsTable(
      dom.queueProgressTable,
      [
        { label: 'Queue', key: 'queueName' },
        { label: 'Cycle watermark', render: row => element('span', { text: row.cycleMaxId === null ? 'None' : String(row.cycleMaxId) }) },
        { label: 'State', render: row => element('span', {
          text: row.cycleMaxId === 0 ? 'Empty' : row.cycleMaxId === null
            ? row.lastOutcomeCode === 'cycle_complete' ? 'Completed' : 'Idle'
            : 'Active'
        }) },
        { label: 'Cursor', render: row => element('span', {
          text: row.lastCreatedUtc ? `${dateTime(row.lastCreatedUtc)} / ${text(row.lastItemId)}` : 'None'
        }) },
        { label: 'Last outcome', render: row => element('span', { text: text(row.lastOutcomeCode, 'Not recorded') }) },
        { label: 'Updated', render: row => element('time', { text: dateTime(row.updatedUtc), datetime: row.updatedUtc }) }
      ],
      data.queue?.items || [],
      'No queue progress has been recorded for this scope.'
    );
    renderOperationsTable(
      dom.emailOperationsTable,
      [
        { label: 'Reference', render: row => element('span', { text: String(row.id) }) },
        { label: 'Created', render: row => element('time', { text: dateTime(row.createdUtc), datetime: row.createdUtc }) },
        { label: 'Status', key: 'status' },
        { label: 'Type', key: 'deliveryClass' },
        { label: 'Error', render: row => element('span', { text: row.lastErrorCode || row.suppressionReason || 'None' }) },
        { label: 'Action', render: row => {
          if (row.status !== 'failed') return element('span', { text: 'No action' });
          return commandButton('Retry', 'refresh', () => retryEmail(row), 'secondary-button');
        } }
      ],
      data.email?.items || [],
      'No email operations have been recorded for this scope.'
    );
  }

  async function loadOperations(options = {}) {
    if (disposed || !sessionIdentity.preferences() || (sessionIdentity.preferences().role !== 'admin' && sessionIdentity.preferences().role !== 'super_admin')) return false;
    const owner = sessionIdentity.preferences();
    const load = reads.begin('operations');
    const requestedScope = state.operationsScope;
    dom.refreshOperations.disabled = true;
    if (!options.silent) announce('Loading workflow operations...');
    try {
      const query = operationsQuery(requestedScope);
      const [queue, email, organizationResult] = await Promise.all([
        request(`/api/asap/staff/workflow/queues${query}`, { signal: load.signal }),
        request(`/api/asap/staff/email-operations${query}`, { signal: load.signal }),
        sessionIdentity.preferences().role === 'super_admin'
          ? request('/api/asap/staff/organizations', { signal: load.signal })
          : Promise.resolve(null)
      ]);
      if (!load.isCurrent() || !sessionIdentity.isCurrent(owner) || requestedScope !== state.operationsScope) return false;
      const organizations = organizationResult?.data ?? organizationResult;
      if (sessionIdentity.preferences().role === 'super_admin' && Array.isArray(organizations)) {
        setLibraries(organizations.filter(item => Number(item.id) > 1 && item.active !== false));
      }
      renderOperations({ queue, email });
      if (state.operationMutation?.uncertain) state.operationMutation.reviewed = true;
      updateOperationControls();
      if (!options.silent) announce('Workflow operations loaded.');
      return true;
    } catch (error) {
      if (load.isCurrent() && sessionIdentity.isCurrent(owner) && requestedScope === state.operationsScope &&
          !options.silent && !isAbortError(error) && error.status !== 401) {
        announce(error.message || 'Workflow operations could not be loaded.', 'error');
      }
      return false;
    } finally {
      if (load.isCurrent()) dom.refreshOperations.disabled = false;
      reads.finish('operations', load.token);
    }
  }

  async function runOperation(path, message, retry = null, body = undefined) {
    if (disposed || !['admin', 'super_admin'].includes(sessionIdentity.preferences()?.role) || state.operationMutation &&
        (retry !== state.operationMutation || !retry.uncertain || !retry.reviewed)) return;
    const owner = sessionIdentity.preferences();
    const operation = retry || { path, message, scope: state.operationsScope,
      operationId: window.crypto.randomUUID(), storageKey: operationStorageKey(), actorKey: sessionIdentity.actor().key,
      body: body ? Object.freeze({ ...body }) : body };
    if (!supportedOperation(operation)) return;
    path = operation.path;
    message = operation.message;
    operation.uncertain = false;
    operation.reviewed = false;
    state.operationMutation = operation;
    // Persist before dispatch: reload/session loss is never evidence that a POST rolled back.
    if (!storeOperation(operation)) {
      if (state.operationMutation === operation) state.operationMutation = null;
      announce('Operation recovery could not be saved. Enable session storage before running manual operations.', 'error');
      return;
    }
    updateOperationControls();
    const scopeQuery = path.endsWith('/retry') ? '' : operationsQuery(operation.scope);
    const query = scopeQuery ? `${path.includes('?') ? '&' : '?'}${scopeQuery.slice(1)}` : '';
    const identityQuery = path.includes('force=true') || path.endsWith('/email-operations/test')
      ? `${path.includes('?') || query ? '&' : '?'}operationId=${encodeURIComponent(operation.operationId)}` : '';
    let committedMessage = '';
    try {
      const result = await request(`${path}${query}${identityQuery}`, { method: 'POST', body: operation.body });
      if (!['queued', 'suppressed'].includes(result?.code)) throw unconfirmedResponseError();
      committedMessage = result.data?.replayed ? `${message} was already recorded as ${result.data.status}. Review email operation ${result.data.id}.`
        : result.code === 'suppressed' ? `${message} was suppressed; no email was sent.`
        : result.manualRunId && !path.endsWith('/retry') ? `${message} Run ${result.manualRunId} queued.` : `${message} queued.`;
      operation.outcome = 'committed';
      onReceipt(`${committedMessage} Sign in again to review workflow operations.`, owner, operation);
      storeOperation(null, operation.storageKey, operation.operationId);
      if (state.operationMutation === operation) state.operationMutation = null;
      if (sessionIdentity.isCurrent(owner)) updateOperationControls();
      if (disposed || !sessionIdentity.isCurrent(owner)) return;
      const refreshed = active ? await loadOperations({ silent: true }) : false;
      if (refreshed === true) clearReceipt(operation);
      if (!disposed && sessionIdentity.isCurrent(owner) && operation.scope === state.operationsScope && active) {
        announce(refreshed ? committedMessage : `${committedMessage} Operations could not be refreshed.`,
          refreshed ? 'success' : 'warning');
      }
    } catch (error) {
      if (committedMessage) {
        if (!disposed && active && sessionIdentity.isCurrent(owner)) announce(`${committedMessage} Operations could not be refreshed.`, 'warning');
        return;
      }
      const uncertain = !error.status || error.status === 408 || error.status >= 500 || isAbortError(error);
      if (uncertain) {
        operation.uncertain = true;
        storeOperation(operation);
        operation.outcome = 'uncertain';
        onReceipt(`${message} outcome is unconfirmed. Review Operations before retrying. Sign in again to check the authoritative result.`, owner, operation);
      } else {
        storeOperation(null, operation.storageKey, operation.operationId);
        if (state.operationMutation === operation) state.operationMutation = null;
      }
      if (sessionIdentity.isCurrent(owner)) updateOperationControls();
      if (!disposed && sessionIdentity.isCurrent(owner) && active && error.status !== 401) {
        announce(uncertain ? `${message} outcome is unconfirmed. Refresh Operations to review it before retrying.`
          : error.message || 'The operation was not queued.', uncertain ? 'warning' : 'error');
      }
    }
  }

  async function retryEmail(row) {
    await runOperation(`/api/asap/staff/email-operations/${encodeURIComponent(row.id)}/retry`,
      `Email retry ${row.id}`, null, { version: row.version });
  }
  const listen = (node, name, handler) => node.addEventListener(name, handler, { signal: events.signal });
  listen(dom.operationsScope, 'change', () => {
    if (state.operationMutation) { dom.operationsScope.value = state.operationsScope; return; }
    state.operationsScope = dom.operationsScope.value;
    onScopeChange();
    void loadOperations();
  });
  listen(dom.runWorkflowNow, 'click', () => runOperation('/api/asap/staff/workflow/run-now', 'Workflow run'));
  listen(dom.runWeeklyNow, 'click', () => runOperation('/api/asap/staff/workflow/weekly-summary/run-now?force=false', 'Weekly summary'));
  listen(dom.forceWeeklyNow, 'click', () => runOperation('/api/asap/staff/workflow/weekly-summary/run-now?force=true', 'Forced weekly summary'));
  listen(dom.sendTestEmail, 'click', () => runOperation('/api/asap/staff/email-operations/test', 'Test email'));
  listen(dom.refreshOperations, 'click', () => loadOperations());
  return {
    setStaff, setLibraries, currentScope: () => state.operationsScope,
    activate() { active = true; },
    deactivate() { active = false; reads.begin('operations').abort(); },
    refresh: loadOperations, run: runOperation,
    inspectDeparture: () => ({ blocked: false, dirty: false }),
    signedOut() { active = false; setStaff(null); },
    dispose() { disposed = true; active = false; events.abort(); reads.begin('operations').abort(); }
  };
}
