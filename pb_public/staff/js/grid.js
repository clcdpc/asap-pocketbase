import { pb, gridContainer, staffGridFilterBar, tagFilterSelect, claimFilterSelect, similarRequestFilterSelect, additionalCopyStatusFilterSelect, closedTypeFilterSelect, gridSearchInput, settingsContainer, grid, formatMap, ageMap, closeReasonMap, descriptions, emptyStateMessages, statusStages, currentStatus, currentSuggestions, activeTagFilter, gridSearchKeyword, currentClaimFilter, currentSimilarRequestFilter, currentAdditionalCopyStatus, currentClosedTypeFilter, currentWorkflowOrgScopeId, allSuggestions, workflowSettings, currentSettingsSection, activeActionMenu, rowActionIdCounter, rowActionRegistry, setCurrentStatus, setCurrentSuggestions, setActiveTagFilter, setGridSearchKeyword, setCurrentClaimFilter, setCurrentWorkflowOrgScopeId, setCurrentClosedTypeFilter, setActiveActionMenu, setGrid, setAllSuggestions, incrementRowActionIdCounter } from './state.js';
import { openEdit, openPolarisSearch, polarisSearchValueForRow, renderPolarisSearchButtonMarkup, confirmAdditionalCopyAction } from './modals.js';
import { openNewSuggestionForPatron } from './patron.js';
import { undoRow, deleteClosedRequest, closeDuplicateRequest } from './actions.js';
import { leapBibUrl, isSuperAdminStaff, isAdminStaff, getSettingsSectionFromHash, activateSettingsSection, requestedRequestIdFromUrl } from './api.js';
import { authorizedJson } from './http.js';
import { showToast, showAlert, showConfirm, closeOpenDialogs } from './dialogs.js';
import { showSettingsAccessDenied, hideSettingsAccessDenied, loadSettings } from './settings.js';
import { loadAnalytics } from './analytics.js';
import { renderNoteActivity } from './note-activity.js';
import { normalizeLabel, flagDisplayMap, getFlagDisplay, getIsbnCheckLabel, effectiveWorkflowFlagsForRow, getFilterableLabelsForRow, normalizeWorkflowTagLabel, cleanWorkflowTags, tagCountsForRecords, normalizeStatus } from './grid-policy.mjs';
import { buildRowActions } from './grid-row-actions.mjs';

export { normalizeLabel, flagDisplayMap, getFlagDisplay, getIsbnCheckLabel, effectiveWorkflowFlagsForRow, getFilterableLabelsForRow, normalizeWorkflowTagLabel, cleanWorkflowTags, tagCountsForRecords, normalizeStatus } from './grid-policy.mjs';

export async function loadTab(status) {
  if (status !== currentStatus) {
    setGridSearchKeyword('');
  }
  syncStatusTab(status);
  renderTabDescription(status);
  clearJobMessage();
  updateAdminActions(status);

  if (status === 'settings') {
    loadSettingsTab();
    return;
  }

  prepareGridView();

  if (status === 'analytics') {
    hideWorkflowScopeControl();
    hideTagFilter();
    hideClaimFilter();
    if (staffGridFilterBar) staffGridFilterBar.classList.add('hidden');
    await loadAnalytics(gridContainer);
    announceTabLoaded(status);
    return;
  }

  try {
    if (status === 'additional_copies') {
      const openResult = await safeFetchAdditionalCopies('open');
      const openRecords = Array.isArray(openResult.items) ? openResult.items : [];
      
      const closedResult = await safeFetchAdditionalCopies('closed');
      const closedRecords = Array.isArray(closedResult.items) ? closedResult.items : [];
      
      const titleResult = await safeFetchTitleRequests();
      const titleRecords = Array.isArray(titleResult.items) ? titleResult.items : [];
      
      updateWorkflowScopeControl(openResult);
      
      // allSuggestions should include EVERYTHING for duplicate detection to work
      setAllSuggestions([...titleRecords, ...openRecords, ...closedRecords]);
      
      updateTabCounts(titleRecords, openRecords.length, closedRecords.length);
      
      renderAdditionalCopiesGrid(openRecords);
      announceTabLoaded(status);
      return;
    }

    const scopedResult = await fetchTitleRequests();
    let records = Array.isArray(scopedResult.items) ? scopedResult.items : [];
    updateWorkflowScopeControl(scopedResult);

    const openAdditionalResult = await safeFetchAdditionalCopies('open');
    const openAdditionalRecords = Array.isArray(openAdditionalResult.items) ? openAdditionalResult.items : [];
    const openAdditionalCount = openAdditionalRecords.length;

    const closedAdditionalResult = await safeFetchAdditionalCopies('closed');
    const closedAdditionalRecords = Array.isArray(closedAdditionalResult.items) ? closedAdditionalResult.items : [];
    const closedAdditionalCount = closedAdditionalRecords.length;

    // allSuggestions should always include EVERYTHING for duplicate detection to work
    setAllSuggestions([...records, ...openAdditionalRecords, ...closedAdditionalRecords]);

    if (status === 'closed') {
      records = records.concat(closedAdditionalRecords);
    }

    updateTabCounts(records, openAdditionalCount, closedAdditionalCount);

    if (!renderStatusGrid(status, records)) {
      return;
    }
  } catch (err) {
    handleLoadTabError(err);
  }

  announceTabLoaded(status);
}

function syncStatusTab(status) {
  setCurrentStatus(status);
  document.querySelectorAll('#status-tabs .nav-link').forEach(link => {
    const isActive = link.getAttribute('data-status') === status;
    link.classList.toggle('active', isActive);
    if (link.hasAttribute('role')) {
      link.setAttribute('aria-selected', isActive ? 'true' : 'false');
    }
  });
}

function renderTabDescription(status) {
  const tabDesc = document.getElementById('tab-desc');
  tabDesc.replaceChildren();
  tabDesc.textContent = descriptions[status] || '';

  const libraryName = workflowSettings.isOverride ? (pb.authStore.model?.libraryOrgName || 'Library') : 'System Defaults';

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

  // Add auto-rejection info for Suggestions
  if (status === 'suggestion') {
    workflowSettings.outstandingTimeoutDays = parseInt(workflowSettings.outstandingTimeoutDays || '30', 10) || 30;
    if (workflowSettings.outstandingTimeoutEnabled) {
      addSuffix(`${libraryName} auto-reject enabled (${workflowSettings.outstandingTimeoutDays} days):`, ' Stalled suggestions will be auto-rejected. (');
    } else {
      addSuffix(`${libraryName} auto-reject disabled:`, ' (');
    }
  }

  // Add auto-promoter info for Pending purchase
  if (status === 'outstanding_purchase') {
    tabDesc.textContent = 'Pending purchase contains approved suggestions that are waiting to appear in Polaris. Staff can add a BIB ID to move a suggestion to the Pending hold phase.';
    if (workflowSettings.autoPromote) {
      addSuffix(`${libraryName} auto-promoter enabled:`, ' ASAP will check Polaris automatically. (');
    } else {
      addSuffix(`${libraryName} auto-promoter disabled:`, ' (');
    }
  }

  // Add auto-close info for Hold placed
  if (status === 'hold_placed') {
    workflowSettings.holdPickupTimeoutDays = parseInt(workflowSettings.holdPickupTimeoutDays || '14', 10) || 14;
    if (workflowSettings.holdPickupTimeoutEnabled) {
      addSuffix(`${libraryName} auto-close enabled (${workflowSettings.holdPickupTimeoutDays} days):`, ' Unpicked-up holds will auto-close. (');
    } else {
      addSuffix(`${libraryName} auto-close disabled:`, ' (');
    }
  }

  // Add auto-close info for Pending hold
  if (status === 'pending_hold') {
    workflowSettings.pendingHoldTimeoutDays = parseInt(workflowSettings.pendingHoldTimeoutDays || '14', 10) || 14;
    if (workflowSettings.pendingHoldTimeoutEnabled) {
      addSuffix(`${libraryName} auto-close enabled (${workflowSettings.pendingHoldTimeoutDays} days):`, ' Unprocessed items will auto-close. (');
    } else {
      addSuffix(`${libraryName} auto-close disabled:`, ' (');
    }
  }

  // Add auto-close info for Additional copies
  if (status === 'additional_copies') {
    workflowSettings.additionalCopyTimeoutDays = parseInt(workflowSettings.additionalCopyTimeoutDays || '14', 10) || 14;
    if (workflowSettings.additionalCopyTimeoutEnabled) {
      addSuffix(`${libraryName} auto-close enabled (${workflowSettings.additionalCopyTimeoutDays} days):`, ' Stale tasks will auto-close. (');
    } else {
      addSuffix(`${libraryName} auto-close disabled:`, ' (');
    }
  }
}

function clearJobMessage() {
  const jobMsg = document.getElementById('job-msg');
  if (jobMsg) jobMsg.textContent = '';
}

function updateAdminActions(status) {
  const adminBar = document.getElementById('admin-actions-bar');
  const promoterBtn = document.getElementById('btn-run-promoter-check');
  const holdBtn = document.getElementById('btn-run-hold-check');
  const deleteClosedBtn = document.getElementById('btn-delete-closed-requests');

  adminBar.classList.add('hidden');
  promoterBtn.classList.add('hidden');
  holdBtn.classList.add('hidden');
  if (deleteClosedBtn) deleteClosedBtn.classList.add('hidden');

  const isCurrentlySuperAdmin = isSuperAdminStaff();

  if (isCurrentlySuperAdmin) {
    if (status === 'outstanding_purchase' && workflowSettings.autoPromote) {
      adminBar.classList.remove('hidden');
      promoterBtn.classList.remove('hidden');
    } else if (status === 'pending_hold') {
      adminBar.classList.remove('hidden');
      holdBtn.classList.remove('hidden');
    }
  }
  if (status === 'closed' && isAdminStaff() && deleteClosedBtn) {
    adminBar.classList.remove('hidden');
    deleteClosedBtn.classList.remove('hidden');
  }
}

function loadSettingsTab() {
  closeOpenDialogs();
  closeActionMenu?.();
  gridContainer.classList.add('hidden');
  if (staffGridFilterBar) staffGridFilterBar.classList.add('hidden');
  hideTagFilter();
  hideClaimFilter();
  settingsContainer.classList.remove('hidden');
  activateSettingsSection(getSettingsSectionFromHash() || currentSettingsSection, { updateHash: false });
  if (!isAdminStaff()) {
    showSettingsAccessDenied();
    return;
  }
  loadSettings({ showErrors: true });
}

function prepareGridView() {
  gridContainer.classList.remove('hidden');
  settingsContainer.classList.add('hidden');
  if (staffGridFilterBar) staffGridFilterBar.classList.remove('hidden');
  hideSettingsAccessDenied();
  resetGrid();
}

async function fetchTitleRequests() {
  const params = new URLSearchParams();
  if (isSuperAdminStaff()) {
    params.set('scope', currentWorkflowOrgScopeId || 'all');
  }
  params.set('_', String(Date.now()));
  return authorizedJson('/api/asap/staff/title-requests?' + params.toString(), { cache: 'no-store' });
}

async function fetchAdditionalCopies(status = 'open') {
  const params = new URLSearchParams();
  if (isSuperAdminStaff()) {
    params.set('scope', currentWorkflowOrgScopeId || 'all');
  }
  params.set('status', status);
  params.set('_', String(Date.now()));
  return authorizedJson('/api/asap/staff/additional-copies?' + params.toString(), { cache: 'no-store' });
}

async function safeFetchTitleRequests() {
  try {
    return await fetchTitleRequests();
  } catch (err) {
    console.warn('Title-request count refresh failed.', err);
    return { items: [] };
  }
}

async function safeFetchAdditionalCopies(status = 'open') {
  try {
    return await fetchAdditionalCopies(status);
  } catch (err) {
    console.warn('Additional-copy refresh failed.', err);
    return { items: [] };
  }
}

function updateWorkflowScopeControl(data) {
  const wrapper = document.getElementById('workflow-library-scope-label');
  const select = document.getElementById('workflow-library-scope');
  if (!wrapper || !select) return;

  const scope = data && data.scope ? data.scope : {};
  if (!scope.superAdmin) {
    hideWorkflowScopeControl();
    return;
  }

  const libraries = Array.isArray(data.availableLibraries) ? data.availableLibraries.slice() : [];
  const selectedScopeId = scope.mode === 'library' && scope.libraryOrgId ? scope.libraryOrgId : 'all';
  setCurrentWorkflowOrgScopeId(selectedScopeId);

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
      setCurrentWorkflowOrgScopeId(select.value || 'all');
      loadTab(currentStatus);
    });
    select.dataset.workflowScopeBound = 'true';
  }

  wrapper.classList.remove('hidden');
  if (staffGridFilterBar) {
    staffGridFilterBar.classList.remove('hidden');
  }
}

function hideWorkflowScopeControl() {
  const wrapper = document.getElementById('workflow-library-scope-label');
  const select = document.getElementById('workflow-library-scope');
  if (wrapper) wrapper.classList.add('hidden');
  if (select) select.innerHTML = '';
}

function renderStatusGrid(status, records) {
  setCurrentSuggestions(records.filter(row => normalizeStatus(row.status) === status));
  updateTagFilter(currentSuggestions);
  updateClaimFilter();

  if (!currentSuggestions.length) {
    gridContainer.innerHTML = `<div class="alert alert-light border">${escapeAttr(emptyStateMessages[status] || 'No suggestions found.')}</div>`;
    return false;
  }

  renderCurrentGrid();
  return true;
}

function renderAdditionalCopiesGrid(records) {
  setCurrentSuggestions(records);
  updateTagFilter(records);
  updateClaimFilter();
  if (additionalCopyStatusFilterSelect) {
    additionalCopyStatusFilterSelect.classList.add('hidden');
  }
  if (!records.length) {
    gridContainer.textContent = '';
    const empty = document.createElement('div');
    empty.className = 'alert alert-light border';
    empty.textContent = emptyStateMessages.additional_copies;
    gridContainer.appendChild(empty);
    return;
  }
  renderCurrentGrid('additional_copies');
}

async function renderAdditionalCopiesLoadError(err) {
  hideTagFilter();
  hideClaimFilter();
  const titleResult = await safeFetchTitleRequests();
  const titleRecords = Array.isArray(titleResult.items) ? titleResult.items : [];
  updateTabCounts(titleRecords, 0);
  gridContainer.replaceChildren();

  const alert = document.createElement('div');
  alert.className = 'alert alert-warning border';
  const title = document.createElement('div');
  title.className = 'font-weight-bold mb-1';
  title.textContent = 'Additional copies could not load.';
  const detail = document.createElement('div');
  detail.textContent = err && err.message ? err.message : 'The additional-copy queue endpoint returned an error.';
  alert.append(title, detail);
  gridContainer.appendChild(alert);
}

function announceTabLoaded(status) {
  const announcer = document.getElementById('status-announcer');
  announcer.textContent = "Loaded " + status + " tab.";

  // Manage focus for screen readers when tab changes
  const firstHeader = document.getElementById('tab-desc');
  if (firstHeader) firstHeader.focus();

  // Auto-open requested record if parameter is present
  const requestId = requestedRequestIdFromUrl();
  if (requestId) {
    const row = allSuggestions.find(r => r.id === requestId);
    if (row) {
      // Clear the parameter from URL to prevent re-opening
      const url = new URL(window.location.href);
      url.searchParams.delete('request');
      window.history.replaceState(null, '', url.pathname + url.search + url.hash);

      openEdit(row.id, row.status, 'Edit', '', 'Save');
    }
  }
}

function handleLoadTabError(err) {
  console.error('Failed to load data', err);
}

export function resetGrid() {
  if (grid && typeof grid.destroy === 'function') {
    grid.destroy();
  }
  setGrid(null);
  gridContainer.innerHTML = '';
}

const duplicateStatusNames = {
  suggestion: 'Suggestions',
  outstanding_purchase: 'Pending purchase',
  pending_hold: 'Pending hold',
  hold_placed: 'Hold placed',
  additional_copies: 'Additional copies',
  closed: 'Closed'
};

function normalizeMatchText(value) {
  return String(value || '')
    .toLowerCase()
    .replace(/\([^)]*\)\s*$/, '') // Strip trailing parentheses
    .replace(/[^\w\s]/g, ' ')     // Replace remaining punctuation with space
    .replace(/\s+/g, ' ')          // Collapse spaces
    .trim();
}

function normalizeMatchIdentifier(value) {
  return String(value || '')
    .toLowerCase()
    .replace(/[^a-z0-9]/g, '') // Strip everything except alphanumeric
    .trim();
}

function duplicateMatchReasons(row, candidate) {
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

export function getDuplicateSummary(row) {
  if (!allSuggestions || !allSuggestions.length) return null;

  const matches = allSuggestions.map(r => {
    if (r.id === row.id) return false;
    // We now allow cross-library duplicates to be flagged
    const reasons = duplicateMatchReasons(row, r);
    return reasons.length ? { row: r, reasons } : null;
  }).filter(Boolean);

  if (!matches.length) return null;

  // Group by status
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

export function getDuplicateLabels(row) {
  const summary = getDuplicateSummary(row);
  if (!summary) return [];

  const labels = [];
  for (const [status, count] of Object.entries(summary.statusCounts)) {
    const displayName = duplicateStatusNames[status] || status;
    const text = count > 1 ? `Dup (${displayName} x${count})` : `Dup (${displayName})`;
    labels.push(text);
  }
  return labels;
}

export function hideTagFilter() {
  setActiveTagFilter('');
  if (tagFilterSelect) {
    tagFilterSelect.classList.add('hidden');
    tagFilterSelect.innerHTML = '';
  }
  if (additionalCopyStatusFilterSelect) {
    additionalCopyStatusFilterSelect.classList.add('hidden');
  }
}

export function hideClaimFilter() {
  if (claimFilterSelect) {
    claimFilterSelect.classList.add('hidden');
  }
}

export function updateTagFilter(records) {
  if (!tagFilterSelect || !staffGridFilterBar) return;
  const counts = tagCountsForRecords(records);
  if (!counts.length) {
    hideTagFilter();
    return;
  }

  const previous = activeTagFilter;
  tagFilterSelect.innerHTML = [
    '<option value="">All flags</option>',
    ...counts.map(([flag, count]) => {
      const display = getFlagDisplay(flag);
      return `<option value="${escapeAttr(flag)}">${escapeAttr(display.label)} (${count})</option>`;
    })
  ].join('');

  const stillExists = counts.some(([tag]) => tag === previous);
  tagFilterSelect.value = stillExists ? previous : '';
  setActiveTagFilter(tagFilterSelect.value);
  tagFilterSelect.classList.remove('hidden');
  staffGridFilterBar.classList.remove('hidden');
}

export function updateClaimFilter() {
  if (!claimFilterSelect || !staffGridFilterBar) return;
  claimFilterSelect.value = currentClaimFilter;
  claimFilterSelect.classList.remove('hidden');
  
  if (similarRequestFilterSelect) {
    similarRequestFilterSelect.value = currentSimilarRequestFilter;
    const showSimilar = currentStatus === 'suggestion';
    similarRequestFilterSelect.classList.toggle('hidden', !showSimilar);
  }
  
  if (closedTypeFilterSelect) {
    const isClosedTab = currentStatus === 'closed';
    closedTypeFilterSelect.classList.toggle('hidden', !isClosedTab);
    if (isClosedTab) {
      closedTypeFilterSelect.value = currentClosedTypeFilter;
    }
  }

  staffGridFilterBar.classList.remove('hidden');
}

export function applyTagFilter(records) {
  if (!activeTagFilter) return records || [];
  return (records || []).filter(record => {
    return getFilterableLabelsForRow(record).includes(activeTagFilter);
  });
}

export function applySimilarRequestFilter(records) {
  if (currentSimilarRequestFilter === 'similar') {
    return (records || []).filter(record => !!getDuplicateSummary(record));
  }
  if (currentSimilarRequestFilter === 'unique') {
    return (records || []).filter(record => !getDuplicateSummary(record));
  }
  return records || [];
}

export function currentStaffId() {
  return String((pb.authStore.model && pb.authStore.model.id) || '').trim();
}

export function isClaimedByCurrentUser(row) {
  const staffId = currentStaffId();
  return !!staffId && String(row?.claimedByStaffUserId || '').trim() === staffId;
}

export function isUnclaimed(row) {
  return !String(row?.claimedByStaffUserId || '').trim();
}

export function applyClaimFilter(records, filter = currentClaimFilter, staffId = currentStaffId()) {
  if (filter === 'mine') {
    return (records || []).filter(record => !!staffId && String(record.claimedByStaffUserId || '').trim() === staffId);
  }
  if (filter === 'unclaimed') {
    return (records || []).filter(record => isUnclaimed(record));
  }
  if (filter === 'mine_unclaimed') {
    return (records || []).filter(record => {
      const claimedBy = String(record.claimedByStaffUserId || '').trim();
      return !claimedBy || (!!staffId && claimedBy === staffId);
    });
  }
  return records || [];
}

function normalizedSortText(value) {
  return String(value || '')
    .replace(/\s+/g, ' ')
    .trim()
    .toLowerCase();
}

function dateSortValue(value) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return normalizedSortText(value);
  return date.toISOString();
}

function bibSortValue(value) {
  const text = String(value || '').trim();
  const num = Number(text);
  return Number.isFinite(num) ? String(num).padStart(20, '0') : normalizedSortText(text);
}

function claimSortValue(row) {
  if (!row || !String(row.claimedByStaffUserId || '').trim()) return 'zz_unclaimed';
  return normalizedSortText(row.claimedByDisplayName || 'claimed');
}

function getGridDataRow(row, status) {
  if (status === 'additional_copies') {
    return {
      id: row.id,
      title: normalizedSortText(row.title),
      author: normalizedSortText(row.author),
      bibid: bibSortValue(row.bibid),
      format: normalizedSortText(formatMap[row.format] || row.format),
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
    format: normalizedSortText(formatMap[row.format] || row.format),
    publication: normalizedSortText(formatPublication(row.publication)),
    submitted: dateSortValue(row.created),
    claimedBy: claimSortValue(row),
    notes: normalizedSortText(row.notes),
    actions: row.id
  };

  if (status === 'closed') {
    base.closeReason = normalizedSortText(closeReasonMap[row.closeReason] || row.closeReason);
  }

  return base;
}

export function toggleTagFilter(tagName) {
  const nextTag = activeTagFilter === tagName ? '' : tagName;
  setActiveTagFilter(nextTag);
  if (tagFilterSelect) {
    tagFilterSelect.value = nextTag;
  }
  renderCurrentGrid(currentStatus);
}

export function applyTypeFilter(records) {
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

export function renderCurrentGrid(status = currentStatus) {
  resetGrid();

  if (gridSearchInput) {
    gridSearchInput.value = gridSearchKeyword;
  }

  const visibleRecords = applyClaimFilter(
    applySimilarRequestFilter(
      applyTypeFilter(
        applyTagFilter(currentSuggestions)
      )
    )
  );

  if (!visibleRecords.length) {
    gridContainer.innerHTML = `<div class="alert alert-light border">${escapeAttr(emptyFilteredGridMessage())}</div>`;
    return;
  }

  const rowById = new Map();
  visibleRecords.forEach(row => rowById.set(row.id, row));

  const g = new gridjs.Grid({
    columns: getGridColumns(status, rowById),
    data: visibleRecords.map(row => getGridDataRow(row, status)),
    search: {
      placeholder: 'Search...',
      keyword: gridSearchKeyword
    },
    pagination: { limit: 25 },
    sort: true,
    width: '100%'
  });

  setGrid(g);
  g.render(gridContainer);
}

function emptyFilteredGridMessage() {
  if (currentSimilarRequestFilter === 'similar') {
    return 'No records with similar requests elsewhere match the current filters.';
  }
  if (currentSimilarRequestFilter === 'unique') {
    return 'No unique records match the current filters.';
  }
  if (activeTagFilter && currentClaimFilter !== 'all') {
    return 'No suggestions match this workflow flag and claim filter.';
  }
  if (activeTagFilter) {
    return 'No suggestions match this workflow flag.';
  }
  if (currentClaimFilter === 'mine') {
    return 'No requests in this stage are claimed by you.';
  }
  if (currentClaimFilter === 'unclaimed') {
    return 'No unclaimed requests in this stage.';
  }
  if (currentClaimFilter === 'mine_unclaimed') {
    return 'No requests in this stage are claimed by you or unclaimed.';
  }
  return 'No suggestions found.';
}

export function updateTabCounts(records, openAdditionalCount = 0, closedAdditionalCount = 0) {
  const counts = Object.fromEntries(statusStages.map(status => [status, 0]));
  records.forEach(row => {
    // Only count title requests from the main records list to avoid double-counting 
    // when additional copies are merged into the same list (like in the Closed tab)
    if (row.type !== 'title_request' && row.type !== undefined) return;
    
    const status = normalizeStatus(row.status);
    if (Object.prototype.hasOwnProperty.call(counts, status)) {
      counts[status] += 1;
    }
  });
  
  // Add additional copy counts to their respective tabs
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

export function formatStandardDate(d) {
  if (!d) return '';
  const date = (d instanceof Date) ? d : new Date(d);
  return date.toLocaleDateString('en-US');
}

export function formatDateTime(value) {
  if (!value) return '';
  const date = new Date(value);
  return formatStandardDate(date) + ' ' + date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function formatPublication(value) {
  return String(value || '').trim();
}

export function formatNote(row) {
  const note = row?.notes;
  const text = String(note || '').trim();
  if (!text) return '';
  return gridjs.html(`<button type="button" class="truncate-note" data-note-record-id="${escapeAttr(row?.id || '')}" data-notes-action="true" data-no-row-edit="true" title="View notes and activity" aria-label="View notes and activity"><i class="fa fa-commenting-o" aria-hidden="true"></i></button>`);
}

export function renderBibIdCell(row) {
  const bibId = String(row?.bibid || '').trim();
  if (!bibId) return '';
  const url = leapBibUrl(bibId);
  if (!url || !/^https?:\/\//i.test(url)) return escapeAttr(bibId);
  return gridjs.html(`<a href="${escapeAttr(url)}" target="_blank" rel="noopener noreferrer" data-no-row-edit="true">${escapeAttr(bibId)}</a>`);
}

export function rowMarker(row) {
  return `<span class="asap-row-marker" data-suggestion-id="${escapeAttr(row.id)}" hidden></span>`;
}

export function renderBarcodeCell(row) {
  const barcode = escapeAttr(row.barcode || '');
  const name = [row.nameFirst, row.nameLast].filter(Boolean).join(' ').trim();
  const nameHtml = name ? `<div class="barcode-patron-name text-muted small">${escapeAttr(name)}</div>` : '';
  const isAdditionalCopy = row.type === 'additional_copy';
  const typeBadge = isAdditionalCopy ? '<div class="flag-badge flag-info mb-1" style="font-size: var(--asap-font-size-xs); cursor: default;">Additional Copy</div>' : '';
  
  return gridjs.html(`
    <div class="barcode-cell">
      <div class="barcode-content">
        ${typeBadge}
        <div class="barcode-text">${barcode}</div>
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

const NOTES_COLUMN_WIDTH = '110px';


export function getActionsColumnWidth(status) {
  if (status === 'additional_copies') return '100px';
  if (status === 'suggestion') return '180px';
  if (status === 'outstanding_purchase') return '160px';
  return '100px';
}

export function getGridColumns(status, rowById = new Map()) {
  const rowFor = (id) => rowById.get(id) || {};

  const barcodeColumn = {
    id: 'barcode',
    name: 'Barcode',
    width: '160px',
    formatter: (cell, row) => renderBarcodeCell(rowFor(row.cells[0].data))
  };


  const titleColumn = {
    id: 'title',
    name: 'Title',
    width: '310px',
    formatter: (cell, row) => renderTitleCell(rowFor(row.cells[0].data))
  };


  const authorColumn = {
    id: 'author',
    name: 'Author',
    width: '170px',
    formatter: (cell, row) => renderAuthorCell(rowFor(row.cells[0].data))
  };


  const formatColumn = {
    id: 'format',
    name: 'Format',
    width: '100px',
    formatter: (cell, row) => escapeAttr(formatMap[rowFor(row.cells[0].data).format] || rowFor(row.cells[0].data).format || '')
  };

  const identifierColumn = {
    id: 'identifier',
    name: 'ID/ISBN',
    width: '150px',
    sort: false,
    formatter: (cell, row) => escapeAttr(rowFor(row.cells[0].data).identifier || '')
  };

  const timingColumn = {
    id: 'publication',
    name: 'Timing',
    width: '100px',
    formatter: (cell, row) => escapeAttr(formatPublication(rowFor(row.cells[0].data).publication))
  };

  const submittedColumn = {
    id: 'submitted',
    name: 'Submitted',
    width: '100px',
    formatter: (cell, row) => escapeAttr(formatStandardDate(rowFor(row.cells[0].data).created))
  };

  const claimedColumn = {
    id: 'claimedBy',
    name: 'Claimed by',
    width: '110px',
    formatter: (cell, row) => renderClaimCell(rowFor(row.cells[0].data))
  };

  const notesColumnDef = {
    id: 'notes',
    name: 'Notes',
    width: NOTES_COLUMN_WIDTH,
    sort: false,
    formatter: (cell, row) => formatNote(rowFor(row.cells[0].data))
  };

  const actionsColumn = {
    id: 'actions',
    name: 'Actions',
    width: getActionsColumnWidth(status),
    sort: false,
    formatter: (cell, row) => gridjs.html(renderRowActions(rowFor(row.cells[0].data)))
  };

  const idColumn = {
    id: 'id',
    name: 'ID',
    hidden: true
  };

  if (status === 'additional_copies') {
    return [
      idColumn,
      titleColumn,
      authorColumn,
      {
        id: 'bibid',
        name: 'BIB ID',
        width: '100px',
        sort: false,
        formatter: (cell, row) => renderBibIdCell(rowFor(row.cells[0].data))
      },
      formatColumn,
      claimedColumn,
      {
        id: 'createdBy',
        name: 'Created by',
        width: '120px',
        formatter: (cell, row) => escapeAttr(rowFor(row.cells[0].data).createdByUsername || '')
      },
      notesColumnDef,
      actionsColumn
    ];
  }

  if (status === 'suggestion') {
    return [
      idColumn,
      barcodeColumn,
      titleColumn,
      authorColumn,
      identifierColumn,
      formatColumn,
      timingColumn,
      submittedColumn,
      claimedColumn,
      notesColumnDef,
      actionsColumn
    ];
  }

  if (status === 'closed') {
    return [
      idColumn,
      barcodeColumn,
      titleColumn,
      authorColumn,
      formatColumn,
      submittedColumn,
      {
        id: 'closeReason',
        name: 'Closed reason',
        width: '140px',
        formatter: (cell, row) => escapeAttr(closeReasonMap[rowFor(row.cells[0].data).closeReason] || rowFor(row.cells[0].data).closeReason || '')
      },
      claimedColumn,
      notesColumnDef,
      actionsColumn
    ];
  }

  const baseCols = [
    idColumn,
    barcodeColumn,
    titleColumn,
    authorColumn,
    identifierColumn,

    {
      id: 'bibid',
      name: 'BIB ID',
      width: '100px',
      sort: false,
      formatter: (cell, row) => renderBibIdCell(rowFor(row.cells[0].data))
    },
    formatColumn
  ];

  if (status !== 'pending_hold' && status !== 'hold_placed') {
    baseCols.push(timingColumn);
  }

  baseCols.push(submittedColumn, claimedColumn, notesColumnDef, actionsColumn);
  return baseCols;
}

function renderAdditionalCopySourceCell(row) {
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


export function getDuplicateBadgesHtml(row) {
  const flags = getDuplicateLabels(row);
  if (!flags.length) return '';

  return flags.map(rawFlag => {
    const normalized = normalizeLabel(rawFlag);
    const display = getFlagDisplay(rawFlag);
    const isActive = activeTagFilter === normalized;
    const title = isActive ? 'Clear filter' : 'Filter by ' + display.label;
    return ` <span class="flag-badge ${escapeAttr(display.className)} ${isActive ? 'active' : ''}" data-tag="${escapeAttr(normalized)}" role="button" title="${escapeAttr(title)}">${escapeAttr(display.label)}</span>`;
  }).join('');
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

export function renderWorkflowTags(tags, row) {
  const clean = row ? effectiveWorkflowFlagsForRow(row, tags) : cleanWorkflowTags(tags);
  if (row && row.autohold === false && !clean.includes("No hold requested")) {
    clean.push("No hold requested");
  }
  if (!clean.length) {
    return '<div class="text-muted small">No workflow flags</div>';
  }
  return `<div class="workflow-tag-list">${clean.map(flag => {
    const normalized = normalizeLabel(flag);
    const presentation = getWorkflowTagPresentation(flag);
    const isActive = activeTagFilter === normalized;
    const title = isActive ? 'Clear filter' : 'Filter by ' + presentation.text;
    return `<span class="flag-badge ${escapeAttr(presentation.className)} ${isActive ? 'active' : ''}" data-tag="${escapeAttr(normalized)}" role="button" title="${escapeAttr(title)}">${escapeAttr(presentation.text)}</span>`;
  }).join('')}</div>`;
}

export function getIsbnCheckBadgesHtml(row) {
  const label = getIsbnCheckLabel(row);
  if (!label) return '';
  const normalized = normalizeLabel(label);
  const isActive = activeTagFilter === normalized;
  
  const status = typeof row?.isbnCheckStatus === 'string' ? row.isbnCheckStatus : '';
  const tooltip = status === 'pending'
    ? 'Background identifier number processing is still running. This suggestion is already submitted.'
    : 'Identifier number background processing result.';
  const display = getFlagDisplay(label);
    
  return ` <span class="flag-badge ${escapeAttr(display.className)} ${isActive ? 'active' : ''}" data-tag="${escapeAttr(normalized)}" role="button" title="${escapeAttr(isActive ? 'Clear filter' : tooltip)}">${escapeAttr(display.label)}</span>`;
}

export function renderDuplicateSummary(row) {
  const summary = getDuplicateSummary(row);
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

export function renderPolarisRowSearchButton(row, mode) {
  const value = polarisSearchValueForRow(row, mode);
  if (!value) return '';
  return renderPolarisSearchButtonMarkup(mode, {
    'data-no-row-edit': 'true',
    'data-polaris-search-mode': mode,
    'data-suggestion-id': row.id
  });
}

export function renderTitleCell(row) {
  return gridjs.html(`
    <div class="staff-title-cell searchable-cell">
      <div class="searchable-cell-text">
        ${renderDuplicateSummary(row)}
        <div class="staff-title-main" title="${escapeAttr(row.title || '')}">${escapeAttr(row.title || '')}</div>
        ${renderWorkflowTags(row.workflowTags, row)}
      </div>
      <div class="searchable-cell-action">
        ${renderPolarisRowSearchButton(row, 'title')}
      </div>
    </div>
  `);
}

export function renderAuthorCell(row) {
  const author = (row.author || '').trim();
  if (!author) return '';
  return gridjs.html(`
    <div class="searchable-cell">
      <div class="searchable-cell-text" title="${escapeAttr(author)}">
        <span class="staff-author-text">${escapeAttr(author)}</span>
      </div>
      <div class="searchable-cell-action">
        ${renderPolarisRowSearchButton(row, 'author')}
      </div>
    </div>
  `);
}

export function renderClaimCell(row) {
  if (isUnclaimed(row)) {
    return gridjs.html('<span class="claim-badge claim-badge--unclaimed">Unclaimed</span>');
  }
  const source = row.claimType === 'automatic_format_rule' ? 'Auto-assigned by format rule' : 'Manual claim';
  if (isClaimedByCurrentUser(row)) {
    return gridjs.html(`<div><span class="claim-badge claim-badge--mine" title="${escapeAttr(source)}">Mine</span><div class="small text-muted">${escapeAttr(source)}</div></div>`);
  }
  const name = row.claimedByDisplayName || 'Staff';
  return gridjs.html(`<div><span class="claim-badge claim-badge--claimed" title="Claimed by ${escapeAttr(name)}. ${escapeAttr(source)}">Claimed by ${escapeAttr(name)}</span><div class="small text-muted">${escapeAttr(source)}</div></div>`);
}


export function formatCloseReason(row) {
  if (normalizeStatus(row.status) !== 'closed') {
    return '';
  }
  return closeReasonMap[row.closeReason] || 'Closed';
}

export function getRowActions(row) {
  const descriptors = buildRowActions(row, {
    currentStatus,
    currentStaffId: currentStaffId(),
    isAdmin: isAdminStaff()
  });
  return materializeRowActions(row, descriptors);
}

function materializeRowActions(row, actions) {
  return {
    ...actions,
    visible: actions.visible?.map(action => materializeRowAction(row, action)),
    primary: actions.primary ? materializeRowAction(row, actions.primary) : undefined,
    secondary: actions.secondary?.map(action => materializeRowAction(row, action)) || []
  };
}

function materializeRowAction(row, action) {
  return {
    ...action,
    onClick: () => runRowActionDescriptor(row, action)
  };
}

async function runRowActionDescriptor(row, action) {
  if (action.key === 'purchase') {
    const hasBib = String(row.bibid || '').trim().length > 0;
    openEdit(row.id, hasBib ? 'pending_hold' : 'outstanding_purchase', 'Approve for purchase', 'purchase', 'Purchase');
    return;
  }
  if (action.key === 'reject') {
    openEdit(row.id, 'closed', 'Reject', 'reject', 'Reject');
    return;
  }
  if (action.key === 'alreadyOwn') {
    openEdit(row.id, 'pending_hold', 'Already own', 'alreadyOwn', 'Already own');
    return;
  }
  if (action.key === 'silentClose') {
    openEdit(row.id, 'closed', 'Silent close', 'silentClose', 'Silent close');
    return;
  }
  if (action.key === 'queueHold') {
    openEdit(row.id, 'pending_hold', 'Queue for hold', '', 'Queue Hold');
    return;
  }
  if (action.key === 'undo') {
    await undoRow(row.id);
    return;
  }
  if (action.key === 'edit') {
    const status = normalizeStatus(row.status);
    const title = status === 'suggestion' ? 'Edit suggestion' : 'Edit';
    openEdit(row.id, row.status, title, '', 'Save');
    return;
  }
  if (action.key === 'delete') {
    await deleteClosedRequest(row.id);
    return;
  }
  if (action.key === 'closeDuplicate') {
    await closeDuplicateRequest(row.id);
    return;
  }
  if (action.key === 'buyAnotherCopy') {
    await buyAnotherCopyForRow(row);
    return;
  }
  if (action.key === 'closeAdditionalCopy') {
    await closeAdditionalCopyRequest(row.id);
    return;
  }
  if (action.key === 'claim') {
    await claimRequest(row.id);
    return;
  }
  if (action.key === 'unclaim' || action.key === 'clearClaim') {
    await unclaimRequest(row.id);
  }

  if (action.key === 'assign') {
    await openAssignDialog(row);
  }
}

export async function openAssignDialog(row) {
  const dialog = document.getElementById('assign-dialog');
  const staffSelect = document.getElementById('assign-staff-select');
  const contextText = document.getElementById('assign-dialog-context');
  const confirmBtn = document.getElementById('assign-confirm');
  const cancelBtn = document.getElementById('assign-cancel');

  if (!dialog || !staffSelect || !contextText || !confirmBtn || !cancelBtn) return;

  // Reset dialog state
  confirmBtn.textContent = 'Assign';
  confirmBtn.disabled = true;
  contextText.textContent = `Assigning: ${row.title || 'Untitled suggestion'}`;
  staffSelect.innerHTML = '<option value="">Loading staff members...</option>';
  staffSelect.value = '';

  try {
    const type = row.type === 'additional_copy' ? 'additional_copy' : 'title_request';
    const res = await authorizedJson(`/api/asap/staff/assignable-users?type=${encodeURIComponent(type)}&id=${encodeURIComponent(row.id)}`);
    const users = res.users || [];

    staffSelect.innerHTML = '<option value="">Select staff member...</option>';
    if (users.length === 0) {
      staffSelect.innerHTML = '<option value="">No active staff members found</option>';
    } else {
      users.forEach(u => {
        const opt = document.createElement('option');
        opt.value = u.id;
        opt.textContent = u.displayName || u.username;
        staffSelect.appendChild(opt);
      });
    }
  } catch (err) {
    staffSelect.innerHTML = '<option value="">Error loading staff</option>';
    console.error('Failed to load staff users', err);
  }

  staffSelect.onchange = () => {
    confirmBtn.disabled = !staffSelect.value;
  };

  const cleanup = () => {
    confirmBtn.onclick = null;
    cancelBtn.onclick = null;
    if (dialog.open) dialog.close();
  };

  cancelBtn.onclick = cleanup;
  confirmBtn.onclick = async () => {
    const assigneeId = staffSelect.value;
    if (!assigneeId) return;

    confirmBtn.disabled = true;
    confirmBtn.textContent = 'Assigning...';

    try {
      const endpointPrefix = row.type === 'additional_copy' ? 'additional-copies' : 'title-requests';
      await authorizedJson(`/api/asap/staff/${endpointPrefix}/${encodeURIComponent(row.id)}/assign`, {
        method: 'POST',
        body: JSON.stringify({ assigneeId })
      });
      const typeLabel = row.type === 'additional_copy' ? 'Additional-copy task' : 'Claim';
      showToast(`${typeLabel} assigned.`, 'success');
      cleanup();
      await loadTab(currentStatus);
    } catch (err) {
      const message = err && err.message ? err.message : 'Assignment failed.';
      await showAlert(message);
      confirmBtn.disabled = false;
      confirmBtn.textContent = 'Assign';
    }
  };

  dialog.showModal();
}

async function closeAdditionalCopyRequest(id) {
  const confirmed = await showConfirm('Close additional-copy task?', 'Closing this task will not change the original patron suggestion.');
  if (!confirmed) return;
  await authorizedJson(`/api/asap/staff/additional-copies/${encodeURIComponent(id)}/close`, {
    method: 'POST',
    body: JSON.stringify({})
  });
  showToast('Additional-copy task closed.', 'success');
  await loadTab(currentStatus);
}

function additionalCopyActionForRow(row) {
  const status = normalizeStatus(row && row.status);
  const bibid = String(row && row.bibid || '').trim();
  if ((status !== 'pending_hold' && status !== 'hold_placed') || !bibid || row.type === 'additional_copy') {
    return null;
  }
  return { label: 'Buy another copy', onClick: () => buyAnotherCopyForRow(row) };
}

function additionalCopyConfirmMessage(bibid, count) {
  if (count === 1) {
    return `There is already 1 open additional-copy task for this BIB. Create another?`;
  }
  if (count > 1) {
    return `There are already ${count} open additional-copy tasks for this BIB. Create another?`;
  }
  return bibid ? `Create an additional-copy task for BIB ${bibid}?` : 'Create an additional-copy task for this BIB?';
}

async function buyAnotherCopyForRow(row) {
  const id = row && row.id;
  if (!id) return;
  const preview = await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/additional-copy`, { cache: 'no-store' });
  const bibid = String(preview.bibid || row.bibid || '').trim();
  const openCount = Number(preview.openCount || 0);
  const confirmed = await confirmAdditionalCopyAction({ bibId: bibid }, {
    message: additionalCopyConfirmMessage(bibid, openCount),
    emailPurchaseReminderDefault: preview.emailPurchaseReminderDefault
  });
  if (!confirmed || !confirmed.confirmed) return;
  const response = await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/additional-copy`, {
    method: 'POST',
    body: JSON.stringify({ emailPurchaseReminder: confirmed.emailPurchaseReminder })
  });
  const afterCount = Number(response && response.openCountAfter || openCount + 1);
  showToast(`Additional-copy task created. Open tasks for this BIB: ${afterCount}.`, 'success');
  await loadTab(currentStatus);
}

function duplicateCloseActionForRow(row) {
  if (!row || normalizeStatus(row.status) === 'closed' || !hasWorkflowTag(row, 'Hold exists (same patron)')) {
    return null;
  }
  return { label: 'Close duplicate', className: 'danger', onClick: () => closeDuplicateRequest(row.id) };
}

export function claimActionsForRow(row) {
  if (isUnclaimed(row)) {
    return [{ label: 'Claim', onClick: () => claimRequest(row.id) }];
  }
  if (isClaimedByCurrentUser(row)) {
    return [{ label: 'Unclaim', onClick: () => unclaimRequest(row.id) }];
  }
  if (isAdminStaff()) {
    return [{ label: 'Clear claim', className: 'danger', onClick: () => unclaimRequest(row.id) }];
  }
  return [];
}

export async function claimRequest(requestId) {
  await mutateRequestClaim(requestId, 'claim', 'Request claimed.');
}

export async function unclaimRequest(requestId) {
  await mutateRequestClaim(requestId, 'unclaim', 'Request unclaimed.');
}

async function mutateRequestClaim(requestId, action, successMessage) {
  const row = currentSuggestions.find(r => r.id === requestId) || allSuggestions.find(r => r.id === requestId);
  if (!row) return;

  try {
    const endpointPrefix = row.type === 'additional_copy' ? 'additional-copies' : 'title-requests';
    await authorizedJson(`/api/asap/staff/${endpointPrefix}/${encodeURIComponent(requestId)}/${action}`, {
      method: 'POST',
      body: JSON.stringify({})
    });
    showToast(successMessage, 'success');
  } catch (err) {
    await showAlert(err.message || 'Claim update failed.');
  } finally {
    await loadTab(currentStatus);
  }
}

export async function runRowAction(action) {
  closeActionMenu();
  try {
    await action.onClick();
  } catch (error) {
    await showAlert(error.message || String(error) || 'Action failed');
  }
}

export function registerRowAction(action) {
  const actionId = `row-action-${incrementRowActionIdCounter()}`;
  rowActionRegistry.set(actionId, action);
  return actionId;
}

export function getRegisteredRowAction(actionId) {
  return rowActionRegistry.get(actionId);
}

export function renderRowActions(row) {
  const actions = getRowActions(row);
  let markup = `<div class="row-action-group" data-no-row-edit="true">`;

  if (actions.visible && actions.visible.length > 0) {
    actions.visible.forEach((action, index) => {
      const actionId = registerRowAction(action);
      const isFirst = index === 0;
      const isLast = (index === actions.visible.length - 1) && (!actions.secondary || actions.secondary.length === 0);

      let classes = `btn btn-sm ${action.className || 'btn-primary'}`;
      if (isFirst) {
        classes += ' row-action-primary';
      } else if (isLast) {
        // No special class needed, default border radii apply on right
      } else {
        classes += ' row-action-middle';
      }

      markup += `<button type="button" class="${escapeAttr(classes)}" data-row-action-id="${actionId}" data-no-row-edit="true">${escapeAttr(action.label)}</button>`;
    });
  } else if (actions.primary) {
    const primaryActionId = registerRowAction(actions.primary);
    markup += `<button type="button" class="btn btn-sm row-action-primary ${escapeAttr(actions.primary.className || 'btn-primary')}" data-row-action-id="${primaryActionId}" data-no-row-edit="true">${escapeAttr(actions.primary.label)}</button>`;
  }

  if (actions.secondary?.length) {
    const menuActionIds = actions.secondary.map(action => registerRowAction(action)).join(',');
    markup += `<button type="button" class="btn btn-sm btn-outline-secondary row-action-menu-trigger" aria-haspopup="menu" aria-expanded="false" data-row-menu-action-ids="${menuActionIds}" data-no-row-edit="true">⋯</button>`;
  }
  markup += `</div>`;
  return markup;
}

export function openActionMenu(triggerButton, actionIds) {
  closeActionMenu();
  const layer = document.getElementById('action-menu-layer');
  if (!layer) return;
  triggerButton.setAttribute('aria-expanded', 'true');
  const menu = document.createElement('div');
  menu.className = 'row-action-menu';
  menu.setAttribute('role', 'menu');
  actionIds.forEach((actionId) => {
    const action = getRegisteredRowAction(actionId);
    if (!action) return;
    const item = document.createElement('button');
    item.type = 'button';
    item.className = `row-action-menu-item ${action.className || ''}`.trim();
    item.setAttribute('role', 'menuitem');
    item.setAttribute('data-row-action-id', actionId);
    item.setAttribute('data-no-row-edit', 'true');
    item.textContent = action.label;
    menu.appendChild(item);
  });
  layer.appendChild(menu);
  positionActionMenu(triggerButton, menu);
  setActiveActionMenu({ triggerButton, menu });
}

export function positionActionMenu(triggerButton, menu) {
  const triggerRect = triggerButton.getBoundingClientRect();
  const menuRect = menu.getBoundingClientRect();
  const spacing = 6;
  const viewportPadding = 8;
  let top = triggerRect.bottom + spacing;
  let left = triggerRect.right - menuRect.width;
  if (top + menuRect.height > window.innerHeight - viewportPadding) {
    top = triggerRect.top - menuRect.height - spacing;
  }
  left = Math.max(viewportPadding, Math.min(left, window.innerWidth - menuRect.width - viewportPadding));
  menu.style.top = `${top}px`;
  menu.style.left = `${left}px`;
}

export function closeActionMenu() {
  if (!activeActionMenu) return;
  activeActionMenu.triggerButton?.setAttribute('aria-expanded', 'false');
  activeActionMenu.menu?.remove();
  setActiveActionMenu(null);
}

export function escapeAttr(value) {
  return String(value || "")
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function sanitizeHtml(html) {
  if (!html) return "";
  try {
    const doc = new DOMParser().parseFromString(html, "text/html");
    const safeTags = ["P", "BR", "B", "I", "STRONG", "EM", "DIV", "SPAN", "A", "UL", "OL", "LI", "H1", "H2", "H3", "H4", "H5", "H6", "BLOCKQUOTE", "TABLE", "THEAD", "TBODY", "TR", "TH", "TD", "U", "S", "HR"];
    const safeAttrs = ["href", "target", "rel", "title", "class", "id", "aria-label", "aria-hidden"];

    // Recursive walker to remove unsafe tags and attributes
    function walk(parent) {
      const children = Array.from(parent.childNodes);
      children.forEach(node => {
        if (node.nodeType === 1) { // Element
          if (!safeTags.includes(node.tagName)) {
            // Unsafe tag: replace with its text content
            const text = document.createTextNode(node.textContent);
            parent.replaceChild(text, node);
          } else {
            // Safe tag: check attributes
            const attrs = node.attributes;
            for (let i = attrs.length - 1; i >= 0; i--) {
              const name = attrs[i].name.toLowerCase();
              const value = attrs[i].value.trim().toLowerCase();

              const cleanValue = value.replace(/[\s\u0000-\u0020]/g, '');
              if (!safeAttrs.includes(name) || (name === "href" && (cleanValue.startsWith("javascript:") || cleanValue.startsWith("data:") || cleanValue.startsWith("vbscript:")))) {
                node.removeAttribute(name);
              }
            }
            walk(node);
          }
        }
      });
    }

    walk(doc.body);
    return doc.body.innerHTML;
  } catch (err) {
    return escapeAttr(html);
  }
}

function shouldIgnoreRowEditClick(target, event) {
  if (event.defaultPrevented) return true;
  if (event.button !== 0) return true;
  if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return true;

  return !!target.closest([
    'button',
    'a',
    'input',
    'select',
    'textarea',
    'label',
    'summary',
    '[role="button"]',
    '[role="menu"]',
    '[role="menuitem"]',
    '[data-row-action-id]',
    '[data-row-menu-action-ids]',
    '[data-no-row-edit]',
    '[data-notes-action]',
    '.row-action-group',
    '.row-action-menu',
    '.gridjs-search',
    '.gridjs-pagination'
  ].join(','));
}

function openSuggestionEditFromRow(recordId) {
  const row = currentSuggestions.find(item => item.id === recordId) || allSuggestions.find(item => item.id === recordId);
  if (!row) {
    showToast('Could not find that suggestion. Refresh and try again.', 'error');
    return;
  }

  const status = normalizeStatus(row.status);
  const isAdditionalCopy = row.type === 'additional_copy';
  const defaultTitle = isAdditionalCopy ? 'Edit additional-copy task' : (status === 'suggestion' ? 'Edit suggestion' : 'Edit');
  
  openEdit(row.id, status || currentStatus, defaultTitle, '', 'Save');
}

gridContainer.addEventListener('click', (e) => {
  const target = e.target;
  if (!(target instanceof Element)) return;

  const actionButton = e.target.closest('[data-row-action-id]');
  if (actionButton) {
    e.preventDefault();
    e.stopPropagation();
    const action = getRegisteredRowAction(actionButton.getAttribute('data-row-action-id'));
    if (action) runRowAction(action);
    return;
  }

  const menuTrigger = e.target.closest('[data-row-menu-action-ids]');
  if (menuTrigger) {
    e.preventDefault();
    e.stopPropagation();
    const actionIds = (menuTrigger.getAttribute('data-row-menu-action-ids') || '').split(',').filter(Boolean);
    openActionMenu(menuTrigger, actionIds);
    return;
  }

  const truncateBtn = e.target.closest('.truncate-note');
  if (truncateBtn && gridContainer.contains(truncateBtn)) {
    e.preventDefault();
    e.stopPropagation();
    const recordId = truncateBtn.getAttribute('data-note-record-id');
    const row = currentSuggestions.find(item => item.id === recordId) || allSuggestions.find(item => item.id === recordId);
    const content = document.getElementById('noteDialogContent');
    const dialog = document.getElementById('noteDialog');
    if (!row || !content || !dialog) {
      showToast('Could not find those notes. Refresh and try again.', 'error');
      return;
    }
    content.replaceChildren(renderNoteActivity(row.notes));

    if (row.type === 'additional_copy' && row.sourceTitleRequest) {
      const sourceWrapper = document.createElement('div');
      sourceWrapper.className = 'mb-3 pb-3 border-bottom';
      const strong = document.createElement('strong');
      strong.textContent = 'Original task: ';
      sourceWrapper.style.display = 'flex';
      sourceWrapper.style.alignItems = 'baseline';
      sourceWrapper.style.gap = '8px';
      sourceWrapper.append(strong, renderAdditionalCopySourceCell(row));
      content.prepend(sourceWrapper);
    }

    dialog.showModal();
    document.getElementById('noteDialogCloseBtn')?.focus();
    return;
  }

  const duplicateSummaryBtn = target.closest('.duplicate-summary-btn');
  if (duplicateSummaryBtn && gridContainer.contains(duplicateSummaryBtn)) {
    e.preventDefault();
    e.stopPropagation();
    const expanded = duplicateSummaryBtn.getAttribute('aria-expanded') === 'true';
    const detailsId = duplicateSummaryBtn.getAttribute('aria-controls');
    const details = detailsId ? document.getElementById(detailsId) : null;
    duplicateSummaryBtn.setAttribute('aria-expanded', expanded ? 'false' : 'true');
    const icon = duplicateSummaryBtn.querySelector('.duplicate-summary-icon');
    if (icon) icon.textContent = expanded ? '▸' : '▾';
    if (details) details.classList.toggle('hidden', expanded);
    return;
  }

  const tagBadge = target.closest('.flag-badge, .workflow-tag, .asap-duplicate-badge, .asap-isbn-check-badge');
  if (tagBadge && gridContainer.contains(tagBadge)) {
    e.preventDefault();
    e.stopPropagation();
    const tag = tagBadge.getAttribute('data-tag');
    if (tag) toggleTagFilter(tag);
    return;
  }

  const quickNewBtn = target.closest('.quick-new-suggestion');
  if (quickNewBtn && gridContainer.contains(quickNewBtn)) {
    e.preventDefault();
    e.stopPropagation();
    const barcode = quickNewBtn.getAttribute('data-barcode');
    if (barcode) openNewSuggestionForPatron(barcode);
    return;
  }

  const polarisSearchBtn = target.closest('.polaris-row-search');
  if (polarisSearchBtn && gridContainer.contains(polarisSearchBtn)) {
    e.preventDefault();
    e.stopPropagation();
    const recordId = polarisSearchBtn.getAttribute('data-suggestion-id');
    const mode = polarisSearchBtn.getAttribute('data-polaris-search-mode') || 'title';
    const row = currentSuggestions.find(item => item.id === recordId) || allSuggestions.find(item => item.id === recordId);
    if (row) {
      openPolarisSearch(row, mode);
    } else {
      showToast('Could not find that suggestion. Refresh and try again.', 'error');
    }
    return;
  }

  if (shouldIgnoreRowEditClick(target, e)) return;

  const tableRow = target.closest('tr');
  if (!tableRow || !gridContainer.contains(tableRow)) return;

  const marker = tableRow.querySelector('[data-suggestion-id]');
  const recordId = marker ? marker.getAttribute('data-suggestion-id') : '';
  if (!recordId) return;

  openSuggestionEditFromRow(recordId);
});

document.addEventListener('click', (event) => {
  const menuActionButton = event.target.closest('#action-menu-layer [data-row-action-id]');
  if (menuActionButton) {
    event.preventDefault();
    event.stopPropagation();
    const action = getRegisteredRowAction(menuActionButton.getAttribute('data-row-action-id'));
    if (action) runRowAction(action);
    return;
  }
  if (!activeActionMenu) return;
  const clickedMenu = activeActionMenu.menu.contains(event.target);
  const clickedTrigger = activeActionMenu.triggerButton.contains(event.target);
  if (!clickedMenu && !clickedTrigger) closeActionMenu();
});
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') closeActionMenu();
});
window.addEventListener('resize', closeActionMenu);
window.addEventListener('scroll', closeActionMenu, true);

document.addEventListener('click', (e) => {
  if (e.target.closest('.js-close-note-dialog')) {
    const dialog = document.getElementById('noteDialog');
    if (dialog) dialog.close();
  }
});
