import { organizationsStatus, lastWorkflowEnabledList } from '../state.js';
import { authorizedJson } from '../http.js';
import { getFieldValue, getFieldChecked, setFieldValue, setFieldChecked, setVisible } from '../app/dom.js';
import { updateOrganizationsStatusUi } from '../app/misc.js';

const polarisTextFields = [
  ['host', 'polaris-host'],
  ['accessId', 'polaris-access-id'],
  ['staffDomain', 'polaris-domain'],
  ['adminUser', 'polaris-admin-user']
];

const polarisIntegerFields = [
  ['workstationId', 'polaris-workstation-id', 'Polaris workstation ID'],
  ['systemPolarisUserId', 'polaris-system-user-id', 'System Polaris user ID'],
  ['organizationIdForRequests', 'polaris-requesting-org-id', 'Requesting organization ID'],
  ['pickupOrganizationId', 'polaris-pickup-org-id', 'Pickup organization ID']
];

const polarisSecretFields = [
  ['polaris-api-key', 'polaris-clear-api-key'],
  ['polaris-admin-pass', 'polaris-clear-admin-pass']
];

function renderMessage(container, className, text) {
  const div = document.createElement('div');
  div.className = className;
  div.textContent = text;
  container.replaceChildren(div);
}

function renderLibraryParticipationTable(container, orgs) {
  const table = document.createElement('table');
  table.className = 'table table-sm table-hover mb-0';

  const thead = document.createElement('thead');
  thead.className = 'bg-white library-table-head';

  const headerRow = document.createElement('tr');

  const enableTh = document.createElement('th');
  enableTh.className = 'library-enable-col';
  enableTh.textContent = 'Enable';

  const nameTh = document.createElement('th');
  nameTh.textContent = 'Library name';

  const idTh = document.createElement('th');
  idTh.className = 'library-id-col';
  idTh.textContent = 'ID';

  headerRow.append(enableTh, nameTh, idTh);
  thead.appendChild(headerRow);

  const tbody = document.createElement('tbody');

  orgs.forEach(org => {
    const row = document.createElement('tr');

    const enableTd = document.createElement('td');
    enableTd.className = 'align-middle';

    const control = document.createElement('div');
    control.className = 'custom-control custom-checkbox';

    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.className = 'custom-control-input lib-participation-cb';
    checkbox.id = `lib-p-${org.organizationId}`;
    checkbox.value = org.organizationId;

    const label = document.createElement('label');
    label.className = 'custom-control-label';
    label.setAttribute('for', checkbox.id);

    control.append(checkbox, label);
    enableTd.appendChild(control);

    const nameTd = document.createElement('td');
    nameTd.className = 'align-middle font-weight-bold';
    nameTd.textContent = org.displayName || org.name || '';

    const orgIdTd = document.createElement('td');
    orgIdTd.className = 'align-middle text-muted small';
    orgIdTd.textContent = org.organizationId || '';

    row.append(enableTd, nameTd, orgIdTd);
    tbody.appendChild(row);
  });

  table.append(thead, tbody);
  container.replaceChildren(table);
}

export function collectSettingsPolaris(validate = false) {
  const result = {};

  polarisTextFields.forEach(([key, id]) => {
    if (document.getElementById(id)) {
      result[key] = getFieldValue(id).trim();
    }
  });

  polarisIntegerFields.forEach(([key, id, label]) => {
    if (!document.getElementById(id)) return;
    const raw = getFieldValue(id).trim();
    if (!raw) {
      result[key] = null;
      return;
    }
    const value = Number(raw);
    if (validate && (!Number.isInteger(value) || value < 1)) {
      throw new Error(`${label} must be a number greater than 0.`);
    }
    result[key] = Number.isInteger(value) ? value : raw;
  });

  const apiKey = getFieldValue('polaris-api-key').trim();
  const adminPassword = getFieldValue('polaris-admin-pass').trim();
  if (apiKey) result.apiKey = apiKey;
  if (adminPassword) result.adminPassword = adminPassword;
  if (document.getElementById('polaris-clear-api-key')) {
    result.clearApiKey = getFieldChecked('polaris-clear-api-key');
  }
  if (document.getElementById('polaris-clear-admin-pass')) {
    result.clearAdminPassword = getFieldChecked('polaris-clear-admin-pass');
  }
  return result;
}

export function populatePolarisSettingsForm(polaris) {
  polaris = polaris || {};
  polarisTextFields.concat(polarisIntegerFields).forEach(([key, id]) => {
    setFieldValue(id, polaris[key] ?? '');
  });
  setFieldValue('polaris-api-key', '');
  setFieldValue('polaris-admin-pass', '');
  setFieldChecked('polaris-clear-api-key', false);
  setFieldChecked('polaris-clear-admin-pass', false);
  setVisible('polaris-api-key-status', !!polaris.hasApiKey);
  setVisible('polaris-admin-pass-status', !!polaris.hasAdminPassword);
}

export function isPolarisConfigured(polaris) {
  return !!(polaris && polaris.host && polaris.accessId && polaris.staffDomain && polaris.adminUser &&
    polaris.hasApiKey && polaris.hasAdminPassword);
}

export function bindPolarisSecretControls() {
  polarisSecretFields.forEach(([inputId, clearId]) => {
    const input = document.getElementById(inputId);
    const clear = document.getElementById(clearId);
    if (!input || !clear) return;
    clear.addEventListener('change', () => {
      if (clear.checked) input.value = '';
    });
    input.addEventListener('input', () => {
      if (input.value) clear.checked = false;
    });
  });
}

export async function renderLibraryParticipationCheckboxes() {
  const container = document.getElementById('enabled-libraries-checkbox-container');
  if (!container || container.getAttribute('data-loaded') === 'true') return;

  if (organizationsStatus === 'loading') {
    renderMessage(container, 'p-3 text-muted', 'Organizations loading...');
    return;
  }

  if (organizationsStatus === 'error') {
    renderMessage(container, 'p-3 text-warning', 'Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.');
    return;
  }

  try {
    const values = await authorizedJson('/api/asap/staff/organizations');
    const orgs = values.map(item => ({ organizationId: item.id, displayName: item.displayName, name: item.displayName }));

    if (!orgs.length) {
      if (organizationsStatus === 'not_loaded') {
        renderMessage(container, 'p-3 text-muted', 'Organizations have not been synced yet. Use Settings > Polaris > Sync Polaris Organizations Now.');
      } else {
        renderMessage(container, 'p-3 text-muted', 'Organization sync completed, but no library organizations were returned.');
      }
      return;
    }

    updateOrganizationsStatusUi('loaded', `Polaris organizations loaded. ${orgs.length} library organization${orgs.length === 1 ? '' : 's'} available. Leave all libraries unchecked to enable all organizations.`);
    renderLibraryParticipationTable(container, orgs);

    container.setAttribute('data-loaded', 'true');

    if (lastWorkflowEnabledList) {
      const checkboxes = container.querySelectorAll('.lib-participation-cb');
      checkboxes.forEach(cb => {
        cb.checked = lastWorkflowEnabledList.indexOf(cb.value) >= 0;
      });
    }
  } catch (err) {
    console.error('Failed to load libraries for participation list', err);
    updateOrganizationsStatusUi('error', 'Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.');
    renderMessage(container, 'p-3 text-warning', 'Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.');
  }
}

export function collectEnabledLibraryIds() {
  const container = document.getElementById('enabled-libraries-checkbox-container');
  if (!container) return '';
  const checked = Array.from(container.querySelectorAll('.lib-participation-cb:checked')).map(cb => cb.value);
  return checked.join(',');
}
