import { staffSession, canAssignSuperAdmin, setCanAssignSuperAdmin, currentLibraryContextOrgId } from './state.js';
import { isSuperAdminStaff } from './api.js';
import { authorizedJson, isAbortError } from './http.js';
import { showAlert, showConfirm } from './dialogs.js';

let staffOrganizations = [];

function clean(value) {
  return String(value ?? '').trim();
}

function staffDisplayName(user) {
  return clean(user.displayName) || clean(user.userPrincipalName) || `Staff ${clean(user.id) || '?'}`;
}

function roleLabel(role) {
  if (role === 'super_admin') return 'Super Admin';
  if (role === 'admin') return 'Admin';
  return 'Staff';
}

function roleOptions(selectedRole) {
  const roles = canAssignSuperAdmin ? ['staff', 'admin', 'super_admin'] : ['staff', 'admin'];
  if (!roles.includes(selectedRole)) roles.push(selectedRole);
  return roles.map(role => new Option(roleLabel(role), role, false, role === selectedRole));
}

function organizationOptions(role, selectedOrganizationId) {
  if (role === 'super_admin') {
    return [new Option('System', '1', false, String(selectedOrganizationId) === '1')];
  }

  const currentStaffOrganizationId = clean(staffSession.staff?.organizationId);
  const organizations = isSuperAdminStaff()
    ? staffOrganizations.filter(item => Number(item.id) > 1)
    : staffOrganizations.filter(item => String(item.id) === currentStaffOrganizationId);
  return organizations.map(item => new Option(
    `${item.displayName || item.name || `Library ${item.id}`} (ID ${item.id})`,
    String(item.id),
    false,
    String(item.id) === String(selectedOrganizationId)
  ));
}

function replaceOrganizationOptions(select, role, selectedOrganizationId) {
  select.replaceChildren(...organizationOptions(role, selectedOrganizationId));
  if (role === 'super_admin') {
    select.value = '1';
  } else if ([...select.options].some(option => option.value === String(selectedOrganizationId))) {
    select.value = String(selectedOrganizationId);
  }
  select.disabled = role === 'super_admin' || !isSuperAdminStaff();
}

function inputCell(type, value, className, label) {
  const cell = document.createElement('td');
  const input = document.createElement('input');
  input.type = type;
  input.value = value || '';
  input.className = `form-control form-control-sm ${className}`;
  input.setAttribute('aria-label', label);
  cell.appendChild(input);
  return cell;
}

function setStaffMessage(message, className) {
  const element = document.getElementById('staff-users-msg');
  if (!element) return;
  element.textContent = message;
  element.className = className;
}

function staffLoadContext(options = {}) {
  return clean(options.contextOrgId ?? currentLibraryContextOrgId) || 'system';
}

function isCurrentStaffLoad(options, contextOrgId) {
  return (!options.isCurrent || options.isCurrent()) &&
    contextOrgId === (clean(currentLibraryContextOrgId) || 'system');
}

export function showStaffAccessLoading(options = {}) {
  const contextOrgId = staffLoadContext(options);
  if (!isCurrentStaffLoad(options, contextOrgId)) return false;

  const body = document.getElementById('staff-users-table-body');
  const refresh = document.getElementById('btn-refresh-staff-users');
  if (!body) return false;

  if (refresh) refresh.disabled = true;
  setStaffMessage('Loading staff users...', 'mb-2 text-muted');
  body.replaceChildren();
  return true;
}

function cleanupSummary(cleanup) {
  if (!cleanup) return '';
  return ` Cleanup: ${Number(cleanup.rulesDeactivated || 0)} auto-claim rules deactivated; ` +
    `${Number(cleanup.openTitleClaimsCleared || 0)} open title claims cleared; ` +
    `${Number(cleanup.openAdditionalCopyClaimsCleared || 0)} open additional-copy claims cleared.`;
}

function staffUsersUrl(contextOrgId = currentLibraryContextOrgId) {
  const requestedOrganizationId = isSuperAdminStaff()
    ? (contextOrgId !== 'system' ? contextOrgId : '')
    : clean(staffSession.staff?.organizationId);
  return requestedOrganizationId
    ? `/api/asap/staff/users?orgId=${encodeURIComponent(requestedOrganizationId)}`
    : '/api/asap/staff/users';
}

export async function loadStaffUsers(options = {}) {
  const contextOrgId = staffLoadContext(options);
  const body = document.getElementById('staff-users-table-body');
  const refresh = document.getElementById('btn-refresh-staff-users');
  if (!body) return;

  if (!options.loadingShown && !showStaffAccessLoading({ ...options, contextOrgId })) return false;

  try {
    const result = await authorizedJson(staffUsersUrl(contextOrgId), { signal: options.signal });
    if (!isCurrentStaffLoad(options, contextOrgId)) return false;
    const users = Array.isArray(result.users) ? result.users : [];
    setCanAssignSuperAdmin(!!result.canAssignSuperAdmin);
    renderStaffUsers(users);
    setStaffMessage(
      users.length ? `Loaded ${users.length} staff user${users.length === 1 ? '' : 's'}.` : 'No staff users found.',
      'mb-2 text-muted'
    );
    return true;
  } catch (error) {
    if (isAbortError(error) || !isCurrentStaffLoad(options, contextOrgId)) return false;
    console.error('Failed to load staff users', error);
    setStaffMessage(error.message || 'Failed to load staff users.', 'mb-2 text-danger font-weight-bold');
    body.replaceChildren();
    const row = document.createElement('tr');
    const cell = document.createElement('td');
    cell.colSpan = 8;
    cell.className = 'text-muted';
    cell.textContent = 'Unable to load staff users.';
    row.appendChild(cell);
    body.appendChild(row);
    return false;
  } finally {
    if (refresh && isCurrentStaffLoad(options, contextOrgId)) refresh.disabled = false;
  }
}

export function renderStaffUsers(users) {
  const body = document.getElementById('staff-users-table-body');
  if (!body) return;
  body.replaceChildren();

  if (!users.length) {
    const row = document.createElement('tr');
    const cell = document.createElement('td');
    cell.colSpan = 8;
    cell.className = 'text-muted';
    cell.textContent = 'No staff users found.';
    row.appendChild(cell);
    body.appendChild(row);
    return;
  }

  for (const user of users) {
    const id = clean(user.id);
    const version = clean(user.version);
    const active = user.active !== false;
    const role = ['staff', 'admin', 'super_admin'].includes(clean(user.role).toLowerCase())
      ? clean(user.role).toLowerCase()
      : 'staff';
    const display = staffDisplayName(user);
    const row = document.createElement('tr');
    row.setAttribute('data-staff-id', id);
    row.setAttribute('data-staff-version', version);
    row.setAttribute('data-staff-active', String(active));
    if (!active) row.classList.add('staff-user-inactive');

    row.appendChild(inputCell('email', user.userPrincipalName, 'staff-authentication-email', `Authentication email for ${display}`));
    row.appendChild(inputCell('text', user.displayName, 'staff-display-name', `Display name for ${display}`));
    row.appendChild(inputCell('email', user.notificationEmail, 'staff-notification-email', `Notification email for ${display}`));

    const roleCell = document.createElement('td');
    const roleSelect = document.createElement('select');
    roleSelect.className = 'form-control form-control-sm staff-role-select';
    roleSelect.setAttribute('aria-label', `Role for ${display}`);
    roleSelect.replaceChildren(...roleOptions(role));
    roleCell.appendChild(roleSelect);
    row.appendChild(roleCell);

    const libraryCell = document.createElement('td');
    const librarySelect = document.createElement('select');
    librarySelect.className = 'form-control form-control-sm staff-library-select';
    librarySelect.setAttribute('aria-label', `Library for ${display}`);
    replaceOrganizationOptions(librarySelect, role, user.organizationId);
    roleSelect.addEventListener('change', () => {
      replaceOrganizationOptions(librarySelect, roleSelect.value, librarySelect.value);
    });
    libraryCell.appendChild(librarySelect);
    row.appendChild(libraryCell);

    const statusCell = document.createElement('td');
    const status = document.createElement('span');
    status.className = active ? 'badge staff-status-active' : 'badge staff-status-inactive';
    status.textContent = active ? 'Active' : 'Inactive';
    statusCell.appendChild(status);
    row.appendChild(statusCell);

    const versionCell = document.createElement('td');
    versionCell.className = 'staff-version-cell text-muted small';
    versionCell.textContent = version;
    row.appendChild(versionCell);

    const actionsCell = document.createElement('td');
    actionsCell.className = 'staff-actions-cell';
    const actions = document.createElement('div');
    actions.className = 'staff-actions-wrap';
    const saveMetadata = document.createElement('button');
    saveMetadata.type = 'button';
    saveMetadata.className = 'btn btn-sm btn-outline-primary staff-metadata-save';
    saveMetadata.textContent = 'Save profile';
    const saveAccess = document.createElement('button');
    saveAccess.type = 'button';
    saveAccess.className = 'btn btn-sm btn-primary staff-role-save';
    saveAccess.textContent = 'Update access';
    const lifecycle = document.createElement('button');
    lifecycle.type = 'button';
    lifecycle.className = active
      ? 'btn btn-sm btn-outline-danger staff-user-deactivate'
      : 'btn btn-sm btn-outline-success staff-user-reactivate';
    lifecycle.textContent = active ? 'Deactivate' : 'Reactivate';
    actions.append(saveMetadata, saveAccess, lifecycle);
    actionsCell.appendChild(actions);
    row.appendChild(actionsCell);
    body.appendChild(row);
  }
}

async function refreshStaffAccess() {
  const contextOrgId = clean(currentLibraryContextOrgId) || 'system';
  const loadedOrganizations = await populateStaffLibraryOptions({ contextOrgId });
  if (!loadedOrganizations || contextOrgId !== (clean(currentLibraryContextOrgId) || 'system')) return false;
  return loadStaffUsers({ contextOrgId });
}

async function runStaffMutation(button, message, operation) {
  const contextOrgId = clean(currentLibraryContextOrgId) || 'system';
  button.disabled = true;
  setStaffMessage(`${message}...`, 'mb-2 text-muted');
  try {
    const result = await operation();
    if (contextOrgId !== (clean(currentLibraryContextOrgId) || 'system')) return;
    await refreshStaffAccess();
    if (contextOrgId !== (clean(currentLibraryContextOrgId) || 'system')) return;
    setStaffMessage(`${message}.${cleanupSummary(result?.cleanup)}`, 'mb-2 text-success font-weight-bold');
  } catch (error) {
    if (isAbortError(error) || contextOrgId !== (clean(currentLibraryContextOrgId) || 'system')) return;
    console.error(message, error);
    setStaffMessage(error.message || 'The staff access change could not be saved.', 'mb-2 text-danger font-weight-bold');
  } finally {
    button.disabled = false;
  }
}

const staffUsersTableBody = document.getElementById('staff-users-table-body');
staffUsersTableBody?.addEventListener('click', async event => {
  const row = event.target.closest('tr[data-staff-id]');
  if (!row) return;
  const id = row.getAttribute('data-staff-id');
  const version = row.getAttribute('data-staff-version');

  const metadataButton = event.target.closest('.staff-metadata-save');
  if (metadataButton) {
    await runStaffMutation(metadataButton, 'Staff profile saved', () => authorizedJson(
      `/api/asap/staff/users/${encodeURIComponent(id)}`,
      {
        method: 'PATCH',
        body: {
          version,
          email: clean(row.querySelector('.staff-authentication-email')?.value),
          displayName: clean(row.querySelector('.staff-display-name')?.value),
          notificationEmail: clean(row.querySelector('.staff-notification-email')?.value)
        }
      }
    ));
    return;
  }

  const accessButton = event.target.closest('.staff-role-save');
  if (accessButton) {
    const role = row.querySelector('.staff-role-select')?.value || 'staff';
    const organizationId = role === 'super_admin'
      ? 1
      : Number(row.querySelector('.staff-library-select')?.value);
    await runStaffMutation(accessButton, 'Staff access updated', () => authorizedJson(
      `/api/asap/staff/users/${encodeURIComponent(id)}/role`,
      { method: 'POST', body: { version, role, organizationId } }
    ));
    return;
  }

  const deactivateButton = event.target.closest('.staff-user-deactivate');
  if (deactivateButton) {
    const ok = await showConfirm('Deactivate staff member', `Deactivate ${staffDisplayName({
      id,
      displayName: row.querySelector('.staff-display-name')?.value,
      userPrincipalName: row.querySelector('.staff-authentication-email')?.value
    })}?`);
    if (!ok) return;
    await runStaffMutation(deactivateButton, 'Staff user deactivated', () => authorizedJson(
      `/api/asap/staff/users/${encodeURIComponent(id)}`,
      { method: 'DELETE', body: { version } }
    ));
    return;
  }

  const reactivateButton = event.target.closest('.staff-user-reactivate');
  if (reactivateButton) {
    const role = row.querySelector('.staff-role-select')?.value || 'staff';
    const organizationId = role === 'super_admin'
      ? 1
      : Number(row.querySelector('.staff-library-select')?.value);
    await runStaffMutation(reactivateButton, 'Staff user reactivated', () => authorizedJson(
      '/api/asap/staff/users',
      {
        method: 'POST',
        body: {
          email: clean(row.querySelector('.staff-authentication-email')?.value),
          role,
          organizationId
        }
      }
    ));
  }
});

document.getElementById('btn-refresh-staff-users')?.addEventListener('click', event => {
  event.preventDefault();
  loadStaffUsers();
});

export async function populateStaffLibraryOptions(options = {}) {
  const contextOrgId = staffLoadContext(options);
  const select = document.getElementById('staff-add-library');
  const context = document.getElementById('staff-add-library-context');
  if (!select || !context || !isCurrentStaffLoad(options, contextOrgId)) return false;

  const me = staffSession.staff || {};
  const isSuper = isSuperAdminStaff();
  if (isSuper) {
    const organizations = await authorizedJson('/api/asap/staff/organizations', { signal: options.signal });
    if (!isCurrentStaffLoad(options, contextOrgId)) return false;
    staffOrganizations = organizations;
    select.classList.remove('hidden');
    context.classList.add('hidden');
  } else {
    if (!isCurrentStaffLoad(options, contextOrgId)) return false;
    const libraryId = clean(me.organizationId || me.libraryOrgId);
    const libraryName = me.organizationName || me.libraryOrgName || `Library ${libraryId || '?'}`;
    staffOrganizations = [{ id: libraryId, displayName: libraryName }];
    select.classList.add('hidden');
    context.classList.remove('hidden');
    context.textContent = `${libraryName} (ID ${libraryId || '?'})`;
  }

  const role = document.getElementById('staff-add-role')?.value || 'staff';
  const selectedOrganizationId = contextOrgId !== 'system'
    ? contextOrgId
    : clean(me.organizationId || me.libraryOrgId);
  replaceOrganizationOptions(select, role, selectedOrganizationId);
  if (isSuper && role !== 'super_admin') select.disabled = false;
  return true;
}

const addStaffRole = document.getElementById('staff-add-role');
addStaffRole?.addEventListener('change', () => {
  const select = document.getElementById('staff-add-library');
  if (!select) return;
  replaceOrganizationOptions(select, addStaffRole.value, select.value);
  if (isSuperAdminStaff() && addStaffRole.value !== 'super_admin') select.disabled = false;
});

const addStaffButton = document.getElementById('btn-add-staff-user');
addStaffButton?.addEventListener('click', async () => {
  const emailInput = document.getElementById('staff-add-identity');
  const librarySelect = document.getElementById('staff-add-library');
  const roleSelect = document.getElementById('staff-add-role');
  const email = clean(emailInput?.value);
  const role = clean(roleSelect?.value) || 'staff';
  const organizationId = role === 'super_admin' ? 1 : Number(librarySelect?.value);

  if (!email) {
    await showAlert('Enter the staff authentication email / UPN.');
    return;
  }
  if (role !== 'super_admin' && !organizationId) {
    await showAlert('Select a library for this staff member.');
    return;
  }

  addStaffButton.disabled = true;
  try {
    await authorizedJson('/api/asap/staff/users', {
      method: 'POST',
      body: { email, role, organizationId }
    });
    emailInput.value = '';
    await refreshStaffAccess();
    setStaffMessage('Staff user saved. They can sign in with the matching Microsoft account.', 'mb-2 text-success font-weight-bold');
  } catch (error) {
    setStaffMessage(error.message || 'Failed to add staff member.', 'mb-2 text-danger font-weight-bold');
  } finally {
    addStaffButton.disabled = false;
  }
});
