import { pb, currentStatus, organizationsStatus, setOrganizationsStatus, lastWorkflowEnabledList } from './state.js';
import { getFieldValue, getFieldChecked, validateSmtpHostField, showToast, isSuperAdminStaff, isAdminStaff, setOrganizationsStatus, setInlineResult, postPolarisTest, authorizedJson } from './api.js';
import { loadTab, escapeAttr } from './grid.js';
import { populateLibrarySelector, saveSettings } from './settings.js';

export function collectSettingsPolaris() {
  return {
    host: getFieldValue('polaris-host'),
    apiKey: getFieldValue('polaris-api-key'),
    accessId: getFieldValue('polaris-access-id'),
    staffDomain: getFieldValue('polaris-domain'),
    adminUser: getFieldValue('polaris-admin-user'),
    adminPassword: getFieldValue('polaris-admin-pass'),
    overridePassword: getFieldValue('polaris-override-pass'),
    autoPromote: getFieldChecked('polaris-auto-promote'),
    langId: "1033",
    appId: "100",
    orgId: "1",
    pickupOrgId: "0",
    requestingOrgId: "3",
    workstationId: "1",
    userId: "1"
  };
}

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
export async function syncPolarisOrganizations(options = {}) {
  const resultEl = document.getElementById('organizations-sync-result');
  const btn = options.button || syncOrganizationsBtn;
  if (btn) btn.disabled = true;
  setOrganizationsStatus('loading', 'Organizations loading from Polaris. Organization selection will be available after this sync completes.');
  setInlineResult(resultEl, 'Syncing organizations...', 'ml-2 text-muted');

  try {
    const result = await authorizedJson('/api/asap/staff/organizations/sync', { method: 'POST' });
    const count = result.synced || 0;
    setOrganizationsStatus('loaded', `Polaris organizations loaded successfully. ${count} organization record${count === 1 ? '' : 's'} synced. Leave all libraries unchecked to enable all organizations.`);
    setInlineResult(resultEl, `Synced ${count} organization records.`, 'ml-2 text-success font-weight-bold');
    const container = document.getElementById('enabled-libraries-checkbox-container');
    if (container) {
      container.removeAttribute('data-loaded');
    }
    if (isSuperAdminStaff()) {
      await populateLibrarySelector();
    }
    await renderLibraryParticipationCheckboxes();
    return result;
  } catch (err) {
    setOrganizationsStatus('error', 'Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.');
    setInlineResult(resultEl, 'Warning: ' + (err.message || 'Organization sync failed.'), 'ml-2 text-warning font-weight-bold');
    throw err;
  } finally {
    if (btn) btn.disabled = false;
  }
}

if (syncOrganizationsBtn) {
  syncOrganizationsBtn.addEventListener('click', async (e) => {
    e.preventDefault();
    try {
      await syncPolarisOrganizations({ button: syncOrganizationsBtn });
    } catch (err) {
      // syncPolarisOrganizations already updates the visible warning state.
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
    const res = await fetch('/api/asap/jobs/hold-check', {
      method: 'POST',
      headers: { 'Authorization': pb.authStore.token }
    });
    const data = await res.json();
    if (res.ok) {
      msg.textContent = `Hold check complete. Moved to Pending hold: ${data.promoted}, holds placed: ${data.holdsPlaced}, closed after checkout: ${data.checkoutClosures}, auto-closed: ${data.timedOut}`;
      msg.className = 'mb-3 font-weight-bold text-success';
      loadTab(currentStatus);
    } else {
      throw new Error(data.message || 'Failed to run hold check');
    }
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
    const res = await fetch('/api/asap/jobs/promoter-check', {
      method: 'POST',
      headers: { 'Authorization': pb.authStore.token }
    });
    const data = await res.json();
    if (res.ok) {
      msg.textContent = `Auto-promoter complete. Moved ${data.promoted} item${data.promoted === 1 ? '' : 's'} to Pending hold.`;
      msg.className = 'mb-3 font-weight-bold text-success';
      loadTab(currentStatus);
    } else {
      throw new Error(data.message || 'Failed to run promoter check');
    }
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
        body: JSON.stringify({ confirm: bulkDeleteClosedInput.value })
      });
      if (bulkDeleteClosedDialog && bulkDeleteClosedDialog.open) bulkDeleteClosedDialog.close();
      showToast(`Deleted ${result.deleted || 0} closed request${result.deleted === 1 ? '' : 's'}.`, 'success');
      loadTab('closed');
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
    // Give PocketBase hooks a brief moment to apply freshly saved SMTP settings
    // before issuing the test request.
    await new Promise(resolve => setTimeout(resolve, 300));

    const res = await fetch('/api/asap/staff/test-smtp', {
      method: 'POST',
      headers: {
        'Authorization': pb.authStore.token,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({ email: testEmail })
    });
    const data = await res.json();
    if (res.ok && data.success) {
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

export async function renderLibraryParticipationCheckboxes() {
  const container = document.getElementById('enabled-libraries-checkbox-container');
  if (!container || container.getAttribute('data-loaded') === 'true') return;

  if (organizationsStatus == 'loading') {
    container.innerHTML = '<div class="p-3 text-muted">Organizations loading...</div>'
    return;
  }

  if (organizationsStatus == 'error') {
    container.innerHTML = '<div class="p-3 text-warning">Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.</div>'
    return;
  }

  try {
    const orgs = await pb.collection('polaris_organizations').getFullList({
      filter: 'organizationCodeId = "2"',
      sort: 'displayName',
      requestKey: 'polaris-orgs-participation'
    });

    if (!orgs.length) {
      if (organizationsStatus == 'not_loaded') {
        container.innerHTML = '<div class="p-3 text-muted">Organizations have not been synced yet. Use Settings > Polaris > Sync Polaris Organizations Now.</div>'
      } else {
        container.innerHTML = '<div class="p-3 text-muted">Organization sync completed, but no library organizations were returned.</div>';
      }
      return;
    }

    setOrganizationsStatus('loaded', `Polaris organizations loaded. ${orgs.length} library organization${orgs.length === 1 ? '' : 's'} available. Leave all libraries unchecked to enable all organizations.`);
    container.innerHTML = `
      <table class="table table-sm table-hover mb-0">
        <thead class="bg-white library-table-head">
          <tr>
            <th class="library-enable-col">Enable</th>
            <th>Library name</th>
            <th class="library-id-col">ID</th>
          </tr>
        </thead>
        <tbody>
          ${orgs.map(org => `
            <tr>
              <td class="align-middle">
                <div class="custom-control custom-checkbox">
                  <input type="checkbox" class="custom-control-input lib-participation-cb" id="lib-p-${org.organizationId}" value="${org.organizationId}">
                  <label class="custom-control-label" for="lib-p-${org.organizationId}"></label>
                </div>
              </td>
              <td class="align-middle font-weight-bold">${escapeAttr(org.displayName || org.name)}</td>
              <td class="align-middle text-muted small">${org.organizationId}</td>
            </tr>
          `).join('')}
        </tbody>
      </table>
    `;

    container.setAttribute('data-loaded', 'true');

    // Restore checked state if we have it
    if (lastWorkflowEnabledList) {
      const checkboxes = container.querySelectorAll('.lib-participation-cb');
      checkboxes.forEach(cb => {
        cb.checked = lastWorkflowEnabledList.indexOf(cb.value) >= 0;
      });
    }
  } catch (err) {
    console.error('Failed to load libraries for participation list', err);
    setOrganizationsStatus('error', 'Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.');
    container.innerHTML = '<div class="p-3 text-warning">Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.</div>';
  }
}

export function collectEnabledLibraryIds() {
  const container = document.getElementById('enabled-libraries-checkbox-container');
  if (!container) return '';
  const checked = Array.from(container.querySelectorAll('.lib-participation-cb:checked')).map(cb => cb.value);
  return checked.join(',');
}
