import { workflowStatusLabel } from './utils.js';

export function buildPendingAuditPreview(row, nextStatus, actionStr, ctx) {
  const username = (ctx.pb.authStore.model && ctx.pb.authStore.model.username) ? ctx.pb.authStore.model.username : 'staff';
  const actionDescriptions = {
    alreadyOwn: 'This request will be marked Already own and move directly to Closed',
    reject: 'This request will be rejected',
    silentClose: 'This request will be closed silently and move directly to Closed',
    purchase: 'This request will move to Pending purchase',
    reassign: 'This request will be reassigned to the selected format'
  };

  let preview = '';
  const actionText = actionDescriptions[actionStr];
  if (actionText) {
    preview = `${actionText} by ${username}.`;
  } else {
    const currentStatus = row.status || '';
    if (nextStatus && nextStatus !== currentStatus) {
      preview = `This request will move from ${workflowStatusLabel(currentStatus)} to ${workflowStatusLabel(nextStatus)} by ${username}.`;
    }
  }

  const editFormat = ctx.format;
  if (editFormat && editFormat.value && editFormat.value !== (row.format || 'book')) {
    const formatName = ctx.formatMap[editFormat.value] || editFormat.value;
    const formatChangeText = `Format will be updated to ${formatName}.`;
    preview = preview ? `${preview} ${formatChangeText}` : formatChangeText;
  }

  return preview;
}

export function renderPendingAuditPreview(row, nextStatus, actionStr, ctx) {
  const container = ctx.auditPreview;
  const text = ctx.auditPreviewText;
  if (!container || !text) return;

  const preview = buildPendingAuditPreview(row, nextStatus, actionStr, ctx);
  text.textContent = preview;
  container.classList.toggle('hidden', !preview);
}

export function refreshEditAuditPreview(ctx) {
  const id = ctx.id.value;
  const row = ctx.currentSuggestions.find(r => r.id === id) || ctx.allSuggestions.find(r => r.id === id);
  const nextStatus = ctx.nextStatus.value;
  const actionStr = ctx.action.value;
  if (row) renderPendingAuditPreview(row, nextStatus, actionStr, ctx);
}
