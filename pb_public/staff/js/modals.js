import { pb, formatMap, availableFormats, currentRejectionTemplates, currentStatus, currentSuggestions, allSuggestions, verifiedBibId, publicationOptions, setVerifiedBibId, workflowSettings } from './state.js';
import { leapBibUrl, openProfileDialog } from './api.js';
import { showToast, showAlert, showConfirm } from './dialogs.js';
import { loadTab, formatDateTime, renderWorkflowTags, escapeAttr } from './grid.js';
import { setSelectValue, dateOnly, lookupEditBibById, applySelectedPolarisResultToEditForm } from './settings-ui.js';
import { rememberRecentSuggestion, renderRecentSuggestionsSwitcher } from './recent-suggestions.js';

function actionErrorMessage(status, data, raw) {
  if (data && data.code === 'duplicate_open_request') {
    return duplicateOpenRequestMessage(data);
  }
  const detail = (data && data.message) || String(raw || '').trim();
  return detail ? `Error updating suggestion (${status}): ${detail}` : `Error updating suggestion (${status})`;
}

function duplicateOpenRequestMessage(data) {
  const duplicate = data && data.duplicate ? data.duplicate : {};
  const message = String((data && data.message) || 'This patron already has an open request for this BIB ID.').trim();
  const title = String(duplicate.title || '').trim();
  const status = workflowStatusLabel(duplicate.status || '');
  const lines = [message];

  if (title) {
    lines.push(`Existing request: ${title}${status ? ` - ${status}.` : '.'}`);
  } else if (status) {
    lines.push(`Existing request status: ${status}.`);
  }

  lines.push('This request was flagged. Choose another BIB, or close this request as duplicate if it should not continue.');
  return lines.join('\n\n');
}

export function openEdit(id, nextStatus, dialogTitle, actionStr, buttonLabel) {
  const row = currentSuggestions.find(r => r.id === id) || allSuggestions.find(r => r.id === id);
  if (!row) return;
  const isAdditionalCopy = row.type === 'additional_copy';

  rememberRecentSuggestion(row);
  renderRecentSuggestionsSwitcher();

  document.getElementById('editModalLabel').textContent = dialogTitle;
  document.getElementById('edit-id').value = row.id;
  document.getElementById('edit-next-status').value = nextStatus;
  document.getElementById('edit-action').value = actionStr;
  setBibIdRequirement(nextStatus);

  const submitBtn = document.getElementById('edit-submit-btn');
  if (submitBtn) {
    submitBtn.textContent = buttonLabel || 'Save';
  }

  document.getElementById('edit-title').value = row.title || '';
  document.getElementById('edit-author').value = row.author || '';
  document.getElementById('edit-identifier').value = row.identifier || '';
  document.getElementById('edit-bibid').value = row.bibid || '';

  const editFormat = document.getElementById('edit-format');
  const fmt = row.format || 'book';
  if (fmt && !availableFormats.includes(fmt)) {
    if (!Array.from(editFormat.options).some(o => o.value === fmt)) {
      const opt = document.createElement('option');
      opt.value = fmt;
      opt.textContent = formatMap[fmt] || fmt;
      editFormat.appendChild(opt);
    }
  }
  editFormat.value = fmt;
  const publicationValue = normalizedAdditionalCopyPublication(row.publication, isAdditionalCopy);
  setSelectValue(document.getElementById('edit-publication'), publicationValue || publicationOptions[0]);
  document.getElementById('edit-exact-publication-date').value = dateOnly(row.exactPublicationDate);
  document.getElementById('edit-autohold').checked = !!row.autohold;

  const autoholdContainer = document.getElementById('edit-autohold')?.closest('.custom-control');
  if (autoholdContainer) {
    autoholdContainer.classList.toggle('hidden', isAdditionalCopy);
  }

  const bibHint = document.getElementById('edit-bibid-hint');
  if (bibHint) {
    if (isAdditionalCopy) {
      bibHint.textContent = 'Required for all additional-copy tasks. Use Lookup to verify.';
    } else {
      bibHint.textContent = 'Required for Pending hold. Use Lookup to verify.';
    }
  }

  renderEditPatronContext(row);
  renderEditWorkflowTags(row.workflowTags, row);
  renderEditClaimState(row);
  renderEditLeapBibLink(row.bibid);
  renderExternalSearchButton(row.title, row.identifier);
  renderPurchaseReminderOption(actionStr);
  renderEditMetadata(row);

  document.getElementById('edit-notes').value = getExistingHistory(row);
  renderPendingAuditPreview(row, nextStatus, actionStr);

  document.getElementById('bib-info-display').classList.add('hidden');
  document.getElementById('bib-info-text').textContent = '';
  setVerifiedBibId(row.bibid || '');

  renderRejectionTemplateSelector(actionStr);

  document.getElementById('editModal').showModal();
  document.getElementById('close-modal-btn').focus();
}

function looksLikeCatalogPublicationDate(value) {
  value = String(value || '').trim();
  return /^\d{4}$/.test(value) || /^\d{4}[-/]\d{1,2}([-/]\d{1,2})?$/.test(value);
}

function normalizedAdditionalCopyPublication(value, isAdditionalCopy) {
  value = String(value || '').trim();
  if (isAdditionalCopy && value && !publicationOptions.includes(value) && looksLikeCatalogPublicationDate(value)) {
    return publicationOptions[0] || '';
  }
  return value;
}

export function renderEditClaimState(row) {
  const container = document.getElementById('edit-claim-state');
  if (!container) return;
  container.replaceChildren();
  const label = document.createElement('span');
  label.className = 'edit-status-group-label';
  label.textContent = 'Claim:';
  container.appendChild(label);
  const valueWrap = document.createElement('span');
  valueWrap.className = 'edit-status-group-value';
  const currentStaffId = String((pb.authStore.model && pb.authStore.model.id) || '').trim();
  const claimantId = String(row.claimedByStaffUserId || '').trim();
  const badge = document.createElement('span');
  badge.className = 'claim-badge';
  if (!claimantId) {
    badge.classList.add('claim-badge--unclaimed');
    badge.textContent = 'Unclaimed';
  } else if (currentStaffId && claimantId === currentStaffId) {
    badge.classList.add('claim-badge--mine');
    badge.textContent = 'Mine';
  } else {
    const name = row.claimedByDisplayName || 'Staff';
    badge.classList.add('claim-badge--claimed');
    badge.textContent = `Claimed by ${name}`;
  }
  valueWrap.appendChild(badge);
  if (claimantId) {
    const source = document.createElement('span');
    source.className = 'text-muted';
    source.textContent = row.claimType === 'automatic_format_rule' ? '(auto)' : '(manual)';
    valueWrap.appendChild(source);
  }
  container.appendChild(valueWrap);
}

export function getExistingHistory(row) {
  return (row.notes || '').trim();
}

export function getDraftCommentValue() {
  return document.getElementById('edit-notes').value;
}

export function buildPendingAuditPreview(row, nextStatus, actionStr) {
  const username = (pb.authStore.model && pb.authStore.model.username) ? pb.authStore.model.username : 'staff';
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

  // Detect format change
  const editFormat = document.getElementById('edit-format');
  if (editFormat && editFormat.value && editFormat.value !== (row.format || 'book')) {
    const formatName = formatMap[editFormat.value] || editFormat.value;
    const formatChangeText = `Format will be updated to ${formatName}.`;
    preview = preview ? `${preview} ${formatChangeText}` : formatChangeText;
  }

  return preview;
}


export function renderPendingAuditPreview(row, nextStatus, actionStr) {
  const container = document.getElementById('edit-pending-audit-preview');
  const text = document.getElementById('edit-pending-audit-preview-text');
  if (!container || !text) return;

  const preview = buildPendingAuditPreview(row, nextStatus, actionStr);
  text.textContent = preview;
  container.classList.toggle('hidden', !preview);
}

export function workflowStatusLabel(status) {
  const labels = {
    suggestion: 'Suggestions',
    outstanding_purchase: 'Pending purchase',
    pending_hold: 'Pending hold',
    hold_placed: 'Hold placed',
    closed: 'Closed'
  };
  return labels[status] || status || 'current status';
}

export function renderRejectionTemplateSelector(actionStr) {
  const rejectionContainer = document.getElementById('edit-rejection-template-container');
  const select = document.getElementById('edit-rejection-template');
  const availability = document.getElementById('edit-rejection-template-availability');
  if (!rejectionContainer || !select) return;

  if (actionStr !== 'reject') {
    rejectionContainer.classList.add('hidden');
    select.value = '';
    if (availability) {
      availability.textContent = '';
      availability.classList.add('hidden');
    }
    return;
  }

  rejectionContainer.classList.remove('hidden');
  select.innerHTML = '<option value="">Default rejection template (recommended)</option>';
  select.value = '';

  const sortedTemplates = [...currentRejectionTemplates].sort((a, b) => {
    const nameA = (a.name || a.subject || '').toLowerCase();
    const nameB = (b.name || b.subject || '').toLowerCase();
    return nameA.localeCompare(nameB);
  });

  sortedTemplates.forEach(t => {
    const opt = document.createElement('option');
    opt.value = t.id;
    opt.textContent = t.name || t.subject || 'Rejection template';
    select.appendChild(opt);
  });

  renderRejectionTemplateAvailability(sortedTemplates.length);
}

function renderRejectionTemplateAvailability(otherTemplateCount) {
  const availability = document.getElementById('edit-rejection-template-availability');
  if (!availability) return;

  if (otherTemplateCount < 1) {
    availability.textContent = '';
    availability.classList.add('hidden');
    return;
  }

  availability.textContent = otherTemplateCount === 1
    ? '1 other template available'
    : `${otherTemplateCount} other templates available`;
  availability.classList.remove('hidden');
}

export function staffProfileEmail() {
  const model = pb.authStore.model || {};
  return String(model.weekly_action_summary_email || '').trim();
}

export function renderPurchaseReminderOption(actionStr) {
  const container = document.getElementById('edit-purchase-reminder-container');
  const checkbox = document.getElementById('edit-email-purchase-reminder');
  const help = document.getElementById('edit-purchase-reminder-help');
  if (!container || !checkbox || !help) return;
  checkbox.checked = !!(pb.authStore.model && pb.authStore.model.purchase_reminder_default);
  const isPurchaseAction = actionStr === 'purchase';
  const isStaff = !!(pb.authStore.isValid && pb.authStore.model && pb.authStore.model.collectionName === 'staff_users');
  container.classList.toggle('hidden', !(isPurchaseAction && isStaff));
  if (!(isPurchaseAction && isStaff)) {
    checkbox.disabled = true;
    help.innerHTML = '';
    return;
  }
  const email = staffProfileEmail();
  checkbox.disabled = !email;
  help.innerHTML = email
    ? `Send purchase details to ${escapeAttr(email)}.`
    : 'Add an email address to your <a href="#" class="js-open-profile-dialog">staff profile</a> to email yourself purchase reminders.';
}

document.addEventListener('click', (e) => {
  if (e.target.closest('.js-open-profile-dialog')) {
    e.preventDefault();
    openProfileDialog();
  }
});

export function renderPatronContext(row, options = {}) {
  const {
    containerSelector,
    blockId,
    expanded = false,
    anchorSelector,
    insertAfter = true
  } = options;

  const container = document.querySelector(containerSelector);
  if (!container) return;

  let block = document.getElementById(blockId);
  if (!block) {
    block = document.createElement('div');
    block.id = blockId;
    block.className = 'alert alert-light border py-2 px-3 mb-2 small';
    const anchor = container.querySelector(anchorSelector);
    if (anchor && anchor.parentNode === container) {
      if (insertAfter) {
        container.insertBefore(block, anchor.nextSibling);
      } else {
        container.insertBefore(block, anchor);
      }
    } else {
      container.insertBefore(block, container.firstChild);
    }
  }

  const patronName = row.patronName || `${row.nameFirst || ''} ${row.nameLast || ''}`.trim() || '—';
  const patronEmail = row.patronEmail || row.email || '—';
  const libraryOrgName = row.libraryOrgName || row.libraryOrgId || '—';
  const preferredPickupBranchName = row.preferredPickupBranchName || '—';
  const barcode = row.barcode || '—';

  block.replaceChildren();

  // Summary toggle button
  const summaryBtn = document.createElement('button');
  summaryBtn.type = 'button';
  summaryBtn.className = 'edit-patron-summary';
  summaryBtn.setAttribute('aria-expanded', expanded ? 'true' : 'false');

  const chevron = document.createElement('i');
  chevron.className = 'fa fa-chevron-right edit-patron-summary-chevron';
  chevron.setAttribute('aria-hidden', 'true');
  summaryBtn.appendChild(chevron);

  const summaryText = document.createElement('span');
  const summaryParts = [patronName];
  if (libraryOrgName !== '—') summaryParts.push(libraryOrgName);
  summaryText.textContent = summaryParts.join(' · ');
  summaryBtn.appendChild(summaryText);

  const hint = document.createElement('span');
  hint.className = 'edit-patron-summary-hint';
  hint.textContent = expanded ? 'Hide details' : 'Show details';
  summaryBtn.appendChild(hint);

  block.appendChild(summaryBtn);

  // Detail rows
  const detailRows = document.createElement('div');
  detailRows.className = 'edit-patron-detail-rows';

  const fields = [
    { label: 'Patron', value: patronName },
    { label: 'Email', value: patronEmail },
    { label: 'Barcode', value: barcode },
    { label: 'Library', value: libraryOrgName },
    { label: 'Preferred pickup branch', value: preferredPickupBranchName }
  ];

  fields.forEach(f => {
    const div = document.createElement('div');
    const strong = document.createElement('strong');
    strong.textContent = f.label + ':';
    div.appendChild(strong);
    div.append(' ' + f.value);
    detailRows.appendChild(div);
  });

  block.appendChild(detailRows);

  if (expanded) {
    block.classList.add('edit-patron-context-expanded');
  } else {
    block.classList.remove('edit-patron-context-expanded');
  }

  // Toggle behavior
  summaryBtn.addEventListener('click', () => {
    const isExpanded = block.classList.toggle('edit-patron-context-expanded');
    summaryBtn.setAttribute('aria-expanded', String(isExpanded));
    hint.textContent = isExpanded ? 'Hide details' : 'Show details';
  });
}

export function renderEditPatronContext(row) {
  const isAdditionalCopy = row.type === 'additional_copy';
  const blockId = 'edit-patron-context';
  const block = document.getElementById(blockId);

  if (isAdditionalCopy) {
    if (block) {
      block.classList.add('hidden');
    }
    return;
  }

  if (block) {
    block.classList.remove('hidden');
  }

  renderPatronContext(row, {
    containerSelector: '#editModal .asap-dialog-edit-body',
    blockId: blockId,
    expanded: false,
    anchorSelector: '#edit-rejection-template-container'
  });
}

export function renderEditWorkflowTags(tags, row) {
  const container = document.getElementById('edit-workflow-tags');
  if (!container) return;
  container.replaceChildren();
  const label = document.createElement('span');
  label.className = 'edit-status-group-label';
  label.textContent = 'Flags:';
  container.appendChild(label);
  const valueWrap = document.createElement('span');
  valueWrap.className = 'edit-status-group-value';
  // renderWorkflowTags returns verbose "No workflow flags" for empty state;
  // use a compact "None" label for the inline status row
  const tagHtml = renderWorkflowTags(tags, row);
  if (tagHtml.includes('No workflow flags')) {
    const none = document.createElement('span');
    none.className = 'text-muted';
    none.textContent = 'None';
    valueWrap.appendChild(none);
  } else {
    const temp = document.createElement('div');
    // Static developer-authored markup from renderWorkflowTags
    temp.innerHTML = tagHtml;
    while (temp.firstChild) {
      valueWrap.appendChild(temp.firstChild);
    }
  }
  container.appendChild(valueWrap);
}

export function renderEditMetadata(row) {
  const editBody = document.querySelector('#editModal .asap-dialog-edit-body');
  if (!editBody) return;

  let block = document.getElementById('edit-metadata');
  if (!block) {
    block = document.createElement('div');
    block.id = 'edit-metadata';
    block.className = 'mt-3 pt-2 border-top small text-muted';
    editBody.appendChild(block);
  }

  const lastChecked = row.lastPromoterCheck ? formatDateTime(row.lastPromoterCheck) : null;
  if (lastChecked) {
    block.innerHTML = `Auto-promoter last checked: ${escapeAttr(lastChecked)}`;
    block.classList.remove('hidden');
  } else {
    block.innerHTML = '';
    block.classList.add('hidden');
  }
}

export function renderEditLeapBibLink(bibId) {
  const container = document.getElementById('edit-leap-bib-link-container');
  if (!container) return;
  const url = leapBibUrl(bibId);
  if (!url || !/^https?:\/\//i.test(url)) {
    container.classList.add('hidden');
    container.innerHTML = '';
    return;
  }
  container.classList.remove('hidden');
  if (/^https?:\/\//i.test(url)) {
    container.innerHTML = `<a class="btn btn-sm btn-outline-primary" href="${escapeAttr(url)}" target="_blank" rel="noopener noreferrer">Open Bib in Leap</a>`;
  }
}

export function renderExternalSearchButton(title, identifier) {
  const container = document.getElementById('edit-external-search-container');
  if (!container) return;

  const encodedTitle = encodeURIComponent(title || '');
  const encodedId = encodeURIComponent(identifier || '');
  const buttonClasses = ['btn-warning', 'btn-success', 'btn-primary', 'btn-info'];

  const providers = [];
  for (let i = 1; i <= 4; i++) {
    providers.push({
      enabled: workflowSettings[`externalSearch${i}Enabled`],
      label: workflowSettings[`externalSearch${i}Label`],
      template: workflowSettings[`externalSearch${i}UrlTemplate`]
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

function polarisSearchModeLabel(mode) {
  if (mode === 'author') return 'author';
  if (mode === 'title_author') return 'title and author';
  if (mode === 'identifier') return 'identifier';
  return 'title';
}

export function polarisSearchButtonLabel(mode) {
  if (mode === 'author') return 'Search Polaris using this author text';
  if (mode === 'identifier') return 'Search Polaris using this identifier';
  return 'Search Polaris using this title text';
}

export function renderPolarisSearchButtonMarkup(mode, attrs = {}) {
  const label = attrs.label || polarisSearchButtonLabel(mode);
  const attrText = Object.keys(attrs).filter(key => key !== 'label' && key !== 'class' && attrs[key] !== undefined && attrs[key] !== null && attrs[key] !== false).map(key => {
    if (attrs[key] === true) return escapeAttr(key);
    return `${escapeAttr(key)}="${escapeAttr(attrs[key])}"`;
  }).join(' ');
  return `
    <button type="button"
      class="polaris-row-search${attrs.class ? ' ' + escapeAttr(attrs.class) : ''}"
      ${attrText}
      title="${escapeAttr(label)}"
      aria-label="${escapeAttr(label)}">
      <i class="fa fa-search" aria-hidden="true"></i>
    </button>
  `;
}

function hasOwn(row, key) {
  return Object.prototype.hasOwnProperty.call(row || {}, key);
}

function looksLikeCatalogWrappedValue(prefix) {
  prefix = String(prefix || '').trim();
  if (!prefix) return false;
  return prefix.includes(' / ') ||
    /[.;:]$/.test(prefix) ||
    /,\s*\d{4}/.test(prefix) ||
    /\b(author|editor|illustrator|director|producer)\.?$/i.test(prefix);
}

function basicPolarisSearchText(value) {
  return String(value || '')
    .replace(/\([^()]*\)/g, ' ')
    .replace(/[\u201c\u201d]/g, '"')
    .replace(/[\u2018\u2019]/g, "'")
    .replace(/[\/:;,.]+/g, ' ')
    .replace(/\s+-\s+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
}

function fallbackPolarisSearchValue(value) {
  const text = String(value || '').replace(/\s+/g, ' ').trim();
  const wrapped = text.match(/^(.*)\(([^()]*)\)\s*$/);
  if (wrapped) {
    const prefix = String(wrapped[1] || '').trim();
    const original = String(wrapped[2] || '').trim();
    if (looksLikeCatalogWrappedValue(prefix)) {
      return basicPolarisSearchText(original);
    }
  }
  return basicPolarisSearchText(text);
}

export function polarisSearchValueForRow(row, mode) {
  if (mode === 'author') {
    return hasOwn(row, 'polarisSearchAuthor')
      ? String(row.polarisSearchAuthor || '').trim()
      : fallbackPolarisSearchValue(row.author);
  }
  if (mode === 'identifier') {
    return String(row.identifier || '').trim();
  }
  return hasOwn(row, 'polarisSearchTitle')
    ? String(row.polarisSearchTitle || '').trim()
    : fallbackPolarisSearchValue(row.title);
}

function polarisSearchQueryForRow(row, mode) {
  if (mode === 'title_author') {
    return [
      polarisSearchValueForRow(row, 'title'),
      polarisSearchValueForRow(row, 'author')
    ].filter(Boolean).join(' ').trim();
  }
  return polarisSearchValueForRow(row, mode);
}

function polarisResultMeta(result) {
  return [
    result.author ? 'Author: ' + result.author : '',
    result.publication ? 'Publication: ' + result.publication : '',
    result.format ? 'Format: ' + result.format : '',
    result.identifier ? 'Identifier: ' + result.identifier : '',
    result.bibId ? 'BIB: ' + result.bibId : ''
  ].filter(Boolean).join(' | ');
}

function polarisSearchElements() {
  return {
    dialog: document.getElementById('polarisSearchDialog'),
    title: document.getElementById('polaris-search-title'),
    summary: document.getElementById('polaris-search-summary'),
    modeSelect: document.getElementById('polaris-search-mode'),
    status: document.getElementById('polaris-search-status'),
    results: document.getElementById('polaris-search-results'),
    searchInput: document.getElementById('polaris-search-input'),
    searchInputLabel: document.getElementById('polaris-search-input-label'),
    authorGroup: document.getElementById('polaris-search-author-group'),
    authorInput: document.getElementById('polaris-search-author'),
    rerunBtn: document.getElementById('polaris-search-rerun-btn'),
    addAuthorBtn: document.getElementById('polaris-search-add-author-btn')
  };
}

async function fetchPolarisSearch(row, mode, query, options) {
  const payload = {
    mode,
    query: query,
    title: options?.title || '',
    author: options?.author || '',
    requestId: row.id || ''
  };
  if (options?.bibId) {
    payload.bibId = options.bibId;
  }
  let res;
  try {
    res = await fetch('/api/asap/staff/bib-lookup', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify(payload)
    });
  } catch (err) {
    err.networkError = true;
    throw err;
  }
  const data = await res.json();
  if (!res.ok) throw new Error(data.message || 'Polaris search failed');
  return data;
}

export function confirmAdditionalCopyAction(result, options = {}) {
  return new Promise(resolve => {
    const previousFocus = document.activeElement;
    const dialog = document.createElement('dialog');
    dialog.className = 'asap-dialog asap-dialog-small';

    const body = document.createElement('div');
    body.className = 'asap-dialog-small-body';

    const title = document.createElement('h2');
    title.className = 'h5 mb-3';
    title.textContent = 'Buy another copy + Queue Now';

    const message = document.createElement('p');
    message.className = 'dialog-message';
    const bibId = result && result.bibId ? String(result.bibId) : '';
    if (options.message) {
      message.textContent = options.message;
    } else {
      message.textContent = bibId
        ? `Create an additional-copy task for BIB ${bibId} and queue the patron hold on this same BIB?`
        : 'Create an additional-copy task and queue the patron hold on this same BIB?';
    }

    const checkboxGroup = document.createElement('div');
    checkboxGroup.className = 'custom-control custom-checkbox mb-4';

    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.id = 'confirm-additional-copy-reminder';
    checkbox.className = 'custom-control-input';
    checkbox.checked = Object.prototype.hasOwnProperty.call(options, 'emailPurchaseReminderDefault')
      ? !!options.emailPurchaseReminderDefault
      : !!(pb.authStore.model && pb.authStore.model.additional_copy_reminder_default);

    const label = document.createElement('label');
    label.className = 'custom-control-label font-weight-bold';
    label.setAttribute('for', 'confirm-additional-copy-reminder');
    label.textContent = 'Email me a purchase reminder';

    checkboxGroup.append(checkbox, label);

    const actions = document.createElement('div');
    actions.className = 'asap-dialog-actions';

    const cancelBtn = document.createElement('button');
    cancelBtn.type = 'button';
    cancelBtn.className = 'btn btn-sm btn-secondary';
    cancelBtn.textContent = 'Cancel';

    const okBtn = document.createElement('button');
    okBtn.type = 'button';
    okBtn.className = 'btn btn-sm btn-success';
    okBtn.textContent = 'Confirm';

    actions.append(cancelBtn, okBtn);

    body.append(title, message, checkboxGroup, actions);
    dialog.append(body);
    document.body.appendChild(dialog);

    let settled = false;
    function cleanup(resultValue) {
      if (settled) return;
      settled = true;
      if (dialog.open) dialog.close();
      dialog.remove();
      if (previousFocus && typeof previousFocus.focus === 'function') previousFocus.focus();
      resolve(resultValue);
    }
    cancelBtn.addEventListener('click', () => cleanup({ confirmed: false, emailPurchaseReminder: false }));
    okBtn.addEventListener('click', () => cleanup({ confirmed: true, emailPurchaseReminder: checkbox.checked }));
    dialog.addEventListener('cancel', event => {
      event.preventDefault();
      cleanup({ confirmed: false, emailPurchaseReminder: false });
    });
    dialog.showModal();
    cancelBtn.focus();
  });
}

let holdingsLookupUnavailable = false;

function renderPolarisSearchResults(row, mode, data, options = {}) {
  const els = polarisSearchElements();
  if (data.status === 'error') {
    els.status.className = 'alert alert-danger py-2 px-3 small';
    els.status.style.wordBreak = 'break-all';
    els.status.textContent = 'Polaris search failed: ' + (data.error || data.message || 'Polaris returned an error.');
    els.results.innerHTML = '';
    return;
  }
  const results = Array.isArray(data.results) ? data.results.slice(0, 10) : [];
  els.status.className = 'alert alert-light border py-2 px-3 small';
  els.status.textContent = results.length
    ? `${results.length} result${results.length === 1 ? '' : 's'} shown${data.totalMatches > results.length ? ' of ' + data.totalMatches : ''}.`
    : 'No Polaris matches found.';

  els.results.replaceChildren();
  results.forEach((result, index) => {
    const title = result.title || '(No title returned)';
    const meta = polarisResultMeta(result);
    
    const div = document.createElement('div');
    div.className = 'polaris-search-result';
    
    const titleDiv = document.createElement('div');
    titleDiv.className = 'polaris-search-result-title';

    if (result.formatIconUrl) {
      const icon = document.createElement('img');
      icon.className = 'asap-format-icon polaris-search-result-title-icon';
      icon.src = result.formatIconUrl;
      icon.alt = '';
      icon.setAttribute('aria-hidden', 'true');
      icon.loading = 'lazy';
      icon.onerror = () => icon.remove();
      titleDiv.appendChild(icon);
    }

    const titleText = document.createElement('span');
    titleText.className = 'polaris-search-result-title-text';
    titleText.textContent = title;
    titleDiv.appendChild(titleText);

    div.appendChild(titleDiv);
    if (meta) {
      const metaDiv = document.createElement('div');
      metaDiv.className = 'polaris-search-result-meta';
      
      const metaParts = [
        result.author ? 'Author: ' + result.author : '',
        result.publication ? 'Publication: ' + result.publication : '',
        result.format ? 'Format: ' + result.format : '',
        result.identifier ? 'Identifier: ' + result.identifier : '',
        result.bibid || result.bibId ? 'BIB: ' + (result.bibid || result.bibId) : ''
      ];

      metaDiv.textContent = metaParts.filter(p => !!p).join(' | ');
      div.appendChild(metaDiv);
    }

    // Holdings/Ownership Badge Container
    const holdingsDiv = document.createElement('div');
    holdingsDiv.className = 'polaris-holdings-container';
    const holdingsLoading = document.createElement('span');
    holdingsLoading.className = 'text-muted small italic';
    holdingsLoading.textContent = 'Checking ownership...';
    holdingsDiv.appendChild(holdingsLoading);
    div.appendChild(holdingsDiv);
    
    const actionsDiv = document.createElement('div');
    actionsDiv.className = 'polaris-search-result-actions';

    // Helper for building action payload
    const buildPayload = (nextStatus, action) => {
      const isAdditionalCopyAction = action === 'additionalCopy';
      const workflowPublication = isAdditionalCopyAction
        ? (row.publication || publicationOptions[0] || '')
        : (result.publication || row.publication);
      return {
        action: action,
        status: nextStatus,
        title: result.title || row.title,
        author: result.author || row.author,
        identifier: result.identifier || row.identifier,
        bibid: result.bibId,
        format: result.format || row.format,
        publication: workflowPublication,
        exactPublicationDate: row.exactPublicationDate || '',
        selectedPolarisBibId: result.bibId,
        selectedPolarisTitle: result.title,
        selectedPolarisAuthor: result.author,
        selectedPolarisIdentifier: result.identifier,
        selectedPolarisPublication: isAdditionalCopyAction ? '' : result.publication,
        selectedPolarisFormat: result.format,
        notes: row.notes || '', 
        autohold: row.autohold !== false,
        editedBy: pb.authStore.model.username
      };
    };

    const launchedFromEditForm = options.source === 'edit';
    let holdBtn = null;
    let additionalCopyBtn = null;

    if (launchedFromEditForm) {
      const applyBtn = document.createElement('button');
      applyBtn.type = 'button';
      applyBtn.className = 'btn btn-sm btn-primary';
      applyBtn.textContent = (options.source === 'new' || document.getElementById('edit-next-status')?.value !== 'pending_hold')
        ? 'Apply to Form'
        : 'Use BIB in Queue Form';
      applyBtn.addEventListener('click', () => {
        applySelectedPolarisResultToEditForm(result, options.source || 'edit');
        els.dialog.close();
        showToast('Polaris details applied to form.', 'success');
      });

      actionsDiv.appendChild(applyBtn);
    } else {
      holdBtn = document.createElement('button');
      holdBtn.type = 'button';
      holdBtn.className = 'btn btn-sm btn-success';
      holdBtn.textContent = 'Use BIB & Queue Now';
      holdBtn.disabled = true; // Disabled until holdings check confirms holdable
      holdBtn.addEventListener('click', async () => {
        const payload = buildPayload('pending_hold', 'catalogFound');
        await performImmediateStaffAction(row.id, payload);
      });
      actionsDiv.appendChild(holdBtn);
    }

    // Always create additionalCopyBtn if we have a BIB ID, but hide it by default
    additionalCopyBtn = document.createElement('button');
    additionalCopyBtn.type = 'button';
    additionalCopyBtn.className = 'btn btn-sm btn-outline-success hidden';
    additionalCopyBtn.textContent = 'Buy another copy + Queue Now';
    additionalCopyBtn.disabled = true;
    additionalCopyBtn.addEventListener('click', async () => {
      const confirmResult = await confirmAdditionalCopyAction(result);
      if (!confirmResult.confirmed) return;
      const payload = buildPayload('pending_hold', 'additionalCopy');
      payload.emailPurchaseReminder = confirmResult.emailPurchaseReminder;
      payload.autohold = true;
      await performImmediateStaffAction(row.id, payload);
    });
    actionsDiv.appendChild(additionalCopyBtn);


    // Background Holdings Check
    if (result.bibId && !holdingsLookupUnavailable) {
      fetchPolarisSearch(row, 'identifier', '', { bibId: result.bibId })
        .then(details => {
          holdingsDiv.replaceChildren();
          const summary = details.holdingsSummary || {};
          
          if (summary.myLibraryCount > 0) {
            const myBadge = document.createElement('span');
            myBadge.className = 'polaris-badge polaris-badge-success';
            myBadge.textContent = `Owned by you (${summary.myLibraryCount})`;
            holdingsDiv.appendChild(myBadge);
          }

          if (summary.otherLibraryCount > 0) {
            const otherBadge = document.createElement('span');
            otherBadge.className = 'polaris-badge polaris-badge-info';
            otherBadge.textContent = `Owned by consortium (${summary.consortiumCount})`;
            holdingsDiv.appendChild(otherBadge);
          }

          if (summary.isHoldable) {
            if (holdBtn) holdBtn.disabled = false;
            if (summary.myLibraryCount > 0) {
              if (holdBtn) holdBtn.classList.add('font-weight-bold');
            }
          } else if (result.bibId) {
            const warn = document.createElement('span');
            warn.className = 'polaris-warning';
            warn.textContent = 'Not Holdable';
            holdingsDiv.appendChild(warn);
            if (holdBtn) {
              holdBtn.className = 'btn btn-sm btn-outline-warning';
              holdBtn.disabled = false;
            }
          }

          if (summary.consortiumCount === 0) {
            const none = document.createElement('span');
            none.className = 'text-muted small';
            none.textContent = 'No item records found.';
            holdingsDiv.appendChild(none);
            if (holdBtn) {
              holdBtn.className = 'btn btn-sm btn-outline-warning';
              holdBtn.disabled = false;
            }
          }

          if (additionalCopyBtn && result.bibId && Number(summary.consortiumCount || 0) > 0) {
            additionalCopyBtn.classList.remove('hidden');
            additionalCopyBtn.disabled = false;
          }
        })
        .catch(err => {
          if (err && err.networkError) {
            holdingsLookupUnavailable = true;
          }
          holdingsDiv.replaceChildren();
          const error = document.createElement('span');
          error.className = 'text-danger small';
          error.textContent = 'Holdings check failed.';
          holdingsDiv.appendChild(error);
          if (holdBtn) {
            holdBtn.className = 'btn btn-sm btn-outline-warning';
            holdBtn.disabled = false;
          }
        });
    } else if (result.bibId) {
      holdingsDiv.replaceChildren();
      const skipped = document.createElement('span');
      skipped.className = 'text-muted small';
      skipped.textContent = 'Holdings check unavailable.';
      holdingsDiv.appendChild(skipped);
      if (holdBtn) {
        holdBtn.className = 'btn btn-sm btn-outline-warning';
        holdBtn.disabled = false;
      }
    }

    div.appendChild(actionsDiv);
    els.results.appendChild(div);
  });
}


export async function openPolarisSearch(row, mode, options = {}) {
  if (!row) return;
  const els = polarisSearchElements();
  if (!els.dialog) return;

  mode = String(mode || 'title').trim().toLowerCase();
  
  const originalTitle = options.title !== undefined ? String(options.title || '').trim() : polarisSearchValueForRow(row, 'title');
  const originalAuthor = options.author !== undefined ? String(options.author || '').trim() : polarisSearchValueForRow(row, 'author');
  const originalIdentifier = options.identifier !== undefined ? String(options.identifier || '').trim() : polarisSearchValueForRow(row, 'identifier');
  
  // Set initial mode and inputs
  els.modeSelect.value = mode;
  els.searchInput.value = (mode === 'identifier') ? originalIdentifier : (mode === 'author' ? originalAuthor : originalTitle);
  els.authorInput.value = originalAuthor;
  
  const updateUiForMode = () => {
    const currentMode = els.modeSelect.value;
    const isIdentifier = currentMode === 'identifier';
    const isTitleAuthor = currentMode === 'title_author';
    const isAuthor = currentMode === 'author';
    
    els.searchInputLabel.textContent = isIdentifier ? 'Identifier (ISBN/UPC)' : (isAuthor ? 'Author Keywords' : 'Title Keywords');
    els.authorGroup.classList.toggle('hidden', !isTitleAuthor);
    els.addAuthorBtn.classList.toggle('hidden', isTitleAuthor || isIdentifier || isAuthor || !originalAuthor);
  };

  updateUiForMode();

  const runSearch = async () => {
    const currentMode = els.modeSelect.value;
    const query = els.searchInput.value.trim();
    const author = els.authorInput.value.trim();

    if (!query && currentMode !== 'author' && currentMode !== 'title_author') {
      showToast('Please enter search terms.', 'warning');
      return;
    }

    els.status.className = 'alert alert-light border py-2 px-3 small';
    els.status.textContent = 'Searching Polaris...';
    els.results.innerHTML = '';

    try {
      const data = await fetchPolarisSearch(row, currentMode, query, { title: query, author: author });
      renderPolarisSearchResults(row, currentMode, data, options);
    } catch (err) {
      els.status.className = 'alert alert-danger py-2 px-3 small';
      els.status.textContent = 'Error: ' + err.message;
    }
  };

  // Event Listeners
  els.rerunBtn.onclick = (e) => {
    e.preventDefault();
    runSearch();
  };

  els.modeSelect.onchange = () => {
    updateUiForMode();
  };

  els.addAuthorBtn.onclick = (e) => {
    e.preventDefault();
    els.modeSelect.value = 'title_author';
    updateUiForMode();
    els.authorInput.focus();
  };

  const onEnter = (e) => { if (e.key === 'Enter') runSearch(); };
  els.searchInput.onkeypress = onEnter;
  els.authorInput.onkeypress = onEnter;

  const returnDialog = options.returnDialog || null;
  const returnFocus = options.returnFocus || null;
  let shouldReturnDialog = !!(returnDialog && returnDialog.open);
  if (shouldReturnDialog) {
    returnDialog.close();
    const reopenReturnDialog = () => {
      if (shouldReturnDialog && returnDialog && !returnDialog.open) {
        returnDialog.showModal();
        if (returnFocus && typeof returnFocus.focus === 'function') {
          returnFocus.focus();
        }
      }
    };
    els.dialog.addEventListener('close', reopenReturnDialog, { once: true });
  }

  if (!els.dialog.open) {
    els.dialog.showModal();
  }

  await runSearch();
  if (els.dialog.open) {
    els.searchInput.focus();
  }
}

function closePolarisSearchDialog() {
  const dialog = document.getElementById('polarisSearchDialog');
  if (dialog) dialog.close();
}

function currentEditPolarisSearchRow(context = 'edit') {
  if (context === 'new') {
    const title = document.getElementById('new-title')?.value || '';
    const author = document.getElementById('new-author')?.value || '';
    const identifier = document.getElementById('new-identifier')?.value || '';
    return {
      id: '',
      title,
      author,
      identifier,
      polarisSearchTitle: fallbackPolarisSearchValue(title),
      polarisSearchAuthor: fallbackPolarisSearchValue(author)
    };
  }

  const id = document.getElementById('edit-id')?.value || '';
  const existing = currentSuggestions.find(r => r.id === id) || allSuggestions.find(r => r.id === id) || {};
  const title = document.getElementById('edit-title')?.value || '';
  const author = document.getElementById('edit-author')?.value || '';
  const identifier = document.getElementById('edit-identifier')?.value || '';
  return Object.assign({}, existing, {
    id: id || existing.id || '',
    title,
    author,
    identifier,
    polarisSearchTitle: fallbackPolarisSearchValue(title),
    polarisSearchAuthor: fallbackPolarisSearchValue(author)
  });
}

function editPolarisSearchInputForMode(mode, context = 'edit') {
  if (mode === 'author') return document.getElementById(`${context}-author`);
  if (mode === 'identifier') return document.getElementById(`${context}-identifier`);
  return document.getElementById(`${context}-title`);
}

function launchEditPolarisSearch(mode, button, context = 'edit') {
  const input = editPolarisSearchInputForMode(mode, context);
  const row = currentEditPolarisSearchRow(context);
  const query = mode === 'identifier'
    ? String(input?.value || '').trim()
    : polarisSearchValueForRow(row, mode);
  if (!query) {
    showToast('Enter text before searching Polaris.', 'warning');
    if (input) input.focus();
    return;
  }
  openPolarisSearch(row, mode, {
    source: context,
    query,
    title: polarisSearchValueForRow(row, 'title'),
    author: polarisSearchValueForRow(row, 'author'),
    identifier: document.getElementById(`${context}-identifier`)?.value || '',
    returnDialog: document.getElementById(context === 'edit' ? 'editModal' : 'newSuggestionModal'),
    returnFocus: button || input
  });
}


document.getElementById('close-polaris-search-x')?.addEventListener('click', closePolarisSearchDialog);
document.getElementById('close-polaris-search-btn')?.addEventListener('click', closePolarisSearchDialog);
document.getElementById('edit-title-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('title', e.currentTarget, 'edit'));
document.getElementById('edit-author-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('author', e.currentTarget, 'edit'));
document.getElementById('edit-identifier-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('identifier', e.currentTarget, 'edit'));

document.getElementById('new-title-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('title', e.currentTarget, 'new'));
document.getElementById('new-author-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('author', e.currentTarget, 'new'));
document.getElementById('new-identifier-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('identifier', e.currentTarget, 'new'));


document.getElementById('edit-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const id = document.getElementById('edit-id').value;
  const nextStatus = document.getElementById('edit-next-status').value;
  const bibid = document.getElementById('edit-bibid').value.trim();
  const row = currentSuggestions.find(r => r.id === id) || allSuggestions.find(r => r.id === id);
  if (row && row.status === 'outstanding_purchase' && bibid && !row.autohold) {
    const confirmed = await showConfirm('Do Not Auto Queue Hold', 'This request is marked Do Not Auto Queue Hold. Saving this BIB ID will close the request immediately and skip the hold-queueing workflow.');
    if (!confirmed) return;
  }
  const nextFormatValue = document.getElementById('edit-format').value;
  if (row && nextFormatValue && nextFormatValue !== row.format) {
    let warning = 'Changing the format may update the automatic claim assignment for this suggestion.';
    if (row.claimedByStaffUserId && row.claimType === 'automatic_format_rule') {
      warning = 'This suggestion is currently auto-claimed based on its format. Changing the format may reassign it to another staff member.';
    } else if (row.claimedByStaffUserId) {
      warning = 'This suggestion was manually claimed. Changing the format will not change the current claim.';
    }
    const confirmed = await showConfirm('Format change may affect claim', warning);
    if (!confirmed) return;
  }

  if (nextStatus === 'pending_hold') {
    if (!bibid) {
      await showAlert('BIB ID is required before moving this suggestion to Pending hold.');
      document.getElementById('edit-bibid').focus();
      return;
    }
    if (bibid !== verifiedBibId) {
      await showAlert('Please use the "Lookup BIB" button to verify this BIB ID before moving to Pending hold.');
      document.getElementById('btn-bib-lookup').focus();
      return;
    }
  }
  const actionValue = document.getElementById('edit-action').value || undefined;
  const payload = {
    action: actionValue,
    status: nextStatus,
    title: document.getElementById('edit-title').value,
    author: document.getElementById('edit-author').value,
    identifier: document.getElementById('edit-identifier').value,
    bibid: bibid,
    format: nextFormatValue,
    publication: document.getElementById('edit-publication').value,
    exactPublicationDate: document.getElementById('edit-exact-publication-date').value,
    selectedPolarisBibId: document.getElementById('selectedPolarisBibId')?.value || '',
    selectedPolarisTitle: document.getElementById('selectedPolarisTitle')?.value || '',
    selectedPolarisAuthor: document.getElementById('selectedPolarisAuthor')?.value || '',
    selectedPolarisIdentifier: document.getElementById('selectedPolarisIdentifier')?.value || '',
    selectedPolarisPublication: document.getElementById('selectedPolarisPublication')?.value || '',
    selectedPolarisFormat: document.getElementById('selectedPolarisFormat')?.value || '',
    notes: getDraftCommentValue(),
    autohold: document.getElementById('edit-autohold').checked,
    editedBy: pb.authStore.model.username
  };
  const reminderCheckbox = document.getElementById('edit-email-purchase-reminder');
  if (actionValue === 'purchase' && reminderCheckbox && reminderCheckbox.checked && !reminderCheckbox.disabled) {
    payload.emailPurchaseReminder = true;
  }

  if (actionValue === 'reject') {
    payload.rejectionTemplateId = document.getElementById('edit-rejection-template').value;
  }

  try {
    const res = await fetch(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/action`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify(payload)
    });
    if (!res.ok) {
      const raw = await res.text().catch(() => '');
      let data = {};
      try {
        data = raw ? JSON.parse(raw) : {};
      } catch (parseErr) { }
      const err = new Error(actionErrorMessage(res.status, data, raw));
      err.status = res.status;
      err.code = data.code || '';
      throw err;
    }
    const updatedRecord = await res.json().catch(() => ({}));
    document.getElementById('editModal').close();
    
    rememberRecentSuggestion(updatedRecord);
    renderRecentSuggestionsSwitcher();

    const reminder = updatedRecord && updatedRecord.purchaseReminderEmail;
    if (actionValue === 'purchase') {
      if (reminder && reminder.requested && reminder.sent) {
        showToast('Purchase saved and reminder email sent.', 'success');
      } else if (reminder && reminder.requested) {
        showToast(reminder.message || 'Purchase saved, but the reminder email could not be sent.', 'warning');
      } else {
        showToast('Purchase saved.', 'success');
      }
    }

    if (updatedRecord && updatedRecord.status && updatedRecord.status !== nextStatus) {
      const statusNames = {
        'outstanding_purchase': 'Pending purchase',
        'pending_hold': 'Pending hold',
        'hold_placed': 'Hold placed',
        'closed': 'Closed'
      };
      
      let reason = 'it was detected as already being on hold or having a BIB ID';
      if (updatedRecord.status === 'closed' && updatedRecord.closeReason === 'purchased_no_hold') {
        reason = 'the patron has opted out of automatic hold placement';
      } else if (updatedRecord.status === 'closed' && updatedRecord.closeReason === 'duplicate_hold') {
        reason = 'a duplicate hold or request was detected for this patron';
      }

      await showAlert(`Note: This suggestion moved directly to "${statusNames[updatedRecord.status] || updatedRecord.status}" because ${reason}.`);
    }

    loadTab(currentStatus);
  } catch (err) {
    await showAlert(err.message || 'Error updating suggestion');
    if (err && err.code === 'duplicate_open_request') {
      loadTab(currentStatus);
    }
  }
});

export function setBibIdRequirement(nextStatus) {
  const bibInput = document.getElementById('edit-bibid');
  const bibRequiredMarker = document.getElementById('edit-bibid-required');
  const bibHint = document.getElementById('edit-bibid-hint');

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

async function performImmediateStaffAction(id, payload) {
  try {
    const res = await fetch(`/api/asap/staff/title-requests/${encodeURIComponent(id)}/action`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify(payload)
    });
    if (!res.ok) {
      const raw = await res.text().catch(() => '');
      let data = {};
      try { data = raw ? JSON.parse(raw) : {}; } catch (e) {}
      const err = new Error(actionErrorMessage(res.status, data, raw));
      err.status = res.status;
      err.code = data.code || '';
      throw err;
    }
    const updatedRecord = await res.json().catch(() => ({}));
    
    // Close search dialog
    const searchDialog = document.getElementById('polarisSearchDialog');
    if (searchDialog) searchDialog.close();
    
    // Close edit modal if open
    const editModal = document.getElementById('editModal');
    if (editModal) editModal.close();

    const actionValue = payload.action;
    const nextStatus = payload.status;
    const reminder = updatedRecord && updatedRecord.purchaseReminderEmail;
    
    if (actionValue === 'purchase') {
      if (reminder && reminder.requested && reminder.sent) {
        showToast('Purchase saved and reminder email sent.', 'success');
      } else {
        showToast('Purchase saved.', 'success');
      }
    } else if (actionValue === 'additionalCopy') {
      if (reminder && reminder.requested && reminder.sent) {
        showToast('Additional-copy task created, request queued, and reminder email sent.', 'success');
      } else if (reminder && reminder.requested) {
        showToast(reminder.message || 'Additional-copy task created, but the reminder email could not be sent.', 'warning');
      } else {
        showToast('Additional-copy task created and request queued.', 'success');
      }
    } else if (nextStatus === 'pending_hold') {
      showToast(`Request queued for hold (BIB ${payload.bibid || 'N/A'}).`, 'success');
    } else {
      showToast('Suggestion updated.', 'success');
    }

    if (updatedRecord && updatedRecord.status && updatedRecord.status !== nextStatus) {
      const statusNames = {
        'outstanding_purchase': 'Pending purchase',
        'pending_hold': 'Pending hold',
        'hold_placed': 'Hold placed',
        'closed': 'Closed'
      };
      let reason = 'it was detected as already being on hold or having a BIB ID';
      if (updatedRecord.status === 'closed' && updatedRecord.closeReason === 'purchased_no_hold') {
        reason = 'the patron has opted out of automatic hold placement';
      } else if (updatedRecord.status === 'closed' && updatedRecord.closeReason === 'duplicate_hold') {
        reason = 'a duplicate hold or request was detected for this patron';
      }
      await showAlert(`Note: This suggestion moved directly to "${statusNames[updatedRecord.status] || updatedRecord.status}" because ${reason}.`);
    }

    if (typeof loadTab === 'function') {
      console.log('Action complete, refreshing grid for status:', currentStatus);
      loadTab(currentStatus);
    }
  } catch (err) {
    console.error('performImmediateStaffAction failed:', err);
    await showAlert(err.message || 'Error updating suggestion');
    if (err && err.code === 'duplicate_open_request' && typeof loadTab === 'function') {
      loadTab(currentStatus);
    }
  }
}

/**
 * Audit Preview and Workflow Flag Cleanup Listeners
 */
export function reactiveCleanupWorkflowFlags(rowId) {
  const row = currentSuggestions.find(r => r.id === rowId) || allSuggestions.find(r => r.id === rowId);
  if (!row || !row.workflowTags) return;

  const staleFlags = ['Hold failed', '! Hold failed', 'No holdable items'];
  const originalTags = Array.isArray(row.workflowTags) ? row.workflowTags : (String(row.workflowTags || '').split(',').map(t => t.trim()).filter(Boolean));
  
  const nextTags = originalTags.filter(t => !staleFlags.includes(t));
  
  if (nextTags.length !== originalTags.length) {
    row.workflowTags = nextTags;
    renderEditWorkflowTags(nextTags, row);
    console.log(`Cleaned up stale workflow flags for row ${rowId}`);
  }
}

function refreshEditAuditPreview() {
  const id = document.getElementById('edit-id').value;
  const row = currentSuggestions.find(r => r.id === id) || allSuggestions.find(r => r.id === id);
  const nextStatus = document.getElementById('edit-next-status').value;
  const actionStr = document.getElementById('edit-action').value;
  if (row) renderPendingAuditPreview(row, nextStatus, actionStr);
}

// Event listeners for Edit Form changes to refresh preview
['edit-format', 'edit-publication', 'edit-autohold'].forEach(id => {
  document.getElementById(id)?.addEventListener('change', refreshEditAuditPreview);
});

// Watch for BIB changes to refresh preview (and cleanup flags when verified)
document.getElementById('edit-bibid')?.addEventListener('input', refreshEditAuditPreview);

// Reactive flag cleanup when a BIB is verified
window.addEventListener('asap-bib-verified', (e) => {
  const { rowId } = e.detail;
  reactiveCleanupWorkflowFlags(rowId);
  refreshEditAuditPreview();
});
