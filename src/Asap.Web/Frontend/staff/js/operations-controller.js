import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { element, commandButton, text, dateTime } from './ui.js';
import { unconfirmedResponseError } from './mutation-outcome.js';
import { actorKey } from './session-identity.js';
import { byDisplayLabel } from './display-order.js';
import { createEmailTestController, emailTestStatusMessage, emailRowVersionBytes } from './email-test.js';
import { createAdministrationAudit } from './administration-audit.js';
import { createEmailHistory } from './email-history.js';

export function createOperationsController({ root, sessionIdentity, announce, onScopeChange,
  onReceipt, clearReceipt, onRouteChange = () => {}, request = authorizedJson }) {
  const state = { operationsScope: 'all', emailTestScope: '1', readinessScope: 'all', section: 'workflow', operationMutation: null };
  const reads = createLatestLoad();
  const events = new window.AbortController();
  let active = false;
  let disposed = false;
  let initialActionContext = true;
  let libraries = [], staffOwner = null, emailReconciliation = null, testSnapshot = null;
  const dom = {
    operationsScopeField: root.querySelector('#operations-scope-field'),
    operationsScope: root.querySelector('#operations-scope'),
    runWorkflowNow: root.querySelector('#run-workflow-now'),
    runWeeklyNow: root.querySelector('#run-weekly-now'),
    sendTestEmail: root.querySelector('#send-test-email'),
    refreshOperations: root.querySelector('#refresh-operations'),
    queueProgressTable: root.querySelector('#queue-progress-table'),
    emailOperationsTable: root.querySelector('#email-operations-table'),
    outcome: root.querySelector('#operations-outcome'),
    emailTestScope: root.querySelector('#email-test-scope'),
    emailTestScopeField: root.querySelector('#email-test-scope-field'),
  };
  const audit = createAdministrationAudit({ root, sessionIdentity, request, onRouteChange: options => onRouteChange(currentRoute(), options) });
  const emailHistory = createEmailHistory({ root, sessionIdentity, request, selectSnapshot: currentEmailSnapshot,
    onRetry: retryEmail, isVisible: () => state.section === 'email',
    onRouteChange: options => routeChanged(options) });
  const sectionTabs = [...root.querySelectorAll('[data-operations-section]')];
  function currentRoute() { return { section: state.section, emailScope: emailHistory.route().scope,
    email: emailHistory.route(), testScope: state.emailTestScope, runScope: state.operationsScope, audit: audit.route() }; }
  function routeChanged(options) { onRouteChange(currentRoute(), options); }
  function acceptReadinessScope(scope) {
    if (state.readinessScope === scope) return;
    state.readinessScope = scope;
    onScopeChange();
  }
  function selectSection(section, { focus = false, write = true } = {}) {
    const selected = ['workflow', 'email', 'audit'].includes(section) ? section : 'workflow';
    if (state.section === 'audit' && selected !== 'audit') audit.deactivate();
    state.section = selected;
    for (const tab of sectionTabs) {
      const chosen = tab.dataset.operationsSection === selected;
      tab.setAttribute('aria-selected', String(chosen)); tab.tabIndex = chosen ? 0 : -1;
      root.querySelector(`#${tab.getAttribute('aria-controls')}`).hidden = !chosen;
      if (chosen && focus) tab.focus();
    }
    if (selected === 'audit' && active) audit.activate();
    if (write) routeChanged();
  }
  const emailTest = createEmailTestController({ root: root.querySelector('#operations-email-test'), request,
    onSubmitted: () => loadOperations({ silent: true }),
    focusRecovery: () => (dom.refreshOperations.disabled ? dom.emailOperationsTable : dom.refreshOperations).focus(),
    context: () => ({ ownerKey: sessionIdentity.actor()?.key, version: sessionIdentity.preferences()?.version,
      organizationId: Number(state.emailTestScope),
      active: active && !disposed, ready: Boolean(sessionIdentity.actor()), dirty: false,
      reconciling: Boolean(emailReconciliation && sessionIdentity.isCurrent(emailReconciliation.owner) &&
        emailReconciliation.scope === state.emailTestScope && !emailReconciliation.signal.aborted),
      blocked: Boolean(state.operationMutation) }) });

  function setLibraries(organizations) {
      if (disposed) return;
      libraries = Array.isArray(organizations)
        ? byDisplayLabel(organizations.filter(item => Number(item?.id) > 1 && typeof item.name === 'string'))
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
      populateTestScopes();
      if (changed) { clearOperations(); acceptReadinessScope(state.operationsScope); }
    }

  function populateTestScopes() {
    const staff = sessionIdentity.preferences();
    dom.emailTestScope.replaceChildren(...(staff?.role === 'super_admin'
      ? [element('option', { value: '1', text: 'System' })]
      : [element('option', { value: String(staff?.organizationId || ''), text: 'My library' })]));
    for (const library of libraries.filter(item => staff?.role === 'super_admin')) dom.emailTestScope.append(element('option', { value: library.id, text: library.name }));
    if (![...dom.emailTestScope.options].some(item => item.value === state.emailTestScope)) {
      dom.emailTestScope.append(element('option', { value: state.emailTestScope, text: `Selected library ${state.emailTestScope}` }));
    }
    dom.emailTestScope.value = state.emailTestScope;
  }

  function setStaff(staff) {
    if (disposed) return;
    if (staff && sessionIdentity.sameSession(staffOwner, staff) && sessionIdentity.isCurrent(staff)) {
      staffOwner = staff; updateOperationControls(); return;
    }
    staffOwner = staff;
    libraries = [];
    reads.begin('operations').abort();
    clearOperations();
    state.operationMutation = null;
    state.operationsScope = staff?.role === 'super_admin' ? 'all' : String(staff?.organizationId || 'all');
    state.emailTestScope = String(staff?.role === 'super_admin' ? 1 : staff?.organizationId || 1);
    state.readinessScope = state.operationsScope; initialActionContext = true;
    testSnapshot = null; emailReconciliation = null;
    audit.setStaff(staff);
    emailHistory.setStaff(staff);
    dom.emailTestScopeField.hidden = staff?.role !== 'super_admin';
    populateTestScopes();
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

  function clearOperations() {
    dom.queueProgressTable.replaceChildren(element('p', { text: 'Queue progress has not been loaded for this context.' }));
    emailHistory.invalidate();
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

  function isCanonicalPositiveInt64Id(value) {
    return typeof value === 'string' && /^[1-9]\d*$/.test(value) &&
      (value.length < 19 || value.length === 19 && value <= '9223372036854775807');
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
        !isCanonicalPositiveInt64Id(data.id) ||
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
    for (const control of [dom.runWorkflowNow, dom.runWeeklyNow,
      dom.operationsScope, dom.emailTestScope]) control.disabled = locked;
    emailHistory.setBlocked(locked);
    emailTest.sync();
    const notice = dom.outcome;
    notice.hidden = !operation?.uncertain;
    notice.replaceChildren();
    if (!operation?.uncertain) return;
    notice.append(element('p', { text: `${operation.message} outcome is unconfirmed. Refresh Operations to review current work before retrying this same operation. A new operation remains blocked.` }));
    if (canRetry(operation)) {
      notice.append(operation.path === '/api/asap/staff/email-operations/test'
        ? commandButton('Review test email status', 'refresh', () => void reviewLegacyEmailTest(operation))
        : commandButton('Retry same operation', 'refresh', () => runOperation(operation.path, operation.message, operation)));
    }
  }

  async function reviewLegacyEmailTest(operation) {
    if (!active || !canRetry(operation) || state.operationMutation !== operation) return false;
    const scope = operation.scope === 'all' ? 1 : Number(operation.scope);
    try {
      const result = await request(`/api/asap/staff/email-operations/test?organizationId=${scope}&operationId=${encodeURIComponent(operation.operationId)}`);
      if (!active || state.operationMutation !== operation || !sessionIdentity.isCurrent(operation.owner) ||
          result?.code !== 'ok' || !isCanonicalPositiveInt64Id(result.data?.id) || result.data.organizationId !== scope ||
          !['pending', 'sending', 'sent', 'suppressed', 'failed'].includes(result.data.status)) return false;
      if (!emailTest.adopt(scope, operation.operationId, result.data)) return false;
      clearOperation(operation); state.operationMutation = null; clearReceipt(operation);
      updateOperationControls(); announce(emailTestStatusMessage(result.data));
      return true;
    } catch (error) {
      if (active && sessionIdentity.isCurrent(operation.owner) && state.operationMutation === operation && !isAbortError(error)) {
        announce('The original test outcome is still unconfirmed. Refresh its status before requesting another test.', 'warning');
      }
      return false;
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
          text: row.cycleMaxId === '0' ? 'Empty' : row.cycleMaxId === null
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
  }

  function currentEmailSnapshot(listed) {
    const exact = testSnapshot?.();
    if (!exact) {
      const remembered = emailTest.currentReference();
      return remembered?.id === listed.id && remembered.organizationId === listed.organizationId &&
        (remembered.unconfirmed || emailReconciliation && !emailReconciliation.signal.aborted)
        ? { ...listed, canRetry: false } : listed;
    }
    if (exact.id !== listed.id || exact.organizationId !== listed.organizationId) return listed;
    const selected = testSnapshot(listed);
    if (!selected) return listed;
    const exactVersion = emailRowVersionBytes(selected.version), listedVersion = emailRowVersionBytes(listed.version);
    // Choose a complete snapshot. Fields from an older list must never be
    // merged into newer exact evidence, especially its separate retry proof.
    return exactVersion !== null && (listedVersion === null || exactVersion > listedVersion)
      ? { ...selected, canRetry: false } : listed;
  }

  async function loadOperations(options = {}) {
    if (disposed || !sessionIdentity.preferences() || (sessionIdentity.preferences().role !== 'admin' && sessionIdentity.preferences().role !== 'super_admin')) return false;
    const owner = sessionIdentity.preferences();
    const load = reads.begin('operations');
    const requestedScope = state.operationsScope;
    const requestedTestScope = state.emailTestScope;
    const requestedHistory = JSON.stringify(emailHistory.route());
    const reconciliation = { owner, scope: requestedTestScope, signal: load.signal };
    emailReconciliation = reconciliation;
    emailTest.sync();
    const currentLoad = () => load.isCurrent() && sessionIdentity.isCurrent(owner) &&
      requestedScope === state.operationsScope && requestedTestScope === state.emailTestScope;
    const retained = state.operationMutation;
    if (retained?.uncertain) { clearReview(retained); updateOperationControls(); }
    // Only the control being disabled owns this handoff. Background reads must
    // leave unrelated focus alone; a current keyboard refresh returns here only
    // if the user has not moved away while the asynchronous work completes.
    const restoreRefresh = document.activeElement === dom.refreshOperations;
    const focusAnchor = root.querySelector('#operations-title');
    let focusMoved = false;
    const moved = () => { focusMoved = true; };
    if (restoreRefresh) { focusAnchor.focus(); document.addEventListener('focusin', moved); }
    dom.refreshOperations.disabled = true;
    const clearLoading = !options.silent ? announce('Loading operations...') : null;
    try {
      const query = operationsQuery(requestedScope);
      const testRead = options.reconcileTest === false ? Promise.resolve() : emailTest.refresh({ signal: load.signal }).then(snapshot => {
        if (currentLoad()) testSnapshot = snapshot || null;
        return snapshot;
      });
      const emailRead = emailHistory.refresh({ waitFor: testRead });
      // Consume available email evidence before unrelated queue/catalog/audit
      // waits, including failures. Send remains guarded until this pair settles.
      const emailPair = Promise.allSettled([emailRead, testRead]).then(([listed, test]) => {
        if (currentLoad()) {
          emailHistory.render();
          if (emailReconciliation === reconciliation) { emailReconciliation = null; emailTest.sync(); }
        }
        return { listed, test };
      });
      const [tables, pair] = await Promise.allSettled([
        Promise.all([
          request(`/api/asap/staff/workflow/queues${query}`, { signal: load.signal }),
          sessionIdentity.preferences().role === 'super_admin'
            ? request('/api/asap/staff/organizations', { signal: load.signal })
            : Promise.resolve(null)
        ]),
        emailPair,
        state.section === 'audit' ? audit.refresh() : Promise.resolve()
      ]);
      if (!currentLoad()) return false;
      if (tables.status === 'rejected') throw tables.reason;
      if (pair.status === 'rejected') throw pair.reason;
      const { listed, test } = pair.value;
      if (test.status === 'rejected') throw test.reason;
      if (listed.status === 'fulfilled' && !listed.value && requestedHistory === JSON.stringify(emailHistory.route()) && emailHistory.failure()) throw emailHistory.failure();
      const [queue, organizationResult] = tables.value;
      const organizations = organizationResult?.data ?? organizationResult;
      if (sessionIdentity.preferences().role === 'super_admin' && Array.isArray(organizations)) {
        const activeLibraries = organizations
          .filter(item => Number(item.id) > 1 && item.organizationCodeId === 2 && item.isActive === true)
          .map(item => ({ id: item.id, name: item.name || item.displayName || String(item.id) }));
        setLibraries(activeLibraries);
        if (requestedScope !== state.operationsScope) return loadOperations(options);
      }
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
        if (retained.path === '/api/asap/staff/email-operations/test') await reviewLegacyEmailTest(retained);
      }
      if (!currentLoad()) return false;
      // Resolve the operation-bound latest observation after every asynchronous
      // read, including audit and retained-command review. A newer poll wins.
      renderOperations({ queue }); emailHistory.render();
      updateOperationControls();
      return listed.status === 'fulfilled' && (listed.value || requestedHistory !== JSON.stringify(emailHistory.route()));
    } catch (error) {
      if (currentLoad() &&
          !options.silent && !isAbortError(error) && error.status !== 401) {
        announce(error.message || 'Operations could not be loaded.', 'error');
      }
      return false;
    } finally {
      if (emailReconciliation === reconciliation && load.isCurrent()) { emailReconciliation = null; emailTest.sync(); }
      document.removeEventListener('focusin', moved);
      if (typeof clearLoading === 'function') clearLoading();
      if (load.isCurrent()) dom.refreshOperations.disabled = false;
      if (restoreRefresh && !focusMoved && load.isCurrent() && active && sessionIdentity.isCurrent(owner) &&
          document.activeElement === focusAnchor) dom.refreshOperations.focus();
      reads.finish('operations', load.token);
    }
  }

  async function runOperation(path, message, retry = null, body = undefined, scope = state.operationsScope) {
    if (disposed || !active || !['admin', 'super_admin'].includes(sessionIdentity.preferences()?.role)) return;
    if (retry ? retry !== state.operationMutation || !canRetry(retry) : state.operationMutation) return;
    if (retry && path === '/api/asap/staff/email-operations/test') return reviewLegacyEmailTest(retry);
    const owner = sessionIdentity.preferences();
    const operation = retry || captureOperation({ path, message, scope,
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
      const visibleScope = path.endsWith('/retry') ? emailHistory.route().scope === 'system' ? 'all' : emailHistory.route().scope : state.operationsScope;
      if (!disposed && sessionIdentity.isCurrent(owner) && operation.scope === visibleScope && active) {
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
      `Email retry ${row.id}`, null, { version: row.version }, emailHistory.route().scope === 'system' ? 'all' : emailHistory.route().scope);
  }
  const listen = (node, name, handler) => node.addEventListener(name, handler, { signal: events.signal });
  listen(dom.operationsScope, 'change', () => {
    if (state.operationMutation) { dom.operationsScope.value = state.operationsScope; return; }
    state.operationsScope = dom.operationsScope.value;
    clearOperations();
    acceptReadinessScope(state.operationsScope); routeChanged({ replace: true });
    void loadOperations();
  });
  listen(dom.emailTestScope, 'change', () => {
    if (state.operationMutation || !emailTest.retainForDeparture()) { dom.emailTestScope.value = state.emailTestScope; return; }
    state.emailTestScope = dom.emailTestScope.value;
    acceptReadinessScope(state.emailTestScope);
    testSnapshot = null; emailTest.sync(); routeChanged({ replace: true }); void loadOperations();
  });
  for (const [index, tab] of sectionTabs.entries()) {
    listen(tab, 'click', () => { selectSection(tab.dataset.operationsSection); if (state.section === 'audit') void audit.refresh(); });
    listen(tab, 'keydown', event => {
      let next = null;
      if (event.key === 'ArrowRight') next = (index + 1) % sectionTabs.length;
      if (event.key === 'ArrowLeft') next = (index + sectionTabs.length - 1) % sectionTabs.length;
      if (event.key === 'Home') next = 0;
      if (event.key === 'End') next = sectionTabs.length - 1;
      if (next === null) return;
      event.preventDefault(); selectSection(sectionTabs[next].dataset.operationsSection, { focus: true });
      if (state.section === 'audit') void audit.refresh();
    });
  }
  listen(dom.runWorkflowNow, 'click', () => runOperation('/api/asap/staff/workflow/run-now', 'Workflow run'));
  listen(dom.runWeeklyNow, 'click', event => {
    // Native click counts identify one double-click gesture even when the first
    // request completes before its second click. A later single click is new work.
    if (event.detail > 1) return;
    void runOperation('/api/asap/staff/workflow/weekly-summary/run-now?force=true', 'Weekly summary');
  });
  listen(dom.runWeeklyNow, 'keydown', event => {
    // Enter can synthesize another click on every repeated keydown after a fast
    // acknowledgement re-enables the button. One held key is still one gesture.
    if (event.repeat && (event.key === 'Enter' || event.key === ' ')) event.preventDefault();
  });
  listen(dom.refreshOperations, 'click', () => loadOperations());
  return {
    setStaff, setLibraries, currentRoute, currentScope: () => state.readinessScope,
    async openEmailReference(input) {
      const operationReference = input?.operationReference, operationsScope = input?.operationsScope;
      const owner = sessionIdentity.preferences();
      if (disposed || !active || !owner || !sessionIdentity.isCurrent(owner) ||
          !['admin', 'super_admin'].includes(owner.role) || !isCanonicalPositiveInt64Id(operationReference) ||
          typeof operationsScope !== 'string' || !/^[1-9]\d{0,9}$/.test(operationsScope) ||
          Number(operationsScope) <= 1 || Number(operationsScope) > 2147483647 ||
          owner.role !== 'super_admin' && operationsScope !== String(owner.organizationId)) return false;
      // A request reference owns only history presentation. Saved test scope,
      // exact recovery and provider readiness retain their existing owners.
      emailHistory.setRoute({ scope: operationsScope, reference: operationReference });
      selectSection('email', { write: false }); routeChanged({ replace: true });
      const captured = JSON.stringify(emailHistory.route());
      // Focus the accepted destination now. Completion cannot reclaim focus
      // after the user moves elsewhere during a delayed history read.
      dom.emailOperationsTable.focus();
      const loaded = await emailHistory.refresh();
      return loaded && !disposed && active && state.section === 'email' && sessionIdentity.isCurrent(owner) &&
        captured === JSON.stringify(emailHistory.route());
    },
    async openEmailScope(organizationId) {
      const owner = sessionIdentity.preferences();
      if (!active || !owner || !Number.isInteger(organizationId) || organizationId < 1 || organizationId > 2147483647) return false;
      const scope = organizationId === 1 ? 'system' : String(organizationId);
      if (owner.role !== 'super_admin' && organizationId !== owner.organizationId) return false;
      // Accept the requested presentation context before reads. An unrelated
      // queue/catalog outage must not strand the original Settings test on the
      // Workflow panel. Each saved-context, exact lookup and command still has
      // its own server-side scope/active-identity authorization.
      state.emailTestScope = String(organizationId); testSnapshot = null; populateTestScopes();
      emailHistory.setRoute({ scope }); selectSection('email', { write: false });
      routeChanged({ replace: true }); emailTest.sync(); acceptReadinessScope(state.emailTestScope);
      const loaded = await loadOperations();
      if (!active || !sessionIdentity.isCurrent(owner) || emailHistory.route().scope !== scope || state.section !== 'email') return false;
      dom.emailOperationsTable.focus();
      return loaded;
    },
    async openStaffAuditScope(organizationId) {
      if (!active || !sessionIdentity.actor()) return false;
      const owner = sessionIdentity.preferences();
      const scope = owner.role === 'super_admin' ? organizationId === null ? 'all' : String(organizationId) : String(owner.organizationId);
      if (owner.role !== 'super_admin' && organizationId !== null && Number(organizationId) !== owner.organizationId) return false;
      audit.setRoute({ scope, category: 'staff' }); selectSection('audit', { write: false }); routeChanged({ replace: true });
      const loaded = await audit.refresh();
      if (loaded && active && state.section === 'audit' && sessionIdentity.isCurrent(owner)) root.querySelector('#audit-search').focus();
      return loaded;
    },
    retireCatalog() {
      if (disposed) return;
      reads.begin('operations').abort();
      audit.invalidate(); emailHistory.invalidate(); emailTest.invalidate(); testSnapshot = null;
      clearReview(state.operationMutation);
      dom.refreshOperations.disabled = false;
      setLibraries([]);
      dom.queueProgressTable.replaceChildren(element('p', { text: 'Queue progress unavailable. Refresh Operations to review current work.' }));
      emailHistory.invalidate('Email operations unavailable. Refresh Operations to review current work.');
      updateOperationControls();
    },
    activate(route) {
      active = true;
      emailHistory.activate();
      if (route) {
        const staff = sessionIdentity.preferences();
        const validScope = value => value === 'all' || value === 'system' || /^[1-9]\d{0,9}$/.test(value || '');
        if (staff?.role === 'super_admin') {
          if (validScope(route.runScope) && route.runScope !== 'system') state.operationsScope = route.runScope;
          if (/^[1-9]\d{0,9}$/.test(route.testScope || '') && Number(route.testScope) <= 2147483647) state.emailTestScope = route.testScope;
          populateTestScopes();
        }
        emailHistory.setRoute(route.email || { scope: route.emailScope });
        audit.setRoute(route.audit); selectSection(route.section, { write: false });
      } else selectSection(state.section, { write: false });
      if (initialActionContext) {
        initialActionContext = false;
        // A restored action context initializes health once. Browsing filters
        // and later tab/route presentation never select a provider context.
        acceptReadinessScope(route?.section === 'email' && route.testScope ? state.emailTestScope : state.operationsScope);
      }
      emailTest.sync();
    },
    deactivate() { active = false; audit.deactivate(); emailHistory.deactivate(); emailTest.invalidate(); testSnapshot = null; clearOperations(); reads.begin('operations').abort(); },
    invalidate() { audit.invalidate(); emailTest.invalidate(); testSnapshot = null; clearOperations(); reads.begin('operations').abort(); },
    refresh: loadOperations, run: runOperation,
    inspectDeparture: () => ({ owner: active ? state.operationsScope : null, blocked: disposed, dirty: false }),
    signedOut() { active = false; setStaff(null); },
    dispose() { disposed = true; active = false; audit.dispose(); emailHistory.dispose(); emailTest.dispose(); events.abort(); reads.begin('operations').abort(); }
  };
}
