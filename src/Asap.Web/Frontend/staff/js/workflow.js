import {
  authorizedJson,
  isAbortError,
  latestLoads,
  loadStaffSession,
  onSessionInvalid,
  onAccessUnavailable
} from './http.js';
import { createSettingsController } from './settings.js';
import { applyPolarisResultToControls, createPolarisLookup, renderResearchLinks, selectedStaffBibId } from './research.js';
import { loadAnalytics, resetAnalytics } from './analytics.js';
import { sanitizedHtmlFragment } from '../../shared/html.js';
import {
  requestedRequestIdFromUrl,
  requestedStatusFromUrl,
  replaceRequestParameter,
  replaceStageParameter
} from './url-utils.js';

const STATUS_LABELS = {
  open: 'Open',
  suggestion: 'Suggestion',
  outstanding_purchase: 'Outstanding purchase',
  pending_hold: 'Pending hold',
  hold_placed: 'Hold placed',
  closed: 'Closed'
};

function element(tag, attributes = {}, children = []) {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attributes)) {
    if (value === null || value === undefined) continue;
    if (name === 'className') node.className = value;
    else if (name === 'text') node.textContent = value;
    else if (name === 'checked') node.checked = Boolean(value);
    else if (name === 'disabled') node.disabled = Boolean(value);
    else if (name === 'value') node.value = value;
    else if (name.startsWith('on') && typeof value === 'function') node.addEventListener(name.slice(2), value);
    else node.setAttribute(name, String(value));
  }
  for (const child of Array.isArray(children) ? children : [children]) {
    if (child === null || child === undefined) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

function icon(name) {
  return element('i', { className: `fa fa-${name}`, 'aria-hidden': 'true' });
}

function commandButton(label, iconName, handler, className = 'secondary-button', disabled = false) {
  return element('button', { type: 'button', className, onclick: handler, disabled }, [icon(iconName), label]);
}

function text(value, fallback = 'Not recorded') {
  const normalized = value === null || value === undefined ? '' : String(value).trim();
  return normalized || fallback;
}

function dateTime(value) {
  if (!value) return 'Not recorded';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? String(value) : parsed.toLocaleString();
}

function statusLabel(value) {
  return STATUS_LABELS[value] || text(value, 'Unknown');
}

function closeReasonLabel(value) {
  const labels = {
    rejected: 'Rejected',
    manual: 'Closed by staff',
    duplicate_hold: 'Duplicate patron hold',
    hold_cancelled: 'Hold cancelled',
    silent: 'Closed silently',
    'Silently Closed': 'Closed silently'
  };
  return Object.hasOwn(labels, value) ? labels[value]
    : value ? String(value).replaceAll('_', ' ') : 'No reason recorded';
}

function currentRequestParameter() {
  return requestedRequestIdFromUrl();
}

function currentStageParameter() {
  return requestedStatusFromUrl();
}

export function createWorkflowApp() {
  const dom = {
    status: document.querySelector('#app-status'),
    signedOut: document.querySelector('#signed-out'),
    signedOutMessage: document.querySelector('#signed-out-message'),
    workspace: document.querySelector('#workspace'),
    sessionActions: document.querySelector('#session-actions'),
    staffIdentity: document.querySelector('#staff-identity'),
    signOut: document.querySelector('#sign-out'),
    queueView: document.querySelector('#queue-view'),
    additionalCopyView: document.querySelector('#additional-copy-view'),
    analyticsView: document.querySelector('#analytics-view'),
    analyticsContainer: document.querySelector('#analytics-container'),
    profileView: document.querySelector('#profile-view'),
    operationsView: document.querySelector('#operations-view'),
    operationsTab: document.querySelector('#operations-view-tab'),
    operationsScopeField: document.querySelector('#operations-scope-field'),
    operationsScope: document.querySelector('#operations-scope'),
    runWorkflowNow: document.querySelector('#run-workflow-now'),
    runWeeklyNow: document.querySelector('#run-weekly-now'),
    forceWeeklyNow: document.querySelector('#force-weekly-now'),
    sendTestEmail: document.querySelector('#send-test-email'),
    refreshOperations: document.querySelector('#refresh-operations'),
    queueProgressTable: document.querySelector('#queue-progress-table'),
    emailOperationsTable: document.querySelector('#email-operations-table'),
    settingsView: document.querySelector('#settings-view'),
    settingsTab: document.querySelector('#settings-view-tab'),
    viewTabs: [...document.querySelectorAll('.view-tab')],
    statusTabs: [...document.querySelectorAll('#status-tabs [data-status]')],
    scopeField: document.querySelector('#scope-field'),
    scope: document.querySelector('#library-scope'),
    search: document.querySelector('#request-search'),
    claim: document.querySelector('#claim-filter'),
    tag: document.querySelector('#tag-filter'),
    refresh: document.querySelector('#refresh-queue'),
    bulkDelete: document.querySelector('#bulk-delete-closed'),
    bulkDeleteCopies: document.querySelector('#bulk-delete-closed-copies'),
    bulkDeleteDialog: document.querySelector('#bulk-delete-dialog'),
    bulkDeleteClose: document.querySelector('#bulk-delete-close'),
    bulkDeleteScope: document.querySelector('#bulk-delete-scope'),
    bulkDeletePreview: document.querySelector('#bulk-delete-preview'),
    bulkDeleteSummary: document.querySelector('#bulk-delete-summary'),
    bulkDeleteItems: document.querySelector('#bulk-delete-items'),
    bulkDeleteConfirmation: document.querySelector('#bulk-delete-confirmation'),
    bulkDeleteExecute: document.querySelector('#bulk-delete-execute'),
    bulkDeleteResults: document.querySelector('#bulk-delete-results'),
    newSuggestion: document.querySelector('#new-suggestion'),
    summary: document.querySelector('#queue-summary'),
    grid: document.querySelector('#request-grid'),
    empty: document.querySelector('#queue-empty'),
    additionalCopyStatusTabs: [...document.querySelectorAll('#additional-copy-status-tabs [data-copy-status]')],
    additionalCopyScopeField: document.querySelector('#additional-copy-scope-field'),
    additionalCopyScope: document.querySelector('#additional-copy-library-scope'),
    additionalCopySearch: document.querySelector('#additional-copy-search'),
    additionalCopyClaim: document.querySelector('#additional-copy-claim-filter'),
    additionalCopyRefresh: document.querySelector('#refresh-additional-copies'),
    additionalCopySummary: document.querySelector('#additional-copy-summary'),
    additionalCopyGrid: document.querySelector('#additional-copy-grid'),
    additionalCopyEmpty: document.querySelector('#additional-copy-empty'),
    additionalCopyCreateReview: document.querySelector('#additional-copy-create-review'),
    additionalCopyCreateReviewSummary: document.querySelector('#additional-copy-create-review-summary'),
    additionalCopyCreateReviewDone: document.querySelector('#additional-copy-create-review-done'),
    profile: document.querySelector('#profile-form'),
    notificationEmail: document.querySelector('#notification-email'),
    weeklyEmail: document.querySelector('#weekly-email'),
    weeklyEnabled: document.querySelector('#weekly-enabled'),
    purchaseDefault: document.querySelector('#purchase-default'),
    additionalCopyDefault: document.querySelector('#additional-copy-default'),
    mineDefault: document.querySelector('#mine-default'),
    dialog: document.querySelector('#request-dialog'),
    dialogTitle: document.querySelector('#request-dialog-title'),
    dialogKicker: document.querySelector('#request-dialog-kicker'),
    dialogBody: document.querySelector('#request-dialog-body'),
    closeDialog: document.querySelector('#close-request'),
    createCopyDialog: document.querySelector('#additional-copy-create-dialog'),
    createCopyForm: document.querySelector('#additional-copy-create-form'),
    createCopySummary: document.querySelector('#additional-copy-create-summary'),
    createCopyReminder: document.querySelector('#additional-copy-reminder'),
    cancelCreateCopy: document.querySelector('#cancel-additional-copy'),
    staffSuggestionDialog: document.querySelector('#staff-suggestion-dialog'),
    staffSuggestionForm: document.querySelector('#staff-suggestion-form'),
    staffSuggestionStatus: document.querySelector('#staff-suggestion-status'),
    staffSuggestionBody: document.querySelector('#staff-suggestion-body'),
    staffSuggestionActions: document.querySelector('#staff-suggestion-actions'),
    closeStaffSuggestion: document.querySelector('#close-staff-suggestion')
  };

  const state = {
    staff: null,
    requests: [],
    additionalCopies: [],
    scope: 'all',
    status: 'suggestion',
    additionalCopyStatus: 'open',
    grid: null,
    additionalCopyGrid: null,
    activeView: 'queue',
    selectedRequestId: null,
    selectedRequestType: null,
    selectedRequestVersion: null,
    returnFocus: null,
    cancelFocusReturn: null,
    deepLinkHandled: false,
    additionalCopyDeepLinkHandled: false,
    additionalCopyLoaded: false,
    operationsScope: 'all',
    operationsLoaded: false,
    createCopyRequest: null,
    createCopyReturnFocus: null,
    staffSuggestion: null,
    staffSuggestionReturnFocus: null,
    partialSessionFailureMessage: null,
    partialSessionFailureOwner: null,
    partialSessionFailureDetailAvailable: false,
    partialSessionFailureAfterQueueSequence: null,
    unconfirmedCopyCreationAwaitingRefresh: readUnconfirmedCopyCreation(),
    queueLoadSequence: 0,
    configurations: new Map(),
    research: null,
    currentRequest: null,
    editControls: null,
    editorDirty: false,
    verifiedBib: null,
    dialogMutationInFlight: null,
    actionChoice: null,
    bulkDeleteState: null
  };

  function readUnconfirmedCopyCreation() {
    try {
      const saved = JSON.parse(window.sessionStorage.getItem('asap.staff.unconfirmedCopyCreation'));
      if (Number.isSafeInteger(saved?.libraryOrgId) && saved.libraryOrgId > 0 &&
          typeof saved.bibid === 'string' && saved.bibid &&
          typeof saved.sourceId === 'string' && /^\d+$/.test(saved.sourceId) &&
          typeof saved.version === 'string' && saved.version) {
        return { ...saved, reviewReady: false, reviewed: false };
      }
    } catch {
      // A later create verifies storage availability before dispatch.
    }
    return false;
  }

  function rememberUnconfirmedCopyCreation(value) {
    try {
      if (value) {
        window.sessionStorage.setItem('asap.staff.unconfirmedCopyCreation', JSON.stringify({
          libraryOrgId: value.libraryOrgId,
          bibid: value.bibid,
          sourceId: value.sourceId,
          version: value.version
        }));
      } else {
        window.sessionStorage.removeItem('asap.staff.unconfirmedCopyCreation');
      }
      return true;
    } catch {
      return false;
    }
  }

  function announce(message, kind = '') {
    dom.status.textContent = message || '';
    dom.status.className = `status-message${kind ? ` ${kind}` : ''}`;
  }

  const polarisLookup = createPolarisLookup({ authorizedJson, isAbortError, announce });

  function isVerifiedDraft(request) {
    return state.verifiedBib?.requestId === String(request.id) &&
      state.verifiedBib.version === request.version &&
      state.verifiedBib.identifier === String(request.identifier || '').trim() &&
      state.editControls?.bib.value.trim() === state.verifiedBib.bibId;
  }

  function updateResearchLinks() {
    const container = dom.dialogBody.querySelector('.research-section');
    if (!container || !state.currentRequest) return;
    renderResearchLinks(container, state.currentRequest, state.research, {
      title: state.editControls?.title.value,
      identifier: state.editControls?.identifier.value,
      bibId: state.editControls?.bib.value
    });
  }

  async function loadResearchConfiguration(request) {
    const load = latestLoads.begin('research-configuration');
    try {
      const data = await authorizedJson(
        `/api/asap/staff/research-configuration?requestId=${encodeURIComponent(request.id)}`,
        { signal: load.signal });
      if (!load.isCurrent() || !isCurrentDialogRequest(request, 'title_request')) return;
      state.research = data;
      updateResearchLinks();
    } catch (error) {
      if (isAbortError(error) || error.status === 401) return;
    } finally {
      latestLoads.finish('research-configuration', load.token);
    }
  }

  const settingsController = createSettingsController({
    root: dom.settingsView,
    tab: dom.settingsTab,
    announce,
    getStaff: () => state.staff
  });

  function cancelAssignmentCandidateLoad() {
    latestLoads.begin('assignment-candidates').abort();
  }

  function cancelPickupOptionsLoad() {
    latestLoads.begin('pickup-options').abort();
  }

  function cancelDialogMutationCompletion() {
    latestLoads.begin('dialog-mutation').abort();
    state.dialogMutationInFlight = null;
  }

  function cancelActionChoiceLoad() {
    latestLoads.begin('action-choice').abort();
    state.actionChoice = null;
  }

  function dismissActionChoice() {
    const choice = state.actionChoice;
    if (!choice) return;
    cancelActionChoiceLoad();
    choice.panel.remove();
    if (choice.returnFocus?.isConnected) choice.returnFocus.focus();
  }

  function cancelAdditionalCopyCreationCompletion() {
    latestLoads.begin('additional-copy-create-mutation').abort();
  }

  function cancelAdditionalCopyPreviewLoad() {
    latestLoads.begin('additional-copy-preview').abort();
  }

  function cancelStaffSuggestionLookup() {
    latestLoads.begin('staff-suggestion-lookup').abort();
  }

  function cancelStaffSuggestionConfiguration() {
    latestLoads.begin('staff-suggestion-configuration').abort();
  }

  function cancelStaffSuggestionMutation() {
    latestLoads.begin('staff-suggestion-mutation').abort();
  }

  function closeStaffSuggestion(options = {}) {
    if (state.staffSuggestion?.submitting && !options.force) {
      setStaffSuggestionStatus('Creation is in progress. Please wait for the authoritative result.', 'error');
      return false;
    }
    cancelStaffSuggestionLookup();
    cancelStaffSuggestionConfiguration();
    cancelStaffSuggestionMutation();
    polarisLookup.close();
    if (dom.staffSuggestionDialog.open) dom.staffSuggestionDialog.close();
    const returnFocus = state.staffSuggestionReturnFocus;
    state.staffSuggestion = null;
    state.staffSuggestionReturnFocus = null;
    if (returnFocus?.isConnected) returnFocus.focus();
    else if (dom.newSuggestion?.isConnected && options.focusButton !== false) dom.newSuggestion.focus();
    return true;
  }

  function cancelDialogFocusReturn() {
    state.cancelFocusReturn?.();
    state.cancelFocusReturn = null;
  }

  function isCurrentDialogSelection(request, requestType) {
    return !!state.staff &&
      dom.dialog.open &&
      state.selectedRequestType === requestType &&
      String(state.selectedRequestId) === String(request.id);
  }

  function isCurrentDialogRequest(request, requestType) {
    return isCurrentDialogSelection(request, requestType) &&
      state.selectedRequestVersion === request.version;
  }

  function isCurrentDialogMutation(mutation, request, requestType) {
    return mutation.isCurrent() && isCurrentDialogRequest(request, requestType);
  }

  function notificationOutcome(status, reason, label = 'Notification') {
    if (!status || status === 'not_requested' || status === 'not_applicable') return { text: '', partial: false };
    const explanation = reason ? ` (${reason.replaceAll('_', ' ')})` : '';
    if (status === 'queued') return { text: ` ${label} queued; delivery is pending.`, partial: false };
    if (status === 'suppressed') return { text: ` ${label} suppressed${explanation}.`, partial: true };
    if (status === 'dispatch_failed') return { text: ` ${label} could not be queued${explanation}.`, partial: true };
    return { text: ` ${label} ${status.replaceAll('_', ' ')}${explanation}.`, partial: true };
  }

  function isUnconfirmedMutationError(error, signal) {
    return !signal.aborted && (isAbortError(error) || error?.status === 0);
  }

  function unconfirmedResponseError() {
    return Object.assign(new Error('The server response did not confirm the workflow result.'), { status: 0 });
  }

  function isCommittedRequestResponse(result, requestId) {
    if (result?.committed !== true) return false;
    if (result.request === null) {
      return result.refreshUnavailable === true && Object.hasOwn(result, 'finalStatus');
    }
    const detail = result.request || result;
    if (String(detail.id) === String(requestId) && typeof detail.version === 'string' &&
        detail.version && typeof detail.status === 'string' && detail.status) return true;
    return false;
  }

  function confirmCurrent(request, requestType, message) {
    return window.confirm(message) && isCurrentDialogRequest(request, requestType);
  }

  function updateBulkDeleteButtons() {
    const authorized = ['admin', 'super_admin'].includes(state.staff?.role);
    dom.bulkDelete.hidden = !authorized || state.status !== 'closed';
    dom.bulkDeleteCopies.hidden = !authorized || state.additionalCopyStatus !== 'closed';
  }

  function closeBulkDelete(force = false) {
    const batch = state.bulkDeleteState;
    if (!batch) return;
    if (batch.submitting && !force) {
      dom.bulkDeleteSummary.textContent = 'Deletion is in progress. Wait for the result before closing.';
      return;
    }
    batch.previewAbort?.abort();
    state.bulkDeleteState = null;
    if (dom.bulkDeleteDialog.open) dom.bulkDeleteDialog.close();
    if (batch.returnFocus?.isConnected && state.staff) batch.returnFocus.focus();
  }

  function resetBulkDeletePreview() {
    const batch = state.bulkDeleteState;
    if (!batch || batch.submitting) return;
    batch.previewAbort?.abort();
    batch.previewAbort = null;
    batch.snapshot = null;
    dom.bulkDeleteSummary.textContent = '';
    dom.bulkDeleteItems.replaceChildren();
    dom.bulkDeleteResults.replaceChildren();
    dom.bulkDeleteConfirmation.value = '';
    dom.bulkDeleteExecute.disabled = true;
    dom.bulkDeletePreview.disabled = false;
    dom.bulkDeleteScope.disabled = false;
    dom.bulkDeleteConfirmation.disabled = false;
  }

  function openBulkDelete(returnFocus) {
    if (!['admin', 'super_admin'].includes(state.staff?.role)) return;
    const batch = { returnFocus, previewAbort: null, snapshot: null, submitting: false, ledger: [] };
    state.bulkDeleteState = batch;
    dom.bulkDeleteScope.replaceChildren();
    if (state.staff.role === 'super_admin') {
      dom.bulkDeleteScope.append(element('option', { value: '', text: 'Choose a library scope' }));
      dom.bulkDeleteScope.append(element('option', { value: 'all', text: 'All libraries' }));
      for (const option of dom.scope.options) {
        if (option.value === 'all') continue;
        dom.bulkDeleteScope.append(element('option', { value: option.value, text: option.textContent }));
      }
    } else {
      dom.bulkDeleteScope.append(element('option', {
        value: String(state.staff.organizationId), text: state.staff.organizationName || 'My library'
      }));
    }
    dom.bulkDeleteScope.value = state.staff.role === 'super_admin' ? '' : String(state.staff.organizationId);
    resetBulkDeletePreview();
    dom.bulkDeleteDialog.showModal();
    (state.staff.role === 'super_admin' ? dom.bulkDeleteScope : dom.bulkDeletePreview).focus();
  }

  function bulkItemLabel(item) {
    return (item.type === 'title_request' ? 'Title request ' : 'Additional-copy task ') +
      item.id + ' (' + item.libraryOrgName + ')';
  }

  function validateBulkItem(item, type, scope) {
    return item?.type === type && typeof item.id === 'string' && /^[1-9]\d*$/.test(item.id) &&
      typeof item.version === 'string' && item.version.length > 0 && item.status === 'closed' &&
      Number.isSafeInteger(item.libraryOrgId) &&
      (scope === 'all' || String(item.libraryOrgId) === scope);
  }

  async function previewBulkDelete() {
    const batch = state.bulkDeleteState;
    const scope = dom.bulkDeleteScope.value;
    resetBulkDeletePreview();
    if (!batch || !scope) {
      dom.bulkDeleteSummary.textContent = 'Choose a library scope before previewing.';
      dom.bulkDeleteScope.focus();
      return;
    }
    const controller = new AbortController();
    batch.previewAbort = controller;
    dom.bulkDeletePreview.disabled = true;
    dom.bulkDeleteSummary.textContent = 'Loading closed title requests and additional-copy tasks...';
    try {
      const session = await loadStaffSession({ signal: controller.signal });
      if (state.bulkDeleteState !== batch || controller.signal.aborted) return;
      if (!session.authenticated) {
        showSignedOut();
        return;
      }
      if (!session.accessAllowed) {
        showAccessUnavailable();
        return;
      }
      const previewActor = session.staff;
      if (!previewActor || previewActor.id !== state.staff?.id ||
          !['admin', 'super_admin'].includes(previewActor.role) ||
          (scope === 'all' && previewActor.role !== 'super_admin') ||
          (previewActor.role === 'admin' && scope !== String(previewActor.organizationId)) ||
          typeof previewActor.version !== 'string' || !previewActor.version) {
        throw new Error('Staff role or library scope changed. Reload the workspace before previewing deletion.');
      }
      const query = encodeURIComponent(scope);
      const [titles, copies] = await Promise.all([
        authorizedJson('/api/asap/staff/title-requests?scope=' + query, { signal: controller.signal }),
        authorizedJson('/api/asap/staff/additional-copies?scope=' + query + '&status=closed',
          { signal: controller.signal })
      ]);
      if (state.bulkDeleteState !== batch || controller.signal.aborted) return;
      if (titles.scope !== scope || copies.scope !== scope || copies.status !== 'closed' ||
          !Array.isArray(titles.items) || !Array.isArray(copies.items)) {
        throw new Error('The requested deletion scope could not be verified.');
      }
      const titleItems = titles.items.filter(item => item.status === 'closed');
      const copyItems = copies.items;
      if (titleItems.some(item => !validateBulkItem(item, 'title_request', scope)) ||
          copyItems.some(item => !validateBulkItem(item, 'additional_copy', scope))) {
        throw new Error('The closed-work preview contained an invalid identity or scope.');
      }
      batch.snapshot = {
        scope,
        actorVersion: previewActor.version,
        items: [...titleItems, ...copyItems].map(item => ({
          type: item.type, id: item.id, version: item.version,
          title: item.title, libraryOrgName: item.libraryOrgName,
          libraryOrgId: item.libraryOrgId, closeReason: item.closeReason
        }))
      };
      const scopeName = dom.bulkDeleteScope.selectedOptions[0]?.textContent || scope;
      dom.bulkDeleteSummary.textContent = 'Preview for ' + scopeName + ': ' +
        titleItems.length + ' closed title requests and ' + copyItems.length +
        ' closed additional-copy tasks. Search, claim, tag, and grid filters do not affect this population.';
      dom.bulkDeleteItems.replaceChildren(...batch.snapshot.items.map(item =>
        element('li', { text: bulkItemLabel(item) + ' — ' + item.title +
          (item.type === 'title_request' ? ' — ' + closeReasonLabel(item.closeReason) : '') })));
      if (batch.snapshot.items.length === 0) {
        dom.bulkDeleteSummary.textContent += ' There are no eligible records to submit.';
      }
      dom.bulkDeleteConfirmation.focus();
    } catch (error) {
      if (state.bulkDeleteState === batch && !controller.signal.aborted && error.status !== 401) {
        dom.bulkDeleteSummary.textContent = error.message || 'The closed-work preview could not be loaded.';
      }
    } finally {
      if (state.bulkDeleteState === batch) {
        batch.previewAbort = null;
        dom.bulkDeletePreview.disabled = false;
      }
    }
  }

  function bulkOutcome(error) {
    const code = error.response?.code;
    if (error.status === 404 || code === 'not_found') return 'not_found';
    if (code === 'stale_version') return 'stale';
    if (code === 'actor_changed_since_preview') return 'actor_changed';
    if (code === 'request_not_closed' || code === 'delete_requires_closed') return 'not_closed';
    if (error.status === 401 || error.status === 403 ||
        ['delete_forbidden', 'staff_scope_forbidden', 'staff_session_invalid'].includes(code)) {
      return 'forbidden/out_of_scope';
    }
    if (code === 'hold_history_retained') return 'blocked_hold_history';
    if (isAbortError(error) || error.status === 408 || error.status >= 500 || !error.status) {
      return 'outcome_unconfirmed';
    }
    return 'operational_failure';
  }

  function renderBulkLedger(batch) {
    const deleted = batch.ledger.filter(item => item.outcome === 'deleted').length;
    const attempted = batch.ledger.filter(item => item.outcome !== 'not_attempted').length;
    const summary = 'Confirmed deleted: ' + deleted + ' of ' + batch.snapshot.items.length +
      '. Attempted: ' + attempted + '. Every record is rechecked by the server.';
    dom.bulkDeleteResults.replaceChildren(
      element('p', { text: summary }),
      element('ul', {}, batch.ledger.map(item =>
        element('li', { text: bulkItemLabel(item) + ': ' + item.outcome.replaceAll('_', ' ') })))
    );
    state.partialSessionFailureMessage = summary + ' ' + batch.ledger.map(item =>
      bulkItemLabel(item) + ': ' + item.outcome.replaceAll('_', ' ')).join('; ') +
      '. Sign in again and refresh Closed work before retrying.';
    state.partialSessionFailureOwner = batch;
    state.partialSessionFailureDetailAvailable = false;
    state.partialSessionFailureAfterQueueSequence = null;
  }

  async function executeBulkDelete() {
    const batch = state.bulkDeleteState;
    if (!batch?.snapshot || batch.submitting || dom.bulkDeleteConfirmation.value !== 'DELETE' ||
        dom.bulkDeleteScope.value !== batch.snapshot.scope || batch.snapshot.items.length === 0) return;
    batch.submitting = true;
    dom.bulkDeleteExecute.disabled = true;
    dom.bulkDeletePreview.disabled = true;
    dom.bulkDeleteScope.disabled = true;
    dom.bulkDeleteConfirmation.disabled = true;
    let stop = false;
    for (const item of batch.snapshot.items) {
      if (stop || state.bulkDeleteState !== batch || !state.staff) {
        batch.ledger.push({ ...item, outcome: 'not_attempted' });
        continue;
      }
      const route = item.type === 'title_request'
        ? '/api/asap/staff/requests/' : '/api/asap/staff/additional-copies/';
      batch.currentItem = item;
      try {
        const result = await authorizedJson(route + encodeURIComponent(item.id),
          { method: 'DELETE', body: {
            version: item.version, actorVersion: batch.snapshot.actorVersion
          } });
        batch.ledger.push({ ...item, outcome: result?.deleted === true ? 'deleted' : 'outcome_unconfirmed' });
        if (result?.deleted !== true) stop = true;
      } catch (error) {
        if (state.bulkDeleteState !== batch) return;
        const outcome = bulkOutcome(error);
        batch.ledger.push({ ...item, outcome });
        stop = outcome === 'outcome_unconfirmed' || outcome === 'actor_changed' ||
          error.status === 401 || error.status === 403;
      }
      batch.currentItem = null;
      if (state.bulkDeleteState === batch) renderBulkLedger(batch);
    }
    if (state.bulkDeleteState !== batch || !state.staff) return;
    batch.submitting = false;
    renderBulkLedger(batch);
    state.scope = batch.snapshot.scope;
    dom.scope.value = state.scope;
    dom.additionalCopyScope.value = state.scope;
    state.status = 'closed';
    state.additionalCopyStatus = 'closed';
    for (const tab of dom.statusTabs) tab.setAttribute('aria-selected', String(tab.dataset.status === 'closed'));
    for (const tab of dom.additionalCopyStatusTabs) {
      tab.setAttribute('aria-selected', String(tab.dataset.copyStatus === 'closed'));
    }
    const queueRefreshed = await loadQueue({ skipDeepLink: true, silent: true });
    const copiesRefreshed = await loadAdditionalCopies({ skipDeepLink: true, silent: true });
    if (queueRefreshed === true && copiesRefreshed === true) clearCommittedSessionFallback(batch);
    if (state.bulkDeleteState === batch) {
      dom.bulkDeleteSummary.textContent = queueRefreshed === true && copiesRefreshed === true
        ? 'Deletion finished. Both Closed views were refreshed from the server.'
        : 'Deletion finished, but a Closed view could not refresh. Review the ledger and refresh before retrying.';
      dom.bulkDeleteResults.focus();
    }
  }

  function isCurrentAdditionalCopyCreation(mutation, pending) {
    return mutation.isCurrent() &&
      !!state.staff &&
      dom.createCopyDialog.open &&
      state.createCopyRequest === pending &&
      isCurrentDialogRequest(pending.request, 'title_request');
  }

  function showSignedOut(message) {
    if (state.bulkDeleteState) {
      state.bulkDeleteState.previewAbort?.abort();
      state.bulkDeleteState = null;
    }
    if (dom.bulkDeleteDialog.open) dom.bulkDeleteDialog.close();
    polarisLookup.close();
    latestLoads.begin('research-configuration').abort();
    state.verifiedBib = null;
    state.research = null;
    cancelDialogFocusReturn();
    cancelAssignmentCandidateLoad();
    cancelPickupOptionsLoad();
    cancelDialogMutationCompletion();
    cancelActionChoiceLoad();
    cancelAdditionalCopyCreationCompletion();
    closeStaffSuggestion({ focusButton: false, force: true });
    if (dom.dialog.open) dom.dialog.close();
    if (dom.createCopyDialog.open) dom.createCopyDialog.close();
    state.staff = null;
    state.selectedRequestId = null;
    state.selectedRequestType = null;
    state.selectedRequestVersion = null;
    state.returnFocus = null;
    state.createCopyRequest = null;
    state.createCopyReturnFocus = null;
    state.staffSuggestion = null;
    state.staffSuggestionReturnFocus = null;
    latestLoads.begin('queue').abort();
    latestLoads.begin('additional-copies').abort();
    latestLoads.begin('operations').abort();
    latestLoads.begin('operations-mutation').abort();
    latestLoads.begin('detail').abort();
    latestLoads.begin('additional-copy-detail').abort();
    latestLoads.begin('additional-copy-preview').abort();
    resetAnalytics();
    settingsController.signedOut();
    dom.signedOutMessage.textContent = state.partialSessionFailureMessage ||
      message || 'Sign in with your authorized library account.';
    dom.signedOut.hidden = false;
    dom.workspace.hidden = true;
    dom.sessionActions.hidden = true;
    announce('');
  }

  function clearCommittedSessionFallback(owner) {
    if (state.partialSessionFailureOwner !== owner || !state.staff) return;
    state.partialSessionFailureMessage = null;
    state.partialSessionFailureOwner = null;
    state.partialSessionFailureDetailAvailable = false;
    state.partialSessionFailureAfterQueueSequence = null;
  }

  function retainUnconfirmedOutcome(message, owner) {
    state.partialSessionFailureMessage = `${message} Sign in again to check the authoritative result before retrying.`;
    state.partialSessionFailureOwner = owner;
    state.partialSessionFailureDetailAvailable = false;
    state.partialSessionFailureAfterQueueSequence = null;
  }

  function showWorkspace(staff) {
    state.partialSessionFailureMessage = null;
    state.partialSessionFailureOwner = null;
    state.partialSessionFailureDetailAvailable = false;
    state.partialSessionFailureAfterQueueSequence = null;
    if (state.unconfirmedCopyCreationAwaitingRefresh) {
      state.unconfirmedCopyCreationAwaitingRefresh.reviewReady = false;
      state.unconfirmedCopyCreationAwaitingRefresh.reviewed = false;
      state.additionalCopyLoaded = false;
      dom.additionalCopyCreateReview.hidden = true;
    }
    state.staff = staff;
    state.scope = staff.role === 'super_admin' ? 'all' : String(staff.organizationId);
    dom.signedOut.hidden = true;
    dom.workspace.hidden = false;
    dom.sessionActions.hidden = false;
    dom.staffIdentity.textContent = staff.displayName || staff.userPrincipalName || 'Staff user';
    dom.staffIdentity.title = `${statusLabel(staff.role)} · ${staff.organizationName}`;
    dom.scopeField.hidden = staff.role !== 'super_admin';
    dom.additionalCopyScopeField.hidden = staff.role !== 'super_admin';
    dom.operationsTab.hidden = staff.role !== 'admin' && staff.role !== 'super_admin';
    updateBulkDeleteButtons();
    dom.operationsScopeField.hidden = staff.role !== 'super_admin';
    state.operationsScope = staff.role === 'super_admin' ? 'all' : String(staff.organizationId);
    dom.operationsScope.replaceChildren(element('option', {
      value: state.operationsScope,
      text: staff.role === 'super_admin' ? 'All libraries' : 'My library'
    }));
    dom.operationsScope.value = state.operationsScope;
    settingsController.setStaff(staff);
    dom.claim.value = staff.defaultMineUnclaimedFilter ? 'mine_unclaimed' : 'all';
    dom.additionalCopyClaim.value = staff.defaultMineUnclaimedFilter ? 'mine_unclaimed' : 'all';
    populateProfile(staff);
  }

  function showAccessUnavailable() {
    showSignedOut('Staff access is not currently available. Sign out or use a different authorized Microsoft account.');
    dom.sessionActions.hidden = false;
    dom.staffIdentity.textContent = 'Access unavailable';
    dom.staffIdentity.title = '';
  }

  function populateProfile(staff) {
    dom.notificationEmail.value = staff.notificationEmail || '';
    dom.weeklyEmail.value = staff.weeklyActionSummaryEmail || '';
    dom.weeklyEnabled.checked = staff.weeklyActionSummaryEnabled;
    dom.purchaseDefault.checked = staff.purchaseReminderDefault;
    dom.additionalCopyDefault.checked = staff.additionalCopyReminderDefault;
    dom.mineDefault.checked = staff.defaultMineUnclaimedFilter;
  }

  async function startSession() {
    const load = latestLoads.begin('session');
    try {
      const session = await loadStaffSession({ signal: load.signal });
      if (!load.isCurrent()) return;
      if (!session.authenticated) {
        showSignedOut();
        return;
      }
      if (session.accessAllowed === false) {
        showAccessUnavailable();
        return;
      }
      showWorkspace(session.staff);
      announce('Staff session ready.');
      const requestedStage = currentStageParameter();
      if (STATUS_LABELS[requestedStage]) {
        state.status = requestedStage;
        for (const tab of dom.statusTabs) {
          tab.setAttribute('aria-selected', String(tab.dataset.status === requestedStage));
        }
      }
      if (requestedStage === 'settings' && session.staff.role !== 'staff') {
        switchView('settings', false);
      } else if (requestedStage === 'operations' && session.staff.role !== 'staff') {
        switchView('operations', false);
        await loadOperations();
      } else if (requestedStage === 'additional_copies') {
        switchView('additional-copies', false);
        await loadAdditionalCopies();
      } else if (requestedStage === 'analytics') {
        switchView('analytics', false);
        await loadAnalytics(dom.analyticsContainer);
      } else {
        await loadQueue();
      }
    } catch (error) {
      if (!isAbortError(error)) showSignedOut('Staff access could not be loaded. Try signing in again.');
    } finally {
      latestLoads.finish('session', load.token);
    }
  }

  function populateScopes(organizations, selectedScope) {
    for (const select of [dom.scope, dom.additionalCopyScope]) {
      if (!select) continue;
      select.replaceChildren(element('option', { value: 'all', text: 'All libraries' }));
      for (const organization of organizations || []) {
        select.append(element('option', { value: organization.id, text: organization.name }));
      }
      select.value = selectedScope;
    }
    if (dom.operationsScope) {
      const operationScope = state.staff?.role === 'super_admin'
        ? state.operationsScope
        : String(state.staff?.organizationId || selectedScope);
      const knownScopes = new Set(['all', ...(organizations || []).map(item => String(item.id))]);
      const reconciledScope = knownScopes.has(String(operationScope)) ? String(operationScope) : String(selectedScope);
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
  }

  function populateTags() {
    const selected = dom.tag.value;
    const tags = [...new Set(state.requests.flatMap(request => request.workflowTags || []))]
      .sort((first, second) => first.localeCompare(second));
    dom.tag.replaceChildren(element('option', { value: 'all', text: 'All tags' }));
    for (const tag of tags) dom.tag.append(element('option', { value: tag, text: tag }));
    dom.tag.value = tags.includes(selected) ? selected : 'all';
  }

  async function loadQueue(options = {}) {
    if (!state.staff) return;
    const load = latestLoads.begin('queue');
    const queueSequence = ++state.queueLoadSequence;
    dom.refresh.disabled = true;
    if (!options.silent) announce('Loading authorized requests...');
    try {
      const scope = state.staff.role === 'super_admin' ? state.scope : String(state.staff.organizationId);
      const result = await authorizedJson(`/api/asap/staff/title-requests?scope=${encodeURIComponent(scope)}`, {
        signal: load.signal
      });
      if (!load.isCurrent()) return;
      state.requests = Array.isArray(result.items) ? result.items : [];
      state.scope = result.scope;
      if (state.staff.role === 'super_admin') populateScopes(result.organizations, result.scope);
      populateTags();
      renderGrid();
      if (state.partialSessionFailureDetailAvailable &&
          state.partialSessionFailureAfterQueueSequence !== null &&
          queueSequence > state.partialSessionFailureAfterQueueSequence) {
        state.partialSessionFailureMessage = null;
        state.partialSessionFailureOwner = null;
        state.partialSessionFailureDetailAvailable = false;
        state.partialSessionFailureAfterQueueSequence = null;
      }
      if (!options.silent) announce(`${state.requests.length} authorized requests loaded.`);
      if (!state.deepLinkHandled && !options.skipDeepLink) {
        state.deepLinkHandled = true;
        const deepLink = currentRequestParameter();
        if (deepLink) await openRequest(deepLink);
      }
      return true;
    } catch (error) {
      if (!load.isCurrent() || isAbortError(error) && load.signal.aborted) return;
      if (!options.silent && error.status !== 401) {
        announce(error.message || 'Requests could not be loaded.', 'error');
      }
      return false;
    } finally {
      if (load.isCurrent()) dom.refresh.disabled = false;
      latestLoads.finish('queue', load.token);
    }
  }

  function operationsQuery(scope = state.operationsScope) {
    return scope && scope !== 'all' ? `?organizationId=${encodeURIComponent(scope)}` : '';
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
    if (!state.staff || (state.staff.role !== 'admin' && state.staff.role !== 'super_admin')) return;
    const load = latestLoads.begin('operations');
    const requestedScope = state.operationsScope;
    dom.refreshOperations.disabled = true;
    if (!options.silent) announce('Loading workflow operations...');
    try {
      const query = operationsQuery(requestedScope);
      const [queue, email, organizationResult] = await Promise.all([
        authorizedJson(`/api/asap/staff/workflow/queues${query}`, { signal: load.signal }),
        authorizedJson(`/api/asap/staff/email-operations${query}`, { signal: load.signal }),
        state.staff.role === 'super_admin'
          ? authorizedJson('/api/asap/staff/organizations', { signal: load.signal })
          : Promise.resolve(null)
      ]);
      if (!load.isCurrent() || requestedScope !== state.operationsScope) return;
      if (state.staff.role === 'super_admin' && Array.isArray(organizationResult)) {
        populateScopes(organizationResult.filter(item => Number(item.id) > 1), requestedScope);
      }
      renderOperations({ queue, email });
      state.operationsLoaded = true;
      if (!options.silent) announce('Workflow operations loaded.');
    } catch (error) {
      if (load.isCurrent() && requestedScope === state.operationsScope &&
          !options.silent && !isAbortError(error) && error.status !== 401) {
        announce(error.message || 'Workflow operations could not be loaded.', 'error');
      }
    } finally {
      if (load.isCurrent()) dom.refreshOperations.disabled = false;
      latestLoads.finish('operations', load.token);
    }
  }

  async function runOperation(path, message) {
    const load = latestLoads.begin('operations-mutation');
    const requestedScope = state.operationsScope;
    const scopeQuery = operationsQuery();
    const query = scopeQuery ? `${path.includes('?') ? '&' : '?'}${scopeQuery.slice(1)}` : '';
    try {
      const result = await authorizedJson(`${path}${query}`, { method: 'POST', signal: load.signal });
      if (!load.isCurrent() || requestedScope !== state.operationsScope) return;
      await loadOperations({ silent: true });
      announce(result.manualRunId ? `${message} Run ${result.manualRunId} queued.` : message, 'success');
    } catch (error) {
      if (load.isCurrent() && requestedScope === state.operationsScope &&
          !isAbortError(error) && error.status !== 401) {
        announce(error.message || 'The operation could not be queued.', 'error');
      }
    } finally {
      latestLoads.finish('operations-mutation', load.token);
    }
  }

  async function retryEmail(row) {
    const load = latestLoads.begin('operations-mutation');
    const requestedScope = state.operationsScope;
    try {
      await authorizedJson(`/api/asap/staff/email-operations/${encodeURIComponent(row.id)}/retry`, {
        method: 'POST',
        body: { version: row.version },
        signal: load.signal
      });
      if (!load.isCurrent() || requestedScope !== state.operationsScope) return;
      await loadOperations({ silent: true });
      announce('Email retry queued.', 'success');
    } catch (error) {
      if (load.isCurrent() && requestedScope === state.operationsScope &&
          !isAbortError(error) && error.status !== 401) {
        announce(error.message || 'Email retry could not be queued.', 'error');
      }
    } finally {
      latestLoads.finish('operations-mutation', load.token);
    }
  }

  function filteredRequests() {
    const query = dom.search.value.trim().toLocaleLowerCase();
    const claim = dom.claim.value;
    const tag = dom.tag.value;
    return state.requests.filter(request => {
      if (request.status !== state.status) return false;
      const mine = request.claimedByStaffUserId === state.staff?.id;
      const unclaimed = !request.claimedByStaffUserId;
      if (claim === 'mine' && !mine) return false;
      if (claim === 'unclaimed' && !unclaimed) return false;
      if (claim === 'mine_unclaimed' && !mine && !unclaimed) return false;
      if (tag !== 'all' && !(request.workflowTags || []).includes(tag)) return false;
      if (!query) return true;
      return [request.title, request.author, request.nameFirst, request.nameLast,
        request.barcode, request.identifier, request.bibid, request.libraryOrgName]
        .filter(Boolean)
        .some(value => String(value).toLocaleLowerCase().includes(query));
    });
  }

  function renderGrid() {
    updateBulkDeleteButtons();
    const requests = filteredRequests();
    dom.summary.textContent = `${requests.length} ${statusLabel(state.status).toLocaleLowerCase()} request${requests.length === 1 ? '' : 's'}`;
    dom.empty.hidden = requests.length !== 0;
    const rows = requests.map(request => [
      request.title,
      'Title request',
      request.status === 'closed' ? closeReasonLabel(request.closeReason) : '—',
      [request.nameLast, request.nameFirst].filter(Boolean).join(', ') || request.barcode,
      request.libraryOrgName,
      request.workflowTags && request.workflowTags.length ? request.workflowTags.join(', ') : 'None',
      request.claimedByDisplayName || 'Unclaimed',
      dateTime(request.updated),
      request.id
    ]);
    if (!state.grid) {
      state.grid = new window.gridjs.Grid({
        columns: [
          { name: 'Title', width: '25%' },
          { name: 'Type', width: '110px' },
          { name: 'Close reason', width: '130px' },
          { name: 'Patron', width: '17%' },
          { name: 'Library', width: '14%' },
          { name: 'Tags', width: '16%' },
          {
            name: 'Claim',
            width: '13%',
            formatter: (cell, row) => {
              const request = state.requests.find(item => item.id === row.cells[8].data);
              const mine = request && request.claimedByStaffUserId === state.staff?.id;
              return window.gridjs.h('span', { className: `claim-label${mine ? ' mine' : ''}` }, cell);
            }
          },
          { name: 'Updated', width: '12%' },
          {
            name: 'Open',
            width: '74px',
            sort: false,
            formatter: id => window.gridjs.h('button', {
              type: 'button',
              className: 'grid-open',
              'aria-label': `Open request ${id}`,
              onClick: event => openRequest(String(id), event.currentTarget)
            }, [window.gridjs.h('i', { className: 'fa fa-chevron-right', 'aria-hidden': 'true' }), 'Open'])
          }
        ],
        data: rows,
        sort: true,
        pagination: { limit: 25, summary: true },
        language: { noRecordsFound: 'No requests match these filters.' }
      });
      state.grid.render(dom.grid);
    } else {
      state.grid.updateConfig({ data: rows }).forceRender();
    }
  }

  async function loadAdditionalCopies(options = {}) {
    if (!state.staff) return;
    const uncertaintyAtStart = state.unconfirmedCopyCreationAwaitingRefresh;
    const load = latestLoads.begin('additional-copies');
    dom.additionalCopyRefresh.disabled = true;
    if (!options.silent) announce('Loading authorized additional-copy tasks...');
    try {
      const scope = state.staff.role === 'super_admin' ? state.scope : String(state.staff.organizationId);
      const result = await authorizedJson(
        `/api/asap/staff/additional-copies?scope=${encodeURIComponent(scope)}&status=${encodeURIComponent(state.additionalCopyStatus)}`,
        { signal: load.signal }
      );
      if (!load.isCurrent()) return false;
      state.additionalCopies = Array.isArray(result.items) ? result.items : [];
      state.scope = result.scope;
      state.additionalCopyLoaded = true;
      if (state.staff.role === 'super_admin') populateScopes(result.availableLibraries, result.scope);
      const uncertainCreation = state.unconfirmedCopyCreationAwaitingRefresh;
      if (uncertainCreation && uncertaintyAtStart === uncertainCreation && result.status === 'open' &&
          (result.scope === 'all' || String(result.scope) === String(uncertainCreation.libraryOrgId))) {
        uncertainCreation.reviewReady = true;
        dom.additionalCopySearch.value = '';
        dom.additionalCopyClaim.value = 'all';
      } else if (uncertainCreation) {
        uncertainCreation.reviewReady = false;
      }
      renderAdditionalCopyGrid();
      if (!options.silent) announce(uncertainCreation?.reviewReady
        ? 'Open additional-copy tasks loaded. Review the matching tasks and acknowledge the review before trying again.'
        : `${state.additionalCopies.length} authorized additional-copy tasks loaded.`);
      if (!state.additionalCopyDeepLinkHandled && !options.skipDeepLink) {
        state.additionalCopyDeepLinkHandled = true;
        const deepLink = currentRequestParameter();
        if (deepLink) await openAdditionalCopy(deepLink);
      }
      return true;
    } catch (error) {
      if (!options.silent && !isAbortError(error) && error.status !== 401) {
        announce(error.message || 'Additional-copy tasks could not be loaded.', 'error');
      }
      return false;
    } finally {
      if (load.isCurrent()) dom.additionalCopyRefresh.disabled = false;
      latestLoads.finish('additional-copies', load.token);
    }
  }

  function filteredAdditionalCopies() {
    const query = dom.additionalCopySearch.value.trim().toLocaleLowerCase();
    const claim = dom.additionalCopyClaim.value;
    return state.additionalCopies.filter(request => {
      const mine = request.claimedByStaffUserId === state.staff?.id;
      const unclaimed = !request.claimedByStaffUserId;
      if (claim === 'mine' && !mine) return false;
      if (claim === 'unclaimed' && !unclaimed) return false;
      if (claim === 'mine_unclaimed' && !mine && !unclaimed) return false;
      if (!query) return true;
      return [request.title, request.author, request.bibid, request.identifier,
        request.publication, request.libraryOrgName, request.claimedByDisplayName]
        .filter(Boolean)
        .some(value => String(value).toLocaleLowerCase().includes(query));
    });
  }

  function renderAdditionalCopyGrid() {
    updateBulkDeleteButtons();
    const uncertainCreation = state.unconfirmedCopyCreationAwaitingRefresh;
    const reviewReady = Boolean(uncertainCreation?.reviewReady) && !uncertainCreation.reviewed &&
      state.additionalCopyStatus === 'open' &&
      (state.scope === 'all' || String(state.scope) === String(uncertainCreation.libraryOrgId));
    dom.additionalCopyCreateReview.hidden = !reviewReady;
    if (reviewReady) {
      const matching = state.additionalCopies.filter(item =>
        String(item.libraryOrgId) === String(uncertainCreation.libraryOrgId) &&
        String(item.bibid) === String(uncertainCreation.bibid));
      const ids = matching.map(item => item.id).join(', ');
      dom.additionalCopyCreateReviewSummary.textContent =
        matching.length === 0
          ? `Creation for BIB ${uncertainCreation.bibid} could not be confirmed. No matching open task is visible yet. Review the list before retrying; a retry will use the original request version so a completed earlier creation cannot be duplicated.`
          : `Creation for BIB ${uncertainCreation.bibid} could not be confirmed. Review the ${matching.length} matching open task${matching.length === 1 ? '' : 's'} for this library before creating another: ${ids}.`;
    }
    const requests = filteredAdditionalCopies();
    dom.additionalCopySummary.textContent = `${requests.length} ${state.additionalCopyStatus} task${requests.length === 1 ? '' : 's'}`;
    dom.additionalCopyEmpty.hidden = requests.length !== 0;
    const rows = requests.map(request => [
      request.title,
      request.bibid,
      request.libraryOrgName,
      request.formatLabel || request.format || 'Not recorded',
      request.claimedByDisplayName || 'Unclaimed',
      dateTime(request.updated),
      request.id
    ]);
    if (!state.additionalCopyGrid) {
      state.additionalCopyGrid = new window.gridjs.Grid({
        columns: [
          { name: 'Title', width: '27%' },
          { name: 'BIB ID', width: '14%' },
          { name: 'Library', width: '17%' },
          { name: 'Format', width: '14%' },
          {
            name: 'Claim',
            width: '14%',
            formatter: (cell, row) => {
              const request = state.additionalCopies.find(item => item.id === row.cells[6].data);
              const mine = request && request.claimedByStaffUserId === state.staff?.id;
              return window.gridjs.h('span', { className: `claim-label${mine ? ' mine' : ''}` }, cell);
            }
          },
          { name: 'Updated', width: '13%' },
          {
            name: 'Open',
            width: '74px',
            sort: false,
            formatter: id => window.gridjs.h('button', {
              type: 'button',
              className: 'grid-open additional-copy-open',
              'aria-label': `Open additional-copy task ${id}`,
              onClick: event => openAdditionalCopy(String(id), event.currentTarget)
            }, [window.gridjs.h('i', { className: 'fa fa-chevron-right', 'aria-hidden': 'true' }), 'Open'])
          }
        ],
        data: rows,
        sort: true,
        pagination: { limit: 25, summary: true },
        language: { noRecordsFound: 'No additional-copy tasks match these filters.' }
      });
      state.additionalCopyGrid.render(dom.additionalCopyGrid);
    } else {
      state.additionalCopyGrid.updateConfig({ data: rows }).forceRender();
    }
  }

  async function openAdditionalCopy(id, returnFocus) {
    if (!state.staff) return false;
    polarisLookup.close();
    latestLoads.begin('research-configuration').abort();
    state.research = null;
    state.verifiedBib = null;
    state.currentRequest = null;
    state.editControls = null;
    cancelDialogFocusReturn();
    cancelAssignmentCandidateLoad();
    cancelPickupOptionsLoad();
    cancelDialogMutationCompletion();
    cancelActionChoiceLoad();
    cancelAdditionalCopyPreviewLoad();
    cancelAdditionalCopyCreationCompletion();
    state.selectedRequestId = String(id);
    state.selectedRequestType = 'additional_copy';
    state.selectedRequestVersion = null;
    state.returnFocus = returnFocus || document.activeElement;
    const load = latestLoads.begin('additional-copy-detail');
    announce('Loading additional-copy details...');
    try {
      const request = await authorizedJson(`/api/asap/staff/additional-copies/${encodeURIComponent(id)}`, {
        signal: load.signal
      });
      if (!load.isCurrent() || state.selectedRequestId !== String(id) || state.selectedRequestType !== 'additional_copy') return false;
      state.selectedRequestId = request.id;
      replaceRequestParameter(request.id, true);
      renderAdditionalCopy(request);
      if (!dom.dialog.open) dom.dialog.showModal();
      dom.closeDialog.focus();
      announce(`Opened additional-copy task ${request.id}.`);
      return true;
    } catch (error) {
      if (load.isCurrent() && state.selectedRequestId === String(id) &&
          state.selectedRequestType === 'additional_copy' &&
          !(isAbortError(error) && load.signal.aborted) && error.status !== 401) {
        announce(error.status === 404 ? 'That additional-copy task is no longer available.' : error.message, 'error');
      }
      return false;
    } finally {
      latestLoads.finish('additional-copy-detail', load.token);
    }
  }

  function renderAdditionalCopy(request, { preserveDialogMutation = false } = {}) {
    cancelAssignmentCandidateLoad();
    cancelPickupOptionsLoad();
    cancelAdditionalCopyPreviewLoad();
    cancelAdditionalCopyCreationCompletion();
    if (!preserveDialogMutation) cancelDialogMutationCompletion();
    if (state.selectedRequestType === 'additional_copy' && String(state.selectedRequestId) === String(request.id)) {
      state.selectedRequestVersion = request.version;
    }
    dom.dialogTitle.textContent = request.title;
    dom.dialogKicker.textContent = `${request.libraryOrgName} · Additional copy ${request.id}`;
    const body = document.createDocumentFragment();
    body.append(
      element('div', { className: 'detail-meta' }, [
        element('span', { className: `status-badge${request.status === 'closed' ? ' closed' : ''}`, text: statusLabel(request.status) }),
        element('span', { text: `Updated ${dateTime(request.updated)}` }),
        element('span', { text: request.claimedByDisplayName ? `Claimed by ${request.claimedByDisplayName}` : 'Unclaimed' })
      ]),
      buildAdditionalCopyActionBar(request)
    );
    const details = element('dl', { className: 'detail-grid' });
    addDetail(details, 'BIB ID', request.bibid);
    addDetail(details, 'Format', request.formatLabel || request.format);
    addDetail(details, 'Author', request.author);
    addDetail(details, 'Identifier', request.identifier);
    addDetail(details, 'Publication', request.publication);
    addDetail(details, 'Created by', request.createdByUsername);
    addDetail(details, 'Created', dateTime(request.created));
    addDetail(details, 'Closed by', request.closedByUsername);
    addDetail(details, 'Closed', dateTime(request.closedAt));
    if (request.status === 'closed') addDetail(details, 'Close reason', 'No reason recorded');
    body.append(details);
    if (request.sourceTitleRequest) {
      body.append(element('a', {
        className: 'source-link',
        href: `/staff/?request=${encodeURIComponent(request.sourceTitleRequest)}`
      }, [icon('external-link'), 'Open source title request']));
    } else {
      body.append(element('p', { text: 'The source title request is no longer available.' }));
    }
    if (request.notes) {
      body.append(
        element('h3', { text: 'Notes' }),
        element('pre', { className: 'notes-history', text: request.notes })
      );
    }
    dom.dialogBody.replaceChildren(body);
  }

  function buildAdditionalCopyActionBar(request) {
    const bar = element('div', { className: 'action-bar', 'aria-label': 'Additional-copy actions' });
    if (request.capabilities?.canUnclaim && request.claimedByStaffUserId === state.staff?.id) {
      bar.append(commandButton('Unclaim', 'user-times', () => mutateAdditionalCopy(request, 'unclaim', 'Task unclaimed.')));
    } else if (request.capabilities?.canClearClaim && request.claimedByStaffUserId &&
               ['admin', 'super_admin'].includes(state.staff?.role)) {
      bar.append(commandButton('Clear claim', 'user-times', () => {
        if (confirmCurrent(request, 'additional_copy',
          `Clear ${request.claimedByDisplayName || 'another staff member'}'s claim? The task will remain open and unclaimed.`)) {
          mutateAdditionalCopy(request, 'clear-claim', 'Additional-copy claim cleared.');
        }
      }));
    } else if (request.capabilities?.canClaim) {
      bar.append(commandButton('Claim', 'user-plus', () => mutateAdditionalCopy(request, 'claim', 'Task claimed.'), 'primary-button'));
    }
    if (request.capabilities?.canAssign) {
      bar.append(commandButton('Assign', 'users', () => showAdditionalCopyAssignment(request)));
    }
    if (request.capabilities?.canClose) {
      bar.append(commandButton('Close task', 'check', () => {
        if (confirmCurrent(request, 'additional_copy',
          'Close this additional-copy task? It will leave the open work queue; the source patron hold will not change.')) {
          mutateAdditionalCopy(request, 'close', 'Additional-copy task closed.');
        }
      }, 'primary-button'));
    }
    if (request.capabilities?.canReopen) {
      bar.append(commandButton('Reopen task', 'undo', () => {
        if (confirmCurrent(request, 'additional_copy',
          'Reopen this additional-copy task? It will return to open work. An eligible retained claim stays; an invalid claim is cleared.')) {
          mutateAdditionalCopy(request, 'reopen', 'Additional-copy task reopened.');
        }
      }, 'primary-button'));
    }
    if (request.capabilities?.canDelete) {
      bar.append(commandButton('Permanently delete task', 'trash', () => {
        if (confirmCurrent(request, 'additional_copy',
          `Permanently delete closed additional-copy task ${request.id}? This cannot be undone. Its deletion audit will remain.`)) {
          mutateAdditionalCopy(request, 'delete', 'Additional-copy task deleted.');
        }
      }, 'danger-button'));
    }
    return bar;
  }

  async function mutateAdditionalCopy(request, operation, successMessage, extra = {}) {
    if (!isCurrentDialogRequest(request, 'additional_copy') ||
        state.dialogMutationInFlight === `copy:${request.id}:${request.version}`) return;
    const mutation = latestLoads.begin('dialog-mutation');
    state.dialogMutationInFlight = `copy:${request.id}:${request.version}`;
    announce('Saving additional-copy task...');
    try {
      const result = await authorizedJson(`/api/asap/staff/additional-copies/${request.id}${operation === 'delete' ? '' : `/${operation}`}`, {
        method: operation === 'delete' ? 'DELETE' : 'POST',
        body: { version: request.version,
          ...(operation === 'delete' ? { actorVersion: state.staff?.version } : {}), ...extra },
        signal: mutation.signal
      });
      if (!isCurrentDialogMutation(mutation, request, 'additional_copy')) return;
      if (operation === 'delete' ? result?.deleted !== true :
          !isCommittedRequestResponse(result, request.id)) {
        throw unconfirmedResponseError();
      }
      const notification = notificationOutcome(result.notificationStatus, result.notificationReason,
        operation === 'assign' ? 'Assignment notification' : 'Notification');
      const resultStatus = result.status || result.finalStatus;
      let message = `${successMessage}${resultStatus ? ` Final state: ${statusLabel(resultStatus)}.` : ''}${notification.text}`;
      let messageKind = notification.partial ? 'warning' : 'success';
      state.partialSessionFailureMessage = `${message} Sign in again to review ${operation === 'delete' ? 'the updated task list' : 'the committed task'}.`;
      state.partialSessionFailureOwner = mutation.token;
      state.partialSessionFailureDetailAvailable = Boolean(result.id);
      state.partialSessionFailureAfterQueueSequence = null;
      if (operation === 'delete') {
        closeDialog({ preserveMutation: true });
        announce(message, messageKind);
        const refreshed = await loadAdditionalCopies({ skipDeepLink: true, silent: true });
        if (refreshed === true) clearCommittedSessionFallback(mutation.token);
        if (refreshed === false && state.staff) announce(`${message} The task list could not refresh.`, 'warning');
        return;
      }
      let current = result.request || (result.id ? result : null);
      if (!current) {
        try {
          current = await authorizedJson(`/api/asap/staff/additional-copies/${encodeURIComponent(request.id)}`,
            { signal: mutation.signal });
        } catch (error) {
          if (error.status !== 401 && !isAbortError(error)) message += ' Details could not refresh.';
        }
      }
      if (current?.status && current.status !== resultStatus) {
        message = `${successMessage} Final state: ${statusLabel(current.status)}.${notification.text}`;
      }
      if (result.claimClearedReason) message += ` The retained claim was cleared (${result.claimClearedReason.replaceAll('_', ' ')}).`;
      if (state.partialSessionFailureOwner === mutation.token) {
        state.partialSessionFailureMessage = `${message} Sign in again to review the committed task.`;
      }
      if (!mutation.isCurrent() || !isCurrentDialogSelection(request, 'additional_copy')) return;
      if (current) {
        renderAdditionalCopy(current, { preserveDialogMutation: true });
      } else {
        state.selectedRequestVersion = null;
        dom.dialogBody.replaceChildren(element('p', { text: 'The action committed. Reload this task to review current details.' }));
      }
      dom.closeDialog.focus();
      announce(message, messageKind);
      if (state.dialogMutationInFlight === `copy:${request.id}:${request.version}`) {
        state.dialogMutationInFlight = null;
      }
      const refreshed = await loadAdditionalCopies({ skipDeepLink: true, silent: true });
      if (refreshed === true) clearCommittedSessionFallback(mutation.token);
      if (mutation.isCurrent() && isCurrentDialogSelection(request, 'additional_copy')) {
        if (refreshed === false) messageKind = 'warning';
        announce(refreshed === false ? `${message} The task list could not refresh.` : message, messageKind);
      }
    } catch (error) {
      const definiteNoCommit = error.status === 503 &&
        error.response?.code === 'notification_dependency_unavailable';
      const unconfirmedOutcome = !definiteNoCommit &&
        (isUnconfirmedMutationError(error, mutation.signal) || error.status === 408 || error.status >= 500);
      if (error.status === 409 || unconfirmedOutcome) {
        const message = unconfirmedOutcome
          ? 'The additional-copy action outcome could not be confirmed. Reload before trying again.'
          : error.message || 'The task changed. Review the refreshed version before trying again.';
        if (unconfirmedOutcome && isCurrentDialogMutation(mutation, request, 'additional_copy')) {
          retainUnconfirmedOutcome(message, mutation.token);
        }
        const refreshed = await loadAdditionalCopies({ skipDeepLink: true, silent: true });
        if (!isCurrentDialogMutation(mutation, request, 'additional_copy')) return;
        const detailLoaded = await openAdditionalCopy(request.id);
        if (unconfirmedOutcome && refreshed === true && detailLoaded === true) {
          clearCommittedSessionFallback(mutation.token);
        }
        if (isCurrentDialogSelection(request, 'additional_copy')) announce(message, 'error');
      } else if (isCurrentDialogMutation(mutation, request, 'additional_copy') &&
                 error.status !== 401 && !isAbortError(error)) {
        announce(error.message || 'The additional-copy task could not be updated.', 'error');
      }
    } finally {
      if (state.dialogMutationInFlight === `copy:${request.id}:${request.version}`) state.dialogMutationInFlight = null;
      latestLoads.finish('dialog-mutation', mutation.token);
    }
  }

  async function showAdditionalCopyAssignment(request) {
    const load = latestLoads.begin('assignment-candidates');
    announce('Loading eligible staff...');
    try {
      const result = await authorizedJson(`/api/asap/staff/assignment-candidates?libraryOrgId=${request.libraryOrgId}`, {
        signal: load.signal
      });
      if (!load.isCurrent() || !isCurrentDialogRequest(request, 'additional_copy')) return;
      const select = element('select', { 'aria-label': 'Assign additional-copy task' });
      const candidates = result.candidates || [];
      for (const candidate of candidates) {
        select.append(element('option', {
          value: candidate.id,
          text: candidate.displayName
        }));
      }
      const form = element('form', { className: 'inline-form' }, [
        labeledInput('Assign task', select),
        element('button', { type: 'submit', className: 'primary-button', disabled: candidates.length === 0 }, [icon('user-plus'), 'Assign'])
      ]);
      form.addEventListener('submit', async event => {
        event.preventDefault();
        if (!form.isConnected || !isCurrentDialogRequest(request, 'additional_copy')) return;
        await mutateAdditionalCopy(request, 'assign', 'Additional-copy task assigned.', {
          assigneeId: select.value
        });
      });
      dom.dialogBody.prepend(form);
      select.focus();
      announce(candidates.length ? 'Choose an assignee.' : 'No eligible staff are available.');
    } catch (error) {
      if (load.isCurrent() && isCurrentDialogRequest(request, 'additional_copy') &&
          error.status !== 401 && !isAbortError(error)) {
        announce(error.message || 'Assignable staff could not be loaded.', 'error');
      }
    } finally {
      latestLoads.finish('assignment-candidates', load.token);
    }
  }

  async function openRequest(id, returnFocus) {
    if (!state.staff) return;
    polarisLookup.close();
    latestLoads.begin('research-configuration').abort();
    state.research = null;
    if (String(state.selectedRequestId) !== String(id) || state.selectedRequestType !== 'title_request') {
      state.verifiedBib = null;
    }
    cancelDialogFocusReturn();
    cancelAssignmentCandidateLoad();
    cancelPickupOptionsLoad();
    cancelDialogMutationCompletion();
    cancelAdditionalCopyPreviewLoad();
    cancelAdditionalCopyCreationCompletion();
    state.selectedRequestId = String(id);
    state.selectedRequestType = 'title_request';
    state.selectedRequestVersion = null;
    state.returnFocus = returnFocus || document.activeElement;
    const load = latestLoads.begin('detail');
    announce('Loading request details...');
    try {
      const request = await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}`, {
        signal: load.signal
      });
      if (!load.isCurrent() || state.selectedRequestId !== String(id) || state.selectedRequestType !== 'title_request') return;
      const configuration = await loadRequestConfiguration(request.libraryOrgId, load.signal);
      if (!load.isCurrent() || state.selectedRequestId !== String(id) || state.selectedRequestType !== 'title_request') return;
      state.selectedRequestId = request.id;
      replaceRequestParameter(request.id, false);
      renderRequest(request, configuration);
      if (!dom.dialog.open) dom.dialog.showModal();
      dom.closeDialog.focus();
      announce(`Opened ${request.title}.`);
      loadResearchConfiguration(request);
      return true;
    } catch (error) {
      if (load.isCurrent() && state.selectedRequestId === String(id) &&
          state.selectedRequestType === 'title_request' &&
          !(isAbortError(error) && load.signal.aborted) && error.status !== 401) {
        announce(error.status === 404 ? 'That request is no longer available.' : error.message, 'error');
      }
      return false;
    } finally {
      latestLoads.finish('detail', load.token);
    }
  }

  async function loadRequestConfiguration(organizationId, signal) {
    const key = String(organizationId);
    if (state.configurations.has(key)) return state.configurations.get(key);
    const configuration = await authorizedJson(
      `/api/asap/config?libraryOrgId=${encodeURIComponent(key)}`,
      { signal }
    );
    state.configurations.set(key, configuration);
    return configuration;
  }

  function staffSuggestionScopeOptions() {
    if (state.staff?.role !== 'super_admin') {
      return [{
        value: String(state.staff?.organizationId || ''),
        text: state.staff?.organizationName || 'My library'
      }];
    }
    return [...(dom.scope?.options || [])]
      .filter(option => option.value && option.value !== 'all')
      .map(option => ({ value: option.value, text: option.textContent || option.value }));
  }

  function setStaffSuggestionStatus(message, kind = '') {
    dom.staffSuggestionStatus.textContent = message || '';
    dom.staffSuggestionStatus.className = `dialog-status${kind ? ` ${kind}` : ''}`;
  }

  function renderStaffSuggestionSearch({ query = '', message = '', kind = '', scopeId = null } = {}) {
    const current = state.staffSuggestion || {};
    const options = staffSuggestionScopeOptions();
    const scope = element('select', {
      required: 'required',
      'aria-label': 'Servicing library',
      disabled: state.staff?.role !== 'super_admin'
    });
    if (state.staff?.role === 'super_admin') {
      scope.append(element('option', { value: '', text: 'Choose a servicing library' }));
    }
    for (const option of options) {
      scope.append(element('option', { value: option.value, text: option.text }));
    }
    const selectedScope = scopeId || current.scopeId ||
      (state.staff?.role === 'super_admin' ? '' : String(state.staff?.organizationId || ''));
    if (selectedScope && options.some(option => String(option.value) === String(selectedScope))) {
      scope.value = String(selectedScope);
    }
    const queryInput = element('input', {
      type: 'search',
      autocomplete: 'off',
      spellcheck: 'false',
      maxlength: '200',
      value: query,
      placeholder: 'Barcode or patron name',
      'aria-label': 'Patron barcode or name'
    });
    const help = element('small', {
      text: state.staff?.role === 'super_admin'
        ? 'Select the library that will own this request. Queue scope is not used as a default.'
        : 'Search by barcode or name. Name matches are refreshed against current Polaris data before selection.'
    });
    const fields = element('div', { className: 'staff-suggestion-search-fields' }, [
      labeledInput('Servicing library', scope),
      labeledInput('Patron barcode or name', queryInput),
      help
    ]);
    dom.staffSuggestionBody.replaceChildren(fields);
    dom.staffSuggestionActions.replaceChildren(
      element('button', { type: 'button', className: 'secondary-button', onclick: () => closeStaffSuggestion() }, 'Cancel'),
      element('button', { type: 'submit', className: 'primary-button' }, [icon('search'), 'Look up patron'])
    );
    state.staffSuggestion = {
      ...current,
      stage: 'lookup',
      scopeId: scope.value || selectedScope || null,
      submitting: false,
      controls: { scope, queryInput }
    };
    scope.addEventListener('change', () => {
      cancelStaffSuggestionLookup();
      cancelStaffSuggestionConfiguration();
      state.staffSuggestion = {
        ...state.staffSuggestion,
        stage: 'lookup',
        scopeId: scope.value || null,
        verifiedBibId: null
      };
      setStaffSuggestionStatus(scope.value ? 'Library selected. Look up the patron to continue.' : 'Choose a servicing library.', '');
    });
    queryInput.addEventListener('input', () => {
      cancelStaffSuggestionLookup();
      cancelStaffSuggestionConfiguration();
    });
    setStaffSuggestionStatus(message, kind);
  }

  function renderStaffSuggestionMatches(result, query, scopeId) {
    renderStaffSuggestionSearch({
      query,
      scopeId,
      message: 'More than one patron matched. Choose a candidate to refresh authoritative details.',
      kind: ''
    });
    const list = element('div', { className: 'staff-suggestion-matches' });
    list.append(element('h3', { text: 'Choose a patron' }));
    const candidates = element('div', { className: 'staff-suggestion-candidate-list' });
    for (const match of result.matches || []) {
      candidates.append(element('button', {
        type: 'button',
        className: 'staff-suggestion-candidate',
        onclick: () => lookupStaffPatron(null, match.barcode)
      }, [
        element('strong', { text: match.name || 'Patron' }),
        element('span', { text: `${match.barcode} · Home library ${match.homeLibraryOrganizationName || match.homeLibraryOrganizationId}` })
      ]));
    }
    list.append(candidates);
    dom.staffSuggestionBody.append(list);
  }

  async function lookupStaffPatron(queryOverride = null, barcodeOverride = null) {
    if (!state.staff || !state.staffSuggestion) return;
    const current = state.staffSuggestion;
    const controls = current.controls;
    const scopeValue = controls?.scope?.value || current.scopeId;
    const scopeId = Number(scopeValue);
    const query = queryOverride === null
      ? controls?.queryInput?.value.trim() || ''
      : String(queryOverride).trim();
    const barcode = barcodeOverride === null ? null : String(barcodeOverride).trim();
    if (!Number.isInteger(scopeId) || scopeId <= 1) {
      setStaffSuggestionStatus('Choose a participating servicing library first.', 'error');
      controls?.scope?.focus();
      return;
    }
    if (!barcode && !query) {
      setStaffSuggestionStatus('Enter a patron barcode or name.', 'error');
      controls?.queryInput?.focus();
      return;
    }

    const load = latestLoads.begin('staff-suggestion-lookup');
    state.staffSuggestion = { ...current, scopeId: String(scopeId), stage: 'lookup', submitting: false };
    setStaffSuggestionStatus(barcode ? 'Refreshing the selected patron...' : 'Looking up the patron...');
    const submit = dom.staffSuggestionActions.querySelector('button[type="submit"]');
    if (submit) submit.disabled = true;
    let configurationLoad = null;
    try {
      const result = await authorizedJson('/api/asap/staff/patron-lookup', {
        method: 'POST',
        body: { query: barcode ? null : query, barcode, libraryOrgId: scopeId },
        signal: load.signal
      });
      if (!load.isCurrent() || state.staffSuggestion?.scopeId !== String(scopeId)) return;
      if (result.status === 'multiple_matches') {
        renderStaffSuggestionMatches(result, query, String(scopeId));
        return;
      }
      if (result.status === 'ineligible') {
        renderStaffSuggestionSearch({
          query,
          scopeId: String(scopeId),
          message: result.message || 'The matching patron is not eligible for this servicing library.',
          kind: 'error'
        });
        return;
      }
      if (result.status !== 'verified' || !result.patron) {
        renderStaffSuggestionSearch({
          query,
          scopeId: String(scopeId),
          message: result.message || 'No patron matched that search.',
          kind: 'error'
        });
        return;
      }
      configurationLoad = latestLoads.begin('staff-suggestion-configuration');
      const configured = await authorizedJson(
        `/api/asap/staff/suggestion-configuration?libraryOrgId=${encodeURIComponent(scopeId)}`,
        { signal: configurationLoad.signal });
      if (!load.isCurrent() || !configurationLoad.isCurrent() ||
          state.staffSuggestion?.scopeId !== String(scopeId)) return;
      if (configured.libraryOrgId !== scopeId) return;
      renderStaffSuggestionForm(result, configured.configuration);
    } catch (error) {
      if (!load.isCurrent() || isAbortError(error) || error.status === 401) return;
      const message = error.response?.message || error.message || 'Patron lookup could not be completed.';
      renderStaffSuggestionSearch({ query, scopeId: String(scopeId), message, kind: 'error' });
    } finally {
      if (configurationLoad) {
        latestLoads.finish('staff-suggestion-configuration', configurationLoad.token);
      }
      latestLoads.finish('staff-suggestion-lookup', load.token);
    }
  }

  function collectStaffCustomFields(controls) {
    const result = {};
    for (const [key, value] of controls || []) {
      if (value.mode === 'hidden') continue;
      const normalized = value.input.value.trim();
      if (normalized) result[key] = normalized;
    }
    return result;
  }

  function renderStaffSuggestionForm(result, configuration) {
    const context = result.patron;
    const patron = context.patron;
    const fakeRequest = { customFields: {} };
    const availableFormats = Array.isArray(configuration.availableFormats)
      ? configuration.availableFormats : [];
    const format = selectWithHistorical(availableFormats, null, configuration.formatLabels || {});
    format.setAttribute('aria-label', 'Material format');
    format.required = true;
    const title = element('input', { maxlength: '500', autocomplete: 'off' });
    const author = element('input', { maxlength: '500', autocomplete: 'off' });
    const identifier = element('input', { maxlength: '100', autocomplete: 'off' });
    const publication = selectWithHistorical(configuration.publicationOptions, null);
    publication.prepend(element('option', { value: '', text: 'Not specified' }));
    publication.value = '';
    const exactDate = element('input', { type: 'date' });
    const notes = element('textarea', { maxlength: '10000' });
    const autohold = element('input', { type: 'checkbox', checked: true });
    const emailConfirmation = element('input', { type: 'checkbox' });
    const pickup = element('select', { required: 'required', 'aria-label': 'Preferred pickup location' });
    const branches = Array.isArray(context.pickupBranches) ? context.pickupBranches : [];
    for (const branch of branches) pickup.append(element('option', { value: branch.id, text: branch.label }));
    if (context.currentPreferredPickupBranchId &&
        branches.some(branch => branch.id === context.currentPreferredPickupBranchId)) {
      pickup.value = String(context.currentPreferredPickupBranchId);
    } else {
      pickup.value = '';
      pickup.prepend(element('option', { value: '', text: 'Choose a pickup location' }));
    }
    const customFields = element('div', { className: 'custom-fields wide' });
    const formatNotice = element('div', { className: 'staff-format-notice field-help wide', hidden: true });
    const titleField = labeledInput('Title', title);
    const authorField = labeledInput('Author', author);
    const identifierField = labeledInput('Identifier / ISBN', identifier);
    const publicationField = labeledInput('Publication timing', publication);
    const exactDateField = labeledInput('Exact publication date', exactDate);
    const pickupField = labeledInput('Preferred pickup location', pickup);
    const pickupHelp = element('small', {
      className: 'field-help',
      text: "Changing this updates the patron's preferred pickup location in Polaris."
    });
    pickupField.append(pickupHelp);

    const bib = element('input', { type: 'hidden' });
    const catalogStatus = element('p', { className: 'field-help', role: 'status', 'aria-live': 'polite' });
    const catalogButton = element('button', {
      type: 'button',
      className: 'secondary-button',
      onclick: () => polarisLookup.open({
        requestId: null,
        libraryOrgId: Number(state.staffSuggestion?.scopeId),
        mode: 'title',
        query: title.value,
        title: title.value,
        author: author.value,
        canApply: true,
        isCurrent: () => dom.staffSuggestionDialog.open &&
          state.staffSuggestion?.stage === 'create',
        apply: selected => {
          applyPolarisResultToControls(selected, { bib, title, author, identifier });
          state.staffSuggestion.verifiedBibId = String(selected.bibId);
          catalogStatus.textContent = `Verified Polaris BIB ${selected.bibId} selected.`;
        },
        editorFocus: title
      })
    }, [icon('search'), 'Search Polaris catalog']);
    const catalogPanel = element('section', {
      className: 'staff-suggestion-catalog wide',
      'aria-label': 'Polaris catalog lookup'
    }, [
      element('p', { text: 'Use the integrated Polaris search to verify a catalog record and fill the suggestion fields. Manual text is not treated as a verified BIB.' }),
      catalogButton,
      catalogStatus
    ]);

    const controls = {
      format, title, author, identifier, publication, exactDate, notes,
      autohold, emailConfirmation, pickup, bib,
      customFieldControls: new Map(),
      titleField, authorField, identifierField, publicationField, exactDateField,
      catalogStatus
    };
    const applyFieldRule = (field, input, key, fallbackLabel, forceRequired = false) => {
      const rule = configuration.formatRules?.[format.value]?.fields?.[key] || {};
      const hidden = !forceRequired && rule.mode === 'hidden';
      field.hidden = hidden;
      input.disabled = hidden;
      input.required = forceRequired || rule.mode === 'required';
      input.setAttribute('aria-required', String(input.required));
      const label = rule.label || fallbackLabel;
      field.firstElementChild.textContent = `${label}${input.required ? ' *' : ''}`;
    };
    let submitButton;
    const updateFormat = () => {
      applyFieldRule(titleField, title, 'title', 'Title', true);
      applyFieldRule(authorField, author, 'author', 'Author');
      applyFieldRule(identifierField, identifier, 'identifier', 'Identifier / ISBN');
      applyFieldRule(publicationField, publication, 'publication', 'Publication timing');
      exactDateField.hidden = publicationField.hidden;
      exactDate.disabled = publicationField.hidden;
      controls.customFieldControls = renderCustomFieldEditor(customFields, fakeRequest, configuration, format.value);
      const formatRule = configuration.formatRules?.[format.value];
      const behavior = formatRule?.messageBehavior;
      const message = behavior === 'ebookMessage'
        ? configuration.ebookMessage
        : behavior === 'eaudiobookMessage' ? configuration.eaudiobookMessage : formatRule?.message;
      formatNotice.replaceChildren();
      if (message) formatNotice.append(sanitizedHtmlFragment(message));
      formatNotice.hidden = !message;
      if (submitButton) submitButton.disabled = branches.length === 0;
    };
    format.addEventListener('change', updateFormat);

    const patronCard = element('section', { className: 'staff-patron-card', 'aria-labelledby': 'staff-patron-card-title' }, [
      element('h3', { id: 'staff-patron-card-title', text: patron.name || 'Verified patron' }),
      element('p', { text: `${patron.barcode} · Home library: ${patron.homeLibraryOrganizationName}` }),
      element('p', { text: context.email || 'No patron email is recorded.' })
    ]);
    const scopeNote = result.searchLibraryLimited
      ? element('p', { className: 'field-help wide', text: `Patron is being serviced by ${result.libraryOrgName}.` })
      : null;
    const fields = element('div', { className: 'edit-form staff-suggestion-fields' }, [
      patronCard,
      scopeNote,
      labeledInput('Material format', format),
      formatNotice,
      catalogPanel,
      titleField,
      authorField,
      identifierField,
      publicationField,
      exactDateField,
      pickupField,
      customFields,
      element('label', { className: 'check-field' }, [autohold, element('span', { text: 'Automatically place hold' })]),
      element('label', { className: 'check-field' }, [emailConfirmation, element('span', { text: 'Email patron confirmation (optional)' })]),
      labeledInput('Staff notes', notes, 'wide'),
      bib
    ]);
    dom.staffSuggestionBody.replaceChildren(fields);
    const changeButton = element('button', {
      type: 'button',
      className: 'secondary-button',
      onclick: () => renderStaffSuggestionSearch({ query: patron.barcode, scopeId: String(result.libraryOrgId) })
    }, 'Change patron');
    submitButton = element('button', { type: 'submit', className: 'primary-button' }, [icon('send'), 'Create suggestion']);
    dom.staffSuggestionActions.replaceChildren(changeButton, submitButton);
    controls.changeButton = changeButton;
    state.staffSuggestion = {
      ...state.staffSuggestion,
      stage: 'create',
      result,
      configuration,
      controls,
      verifiedBibId: null,
      submitting: false
    };
    updateFormat();
    setStaffSuggestionStatus(
      branches.length ? 'Patron verified. Complete the configured fields, then create the suggestion.'
        : 'Pickup locations are unavailable; the suggestion cannot be created.',
      branches.length ? '' : 'error');
    if (availableFormats.length === 0) {
      setStaffSuggestionStatus('No enabled material formats are available for this library.', 'error');
      submitButton.disabled = true;
    }
    title.focus();
  }

  async function createStaffSuggestion() {
    const current = state.staffSuggestion;
    if (!current || current.stage !== 'create' || current.submitting) return;
    if (!dom.staffSuggestionForm.reportValidity()) return;
    current.submitting = true;
    const controls = current.controls;
    const result = current.result;
    const mutation = latestLoads.begin('staff-suggestion-mutation');
    const submit = dom.staffSuggestionActions.querySelector('button[type="submit"]');
    if (submit) submit.disabled = true;
    if (controls.changeButton) controls.changeButton.disabled = true;
    setStaffSuggestionStatus('Creating the suggestion...');
    try {
      const created = await authorizedJson('/api/asap/staff/suggestions', {
        method: 'POST',
        body: {
          libraryOrgId: Number(current.scopeId),
          barcode: result.patron.patron.barcode,
          format: controls.format.value || null,
          title: controls.title.disabled ? '' : controls.title.value,
          author: controls.author.disabled ? '' : controls.author.value,
          identifier: controls.identifier.disabled ? '' : controls.identifier.value,
          publication: controls.publication.disabled ? null : controls.publication.value || null,
          exactPublicationDate: controls.exactDate.disabled ? null : controls.exactDate.value || null,
          notes: controls.notes.value,
          preferredPickupBranchId: Number(controls.pickup.value) || null,
          currentPreferredPickupBranchIdAtLoad: result.patron.currentPreferredPickupBranchId,
          currentPreferredPickupBranchObservedAtLoad: true,
          autohold: controls.autohold.checked,
          emailPatronConfirmation: controls.emailConfirmation.checked,
          customFields: collectStaffCustomFields(controls.customFieldControls),
          verifiedBibId: current.verifiedBibId || null
        },
        signal: mutation.signal
      });
      if (!mutation.isCurrent() || state.staffSuggestion !== current) return;
      const id = String(created.id);
      const targetLibraryId = String(created.libraryOrgId || current.scopeId);
      current.submitting = false;
      closeStaffSuggestion({ focusButton: false, force: true });
      if (state.staff?.role === 'super_admin') {
        state.scope = targetLibraryId;
        dom.scope.value = targetLibraryId;
      }
      try {
        await loadQueue({ skipDeepLink: true, silent: true });
      } catch {
        // Queue refresh is follow-up presentation work. The create response is already
        // authoritative and must remain a visible success even if the refresh races.
      }
      try {
        await openRequest(id, dom.newSuggestion);
      } catch {
        // The authoritative create response remains the success path if the queue refresh races.
      }
      const notification = created.notificationStatus === 'queued'
        ? 'Confirmation email queued.'
        : created.notificationStatus === 'suppressed'
          ? 'No confirmation email was sent because delivery is suppressed.'
          : 'No confirmation email was requested.';
      announce(`Suggestion ${id} created on behalf of the patron. ${notification}`, 'success');
    } catch (error) {
      if (!mutation.isCurrent() || isAbortError(error) || error.status === 401) return;
      dom.staffSuggestionBody.querySelector('.staff-suggestion-conflict')?.remove();
      const duplicateId = error.response?.duplicate?.id;
      const partialPickupChange = error.response?.code === 'request_not_created_pickup_changed' &&
        error.response?.pickupPreferenceChanged === true;
      if (error.status === 409 && typeof duplicateId === 'string' && /^\d+$/.test(duplicateId)) {
        const matchDescription = {
          bibid: 'catalog BIB',
          identifier: 'identifier',
          title_format: 'title and format'
        }[error.response?.duplicate?.matchType] || 'request details';
        dom.staffSuggestionBody.append(element('div', { className: 'staff-suggestion-conflict' }, [
          element('strong', { text: partialPickupChange ? 'Pickup changed; existing suggestion found' : 'Existing suggestion found' }),
          element('span', { text: `Request ${duplicateId} already matches this patron by ${matchDescription}.` }),
          element('button', {
            type: 'button',
            className: 'secondary-button',
            onclick: async () => {
              closeStaffSuggestion({ focusButton: false });
              await openRequest(duplicateId);
            }
          }, 'Open existing request')
        ]));
        setStaffSuggestionStatus(error.response?.message || error.message || 'This patron already has this suggestion.', 'error');
      } else {
        setStaffSuggestionStatus(error.response?.message || error.message || 'The suggestion could not be created.', 'error');
      }
    } finally {
      if (mutation.isCurrent() && state.staffSuggestion === current) {
        current.submitting = false;
        if (submit && submit.isConnected) submit.disabled = false;
        if (controls.changeButton?.isConnected) controls.changeButton.disabled = false;
      }
      latestLoads.finish('staff-suggestion-mutation', mutation.token);
    }
  }

  async function submitStaffSuggestion(event) {
    event.preventDefault();
    if (state.staffSuggestion?.stage === 'create') {
      await createStaffSuggestion();
    } else {
      await lookupStaffPatron();
    }
  }

  function openStaffSuggestion(returnFocus = null) {
    if (!state.staff) return;
    if (!closeStaffSuggestion({ focusButton: false })) return;
    state.staffSuggestionReturnFocus = returnFocus || document.activeElement;
    state.staffSuggestion = {
      scopeId: state.staff.role === 'super_admin' ? null : String(state.staff.organizationId),
      stage: 'lookup'
    };
    renderStaffSuggestionSearch();
    if (!dom.staffSuggestionDialog.open) dom.staffSuggestionDialog.showModal();
    window.requestAnimationFrame(() => {
      if (!dom.staffSuggestionDialog.open) return;
      const control = state.staffSuggestion?.controls;
      (state.staff.role === 'super_admin' && !control?.scope?.value ? control?.scope : control?.queryInput)?.focus();
    });
    announce('Look up a patron to start a new suggestion.');
  }

  function addDetail(list, label, value) {
    const wrapper = element('div');
    wrapper.append(element('dt', { text: label }), element('dd', { text: text(value) }));
    list.append(wrapper);
  }

  function renderRequest(request, configuration, { preserveDialogMutation = false } = {}) {
    cancelAssignmentCandidateLoad();
    cancelPickupOptionsLoad();
    cancelAdditionalCopyPreviewLoad();
    cancelAdditionalCopyCreationCompletion();
    if (!preserveDialogMutation) cancelDialogMutationCompletion();
    cancelActionChoiceLoad();
    if (state.selectedRequestType === 'title_request' && String(state.selectedRequestId) === String(request.id)) {
      state.selectedRequestVersion = request.version;
    }
    state.currentRequest = request;
    if (state.verifiedBib && (state.verifiedBib.requestId !== String(request.id) ||
        state.verifiedBib.version !== request.version ||
        state.verifiedBib.identifier !== String(request.identifier || '').trim() ||
        state.verifiedBib.bibId !== String(request.bibid || '').trim())) {
      state.verifiedBib = null;
    }
    dom.dialogTitle.textContent = request.title;
    dom.dialogKicker.textContent = `${request.libraryOrgName} · Request ${request.id}`;
    const body = document.createDocumentFragment();
    const meta = element('div', { className: 'detail-meta' }, [
      element('span', { className: `status-badge${request.status === 'closed' ? ' closed' : ''}`, text: statusLabel(request.status) }),
      element('span', { text: `Phase entered ${dateTime(request.phaseEnteredAt)}` }),
      element('span', { text: request.claimedByDisplayName ? `Claimed by ${request.claimedByDisplayName}` : 'Unclaimed' })
    ]);
    body.append(meta, buildActionBar(request));

    if (request.capabilities && request.capabilities.blockingReason) {
      body.append(element('p', {
        className: 'blocked-callout',
        text: request.capabilities.blockingReason === 'hold_operation_incomplete'
          ? 'Workflow-changing edits are blocked while hold placement needs recovery.'
          : request.capabilities.blockingReason === 'hold_history_retained'
          ? 'Reopen is unavailable because placed-hold history has no confirmed external reversal.'
          : 'Identifier and BIB changes are locked by this request’s placement history.'
      }));
    }

    const details = element('dl', { className: 'detail-grid' });
    addDetail(details, 'Patron', [request.nameFirst, request.nameLast].filter(Boolean).join(' '));
    addDetail(details, 'Barcode', request.barcode);
    addDetail(details, 'Email', request.email);
    addDetail(details, 'Format', request.formatLabel || request.format);
    addDetail(details, 'Identifier', request.identifier);
    addDetail(details, 'BIB ID', request.bibid);
    addDetail(details, 'Publication', request.publication);
    addDetail(details, 'Pickup', request.preferredPickupBranchName || request.preferredPickupBranchId);
    addDetail(details, 'Identifier check', request.isbnCheckStatus);
    if (request.status === 'closed') addDetail(details, 'Close reason', closeReasonLabel(request.closeReason));
    body.append(details);

    if (request.workflowTags && request.workflowTags.length) {
      const tags = element('div', { className: 'tags', 'aria-label': 'Workflow tags' });
      for (const tag of request.workflowTags) tags.append(element('span', { className: 'tag', text: tag }));
      body.append(tags);
    }
    body.append(buildEditForm(request, configuration));
    body.append(renderActivity(request.activity));
    body.append(element('section', { className: 'research-section', hidden: 'hidden' }));
    if (request.holdOperation) body.append(buildHoldOperation(request, request.holdOperation));
    dom.dialogBody.replaceChildren(body);
    updateResearchLinks();
  }

  function renderActivity(activity) {
    const section = element('section', { className: 'request-activity', 'aria-label': 'Request activity' });
    section.append(element('h3', { text: 'Activity' }));
    if (!Array.isArray(activity) || activity.length === 0) {
      section.append(element('p', { text: 'No recorded activity.' }));
      return section;
    }
    const list = element('ol');
    for (const item of activity) {
      const type = text(item.eventType, 'Event').replaceAll('_', ' ');
      const actor = item.actorName || (item.actorType === 'system' ? 'System' : 'Actor not recorded');
      list.append(element('li', { 'data-event-id': String(item.id) }, [
        element('strong', { text: type }),
        element('span', { text: item.message ? ` ${item.message}` : '' }),
        element('small', { text: `${actor} · ${dateTime(item.created)}` })
      ]));
    }
    section.append(list);
    return section;
  }

  function buildActionBar(request) {
    const bar = element('div', { className: 'action-bar', 'aria-label': 'Request actions' });
    const workflowBlocked = request.capabilities?.canChangeWorkflowState !== true;
    if (request.status !== 'closed') {
      if (request.claimedByStaffUserId === state.staff?.id) {
        bar.append(commandButton('Unclaim', 'user-times', () => mutateSimple(request, 'unclaim')));
      } else if (!request.claimedByStaffUserId) {
        bar.append(commandButton('Claim', 'user-plus', () => mutateSimple(request, 'claim'), 'primary-button'));
      } else if (['admin', 'super_admin'].includes(state.staff?.role)) {
        bar.append(commandButton('Clear claim', 'user-times', () => {
          if (confirmCurrent(request, 'title_request',
            `Clear ${request.claimedByDisplayName || 'another staff member'}'s claim? The request will remain in ${statusLabel(request.status)} and become unclaimed.`)) {
            mutateSimple(request, 'clear-claim');
          }
        }));
      }
      bar.append(commandButton('Assign', 'users', () => showAssignment(request)));
    }
    if (request.status === 'suggestion') {
      bar.append(
        commandButton('Purchase', 'shopping-cart', event => showActionChoice(request, 'purchase', event.currentTarget), 'primary-button', workflowBlocked),
        commandButton('Already own', 'book', () => runAction(request, 'alreadyOwn'), 'secondary-button', workflowBlocked),
        commandButton('Reject', 'ban', event => showActionChoice(request, 'reject', event.currentTarget), 'danger-button', workflowBlocked),
        commandButton('Close silently', 'archive', () => runAction(request, 'silentClose'), 'secondary-button', workflowBlocked)
      );
    } else if (request.status === 'outstanding_purchase') {
      bar.append(commandButton('Ready for hold', 'arrow-right', () => runAction(request, 'catalogFound'),
        'primary-button', workflowBlocked));
    } else if (request.status === 'pending_hold') {
      bar.append(commandButton('Additional copy', 'clone', event => showAdditionalCopyPreview(request, event.currentTarget), 'secondary-button', !request.bibid));
      bar.append(commandButton('Pickup', 'map-marker', () => showPickup(request), 'secondary-button', workflowBlocked));
      if (request.autohold && request.bibid && request.bibidStaffVerified === true) {
        bar.append(commandButton('Place hold', 'bookmark', () => {
          if (confirmCurrent(request, 'title_request',
            `Place a Polaris hold for BIB ${request.bibid} and this patron? If the provider outcome is uncertain, recovery will be required before another attempt.`)) {
            mutateSimple(request, 'place-hold');
          }
        }, 'primary-button', workflowBlocked));
      }
      if ((request.workflowTags || []).includes('Hold exists (same patron)')) {
        bar.append(commandButton('Close duplicate', 'clone', () => runAction(request, 'closeDuplicate'), 'secondary-button', workflowBlocked));
      }
    } else if (request.status === 'hold_placed') {
      bar.append(commandButton('Additional copy', 'clone', event => showAdditionalCopyPreview(request, event.currentTarget), 'secondary-button', !request.bibid));
      bar.append(commandButton('Close request', 'check', () => runAction(request, 'close'), 'primary-button', workflowBlocked));
    } else if (request.status === 'closed') {
      if (!workflowBlocked) {
        bar.append(commandButton('Reopen', 'undo', () => runAction(request, 'reopen'), 'primary-button'));
      }
      if (['admin', 'super_admin'].includes(state.staff?.role)) {
        bar.append(commandButton('Permanently delete request', 'trash', () => {
          if (confirmCurrent(request, 'title_request',
            `Permanently delete closed title request ${request.id}? This cannot be undone. Its deletion audit will remain.`)) {
            deleteTitleRequest(request);
          }
        }, 'danger-button'));
      }
    }
    if (request.capabilities && request.capabilities.canRetryIdentifierCheck) {
      bar.append(commandButton('Retry identifier check', 'refresh', () => mutateSimple(request, 'retry-identifier-check')));
    }
    return bar;
  }

  function labeledInput(label, input, className = '') {
    return element('label', { className }, [element('span', { text: label }), input]);
  }

  function selectWithHistorical(options, selectedValue, labels = {}) {
    const select = element('select');
    const values = [];
    for (const option of options || []) {
      const value = String(option);
      if (!value || values.includes(value)) continue;
      values.push(value);
      select.append(element('option', { value, text: labels[value] || value }));
    }
    const historical = selectedValue === null || selectedValue === undefined ? '' : String(selectedValue);
    if (historical && !values.includes(historical)) {
      select.append(element('option', { value: historical, text: labels[historical] || historical }));
    }
    select.value = historical || values[0] || '';
    return select;
  }

  function customFieldValue(value) {
    if (value === null || value === undefined) return '';
    if (typeof value === 'object' && !Array.isArray(value)) return String(value.value ?? '');
    return String(value);
  }

  function renderCustomFieldEditor(container, request, configuration, formatCode) {
    const controls = new Map();
    const definitions = Array.isArray(configuration.additionalFieldDefinitions)
      ? configuration.additionalFieldDefinitions
      : [];
    const existing = request.customFields && typeof request.customFields === 'object'
      ? request.customFields
      : {};
    const customRules = configuration.formatRules?.[formatCode]?.customFields || {};
    const fields = [];

    for (const definition of definitions) {
      const key = String(definition.key || definition.id || '');
      if (!key) continue;
      const rule = customRules[key] || { mode: 'hidden' };
      const hasHistorical = Object.prototype.hasOwnProperty.call(existing, key);
      if (rule.mode === 'hidden' && !hasHistorical) continue;
      const currentValue = customFieldValue(existing[key]);
      let input;
      if (definition.type === 'textarea') {
        input = element('textarea', { maxlength: '2000' });
        input.value = currentValue;
      } else if (definition.type === 'select') {
        input = element('select');
        input.append(element('option', { value: '', text: '' }));
        const knownValues = [];
        for (const option of definition.options || []) {
          if (option.enabled === false) continue;
          const value = String(option.id || option.key || '');
          if (!value || knownValues.includes(value)) continue;
          knownValues.push(value);
          input.append(element('option', { value, text: option.label || value }));
        }
        if (currentValue && !knownValues.includes(currentValue)) {
          const historicalLabel = existing[key] && typeof existing[key] === 'object'
            ? existing[key].displayValue
            : null;
          input.append(element('option', { value: currentValue, text: historicalLabel || currentValue }));
        }
        input.value = currentValue;
      } else {
        input = element('input', { type: 'text', maxlength: '250', value: currentValue });
      }
      const required = rule.mode === 'required';
      const configuredLabel = rule.label || definition.label || key;
      const label = `${configuredLabel}${required ? ' *' : ''}`;
      input.required = required;
      input.setAttribute('aria-required', String(required));
      input.setAttribute('aria-label', label);
      controls.set(key, { definition, input, mode: rule.mode, label: configuredLabel });
      const field = labeledInput(label, input);
      if (definition.helpText) field.append(element('small', { text: definition.helpText }));
      fields.push(field);
    }
    container.replaceChildren(...fields);
    container.hidden = fields.length === 0;
    return controls;
  }

  function collectCustomFields(request, controls) {
    const existing = request.customFields && typeof request.customFields === 'object'
      ? request.customFields
      : {};
    const result = { ...existing };
    for (const [key, value] of controls) {
      if (value.mode === 'hidden') continue;
      const normalized = value.input.value.trim();
      if (!normalized) {
        delete result[key];
        continue;
      }
      result[key] = {
        label: value.label || value.definition.label || key,
        type: value.definition.type || 'text',
        value: normalized
      };
      if (value.definition.type === 'select') {
        result[key].displayValue = value.input.selectedOptions[0]?.textContent || normalized;
      }
    }
    return result;
  }

  function stableJson(value) {
    if (Array.isArray(value)) return `[${value.map(stableJson).join(',')}]`;
    if (value && typeof value === 'object') {
      return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${stableJson(value[key])}`).join(',')}}`;
    }
    return JSON.stringify(value);
  }

  function buildEditForm(request, configuration) {
    const form = element('form', { className: 'edit-form' });
    state.editorDirty = false;
    const title = element('input', { value: request.title, required: 'required', maxlength: '500' });
    const author = element('input', { value: request.author || '', maxlength: '500' });
    const identifier = element('input', {
      value: request.identifier || '',
      maxlength: '100',
      disabled: !request.capabilities.canEditIdentifier
    });
    const bib = element('input', {
      value: request.bibid || '',
      inputmode: 'numeric',
      pattern: '[0-9]*',
      maxlength: '100',
      disabled: !request.capabilities.canChangeBib
    });
    state.editControls = { title, author, identifier, bib };
    const selectedContext = element('p', { className: 'polaris-selection-context wide', role: 'status' });
    const searchButton = commandButton('Search Polaris catalog', 'search', () => {
      polarisLookup.open({
        requestId: String(request.id),
        libraryOrgId: request.libraryOrgId,
        isCurrent: () => isCurrentDialogRequest(request, 'title_request') && form.isConnected,
        returnFocus: searchButton,
        editorFocus: bib,
        canApply: row => !bib.disabled || String(row.bibId) === bib.value.trim(),
        mode: bib.value.trim() ? 'bib' : identifier.value.trim() ? 'identifier' : 'title',
        query: bib.value.trim() || identifier.value.trim() || title.value.trim(),
        title: title.value.trim(),
        author: author.value.trim(),
        apply: (selected, verifiedDetail) => {
          applyPolarisResultToControls(selected, { bib, title, author, identifier });
          state.verifiedBib = {
            requestId: String(request.id),
            version: request.version,
            identifier: String(identifier.value).trim(),
            bibId: String(selected.bibId),
            detail: verifiedDetail
          };
          state.editorDirty = true;
          updateResearchLinks();
          showSelectedContext();
          updatePreview();
        }
      });
    });
    function showSelectedContext() {
      const detail = state.verifiedBib?.detail;
      if (!detail || state.verifiedBib.requestId !== String(request.id)) {
        selectedContext.textContent = '';
        return;
      }
      const holdings = detail.holdingsSummary;
      selectedContext.textContent = [
        `Polaris BIB ${detail.bibId} verified for this request.`,
        detail.publication ? `Polaris publication: ${detail.publication}.` : '',
        detail.format ? `Polaris format: ${detail.format}.` : '',
        holdings ? `${holdings.myLibraryCount} item(s) at this library, ${holdings.otherLibraryCount} elsewhere; ${holdings.isHoldable ? 'holdable' : 'not holdable'}.` :
          detail.holdingsUnavailable ? 'Holdings are temporarily unavailable.' : '',
        detail.patronHasHold === true ? 'This patron already has a hold for this BIB.' :
          detail.patronHasHold === false ? 'No existing patron hold was found for this BIB.' : ''
      ].filter(Boolean).join(' ');
    }
    showSelectedContext();
    bib.addEventListener('input', () => {
      state.verifiedBib = null;
      polarisLookup.invalidate();
      showSelectedContext();
      updateResearchLinks();
    });
    identifier.addEventListener('input', () => {
      state.verifiedBib = null;
      polarisLookup.invalidate();
      showSelectedContext();
      updateResearchLinks();
    });
    title.addEventListener('input', updateResearchLinks);
    form.addEventListener('input', () => { state.editorDirty = true; });
    form.addEventListener('change', () => { state.editorDirty = true; });
    const publication = selectWithHistorical(configuration.publicationOptions, request.publication);
    publication.setAttribute('aria-label', 'Publication timing');
    const exactDate = element('input', { type: 'date', value: request.exactPublicationDate || '' });
    const format = selectWithHistorical(configuration.availableFormats, request.format, configuration.formatLabels || {});
    format.setAttribute('aria-label', 'Format');
    const notes = element('textarea', { maxlength: '10000' });
    notes.value = request.notes || '';
    const autohold = element('input', {
      type: 'checkbox',
      checked: request.autohold,
      disabled: request.capabilities?.canChangeWorkflowState !== true
    });
    const customFields = element('div', { className: 'custom-fields wide' });
    let customFieldControls = renderCustomFieldEditor(customFields, request, configuration, format.value);
    const pendingPreview = element('p', { className: 'pending-audit-preview wide', role: 'status' });
    const save = element('button', { type: 'submit', className: 'primary-button' }, [icon('save'), 'Save changes']);
    function updatePreview() {
      const changed = [];
      const identifierChanged = !identifier.disabled && identifier.value.trim() !== (request.identifier || '').trim();
      const bibChanged = !bib.disabled && bib.value.trim() !== (request.bibid || '').trim();
      const selectedBib = selectedStaffBibId(state.verifiedBib, request.id, bib.value);
      let bibVerified = request.bibidStaffVerified === true;
      if (identifierChanged || bibChanged) bibVerified = false;
      if (bibChanged || selectedBib) bibVerified = Boolean(bib.value.trim());
      if (title.value.trim() !== request.title) changed.push('title');
      if ((author.value.trim() || null) !== (request.author || null)) changed.push('author');
      if (identifierChanged) changed.push('identifier');
      if (bibChanged) changed.push('BIB ID');
      if (bibVerified !== (request.bibidStaffVerified === true)) changed.push('BIB verification');
      if ((publication.value.trim() || null) !== (request.publication || null)) changed.push('publication timing');
      if (exactDate.value !== (request.exactPublicationDate || '')) changed.push('exact publication date');
      if (format.value !== request.format) changed.push('format');
      if (autohold.checked !== request.autohold) changed.push('automatic hold');
      if (notes.value !== (request.notes || '')) changed.push('notes');
      if (stableJson(collectCustomFields(request, customFieldControls)) !==
          stableJson(request.customFields || {})) changed.push('custom fields');
      const claimantId = request.claimedByStaffUserId == null ? null : String(request.claimedByStaffUserId);
      const actorId = state.staff?.id == null ? null : String(state.staff.id);
      if (actorId && claimantId !== actorId) changed.push(claimantId ? 'claim transfer' : 'staff claim');
      state.editorDirty = changed.some(item => item !== 'claim transfer' && item !== 'staff claim');
      pendingPreview.textContent = changed.length
        ? `Pending changes (not saved): ${changed.join(', ')}.`
        : 'No pending changes.';
      save.disabled = changed.length === 0;
    }
    format.addEventListener('change', () => {
      customFieldControls = renderCustomFieldEditor(customFields, request, configuration, format.value);
      updatePreview();
    });
    form.addEventListener('input', updatePreview);
    form.addEventListener('change', updatePreview);
    form.append(
      labeledInput('Title', title),
      labeledInput('Author', author),
      labeledInput('Identifier', identifier),
      labeledInput('BIB ID', bib),
      element('div', { className: 'wide polaris-edit-tools' }, [searchButton, selectedContext]),
      labeledInput('Publication timing', publication),
      labeledInput('Exact publication date', exactDate),
      labeledInput('Format', format),
      customFields,
      element('label', { className: 'check-field' }, [autohold, element('span', { text: 'Automatically place hold' })]),
      labeledInput('Notes', notes, 'wide'),
      pendingPreview,
      element('div', { className: 'form-actions wide' }, [save])
    );
    updatePreview();
    form.addEventListener('submit', async event => {
      event.preventDefault();
      if (save.disabled || !isCurrentDialogRequest(request, 'title_request')) return;
      const bibWillChange = !bib.disabled && bib.value.trim() !== String(request.bibid || '').trim();
      const bibSelected = Boolean(selectedStaffBibId(state.verifiedBib, request.id, bib.value));
      const turnsOffHoldForBib = request.autohold && !autohold.checked && Boolean(request.bibid);
      const noAutoHoldConsequence = request.status === 'outstanding_purchase'
        ? 'Save this BIB with automatic hold off? No hold will be placed automatically. The request stays Outstanding purchase now; automatic purchase promotion may later close it without a hold.'
        : 'Save this BIB with automatic hold off? No hold will be placed automatically, and hold placement remains unavailable while auto-hold is off.';
      if ((bibWillChange || bibSelected || turnsOffHoldForBib) && !autohold.checked &&
          !confirmCurrent(request, 'title_request', noAutoHoldConsequence)) return;
      if (request.claimType === 'automatic_format_rule') {
        const claimantId = request.claimedByStaffUserId == null ? null : String(request.claimedByStaffUserId);
        const transfersClaim = claimantId && claimantId !== String(state.staff?.id);
        const formatConsequence = format.value !== request.format
          ? `Change the format from ${request.formatLabel || request.format} to ${format.selectedOptions[0]?.textContent || format.value}?`
          : 'Save these request edits?';
        const claimConsequence = transfersClaim
          ? 'This transfers the automatic format claim from the current claimant to your manual claim.'
          : 'This replaces the automatic format claim with your manual claim.';
        if (!confirmCurrent(request, 'title_request', `${formatConsequence} ${claimConsequence}`)) return;
      }
      if (!isCurrentDialogRequest(request, 'title_request') || !form.isConnected) return;
      await mutateRequest(request, `/api/asap/staff/title-requests/${request.id}/action`, {
        version: request.version,
        action: 'edit',
        title: title.value,
        author: author.value,
        identifier: identifier.disabled ? request.identifier : identifier.value.trim() || null,
        bibid: bib.disabled ? request.bibid : bib.value.trim() || null,
        ...(selectedStaffBibId(state.verifiedBib, request.id, bib.value)
          ? { staffSelectedBibId: selectedStaffBibId(state.verifiedBib, request.id, bib.value) }
          : {}),
        publication: publication.value,
        exactPublicationDate: exactDate.value || null,
        format: format.value,
        autohold: autohold.checked,
        notes: notes.value,
        customFields: collectCustomFields(request, customFieldControls)
      }, 'Request changes saved.');
    });
    return form;
  }

  async function mutateSimple(request, operation) {
    const path = operation === 'claim' || operation === 'unclaim' || operation === 'clear-claim' ||
      operation === 'retry-identifier-check' || operation === 'place-hold'
      ? `/api/asap/staff/title-requests/${request.id}/${operation}`
      : null;
    if (!path) return;
    const messages = {
      claim: 'Request claimed.',
      unclaim: 'Your claim was released.',
      'clear-claim': 'Another staff member’s claim was cleared.',
      'retry-identifier-check': 'Identifier retry requested.',
      'place-hold': 'Hold placement completed.'
    };
    await mutateRequest(request, path, { version: request.version }, messages[operation]);
  }

  function showActionChoice(request, action, returnFocus) {
    if (!isCurrentDialogRequest(request, 'title_request')) return;
    cancelActionChoiceLoad();
    dom.dialogBody.querySelector('.action-choice')?.remove();
    const panel = element('form', { className: 'action-choice', 'aria-label': `${action} options` });
    const heading = element('h3', { text: action === 'reject' ? 'Reject suggestion' : 'Purchase suggestion' });
    const submit = element('button', { type: 'submit', className: action === 'reject' ? 'danger-button' : 'primary-button',
      disabled: action === 'reject' }, action === 'reject' ? 'Reject' : 'Purchase');
    const cancel = commandButton('Cancel', 'times', dismissActionChoice);
    let input;
    if (action === 'purchase') {
      const entersPendingHold = Boolean(request.bibid);
      input = entersPendingHold ? null : element('input', { type: 'checkbox', checked: state.staff.purchaseReminderDefault });
      panel.append(heading, element('p', { text: request.bibid
        ? 'Purchase moves this BIB to Pending hold after server verification. A purchase reminder does not apply. The final state comes from the server.'
        : 'Purchase moves this request to Outstanding purchase. A reminder is optional.' }));
      if (input) panel.append(element('label', { className: 'check-field' },
        [input, element('span', { text: 'Send purchase reminder' })]));
    } else {
      input = element('select', { 'aria-label': 'Rejection template' });
      input.append(element('option', { value: '', text: 'Default rejection email' }));
      panel.append(heading, element('p', { text: 'Reject closes this request. A patron rejection email is queued only when a template and delivery are available.' }),
        labeledInput('Rejection template', input));
    }
    const choice = { request, action, panel, returnFocus };
    state.actionChoice = choice;
    panel.append(element('div', { className: 'form-actions' }, [submit, cancel]));
    panel.addEventListener('submit', async event => {
      event.preventDefault();
      if (state.actionChoice !== choice || !isCurrentDialogRequest(request, 'title_request')) return;
      submit.disabled = true;
      try {
        await runAction(request, action, undefined, action === 'purchase'
          ? { emailPurchaseReminder: input?.checked === true }
          : { rejectionTemplateId: input.value || null });
      } finally {
        if (state.actionChoice === choice && panel.isConnected) submit.disabled = false;
      }
    });
    dom.dialogBody.querySelector('.action-bar')?.after(panel);
    (input || submit).focus();
    if (action !== 'reject') return;
    const load = latestLoads.begin('action-choice');
    authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(request.id)}/rejection-templates`,
      { signal: load.signal })
      .then(data => {
        if (!load.isCurrent() || state.actionChoice !== choice ||
            !isCurrentDialogRequest(request, 'title_request')) return;
        for (const item of data.items || []) {
          input.append(element('option', { value: String(item.id), text: item.name }));
        }
        input.value = data.defaultTemplateId || '';
        submit.disabled = false;
      })
      .catch(error => {
        if (load.isCurrent() && state.actionChoice === choice &&
            !(isAbortError(error) && load.signal.aborted) && error.status !== 401) {
          announce(error.message || 'Rejection templates could not be loaded.', 'error');
        }
      })
      .finally(() => latestLoads.finish('action-choice', load.token));
  }

  async function runAction(request, action, targetStatus, choices = {}) {
    const entersPendingHold = action === 'catalogFound' || action === 'alreadyOwn' ||
      targetStatus === 'pending_hold' || action === 'purchase' && Boolean(request.bibid);
    if (entersPendingHold) {
      if (!isVerifiedDraft(request)) {
        announce('Search Polaris, select the matching BIB, and save it before moving to Pending hold.', 'error');
        return;
      }
      if (state.editorDirty || state.verifiedBib.bibId !== String(request.bibid || '').trim()) {
        announce('Save the current request edits before moving to Pending hold.', 'error');
        return;
      }
    }
    const holdOffConsequence = request.autohold
      ? '' : ' Automatic hold is off, so staff cannot place a hold until it is enabled.';
    const confirmations = {
      purchase: request.bibid
        ? `Move this request to Pending hold using its verified BIB? The server will determine the final state.${holdOffConsequence}`
        : 'Record this purchase decision and move the request to Outstanding purchase?',
      alreadyOwn: `Record that the library already owns this title and move the verified BIB to Pending hold?${holdOffConsequence}`,
      catalogFound: `Move this verified catalog title to Pending hold?${holdOffConsequence}`,
      reject: 'Reject and close this request? A patron rejection email is queued only when a template and delivery are available.',
      silentClose: 'Close this suggestion without a rejection email? It will leave the active queue.',
      closeDuplicate: 'Close this request as a duplicate of an existing patron hold? No new hold will be placed.',
      close: 'Close this hold-placed request? Its placed-hold history will remain.',
      reopen: 'Reopen this closed request as a suggestion? The action will assign a manual claim to you.'
    };
    if (confirmations[action] && !confirmCurrent(request, 'title_request', confirmations[action])) return;
    await mutateRequest(request, `/api/asap/staff/title-requests/${request.id}/action`, {
      version: request.version,
      action,
      status: targetStatus,
      ...choices
    }, 'Workflow action completed.');
  }

  async function showAdditionalCopyPreview(request, returnFocus) {
    const uncertainCreation = state.unconfirmedCopyCreationAwaitingRefresh;
    if (uncertainCreation && (!uncertainCreation.reviewed || uncertainCreation.sourceId !== String(request.id))) {
      announce('Additional-copy creation is unconfirmed. Refresh the open additional-copy task list for this library and review matching tasks before trying again.', 'warning');
      return;
    }
    cancelAdditionalCopyCreationCompletion();
    const load = latestLoads.begin('additional-copy-preview');
    announce('Loading additional-copy preview...');
    try {
      const preview = await authorizedJson(`/api/asap/staff/title-requests/${request.id}/additional-copy`, {
        signal: load.signal
      });
      if (!load.isCurrent() || !isCurrentDialogRequest(request, 'title_request')) return;
      state.createCopyRequest = { request, version: preview.version };
      state.createCopyReturnFocus = returnFocus || document.activeElement;
      const holdState = request.status === 'hold_placed' ? 'placed' : 'queued';
      dom.createCopySummary.textContent = `${preview.openCount} open additional-copy task${preview.openCount === 1 ? '' : 's'} already exist for BIB ${preview.bibid}. The patron hold remains ${holdState}.` +
        (uncertainCreation ? ' This retry uses the original request version; the server will reject it if the earlier creation changed the request.' : '');
      dom.createCopyReminder.checked = Boolean(preview.emailPurchaseReminderDefault);
      dom.createCopyForm.querySelector('button[type="submit"]').disabled = false;
      dom.createCopyDialog.showModal();
      dom.cancelCreateCopy.focus();
      announce('Review the additional-copy task before creating it.');
    } catch (error) {
      if (load.isCurrent() && isCurrentDialogRequest(request, 'title_request') &&
          !(isAbortError(error) && load.signal.aborted) && error.status !== 401) {
        announce(error.message || 'The additional-copy preview could not be loaded.', 'error');
      }
    } finally {
      latestLoads.finish('additional-copy-preview', load.token);
    }
  }

  function closeAdditionalCopyCreateDialog(options = {}) {
    if (state.createCopyRequest?.submitting && options.preserveMutation !== true && !options.force) {
      announce('Task creation is in progress. Wait for the authoritative result before closing.', 'warning');
      dom.cancelCreateCopy.focus();
      return false;
    }
    if (options.preserveMutation !== true) cancelAdditionalCopyCreationCompletion();
    if (dom.createCopyDialog.open) dom.createCopyDialog.close();
    state.createCopyRequest = null;
    const returnFocus = state.createCopyReturnFocus;
    state.createCopyReturnFocus = null;
    if (returnFocus && returnFocus.isConnected) returnFocus.focus();
    return true;
  }

  async function createAdditionalCopy(event) {
    event.preventDefault();
    const pending = state.createCopyRequest;
    const uncertainCreation = state.unconfirmedCopyCreationAwaitingRefresh;
    if (!pending || pending.submitting || pending.outcomeUnconfirmed ||
        (uncertainCreation && (!uncertainCreation.reviewed || uncertainCreation.sourceId !== String(pending.request.id))) ||
        !isCurrentDialogRequest(pending.request, 'title_request')) return;
    pending.submitting = true;
    const mutation = latestLoads.begin('additional-copy-create-mutation');
    const submit = dom.createCopyForm.querySelector('button[type="submit"]');
    submit.disabled = true;
    announce('Creating additional-copy task...');
    try {
      const attempt = uncertainCreation || {
        libraryOrgId: pending.request.libraryOrgId,
        bibid: pending.request.bibid,
        sourceId: String(pending.request.id),
        version: pending.version,
        reviewReady: false,
        reviewed: false
      };
      if (!rememberUnconfirmedCopyCreation(attempt)) {
        announce('This browser could not save the pending task attempt. Enable site storage before creating the task.', 'error');
        return;
      }
      state.unconfirmedCopyCreationAwaitingRefresh = attempt;
      const result = await authorizedJson(`/api/asap/staff/title-requests/${pending.request.id}/additional-copy`, {
        method: 'POST',
        signal: mutation.signal,
        body: {
          version: attempt.version,
          emailPurchaseReminder: dom.createCopyReminder.checked
        }
      });
      if (!isCurrentAdditionalCopyCreation(mutation, pending)) return;
      if (result?.committed !== true || typeof result.additionalCopyRequestId !== 'string' ||
          !/^\d+$/.test(result.additionalCopyRequestId) || typeof result.finalStatus !== 'string' ||
          !Object.hasOwn(result, 'additionalCopyRequest') ||
          (result.additionalCopyRequest === null && result.refreshUnavailable !== true) ||
          (result.additionalCopyRequest !== null &&
            (String(result.additionalCopyRequest.id) !== result.additionalCopyRequestId ||
             result.additionalCopyRequest.status !== result.finalStatus ||
             typeof result.additionalCopyRequest.version !== 'string'))) {
        throw unconfirmedResponseError();
      }
      state.unconfirmedCopyCreationAwaitingRefresh = false;
      rememberUnconfirmedCopyCreation(false);
      const taskId = result.additionalCopyRequest?.id || result.additionalCopyRequestId;
      const notification = notificationOutcome(result.notificationStatus, result.notificationReason, 'Purchase reminder');
      const message = `Additional-copy task ${taskId || ''} created. Final state: ${statusLabel(result.finalStatus || 'open')}.${notification.text}`;
      state.partialSessionFailureMessage = `${message} Sign in again to review the committed task.`;
      state.partialSessionFailureOwner = mutation.token;
      state.partialSessionFailureDetailAvailable = Boolean(taskId);
      state.partialSessionFailureAfterQueueSequence = null;
      state.additionalCopyLoaded = false;
      announce(message, notification.partial ? 'warning' : 'success');
      const refreshed = await loadQueue({ skipDeepLink: true, silent: true });
      if (isCurrentAdditionalCopyCreation(mutation, pending)) {
        const returnFocus = state.createCopyReturnFocus;
        closeAdditionalCopyCreateDialog({ preserveMutation: true });
        const detailLoaded = await openRequest(pending.request.id, returnFocus);
        if (refreshed === true && detailLoaded === true) clearCommittedSessionFallback(mutation.token);
        if (isCurrentDialogSelection(pending.request, 'title_request')) {
          const followup = `${refreshed === false ? ' The request queue could not refresh.' : ''}${detailLoaded ? '' : ' Request details could not refresh.'}`;
          announce(`${message}${followup}`, notification.partial || followup ? 'warning' : 'success');
        }
      }
    } catch (error) {
      const definiteNoCommit = [400, 401, 403, 404, 409].includes(error.status) ||
        (error.status === 503 && error.response?.code === 'notification_dependency_unavailable');
      if (definiteNoCommit) {
        state.unconfirmedCopyCreationAwaitingRefresh = false;
        rememberUnconfirmedCopyCreation(false);
      }
      const unconfirmedOutcome = !definiteNoCommit &&
        (isUnconfirmedMutationError(error, mutation.signal) || error.status === 408 || error.status >= 500);
      if (unconfirmedOutcome && isCurrentAdditionalCopyCreation(mutation, pending)) {
        pending.outcomeUnconfirmed = true;
        const message = 'Additional-copy creation could not be confirmed. Check the task list before trying again.';
        retainUnconfirmedOutcome(message, mutation.token);
        state.unconfirmedCopyCreationAwaitingRefresh.reviewReady = false;
        state.unconfirmedCopyCreationAwaitingRefresh.reviewed = false;
        renderAdditionalCopyGrid();
        latestLoads.begin('additional-copies').abort();
        announce(message, 'warning');
      } else if (error.status === 409) {
        await loadQueue({ skipDeepLink: true, silent: true });
        if (isCurrentAdditionalCopyCreation(mutation, pending)) {
          closeAdditionalCopyCreateDialog({ preserveMutation: true });
          await openRequest(pending.request.id);
          if (isCurrentDialogSelection(pending.request, 'title_request')) {
            announce(error.message || 'The request changed. Review the refreshed version before trying again.', 'error');
          }
        }
      } else if (isCurrentAdditionalCopyCreation(mutation, pending) &&
                 error.status !== 401 && !isAbortError(error)) {
        announce(error.message || 'The additional-copy task could not be created.', 'error');
      }
    } finally {
      pending.submitting = false;
      if (mutation.isCurrent() && state.createCopyRequest === pending && submit.isConnected) {
        submit.disabled = pending.outcomeUnconfirmed === true;
      }
      latestLoads.finish('additional-copy-create-mutation', mutation.token);
    }
  }

  async function deleteTitleRequest(request) {
    if (!isCurrentDialogRequest(request, 'title_request') || state.dialogMutationInFlight) return;
    const mutation = latestLoads.begin('dialog-mutation');
    state.dialogMutationInFlight = 'title:' + request.id + ':' + request.version;
    announce('Permanently deleting title request...');
    try {
      const result = await authorizedJson('/api/asap/staff/requests/' + encodeURIComponent(request.id), {
        method: 'DELETE', body: { version: request.version, actorVersion: state.staff?.version },
        signal: mutation.signal
      });
      if (result?.deleted !== true) throw unconfirmedResponseError();
      const message = 'Title request ' + request.id + ' permanently deleted. Its deletion audit remains.';
      state.partialSessionFailureMessage = message + ' Sign in again to review Closed work.';
      state.partialSessionFailureOwner = mutation.token;
      state.partialSessionFailureDetailAvailable = false;
      state.partialSessionFailureAfterQueueSequence = null;
      if (!isCurrentDialogMutation(mutation, request, 'title_request')) {
        if (!state.staff) dom.signedOutMessage.textContent = state.partialSessionFailureMessage;
        return;
      }
      closeDialog({ preserveMutation: true });
      announce(message, 'success');
      const refreshed = await loadQueue({ skipDeepLink: true, silent: true });
      if (refreshed === true) clearCommittedSessionFallback(mutation.token);
      if (refreshed === false && state.staff) announce(message + ' The Closed view could not refresh.', 'warning');
    } catch (error) {
      const uncertain = isUnconfirmedMutationError(error, mutation.signal) ||
        error.status === 408 || error.status >= 500;
      if (uncertain && isCurrentDialogMutation(mutation, request, 'title_request')) {
        retainUnconfirmedOutcome(
          'Title-request deletion could not be confirmed. Refresh Closed work before retrying.',
          mutation.token);
        await loadQueue({ skipDeepLink: true, silent: true });
        announce('Title-request deletion could not be confirmed. Review Closed work before retrying.', 'warning');
      } else if (isCurrentDialogMutation(mutation, request, 'title_request') && error.status !== 401) {
        announce(error.message || 'The title request was not deleted. Review its current state.', 'error');
        await loadQueue({ skipDeepLink: true, silent: true });
      }
    } finally {
      if (state.dialogMutationInFlight === 'title:' + request.id + ':' + request.version) {
        state.dialogMutationInFlight = null;
      }
      latestLoads.finish('dialog-mutation', mutation.token);
    }
  }

  async function mutateRequest(request, path, body, successMessage) {
    if (!isCurrentDialogRequest(request, 'title_request') || state.dialogMutationInFlight === `title:${request.id}:${request.version}`) return;
    const mutation = latestLoads.begin('dialog-mutation');
    state.dialogMutationInFlight = `title:${request.id}:${request.version}`;
    announce('Saving request...');
    try {
      const result = await authorizedJson(path, { method: 'POST', body, signal: mutation.signal });
      if (!isCurrentDialogMutation(mutation, request, 'title_request')) return;
      if (!isCommittedRequestResponse(result, request.id)) {
        throw unconfirmedResponseError();
      }
      const committed = result?.committed === true;
      let current = result?.request || (committed ? (result.id ? result : null) : result);
      const resultStatus = current?.status || result.finalStatus;
      const status = committed && resultStatus ? ` Final state: ${statusLabel(resultStatus)}.` : '';
      const notificationLabel = body?.action === 'reject' ? 'Rejection email'
        : path.endsWith('/assign') ? 'Assignment notification'
          : body?.action === 'purchase' ? 'Purchase reminder' : 'Notification';
      const notification = notificationOutcome(result?.notificationStatus, result?.notificationReason,
        notificationLabel);
      let message = `${successMessage}${status}${notification.text}`;
      let messageKind = notification.partial ? 'warning' : 'success';
      const sessionFailureMessage = committed
        ? `${message} Sign in again to review the committed request.`
        : null;
      state.partialSessionFailureMessage = sessionFailureMessage;
      state.partialSessionFailureOwner = committed ? mutation.token : null;
      state.partialSessionFailureDetailAvailable = committed && Boolean(current);
      state.partialSessionFailureAfterQueueSequence = committed ? state.queueLoadSequence : null;
      if (!current && committed) {
        try {
          current = await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(request.id)}`,
            { signal: mutation.signal });
        } catch (error) {
          if (!isAbortError(error) && error.status !== 401) {
            message += ' Details and activity could not refresh.';
          }
        }
      }
      if (current?.status && current.status !== resultStatus) {
        message = `${successMessage} Final state: ${statusLabel(current.status)}.${notification.text}`;
        if (state.partialSessionFailureOwner === mutation.token) {
          state.partialSessionFailureMessage = `${message} Sign in again to review the committed request.`;
        }
      }
      if (state.partialSessionFailureOwner === mutation.token) {
        state.partialSessionFailureDetailAvailable = Boolean(current);
      }
      if (!mutation.isCurrent() || !isCurrentDialogSelection(request, 'title_request')) return;
      if (current) {
        if (body?.action === 'edit' && current.bibidStaffVerified === true && state.verifiedBib &&
            state.verifiedBib.requestId === String(current.id) &&
            state.verifiedBib.bibId === String(current.bibid || '').trim() &&
            state.verifiedBib.identifier === String(current.identifier || '').trim()) {
          state.verifiedBib.version = current.version;
        }
        renderRequest(current, state.configurations.get(String(current.libraryOrgId)) || {},
          { preserveDialogMutation: true });
      } else {
        state.selectedRequestVersion = null;
        dom.dialogBody.replaceChildren(element('p', { text: 'The action committed. Reload this request to review current details.' }));
      }
      dom.closeDialog.focus();
      announce(message, messageKind);
      if (state.dialogMutationInFlight === `title:${request.id}:${request.version}`) {
        state.dialogMutationInFlight = null;
      }
      const refreshed = await loadQueue({ skipDeepLink: true, silent: true });
      if (mutation.isCurrent() && isCurrentDialogSelection(request, 'title_request')) {
        if (refreshed === false) messageKind = 'warning';
        announce(refreshed === false ? `${message} The queue could not refresh.` : message, messageKind);
      }
    } catch (error) {
      const recordedProviderOutcome = error.response?.providerOutcomeRecorded === true;
      const definiteNoCommit = ['bib_validation_unavailable', 'notification_dependency_unavailable']
        .includes(error.response?.code);
      const unconfirmedOutcome = !definiteNoCommit &&
        (isUnconfirmedMutationError(error, mutation.signal) || error.status === 408 || error.status >= 500);
      const outcomeUnknown = unconfirmedOutcome ||
        ['request_outcome_unconfirmed', 'hold_outcome_unconfirmed', 'hold_provider_error']
          .includes(error.response?.code);
      const holdReviewRequired = path.endsWith('/place-hold') && error.status === 409;
      if (error.status === 409 || outcomeUnknown || recordedProviderOutcome) {
        const message = recordedProviderOutcome
          ? 'Polaris returned a hold result, but request finalization was deferred after staff access changed. Review the hold operation with an authorized account.'
          : outcomeUnknown
          ? path.endsWith('/place-hold')
            ? 'The hold outcome could not be confirmed. Reload the operation before trying again.'
            : 'The request outcome could not be confirmed. Reload before trying again.'
          : error.message || 'The request changed. Review the refreshed version before trying again.';
        if ((outcomeUnknown || recordedProviderOutcome || holdReviewRequired) &&
            isCurrentDialogMutation(mutation, request, 'title_request')) {
          retainUnconfirmedOutcome(message, mutation.token);
        }
        const refreshed = await loadQueue({ skipDeepLink: true, silent: true });
        if (!isCurrentDialogMutation(mutation, request, 'title_request')) return;
        const detailLoaded = await openRequest(request.id);
        if ((outcomeUnknown || recordedProviderOutcome || holdReviewRequired) &&
            refreshed === true && detailLoaded === true) {
          clearCommittedSessionFallback(mutation.token);
        }
        if (isCurrentDialogSelection(request, 'title_request')) announce(message, 'error');
      } else if (isCurrentDialogMutation(mutation, request, 'title_request') &&
                 error.status !== 401 && !isAbortError(error)) {
        announce(error.message || 'The request could not be updated.', 'error');
      }
    } finally {
      if (state.dialogMutationInFlight === `title:${request.id}:${request.version}`) state.dialogMutationInFlight = null;
      latestLoads.finish('dialog-mutation', mutation.token);
    }
  }

  async function showAssignment(request) {
    const load = latestLoads.begin('assignment-candidates');
    announce('Loading eligible staff...');
    try {
      const result = await authorizedJson(`/api/asap/staff/assignment-candidates?libraryOrgId=${request.libraryOrgId}`, {
        signal: load.signal
      });
      if (!load.isCurrent() || !isCurrentDialogRequest(request, 'title_request')) return;
      const select = element('select', { 'aria-label': 'Assign to staff member' });
      const candidates = result.candidates || [];
      for (const candidate of candidates) {
        select.append(element('option', {
          value: candidate.id,
          text: candidate.displayName
        }));
      }
      const form = element('form', { className: 'inline-form' }, [
        labeledInput('Assign request', select),
        element('button', { type: 'submit', className: 'primary-button', disabled: candidates.length === 0 }, [icon('user-plus'), 'Assign'])
      ]);
      form.addEventListener('submit', async event => {
        event.preventDefault();
        if (!form.isConnected || !isCurrentDialogRequest(request, 'title_request')) return;
        await mutateRequest(request, `/api/asap/staff/title-requests/${request.id}/assign`, {
          version: request.version,
          assigneeId: select.value
        }, 'Request assigned.');
      });
      dom.dialogBody.prepend(form);
      select.focus();
      announce(candidates.length ? 'Choose an assignee.' : 'No eligible staff are available.');
    } catch (error) {
      if (load.isCurrent() && isCurrentDialogRequest(request, 'title_request') &&
          error.status !== 401 && !(isAbortError(error) && load.signal.aborted)) {
        announce(error.message || 'Assignable staff could not be loaded.', 'error');
      }
    } finally {
      latestLoads.finish('assignment-candidates', load.token);
    }
  }

  async function showPickup(request) {
    const load = latestLoads.begin('pickup-options');
    announce('Loading current pickup preference...');
    try {
      const options = await authorizedJson(`/api/asap/staff/title-requests/${request.id}/pickup-options`, {
        method: 'POST',
        body: { forceRefresh: false },
        signal: load.signal
      });
      if (!load.isCurrent() || !isCurrentDialogRequest(request, 'title_request')) return;
      const select = element('select', { 'aria-label': 'Preferred pickup branch' });
      for (const branch of options.pickupBranches || []) {
        select.append(element('option', { value: branch.id, text: branch.name }));
      }
      if (options.selectedPickupBranchId) select.value = String(options.selectedPickupBranchId);
      const form = element('form', { className: 'inline-form' }, [
        labeledInput('Preferred pickup branch', select),
        element('button', { type: 'submit', className: 'primary-button', disabled: options.readOnly }, [icon('map-marker'), 'Update pickup'])
      ]);
      if (options.pickupBranchWarning) form.append(element('p', { className: 'wide', text: options.pickupBranchWarning }));
      form.addEventListener('submit', async event => {
        event.preventDefault();
        if (!form.isConnected || !isCurrentDialogRequest(request, 'title_request')) return;
        await mutateRequest(request, `/api/asap/staff/title-requests/${request.id}/pickup-preference`, {
          version: options.version,
          preferredPickupBranchId: Number(select.value),
          currentPreferredPickupBranchIdAtLoad: options.currentPreferredPickupBranchId
        }, 'Pickup preference updated.');
      });
      dom.dialogBody.prepend(form);
      select.focus();
      announce('Current pickup preference loaded.');
    } catch (error) {
      if (load.isCurrent() && isCurrentDialogRequest(request, 'title_request') &&
          error.status !== 401 && !(isAbortError(error) && load.signal.aborted)) {
        announce(error.message || 'Pickup choices could not be loaded.', 'error');
      }
    } finally {
      latestLoads.finish('pickup-options', load.token);
    }
  }

  function buildHoldOperation(request, operation) {
    const section = element('section', { className: 'hold-operation' });
    section.append(
      element('h3', { text: operation.state === 'succeeded' ? 'Hold tracking' : 'Hold placement recovery' }),
      element('p', { text: `State: ${operation.state}; phase: ${operation.phase}; attempt: ${operation.attemptNumber}.` })
    );
    if (operation.lastErrorCode) section.append(element('p', { text: `Last diagnostic: ${operation.lastErrorCode}` }));
    if (operation.canReconcile) {
      section.append(commandButton('Reconcile provider state', 'search', async () => {
        if (!confirmCurrent(request, 'title_request',
          `Reconcile hold operation ${operation.id}, attempt ${operation.attemptNumber}? This may inspect Polaris or retry only when the server confirms the operation is safe to resume.`)) return;
        await mutateOperation(request, operation, 'reconcile', { version: operation.version });
      }));
    }
    if (operation.canResolveSucceeded || operation.canResolveNotPerformed) {
      section.append(buildResolutionForm(request, operation));
    }
    return section;
  }

  function buildResolutionForm(request, operation) {
    const markedMutation = operation.phase === 'create_started' || operation.phase === 'reply_started';
    const outcome = element('select', { 'aria-label': 'Resolution' });
    if (operation.canResolveSucceeded) outcome.append(element('option', { value: 'succeeded', text: 'Confirmed succeeded' }));
    if (operation.canResolveNotPerformed) outcome.append(element('option', { value: 'not_performed', text: 'Confirmed not performed' }));
    const evidence = element('select', { 'aria-label': 'Evidence type' });
    const reference = element('input', { maxlength: '1000' });
    const proofSource = element('input', { maxlength: '500' });
    const causalConnection = element('textarea', { maxlength: '2000' });
    const finalHoldId = element('input', { maxlength: '100', inputmode: 'numeric', pattern: '[1-9][0-9]*' });
    const reason = element('textarea', { required: 'required', maxlength: '2000' });
    const excluded = element('input', { type: 'checkbox' });
    const proofAttested = element('input', { type: 'checkbox' });
    const exclusionAttested = element('input', { type: 'checkbox' });
    const exclusionReference = element('input', { maxlength: '1000' });
    const exclusionExplanation = element('textarea', { maxlength: '2000' });
    const form = element('form', { className: 'resolution-form' });
    const referenceField = labeledInput('Evidence reference', reference, 'wide');
    const proofSourceField = labeledInput('Evidence provenance', proofSource, 'wide');
    const causalConnectionField = labeledInput('Connection to this exact attempt', causalConnection, 'wide');
    const finalHoldIdField = labeledInput('Proven final hold ID', finalHoldId, 'wide');
    const proofAttestationField = element('label', { className: 'check-field wide' }, [
      proofAttested,
      element('span', { text: 'I attest that this evidence proves the definitive outcome for this exact operation, attempt, frozen patron, and BIB.' })
    ]);
    const excludedField = element('label', { className: 'check-field wide' }, [
      excluded,
      element('span', { text: 'I confirm every responsible or superseded worker, interactive host, and overlapping process has actually ended or been terminated.' })
    ]);
    const exclusionReferenceField = labeledInput('Executor exclusion reference', exclusionReference, 'wide');
    const exclusionExplanationField = labeledInput('Executor exclusion and in-flight work account', exclusionExplanation, 'wide');
    const exclusionAttestationField = element('label', { className: 'check-field wide' }, [
      exclusionAttested,
      element('span', { text: 'I attest that the exclusion record identifies the affected executions, when and how they ended, and accounts for provider work already sent.' })
    ]);

    function configureField(wrapper, control, visible, required = false) {
      wrapper.hidden = !visible;
      control.disabled = !visible;
      control.required = visible && required;
    }

    function updateEvidence() {
      evidence.replaceChildren();
      if (outcome.value === 'succeeded') {
        evidence.append(
          element('option', { value: 'authoritative_correlated_hold', text: 'Correlated final hold ID' }),
          element('option', { value: 'provider_final_success', text: 'Provider final success' })
        );
      } else {
        if (operation.phase === 'acquired') {
          evidence.append(element('option', { value: 'fenced_never_dispatched', text: 'Server-fenced, never dispatched' }));
        } else {
          evidence.append(element('option', { value: 'provider_final_no_effect', text: 'Provider final no-effect result' }));
        }
      }
      updateEvidenceFields();
    }
    function updateEvidenceFields() {
      const serverFenced = evidence.value === 'fenced_never_dispatched';
      const correlated = evidence.value === 'authoritative_correlated_hold';
      configureField(referenceField, reference, !serverFenced, true);
      configureField(proofSourceField, proofSource, !serverFenced, true);
      configureField(causalConnectionField, causalConnection, !serverFenced, true);
      configureField(proofAttestationField, proofAttested, !serverFenced, true);
      configureField(finalHoldIdField, finalHoldId, correlated, correlated);
      configureField(excludedField, excluded, markedMutation && !serverFenced, true);
      configureField(exclusionReferenceField, exclusionReference, markedMutation && !serverFenced, true);
      configureField(exclusionExplanationField, exclusionExplanation, markedMutation && !serverFenced, true);
      configureField(exclusionAttestationField, exclusionAttested, markedMutation && !serverFenced, true);
    }
    outcome.addEventListener('change', updateEvidence);
    evidence.addEventListener('change', updateEvidenceFields);
    form.append(
      element('p', {
        className: 'resolution-context wide',
        text: `Operation ${operation.id}; attempt ${operation.attemptNumber}; epoch ${operation.executionEpoch}; frozen patron ${operation.patronBarcodeSnapshotMasked}; frozen BIB ${operation.bibIdSnapshot}.`
      }),
      labeledInput('Resolution', outcome),
      labeledInput('Evidence type', evidence),
      referenceField,
      proofSourceField,
      causalConnectionField,
      finalHoldIdField,
      proofAttestationField,
      excludedField,
      exclusionReferenceField,
      exclusionExplanationField,
      exclusionAttestationField,
      labeledInput('Reason', reason, 'wide'),
      element('div', { className: 'form-actions wide' }, [
        element('button', { type: 'submit', className: 'danger-button' }, [icon('check-circle'), 'Resolve operation'])
      ])
    );
    updateEvidence();
    form.addEventListener('submit', async event => {
      event.preventDefault();
      if (!confirmCurrent(request, 'title_request',
        `Resolve hold operation ${operation.id}, attempt ${operation.attemptNumber}, as ${outcome.value.replaceAll('_', ' ')} using ${evidence.selectedOptions[0]?.textContent || evidence.value}? The recorded evidence will determine whether this request has a placed hold or may be retried.`)) return;
      await mutateOperation(request, operation, 'resolve', {
        version: operation.version,
        requestVersion: request.version,
        outcome: outcome.value,
        reason: reason.value,
        evidenceKind: evidence.value,
        evidenceReference: reference.disabled ? null : reference.value,
        operationSpecificProofAttested: !proofAttested.disabled && proofAttested.checked,
        proofSource: proofSource.disabled ? null : proofSource.value,
        causalConnection: causalConnection.disabled ? null : causalConnection.value,
        provenFinalHoldId: finalHoldId.disabled ? null : finalHoldId.value,
        originalExecutorExcluded: !excluded.disabled && excluded.checked,
        executorExclusionAttested: !exclusionAttested.disabled && exclusionAttested.checked,
        executorExclusionReference: exclusionReference.disabled ? null : exclusionReference.value,
        executorExclusionExplanation: exclusionExplanation.disabled ? null : exclusionExplanation.value
      });
    });
    return form;
  }

  async function mutateOperation(request, operation, action, body) {
    if (!isCurrentDialogRequest(request, 'title_request') ||
        state.dialogMutationInFlight === `hold:${operation.id}:${operation.version}`) return;
    const mutation = latestLoads.begin('dialog-mutation');
    state.dialogMutationInFlight = `hold:${operation.id}:${operation.version}`;
    announce(`${action === 'resolve' ? 'Resolving' : 'Reconciling'} hold operation...`);
    try {
      const result = await authorizedJson(`/api/asap/staff/hold-operations/${operation.id}/${action}`,
        { method: 'POST', body, signal: mutation.signal });
      if (!isCurrentDialogMutation(mutation, request, 'title_request')) return;
      if (result?.committed !== true || !['updated', 'resolved'].includes(result.code) ||
          String(result.operationId) !== String(operation.id)) {
        throw unconfirmedResponseError();
      }
      const notification = notificationOutcome(result.notificationStatus, result.notificationReason, 'Hold notification');
      const finalState = result.finalStatus ? ` Final state: ${statusLabel(result.finalStatus)}.` :
        ' Review the refreshed request for final state.';
      const message = action === 'resolve'
        ? `Hold operation ${result.operationId || operation.id} resolved as ${body.outcome.replaceAll('_', ' ')}.${finalState}${notification.text}`
        : `Hold operation ${result.operationId || operation.id} reconciliation recorded.${finalState}${notification.text}`;
      state.partialSessionFailureMessage = `${message} Sign in again to review the committed recovery result.`;
      state.partialSessionFailureOwner = mutation.token;
      state.partialSessionFailureDetailAvailable = false;
      state.partialSessionFailureAfterQueueSequence = state.queueLoadSequence;
      announce(message, notification.partial ? 'warning' : 'success');
      const refreshed = await loadQueue({ skipDeepLink: true, silent: true });
      if (!isCurrentDialogMutation(mutation, request, 'title_request')) return;
      const detailLoaded = await openRequest(request.id);
      if (refreshed === true && detailLoaded === true) clearCommittedSessionFallback(mutation.token);
      if (isCurrentDialogSelection(request, 'title_request')) {
        state.partialSessionFailureDetailAvailable = Boolean(detailLoaded);
        const followup = `${refreshed === false ? ' The queue could not refresh.' : ''}${detailLoaded ? '' : ' Details could not refresh.'}`;
        announce(`${message}${followup}`, notification.partial || followup ? 'warning' : 'success');
      }
    } catch (error) {
      const recordedProviderOutcome = error.response?.providerOutcomeRecorded === true;
      const definiteNoCommit = error.response?.code === 'hold_resolution_dependency_unavailable';
      const unconfirmedOutcome = !definiteNoCommit &&
        (isUnconfirmedMutationError(error, mutation.signal) || error.status === 408 || error.status >= 500);
      const outcomeUnknown = unconfirmedOutcome ||
        ['hold_outcome_unconfirmed', 'hold_provider_error'].includes(error.response?.code);
      const holdReviewRequired = error.status === 409;
      if (error.status === 409 || outcomeUnknown || recordedProviderOutcome) {
        const message = recordedProviderOutcome
          ? 'Polaris returned a hold result, but request finalization was deferred after staff access changed. Review the hold operation with an authorized account.'
          : outcomeUnknown
          ? 'The hold recovery outcome could not be confirmed. Reload before trying again.'
          : error.message || 'The hold recovery changed. Review the refreshed request before trying again.';
        if ((outcomeUnknown || recordedProviderOutcome || holdReviewRequired) &&
            isCurrentDialogMutation(mutation, request, 'title_request')) {
          retainUnconfirmedOutcome(message, mutation.token);
        }
        const refreshed = await loadQueue({ skipDeepLink: true, silent: true });
        if (!isCurrentDialogMutation(mutation, request, 'title_request')) return;
        const detailLoaded = await openRequest(request.id);
        if ((outcomeUnknown || recordedProviderOutcome || holdReviewRequired) &&
            refreshed === true && detailLoaded === true) {
          clearCommittedSessionFallback(mutation.token);
        }
        if (isCurrentDialogSelection(request, 'title_request')) announce(message, 'error');
      } else if (isCurrentDialogMutation(mutation, request, 'title_request') &&
                 error.status !== 401 && !isAbortError(error)) {
        announce(error.message || 'Hold recovery could not be updated.', 'error');
      }
    } finally {
      if (state.dialogMutationInFlight === `hold:${operation.id}:${operation.version}`) state.dialogMutationInFlight = null;
      latestLoads.finish('dialog-mutation', mutation.token);
    }
  }

  async function saveProfile(event) {
    event.preventDefault();
    announce('Saving profile...');
    try {
      const result = await authorizedJson('/api/asap/staff/profile', {
        method: 'POST',
        body: {
          version: state.staff.version,
          weeklyActionSummaryEnabled: dom.weeklyEnabled.checked,
          weeklyActionSummaryEmail: dom.weeklyEmail.value.trim() || null,
          purchaseReminderDefault: dom.purchaseDefault.checked,
          additionalCopyReminderDefault: dom.additionalCopyDefault.checked,
          defaultMineUnclaimedFilter: dom.mineDefault.checked
        }
      });
      state.staff = result.staff;
      populateProfile(state.staff);
      dom.claim.value = state.staff.defaultMineUnclaimedFilter ? 'mine_unclaimed' : 'all';
      dom.additionalCopyClaim.value = state.staff.defaultMineUnclaimedFilter ? 'mine_unclaimed' : 'all';
      renderGrid();
      if (state.additionalCopyLoaded) renderAdditionalCopyGrid();
      announce('Profile saved.', 'success');
    } catch (error) {
      if (error.status === 409) {
        const session = await loadStaffSession();
        if (session.accessAllowed === false) {
          showAccessUnavailable();
          return;
        }
        if (session.authenticated) {
          state.staff = session.staff;
          populateProfile(session.staff);
        }
      }
      if (error.status !== 401) announce(error.message || 'Profile could not be saved.', 'error');
    }
  }

  function switchView(name, updateUrl = true) {
    cancelDialogFocusReturn();
    state.activeView = name;
    dom.queueView.hidden = name !== 'queue';
    dom.additionalCopyView.hidden = name !== 'additional-copies';
    dom.analyticsView.hidden = name !== 'analytics';
    dom.profileView.hidden = name !== 'profile';
    dom.operationsView.hidden = name !== 'operations';
    dom.settingsView.hidden = name !== 'settings';
    for (const tab of dom.viewTabs) {
      const active = tab.dataset.view === name;
      tab.classList.toggle('active', active);
      if (active) tab.setAttribute('aria-current', 'page');
      else tab.removeAttribute('aria-current');
    }
    if (updateUrl) replaceStageParameter(
      name === 'additional-copies' ? 'additional_copies' : name === 'analytics' ? 'analytics' : name === 'settings' ? 'settings' : name === 'operations' ? 'operations' : null
    );
    const heading = name === 'queue'
      ? '#queue-title'
      : name === 'additional-copies' ? '#additional-copy-title'
      : name === 'analytics' ? '#analytics-title'
      : name === 'profile' ? '#profile-title' : name === 'operations' ? '#operations-title' : '#settings-title';
    document.querySelector(heading).focus({ preventScroll: true });
    if (updateUrl && name === 'additional-copies' && !state.additionalCopyLoaded) {
      loadAdditionalCopies({ skipDeepLink: true });
    }
    if (updateUrl && name === 'operations') loadOperations();
    if (updateUrl && name === 'analytics') loadAnalytics(dom.analyticsContainer);
    if (name === 'settings') settingsController.activate();
  }

  function closeDialog(options = {}) {
    if (state.dialogMutationInFlight && options.preserveMutation !== true && !options.force) {
      announce('The workflow action is in progress. Wait for its authoritative result before closing.', 'warning');
      dom.closeDialog.focus();
      return false;
    }
    polarisLookup.close();
    latestLoads.begin('research-configuration').abort();
    state.verifiedBib = null;
    state.research = null;
    state.currentRequest = null;
    state.editControls = null;
    cancelDialogFocusReturn();
    cancelAssignmentCandidateLoad();
    cancelPickupOptionsLoad();
    if (options.preserveMutation !== true) cancelDialogMutationCompletion();
    cancelActionChoiceLoad();
    cancelAdditionalCopyPreviewLoad();
    cancelAdditionalCopyCreationCompletion();
    if (dom.dialog.open) dom.dialog.close();
    const selectedId = state.selectedRequestId;
    const wasAdditionalCopy = state.selectedRequestType === 'additional_copy';
    const returnFocus = state.returnFocus;
    const staff = state.staff;
    const view = state.activeView;
    const scope = state.scope;
    const status = wasAdditionalCopy ? state.additionalCopyStatus : state.status;
    state.selectedRequestId = null;
    state.selectedRequestType = null;
    state.selectedRequestVersion = null;
    state.returnFocus = null;
    replaceRequestParameter(null, wasAdditionalCopy || state.activeView === 'additional-copies');
    if (options.preserveMutation === true) state.dialogMutationInFlight = null;
    const ariaLabel = wasAdditionalCopy
      ? `Open additional-copy task ${selectedId}`
      : `Open request ${selectedId}`;
    const grid = wasAdditionalCopy ? dom.additionalCopyGrid : dom.grid;
    let frame;
    let focusedReturnTarget;
    const focusReturnButton = () => {
      if (state.staff !== staff || !staff || state.activeView !== view || state.scope !== scope ||
          (wasAdditionalCopy ? state.additionalCopyStatus : state.status) !== status ||
          dom.dialog.open || state.selectedRequestId !== null) {
        cancelDialogFocusReturn();
        return;
      }
      const liveGridButton = [...grid.querySelectorAll('.grid-open')]
        .find(button => button.getAttribute('aria-label') === ariaLabel);
      const focusTarget = liveGridButton ||
        (returnFocus?.isConnected && !dom.dialog.contains(returnFocus) ? returnFocus : null);
      if (focusTarget) {
        focusedReturnTarget = focusTarget;
        focusTarget.focus();
      }
    };
    const focusMoved = event => {
      if (event.target !== focusedReturnTarget && !dom.dialog.contains(event.target)) cancelDialogFocusReturn();
    };
    // Grid.js may first render old rows, then replace them. Follow the opener until focus moves elsewhere.
    const observer = new window.MutationObserver(focusReturnButton);
    state.cancelFocusReturn = () => {
      observer.disconnect();
      window.cancelAnimationFrame(frame);
      document.removeEventListener('focusin', focusMoved);
    };
    observer.observe(grid, { childList: true, subtree: true });
    document.addEventListener('focusin', focusMoved);
    frame = window.requestAnimationFrame(focusReturnButton);
    return true;
  }

  function bindEvents() {
    onSessionInvalid(error => {
      const batch = state.bulkDeleteState;
      if (batch?.submitting && batch.currentItem) {
        batch.ledger.push({ ...batch.currentItem, outcome: 'forbidden/out_of_scope' });
        const remaining = batch.snapshot.items.slice(batch.snapshot.items.indexOf(batch.currentItem) + 1);
        batch.ledger.push(...remaining.map(item => ({ ...item, outcome: 'not_attempted' })));
        renderBulkLedger(batch);
        batch.currentItem = null;
      }
      const pickupChanged = error.response?.code === 'request_not_created_pickup_changed' &&
        error.response?.pickupPreferenceChanged === true;
      if (pickupChanged) {
        const detail = typeof error.response.message === 'string' && error.response.message.trim()
          ? error.response.message
          : "The suggestion was not created, but the patron's preferred pickup location was changed successfully.";
        state.partialSessionFailureMessage = `${detail} Sign in again to restore staff access before continuing.`;
        state.partialSessionFailureOwner = null;
        state.partialSessionFailureDetailAvailable = false;
        state.partialSessionFailureAfterQueueSequence = null;
      }
      showSignedOut(state.partialSessionFailureMessage ||
        'Your staff session ended or no longer has access. Sign in again.');
    });
    onAccessUnavailable(() => {
      const batch = state.bulkDeleteState;
      if (batch?.submitting && batch.currentItem) {
        batch.ledger.push({ ...batch.currentItem, outcome: 'forbidden/out_of_scope' });
        const remaining = batch.snapshot.items.slice(batch.snapshot.items.indexOf(batch.currentItem) + 1);
        batch.ledger.push(...remaining.map(item => ({ ...item, outcome: 'not_attempted' })));
        renderBulkLedger(batch);
        batch.currentItem = null;
      }
      showAccessUnavailable();
    });
    settingsController.bind();
    dom.newSuggestion.addEventListener('click', event => openStaffSuggestion(event.currentTarget));
    dom.bulkDelete.addEventListener('click', event => openBulkDelete(event.currentTarget));
    dom.bulkDeleteCopies.addEventListener('click', event => openBulkDelete(event.currentTarget));
    dom.bulkDeleteClose.addEventListener('click', () => closeBulkDelete());
    dom.bulkDeleteDialog.addEventListener('cancel', event => {
      event.preventDefault();
      closeBulkDelete();
    });
    dom.bulkDeleteScope.addEventListener('change', resetBulkDeletePreview);
    dom.bulkDeletePreview.addEventListener('click', previewBulkDelete);
    dom.bulkDeleteConfirmation.addEventListener('input', () => {
      const batch = state.bulkDeleteState;
      dom.bulkDeleteExecute.disabled = !batch?.snapshot?.items.length ||
        batch.submitting || dom.bulkDeleteScope.value !== batch.snapshot.scope ||
        dom.bulkDeleteConfirmation.value !== 'DELETE';
    });
    dom.bulkDeleteExecute.addEventListener('click', executeBulkDelete);
    dom.signOut.addEventListener('click', async () => {
      try {
        await authorizedJson('/api/asap/staff/sign-out', { method: 'POST' });
        showSignedOut('You are signed out.');
      } catch (error) {
        if (error.status !== 401) {
          dom.signedOutMessage.textContent = 'Sign out did not complete. Please try again.';
          announce('Sign out did not complete. Please try again.', 'error');
        }
      }
    });
    dom.refresh.addEventListener('click', () => loadQueue({ skipDeepLink: true }));
    dom.scope.addEventListener('change', () => {
      state.scope = dom.scope.value;
      dom.additionalCopyScope.value = state.scope;
      loadQueue({ skipDeepLink: true });
    });
    dom.additionalCopyRefresh.addEventListener('click', () => loadAdditionalCopies({ skipDeepLink: true }));
    dom.additionalCopyCreateReviewDone.addEventListener('click', () => {
      if (!state.unconfirmedCopyCreationAwaitingRefresh?.reviewReady ||
          state.additionalCopyStatus !== 'open' ||
          (state.scope !== 'all' && String(state.scope) !== String(state.unconfirmedCopyCreationAwaitingRefresh.libraryOrgId))) return;
      state.unconfirmedCopyCreationAwaitingRefresh.reviewed = true;
      clearCommittedSessionFallback(state.partialSessionFailureOwner);
      renderAdditionalCopyGrid();
      dom.additionalCopyRefresh.focus();
      announce('Additional-copy task list reviewed. You can open the request again if another task is needed.', 'success');
    });
    dom.additionalCopyScope.addEventListener('change', () => {
      state.scope = dom.additionalCopyScope.value;
      dom.scope.value = state.scope;
      loadAdditionalCopies({ skipDeepLink: true });
    });
    dom.operationsScope.addEventListener('change', () => {
      state.operationsScope = dom.operationsScope.value;
      loadOperations();
    });
    dom.runWorkflowNow.addEventListener('click', () =>
      runOperation('/api/asap/staff/workflow/run-now', 'Workflow run'));
    dom.runWeeklyNow.addEventListener('click', () =>
      runOperation('/api/asap/staff/workflow/weekly-summary/run-now?force=false', 'Weekly summary'));
    dom.forceWeeklyNow.addEventListener('click', () =>
      runOperation('/api/asap/staff/workflow/weekly-summary/run-now?force=true', 'Forced weekly summary'));
    dom.sendTestEmail.addEventListener('click', () =>
      runOperation('/api/asap/staff/email-operations/test', 'Test email'));
    dom.refreshOperations.addEventListener('click', () => loadOperations());
    for (const tab of dom.statusTabs) {
      tab.addEventListener('click', () => {
        state.status = tab.dataset.status;
        for (const item of dom.statusTabs) item.setAttribute('aria-selected', String(item === tab));
        renderGrid();
      });
      tab.addEventListener('keydown', event => {
        if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
        event.preventDefault();
        const index = dom.statusTabs.indexOf(tab);
        const offset = event.key === 'ArrowRight' ? 1 : -1;
        dom.statusTabs[(index + offset + dom.statusTabs.length) % dom.statusTabs.length].focus();
      });
    }
    dom.search.addEventListener('input', renderGrid);
    dom.claim.addEventListener('change', renderGrid);
    dom.tag.addEventListener('change', renderGrid);
    for (const tab of dom.additionalCopyStatusTabs) {
      tab.addEventListener('click', () => {
        state.additionalCopyStatus = tab.dataset.copyStatus;
        for (const item of dom.additionalCopyStatusTabs) item.setAttribute('aria-selected', String(item === tab));
        loadAdditionalCopies({ skipDeepLink: true });
      });
      tab.addEventListener('keydown', event => {
        if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
        event.preventDefault();
        const index = dom.additionalCopyStatusTabs.indexOf(tab);
        const offset = event.key === 'ArrowRight' ? 1 : -1;
        dom.additionalCopyStatusTabs[(index + offset + dom.additionalCopyStatusTabs.length) % dom.additionalCopyStatusTabs.length].focus();
      });
    }
    dom.additionalCopySearch.addEventListener('input', renderAdditionalCopyGrid);
    dom.additionalCopyClaim.addEventListener('change', renderAdditionalCopyGrid);
    dom.profile.addEventListener('submit', saveProfile);
    for (const tab of dom.viewTabs) tab.addEventListener('click', () => {
      if (dom.dialog.open && !closeDialog()) return;
      if (dom.staffSuggestionDialog.open) closeStaffSuggestion();
      switchView(tab.dataset.view);
    });
    dom.closeDialog.addEventListener('click', () => closeDialog());
    dom.dialog.addEventListener('cancel', event => {
      event.preventDefault();
      if (state.dialogMutationInFlight) closeDialog();
      else if (state.actionChoice) dismissActionChoice();
      else closeDialog();
    });
    dom.createCopyForm.addEventListener('submit', createAdditionalCopy);
    dom.cancelCreateCopy.addEventListener('click', closeAdditionalCopyCreateDialog);
    dom.createCopyDialog.addEventListener('cancel', event => {
      event.preventDefault();
      closeAdditionalCopyCreateDialog();
    });
    dom.staffSuggestionForm.addEventListener('submit', submitStaffSuggestion);
    dom.closeStaffSuggestion.addEventListener('click', () => closeStaffSuggestion());
    dom.staffSuggestionDialog.addEventListener('cancel', event => {
      event.preventDefault();
      closeStaffSuggestion();
    });
  }

  return {
    async start() {
      bindEvents();
      await startSession();
    }
  };
}
