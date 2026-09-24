import { openEdit } from './modals.js';
import { isSuperAdminStaff, isAdminStaff, getSettingsSectionFromHash, activateSettingsSection, requestedRequestIdFromUrl, requestedRequestTypeFromUrl, replaceResolvedRequestId } from './api.js';
import { authorizedJson, isAbortError } from './http.js';
import { closeOpenDialogs } from './dialogs.js';
import { showSettingsAccessDenied, hideSettingsAccessDenied, refreshSettingsView } from './settings.js';
import { refreshAnalyticsView } from './analytics.js';
import { normalizeStatus } from './grid-policy.mjs';
import { escapeAttr } from './grid-utils.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { findWorkflowRow, requestIdentity, sameRequestIdentity } from './request-identity.mjs';

const tabLoads = createLatestLoad();

export async function loadTab(status, ctx, options = {}) {
  const guard = tabLoads.begin('tab');
  if (status !== ctx.currentStatus) {
    ctx.setGridSearchKeyword('');
  }
  syncStatusTab(status, ctx);
  renderTabDescription(status, ctx);
  clearJobMessage();
  updateAdminActions(status, ctx);

  if (status === 'settings') {
    loadSettingsTab(ctx);
    tabLoads.finish('tab', guard.token);
    return;
  }

  prepareGridView(ctx);

  if (status === 'analytics') {
    hideWorkflowScopeControl(ctx);
    ctx.hideTagFilter();
    ctx.hideClaimFilter();
    if (ctx.staffGridFilterBar) ctx.staffGridFilterBar.classList.add('hidden');
    await refreshAnalyticsView(ctx.gridContainer);
    if (guard.isCurrent()) {
      await announceTabLoaded(status, ctx, options);
    }
    tabLoads.finish('tab', guard.token);
    return;
  }

  try {
    if (status === 'additional_copies') {
      const openResult = await safeFetchAdditionalCopies('open', guard.signal, ctx);
      if (!guard.isCurrent()) return;
      const openRecords = Array.isArray(openResult.items) ? openResult.items : [];

      const closedResult = await safeFetchAdditionalCopies('closed', guard.signal, ctx);
      if (!guard.isCurrent()) return;
      const closedRecords = Array.isArray(closedResult.items) ? closedResult.items : [];

      const titleResult = await safeFetchTitleRequests(guard.signal, ctx);
      if (!guard.isCurrent()) return;
      const titleRecords = Array.isArray(titleResult.items) ? titleResult.items : [];

      updateWorkflowScopeControl(openResult, ctx);
      ctx.setAllSuggestions([...titleRecords, ...openRecords, ...closedRecords]);
      updateTabCounts(titleRecords, openRecords.length, closedRecords.length, ctx);
      renderAdditionalCopiesGrid(openRecords, ctx);
      return;
    }

    const scopedResult = await fetchTitleRequests(guard.signal, ctx);
    if (!guard.isCurrent()) return;
    let records = Array.isArray(scopedResult.items) ? scopedResult.items : [];
    updateWorkflowScopeControl(scopedResult, ctx);

    const openAdditionalResult = await safeFetchAdditionalCopies('open', guard.signal, ctx);
    if (!guard.isCurrent()) return;
    const openAdditionalRecords = Array.isArray(openAdditionalResult.items) ? openAdditionalResult.items : [];
    const openAdditionalCount = openAdditionalRecords.length;

    const closedAdditionalResult = await safeFetchAdditionalCopies('closed', guard.signal, ctx);
    if (!guard.isCurrent()) return;
    const closedAdditionalRecords = Array.isArray(closedAdditionalResult.items) ? closedAdditionalResult.items : [];
    const closedAdditionalCount = closedAdditionalRecords.length;

    ctx.setAllSuggestions([...records, ...openAdditionalRecords, ...closedAdditionalRecords]);

    if (status === 'closed') {
      records = records.concat(closedAdditionalRecords);
    }

    updateTabCounts(records, openAdditionalCount, closedAdditionalCount, ctx);

    renderStatusGrid(status, records, ctx);
  } catch (err) {
    if (!isAbortError(err) && guard.isCurrent()) {
      handleLoadTabError(err);
    }
  } finally {
    if (guard.isCurrent()) {
      await announceTabLoaded(status, ctx, options);
    }
    tabLoads.finish('tab', guard.token);
  }
}

export function syncStatusTab(status, ctx) {
  ctx.setCurrentStatus(status);
  document.querySelectorAll('#status-tabs .nav-link').forEach(link => {
    const isActive = link.getAttribute('data-status') === status;
    link.classList.toggle('active', isActive);
    if (link.hasAttribute('role')) {
      link.setAttribute('aria-selected', isActive ? 'true' : 'false');
    }
  });
}

export function renderTabDescription(status, ctx) {
  const tabDesc = document.getElementById('tab-desc');
  tabDesc.replaceChildren();
  tabDesc.textContent = ctx.descriptions[status] || '';

  const libraryName = ctx.workflowSettings.isOverride ? (ctx.staffSession.staff?.libraryOrgName || 'Library') : 'System Defaults';

  const addSuffix = (strongText, plainText) => {
    tabDesc.appendChild(document.createTextNode(' '));
    const strong = document.createElement('strong');
    strong.textContent = strongText;
    tabDesc.appendChild(strong);
    tabDesc.appendChild(document.createTextNode(plainText));

    const link = document.createElement('a');
    const url = new URL(window.location.href);
    url.searchParams.set('stage', 'settings');
    url.hash = 'settings-workflow';
    link.href = url.pathname + url.search + url.hash;
    link.textContent = 'Settings';
    tabDesc.appendChild(link);

    tabDesc.appendChild(document.createTextNode(')'));
  };

  if (status === 'suggestion') {
    ctx.workflowSettings.outstandingTimeoutDays = parseInt(ctx.workflowSettings.outstandingTimeoutDays || '30', 10) || 30;
    if (ctx.workflowSettings.outstandingTimeoutEnabled) {
      addSuffix(`${libraryName} auto-reject enabled (${ctx.workflowSettings.outstandingTimeoutDays} days):`, ' Stalled suggestions will be auto-rejected. (');
    } else {
      addSuffix(`${libraryName} auto-reject disabled:`, ' (');
    }
  }

  if (status === 'outstanding_purchase') {
    tabDesc.textContent = 'Pending purchase contains approved suggestions that are waiting to appear in Polaris. Staff can add a BIB ID to move a suggestion to the Pending hold phase.';
    if (ctx.workflowSettings.autoPromote) {
      addSuffix(`${libraryName} auto-promoter enabled:`, ' ASAP will check Polaris automatically. (');
    } else {
      addSuffix(`${libraryName} auto-promoter disabled:`, ' (');
    }
  }

  if (status === 'hold_placed') {
    ctx.workflowSettings.holdPickupTimeoutDays = parseInt(ctx.workflowSettings.holdPickupTimeoutDays || '14', 10) || 14;
    if (ctx.workflowSettings.holdPickupTimeoutEnabled) {
      addSuffix(`${libraryName} auto-close enabled (${ctx.workflowSettings.holdPickupTimeoutDays} days):`, ' Unpicked-up holds will auto-close. (');
    } else {
      addSuffix(`${libraryName} auto-close disabled:`, ' (');
    }
  }

  if (status === 'pending_hold') {
    ctx.workflowSettings.pendingHoldTimeoutDays = parseInt(ctx.workflowSettings.pendingHoldTimeoutDays || '14', 10) || 14;
    if (ctx.workflowSettings.pendingHoldTimeoutEnabled) {
      addSuffix(`${libraryName} auto-close enabled (${ctx.workflowSettings.pendingHoldTimeoutDays} days):`, ' Unprocessed items will auto-close. (');
    } else {
      addSuffix(`${libraryName} auto-close disabled:`, ' (');
    }
  }

  if (status === 'additional_copies') {
    ctx.workflowSettings.additionalCopyTimeoutDays = parseInt(ctx.workflowSettings.additionalCopyTimeoutDays || '14', 10) || 14;
    if (ctx.workflowSettings.additionalCopyTimeoutEnabled) {
      addSuffix(`${libraryName} auto-close enabled (${ctx.workflowSettings.additionalCopyTimeoutDays} days):`, ' Stale tasks will auto-close. (');
    } else {
      addSuffix(`${libraryName} auto-close disabled:`, ' (');
    }
  }
}

export function clearJobMessage() {
  const jobMsg = document.getElementById('job-msg');
  if (jobMsg) jobMsg.textContent = '';
}

export function updateAdminActions(status, ctx) {
  const adminBar = document.getElementById('admin-actions-bar');
  const workflowBtn = document.getElementById('btn-run-workflow-now');
  const scopeWrapper = document.getElementById('workflow-library-scope-label');
  const scopeSelect = document.getElementById('workflow-library-scope');

  adminBar.classList.add('hidden');
  workflowBtn.classList.add('hidden');
  const scopeReady = !isSuperAdminStaff() ||
    (scopeWrapper && !scopeWrapper.classList.contains('hidden') &&
      scopeSelect && scopeSelect.value === String(ctx.currentWorkflowOrgScopeId));
  if (isAdminStaff() && scopeReady && status !== 'settings' && status !== 'analytics') {
    adminBar.classList.remove('hidden');
    workflowBtn.classList.remove('hidden');
    if (workflowBtn.dataset.reloadMessage) {
      const msg = document.getElementById('job-msg');
      msg.textContent = workflowBtn.dataset.reloadMessage;
      msg.className = 'mb-3 font-weight-bold text-warning';
    }
  }
}

export function loadSettingsTab(ctx) {
  closeOpenDialogs();
  ctx.closeActionMenu?.();
  ctx.gridContainer.classList.add('hidden');
  if (ctx.staffGridFilterBar) ctx.staffGridFilterBar.classList.add('hidden');
  ctx.hideTagFilter();
  ctx.hideClaimFilter();
  ctx.settingsContainer.classList.remove('hidden');
  activateSettingsSection(getSettingsSectionFromHash() || ctx.currentSettingsSection, { updateHash: false });
  if (!isAdminStaff()) {
    showSettingsAccessDenied();
    return;
  }
  refreshSettingsView({ showErrors: true, preserveDraft: true });
}

export function prepareGridView(ctx) {
  ctx.gridContainer.classList.remove('hidden');
  ctx.settingsContainer.classList.add('hidden');
  if (ctx.staffGridFilterBar) ctx.staffGridFilterBar.classList.remove('hidden');
  hideSettingsAccessDenied();
  resetGrid(ctx);
}

export async function fetchTitleRequests(signal, ctx) {
  const params = new URLSearchParams();
  if (isSuperAdminStaff()) {
    params.set('scope', ctx.currentWorkflowOrgScopeId || 'all');
  }
  params.set('_', String(Date.now()));
  return authorizedJson('/api/asap/staff/title-requests?' + params.toString(), { cache: 'no-store', signal });
}

export async function fetchAdditionalCopies(status = 'open', signal, ctx) {
  const params = new URLSearchParams();
  if (isSuperAdminStaff()) {
    params.set('scope', ctx.currentWorkflowOrgScopeId || 'all');
  }
  params.set('status', status);
  params.set('_', String(Date.now()));
  return authorizedJson('/api/asap/staff/additional-copies?' + params.toString(), { cache: 'no-store', signal });
}

export async function safeFetchTitleRequests(signal, ctx) {
  try {
    return await fetchTitleRequests(signal, ctx);
  } catch (err) {
    if (isAbortError(err)) throw err;
    console.warn('Title-request count refresh failed.', err);
    return { items: [] };
  }
}

export async function safeFetchAdditionalCopies(status = 'open', signal, ctx) {
  try {
    return await fetchAdditionalCopies(status, signal, ctx);
  } catch (err) {
    if (isAbortError(err)) throw err;
    console.warn('Additional-copy refresh failed.', err);
    return { items: [] };
  }
}

export function updateWorkflowScopeControl(data, ctx) {
  const wrapper = document.getElementById('workflow-library-scope-label');
  const select = document.getElementById('workflow-library-scope');
  if (!wrapper || !select) return;

  const scope = data && data.scope ? data.scope : {};
  if (!scope.superAdmin) {
    hideWorkflowScopeControl(ctx);
    return;
  }

  const libraries = Array.isArray(data.availableLibraries) ? data.availableLibraries.slice() : [];
  const selectedScopeId = scope.mode === 'library' && scope.libraryOrgId ? scope.libraryOrgId : 'all';
  ctx.setCurrentWorkflowOrgScopeId(selectedScopeId);

  if (scope.mode === 'library' && scope.libraryOrgId && !libraries.some(library => String(library.orgId) === String(scope.libraryOrgId))) {
    libraries.push({ orgId: scope.libraryOrgId, name: scope.label || 'Current library' });
  }

  select.innerHTML = [
    `<option value="all"${selectedScopeId === 'all' ? ' selected' : ''}>All libraries</option>`,
    ...libraries.map(library => {
      const orgId = String(library.orgId || '').trim();
      const selected = selectedScopeId === orgId ? ' selected' : '';
      return `<option value="${escapeAttr(orgId)}"${selected}>${escapeAttr(library.name || orgId)} (ID ${escapeAttr(orgId)})</option>`;
    })
  ].join('');
  select.value = selectedScopeId;

  if (!select.dataset.workflowScopeBound) {
    select.addEventListener('change', () => {
      ctx.setCurrentWorkflowOrgScopeId(select.value || 'all');
      refreshCurrentStaffView(ctx);
    });
    select.dataset.workflowScopeBound = 'true';
  }

  wrapper.classList.remove('hidden');
  if (ctx.staffGridFilterBar) {
    ctx.staffGridFilterBar.classList.remove('hidden');
  }
  updateAdminActions(ctx.currentStatus, ctx);
}

export function hideWorkflowScopeControl() {
  const wrapper = document.getElementById('workflow-library-scope-label');
  const select = document.getElementById('workflow-library-scope');
  if (wrapper) wrapper.classList.add('hidden');
  if (select) select.innerHTML = '';
}

export function renderStatusGrid(status, records, ctx) {
  ctx.setCurrentSuggestions(records.filter(row => normalizeStatus(row.status) === status));
  ctx.updateTagFilter(ctx.currentSuggestions);
  ctx.updateClaimFilter();

  if (!ctx.currentSuggestions.length) {
    ctx.gridContainer.innerHTML = `<div class="alert alert-light border">${escapeAttr(ctx.emptyStateMessages[status] || 'No suggestions found.')}</div>`;
    return false;
  }

  ctx.renderCurrentGrid();
  return true;
}

export function renderAdditionalCopiesGrid(records, ctx) {
  ctx.setCurrentSuggestions(records);
  ctx.updateTagFilter(records);
  ctx.updateClaimFilter();
  if (ctx.additionalCopyStatusFilterSelect) {
    ctx.additionalCopyStatusFilterSelect.classList.add('hidden');
  }
  if (!records.length) {
    ctx.gridContainer.textContent = '';
    const empty = document.createElement('div');
    empty.className = 'alert alert-light border';
    empty.textContent = ctx.emptyStateMessages.additional_copies;
    ctx.gridContainer.appendChild(empty);
    return;
  }
  ctx.renderCurrentGrid('additional_copies');
}

export async function renderAdditionalCopiesLoadError(err, ctx) {
  ctx.hideTagFilter();
  ctx.hideClaimFilter();
  const titleResult = await safeFetchTitleRequests(undefined, ctx);
  const titleRecords = Array.isArray(titleResult.items) ? titleResult.items : [];
  updateTabCounts(titleRecords, 0, 0, ctx);
  ctx.gridContainer.replaceChildren();

  const alert = document.createElement('div');
  alert.className = 'alert alert-warning border';
  const title = document.createElement('div');
  title.className = 'font-weight-bold mb-1';
  title.textContent = 'Additional copies could not load.';
  const detail = document.createElement('div');
  detail.textContent = err && err.message ? err.message : 'The additional-copy queue endpoint returned an error.';
  alert.append(title, detail);
  ctx.gridContainer.appendChild(alert);
}

export async function announceTabLoaded(status, ctx, options = {}) {
  const announcer = document.getElementById('status-announcer');
  announcer.textContent = "Loaded " + status + " tab.";

  const firstHeader = document.getElementById('tab-desc');
  if (firstHeader && options.focusHeader !== false) firstHeader.focus();

  if (options.openLinkedRequest === false) return;

  const requestId = requestedRequestIdFromUrl();
  if (requestId) {
    const expectedType = requestedRequestTypeFromUrl(status);
    const identity = requestIdentity({ id: requestId, type: expectedType });
    let row = findWorkflowRow(identity, ctx.allSuggestions);
    if (!row) {
      try {
        const collection = expectedType === 'additional_copy' ? 'additional-copies' : 'title-requests';
        row = await authorizedJson(`/api/asap/staff/${collection}/${encodeURIComponent(requestId)}`, { cache: 'no-store' });
        if (row?.id) {
          if (!ctx.allSuggestions.some(item => sameRequestIdentity(item, row))) {
            ctx.setAllSuggestions([...ctx.allSuggestions, row]);
          }
          replaceResolvedRequestId(row.id, requestIdentity(row).type);
        }
      } catch (error) {
        if (!isAbortError(error)) {
          console.warn('The linked request could not be resolved.');
          const message = status === 'additional_copies'
            ? 'That additional-copy task is no longer available.'
            : 'That request is no longer available.';
          const jobMessage = document.getElementById('job-msg');
          if (jobMessage) {
            jobMessage.textContent = message;
            jobMessage.className = 'mb-3 font-weight-bold job-message text-warning';
          }
          announcer.textContent = message;
        }
      }
    }
    if (row) {
      openEdit(requestIdentity(row), row.status, 'Edit', '', 'Save');
    }
  }
}

export function handleLoadTabError(err) {
  console.error('Failed to load data', err);
}

export function resetGrid(ctx) {
  if (ctx.grid && typeof ctx.grid.destroy === 'function') {
    ctx.grid.destroy();
  }
  ctx.setGrid(null);
  ctx.gridContainer.innerHTML = '';
}

export function refreshCurrentStaffView(ctx, options = {}) {
  if (options.silent && (ctx.currentStatus === 'settings' || ctx.currentStatus === 'analytics')) {
    return Promise.resolve();
  }
  return loadTab(ctx.currentStatus, ctx, options.silent
    ? { openLinkedRequest: false, focusHeader: false }
    : {});
}

export function refreshStaffStatus(status, ctx) {
  return loadTab(status || ctx.currentStatus, ctx);
}

export function updateTabCounts(records, openAdditionalCount = 0, closedAdditionalCount = 0, ctx) {
  const counts = Object.fromEntries(ctx.statusStages.map(status => [status, 0]));
  records.forEach(row => {
    if (row.type !== 'title_request' && row.type !== undefined) return;

    const status = normalizeStatus(row.status);
    if (Object.prototype.hasOwnProperty.call(counts, status)) {
      counts[status] += 1;
    }
  });

  counts.additional_copies = openAdditionalCount;
  if (counts.closed !== undefined) {
    counts.closed += closedAdditionalCount;
  }

  document.querySelectorAll('#status-tabs .nav-link[data-status]').forEach(link => {
    const status = link.getAttribute('data-status');
    const count = counts[status];
    const badge = link.querySelector('.tab-count');
    if (badge && count !== undefined) {
      badge.textContent = count;
      badge.setAttribute('aria-label', count + ' records');
    }
  });
}
