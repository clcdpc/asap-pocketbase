import { openEdit, confirmAdditionalCopyAction } from './modals.js';
import { undoRow, deleteClosedRequest, closeDuplicateRequest } from './actions.js';
import { isAdminStaff } from './api.js';
import { authorizedJson } from './http.js';
import { showToast, showAlert, showConfirm } from './dialogs.js';
import { normalizeStatus } from './grid-policy.mjs';
import { buildRowActions } from './grid-row-actions.mjs';
import { escapeAttr } from './grid-utils.js';
import { hasWorkflowTag, isUnclaimed } from './grid-filters.js';

const noopRefresh = async () => {};

export function currentStaffId(ctx) {
  return String((ctx.pb.authStore.model && ctx.pb.authStore.model.id) || '').trim();
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
  if (action.key === 'purchase') {
    const hasBib = String(row.bibid || '').trim().length > 0;
    openEdit(row.id, hasBib ? 'pending_hold' : 'outstanding_purchase', 'Approve for purchase', 'purchase', 'Purchase');
    return;
  }
  if (action.key === 'reject') {
    openEdit(row.id, 'closed', 'Reject', 'reject', 'Reject');
    return;
  }
  if (action.key === 'alreadyOwn') {
    openEdit(row.id, 'pending_hold', 'Already own', 'alreadyOwn', 'Already own');
    return;
  }
  if (action.key === 'silentClose') {
    openEdit(row.id, 'closed', 'Silent close', 'silentClose', 'Silent close');
    return;
  }
  if (action.key === 'queueHold') {
    openEdit(row.id, 'pending_hold', 'Queue for hold', '', 'Queue Hold');
    return;
  }
  if (action.key === 'undo') {
    await undoRow(row.id);
    return;
  }
  if (action.key === 'edit') {
    const status = normalizeStatus(row.status);
    const title = status === 'suggestion' ? 'Edit suggestion' : 'Edit';
    openEdit(row.id, row.status, title, '', 'Save');
    return;
  }
  if (action.key === 'delete') {
    await deleteClosedRequest(row.id);
    return;
  }
  if (action.key === 'closeDuplicate') {
    await closeDuplicateRequest(row.id);
    return;
  }
  if (action.key === 'buyAnotherCopy') {
    await buyAnotherCopyForRow(row, ctx, onRefresh);
    return;
  }
  if (action.key === 'closeAdditionalCopy') {
    await closeAdditionalCopyRequest(row.id, onRefresh);
    return;
  }
  if (action.key === 'claim') {
    await claimRequest(row.id, ctx, onRefresh);
    return;
  }
  if (action.key === 'unclaim' || action.key === 'clearClaim') {
    await unclaimRequest(row.id, ctx, onRefresh);
    return;
  }
  if (action.key === 'assign') {
    await openAssignDialog(row, onRefresh);
  }
}

export async function openAssignDialog(row, onRefresh = noopRefresh) {
  const dialog = document.getElementById('assign-dialog');
  const staffSelect = document.getElementById('assign-staff-select');
  const contextText = document.getElementById('assign-dialog-context');
  const confirmBtn = document.getElementById('assign-confirm');
  const cancelBtn = document.getElementById('assign-cancel');

  if (!dialog || !staffSelect || !contextText || !confirmBtn || !cancelBtn) return;

  // Reset dialog state
  confirmBtn.textContent = 'Assign';
  confirmBtn.disabled = true;
  contextText.textContent = `Assigning: ${row.title || 'Untitled suggestion'}`;
  staffSelect.innerHTML = '<option value="">Loading staff members...</option>';
  staffSelect.value = '';

  try {
    const type = row.type === 'additional_copy' ? 'additional_copy' : 'title_request';
    const res = await authorizedJson(`/api/asap/staff/assignable-users?type=${encodeURIComponent(type)}&id=${encodeURIComponent(row.id)}`);
    const users = res.users || [];

    staffSelect.innerHTML = '<option value="">Select staff member...</option>';
    if (users.length === 0) {
      staffSelect.innerHTML = '<option value="">No active staff members found</option>';
    } else {
      users.forEach(u => {
        const opt = document.createElement('option');
        opt.value = u.id;
        opt.textContent = u.displayName || u.username;
        staffSelect.appendChild(opt);
      });
    }
  } catch (err) {
    staffSelect.innerHTML = '<option value="">Error loading staff</option>';
    console.error('Failed to load staff users', err);
  }

  staffSelect.onchange = () => {
    confirmBtn.disabled = !staffSelect.value;
  };

  const cleanup = () => {
    confirmBtn.onclick = null;
    cancelBtn.onclick = null;
    if (dialog.open) dialog.close();
  };

  cancelBtn.onclick = cleanup;
  confirmBtn.onclick = async () => {
    const assigneeId = staffSelect.value;
    if (!assigneeId) return;

    confirmBtn.disabled = true;
    confirmBtn.textContent = 'Assigning...';

    try {
      const endpointPrefix = row.type === 'additional_copy' ? 'additional-copies' : 'title-requests';
      await authorizedJson(`/api/asap/staff/${endpointPrefix}/${encodeURIComponent(row.id)}/assign`, {
        method: 'POST',
        body: { assigneeId }
      });
      const typeLabel = row.type === 'additional_copy' ? 'Additional-copy task' : 'Claim';
      showToast(`${typeLabel} assigned.`, 'success');
      cleanup();
      await onRefresh();
    } catch (err) {
      const message = err && err.message ? err.message : 'Assignment failed.';
      await showAlert(message);
      confirmBtn.disabled = false;
      confirmBtn.textContent = 'Assign';
    }
  };

  dialog.showModal();
}

export async function closeAdditionalCopyRequest(id, onRefresh = noopRefresh) {
  const confirmed = await showConfirm('Close additional-copy task?', 'Closing this task will not change the original patron suggestion.');
  if (!confirmed) return;
  await authorizedJson(`/api/asap/staff/additional-copies/${encodeURIComponent(id)}/close`, {
    method: 'POST',
    body: {}
  });
  showToast('Additional-copy task closed.', 'success');
  await onRefresh();
}

export function additionalCopyActionForRow(row) {
  const status = normalizeStatus(row && row.status);
  const bibid = String(row && row.bibid || '').trim();
  if ((status !== 'pending_hold' && status !== 'hold_placed') || !bibid || row.type === 'additional_copy') {
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
  const id = row && row.id;
  if (!id) return;
  const preview = await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/additional-copy`, { cache: 'no-store' });
  const bibid = String(preview.bibid || row.bibid || '').trim();
  const openCount = Number(preview.openCount || 0);
  const confirmed = await confirmAdditionalCopyAction({ bibId: bibid }, {
    message: additionalCopyConfirmMessage(bibid, openCount),
    emailPurchaseReminderDefault: preview.emailPurchaseReminderDefault
  });
  if (!confirmed || !confirmed.confirmed) return;
  const response = await authorizedJson(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/additional-copy`, {
    method: 'POST',
    body: { emailPurchaseReminder: confirmed.emailPurchaseReminder }
  });
  const afterCount = Number(response && response.openCountAfter || openCount + 1);
  showToast(`Additional-copy task created. Open tasks for this BIB: ${afterCount}.`, 'success');
  await onRefresh();
}

export function duplicateCloseActionForRow(row) {
  if (!row || normalizeStatus(row.status) === 'closed' || !hasWorkflowTag(row, 'Hold exists (same patron)')) {
    return null;
  }
  return { label: 'Close duplicate', className: 'danger', onClick: () => closeDuplicateRequest(row.id) };
}

export function claimActionsForRow(row, ctx, onRefresh = noopRefresh) {
  if (isUnclaimed(row)) {
    return [{ label: 'Claim', onClick: () => claimRequest(row.id, ctx, onRefresh) }];
  }
  if (isClaimedByCurrentUser(row, ctx)) {
    return [{ label: 'Unclaim', onClick: () => unclaimRequest(row.id, ctx, onRefresh) }];
  }
  if (isAdminStaff()) {
    return [{ label: 'Clear claim', className: 'danger', onClick: () => unclaimRequest(row.id, ctx, onRefresh) }];
  }
  return [];
}

export async function claimRequest(requestId, ctx, onRefresh = noopRefresh) {
  await mutateRequestClaim(requestId, 'claim', 'Request claimed.', ctx, onRefresh);
}

export async function unclaimRequest(requestId, ctx, onRefresh = noopRefresh) {
  await mutateRequestClaim(requestId, 'unclaim', 'Request unclaimed.', ctx, onRefresh);
}

export async function mutateRequestClaim(requestId, action, successMessage, ctx, onRefresh = noopRefresh) {
  const row = ctx.currentSuggestions.find(r => r.id === requestId) || ctx.allSuggestions.find(r => r.id === requestId);
  if (!row) return;

  try {
    const endpointPrefix = row.type === 'additional_copy' ? 'additional-copies' : 'title-requests';
    await authorizedJson(`/api/asap/staff/${endpointPrefix}/${encodeURIComponent(requestId)}/${action}`, {
      method: 'POST',
      body: {}
    });
    showToast(successMessage, 'success');
  } catch (err) {
    await showAlert(err.message || 'Claim update failed.');
  } finally {
    await onRefresh();
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
