import { normalizedAdditionalCopyPublication, staffProfileEmail } from './utils.js';
import { rememberRecentSuggestion, renderRecentSuggestionsSwitcher } from '../recent-suggestions.js';
import { loadEditPickupForRequest } from '../edit-pickup.js';
import { setSelectValue, dateOnly } from '../settings-ui.js';
import { renderEditCustomFields } from '../request-custom-fields.js';
import { leapBibUrl } from '../api.js';
import { escapeAttr, formatDateTime } from '../grid.js';
import { renderEditWorkflowTags, renderEditClaimState } from './claim-tags.js';
import { renderPendingAuditPreview } from './audit-preview.js';
import { renderRejectionTemplateSelector } from './rejection-templates.js';
import { renderEditPatronContext } from './patron-context.js';

export function openEdit(id, nextStatus, dialogTitle, actionStr, buttonLabel, ctx) {
  const row = ctx.currentSuggestions.find(r => r.id === id) || ctx.allSuggestions.find(r => r.id === id);
  if (!row) return;
  const isAdditionalCopy = row.type === 'additional_copy';

  rememberRecentSuggestion(row);
  renderRecentSuggestionsSwitcher();

  ctx.modalLabel.textContent = dialogTitle;
  ctx.id.value = row.id;
  ctx.nextStatus.value = nextStatus;
  ctx.action.value = actionStr;
  setBibIdRequirement(nextStatus, ctx);

  if (ctx.submitBtn) {
    ctx.submitBtn.textContent = buttonLabel || 'Save';
  }

  ctx.title.value = row.title || '';
  ctx.author.value = row.author || '';
  ctx.identifier.value = row.identifier || '';
  ctx.bibid.value = row.bibid || '';

  const fmt = row.format || 'book';
  if (fmt && !ctx.availableFormats.includes(fmt)) {
    if (!Array.from(ctx.format.options).some(o => o.value === fmt)) {
      const opt = document.createElement('option');
      opt.value = fmt;
      opt.textContent = ctx.formatMap[fmt] || fmt;
      ctx.format.appendChild(opt);
    }
  }
  ctx.format.value = fmt;
  const publicationValue = normalizedAdditionalCopyPublication(row.publication, isAdditionalCopy, ctx.publicationOptions);
  setSelectValue(ctx.publication, publicationValue || ctx.publicationOptions[0]);
  ctx.exactPublicationDate.value = dateOnly(row.exactPublicationDate);
  ctx.autohold.checked = !!row.autohold;

  const autoholdContainer = ctx.autohold?.closest('.custom-control');
  if (autoholdContainer) {
    autoholdContainer.classList.toggle('hidden', isAdditionalCopy);
  }

  if (ctx.bibidHint) {
    if (isAdditionalCopy) {
      ctx.bibidHint.textContent = 'Required for all additional-copy tasks. Use Lookup to verify.';
    } else {
      ctx.bibidHint.textContent = 'Required for Pending hold. Use Lookup to verify.';
    }
  }
  applyHoldPlacedBibLock(row, ctx);

  renderEditPatronContext(row, ctx);
  renderEditWorkflowTags(row.workflowTags, row, ctx);
  renderEditClaimState(row, ctx);
  renderEditLeapBibLink(row.bibid, ctx);
  renderExternalSearchButton(row.title, row.identifier, ctx);
  renderPurchaseReminderOption(actionStr, ctx);
  renderEditMetadata(row, ctx);
  loadEditPickupForRequest(row);
  renderEditCustomFieldsForCurrentFormat(row, ctx);

  ctx.notes.value = getExistingHistory(row);
  renderPendingAuditPreview(row, nextStatus, actionStr, ctx);

  ctx.bibInfoDisplay.classList.add('hidden');
  ctx.bibInfoText.textContent = '';
  ctx.setVerifiedBibId(row.bibid || '');

  renderRejectionTemplateSelector(actionStr, ctx);

  ctx.modal.showModal();
  document.getElementById('close-modal-btn').focus();
}

function applyHoldPlacedBibLock(row, ctx) {
  const isLocked = row.status === 'hold_placed';
  const bibHint = ctx.bibidHint;

  ctx.bibid.disabled = isLocked;
  const bibLookupBtn = document.getElementById('btn-bib-lookup');
  if (bibLookupBtn) {
    bibLookupBtn.disabled = isLocked;
    bibLookupBtn.classList.toggle('hidden', isLocked);
  }
  document.getElementById('edit-title-polaris-search')?.classList.toggle('hidden', isLocked);
  document.getElementById('edit-author-polaris-search')?.classList.toggle('hidden', isLocked);
  document.getElementById('edit-identifier-polaris-search')?.classList.toggle('hidden', isLocked);
  if (bibHint && isLocked) {
    bibHint.textContent = 'BIB ID is locked because the hold has already been placed.';
    bibHint.classList.remove('text-danger', 'font-weight-bold');
  }
}

export function renderEditCustomFieldsForCurrentFormat(row, ctx) {
  const format = (ctx.format && ctx.format.value) || (row && row.format) || 'book';
  const formatRule = ctx.currentFormatRules[format] || {};
  renderEditCustomFields(row, ctx.currentAdditionalFieldDefinitions, formatRule.customFields || {});
}

export function getExistingHistory(row) {
  return (row.notes || '').trim();
}

export function getDraftCommentValue(ctx) {
  return ctx.notes.value;
}

export function setBibIdRequirement(nextStatus, ctx) {
  const bibInput = ctx.bibid;
  const bibRequiredMarker = ctx.bibidRequiredMarker;
  const bibHint = ctx.bibidHint;

  const isRequired = nextStatus === 'pending_hold';
  bibInput.required = isRequired;
  bibInput.setAttribute('aria-required', String(isRequired));
  if (bibRequiredMarker) {
    bibRequiredMarker.classList.toggle('hidden', !isRequired);
  }
  if (bibHint) {
    bibHint.classList.toggle('text-danger', isRequired);
    bibHint.classList.toggle('font-weight-bold', isRequired);
    bibHint.textContent = isRequired
      ? 'Required before moving this suggestion to the Pending hold phase.'
      : 'Needed to link this request to a catalog record.';
  }
}

export function renderEditLeapBibLink(bibId, ctx) {
  const container = ctx.leapBibLinkContainer;
  if (!container) return;
  const url = leapBibUrl(bibId);
  if (!url || !/^https?:\/\//i.test(url)) {
    container.classList.add('hidden');
    container.innerHTML = '';
    return;
  }
  container.classList.remove('hidden');
  container.innerHTML = `<a class="btn btn-sm btn-outline-primary" href="${escapeAttr(url)}" target="_blank" rel="noopener noreferrer">Open Bib in Leap</a>`;
}

export function renderExternalSearchButton(title, identifier, ctx) {
  const container = ctx.externalSearchContainer;
  if (!container) return;

  const encodedTitle = encodeURIComponent(title || '');
  const encodedId = encodeURIComponent(identifier || '');
  const buttonClasses = ['btn-warning', 'btn-success', 'btn-primary', 'btn-info'];

  const providers = [];
  for (let i = 1; i <= 4; i++) {
    providers.push({
      enabled: ctx.workflowSettings[`externalSearch${i}Enabled`],
      label: ctx.workflowSettings[`externalSearch${i}Label`],
      template: ctx.workflowSettings[`externalSearch${i}UrlTemplate`]
    });
  }

  const nodes = [];
  providers.forEach((p, index) => {
    if (!p.enabled || !p.template || !/^https?:\/\//i.test(p.template)) return;

    let url = p.template;
    url = url.replace(/\{\{title\}\}/g, encodedTitle);
    url = url.replace(/\{\{identifier\}\}/g, encodedId);

    const btnClass = buttonClasses[index] || 'btn-info';
    const a = document.createElement('a');
    a.href = url;
    a.target = '_blank';
    a.rel = 'noopener noreferrer';
    a.className = `btn btn-xs ${btnClass} mr-1 mb-1`;
    
    const icon = document.createElement('i');
    icon.className = 'fa fa-external-link';
    icon.setAttribute('aria-hidden', 'true');
    a.appendChild(icon);
    a.append(' ' + (p.label || 'Search'));
    nodes.push(a);
  });

  container.replaceChildren(...nodes);
}

export function renderEditMetadata(row, ctx) {
  const editBody = document.querySelector('#editModal .asap-dialog-edit-body');
  if (!editBody) return;

  let block = ctx.metadataBlock;
  if (!block) {
    block = document.createElement('div');
    block.id = 'edit-metadata';
    block.className = 'mt-3 pt-2 border-top small text-muted';
    editBody.appendChild(block);
  }

  const lastChecked = row.lastPromoterCheck ? formatDateTime(row.lastPromoterCheck) : null;
  if (lastChecked) {
    block.textContent = `Auto-promoter last checked: ${escapeAttr(lastChecked)}`;
    block.classList.remove('hidden');
  } else {
    block.innerHTML = '';
    block.classList.add('hidden');
  }
}

export function renderPurchaseReminderOption(actionStr, ctx) {
  const container = ctx.purchaseReminderContainer;
  const checkbox = ctx.purchaseReminderCheckbox;
  const help = ctx.purchaseReminderHelp;
  if (!container || !checkbox || !help) return;
  checkbox.checked = !!(ctx.pb.authStore.model && ctx.pb.authStore.model.purchase_reminder_default);
  const isPurchaseAction = actionStr === 'purchase';
  const isStaff = !!(ctx.pb.authStore.isValid && ctx.pb.authStore.model && ctx.pb.authStore.model.collectionName === 'staff_users');
  container.classList.toggle('hidden', !(isPurchaseAction && isStaff));
  if (!(isPurchaseAction && isStaff)) {
    checkbox.disabled = true;
    help.innerHTML = '';
    return;
  }
  const email = staffProfileEmail(ctx.pb);
  checkbox.disabled = !email;
  help.innerHTML = email
    ? `Send purchase details to ${escapeAttr(email)}.`
    : 'Add an email address to your <a href="#" class="js-open-profile-dialog">staff profile</a> to email yourself purchase reminders.';
}
