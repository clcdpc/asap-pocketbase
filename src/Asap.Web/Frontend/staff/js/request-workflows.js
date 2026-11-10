import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { byDisplayLabel } from './display-order.js';
import { positivePolarisId } from './research.js';
import { eligibleRowActions } from './request-row-actions.js';
import { actorKey } from './session-identity.js';
import { validRequestId } from './recent-requests.js';
import { element, icon, commandButton, statusLabel, labeledInput } from './ui.js';

const purchaseTaskOperationId = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function createRequestWorkflows({ context, editor, announce, request: send = authorizedJson }) {
  const { request, actor, root, scope: interactionScope, isCurrent, isPending } = context;
  const reads = createLatestLoad();
  const dialogForms = new WeakMap();
  let actionChoice = null;
  const isCurrentDialogRequest = () => isCurrent();
  const purchaseTask = snapshot => snapshot.status === 'suggestion' && snapshot.bibidStaffVerified === true &&
    positivePolarisId(snapshot.bibid) !== null && snapshot.autohold === false;
  function purchaseTaskOperationKey(requestId = request.id) {
    const key = actorKey(actor), id = String(requestId);
    return key && validRequestId(id)
      ? `asap.staff.purchaseTask.${encodeURIComponent(key)}.${id}` : null;
  }
  function readPurchaseTaskOperation(requestId = request.id) {
    const key = purchaseTaskOperationKey(requestId);
    if (!key) return null;
    try {
      const raw = window.sessionStorage.getItem(key);
      if (!raw) return null;
      const saved = JSON.parse(raw), body = saved?.body;
      if (saved?.actorKey !== actorKey(actor) || saved.requestId !== String(requestId) || !body ||
          body.action !== 'purchase' || body.requestType !== 'title_request' ||
          typeof body.version !== 'string' || !body.version || !purchaseTaskOperationId.test(body.operationId) ||
          typeof body.emailPurchaseReminder !== 'boolean' ||
          Object.keys(body).some(name => !['version', 'operationId', 'requestType', 'action', 'emailPurchaseReminder'].includes(name))) {
        return null;
      }
      return { key, raw, body: Object.freeze({ ...body }) };
    } catch {
      return null;
    }
  }
  function savePurchaseTaskOperation(body) {
    const key = purchaseTaskOperationKey();
    if (!key) return null;
    const raw = JSON.stringify({ actorKey: actorKey(actor), requestId: String(request.id), body });
    try {
      window.sessionStorage.setItem(key, raw);
      return window.sessionStorage.getItem(key) === raw ? { key, raw, body } : null;
    } catch {
      return null;
    }
  }
  function clearPurchaseTaskOperation(operationId = null, expectedBody = null) {
    const saved = readPurchaseTaskOperation();
    if (!saved || operationId && saved.body.operationId !== operationId ||
        expectedBody && JSON.stringify(saved.body) !== JSON.stringify(expectedBody)) return false;
    try {
      if (window.sessionStorage.getItem(saved.key) !== saved.raw) return false;
      window.sessionStorage.removeItem(saved.key);
      root.querySelector('.purchase-task-recovery')?.remove();
      return true;
    } catch {
      return false;
    }
  }
  const dependsOnAutomaticHold = (action, snapshot = request) => Boolean(snapshot.bibid) &&
    ['purchase', 'alreadyOwn', 'catalogFound'].includes(action);
  const allowRequestMutation = (snapshot, declaration) => {
    if (readPurchaseTaskOperation()) {
      announce('Recover the previous Purchase result before making another request change.', 'warning');
      return false;
    }
    return isCurrent() && context.admit(declaration);
  };
  const confirmCurrent = (snapshot, label, message, draft = null, tone = 'normal') =>
    context.confirm({ title: label, message, confirmLabel: label, tone }, draft);
  const mutateRequest = (snapshot, path, body, message, draft = null) => context.mutate(path, body, message, draft);
  const mutateOperation = (snapshot, operation, action, body, draft = null) => context.mutateOperation(operation, action, body, draft);
  const deleteTitleRequest = () => context.delete();
  const showAdditionalCopyPreview = (snapshot, opener) => context.openCopy(opener);
  function trackDialogFormDraft(form) {
    const value = control => control.type === 'checkbox' ? control.checked : control.value;
    const baseline = [...form.querySelectorAll('input, select, textarea')]
      .map(control => ({ control, value: value(control) }));
    const draft = interactionScope.register({ root: form,
      isDirty: () => baseline.some(item => value(item.control) !== item.value) });
    dialogForms.set(form, draft);
    return draft;
  }

  function cancelDialogFormDraft(form, returnFocus) {
    if (!form.isConnected || isPending()) return;
    interactionScope.release(dialogForms.get(form));
    context.endInlineAction(form);
    form.remove();
    if (returnFocus?.isConnected) returnFocus.focus();
    announce('Unsaved request changes discarded.');
  }

  function cancelAssignmentCandidateLoad() {
    reads.begin('assignment-candidates').abort();
  }

  function cancelPickupOptionsLoad() {
    reads.begin('pickup-options').abort();
  }

  function cancelActionChoiceLoad() {
    reads.begin('action-choice').abort();
    actionChoice = null;
  }

  function dismissActionChoice() {
    const choice = actionChoice;
    if (!choice) return;
    cancelActionChoiceLoad();
    interactionScope.release(dialogForms.get(choice.panel));
    context.endInlineAction(choice.panel);
    choice.panel.remove();
    if (choice.returnFocus?.isConnected) choice.returnFocus.focus();
  }

  function invalidateAutomaticHoldReview(choice, changed) {
    if (!choice || !dependsOnAutomaticHold(choice.action, choice.request)) return;
    choice.automaticHoldChanged = changed;
    choice.submit.disabled = changed || !choice.ready || choice.refreshRequired;
    let explanation = choice.panel.querySelector('.automatic-hold-review-invalidated');
    if (changed) {
      if (!explanation) {
        explanation = element('p', { className: 'automatic-hold-review-invalidated', role: 'alert',
          text: 'Save the automatic-hold change before continuing.' });
        choice.panel.insertBefore(explanation, choice.panel.querySelector('.form-actions'));
      }
    } else {
      explanation?.remove();
    }
  }

  function focusAutomaticHoldReview(choice) {
    const submit = choice?.submit;
    if (!submit) return;
    submit.classList.add('automatic-hold-review-focus');
    submit.addEventListener('blur', () => submit.classList.remove('automatic-hold-review-focus'), { once: true });
    submit.focus({ preventScroll: true, focusVisible: true });
  }

  function editorAutomaticHoldDraftChanged(changed) {
    invalidateAutomaticHoldReview(actionChoice, changed);
  }

  function preservedDraftsForEditorSave() {
    const choice = actionChoice;
    return choice?.automaticHoldChanged && choice.action === 'purchase' && choice.draft
      ? [choice.draft] : [];
  }

  function captureAutomaticHoldReview() {
    const choice = actionChoice;
    if (!choice?.automaticHoldChanged || !dependsOnAutomaticHold(choice.action, choice.request)) return null;
    return Object.freeze({ requestId: String(choice.request.id), status: choice.request.status,
      bibid: choice.request.bibid == null ? null : String(choice.request.bibid), action: choice.action,
      emailPurchaseReminder: choice.input?.type === 'checkbox' ? choice.input.checked : undefined });
  }

  function requireAutomaticHoldReviewRefresh(message, retry) {
    const choice = actionChoice;
    if (!choice?.panel.isConnected) return;
    choice.refreshRequired = true;
    choice.submit.disabled = true;
    choice.panel.querySelector('.automatic-hold-review-refresh')?.remove();
    const retryButton = commandButton('Retry current request', 'refresh', async () => {
      if (retryButton.disabled || actionChoice !== choice || !isCurrentDialogRequest()) return;
      retryButton.disabled = true;
      await retry();
      if (actionChoice === choice && retryButton.isConnected) retryButton.disabled = false;
    });
    choice.panel.append(element('div', { className: 'automatic-hold-review-refresh' }, [
      element('p', { role: 'alert', text: message }), retryButton
    ]));
  }

  function showForm(form, cancel) {
    context.beginInlineAction(form, cancel);
    const panel = root.querySelector('.request-workflow-panel');
    if (panel) panel.prepend(form);
    else root.querySelector('.action-bar')?.after(form);
  }

  function buildActionBar(request) {
    const bar = element('div', { className: 'action-bar', 'aria-label': 'Request actions' });
    const workflowBlocked = request.capabilities?.canChangeWorkflowState !== true;
    const allowedActions = new Set(request.capabilities?.allowedActions || []);
    let assign = null;
    if (request.status !== 'closed') {
      if (request.claimedByStaffUserId === actor?.id) {
        bar.append(commandButton('Unclaim', 'user-times', () => mutateSimple(request, 'unclaim')));
      } else if (!request.claimedByStaffUserId) {
        bar.append(commandButton('Claim', 'user-plus', event => showActionConfirmation(request, 'claim', 'Claim request',
          `Claim this request for yourself? It will remain in ${statusLabel(request.status)}.`,
          'Claim', () => mutateSimple(request, 'claim'), event.currentTarget), 'primary-button'));
      } else if (['admin', 'super_admin'].includes(actor?.role)) {
        bar.append(commandButton('Clear claim', 'user-times', event => showActionConfirmation(request, 'clear-claim', 'Clear claim',
          `Clear ${request.claimedByDisplayName || 'another staff member'}'s claim? The request will remain in ${statusLabel(request.status)} and become unclaimed.`,
          'Clear claim', () => mutateSimple(request, 'clear-claim'), event.currentTarget)));
      }
      assign = commandButton('Assign', 'users', event => showAssignment(request, event.currentTarget));
      if (request.status !== 'suggestion') bar.append(assign);
    }
    if (request.status === 'suggestion') {
      bar.append(
        commandButton('Purchase', 'shopping-cart', event => showActionChoice(request, 'purchase', event.currentTarget), 'primary-button', !allowedActions.has('purchase')),
        assign,
        commandButton('Already own', 'book', event => runAction(request, 'alreadyOwn', undefined, {}, null, event.currentTarget), 'secondary-button', !allowedActions.has('alreadyOwn')),
        commandButton('Close silently', 'archive', event => runAction(request, 'silentClose', undefined, {}, null, event.currentTarget), 'secondary-button', !allowedActions.has('silentClose')),
        commandButton('Reject', 'ban', event => showActionChoice(request, 'reject', event.currentTarget), 'danger-button', !allowedActions.has('reject'))
      );
    } else if (request.status === 'outstanding_purchase') {
      bar.append(commandButton(request.autohold ? 'Ready for hold' : 'Close without hold', 'arrow-right', event => runAction(request, 'catalogFound', undefined, {}, null, event.currentTarget),
        'primary-button', !allowedActions.has('catalogFound')));
    } else if (request.status === 'pending_hold') {
      bar.append(commandButton('Additional copy', 'clone', event => showAdditionalCopyPreview(request, event.currentTarget), 'secondary-button', !request.bibid));
      bar.append(commandButton('Pickup', 'map-marker', event => showPickup(request, event.currentTarget), 'secondary-button',
        workflowBlocked && request.capabilities?.blockingReason !== 'pickup_reconciliation_required'));
      if (request.capabilities?.canPlaceHold === true) {
        bar.append(commandButton('Place hold', 'bookmark', event => showActionConfirmation(request, 'place-hold', 'Place hold',
          `Place a Polaris hold for BIB ${request.bibid} and this patron? If the provider outcome is uncertain, recovery will be required before another attempt.`,
          'Place hold', () => mutateSimple(request, 'place-hold'), event.currentTarget), 'primary-button', workflowBlocked));
      }
      if ((request.workflowTags || []).includes('Hold exists (same patron)')) {
        bar.append(commandButton('Close duplicate', 'clone', event => runAction(request, 'closeDuplicate', undefined, {}, null, event.currentTarget), 'secondary-button', !allowedActions.has('closeDuplicate')));
      }
    } else if (request.status === 'hold_placed') {
      bar.append(commandButton('Additional copy', 'clone', event => showAdditionalCopyPreview(request, event.currentTarget), 'secondary-button', !request.bibid));
      bar.append(commandButton('Close request', 'check', event => runAction(request, 'close', undefined, {}, null, event.currentTarget), 'primary-button', !allowedActions.has('close')));
    } else if (request.status === 'closed') {
      if (allowedActions.has('reopen')) {
        bar.append(commandButton('Reopen', 'undo', event => runAction(request, 'reopen', undefined, {}, null, event.currentTarget), 'primary-button'));
      }
      if (['admin', 'super_admin'].includes(actor?.role)) {
        bar.append(commandButton('Permanently delete request', 'trash', event => showActionConfirmation(request, 'delete', 'Delete permanently',
          `Permanently delete closed title request ${request.id}? This cannot be undone. Its deletion audit will remain.`,
          'Delete permanently', deleteTitleRequest, event.currentTarget, 'danger'), 'danger-button'));
      }
    }
    if (request.capabilities && request.capabilities.canRetryIdentifierCheck) {
      bar.insertBefore(commandButton('Retry identifier check', 'refresh', () => mutateSimple(request, 'retry-identifier-check')), bar.querySelector('.danger-button'));
    }
    if (readPurchaseTaskOperation()) {
      for (const button of bar.querySelectorAll('button')) button.disabled = true;
    }
    return bar;
  }

  function mountPurchaseTaskRecovery() {
    const saved = readPurchaseTaskOperation();
    if (!saved || !isCurrentDialogRequest()) return false;
    const panel = element('section', { className: 'action-choice purchase-task-recovery', 'aria-labelledby': 'purchase-task-recovery-title' });
    const recover = element('button', { type: 'button', className: 'primary-button', text: 'Recover identical Purchase' });
    recover.addEventListener('click', async () => {
      if (recover.disabled || !isCurrentDialogRequest() || isPending()) return;
      recover.disabled = true;
      const result = await context.mutateRecovery(
        `/api/asap/staff/title-requests/${encodeURIComponent(request.id)}/action`, saved.body,
        'Purchase result recovered.');
      if (result?.operationResolved === true) {
        clearPurchaseTaskOperation(saved.body.operationId, saved.body);
      } else if (recover.isConnected) {
        recover.disabled = false;
      }
    });
    panel.append(element('h3', { id: 'purchase-task-recovery-title', text: 'Purchase result unconfirmed' }),
      element('p', { text: 'A previous Purchase may have created a purchasing task. Recover the original result before starting another action.' }),
      element('div', { className: 'form-actions' }, recover));
    const workflowPanel = root.querySelector('.request-workflow-panel');
    if (!workflowPanel) return false;
    workflowPanel.prepend(panel);
    recover.focus({ preventScroll: true });
    announce('A previous Purchase result is unconfirmed. Recover the same action before continuing.', 'warning');
    return true;
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

  function showActionChoice(request, action, returnFocus, preserved = null) {
    if (context.hasInlineAction() || !allowRequestMutation(request, { consumes: null })) return;
    cancelActionChoiceLoad();
    root.querySelector('.action-choice')?.remove();
    const createsPurchaseTask = action === 'purchase' && purchaseTask(request);
    const panel = element('form', { className: 'action-choice', 'aria-label': createsPurchaseTask ? 'Purchase an additional copy options' : `${action} options` });
    const heading = element('h3', { text: action === 'reject' ? 'Reject suggestion' : createsPurchaseTask ? 'Purchase an additional copy' : 'Purchase suggestion' });
    const submit = element('button', { type: 'submit', className: action === 'reject' ? 'danger-button' : 'primary-button',
      disabled: action === 'reject' }, action === 'reject' ? 'Reject and close' : createsPurchaseTask ? 'Create purchasing task' : 'Confirm purchase');
    const cancel = commandButton('Cancel', 'times', dismissActionChoice);
    let input;
    if (action === 'purchase') {
      const entersPendingHold = Boolean(request.bibid);
      input = createsPurchaseTask
        ? element('input', { type: 'checkbox', checked: actor.additionalCopyReminderDefault })
        : entersPendingHold ? null : element('input', { type: 'checkbox', checked: actor.purchaseReminderDefault });
      const explanation = createsPurchaseTask
        ? 'A matching BIB exists and the patron does not want a hold. A purchasing task will be created and this request will close without placing a hold.'
        : request.bibid
          ? `${request.autohold ? 'Purchase moves this verified BIB to Pending hold for automatic hold placement' : 'Automatic hold is off. Purchase closes this request without placing a hold'} after server verification. A purchase reminder does not apply. The final state comes from the server.`
          : 'Purchase moves this request to Outstanding purchase. A reminder is optional.';
      panel.append(heading, element('p', { text: explanation }));
      if (input) panel.append(element('label', { className: 'check-field' },
        [input, element('span', { text: createsPurchaseTask ? 'Email me a purchase reminder' : 'Send purchase reminder' })]));
    } else {
      input = element('select', { 'aria-label': 'Rejection template' });
      input.append(element('option', { value: '', text: 'Default rejection email' }));
      panel.append(heading, element('p', { text: 'Reject closes this request. A patron rejection email is queued only when a template and delivery are available.' }),
        labeledInput('Rejection template', input));
    }
    const choice = { request, action, panel, returnFocus, input, submit, createsPurchaseTask,
      ready: action !== 'reject', automaticHoldChanged: false };
    actionChoice = choice;
    panel.append(element('div', { className: 'form-actions' }, [submit, cancel]));
    let draft = action === 'reject' ? null : trackDialogFormDraft(panel);
    choice.draft = draft;
    panel.addEventListener('submit', async event => {
      event.preventDefault();
      if (submit.disabled || actionChoice !== choice || !isCurrentDialogRequest(request, 'title_request')) return;
      submit.disabled = true;
      try {
        await runAction(request, action, undefined, action === 'purchase'
          ? { emailPurchaseReminder: input?.checked === true }
          : { rejectionTemplateId: input.value || null }, draft);
      } finally {
        if (actionChoice === choice && panel.isConnected) submit.disabled = false;
      }
    });
    showForm(panel, dismissActionChoice);
    (input || submit).focus({ preventScroll: true });
    if (preserved && input?.type === 'checkbox' && typeof preserved.emailPurchaseReminder === 'boolean') {
      input.checked = preserved.emailPurchaseReminder;
      choice.draft = draft = trackDialogFormDraft(panel);
    }
    if (action !== 'reject') return;
    const load = reads.begin('action-choice');
    send(`/api/asap/staff/title-requests/${encodeURIComponent(request.id)}/rejection-templates`,
      { signal: load.signal })
      .then(data => {
        if (!load.isCurrent() || actionChoice !== choice ||
            !isCurrentDialogRequest(request, 'title_request')) return;
        for (const item of data.items || []) {
          input.append(element('option', { value: String(item.id), text: item.name }));
        }
        input.value = data.defaultTemplateId || '';
        draft = trackDialogFormDraft(panel);
        choice.draft = draft;
        choice.ready = true;
        submit.disabled = false;
      })
      .catch(error => {
        if (load.isCurrent() && actionChoice === choice &&
            !(isAbortError(error) && load.signal.aborted) && error.status !== 401) {
          announce(error.message || 'Rejection templates could not be loaded.', 'error');
        }
      })
      .finally(() => reads.finish('action-choice', load.token));
  }

  function showActionConfirmation(request, action, title, message, confirmLabel, onConfirm, returnFocus = null, tone = 'normal') {
    if (context.hasInlineAction() || !allowRequestMutation(request, { consumes: null })) return;
    cancelActionChoiceLoad();
    const heading = element('h3', { id: 'request-action-confirmation-title', text: title });
    const panel = element('form', { className: 'action-choice', 'aria-labelledby': heading.id });
    const submit = element('button', { type: 'submit', className: tone === 'danger' ? 'danger-button' : 'primary-button' }, confirmLabel);
    const cancel = commandButton('Cancel', 'times', dismissActionChoice);
    const choice = { request, action, panel, returnFocus, submit, ready: true, automaticHoldChanged: false };
    actionChoice = choice;
    panel.append(heading, element('p', { text: message }), element('div', { className: 'form-actions' }, [submit, cancel]));
    panel.addEventListener('submit', async event => {
      event.preventDefault();
      if (submit.disabled || actionChoice !== choice || !isCurrentDialogRequest(request, 'title_request')) return;
      submit.disabled = true;
      try {
        await onConfirm();
      } finally {
        if (actionChoice === choice && panel.isConnected) submit.disabled = false;
      }
    });
    showForm(panel, dismissActionChoice);
    submit.focus({ preventScroll: true });
  }

  function restoreAutomaticHoldReview(saved) {
    if (!saved || saved.requestId !== String(request.id) || !isCurrentDialogRequest()) return false;
    const allowed = new Set(request.capabilities?.allowedActions || []);
    if (request.status !== saved.status || String(request.bibid ?? '') !== String(saved.bibid ?? '') ||
        !dependsOnAutomaticHold(saved.action, request) || !allowed.has(saved.action) ||
        saved.action === 'alreadyOwn' && (request.bibidStaffVerified !== true || positivePolarisId(request.bibid) === null)) {
      announce('The request or its permissions changed. Review the current request before continuing.', 'warning');
      return false;
    }
    const buttonLabel = saved.action === 'purchase' ? 'Purchase'
      : saved.action === 'alreadyOwn' ? 'Already own'
        : request.autohold ? 'Ready for hold' : 'Close without hold';
    const trigger = [...root.querySelectorAll('.action-bar button')]
      .find(button => button.textContent.trim() === buttonLabel);
    if (!trigger || trigger.disabled) {
      announce('The request or its permissions changed. Review the current request before continuing.', 'warning');
      return false;
    }
    if (saved.action === 'purchase') showActionChoice(request, 'purchase', trigger, saved);
    else runAction(request, saved.action, undefined, {}, null, trigger);
    if (!actionChoice) return false;
    invalidateAutomaticHoldReview(actionChoice, editor.isAutomaticHoldDraftChanged());
    focusAutomaticHoldReview(actionChoice);
    return true;
  }

  async function runAction(request, action, targetStatus, choices = {}, draft = null, returnFocus = null, confirmed = false) {
    if (action === 'alreadyOwn' &&
        (request.bibidStaffVerified !== true || positivePolarisId(request.bibid) === null)) {
      editor.beginBibLink(returnFocus);
      return;
    }
    const entersPendingHold = action === 'catalogFound' || action === 'alreadyOwn' ||
      targetStatus === 'pending_hold' || action === 'purchase' && Boolean(request.bibid);
    if (!allowRequestMutation(request, { consumes: draft })) {
      if (entersPendingHold && editor.isDirty() && editor.draft() !== draft && !isPending()) {
        announce(editor.isVerifiedDraft()
          ? 'Save the current request edits before moving to Pending hold.'
          : 'Search Polaris, select the matching BIB, and save it before moving to Pending hold.', 'error');
      }
      return;
    }
    if (entersPendingHold) {
      if (!editor.hasAuthoritativeBib()) {
        announce('Search Polaris, select the matching BIB, and save it before moving to Pending hold.', 'error');
        return;
      }
    }
    const confirmations = {
      alreadyOwn: `Record that the library already owns this title and ${request.autohold ? 'move the verified BIB to Pending hold' : 'close it without a hold'}?`,
      catalogFound: `${request.autohold ? 'Move this verified catalog title to Pending hold' : 'Close this verified catalog title without a hold'}?`,
      silentClose: 'Close this suggestion without a rejection email? It will leave the active queue.',
      closeDuplicate: 'Close this request as a duplicate of an existing patron hold? No new hold will be placed.',
      close: 'Close this hold-placed request? Its placed-hold history will remain.',
      reopen: 'Reopen this closed request as a suggestion? The action will assign a manual claim to you.'
    };
    const labels = { alreadyOwn: 'Already own', catalogFound: request.autohold ? 'Ready for hold' : 'Close without hold',
      silentClose: 'Close silently', closeDuplicate: 'Close duplicate', close: 'Close request', reopen: 'Reopen request' };
    if (confirmations[action] && !confirmed) {
      showActionConfirmation(request, action, labels[action], confirmations[action], labels[action],
        () => runAction(request, action, targetStatus, choices, draft, returnFocus, true), returnFocus);
      return;
    }
    const createsPurchaseTask = action === 'purchase' && purchaseTask(request);
    const body = {
      version: request.version,
      action,
      status: targetStatus,
      ...choices
    };
    if (createsPurchaseTask) {
      if (typeof window.crypto?.randomUUID !== 'function') {
        announce('This browser cannot save a safe Purchase retry. Refresh or use a supported browser before continuing.', 'error');
        return;
      }
      Object.assign(body, { operationId: window.crypto.randomUUID(), requestType: 'title_request' });
      if (!savePurchaseTaskOperation(body)) {
        announce('This browser could not save the Purchase action for safe retry. Enable site storage before continuing.', 'error');
        return;
      }
    }
    const result = await mutateRequest(request, `/api/asap/staff/title-requests/${request.id}/action`, body,
      createsPurchaseTask ? 'Purchasing task created.' : 'Workflow action completed.', draft);
    if (action === 'alreadyOwn' && ['bib_not_found', 'invalid_bib', 'bib_unverified', 'bib_required'].includes(result?.code)) {
      dismissActionChoice();
      editor.beginBibLink(returnFocus, result.message);
    }
  }

  async function showAssignment(request, returnFocus) {
    if (!isCurrentDialogRequest(request, 'title_request') || isPending() || context.hasInlineAction()) return;
    const load = reads.begin('assignment-candidates');
    announce('Loading eligible staff...');
    try {
      const result = await send(`/api/asap/staff/assignment-candidates?libraryOrgId=${request.libraryOrgId}`, {
        signal: load.signal
      });
      if (!load.isCurrent() || !isCurrentDialogRequest(request, 'title_request') || isPending() || context.hasInlineAction()) return;
      const select = element('select', { 'aria-label': 'Assign to staff member' });
      const candidates = byDisplayLabel(result.candidates, item => item.displayName || item.userPrincipalName || `Staff ${item.id}`);
      for (const candidate of candidates) {
        select.append(element('option', {
          value: candidate.id,
          text: candidate.displayName || candidate.userPrincipalName || `Staff ${candidate.id}`
        }));
      }
      const form = element('form', { className: 'inline-form' }, [
        labeledInput('Assign request', select),
        element('button', { type: 'submit', className: 'primary-button', disabled: candidates.length === 0 }, [icon('user-plus'), 'Assign'])
      ]);
      form.append(commandButton('Cancel', 'times', () => cancelDialogFormDraft(form, returnFocus)));
      const draft = trackDialogFormDraft(form);
      form.addEventListener('submit', async event => {
        event.preventDefault();
        if (!form.isConnected || !isCurrentDialogRequest(request, 'title_request')) return;
        await mutateRequest(request, `/api/asap/staff/title-requests/${request.id}/assign`, {
          version: request.version,
          assigneeId: select.value
        }, 'Request assigned.', draft);
      });
      showForm(form, () => cancelDialogFormDraft(form, returnFocus));
        if (!root.querySelector('.request-details-panel')?.hidden) select.focus({ preventScroll: true });
      announce(candidates.length ? 'Choose an assignee.' : 'No eligible staff are available.');
    } catch (error) {
      if (load.isCurrent() && isCurrentDialogRequest(request, 'title_request') &&
          error.status !== 401 && !(isAbortError(error) && load.signal.aborted)) {
        announce(error.message || 'Assignable staff could not be loaded.', 'error');
      }
    } finally {
      reads.finish('assignment-candidates', load.token);
    }
  }

  async function showPickup(request, returnFocus) {
    if (!isCurrentDialogRequest(request, 'title_request') || isPending() || context.hasInlineAction()) return;
    const load = reads.begin('pickup-options');
    const clearLoading = announce('Loading current pickup preference...');
    try {
      const options = await send(`/api/asap/staff/title-requests/${request.id}/pickup-options`, {
        method: 'POST',
        body: {},
        signal: load.signal
      });
      if (!load.isCurrent() || !isCurrentDialogRequest(request, 'title_request') || isPending() || context.hasInlineAction()) return;
      const select = element('select', { 'aria-label': 'Preferred pickup branch', required: 'required',
        'aria-required': 'true', 'aria-describedby': 'request-pickup-help request-pickup-warning' });
      for (const branch of options.pickupBranches || []) {
        select.append(element('option', { value: branch.id, text: branch.label }));
      }
      if (options.selectedPickupBranchId) select.value = String(options.selectedPickupBranchId);
      else {
        select.prepend(element('option', { value: '', text: 'Choose an eligible pickup branch' }));
        select.value = '';
      }
      const form = element('form', { className: 'inline-form' }, [
        labeledInput('Preferred pickup branch', select),
        element('p', { id: 'request-pickup-help', className: 'field-help wide',
          text: "This changes the patron's preferred pickup location in Polaris. Every unfilled request uses the live default when its hold is placed; existing holds are not moved." }),
        element('button', { type: 'submit', className: 'primary-button', disabled: options.readOnly }, [icon('map-marker'), 'Update pickup'])
      ]);
      const warning = element('p', { id: 'request-pickup-warning', className: 'wide', role: 'status', 'aria-live': 'polite',
        text: options.pickupBranchWarning || '' });
      warning.hidden = !options.pickupBranchWarning;
      form.append(warning);
      form.addEventListener('submit', async event => {
        event.preventDefault();
        if (!form.isConnected || !isCurrentDialogRequest(request, 'title_request')) return;
        await mutateRequest(request, `/api/asap/staff/title-requests/${request.id}/pickup-preference`, {
          version: options.version,
          preferredPickupBranchId: Number(select.value),
          currentPreferredPickupBranchIdAtLoad: options.currentPreferredPickupBranchId,
          currentPreferredPickupBranchObservedAtLoad: true
        }, 'Pickup preference updated.', draft);
      });
      if (request.pickupOperation && actor?.role === 'super_admin') {
        const acknowledged = element('input', { type: 'checkbox' });
        form.append(labeledInput('I inspected the original invocation and confirmed it has ended', acknowledged));
        form.append(commandButton('Accept observed live preference', 'check', async () => {
          if (!acknowledged.checked || !form.isConnected || !isCurrentDialogRequest(request, 'title_request')) {
            announce('Confirm the original invocation has ended before resolving its operation.', 'error');
            return;
          }
          await mutateRequest(request, `/api/asap/staff/pickup-operations/${request.pickupOperation.id}/reconcile`, {
            version: options.version,
            currentPreferredPickupBranchIdAtLoad: options.currentPreferredPickupBranchId,
            currentPreferredPickupBranchObservedAtLoad: true,
            confirmOriginalDispatchEnded: true
          }, 'Observed pickup preference reconciled.', draft);
        }, 'secondary-button'));
      }
      form.append(commandButton('Cancel', 'times', () => cancelDialogFormDraft(form, returnFocus)));
      const draft = trackDialogFormDraft(form);
      showForm(form, () => cancelDialogFormDraft(form, returnFocus));
        if (!root.querySelector('.request-details-panel')?.hidden) select.focus({ preventScroll: true });
    } catch (error) {
      if (load.isCurrent() && isCurrentDialogRequest(request, 'title_request') &&
          error.status !== 401 && !(isAbortError(error) && load.signal.aborted)) {
        announce(error.message || 'Pickup choices could not be loaded.', 'error');
      }
    } finally {
      if (typeof clearLoading === 'function') clearLoading();
      reads.finish('pickup-options', load.token);
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
        if (!await confirmCurrent(request, 'Reconcile hold',
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
        element('button', { type: 'submit', className: 'danger-button' }, [icon('check-circle'), 'Resolve operation']),
        commandButton('Revert resolution changes', 'undo', () => {
          if (!form.isConnected || isPending()) return;
          form.reset();
          updateEvidence();
          interactionScope.touch();
          outcome.focus();
          announce('Unsaved resolution changes discarded.');
        })
      ])
    );
    updateEvidence();
    const draft = trackDialogFormDraft(form);
    form.addEventListener('submit', async event => {
      event.preventDefault();
      if (!form.isConnected || !allowRequestMutation(request, { consumes: draft })) return;
      const provenFinalHoldId = finalHoldId.disabled ? null : positivePolarisId(finalHoldId.value);
      if (!finalHoldId.disabled && provenFinalHoldId === null) {
        announce('Enter a positive Polaris hold ID no larger than 2147483647.');
        finalHoldId.focus();
        return;
      }
      if (!await confirmCurrent(request, 'Resolve operation',
        `Resolve hold operation ${operation.id}, attempt ${operation.attemptNumber}, as ${outcome.value.replaceAll('_', ' ')} using ${evidence.selectedOptions[0]?.textContent || evidence.value}? The recorded evidence will determine whether this request has a placed hold or may be retried.`, draft, 'danger')) return;
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
        provenFinalHoldId,
        originalExecutorExcluded: !excluded.disabled && excluded.checked,
        executorExclusionAttested: !exclusionAttested.disabled && exclusionAttested.checked,
        executorExclusionReference: exclusionReference.disabled ? null : exclusionReference.value,
        executorExclusionExplanation: exclusionExplanation.disabled ? null : exclusionExplanation.value
      }, draft);
    });
    return form;
  }

  function invalidate() { cancelAssignmentCandidateLoad(); cancelPickupOptionsLoad(); reads.begin('action-choice').abort(); }
  return {
    buildActions: () => buildActionBar(request),
    buildHold: () => request.holdOperation ? buildHoldOperation(request, request.holdOperation) : null,
    mountPurchaseTaskRecovery,
    clearPurchaseTaskOperation,
    editorAutomaticHoldDraftChanged,
    preservedDraftsForEditorSave,
    captureAutomaticHoldReview,
    restoreAutomaticHoldReview,
    requireAutomaticHoldReviewRefresh,
    runAction: (action, options = {}) => runAction(request, action, undefined, {}, null,
      options.returnFocus || null, options.confirmed === true),
    startAction(action) {
      if (!isCurrent() || isPending()) return;
      if (action === 'edit') { context.showDetails?.(); root.querySelector('.edit-form input')?.focus(); return; }
      // Re-evaluate against the authoritative detail snapshot, including claim,
      // actor scope and operation protection, before invoking its existing UI.
      const label = eligibleRowActions(request, actor).find(item => item.key === action)?.label;
      const button = [...root.querySelectorAll('.action-bar button')].find(control => control.textContent.trim() === label);
      if (button && !button.disabled) { button.focus(); button.click(); }
      else announce('That action is no longer available. Review the current request.', 'warning');
    },
    escape() {
      return context.cancelInlineAction();
    },
    invalidate,
    dispose() { invalidate(); actionChoice = null; }
  };
}
