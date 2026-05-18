import { effectiveWorkflowFlagsForRow, normalizeStatus } from './grid-policy.mjs';

function isUnclaimed(row) {
  return !String(row?.claimedByStaffUserId || '').trim();
}

function isClaimedByCurrentUser(row, currentStaffId) {
  const staffId = String(currentStaffId || '').trim();
  return !!staffId && String(row?.claimedByStaffUserId || '').trim() === staffId;
}

function claimActionDescriptors(row, context = {}) {
  if (isUnclaimed(row)) {
    return [{ key: 'claim', label: 'Claim' }];
  }
  if (isClaimedByCurrentUser(row, context.currentStaffId)) {
    return [{ key: 'unclaim', label: 'Unclaim' }];
  }
  if (context.isAdmin) {
    return [{ key: 'clearClaim', label: 'Clear claim', className: 'danger' }];
  }
  return [];
}

function hasDuplicateHold(row) {
  return effectiveWorkflowFlagsForRow(row).includes('Hold exists (same patron)');
}

function duplicateCloseActionDescriptor(row) {
  if (!row || normalizeStatus(row.status) === 'closed' || !hasDuplicateHold(row)) {
    return null;
  }
  return { key: 'closeDuplicate', label: 'Close duplicate', className: 'danger' };
}

function additionalCopyActionDescriptor(row) {
  const status = normalizeStatus(row && row.status);
  const bibid = String(row && row.bibid || '').trim();
  if ((status !== 'pending_hold' && status !== 'hold_placed') || !bibid || row.type === 'additional_copy') {
    return null;
  }
  return { key: 'buyAnotherCopy', label: 'Buy another copy' };
}

export function buildRowActions(row, context = {}) {
  if (context.currentStatus === 'additional_copies') {
    return {
      primary: { key: 'closeAdditionalCopy', label: 'Close', className: 'btn-outline-secondary' },
      secondary: claimActionDescriptors(row, context)
    };
  }

  const status = normalizeStatus(row?.status);

  if (status === 'suggestion') {
    return {
      visible: [
        { key: 'purchase', label: 'Purchase', className: 'btn-primary' },
        { key: 'reject', label: 'Reject', className: 'btn-outline-danger' }
      ],
      secondary: [
        { key: 'alreadyOwn', label: 'Already own' },
        ...claimActionDescriptors(row, context),
        { key: 'silentClose', label: 'Silent close', className: 'danger' },
        { key: 'edit', label: 'Edit' }
      ]
    };
  }

  if (status === 'outstanding_purchase') {
    const duplicateCloseAction = duplicateCloseActionDescriptor(row);
    return {
      primary: { key: 'queueHold', label: 'Queue Hold', className: 'btn-success' },
      secondary: [
        ...(duplicateCloseAction ? [duplicateCloseAction] : []),
        ...claimActionDescriptors(row, context),
        { key: 'silentClose', label: 'Silent close', className: 'danger' },
        { key: 'undo', label: 'Undo' },
        { key: 'edit', label: 'Edit' }
      ]
    };
  }

  if (status === 'pending_hold' || status === 'hold_placed' || status === 'closed') {
    const secondary = [];
    const duplicateCloseAction = duplicateCloseActionDescriptor(row);
    if (duplicateCloseAction && status !== 'closed') {
      secondary.push(duplicateCloseAction);
    }
    const additionalCopyAction = additionalCopyActionDescriptor(row);
    if (additionalCopyAction) {
      secondary.push(additionalCopyAction);
    }
    claimActionDescriptors(row, context).forEach(action => secondary.push(action));
    if (status !== 'closed') {
      secondary.push({ key: 'silentClose', label: 'Silent close', className: 'danger' });
    }
    secondary.push({ key: 'edit', label: 'Edit' });
    if (status === 'closed' && context.isAdmin) {
      secondary.push({ key: 'delete', label: 'Delete', className: 'danger' });
    }
    return {
      primary: { key: 'undo', label: 'Undo', className: 'btn-outline-secondary' },
      secondary
    };
  }

  return {
    primary: { key: 'edit', label: 'Edit', className: 'btn-secondary' },
    secondary: claimActionDescriptors(row, context)
  };
}
