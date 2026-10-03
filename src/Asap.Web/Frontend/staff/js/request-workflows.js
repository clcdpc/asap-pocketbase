import { authorizedJson, isAbortError } from './http.js';
import { createLatestLoad } from '../../shared/latest-load.js';
import { positivePolarisId } from './research.js';
import { element, icon, commandButton, statusLabel, labeledInput } from './ui.js';

export function createRequestWorkflows({ context, editor, announce, request: send = authorizedJson }) {
  const { request, actor, root, scope: interactionScope, isCurrent, isPending } = context;
  const reads = createLatestLoad();
  const dialogForms = new WeakMap();
  let actionChoice = null;
  const isCurrentDialogRequest = () => isCurrent();
  const allowRequestMutation = (snapshot, declaration) => isCurrent() && context.admit(declaration);
  const confirmCurrent = (snapshot, type, message, draft = null) => context.confirm(message, draft);
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
    choice.panel.remove();
    if (choice.returnFocus?.isConnected) choice.returnFocus.focus();
  }

  function buildActionBar(request) {
    const bar = element('div', { className: 'action-bar', 'aria-label': 'Request actions' });
    const workflowBlocked = request.capabilities?.canChangeWorkflowState !== true;
    const allowedActions = new Set(request.capabilities?.allowedActions || []);
    if (request.status !== 'closed') {
      if (request.claimedByStaffUserId === actor?.id) {
        bar.append(commandButton('Unclaim', 'user-times', () => mutateSimple(request, 'unclaim')));
      } else if (!request.claimedByStaffUserId) {
        bar.append(commandButton('Claim', 'user-plus', () => mutateSimple(request, 'claim'), 'primary-button'));
      } else if (['admin', 'super_admin'].includes(actor?.role)) {
        bar.append(commandButton('Clear claim', 'user-times', () => {
          if (confirmCurrent(request, 'title_request',
            `Clear ${request.claimedByDisplayName || 'another staff member'}'s claim? The request will remain in ${statusLabel(request.status)} and become unclaimed.`)) {
            mutateSimple(request, 'clear-claim');
          }
        }));
      }
      bar.append(commandButton('Assign', 'users', event => showAssignment(request, event.currentTarget)));
    }
    if (request.status === 'suggestion') {
      bar.append(
        commandButton('Purchase', 'shopping-cart', event => showActionChoice(request, 'purchase', event.currentTarget), 'primary-button', !allowedActions.has('purchase')),
        commandButton('Already own', 'book', () => runAction(request, 'alreadyOwn'), 'secondary-button', !allowedActions.has('alreadyOwn')),
        commandButton('Reject', 'ban', event => showActionChoice(request, 'reject', event.currentTarget), 'danger-button', !allowedActions.has('reject')),
        commandButton('Close silently', 'archive', () => runAction(request, 'silentClose'), 'secondary-button', !allowedActions.has('silentClose'))
      );
    } else if (request.status === 'outstanding_purchase') {
      bar.append(commandButton(request.autohold ? 'Ready for hold' : 'Close without hold', 'arrow-right', () => runAction(request, 'catalogFound'),
        'primary-button', !allowedActions.has('catalogFound')));
    } else if (request.status === 'pending_hold') {
      bar.append(commandButton('Additional copy', 'clone', event => showAdditionalCopyPreview(request, event.currentTarget), 'secondary-button', !request.bibid));
      bar.append(commandButton('Pickup', 'map-marker', event => showPickup(request, event.currentTarget), 'secondary-button',
        workflowBlocked && request.capabilities?.blockingReason !== 'pickup_reconciliation_required'));
      if (request.capabilities?.canPlaceHold === true) {
        bar.append(commandButton('Place hold', 'bookmark', () => {
          if (confirmCurrent(request, 'title_request',
            `Place a Polaris hold for BIB ${request.bibid} and this patron? If the provider outcome is uncertain, recovery will be required before another attempt.`)) {
            mutateSimple(request, 'place-hold');
          }
        }, 'primary-button', workflowBlocked));
      }
      if ((request.workflowTags || []).includes('Hold exists (same patron)')) {
        bar.append(commandButton('Close duplicate', 'clone', () => runAction(request, 'closeDuplicate'), 'secondary-button', !allowedActions.has('closeDuplicate')));
      }
    } else if (request.status === 'hold_placed') {
      bar.append(commandButton('Additional copy', 'clone', event => showAdditionalCopyPreview(request, event.currentTarget), 'secondary-button', !request.bibid));
      bar.append(commandButton('Close request', 'check', () => runAction(request, 'close'), 'primary-button', !allowedActions.has('close')));
    } else if (request.status === 'closed') {
      if (allowedActions.has('reopen')) {
        bar.append(commandButton('Reopen', 'undo', () => runAction(request, 'reopen'), 'primary-button'));
      }
      if (['admin', 'super_admin'].includes(actor?.role)) {
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
    if (!allowRequestMutation(request, { consumes: null })) return;
    cancelActionChoiceLoad();
    root.querySelector('.action-choice')?.remove();
    const panel = element('form', { className: 'action-choice', 'aria-label': `${action} options` });
    const heading = element('h3', { text: action === 'reject' ? 'Reject suggestion' : 'Purchase suggestion' });
    const submit = element('button', { type: 'submit', className: action === 'reject' ? 'danger-button' : 'primary-button',
      disabled: action === 'reject' }, action === 'reject' ? 'Reject' : 'Purchase');
    const cancel = commandButton('Cancel', 'times', dismissActionChoice);
    let input;
    if (action === 'purchase') {
      const entersPendingHold = Boolean(request.bibid);
      input = entersPendingHold ? null : element('input', { type: 'checkbox', checked: actor.purchaseReminderDefault });
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
    actionChoice = choice;
    panel.append(element('div', { className: 'form-actions' }, [submit, cancel]));
    let draft = action === 'reject' ? null : trackDialogFormDraft(panel);
    panel.addEventListener('submit', async event => {
      event.preventDefault();
      if (actionChoice !== choice || !isCurrentDialogRequest(request, 'title_request')) return;
      submit.disabled = true;
      try {
        await runAction(request, action, undefined, action === 'purchase'
          ? { emailPurchaseReminder: input?.checked === true }
          : { rejectionTemplateId: input.value || null }, draft);
      } finally {
        if (actionChoice === choice && panel.isConnected) submit.disabled = false;
      }
    });
    root.querySelector('.action-bar')?.after(panel);
    (input || submit).focus();
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

  async function runAction(request, action, targetStatus, choices = {}, draft = null) {
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
      purchase: request.bibid
        ? `${request.autohold ? 'Move this request to Pending hold' : 'Close this request without a hold'} using its verified BIB? The server will determine the final state.`
        : 'Record this purchase decision and move the request to Outstanding purchase?',
      alreadyOwn: `Record that the library already owns this title and ${request.autohold ? 'move the verified BIB to Pending hold' : 'close it without a hold'}?`,
      catalogFound: `${request.autohold ? 'Move this verified catalog title to Pending hold' : 'Close this verified catalog title without a hold'}?`,
      reject: 'Reject and close this request? A patron rejection email is queued only when a template and delivery are available.',
      silentClose: 'Close this suggestion without a rejection email? It will leave the active queue.',
      closeDuplicate: 'Close this request as a duplicate of an existing patron hold? No new hold will be placed.',
      close: 'Close this hold-placed request? Its placed-hold history will remain.',
      reopen: 'Reopen this closed request as a suggestion? The action will assign a manual claim to you.'
    };
    if (confirmations[action] && !confirmCurrent(request, 'title_request', confirmations[action], draft)) return;
    await mutateRequest(request, `/api/asap/staff/title-requests/${request.id}/action`, {
      version: request.version,
      action,
      status: targetStatus,
      ...choices
    }, 'Workflow action completed.', draft);
  }

  async function showAssignment(request, returnFocus) {
    if (!isCurrentDialogRequest(request, 'title_request') || isPending()) return;
    const load = reads.begin('assignment-candidates');
    announce('Loading eligible staff...');
    try {
      const result = await send(`/api/asap/staff/assignment-candidates?libraryOrgId=${request.libraryOrgId}`, {
        signal: load.signal
      });
      if (!load.isCurrent() || !isCurrentDialogRequest(request, 'title_request') || isPending()) return;
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
      root.prepend(form);
      select.focus();
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
    if (!isCurrentDialogRequest(request, 'title_request') || isPending()) return;
    const load = reads.begin('pickup-options');
    announce('Loading current pickup preference...');
    try {
      const options = await send(`/api/asap/staff/title-requests/${request.id}/pickup-options`, {
        method: 'POST',
        body: {},
        signal: load.signal
      });
      if (!load.isCurrent() || !isCurrentDialogRequest(request, 'title_request') || isPending()) return;
      const select = element('select', { 'aria-label': 'Preferred pickup branch' });
      for (const branch of options.pickupBranches || []) {
        select.append(element('option', { value: branch.id, text: branch.label }));
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
      root.prepend(form);
      select.focus();
      announce('Current pickup preference loaded.');
    } catch (error) {
      if (load.isCurrent() && isCurrentDialogRequest(request, 'title_request') &&
          error.status !== 401 && !(isAbortError(error) && load.signal.aborted)) {
        announce(error.message || 'Pickup choices could not be loaded.', 'error');
      }
    } finally {
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
      if (!confirmCurrent(request, 'title_request',
        `Resolve hold operation ${operation.id}, attempt ${operation.attemptNumber}, as ${outcome.value.replaceAll('_', ' ')} using ${evidence.selectedOptions[0]?.textContent || evidence.value}? The recorded evidence will determine whether this request has a placed hold or may be retried.`, draft)) return;
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
    runAction: action => runAction(request, action),
    escape() { if (!actionChoice || isPending()) return false; dismissActionChoice(); return true; },
    invalidate,
    dispose() { invalidate(); actionChoice = null; }
  };
}
