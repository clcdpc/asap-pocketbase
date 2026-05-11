import { pb, formatMap, availableFormats, currentRejectionTemplates, currentStatus, currentSuggestions, allSuggestions, verifiedBibId, publicationOptions, setVerifiedBibId, workflowSettings } from './state.js';
import { leapBibUrl, showToast, showAlert, showConfirm, openProfileDialog } from './api.js';
import { loadTab, formatDateTime, renderWorkflowTags, escapeAttr } from './grid.js';
import { setSelectValue, dateOnly, lookupEditBibById } from './settings-ui.js';

export function openEdit(id, nextStatus, dialogTitle, actionStr, buttonLabel) {
  const row = currentSuggestions.find(r => r.id === id) || allSuggestions.find(r => r.id === id);
  if (!row) return;

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
  document.getElementById('edit-age').value = row.agegroup || 'adult';
  setSelectValue(document.getElementById('edit-publication'), row.publication || publicationOptions[0]);
  document.getElementById('edit-exact-publication-date').value = dateOnly(row.exactPublicationDate);
  document.getElementById('edit-autohold').checked = !!row.autohold;
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

export function renderEditClaimState(row) {
  const container = document.getElementById('edit-claim-state');
  if (!container) return;
  container.replaceChildren();
  const label = document.createElement('div');
  label.className = 'small font-weight-bold mb-1';
  label.textContent = 'Claim';
  container.appendChild(label);
  const currentStaffId = String((pb.authStore.model && pb.authStore.model.id) || '').trim();
  const claimantId = String(row.claimedByStaffUserId || '').trim();
  const badge = document.createElement('span');
  badge.className = 'claim-badge';
  if (!claimantId) {
    badge.classList.add('claim-badge--unclaimed');
    badge.textContent = 'Unclaimed';
    container.appendChild(badge);
    return;
  }
  if (currentStaffId && claimantId === currentStaffId) {
    badge.classList.add('claim-badge--mine');
    badge.textContent = 'Mine';
  } else {
    const name = row.claimedByDisplayName || 'Staff';
    badge.classList.add('claim-badge--claimed');
    badge.textContent = `Claimed by ${name}`;
  }
  container.appendChild(badge);
  const source = document.createElement('div');
  source.className = 'small text-muted mt-1';
  source.textContent = row.claimType === 'automatic_format_rule' ? 'Auto-assigned by format rule' : 'Manual claim';
  container.appendChild(source);
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
    alreadyOwn: 'This request will be marked Already own',
    reject: 'This request will be rejected',
    silentClose: 'This request will be closed silently',
    purchase: 'This request will move to Pending purchase'
  };
  const actionText = actionDescriptions[actionStr];
  if (actionText) {
    return `${actionText} by ${username}.`;
  }

  const currentStatus = row.status || '';
  if (nextStatus && nextStatus !== currentStatus) {
    return `This request will move from ${workflowStatusLabel(currentStatus)} to ${workflowStatusLabel(nextStatus)} by ${username}.`;
  }

  return '';
}

export function renderPendingAuditPreview(row, nextStatus, actionStr) {
  const container = document.getElementById('edit-pending-audit-preview');
  const text = document.getElementById('edit-pending-audit-preview-text');
  if (!container || !text) return;

  const preview = buildPendingAuditPreview(row, nextStatus, actionStr);
  text.textContent = preview;
  container.classList.toggle('hidden', !preview);
}

function workflowStatusLabel(status) {
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

export function renderEditPatronContext(row) {
  const editBody = document.querySelector('#editModal .asap-dialog-edit-body');
  if (!editBody) return;

  let block = document.getElementById('edit-patron-context');
  if (!block) {
    block = document.createElement('div');
    block.id = 'edit-patron-context';
    block.className = 'alert alert-light border py-2 px-3 mb-2 small';
    const anchor = document.getElementById('edit-rejection-template-container');
    if (anchor && anchor.parentNode === editBody) {
      editBody.insertBefore(block, anchor.nextSibling);
    } else {
      editBody.insertBefore(block, editBody.firstChild);
    }
  }

  const patronName = row.patronName || `${row.nameFirst || ''} ${row.nameLast || ''}`.trim() || '—';
  const patronEmail = row.patronEmail || row.email || '—';
  const libraryOrgName = row.libraryOrgName || row.libraryOrgId || '—';
  const preferredPickupBranchName = row.preferredPickupBranchName || '—';
  const barcode = row.barcode || '—';

  block.innerHTML = `
    <div><strong>Patron:</strong> ${escapeAttr(patronName)}</div>
    <div><strong>Email:</strong> ${escapeAttr(patronEmail)}</div>
    <div><strong>Barcode:</strong> ${escapeAttr(barcode)}</div>
    <div><strong>Library:</strong> ${escapeAttr(libraryOrgName)}</div>
    <div><strong>Preferred pickup branch:</strong> ${escapeAttr(preferredPickupBranchName)}</div>
  `;
}

export function renderEditWorkflowTags(tags, row) {
  const container = document.getElementById('edit-workflow-tags');
  if (!container) return;
  container.innerHTML = `
    <div class="small font-weight-bold mb-1">Workflow flags</div>
    ${renderWorkflowTags(tags, row)}
  `;
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
  container.innerHTML = `<a class="btn btn-sm btn-outline-primary" href="${escapeAttr(url)}" target="_blank" rel="noopener noreferrer">Open Bib in Leap</a>`;
}

export function renderExternalSearchButton(title, identifier) {
  const container = document.getElementById('edit-external-search-container');
  if (!container) return;

  const buttons = [];
  const providers = [
    { enabled: workflowSettings.externalSearch1Enabled, label: workflowSettings.externalSearch1Label, template: workflowSettings.externalSearch1UrlTemplate },
    { enabled: workflowSettings.externalSearch2Enabled, label: workflowSettings.externalSearch2Label, template: workflowSettings.externalSearch2UrlTemplate },
    { enabled: workflowSettings.externalSearch3Enabled, label: workflowSettings.externalSearch3Label, template: workflowSettings.externalSearch3UrlTemplate },
    { enabled: workflowSettings.externalSearch4Enabled, label: workflowSettings.externalSearch4Label, template: workflowSettings.externalSearch4UrlTemplate }
  ];

  let cleanTitle = (title || '').split(' (')[0].trim();
  const encodedTitle = encodeURIComponent(cleanTitle);
  const encodedId = encodeURIComponent(identifier || '');

  const buttonClasses = ['btn-warning', 'btn-success', 'btn-primary'];

  providers.forEach((p, index) => {
    if (!p.enabled || !p.template || !p.template.includes('://')) return;

    let url = p.template;
    url = url.replace(/\{\{title\}\}/g, encodedTitle);
    url = url.replace(/\{\{identifier\}\}/g, encodedId);

    const btnClass = buttonClasses[index] || 'btn-info';

    buttons.push(`
      <a href="${escapeAttr(url)}" target="_blank" rel="noopener noreferrer" class="btn btn-xs ${btnClass} mr-1 mb-1">
        <i class="fa fa-external-link"></i> ${escapeAttr(p.label || 'Search')}
      </a>
    `);
  });

  container.innerHTML = buttons.join('');
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
    modeSwitch: document.getElementById('polaris-search-mode-switch'),
    status: document.getElementById('polaris-search-status'),
    results: document.getElementById('polaris-search-results'),
    searchInput: document.getElementById('polaris-search-input'),
    rerunBtn: document.getElementById('polaris-search-rerun-btn')
  };
}

async function fetchPolarisSearch(row, mode, query, options) {
  const res = await fetch('/api/asap/staff/bib-lookup', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': pb.authStore.token
    },
    body: JSON.stringify({
      mode,
      query: query,
      title: options?.title || '',
      author: options?.author || '',
      requestId: row.id || ''
    })
  });
  const data = await res.json();
  if (!res.ok) throw new Error(data.message || 'Polaris search failed');
  return data;
}

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

  els.results.innerHTML = results.map((result, index) => {
    const title = result.title || '(No title returned)';
    const meta = polarisResultMeta(result);
    return `
      <div class="polaris-search-result">
        <div class="polaris-search-result-title">${escapeAttr(title)}</div>
        ${meta ? `<div class="polaris-search-result-meta">${escapeAttr(meta)}</div>` : ''}
        <div class="polaris-search-result-actions">
          <button type="button" class="btn btn-sm btn-primary polaris-search-select"
            data-result-index="${index}"
            ${result.bibId ? '' : 'disabled'}
            aria-label="Use Polaris BIB ${escapeAttr(result.bibId || '')} for this request">
            Use this BIB
          </button>
        </div>
      </div>
    `;
  }).join('');

  els.results.querySelectorAll('.polaris-search-select').forEach(button => {
    button.addEventListener('click', async () => {
      const result = results[parseInt(button.getAttribute('data-result-index') || '-1', 10)];
      if (!result || !result.bibId) return;
      els.dialog.close();
      if (options.source === 'edit') {
        const editModal = document.getElementById('editModal');
        if (editModal && !editModal.open) {
          editModal.showModal();
        }
        await lookupEditBibById({ bibId: result.bibId, button: null });
        const bibInput = document.getElementById('edit-bibid');
        if (bibInput) bibInput.focus();
        showToast('Polaris BIB applied to the edit form.', 'success');
        return;
      }
      openEdit(row.id, row.status || currentStatus, 'Edit suggestion', '', 'Save');
      await lookupEditBibById({ bibId: result.bibId, button: null });
    });
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
  const initialQuery = options.query !== undefined ? String(options.query || '').trim() : '';

  // Initialize input based on launch mode
  if (initialQuery) {
    els.searchInput.value = initialQuery;
  } else if (mode === 'title') {
    els.searchInput.value = originalTitle;
  } else if (mode === 'author') {
    els.searchInput.value = originalAuthor;
  } else if (mode === 'identifier') {
    els.searchInput.value = originalIdentifier;
  } else if (mode === 'title_author') {
    els.searchInput.value = `${originalTitle} ${originalAuthor}`.trim();
  }

  const runSearch = async () => {
    const currentEls = polarisSearchElements();
    const query = currentEls.searchInput.value.trim();

    if (!query) {
      showToast('Please enter search terms.', 'warning');
      return;
    }

    els.status.className = 'alert alert-light border py-2 px-3 small';
    els.status.textContent = 'Searching Polaris...';
    els.results.innerHTML = '';

    try {
      // For keyword search, we send the same query for title/author context to the backend
      const data = await fetchPolarisSearch(row, mode, query, { title: query, author: mode === 'author' ? query : '' });
      renderPolarisSearchResults(row, mode, data, options);
    } catch (err) {
      els.status.className = 'alert alert-danger py-2 px-3 small';
      els.status.textContent = 'Error: ' + err.message;
    }
  };

  els.title.textContent = 'Search Polaris';
  els.summary.innerHTML = `
    <div class="mb-1">This searches Polaris across keyword fields using the text below.</div>
    <div class="text-muted small">Started from ${polarisSearchModeLabel(mode)}: "${els.searchInput.value || 'Unknown'}"</div>
  `;
  
  // Only show "Add author" if we started from title and have an author available
  els.modeSwitch.innerHTML = (mode === 'title' && originalAuthor)
    ? '<button type="button" class="btn btn-sm btn-outline-secondary" id="polaris-search-add-author">Add author to search</button>'
    : '';

  const addAuthorBtn = document.getElementById('polaris-search-add-author');
  if (addAuthorBtn) {
    addAuthorBtn.addEventListener('click', () => {
      const currentEls = polarisSearchElements();
      const currentVal = currentEls.searchInput.value.trim();
      currentEls.searchInput.value = `${currentVal} ${originalAuthor}`.trim();
      addAuthorBtn.remove(); // Only add once
      runSearch();
    });
  }

  // Handle rerun button
  const newRerunBtn = els.rerunBtn.cloneNode(true);
  els.rerunBtn.parentNode.replaceChild(newRerunBtn, els.rerunBtn);
  els.rerunBtn = newRerunBtn;
  els.rerunBtn.addEventListener('click', runSearch);

  // Handle Enter key in input
  const newInp = els.searchInput.cloneNode(true);
  els.searchInput.parentNode.replaceChild(newInp, els.searchInput);
  
  // Re-fetch els after clone
  const updatedEls = polarisSearchElements();
  updatedEls.searchInput.addEventListener('keypress', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      runSearch();
    }
  });

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

  runSearch();
}

function closePolarisSearchDialog() {
  const dialog = document.getElementById('polarisSearchDialog');
  if (dialog) dialog.close();
}

function currentEditPolarisSearchRow() {
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

function editPolarisSearchInputForMode(mode) {
  if (mode === 'author') return document.getElementById('edit-author');
  if (mode === 'identifier') return document.getElementById('edit-identifier');
  return document.getElementById('edit-title');
}

function launchEditPolarisSearch(mode, button) {
  const input = editPolarisSearchInputForMode(mode);
  const row = currentEditPolarisSearchRow();
  const query = mode === 'identifier'
    ? String(input?.value || '').trim()
    : polarisSearchValueForRow(row, mode);
  if (!query) {
    showToast('Enter text before searching Polaris.', 'warning');
    if (input) input.focus();
    return;
  }
  openPolarisSearch(row, mode, {
    source: 'edit',
    query,
    title: polarisSearchValueForRow(row, 'title'),
    author: polarisSearchValueForRow(row, 'author'),
    identifier: document.getElementById('edit-identifier')?.value || '',
    returnDialog: document.getElementById('editModal'),
    returnFocus: button || input
  });
}

document.getElementById('close-polaris-search-x')?.addEventListener('click', closePolarisSearchDialog);
document.getElementById('close-polaris-search-btn')?.addEventListener('click', closePolarisSearchDialog);
document.getElementById('edit-title-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('title', e.currentTarget));
document.getElementById('edit-author-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('author', e.currentTarget));
document.getElementById('edit-identifier-polaris-search')?.addEventListener('click', (e) => launchEditPolarisSearch('identifier', e.currentTarget));

document.getElementById('edit-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const id = document.getElementById('edit-id').value;
  const nextStatus = document.getElementById('edit-next-status').value;
  const bibid = document.getElementById('edit-bibid').value.trim();
  const row = currentSuggestions.find(r => r.id === id) || allSuggestions.find(r => r.id === id);
  if (row && row.status === 'outstanding_purchase' && bibid && !row.autohold) {
    const confirmed = await showConfirm('Do Not Auto Place Hold', 'This request is marked Do Not Auto Place Hold. Saving this BIB ID will close the request immediately and skip the hold-placement workflow.');
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
    agegroup: document.getElementById('edit-age').value,
    publication: document.getElementById('edit-publication').value,
    exactPublicationDate: document.getElementById('edit-exact-publication-date').value,
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
    const res = await fetch(`/api/asap/staff/title-requests/${id}/action`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify(payload)
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      throw new Error(data.message || 'Error updating suggestion');
    }
    const updatedRecord = await res.json().catch(() => ({}));
    document.getElementById('editModal').close();
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
      ? 'Required to identify the item in the catalog and proceed with the request.'
      : 'Needed to link this request to a catalog record.';
  }
}
