import { pb, organizationsStatus, lastWorkflowEnabledList } from '../state.js';
import { getFieldValue, updateOrganizationsStatusUi } from '../api.js';
import { escapeAttr } from '../grid.js';

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
    container.innerHTML = '<div class="p-3 text-muted">Organizations loading...</div>';
    return;
  }

  if (organizationsStatus === 'error') {
    container.innerHTML = '<div class="p-3 text-warning">Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.</div>';
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
        container.innerHTML = '<div class="p-3 text-muted">Organizations have not been synced yet. Use Settings > Polaris > Sync Polaris Organizations Now.</div>';
      } else {
        container.innerHTML = '<div class="p-3 text-muted">Organization sync completed, but no library organizations were returned.</div>';
      }
      return;
    }

    updateOrganizationsStatusUi('loaded', `Polaris organizations loaded. ${orgs.length} library organization${orgs.length === 1 ? '' : 's'} available. Leave all libraries unchecked to enable all organizations.`);
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
                  <input type="checkbox" class="custom-control-input lib-participation-cb" id="lib-p-${escapeAttr(org.organizationId)}" value="${escapeAttr(org.organizationId)}">
                  <label class="custom-control-label" for="lib-p-${escapeAttr(org.organizationId)}"></label>
                </div>
              </td>
              <td class="align-middle font-weight-bold">${escapeAttr(org.displayName || org.name)}</td>
              <td class="align-middle text-muted small">${escapeAttr(org.organizationId)}</td>
            </tr>
          `).join('')}
        </tbody>
      </table>
    `;

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
    container.innerHTML = '<div class="p-3 text-warning">Polaris connected, but organizations could not be loaded. Some setup options may be unavailable until this sync succeeds.</div>';
  }
}

export function collectEnabledLibraryIds() {
  const container = document.getElementById('enabled-libraries-checkbox-container');
  if (!container) return '';
  const checked = Array.from(container.querySelectorAll('.lib-participation-cb:checked')).map(cb => cb.value);
  return checked.join(',');
}
