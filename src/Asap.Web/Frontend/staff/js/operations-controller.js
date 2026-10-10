import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { element, commandButton, text, dateTime } from './ui.js';
import { unconfirmedResponseError } from './mutation-outcome.js';
import { actorKey } from './session-identity.js';

export function createOperationsController({ root, sessionIdentity, announce, onScopeChange,
  onReceipt, clearReceipt, request = authorizedJson }) {
  const state = { operationsScope: 'all', operationMutation: null };
  const reads = createLatestLoad();
  const events = new window.AbortController();
  let active = false;
  let disposed = false;
  let libraries = [], staffOwner = null, projection = null;
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
      libraries = Array.isArray(organizations)
        ? organizations.filter(item => Number(item?.id) > 1 && typeof item.name === 'string')
        : [];
      const retained = state.operationMutation;
      if (retained?.scope !== 'all' && !libraries.some(item => String(item.id) === retained?.scope)) clearReview(retained);
      const operationScope = sessionIdentity.preferences()?.role === 'super_admin'
        ? state.operationsScope
        : String(sessionIdentity.preferences()?.organizationId);
      const knownScopes = new Set(['all', ...libraries.map(item => String(item.id))]);
      const reconciledScope = knownScopes.has(String(operationScope)) ? String(operationScope) : 'all';
      dom.operationsScope.replaceChildren(element('option', { value: 'all', text: 'All libraries' }));
      for (const organization of libraries) {
        dom.operationsScope.append(element('option', {
          value: organization.id,
          text: organization.name || organization.displayName || String(organization.id)
        }));
      }
      dom.operationsScope.value = reconciledScope;
      const changed = state.operationsScope !== reconciledScope;
      state.operationsScope = reconciledScope;
      if (changed) onScopeChange();
    }

  function setStaff(staff) {
    if (disposed) return;
    if (staff && sessionIdentity.sameSession(staffOwner, staff) && sessionIdentity.isCurrent(staff)) {
      staffOwner = staff; updateOperationControls(); return;
    }
    staffOwner = staff;
    reads.begin('operations').abort();
    state.operationMutation = null;
    state.operationsScope = staff?.role === 'super_admin' ? 'all' : String(staff?.organizationId || 'all');
    dom.operationsScopeField.hidden = staff?.role !== 'super_admin';
    dom.operationsScope.replaceChildren(element('option', { value: state.operationsScope,
      text: staff?.role === 'super_admin' ? 'All libraries' : 'My library' }));
    dom.operationsScope.value = state.operationsScope;
    if (staff && sessionIdentity.isCurrent(staff)) {
      try {
        // Tenant/staff-only legacy records have no reliable actor authority.
        // They are neither adopted nor cleared by the current actor.
        const key = operationStorageKey(staff);
        const raw = window.sessionStorage.getItem(key);
        const retained = JSON.parse(raw || 'null');
        if (supportedOperation(retained) && retained.actorKey === sessionIdentity.actor().key) {
          state.operationMutation = captureOperation({ ...retained, storageKey: key, storageValue: raw });
          state.operationMutation.uncertain = true;
        }
      } catch { /* Invalid recovery cannot authorize dispatch. */ }
    }
    updateOperationControls();
  }

  function operationsQuery(scope = state.operationsScope) {
    return scope && scope !== 'all' ? `?organizationId=${encodeURIComponent(scope)}` : '';
  }

  function captureOperation(value) {
    const operation = { uncertain: false, reviewed: false, outcome: 'pending' };
    for (const key of ['path', 'message', 'scope', 'operationId', 'storageKey', 'actorKey']) {
      Object.defineProperty(operation, key, { value: value[key], enumerable: true });
    }
    Object.defineProperty(operation, 'body', { value: value.body ? Object.freeze({ ...value.body }) : value.body, enumerable: true });
    Object.defineProperty(operation, 'owner', { value: sessionIdentity.preferences() });
    Object.defineProperty(operation, 'reviewEvidence', { value: null, writable: true });
    Object.defineProperty(operation, 'storageValue', { value: value.storageValue, writable: true });
    return operation;
  }

  function clearReview(operation) {
    if (!operation) return;
    operation.reviewed = false; operation.reviewEvidence = null;
  }

  function canRetry(operation) {
    const evidence = operation?.reviewEvidence;
    return operation?.uncertain && evidence?.operation === operation && evidence.scope === operation.scope &&
      evidence.actorKey === operation.actorKey && sessionIdentity.isCurrent(operation.owner) && sessionIdentity.isCurrent(evidence.owner);
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

  function hasNonemptyString(value) {
    return typeof value === 'string' && value.trim().length > 0;
  }

  function isInt32(value) {
    return Number.isInteger(value) && value >= -2147483648 && value <= 2147483647;
  }

  function hasConfirmedResult(result, operation) {
    const path = operation.path;
    if (path.startsWith('/api/asap/staff/workflow/')) {
      const expectedOrganizationId = operation.scope === 'all' ? 1 : Number(operation.scope);
      if (result?.code !== 'queued' || !hasNonemptyString(result.jobId) ||
          !isInt32(result.organizationId) || result.organizationId !== expectedOrganizationId) return false;
      if (path.endsWith('?force=true')) {
        return typeof result.manualRunId === 'string' &&
          /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(result.manualRunId) &&
          result.manualRunId === operation.operationId;
      }
      return true;
    }

    const retry = path.match(/^\/api\/asap\/staff\/email-operations\/([1-9]\d*)\/retry$/);
    const isRetry = Boolean(retry);
    const isTest = path === '/api/asap/staff/email-operations/test';
    const data = result?.data;
    if (!data || typeof data !== 'object' || Array.isArray(data) ||
        !['queued', ...(isTest ? ['suppressed'] : [])].includes(result?.code) ||
        typeof data.id !== 'string' || !/^[1-9]\d*$/.test(data.id) ||
        !hasNonemptyString(data.version)) return false;

    if (isRetry) {
      return result.code === 'queued' && data.id === retry[1] &&
        typeof data.dispatchDelayed === 'boolean' &&
        !Object.prototype.hasOwnProperty.call(data, 'replayed') &&
        !Object.prototype.hasOwnProperty.call(data, 'status');
    }

    const hasReplayMarker = Object.prototype.hasOwnProperty.call(data, 'replayed');
    const hasReplayStatus = Object.prototype.hasOwnProperty.call(data, 'status');
    if (hasReplayMarker || hasReplayStatus) {
      if (data.replayed !== true || !hasReplayStatus ||
          !['pending', 'sending', 'sent', 'failed', 'suppressed'].includes(data.status) ||
          (result.code === 'suppressed') !== (data.status === 'suppressed')) return false;
      return data.status === 'suppressed'
        ? hasNonemptyString(data.code)
        : data.code === null;
    }

    if (typeof data.dispatchDelayed !== 'boolean') return false;
    return result.code === 'suppressed'
      ? hasNonemptyString(data.code)
      : data.code === null;
  }

  function operationStorageKey(staff = sessionIdentity.preferences()) {
    return `asap.staff.operation.${staff?.tenantId || ''}.${staff?.id || ''}.${encodeURIComponent(actorKey(staff) || '')}`;
  }

  function storeOperation(operation) {
    try {
      // Retry preserves command identity but owns a fresh storage record. A
      // completion from before reload cannot clear this newer retry's evidence.
      const raw = JSON.stringify({ ...operation, recordId: window.crypto.randomUUID() });
      window.sessionStorage.setItem(operation.storageKey, raw);
      operation.storageValue = raw;
      return true;
    } catch { return false; }
  }

  function clearOperation(operation) {
    try {
      if (window.sessionStorage.getItem(operation.storageKey) === operation.storageValue) {
        window.sessionStorage.removeItem(operation.storageKey);
      }
    } catch { /* Unavailable storage retains evidence rather than assuming cleanup. */ }
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
    if (canRetry(operation)) {
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
    const rendered = { owner: sessionIdentity.preferences(), scope: state.operationsScope };
    projection = rendered;
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
          if (row.status !== 'failed' || row.canRetry !== true) return element('span', { text: 'No action' });
          return commandButton('Retry', 'refresh', () => {
            if (active && projection === rendered && sessionIdentity.isCurrent(rendered.owner) && rendered.scope === state.operationsScope) void retryEmail(row);
          }, 'secondary-button');
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
    const retained = state.operationMutation;
    if (retained?.uncertain) { clearReview(retained); updateOperationControls(); }
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
        const activeLibraries = organizations
          .filter(item => Number(item.id) > 1 && item.organizationCodeId === 2 && item.isActive === true)
          .map(item => ({ id: item.id, name: item.name || item.displayName || String(item.id) }));
        setLibraries(activeLibraries);
        if (requestedScope !== state.operationsScope) return loadOperations(options);
      }
      renderOperations({ queue, email });
      // Visible scope is only presentation. Retry requires reads under the
      // retained command's exact authority, including a currently active library.
      if (retained?.uncertain && state.operationMutation === retained && sessionIdentity.isCurrent(retained.owner) &&
          retained.actorKey === sessionIdentity.actor().key &&
          (owner.role === 'super_admin' ? retained.scope === 'all' || libraries.some(item => String(item.id) === retained.scope)
            : retained.scope === String(owner.organizationId))) {
        if (retained.scope !== requestedScope) {
          const exactQuery = operationsQuery(retained.scope);
          await Promise.all([
            request(`/api/asap/staff/workflow/queues${exactQuery}`, { signal: load.signal }),
            request(`/api/asap/staff/email-operations${exactQuery}`, { signal: load.signal })
          ]);
        }
        if (!load.isCurrent() || !sessionIdentity.isCurrent(owner) || state.operationMutation !== retained) return false;
        retained.reviewEvidence = Object.freeze({ operation: retained, owner, scope: retained.scope, actorKey: retained.actorKey });
        retained.reviewed = true;
      }
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
    if (disposed || !active || !['admin', 'super_admin'].includes(sessionIdentity.preferences()?.role)) return;
    if (retry ? retry !== state.operationMutation || !canRetry(retry) : state.operationMutation) return;
    const owner = sessionIdentity.preferences();
    const operation = retry || captureOperation({ path, message, scope: state.operationsScope,
      operationId: window.crypto.randomUUID(), storageKey: operationStorageKey(), actorKey: sessionIdentity.actor().key,
      body });
    if (!supportedOperation(operation)) return;
    path = operation.path;
    message = operation.message;
    operation.uncertain = false;
    clearReview(operation);
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
      if (!hasConfirmedResult(result, operation)) throw unconfirmedResponseError();
      committedMessage = result.data?.replayed ? `${message} was already recorded as ${result.data.status}. Review email operation ${result.data.id}.`
        : result.code === 'suppressed' ? `${message} was suppressed; no email was sent.`
        : result.manualRunId && !path.endsWith('/retry') ? `${message} Run ${result.manualRunId} queued.` : `${message} queued.`;
      operation.outcome = 'committed';
      onReceipt(`${committedMessage} Sign in again to review workflow operations.`, owner, operation);
      clearOperation(operation);
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
      const uncertain = error.outcomeUnknown === true || !error.status || error.status === 408 || error.status >= 500 || isAbortError(error);
      if (uncertain) {
        operation.uncertain = true;
        // The record saved before dispatch already requires review on restore.
        // A late uncertain outcome must not overwrite a replacement record.
        operation.outcome = 'uncertain';
        onReceipt(`${message} outcome is unconfirmed. Review Operations before retrying. Sign in again to check the authoritative result.`, owner, operation);
      } else {
        clearOperation(operation);
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
    retireCatalog() {
      if (disposed) return;
      reads.begin('operations').abort(); projection = null;
      clearReview(state.operationMutation);
      dom.refreshOperations.disabled = false;
      setLibraries([]);
      dom.queueProgressTable.replaceChildren(element('p', { text: 'Queue progress unavailable. Refresh Operations to review current work.' }));
      dom.emailOperationsTable.replaceChildren(element('p', { text: 'Email operations unavailable. Refresh Operations to review current work.' }));
      updateOperationControls();
    },
    activate() { active = true; },
    deactivate() { active = false; projection = null; reads.begin('operations').abort(); },
    invalidate() { projection = null; reads.begin('operations').abort(); },
    refresh: loadOperations, run: runOperation,
    inspectDeparture: () => ({ owner: active ? state.operationsScope : null, blocked: disposed, dirty: false }),
    signedOut() { active = false; setStaff(null); },
    dispose() { disposed = true; active = false; events.abort(); reads.begin('operations').abort(); }
  };
}
