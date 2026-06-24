import { authorizedJson } from '../http.js';
import { showToast, showAlert, showConfirm } from '../dialogs.js';
import { actionErrorMessage } from './utils.js';
import { confirmDuplicateOpenRequestClose } from './confirm-duplicate.js';
import { rememberRecentSuggestion, updateRecentSuggestion, renderRecentSuggestionsSwitcher } from '../recent-suggestions.js';
import { collectEditCustomFieldValues } from '../request-custom-fields.js';

export async function submitTitleRequestAction(id, payload, options = {}) {
  const {
    onRefresh,
    dialogsToClose = ['editModal']
  } = options;

  try {
    const updatedRecord = await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/action`, {
      method: 'POST',
      body: payload
    });

    rememberRecentSuggestion(updatedRecord);
    updateRecentSuggestion(updatedRecord);
    renderRecentSuggestionsSwitcher();

    dialogsToClose.forEach(dialogId => {
      const el = document.getElementById(dialogId);
      if (el) el.close();
    });

    const actionValue = payload.action;
    const nextStatus = payload.status;
    const reminder = updatedRecord && updatedRecord.purchaseReminderEmail;

    if (actionValue === 'purchase') {
      if (reminder && reminder.requested && reminder.sent) {
        showToast('Purchase saved and reminder email sent.', 'success');
      } else if (reminder && reminder.requested) {
        showToast(reminder.message || 'Purchase saved, but the reminder email could not be sent.', 'warning');
      } else {
        showToast('Purchase saved.', 'success');
      }
    } else if (actionValue === 'additionalCopy') {
      if (reminder && reminder.requested && reminder.sent) {
        showToast('Additional-copy task created, request queued, and reminder email sent.', 'success');
      } else if (reminder && reminder.requested) {
        showToast(reminder.message || 'Additional-copy task created, but the reminder email could not be sent.', 'warning');
      } else {
        showToast('Additional-copy task created and request queued.', 'success');
      }
    } else if (nextStatus === 'pending_hold') {
      showToast(`Request queued for hold (BIB ${payload.bibid || 'N/A'}).`, 'success');
    } else {
      showToast('Suggestion updated.', 'success');
    }

    if (updatedRecord && updatedRecord.status && updatedRecord.status !== nextStatus) {
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

    if (typeof onRefresh === 'function') onRefresh();
  } catch (err) {
    console.error('submitTitleRequestAction failed:', err);
    if (err.response) {
      err.code = err.response.code || '';
    }
    if (err && err.code === 'duplicate_open_request') {
      const confirmed = await confirmDuplicateOpenRequestClose(err, id);
      if (confirmed) {
        if (typeof onRefresh === 'function') onRefresh();
        return;
      }
    }
    await showAlert(err.message || 'Error updating suggestion');
  }
}

export async function submitEditForm(e, ctx, options = {}) {
  const { onRefresh } = options;
  e.preventDefault();

  const id = ctx.id.value;
  const nextStatus = ctx.nextStatus.value;
  const row = ctx.currentSuggestions.find(r => r.id === id) || ctx.allSuggestions.find(r => r.id === id);
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
    editedBy: ctx.pb.authStore.model.username
  };

  const reminderCheckbox = ctx.purchaseReminderCheckbox;
  if (actionValue === 'purchase' && reminderCheckbox && reminderCheckbox.checked && !reminderCheckbox.disabled) {
    payload.emailPurchaseReminder = true;
  }

  if (actionValue === 'reject') {
    payload.rejectionTemplateId = ctx.rejectionTemplate.value;
  }

  await submitTitleRequestAction(id, payload, {
    onRefresh,
    dialogsToClose: ['editModal']
  });
}
