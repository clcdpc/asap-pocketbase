import { currentLibraryContextOrgId, currentWorkflowOrgScopeId, staffSession, staffAccessGeneration } from './state.js';
import { isAdminStaff, setInlineResult } from './api.js';
import { authorizedJson, loadStaffSession } from './http.js';
import { collectSettingsPolaris, renderLibraryParticipationCheckboxes, collectEnabledLibraryIds } from './settings/polaris-fields.js';
import { syncPolarisOrganizations } from './settings/polaris-sync.js';
import { saveSettings } from './settings/save-controller.js';
import { saveThenTestPolaris } from './settings/polaris-test.js';

export { collectSettingsPolaris, renderLibraryParticipationCheckboxes, collectEnabledLibraryIds };

document.getElementById('btn-test-polaris').addEventListener('click', async (e) => {
  e.preventDefault();
  const resSpan = document.getElementById('polaris-test-result');
  const btn = e.currentTarget;

  btn.disabled = true;
  setInlineResult(resSpan, 'Saving Polaris settings...', 'ml-2 text-muted');
  try {
    const outcome = await saveThenTestPolaris(
      () => saveSettings({
        button: btn,
        pendingText: 'Saving Polaris settings...',
        successText: 'Polaris settings saved.',
        clearDelay: 0
      }),
      () => {
        btn.disabled = true;
        setInlineResult(resSpan, 'Settings saved. Testing Polaris...', 'ml-2 text-muted');
        return authorizedJson('/api/asap/staff/polaris/test', { method: 'POST' });
      }
    );
    if (!outcome.saved) return;
    const result = outcome.result;
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

function workflowRunWasRejectedBeforeEnqueue(err) {
  return err.response?.operationPhase === 'rejected' || [401, 403].includes(err.status);
}

document.getElementById('btn-run-workflow-now').addEventListener('click', async (event) => {
  if (!isAdminStaff()) return;
  const btn = event.currentTarget;
  if (btn.disabled) return;
  const msg = document.getElementById('job-msg');
  const actionStaff = staffSession.staff;
  const actionAccessGeneration = staffAccessGeneration;
  const sameActionStaff = () => staffSession.authenticated && staffSession.accessAllowed &&
    staffAccessGeneration === actionAccessGeneration &&
    staffSession.staff?.id === actionStaff.id && staffSession.staff.role === actionStaff.role &&
    String(staffSession.staff.organizationId) === String(actionStaff.organizationId);
  const scope = actionStaff.role === 'super_admin' ? currentWorkflowOrgScopeId : String(actionStaff.organizationId);
  if (actionStaff.role === 'super_admin') {
    const scopeWrapper = document.getElementById('workflow-library-scope-label');
    const scopeSelect = document.getElementById('workflow-library-scope');
    if (scopeWrapper.classList.contains('hidden') || scopeSelect.value !== scope) return;
  }
  const scopeLabel = scope === 'all' ? 'all libraries' : `Library ${scope}`;
  const url = scope === 'all'
    ? '/api/asap/staff/workflow/run-now'
    : `/api/asap/staff/workflow/run-now?organizationId=${encodeURIComponent(scope)}`;
  btn.disabled = true;
  msg.textContent = `Queueing workflow run for ${scopeLabel}...`;
  msg.className = 'mb-3 font-weight-bold text-info';
  let reloadRequired = false;
  const requireReload = (message) => {
    reloadRequired = true;
    btn.dataset.reloadMessage = message;
    if (staffSession.authenticated && staffSession.accessAllowed) {
      msg.textContent = message;
      msg.className = 'mb-3 font-weight-bold text-warning';
    } else {
      msg.textContent = '';
    }
  };

  try {
    const data = await authorizedJson(url, { method: 'POST' });
    if (!sameActionStaff()) {
      requireReload(`Workflow run for ${scopeLabel} was queued under a changed staff session. Reload this page before another run.`);
      return;
    }
    const effectiveScope = data.organizationId == null ? '' : String(data.organizationId);
    const expectedScope = scope === 'all' ? '1' : scope;
    if (effectiveScope !== expectedScope) {
      const effectiveLabel = effectiveScope === '1' ? 'all libraries'
        : effectiveScope ? `Library ${effectiveScope}` : 'an unconfirmed scope';
      requireReload(`Workflow run queued for ${effectiveLabel}${data.jobId ? ` (job ${data.jobId})` : ''}. Your staff scope changed. Reload this page before another run.`);
      return;
    }
    msg.textContent = `Workflow run queued for ${scopeLabel}${data.jobId ? ` (job ${data.jobId})` : ''}.`;
    msg.className = 'mb-3 font-weight-bold text-success';
  } catch (err) {
    const ambiguous = !workflowRunWasRejectedBeforeEnqueue(err);
    if (!sameActionStaff()) {
      if (ambiguous) {
        requireReload(`Workflow run for ${scopeLabel} may have been queued, but its result could not be confirmed. Reload this page before another run.`);
      } else {
        requireReload(`Workflow run for ${scopeLabel} was rejected, but your staff session changed. Reload this page before another run.`);
      }
      return;
    }
    if (ambiguous) {
      requireReload(`Workflow run for ${scopeLabel} may have been queued, but its result could not be confirmed. Reload this page before another run.`);
      return;
    }
    if (err.status === 403 && err.response?.code === 'staff_scope_forbidden') {
      try {
        await loadStaffSession();
      } catch {
        requireReload(`Workflow run for ${scopeLabel} was rejected, but your current staff scope could not be confirmed. Reload this page before another run.`);
        return;
      }
      if (!sameActionStaff()) {
        requireReload(`Workflow run for ${scopeLabel} was rejected after your staff scope changed. Reload this page before another run.`);
        return;
      }
    }
    msg.textContent = 'Error: ' + err.message;
    msg.className = 'mb-3 font-weight-bold text-danger';
  } finally {
    if (!reloadRequired) btn.disabled = false;
  }
});

document.getElementById('btn-test-smtp').addEventListener('click', async (e) => {
  e.preventDefault();
  const resSpan = document.getElementById('smtp-test-result');
  const btn = e.currentTarget;

  btn.disabled = true;
  resSpan.textContent = "Queueing test email...";
  resSpan.className = "mt-2 text-muted small";

  try {
    if (!await saveSettings({ button: btn })) return;
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
