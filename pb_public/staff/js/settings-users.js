import { pb, canAssignSuperAdmin, setCanAssignSuperAdmin, formatMap, availableFormats, currentFormatClaimRules, setCurrentFormatClaimRules, currentLibraryContextOrgId } from './state.js';
import { setFieldValue, getFieldValue, showAlert, showConfirm, isSuperAdminStaff, authorizedJson, markSettingsDirty } from './api.js';
import { escapeAttr } from './grid.js';
import { updateFormatClaimRuleState } from './settings-formats.js';

export function formatLastLogin(lastLogin) {
  const raw = String(lastLogin || '').trim();
  if (!raw) return 'Never';
  const date = new Date(raw);
  if (Number.isNaN(date.getTime())) return 'Never';
  return date.toLocaleString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit'
  });
}

export async function loadStaffUsers() {
  const msgEl = document.getElementById('staff-users-msg');
  const bodyEl = document.getElementById('staff-users-table-body');
  const refreshBtn = document.getElementById('btn-refresh-staff-users');
  if (!msgEl || !bodyEl) {
    return;
  }

  if (refreshBtn) refreshBtn.disabled = true;
  msgEl.textContent = 'Loading staff users...';
  msgEl.className = 'mb-2 text-muted';
    bodyEl.innerHTML = '<tr><td colspan="7" class="text-muted">Loading staff users...</td></tr>';


  try {
    const orgId = currentLibraryContextOrgId || 'system';
    const result = await authorizedJson(`/api/asap/staff/users?orgId=${encodeURIComponent(orgId)}`);
    const users = Array.isArray(result.users) ? result.users : [];
    const totalAcrossSystem = result.totalAcrossSystem;
    setCanAssignSuperAdmin(!!result.canAssignSuperAdmin);
    renderStaffUsers(users);
    msgEl.textContent = users.length ? `Loaded ${users.length} staff user${users.length === 1 ? '' : 's'}.` : 'No staff users found.';

    if (isSuperAdminStaff() && currentLibraryContextOrgId && currentLibraryContextOrgId !== 'system' && totalAcrossSystem !== undefined) {
      const others = totalAcrossSystem - users.length;
      if (others > 0) {
        msgEl.textContent += ` (${others} other staff user${others === 1 ? '' : 's'} in different libraries)`;
      }
    }

    msgEl.className = 'mb-2 text-muted';
  } catch (err) {
    console.error('Failed to load staff users', err);
    msgEl.textContent = err.message || 'Failed to load staff users.';
    msgEl.className = 'mb-2 text-danger font-weight-bold';
    bodyEl.innerHTML = '<tr><td colspan="7" class="text-muted">Unable to load staff users.</td></tr>';

  } finally {
    if (refreshBtn) refreshBtn.disabled = false;
  }
}

export function renderStaffUsers(users) {
  const bodyEl = document.getElementById('staff-users-table-body');
  if (!bodyEl) {
    return;
  }

  bodyEl.innerHTML = '';

  if (!users.length) {
    const tr = document.createElement('tr');
    const td = document.createElement('td');
      td.colSpan = 7;

    td.className = 'text-muted';
    td.textContent = 'No staff users found.';
    tr.appendChild(td);
    bodyEl.appendChild(tr);
    return;
  }


  for (const user of users) {
    const tr = document.createElement('tr');
    tr.setAttribute('data-staff-id', user.id || '');

    const tdUsername = document.createElement('td');
    const strongUsername = document.createElement('strong');
    strongUsername.textContent = user.username || '';
    tdUsername.appendChild(strongUsername);
    tr.appendChild(tdUsername);

    const tdDomain = document.createElement('td');
    if (user.domain) {
      tdDomain.textContent = user.domain;
    } else {
      const span = document.createElement('span');
      span.className = 'text-muted';
      span.textContent = 'Default';
      tdDomain.appendChild(span);
    }
    tr.appendChild(tdDomain);

    const tdLibrary = document.createElement('td');
    tdLibrary.textContent = user.libraryOrgName || user.libraryOrgId || (user.scope === 'system' ? 'System' : 'Unmapped');
    tr.appendChild(tdLibrary);

    const tdDisplayName = document.createElement('td');
    if (user.displayName) {
      tdDisplayName.textContent = user.displayName;
    } else {
      const span = document.createElement('span');
      span.className = 'text-muted';
      span.textContent = 'No display name';
      tdDisplayName.appendChild(span);
    }
    tr.appendChild(tdDisplayName);

    const tdRole = document.createElement('td');
    tdRole.className = 'staff-role-cell';
    const select = document.createElement('select');
    select.className = 'form-control form-control-sm staff-role-select';

    const role = ['staff', 'admin', 'super_admin'].includes(String(user.role || '').toLowerCase()) ? String(user.role || '').toLowerCase() : 'staff';

    select.appendChild(new Option('Staff', 'staff', false, role === 'staff'));
    select.appendChild(new Option('Admin', 'admin', false, role === 'admin'));

    if (canAssignSuperAdmin) {
      select.appendChild(new Option('Super Admin', 'super_admin', false, role === 'super_admin'));
    }

    tdRole.appendChild(select);
    tr.appendChild(tdRole);


    const tdLastLogin = document.createElement('td');
    tdLastLogin.className = 'staff-last-login-cell';
    const lastLoginText = formatLastLogin(user.lastLogin);
    tdLastLogin.textContent = lastLoginText;
    if (lastLoginText === 'Never') tdLastLogin.classList.add('text-muted');
    tr.appendChild(tdLastLogin);

    const tdSave = document.createElement('td');
    tdSave.className = 'staff-actions-cell';
    const actionWrap = document.createElement('div');
    actionWrap.className = 'staff-actions-wrap';
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn btn-sm btn-primary staff-role-save mr-1';
    btn.textContent = 'Save Role';
    actionWrap.appendChild(btn);
    const del = document.createElement('button');
    del.type = 'button';
    del.className = 'btn btn-sm btn-outline-danger staff-user-delete';
    del.textContent = 'Remove';
    actionWrap.appendChild(del);
    tdSave.appendChild(actionWrap);
    tr.appendChild(tdSave);

    bodyEl.appendChild(tr);
  }
}

function formatAssignmentMapFromDom() {
  const byFormat = {};
  // 1. Start with the state variable (Source of truth for user intent)
  (currentFormatClaimRules || []).forEach(rule => {
    if (rule && rule.format) byFormat[rule.format] = rule.staffUserId || '';
  });
  // 2. Overlay with current DOM values if the Formats tab has been rendered.
  // This captures any pending changes made directly in the Formats tab dropdowns.
  document.querySelectorAll('.format-setting-row').forEach(row => {
    const format = row.getAttribute('data-key');
    const staffUserId = row.querySelector('.format-claim-staff-select')?.value || '';
    if (format) byFormat[format] = staffUserId;
  });
  return byFormat;
}



const staffUsersTableBody = document.getElementById('staff-users-table-body');
if (staffUsersTableBody) {
  staffUsersTableBody.addEventListener('click', async (e) => {
    const row = e.target.closest('tr[data-staff-id]');
    if (!row) return;
    const delBtn = e.target.closest('.staff-user-delete');
    if (delBtn) {
      const id = row.getAttribute('data-staff-id');
      const ok = await showConfirm('Remove staff member', 'Are you sure you want to remove this staff user from access?');
      if (!ok) return;
      delBtn.disabled = true;
      try {
        await authorizedJson(`/api/asap/staff/users/${encodeURIComponent(id)}`, { method: 'DELETE' });
        await populateStaffLibraryOptions();
    await loadStaffUsers();
      } catch (err) {
        const msgEl = document.getElementById('staff-users-msg');
        if (msgEl) { msgEl.textContent = err.message || 'Failed to remove staff user.'; msgEl.className = 'mb-2 text-danger font-weight-bold'; }
      } finally { delBtn.disabled = false; }
      return;
    }


    const btn = e.target.closest('.staff-role-save');
    if (!btn) return;


    if (!row) return;

    const id = row.getAttribute('data-staff-id');
    const select = row.querySelector('.staff-role-select');
    const nextRole = select ? select.value : 'staff';
    const msgEl = document.getElementById('staff-users-msg');

    btn.disabled = true;
    if (msgEl) {
      msgEl.textContent = 'Saving role...';
      msgEl.className = 'mb-2 text-muted';
    }

    try {
      await authorizedJson(`/api/asap/staff/users/${encodeURIComponent(id)}/role`, {
        method: 'POST',
        body: JSON.stringify({ role: nextRole })
      });
      if (msgEl) {
        msgEl.textContent = 'Staff role updated successfully.';
        msgEl.className = 'mb-2 text-success font-weight-bold';
      }
      await populateStaffLibraryOptions();
    await loadStaffUsers();
    } catch (err) {
      console.error('Failed to update staff role', err);
      if (msgEl) {
        msgEl.textContent = err.message || 'Failed to update staff role.';
        msgEl.className = 'mb-2 text-danger font-weight-bold';
      }
    } finally {
      btn.disabled = false;
    }
  });
}

const refreshStaffUsersBtn = document.getElementById('btn-refresh-staff-users');
if (refreshStaffUsersBtn) {
  refreshStaffUsersBtn.addEventListener('click', (e) => {
    e.preventDefault();
    loadStaffUsers();
  });
}

export async function populateStaffLibraryOptions() {
  const select = document.getElementById('staff-add-library');
  const context = document.getElementById('staff-add-library-context');
  if (!select || !context) return;

  const me = pb.authStore.model || {};
  const isSuper = isSuperAdminStaff();

  if (isSuper) {
    // Super admins always get the dropdown so they can add staff to any library.
    select.classList.remove('hidden');
    context.classList.add('hidden');

    const isLibraryContext = currentLibraryContextOrgId && currentLibraryContextOrgId !== 'system';
    
    select.innerHTML = '<option value="">Select library</option>';
    const orgs = await pb.collection('polaris_organizations').getFullList({
      filter: 'organizationCodeId = "2"',
      sort: 'displayName',
      requestKey: 'polaris-orgs-staff-options'
    });
    orgs.forEach(org => select.appendChild(new Option(`${org.displayName || org.name} (ID ${org.organizationId})`, org.organizationId)));

    if (isLibraryContext) {
      select.value = currentLibraryContextOrgId;
    }
  } else {
    select.classList.add('hidden');
    context.classList.remove('hidden');
    const libraryName = me.libraryOrgName || me.libraryOrgId || 'My Library';
    context.textContent = `${libraryName} (ID ${me.libraryOrgId || '?'})`;
    select.innerHTML = '';
    select.appendChild(new Option(libraryName, me.libraryOrgId));
    select.value = me.libraryOrgId;
  }
}

const addStaffBtn = document.getElementById('btn-add-staff-user');
if (addStaffBtn) {
  addStaffBtn.addEventListener('click', async () => {
    const identityInput = document.getElementById('staff-add-identity');
    const libSelect = document.getElementById('staff-add-library');
    const roleSelect = document.getElementById('staff-add-role');
    const msgEl = document.getElementById('staff-users-msg');

    const identity = (identityInput.value || '').trim();
    const libraryOrgId = (libSelect.value || '').trim();
    const role = (roleSelect.value || '').trim() || 'staff';

    if (!identity) return showAlert('Enter a staff username or identity.');
    if (role !== 'super_admin' && !libraryOrgId) return showAlert('Select a library for this staff member.');

    addStaffBtn.disabled = true;
    try {
      const opt = libSelect && libSelect.selectedIndex >= 0 ? libSelect.options[libSelect.selectedIndex] : null;
      await authorizedJson('/api/asap/staff/users', {
        method: 'POST',
        body: JSON.stringify({
          username: identity,
          libraryOrgId,
          libraryOrgName: opt ? opt.text : '',
          role
        })
      });

      identityInput.value = '';

      if (msgEl) {
        msgEl.innerHTML = '<i class="fa fa-check-circle"></i> Staff record created or updated. This user still signs in with their Polaris credentials.';
        msgEl.className = 'mb-2 text-success font-weight-bold';
      }

      await populateStaffLibraryOptions();
      await loadStaffUsers();
    } catch (err) {
      if (msgEl) {
        msgEl.textContent = err.message || 'Failed to add staff member.';
        msgEl.className = 'mb-2 text-danger font-weight-bold';
      }
    } finally {
      addStaffBtn.disabled = false;
    }
  });
}
