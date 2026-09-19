import { currentLibraryContextOrgId } from './state.js';
import { isAdminStaff, setInlineResult } from './api.js';
import { authorizedJson } from './http.js';
import { showToast } from './dialogs.js';
import { refreshCurrentStaffView, refreshStaffStatus } from './grid.js';
import { collectSettingsPolaris, renderLibraryParticipationCheckboxes, collectEnabledLibraryIds } from './settings/polaris-fields.js';
import { syncPolarisOrganizations } from './settings/polaris-sync.js';

export { collectSettingsPolaris, renderLibraryParticipationCheckboxes, collectEnabledLibraryIds };

document.getElementById('btn-test-polaris').addEventListener('click', async (e) => {
  e.preventDefault();
  const resSpan = document.getElementById('polaris-test-result');
  const btn = e.currentTarget;

  btn.disabled = true;
  setInlineResult(resSpan, 'Testing Polaris...', 'ml-2 text-muted');
  try {
    const result = await authorizedJson('/api/asap/staff/polaris/test', { method: 'POST' });
    setInlineResult(
      resSpan,
      result.code === 'polaris_connected' ? 'Success! Polaris API is working.' : (result.message || 'Polaris is unavailable.'),
      result.code === 'polaris_connected' ? 'ml-2 text-success font-weight-bold' : 'ml-2 text-danger font-weight-bold'
    );
  } catch (err) {
    setInlineResult(resSpan, err.message || 'Error testing Polaris.', 'ml-2 text-danger font-weight-bold');
  } finally {
    btn.disabled = false;
  }
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
    setInlineResult(
      resultEl,
      'Material formats are maintained in SQL-backed Format settings; no separate Polaris sync is required.',
      'ml-2 text-muted'
    );
  });
}

document.getElementById('btn-run-hold-check').addEventListener('click', async () => {
  const btn = document.getElementById('btn-run-hold-check');
  const msg = document.getElementById('job-msg');

  btn.disabled = true;
  msg.textContent = 'Running hold check...';
  msg.className = 'mb-3 font-weight-bold text-info';

  try {
    const orgId = currentLibraryContextOrgId !== 'system' ? currentLibraryContextOrgId : '';
    const data = await authorizedJson(`/api/asap/staff/workflow/run-now${orgId ? `?organizationId=${encodeURIComponent(orgId)}` : ''}`, { method: 'POST' });
    msg.textContent = `Workflow run queued${data.jobId ? ` (job ${data.jobId})` : ''}.`;
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
    const orgId = currentLibraryContextOrgId !== 'system' ? currentLibraryContextOrgId : '';
    const data = await authorizedJson(`/api/asap/staff/workflow/run-now${orgId ? `?organizationId=${encodeURIComponent(orgId)}` : ''}`, { method: 'POST' });
    msg.textContent = `Workflow run queued${data.jobId ? ` (job ${data.jobId})` : ''}.`;
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
      const scope = currentLibraryContextOrgId !== 'system' ? currentLibraryContextOrgId : 'all';
      const result = await authorizedJson(`/api/asap/staff/title-requests?scope=${encodeURIComponent(scope)}`);
      const closed = (result.items || []).filter(item => item.status === 'closed');
      for (const item of closed) {
        await authorizedJson(`/api/asap/staff/requests/${encodeURIComponent(item.id)}`, {
          method: 'DELETE',
          body: { version: item.version }
        });
      }
      if (bulkDeleteClosedDialog && bulkDeleteClosedDialog.open) bulkDeleteClosedDialog.close();
      showToast(`Deleted ${closed.length} closed request${closed.length === 1 ? '' : 's'}.`, 'success');
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
  const btn = e.currentTarget;

  btn.disabled = true;
  resSpan.textContent = "Queueing test email...";
  resSpan.className = "mt-2 text-muted small";

  try {
    const orgId = currentLibraryContextOrgId !== 'system' ? currentLibraryContextOrgId : '';
    const data = await authorizedJson(`/api/asap/staff/email-operations/test${orgId ? `?organizationId=${encodeURIComponent(orgId)}` : ''}`, {
      method: 'POST'
    });
    resSpan.textContent = data.code === 'suppressed'
      ? 'Test email suppressed by the configured nonproduction recipient policy.'
      : 'Test email queued for the current staff notification address.';
    resSpan.className = "mt-2 text-success font-weight-bold small";
  } catch (err) {
    resSpan.textContent = err.message || "Error testing email delivery.";
    resSpan.className = "mt-2 text-danger font-weight-bold small";
  } finally {
    btn.disabled = false;
  }
});
