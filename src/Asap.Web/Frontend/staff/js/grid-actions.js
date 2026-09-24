import { openEdit, confirmAdditionalCopyAction } from './modals.js';
import { undoRow, deleteClosedRequest, closeDuplicateRequest } from './actions.js';
import { isAdminStaff } from './api.js';
import { authorizedJson } from './http.js';
import { showToast, showAlert, showConfirm } from './dialogs.js';
import { normalizeStatus } from './grid-policy.mjs';
import { buildRowActions } from './grid-row-actions.mjs';
import { escapeAttr } from './grid-utils.js';
import { hasWorkflowTag, isUnclaimed } from './grid-filters.js';
import { findWorkflowRow, requestIdentity, sameRequestIdentity } from './request-identity.mjs';
import { staffSession, staffAccessGeneration, currentSuggestions, allSuggestions, currentStatus, currentWorkflowOrgScopeId } from './state.js';

const noopRefresh = async () => {};
let rowActionGeneration = 0;
let assignmentGeneration = 0;
export function invalidatePendingRowAction() {
  rowActionGeneration += 1;
  assignmentGeneration += 1;
  const assignmentDialog = document.getElementById('assign-dialog');
  if (assignmentDialog?.open) assignmentDialog.close();
}
function selectPlaceholder(select, label) {
  const option = document.createElement('option');
  option.value = '';
  option.textContent = label;
  select.replaceChildren(option);
}

export function currentStaffId(ctx) {
  return String(ctx.staffSession.staff?.id || '').trim();
}

export function isClaimedByCurrentUser(row, ctx) {
  const staffId = currentStaffId(ctx);
  return !!staffId && String(row?.claimedByStaffUserId || '').trim() === staffId;
}

export function getRowActions(row, ctx, onRefresh = noopRefresh) {
  const descriptors = buildRowActions(row, {
    currentStatus: ctx.currentStatus,
    currentStaffId: currentStaffId(ctx),
    isAdmin: isAdminStaff()
  });
  return materializeRowActions(row, descriptors, ctx, onRefresh);
}

function materializeRowActions(row, actions, ctx, onRefresh) {
  return {
    ...actions,
    visible: actions.visible?.map(action => materializeRowAction(row, action, ctx, onRefresh)),
    primary: actions.primary ? materializeRowAction(row, actions.primary, ctx, onRefresh) : undefined,
    secondary: actions.secondary?.map(action => materializeRowAction(row, action, ctx, onRefresh)) || []
  };
}

function materializeRowAction(row, action, ctx, onRefresh) {
  return {
    ...action,
    onClick: () => runRowActionDescriptor(row, action, ctx, onRefresh)
  };
}

export async function runRowActionDescriptor(row, action, ctx, onRefresh = noopRefresh) {
  if (!['title_request', 'additional_copy'].includes(row?.type) || !String(row.id ?? '').trim()) return;
  const current = findWorkflowRow(row, currentSuggestions);
  if (!current || current.version !== row.version || current.status !== row.status) return;
  invalidatePendingRowAction();
  if (action.key === 'purchase') {
    const hasBib = String(row.bibid || '').trim().length > 0;
    openEdit(row, hasBib ? 'pending_hold' : 'outstanding_purchase', 'Approve for purchase', 'purchase', 'Purchase');
    return;
  }
  if (action.key === 'reject') {
    openEdit(row, 'closed', 'Reject', 'reject', 'Reject');
    return;
  }
  if (action.key === 'alreadyOwn') {
    openEdit(row, 'pending_hold', 'Already own', 'alreadyOwn', 'Already own');
    return;
  }
  if (action.key === 'silentClose') {
    openEdit(row, 'closed', 'Silent close', 'silentClose', 'Silent close');
    return;
  }
  if (action.key === 'queueHold') {
    openEdit(row, 'pending_hold', 'Queue for hold', 'catalogFound', 'Queue Hold');
    return;
  }
  if (action.key === 'close') {
    openEdit(row, 'closed', 'Close', 'close', 'Close');
    return;
  }
  if (action.key === 'undo') {
    await undoRow(row);
    return;
  }
  if (action.key === 'edit') {
    const status = normalizeStatus(row.status);
    const title = status === 'suggestion' ? 'Edit suggestion' : 'Edit';
    openEdit(row, row.status, title, 'edit', 'Save');
    return;
  }
  if (action.key === 'delete') {
    await deleteClosedRequest(row);
    return;
  }
  if (action.key === 'closeDuplicate') {
    await closeDuplicateRequest(row);
    return;
  }
  if (action.key === 'buyAnotherCopy') {
    await buyAnotherCopyForRow(row, ctx, onRefresh);
    return;
  }
  if (action.key === 'closeAdditionalCopy') {
    await closeAdditionalCopyRequest(row, onRefresh);
    return;
  }
  if (action.key === 'claim') {
    await claimRequest(row, ctx, onRefresh);
    return;
  }
  if (action.key === 'unclaim' || action.key === 'clearClaim') {
    await unclaimRequest(row, ctx, onRefresh);
    return;
  }
  if (action.key === 'assign') {
    await openAssignDialog(row, onRefresh);
  }
}

export async function openAssignDialog(row, onRefresh = noopRefresh) {
  if (!['title_request', 'additional_copy'].includes(row?.type) || !String(row.id ?? '').trim()) return;
  const generation = ++assignmentGeneration;
  const accessGeneration = staffAccessGeneration;
  const identity = requestIdentity(row);
  const status = currentStatus;
  const scope = currentWorkflowOrgScopeId;
  const ownsRequest = () => {
    const current = findWorkflowRow(identity, currentSuggestions, allSuggestions);
    return generation === assignmentGeneration && staffAccessGeneration === accessGeneration &&
      staffSession.authenticated && staffSession.accessAllowed && currentStatus === status &&
      currentWorkflowOrgScopeId === scope && current?.type === identity.type &&
      sameRequestIdentity(current, identity) && current.version === row.version &&
      current.status === row.status;
  };
  if (!ownsRequest()) return;
  const dialog = document.getElementById('assign-dialog');
  const staffSelect = document.getElementById('assign-staff-select');
  const contextText = document.getElementById('assign-dialog-context');
  const confirmBtn = document.getElementById('assign-confirm');
  const cancelBtn = document.getElementById('assign-cancel');

  if (!dialog || !staffSelect || !contextText || !confirmBtn || !cancelBtn) return;

  if (dialog.open) dialog.close();
  confirmBtn.textContent = 'Assign';
  confirmBtn.disabled = true;
  contextText.textContent = `Assigning: ${row.title || 'Untitled suggestion'}`;
  selectPlaceholder(staffSelect, 'Loading staff members...');
  staffSelect.value = '';

  try {
    const res = await authorizedJson(`/api/asap/staff/assignment-candidates?libraryOrgId=${encodeURIComponent(row.libraryOrgId)}`);
    if (!ownsRequest()) return;
    const users = res.candidates || [];

    selectPlaceholder(staffSelect, 'Select staff member...');
    if (users.length === 0) {
      selectPlaceholder(staffSelect, 'No active staff members found');
    } else {
      users.forEach(u => {
        const opt = document.createElement('option');
        opt.value = u.id;
        opt.textContent = u.displayName || u.username;
        staffSelect.appendChild(opt);
      });
    }
  } catch (err) {
    if (!ownsRequest()) return;
    selectPlaceholder(staffSelect, 'Error loading staff');
    console.error('Failed to load staff users', err);
  }

  staffSelect.onchange = () => {
    if (!ownsRequest() || !dialog.open) return;
    confirmBtn.disabled = !staffSelect.value;
  };

  const cleanup = () => {
    if (generation !== assignmentGeneration) return;
    assignmentGeneration += 1;
    confirmBtn.onclick = null;
    cancelBtn.onclick = null;
    if (dialog.open) dialog.close();
  };

  cancelBtn.onclick = cleanup;
  let submitting = false;
  confirmBtn.onclick = async () => {
    if (!ownsRequest() || !dialog.open || submitting) return;
    const assigneeId = staffSelect.value;
    if (!assigneeId) return;

    submitting = true;
    confirmBtn.disabled = true;
    confirmBtn.textContent = 'Assigning...';

    try {
      const endpointPrefix = row.type === 'additional_copy' ? 'additional-copies' : 'title-requests';
      await authorizedJson(`/api/asap/staff/${endpointPrefix}/${encodeURIComponent(row.id)}/assign`, {
        method: 'POST',
        body: { assigneeId, version: row.version }
      });
      if (!ownsRequest()) return;
      const typeLabel = row.type === 'additional_copy' ? 'Additional-copy task' : 'Claim';
      showToast(`${typeLabel} assigned.`, 'success');
      cleanup();
      await onRefresh();
    } catch (err) {
      if (!ownsRequest()) return;
      const message = err && err.message ? err.message : 'Assignment failed.';
      await showAlert(message);
      confirmBtn.disabled = false;
      confirmBtn.textContent = 'Assign';
      submitting = false;
    }
  };

  if (ownsRequest() && !dialog.open) dialog.showModal();
}

export async function closeAdditionalCopyRequest(identity, onRefresh = noopRefresh) {
  if (identity?.type !== 'additional_copy' || !String(identity.id ?? '').trim()) return;
  const row = findWorkflowRow(identity, currentSuggestions, allSuggestions);
  if (!row || row.type !== 'additional_copy' || normalizeStatus(row.status) === 'closed') return;
  const generation = ++rowActionGeneration;
  const accessGeneration = staffAccessGeneration;
  const status = currentStatus;
  const scope = currentWorkflowOrgScopeId;
  const ownsRequest = () => {
    const current = findWorkflowRow(row, currentSuggestions, allSuggestions);
    return generation === rowActionGeneration && staffAccessGeneration === accessGeneration &&
      staffSession.authenticated && staffSession.accessAllowed && currentStatus === status &&
      currentWorkflowOrgScopeId === scope && current?.type === 'additional_copy' &&
      sameRequestIdentity(current, row) && current.version === row.version && current.status === row.status;
  };
  const confirmed = await showConfirm('Close additional-copy task?', 'Closing this task will not change the original patron suggestion.');
  if (!confirmed || !ownsRequest()) return;
  try {
    await authorizedJson(`/api/asap/staff/additional-copies/${encodeURIComponent(String(identity.id))}/close`, {
      method: 'POST',
      body: { version: row.version }
    });
    if (!ownsRequest()) return;
    showToast('Additional-copy task closed.', 'success');
    await onRefresh();
  } catch (error) {
    if (ownsRequest()) throw error;
  }
}

export function additionalCopyActionForRow(row) {
  const status = normalizeStatus(row && row.status);
  const bibid = String(row && row.bibid || '').trim();
  if ((status !== 'pending_hold' && status !== 'hold_placed') || !bibid || row.type !== 'title_request') {
    return null;
  }
  return { label: 'Buy another copy', onClick: () => buyAnotherCopyForRow(row) };
}

export function additionalCopyConfirmMessage(bibid, count) {
  if (count === 1) {
    return `There is already 1 open additional-copy task for this BIB. Create another?`;
  }
  if (count > 1) {
    return `There are already ${count} open additional-copy tasks for this BIB. Create another?`;
  }
  return bibid ? `Create an additional-copy task for BIB ${bibid}?` : 'Create an additional-copy task for this BIB?';
}

export async function buyAnotherCopyForRow(row, ctx, onRefresh = noopRefresh) {
  if (row?.type !== 'title_request' || !String(row.id ?? '').trim()) return;
  const generation = ++rowActionGeneration;
  const accessGeneration = staffAccessGeneration;
  const identity = requestIdentity(row);
  const status = currentStatus;
  const scope = currentWorkflowOrgScopeId;
  const ownsRequest = () => {
    const current = findWorkflowRow(identity, currentSuggestions, allSuggestions);
    return generation === rowActionGeneration && staffAccessGeneration === accessGeneration &&
      staffSession.authenticated && staffSession.accessAllowed && currentStatus === status &&
      currentWorkflowOrgScopeId === scope && current?.type === 'title_request' &&
      sameRequestIdentity(current, identity) && current.version === row.version &&
      current.status === row.status && current.bibid === row.bibid;
  };
  if (!ownsRequest()) return;
  const id = String(row.id);
  try {
    const preview = await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/additional-copy`, { cache: 'no-store' });
    if (!ownsRequest()) return;
    const bibid = String(preview.bibid || row.bibid || '').trim();
    const openCount = Number(preview.openCount || 0);
    const confirmed = await confirmAdditionalCopyAction({ bibId: bibid }, {
      message: additionalCopyConfirmMessage(bibid, openCount),
      emailPurchaseReminderDefault: preview.emailPurchaseReminderDefault,
      isCurrent: ownsRequest
    });
    if (!ownsRequest() || !confirmed?.confirmed) return;
    const response = await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/additional-copy`, {
      method: 'POST',
      body: { emailPurchaseReminder: confirmed.emailPurchaseReminder, version: row.version }
    });
    if (!ownsRequest()) return;
    const afterCount = Number(response && response.openCountAfter || openCount + 1);
    showToast(`Additional-copy task created. Open tasks for this BIB: ${afterCount}.`, 'success');
    await onRefresh();
  } catch (error) {
    if (ownsRequest()) throw error;
  }
}

export function duplicateCloseActionForRow(row) {
  if (!row || normalizeStatus(row.status) === 'closed' || !hasWorkflowTag(row, 'Hold exists (same patron)')) {
    return null;
  }
  return { label: 'Close duplicate', className: 'danger', onClick: () => closeDuplicateRequest(row) };
}

export function claimActionsForRow(row, ctx, onRefresh = noopRefresh) {
  if (isUnclaimed(row)) {
    return [{ label: 'Claim', onClick: () => claimRequest(row, ctx, onRefresh) }];
  }
  if (isClaimedByCurrentUser(row, ctx)) {
    return [{ label: 'Unclaim', onClick: () => unclaimRequest(row, ctx, onRefresh) }];
  }
  if (isAdminStaff()) {
    return [{ label: 'Clear claim', className: 'danger', onClick: () => unclaimRequest(row, ctx, onRefresh) }];
  }
  return [];
}

export async function claimRequest(identity, ctx, onRefresh = noopRefresh) {
  await mutateRequestClaim(identity, 'claim', 'Request claimed.', ctx, onRefresh);
}

export async function unclaimRequest(identity, ctx, onRefresh = noopRefresh) {
  await mutateRequestClaim(identity, 'unclaim', 'Request unclaimed.', ctx, onRefresh);
}

export async function mutateRequestClaim(identity, action, successMessage, ctx, onRefresh = noopRefresh) {
  if (!['title_request', 'additional_copy'].includes(identity?.type) || !String(identity.id ?? '').trim()) return;
  const row = findWorkflowRow(identity, ctx.currentSuggestions, ctx.allSuggestions);
  if (!row || row.type !== identity.type) return;
  const requestId = row.id;
  const accessGeneration = staffAccessGeneration;
  const status = ctx.currentStatus;
  const scope = currentWorkflowOrgScopeId;
  const ownsRequest = () => {
    const current = findWorkflowRow(identity, ctx.currentSuggestions, ctx.allSuggestions);
    return staffAccessGeneration === accessGeneration && staffSession.authenticated &&
      staffSession.accessAllowed && ctx.currentStatus === status && currentWorkflowOrgScopeId === scope &&
      current?.type === row.type && current.version === row.version && current.status === row.status;
  };
  if (!ownsRequest()) return;

  try {
    const endpointPrefix = row.type === 'additional_copy' ? 'additional-copies' : 'title-requests';
    await authorizedJson(`/api/asap/staff/${endpointPrefix}/${encodeURIComponent(requestId)}/${action}`, {
      method: 'POST',
      body: { version: row.version }
    });
    if (ownsRequest()) showToast(successMessage, 'success');
  } catch (err) {
    if (ownsRequest()) await showAlert(err.message || 'Claim update failed.');
  } finally {
    if (ownsRequest()) await onRefresh();
  }
}

export async function runRowAction(action, ctx) {
  closeActionMenu(ctx);
  try {
    await action.onClick();
  } catch (error) {
    await showAlert(error.message || String(error) || 'Action failed');
  }
}

export function registerRowAction(action, ctx) {
  const actionId = `row-action-${ctx.incrementRowActionIdCounter()}`;
  ctx.rowActionRegistry.set(actionId, action);
  return actionId;
}

export function getRegisteredRowAction(actionId, ctx) {
  return ctx.rowActionRegistry.get(actionId);
}

export function renderRowActions(row, ctx, onRefresh = noopRefresh) {
  const actions = getRowActions(row, ctx, onRefresh);
  let markup = `<div class="row-action-group" data-no-row-edit="true">`;

  if (actions.visible && actions.visible.length > 0) {
    actions.visible.forEach((action, index) => {
      const actionId = registerRowAction(action, ctx);
      const isFirst = index === 0;
      const isLast = (index === actions.visible.length - 1) && (!actions.secondary || actions.secondary.length === 0);

      let classes = `btn btn-sm ${action.className || 'btn-primary'}`;
      if (isFirst) {
        classes += ' row-action-primary';
      } else if (isLast) {
        // No special class needed, default border radii apply on right
      } else {
        classes += ' row-action-middle';
      }

      markup += `<button type="button" class="${escapeAttr(classes)}" data-row-action-id="${actionId}" data-no-row-edit="true">${escapeAttr(action.label)}</button>`;
    });
  } else if (actions.primary) {
    const primaryActionId = registerRowAction(actions.primary, ctx);
    markup += `<button type="button" class="btn btn-sm row-action-primary ${escapeAttr(actions.primary.className || 'btn-primary')}" data-row-action-id="${primaryActionId}" data-no-row-edit="true">${escapeAttr(actions.primary.label)}</button>`;
  }

  if (actions.secondary?.length) {
    const menuActionIds = actions.secondary.map(action => registerRowAction(action, ctx)).join(',');
    markup += `<button type="button" class="btn btn-sm btn-outline-secondary row-action-menu-trigger" aria-haspopup="menu" aria-expanded="false" data-row-menu-action-ids="${menuActionIds}" data-no-row-edit="true">⋯</button>`;
  }
  markup += `</div>`;
  return markup;
}

export function openActionMenu(triggerButton, actionIds, ctx) {
  closeActionMenu(ctx);
  const layer = document.getElementById('action-menu-layer');
  if (!layer) return;
  triggerButton.setAttribute('aria-expanded', 'true');
  const menu = document.createElement('div');
  menu.className = 'row-action-menu';
  menu.setAttribute('role', 'menu');
  actionIds.forEach((actionId) => {
    const action = getRegisteredRowAction(actionId, ctx);
    if (!action) return;
    const item = document.createElement('button');
    item.type = 'button';
    item.className = `row-action-menu-item ${action.className || ''}`.trim();
    item.setAttribute('role', 'menuitem');
    item.setAttribute('data-row-action-id', actionId);
    item.setAttribute('data-no-row-edit', 'true');
    item.textContent = action.label;
    menu.appendChild(item);
  });
  layer.appendChild(menu);
  positionActionMenu(triggerButton, menu);
  ctx.setActiveActionMenu({ triggerButton, menu });
  menu.querySelector('[role="menuitem"]')?.focus();
}

export function positionActionMenu(triggerButton, menu) {
  const triggerRect = triggerButton.getBoundingClientRect();
  const menuRect = menu.getBoundingClientRect();
  const spacing = 6;
  const viewportPadding = 8;
  let top = triggerRect.bottom + spacing;
  let left = triggerRect.right - menuRect.width;
  if (top + menuRect.height > window.innerHeight - viewportPadding) {
    top = triggerRect.top - menuRect.height - spacing;
  }
  left = Math.max(viewportPadding, Math.min(left, window.innerWidth - menuRect.width - viewportPadding));
  menu.style.top = `${top}px`;
  menu.style.left = `${left}px`;
}

export function closeActionMenu(ctx) {
  if (!ctx.activeActionMenu) return;
  ctx.activeActionMenu.triggerButton?.setAttribute('aria-expanded', 'false');
  ctx.activeActionMenu.menu?.remove();
  ctx.setActiveActionMenu(null);
}

export function formatCloseReason(row, ctx) {
  if (normalizeStatus(row.status) !== 'closed') {
    return '';
  }
  return ctx.closeReasonMap[row.closeReason] || 'Closed';
}
