import { pb, currentStatus, organizationsStatus, setOrganizationsStatus, lastWorkflowEnabledList } from './state.js';
import { getFieldValue, getFieldChecked, validateSmtpHostField, isSuperAdminStaff, isAdminStaff, updateOrganizationsStatusUi, setInlineResult, postPolarisTest } from './api.js';
import { authorizedJson } from './http.js';
import { showToast } from './dialogs.js';
import { refreshCurrentStaffView, refreshStaffStatus } from './grid.js';
import { saveSettings } from './settings/save-controller.js';
import { collectSettingsPolaris, renderLibraryParticipationCheckboxes, collectEnabledLibraryIds } from './settings/polaris-fields.js';
import { syncPolarisOrganizations } from './settings/polaris-sync.js';

export { collectSettingsPolaris, renderLibraryParticipationCheckboxes, collectEnabledLibraryIds };

document.getElementById('btn-test-polaris').addEventListener('click', async (e) => {
  e.preventDefault();
  const resSpan = document.getElementById('polaris-test-result');
  const btn = e.currentTarget;
  const polarisPayload = collectSettingsPolaris();

  if (!polarisPayload.host || !polarisPayload.accessId || !polarisPayload.apiKey) {
    setInlineResult(resSpan, 'Enter the Polaris host, PAPI access ID, and PAPI API key before testing.', 'ml-2 text-danger font-weight-bold');
    return;
  }

  const saved = await saveSettings({
    button: btn,
    pendingText: 'Saving before Polaris test...',
    successText: 'Settings saved. Testing Polaris...',
    clearDelay: 0
  });
  if (!saved) {
    setInlineResult(resSpan, 'Error: settings could not be saved.', 'ml-2 text-danger font-weight-bold');
    return;
  }

  await postPolarisTest('/api/asap/staff/test-polaris', resSpan, { polaris: polarisPayload }, {
    button: btn,
    token: pb.authStore.token,
    pendingText: 'Saving and testing...',
    pendingClass: 'ml-2 text-muted',
    successClass: 'ml-2 text-success font-weight-bold',
    errorClass: 'ml-2 text-danger font-weight-bold',
    successText: 'Success! Polaris API is working.'
  });
});

const syncOrganizationsBtn = document.getElementById('btn-sync-organizations');

if (syncOrganizationsBtn) {
  syncOrganizationsBtn.addEventListener('click', async (e) => {
    e.preventDefault();
    try {
      await syncPolarisOrganizations({ button: syncOrganizationsBtn });
    } catch (err) {
    }
  });
}

const syncMaterialTypesBtn = document.getElementById('btn-sync-material-types');
if (syncMaterialTypesBtn) {
  syncMaterialTypesBtn.addEventListener('click', async (e) => {
    e.preventDefault();
    const resultEl = document.getElementById('material-types-sync-result');
    const btn = e.currentTarget;
    btn.disabled = true;
    setInlineResult(resultEl, 'Syncing material types...', 'ml-2 text-muted');

    try {
      const result = await authorizedJson('/api/asap/staff/material-types/sync', { method: 'POST' });
      setInlineResult(resultEl, `Synced ${result.count || 0} material types.`, 'ml-2 text-success font-weight-bold');
    } catch (err) {
      setInlineResult(resultEl, 'Error: ' + (err.message || 'Sync failed.'), 'ml-2 text-danger font-weight-bold');
    } finally {
      btn.disabled = false;
    }
  });
}

document.getElementById('btn-run-hold-check').addEventListener('click', async () => {
  const btn = document.getElementById('btn-run-hold-check');
  const msg = document.getElementById('job-msg');

  btn.disabled = true;
  msg.textContent = 'Running hold check...';
  msg.className = 'mb-3 font-weight-bold text-info';

  try {
    const data = await authorizedJson('/api/asap/jobs/hold-check', { method: 'POST' });
    msg.textContent = `Hold check complete. Moved to Pending hold: ${data.promoted}, holds placed: ${data.holdsPlaced}, closed after checkout: ${data.checkoutClosures}, auto-closed: ${data.timedOut}`;
    msg.className = 'mb-3 font-weight-bold text-success';
    refreshCurrentStaffView();
  } catch (err) {
    msg.textContent = 'Error: ' + err.message;
    msg.className = 'mb-3 font-weight-bold text-danger';
  } finally {
    btn.disabled = false;
  }
});

document.getElementById('btn-run-promoter-check').addEventListener('click', async () => {
  const btn = document.getElementById('btn-run-promoter-check');
  const msg = document.getElementById('job-msg');

  btn.disabled = true;
  msg.textContent = 'Running auto-promoter...';
  msg.className = 'mb-3 font-weight-bold text-info';

  try {
    const data = await authorizedJson('/api/asap/jobs/promoter-check', { method: 'POST' });
    msg.textContent = `Auto-promoter complete. Moved ${data.promoted} item${data.promoted === 1 ? '' : 's'} to Pending hold.`;
    msg.className = 'mb-3 font-weight-bold text-success';
    refreshCurrentStaffView();
  } catch (err) {
    msg.textContent = 'Error: ' + err.message;
    msg.className = 'mb-3 font-weight-bold text-danger';
  } finally {
    btn.disabled = false;
  }
});

const deleteClosedRequestsBtn = document.getElementById('btn-delete-closed-requests');
const bulkDeleteClosedDialog = document.getElementById('bulk-delete-closed-dialog');
const bulkDeleteClosedForm = document.getElementById('bulk-delete-closed-form');
const bulkDeleteClosedInput = document.getElementById('bulk-delete-closed-confirm');
const bulkDeleteClosedSubmit = document.getElementById('bulk-delete-closed-submit');
const bulkDeleteClosedCancel = document.getElementById('bulk-delete-closed-cancel');
const bulkDeleteClosedMsg = document.getElementById('bulk-delete-closed-msg');

if (deleteClosedRequestsBtn && bulkDeleteClosedDialog) {
  deleteClosedRequestsBtn.addEventListener('click', () => {
    if (!isAdminStaff()) return;
    if (bulkDeleteClosedInput) bulkDeleteClosedInput.value = '';
    if (bulkDeleteClosedSubmit) bulkDeleteClosedSubmit.disabled = true;
    if (bulkDeleteClosedMsg) bulkDeleteClosedMsg.textContent = '';
    bulkDeleteClosedDialog.showModal();
    if (bulkDeleteClosedInput) bulkDeleteClosedInput.focus();
  });
}

if (bulkDeleteClosedInput && bulkDeleteClosedSubmit) {
  bulkDeleteClosedInput.addEventListener('input', () => {
    bulkDeleteClosedSubmit.disabled = bulkDeleteClosedInput.value !== 'DELETE';
  });
}

if (bulkDeleteClosedCancel && bulkDeleteClosedDialog) {
  bulkDeleteClosedCancel.addEventListener('click', () => {
    if (bulkDeleteClosedDialog.open) bulkDeleteClosedDialog.close();
  });
}

if (bulkDeleteClosedForm) {
  bulkDeleteClosedForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    if (!isAdminStaff() || !bulkDeleteClosedSubmit || !bulkDeleteClosedInput) return;
    bulkDeleteClosedSubmit.disabled = true;
    if (bulkDeleteClosedMsg) {
      bulkDeleteClosedMsg.textContent = 'Deleting closed requests...';
      bulkDeleteClosedMsg.className = 'mb-3 font-weight-bold text-info';
    }
    try {
      const result = await authorizedJson('/api/asap/staff/requests/delete-closed', {
        method: 'POST',
        body: { confirm: bulkDeleteClosedInput.value }
      });
      if (bulkDeleteClosedDialog && bulkDeleteClosedDialog.open) bulkDeleteClosedDialog.close();
      showToast(`Deleted ${result.deleted || 0} closed request${result.deleted === 1 ? '' : 's'}.`, 'success');
      refreshStaffStatus('closed');
    } catch (err) {
      if (bulkDeleteClosedMsg) {
        bulkDeleteClosedMsg.textContent = err.message || 'Could not delete closed requests.';
        bulkDeleteClosedMsg.className = 'mb-3 font-weight-bold text-danger';
      }
      bulkDeleteClosedSubmit.disabled = bulkDeleteClosedInput.value !== 'DELETE';
    }
  });
}

document.getElementById('btn-test-smtp').addEventListener('click', async (e) => {
  e.preventDefault();
  const resSpan = document.getElementById('smtp-test-result');
  const testInput = document.getElementById('smtp-test-email');
  const btn = e.currentTarget;

  const testEmail = testInput ? testInput.value.trim() : '';
  const smtpHost = getFieldValue('smtp-host').trim();
  const sender = getFieldValue('smtp-from').trim() || getFieldValue('email-from-address').trim();

  if (!smtpHost || !sender || !testEmail) {
    resSpan.textContent = 'Enter SMTP host, sender address, and test recipient before testing SMTP.';
    resSpan.className = 'mt-2 text-danger font-weight-bold small';
    return;
  }
  if (!validateSmtpHostField(true)) {
    return;
  }

  resSpan.textContent = "Saving and testing...";
  resSpan.className = "mt-2 text-muted small";

  const saved = await saveSettings({
    button: btn,
    pendingText: 'Saving settings...',
    successText: 'Settings saved. Testing SMTP...',
    clearDelay: 0
  });
  if (!saved) {
    resSpan.textContent = "Error: settings could not be saved.";
    resSpan.className = "mt-2 text-danger font-weight-bold small";
    return;
  }

  try {
    await new Promise(resolve => setTimeout(resolve, 300));

    const data = await authorizedJson('/api/asap/staff/test-smtp', {
      method: 'POST',
      body: { email: testEmail }
    });
    if (data.success) {
      resSpan.textContent = "Success! " + data.message;
      resSpan.className = "mt-2 text-success font-weight-bold small";
    } else {
      resSpan.textContent = "Error: " + (data.message || "Failed");
      resSpan.className = "mt-2 text-danger font-weight-bold small";
    }
  } catch (err) {
    resSpan.textContent = "Error testing SMTP.";
    resSpan.className = "mt-2 text-danger font-weight-bold small";
  }
});
