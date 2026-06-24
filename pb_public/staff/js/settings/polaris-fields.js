import { pb, organizationsStatus, lastWorkflowEnabledList } from '../state.js';
import { getFieldValue, updateOrganizationsStatusUi } from '../api.js';

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

export function collectSettingsPolaris() {
  return {
    host: getFieldValue('polaris-host'),
    apiKey: getFieldValue('polaris-api-key'),
    accessId: getFieldValue('polaris-access-id'),
    staffDomain: getFieldValue('polaris-domain'),
    adminUser: getFieldValue('polaris-admin-user'),
    adminPassword: getFieldValue('polaris-admin-pass'),
    overridePassword: getFieldValue('polaris-override-pass'),
    langId: "1033",
    appId: "100",
    orgId: "1",
    pickupOrgId: "0",
    requestingOrgId: "3",
    workstationId: getFieldValue('polaris-workstation-id') || "1",
    userId: "1"
  };
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
    const orgs = await pb.collection('polaris_organizations').getFullList({
      filter: 'organizationCodeId = "2"',
      sort: 'displayName',
      requestKey: 'polaris-orgs-participation'
    });

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
