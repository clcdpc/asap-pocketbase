import { authorizedJson } from '../http.js';
import { showToast, showAlert, showConfirm } from '../dialogs.js';
import { actionErrorMessage } from './utils.js';
import { confirmDuplicateOpenRequestClose } from './confirm-duplicate.js';
import { rememberRecentSuggestion, updateRecentSuggestion, renderRecentSuggestionsSwitcher } from '../recent-suggestions.js';
import { collectEditCustomFieldValues } from '../request-custom-fields.js';
import { editRequestIdentity, findWorkflowRow } from '../request-identity.mjs';
import { clearMatchingRequestSelection } from '../app/url-utils.js';

export async function submitTitleRequestAction(identity, payload, options = {}) {
  if (identity?.type !== 'title_request' || !String(identity.id ?? '').trim()) return false;
  const id = String(identity.id).trim();
  const {
    onRefresh,
    beforeDialogsClose,
    dialogsToClose = ['editModal'],
    ownsCurrentUi = () => true,
    isSessionCurrent = () => true
  } = options;

  let response;
  let ownedAtCompletion = false;
  const ownsUiNow = () => {
    if (!isSessionCurrent()) return false;
    try {
      return ownsCurrentUi();
    } catch (error) {
      console.error('Could not verify title-request dialog ownership:', error);
      return false;
    }
  };
  const refreshAfterAction = async silent => {
    if (!isSessionCurrent() || typeof onRefresh !== 'function') return;
    try {
      await onRefresh({ silent });
    } catch (error) {
      console.error('Could not refresh the staff view after a title-request action:', error);
    }
  };
  try {
    response = await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/action`, {
      method: 'POST',
      body: payload
    });
  } catch (err) {
    console.error('submitTitleRequestAction failed:', err);
    if (err.response) {
      err.code = err.response.code || '';
    }
    if (!ownsUiNow()) {
      await refreshAfterAction(true);
      return false;
    }
    if (err && err.code === 'duplicate_open_request') {
      const closed = await confirmDuplicateOpenRequestClose(err, { type: 'title_request', id }, ownsUiNow);
      if (closed) {
        const stillOwnsUi = ownsUiNow();
        if (stillOwnsUi) {
          try {
            if (typeof beforeDialogsClose === 'function') beforeDialogsClose();
            clearMatchingRequestSelection({ type: 'title_request', id });
            dialogsToClose.forEach(dialogId => {
              const el = document.getElementById(dialogId);
              if (el?.open) el.close();
            });
          } catch (error) {
            console.error('Could not close the duplicate title-request dialog:', error);
          }
        }
        await refreshAfterAction(!stillOwnsUi);
      }
      return false;
    }
    if (isSessionCurrent()) await showAlert(err.message || 'Error updating suggestion');
    return false;
  }

  // The write is committed. UI callbacks must never turn its success into a reported mutation failure.
  try {
    const updatedRecord = response?.request || response;
    if (isSessionCurrent()) {
      rememberRecentSuggestion(updatedRecord);
      updateRecentSuggestion(updatedRecord);
      renderRecentSuggestionsSwitcher();
      const ownsUi = ownsUiNow();
      if (ownsUi) {
        ownedAtCompletion = true;
        try {
          if (typeof beforeDialogsClose === 'function') beforeDialogsClose();
          clearMatchingRequestSelection({ type: 'title_request', id });
          dialogsToClose.forEach(dialogId => {
            const el = document.getElementById(dialogId);
            if (el?.open) el.close();
          });
        } catch (error) {
          console.error('Could not close the originating title-request dialog:', error);
        }
      }
    }
    const actionValue = payload.action;
    const nextStatus = payload.status;
    const reminder = response?.purchaseReminderEmail;

    if (isSessionCurrent()) {
      if (actionValue === 'purchase') {
        if (reminder?.requested && reminder.sent) {
          showToast('Purchase saved and reminder email sent.', 'success');
        } else if (reminder?.requested) {
          showToast(reminder.message || 'Purchase saved, but the reminder email could not be sent.', 'warning');
        } else {
          showToast('Purchase saved.', 'success');
        }
      } else if (actionValue === 'additionalCopy' && response?.additionalCopyRequestId) {
        const message = reminder?.requested
          ? reminder.queued
            ? 'Additional-copy task created, request queued, and reminder email queued.'
            : 'Additional-copy task created and request queued; reminder email was not queued.'
          : 'Additional-copy task created and request queued.';
        showToast(message, reminder?.requested && !reminder.queued ? 'warning' : 'success');
      } else if (actionValue === 'additionalCopy') {
        showToast('Request updated; additional-copy task was not confirmed. Refresh to verify.', 'warning');
      } else if (nextStatus === 'pending_hold') {
        showToast(`Request queued for hold (BIB ${payload.bibid || 'N/A'}).`, 'success');
      } else {
        showToast('Suggestion updated.', 'success');
      }
    }

    if (isSessionCurrent() && updatedRecord && updatedRecord.status && updatedRecord.status !== nextStatus) {
      const statusNames = {
        'outstanding_purchase': 'Pending purchase',
        'pending_hold': 'Pending hold',
        'hold_placed': 'Hold placed',
        'closed': 'Closed'
      };

      let reason = 'it was detected as already being on hold or having a BIB ID';
      if (updatedRecord.status === 'closed' && updatedRecord.closeReason === 'purchased_no_hold') {
        reason = 'the patron has opted out of automatic hold placement';
      } else if (updatedRecord.status === 'closed' && updatedRecord.closeReason === 'duplicate_hold') {
        reason = 'a duplicate hold or request was detected for this patron';
      }

      await showAlert(`Note: This suggestion moved directly to "${statusNames[updatedRecord.status] || updatedRecord.status}" because ${reason}.`);
    }

  } catch (err) {
    console.error('Committed title-request action could not update the staff view:', err);
  } finally {
    await refreshAfterAction(!ownedAtCompletion);
  }
  return true;
}

export async function submitEditForm(e, ctx, options = {}) {
  const { onRefresh } = options;
  e.preventDefault();

  const identity = editRequestIdentity(ctx.id);
  const id = identity.id;
  if (ctx.id?.dataset.requestType !== 'title_request' || !id) return false;
  const nextStatus = ctx.nextStatus.value;
  const row = findWorkflowRow(identity, ctx.currentSuggestions, ctx.allSuggestions);
  if (!row) {
    await showAlert('Could not find that request. Refresh and try again.');
    return;
  }
  if (identity.type === 'additional_copy') {
    await showAlert('Use the additional-copy task controls to change this request.');
    return;
  }
  const bibInput = ctx.bibid;
  const bibid = row && row.status === 'hold_placed'
    ? String(row.bibid || '').trim()
    : bibInput.value.trim();

  if (row && row.status === 'outstanding_purchase' && bibid && !row.autohold) {
    const confirmed = await showConfirm('Do Not Auto Queue Hold', 'This request is marked Do Not Auto Queue Hold. Saving this BIB ID will close the request immediately and skip the hold-queueing workflow.');
    if (!confirmed) return;
  }

  const nextFormatValue = ctx.format.value;
  if (row && nextFormatValue && nextFormatValue !== row.format) {
    let warning = 'Changing the format may update the automatic claim assignment for this suggestion.';
    if (row.claimedByStaffUserId && row.claimType === 'automatic_format_rule') {
      warning = 'This suggestion is currently auto-claimed based on its format. Changing the format may reassign it to another staff member.';
    } else if (row.claimedByStaffUserId) {
      warning = 'This suggestion was manually claimed. Changing the format will not change the current claim.';
    }
    const confirmed = await showConfirm('Format change may affect claim', warning);
    if (!confirmed) return;
  }

  if (nextStatus === 'pending_hold') {
    if (!bibid) {
      await showAlert('BIB ID is required before moving this suggestion to Pending hold.');
      ctx.bibid.focus();
      return;
    }
    if (bibid !== ctx.verifiedBibId) {
      await showAlert('Please use the "Lookup BIB" button to verify this BIB ID before moving to Pending hold.');
      document.getElementById('btn-bib-lookup').focus();
      return;
    }
  }

  const actionValue = ctx.action.value || undefined;
  const payload = {
    action: actionValue,
    status: nextStatus,
    title: ctx.title.value,
    author: ctx.author.value,
    identifier: ctx.identifier.value,
    bibid: bibid,
    format: nextFormatValue,
    publication: ctx.publication.value,
    exactPublicationDate: ctx.exactPublicationDate.value,
    selectedPolarisBibId: ctx.selectedPolarisBibId?.value || '',
    selectedPolarisTitle: ctx.selectedPolarisTitle?.value || '',
    selectedPolarisAuthor: ctx.selectedPolarisAuthor?.value || '',
    selectedPolarisIdentifier: ctx.selectedPolarisIdentifier?.value || '',
    selectedPolarisPublication: ctx.selectedPolarisPublication?.value || '',
    selectedPolarisFormat: ctx.selectedPolarisFormat?.value || '',
    notes: ctx.notes.value,
    customFields: collectEditCustomFieldValues(),
    autohold: ctx.autohold.checked,
    editedBy: ctx.staffSession.staff?.username
  };

  const reminderCheckbox = ctx.purchaseReminderCheckbox;
  if (actionValue === 'purchase' && reminderCheckbox && reminderCheckbox.checked && !reminderCheckbox.disabled) {
    payload.emailPurchaseReminder = true;
  }

  if (actionValue === 'reject') {
    payload.rejectionTemplateId = ctx.rejectionTemplate.value;
  }

  await submitTitleRequestAction(identity, payload, {
    onRefresh,
    dialogsToClose: ['editModal']
  });
}
