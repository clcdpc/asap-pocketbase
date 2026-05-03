import { pb, formatMap, availableFormats, currentRejectionTemplates, currentStatus, currentSuggestions, allSuggestions, verifiedBibId, publicationOptions, setVerifiedBibId } from './state.js';
import { leapBibUrl, showToast, showAlert, openProfileDialog } from './api.js';
import { loadTab, formatStandardDate, renderWorkflowTags, escapeAttr } from './grid.js';
import { setSelectValue, dateOnly } from './settings-ui.js';

export function openEdit(id, nextStatus, dialogTitle, actionStr) {
  const row = currentSuggestions.find(r => r.id === id) || allSuggestions.find(r => r.id === id);
  if (!row) return;

  document.getElementById('editModalLabel').textContent = dialogTitle;
  document.getElementById('edit-id').value = row.id;
  document.getElementById('edit-next-status').value = nextStatus;
  document.getElementById('edit-action').value = actionStr;
  setBibIdRequirement(nextStatus);

  document.getElementById('edit-title').value = row.title || '';
  document.getElementById('edit-author').value = row.author || '';
  document.getElementById('edit-identifier').value = row.identifier || '';
  document.getElementById('edit-bibid').value = row.bibid || '';

  const editFormat = document.getElementById('edit-format');
  const fmt = row.format || 'book';
  if (fmt && !availableFormats.includes(fmt)) {
    if (!Array.from(editFormat.options).some(o => o.value === fmt)) {
      const opt = document.createElement('option');
      opt.value = fmt;
      opt.textContent = formatMap[fmt] || fmt;
      editFormat.appendChild(opt);
    }
  }
  editFormat.value = fmt;
  document.getElementById('edit-age').value = row.agegroup || 'adult';
  setSelectValue(document.getElementById('edit-publication'), row.publication || publicationOptions[0]);
  document.getElementById('edit-exact-publication-date').value = dateOnly(row.exactPublicationDate);
  renderEditPatronContext(row);
  renderEditWorkflowTags(row.workflowTags);
  renderEditLeapBibLink(row.bibid);
  renderPurchaseReminderOption(actionStr);

  const username = (pb.authStore.model && pb.authStore.model.username) ? pb.authStore.model.username : 'staff';
  const today = formatStandardDate(new Date());
  let appendNotes = '';
  if (actionStr === 'alreadyOwn') appendNotes = `${today} ALREADY OWN by ${username}.`;
  if (actionStr === 'reject') appendNotes = `${today} REJECTED by ${username}.`;
  if (actionStr === 'silentClose') appendNotes = `${today} SILENTLY CLOSED by ${username}.`;

  const existingNotes = (row.notes || '').trim();
  document.getElementById('edit-notes').value = appendNotes + (appendNotes && existingNotes ? '\n' : '') + existingNotes;

  document.getElementById('bib-info-display').classList.add('hidden');
  document.getElementById('bib-info-text').textContent = '';
  setVerifiedBibId(row.bibid || '');

  const rejectionContainer = document.getElementById('edit-rejection-template-container');
  if (actionStr === 'reject' && currentRejectionTemplates.length > 0) {
    rejectionContainer.classList.remove('hidden');
    const select = document.getElementById('edit-rejection-template');
    select.innerHTML = '<option value="">Default rejection template</option>';
    const sortedTemplates = [...currentRejectionTemplates].sort((a, b) => {
      const nameA = (a.name || a.subject || '').toLowerCase();
      const nameB = (b.name || b.subject || '').toLowerCase();
      return nameA.localeCompare(nameB);
    });

    sortedTemplates.forEach(t => {
      const opt = document.createElement('option');
      opt.value = t.id;
      opt.textContent = t.name || t.subject;
      select.appendChild(opt);
    });
  } else {
    rejectionContainer.classList.add('hidden');
    document.getElementById('edit-rejection-template').value = '';
  }

  document.getElementById('editModal').showModal();
  document.getElementById('close-modal-btn').focus();
}

export function staffProfileEmail() {
  const model = pb.authStore.model || {};
  return String(model.weekly_action_summary_email || '').trim();
}

export function renderPurchaseReminderOption(actionStr) {
  const container = document.getElementById('edit-purchase-reminder-container');
  const checkbox = document.getElementById('edit-email-purchase-reminder');
  const help = document.getElementById('edit-purchase-reminder-help');
  if (!container || !checkbox || !help) return;
  checkbox.checked = !!(pb.authStore.model && pb.authStore.model.purchase_reminder_default);
  const isPurchaseAction = actionStr === 'purchase';
  const isStaff = !!(pb.authStore.isValid && pb.authStore.model && pb.authStore.model.collectionName === 'staff_users');
  container.classList.toggle('hidden', !(isPurchaseAction && isStaff));
  if (!(isPurchaseAction && isStaff)) {
    checkbox.disabled = true;
    help.innerHTML = '';
    return;
  }
  const email = staffProfileEmail();
  checkbox.disabled = !email;
  help.innerHTML = email
    ? `Send purchase details to ${escapeAttr(email)}.`
    : 'Add an email address to your <a href="#" onclick="openProfileDialog(); return false;">staff profile</a> to email yourself purchase reminders.';
}

export function renderEditPatronContext(row) {
  const editBody = document.querySelector('#editModal .asap-dialog-edit-body');
  if (!editBody) return;

  let block = document.getElementById('edit-patron-context');
  if (!block) {
    block = document.createElement('div');
    block.id = 'edit-patron-context';
    block.className = 'alert alert-light border py-2 px-3 mb-2 small';
    const anchor = document.getElementById('edit-rejection-template-container');
    if (anchor && anchor.parentNode === editBody) {
      editBody.insertBefore(block, anchor.nextSibling);
    } else {
      editBody.insertBefore(block, editBody.firstChild);
    }
  }

  const patronName = row.patronName || `${row.nameFirst || ''} ${row.nameLast || ''}`.trim() || '—';
  const patronEmail = row.patronEmail || row.email || '—';
  const libraryOrgName = row.libraryOrgName || row.libraryOrgId || '—';
  const preferredPickupBranchName = row.preferredPickupBranchName || '—';
  const barcode = row.barcode || '—';

  block.innerHTML = `
    <div><strong>Patron:</strong> ${escapeAttr(patronName)}</div>
    <div><strong>Email:</strong> ${escapeAttr(patronEmail)}</div>
    <div><strong>Barcode:</strong> ${escapeAttr(barcode)}</div>
    <div><strong>Library:</strong> ${escapeAttr(libraryOrgName)}</div>
    <div><strong>Preferred pickup branch:</strong> ${escapeAttr(preferredPickupBranchName)}</div>
  `;
}

export function renderEditWorkflowTags(tags) {
  const container = document.getElementById('edit-workflow-tags');
  if (!container) return;
  container.innerHTML = `
    <div class="small font-weight-bold mb-1">Workflow tags</div>
    ${renderWorkflowTags(tags)}
  `;
}

export function renderEditLeapBibLink(bibId) {
  const container = document.getElementById('edit-leap-bib-link-container');
  if (!container) return;
  const url = leapBibUrl(bibId);
  if (!url) {
    container.classList.add('hidden');
    container.innerHTML = '';
    return;
  }
  container.classList.remove('hidden');
  container.innerHTML = `<a class="btn btn-sm btn-outline-primary" href="${escapeAttr(url)}" target="_blank" rel="noopener noreferrer">Open Bib in Leap</a>`;
}

document.getElementById('edit-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const id = document.getElementById('edit-id').value;
  const nextStatus = document.getElementById('edit-next-status').value;
  const bibid = document.getElementById('edit-bibid').value.trim();
  if (nextStatus === 'pending_hold') {
    if (!bibid) {
      await showAlert('BIB ID is required before moving this suggestion to Pending hold.');
      document.getElementById('edit-bibid').focus();
      return;
    }
    if (bibid !== verifiedBibId) {
      await showAlert('Please use the "Lookup BIB" button to verify this BIB ID before moving to Pending hold.');
      document.getElementById('btn-bib-lookup').focus();
      return;
    }
  }
  const actionValue = document.getElementById('edit-action').value || undefined;
  const payload = {
    action: actionValue,
    status: nextStatus,
    title: document.getElementById('edit-title').value,
    author: document.getElementById('edit-author').value,
    identifier: document.getElementById('edit-identifier').value,
    bibid: bibid,
    format: document.getElementById('edit-format').value,
    agegroup: document.getElementById('edit-age').value,
    publication: document.getElementById('edit-publication').value,
    exactPublicationDate: document.getElementById('edit-exact-publication-date').value,
    notes: document.getElementById('edit-notes').value,
    editedBy: pb.authStore.model.username
  };
  const reminderCheckbox = document.getElementById('edit-email-purchase-reminder');
  if (actionValue === 'purchase' && reminderCheckbox && reminderCheckbox.checked && !reminderCheckbox.disabled) {
    payload.emailPurchaseReminder = true;
  }

  if (actionValue === 'reject') {
    payload.rejectionTemplateId = document.getElementById('edit-rejection-template').value;
  }

  try {
    const res = await fetch(`/api/asap/staff/title-requests/${id}/action`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify(payload)
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      throw new Error(data.message || 'Error updating suggestion');
    }
    const updatedRecord = await res.json().catch(() => ({}));
    document.getElementById('editModal').close();
    const reminder = updatedRecord && updatedRecord.purchaseReminderEmail;
    if (actionValue === 'purchase') {
      if (reminder && reminder.requested && reminder.sent) {
        showToast('Purchase saved and reminder email sent.', 'success');
      } else if (reminder && reminder.requested) {
        showToast(reminder.message || 'Purchase saved, but the reminder email could not be sent.', 'warning');
      } else {
        showToast('Purchase saved.', 'success');
      }
    }

    if (updatedRecord && updatedRecord.status && updatedRecord.status !== nextStatus) {
      const statusNames = {
        'outstanding_purchase': 'Pending purchase',
        'pending_hold': 'Pending hold',
        'hold_placed': 'Hold placed',
        'closed': 'Closed'
      };
      await showAlert(`Note: This suggestion moved directly to "${statusNames[updatedRecord.status] || updatedRecord.status}" because it was detected as already being on hold or having a BIB ID.`);
    }

    loadTab(currentStatus);
  } catch (err) {
    await showAlert(err.message || 'Error updating suggestion');
  }
});

export function setBibIdRequirement(nextStatus) {
  const bibInput = document.getElementById('edit-bibid');
  const bibRequiredMarker = document.getElementById('edit-bibid-required');
  const bibHint = document.getElementById('edit-bibid-hint');

  const isRequired = nextStatus === 'pending_hold';
  bibInput.required = isRequired;
  bibInput.setAttribute('aria-required', String(isRequired));
  if (bibRequiredMarker) {
    bibRequiredMarker.classList.toggle('hidden', !isRequired);
  }
  if (bibHint) {
    bibHint.classList.toggle('text-danger', isRequired);
    bibHint.classList.toggle('font-weight-bold', isRequired);
    bibHint.textContent = isRequired
      ? 'Required for Pending hold so the system can place the patron hold.'
      : 'Needed before a hold can be placed for the patron.';
  }
}
