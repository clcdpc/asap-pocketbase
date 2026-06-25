import { polarisSearchValueForRow, fallbackPolarisSearchValue, actionErrorMessage } from './utils.js';
import { confirmAdditionalCopyAction } from './additional-copy.js';
import { authorizedJson } from '../http.js';
import { showToast, showAlert } from '../dialogs.js';
import { applySelectedPolarisResultToEditForm } from '../settings-ui.js';
import { escapeAttr } from '../grid-utils.js';
import { pb, publicationOptions } from '../state.js';
import { submitTitleRequestAction } from './edit-submit.js';

let holdingsLookupUnavailable = false;

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

async function fetchPolarisSearch(row, mode, query, options, ctx) {
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
  try {
    return await authorizedJson('/api/asap/staff/bib-lookup', {
      method: 'POST',
      body: payload
    });
  } catch (err) {
    if (err && err.status === 0) {
      err.networkError = true;
    }
    throw err;
  }
}

function renderPolarisSearchResults(row, mode, data, options = {}, ctx, onRefresh) {
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

    const fallbackPubOptions = ctx && ctx.publicationOptions ? ctx.publicationOptions : publicationOptions;
    const fallbackPb = ctx && ctx.pb ? ctx.pb : pb;
    const buildPayload = (nextStatus, action) => {
      const isAdditionalCopyAction = action === 'additionalCopy';
      const workflowPublication = isAdditionalCopyAction
        ? (row.publication || fallbackPubOptions[0] || '')
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
        editedBy: fallbackPb.authStore.model.username
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
        await performImmediateStaffAction(row.id, payload, ctx, onRefresh);
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
      await performImmediateStaffAction(row.id, payload, ctx, onRefresh);
    });
    actionsDiv.appendChild(additionalCopyBtn);


    const holdingsUnavailable = ctx ? ctx.holdingsLookupUnavailable : holdingsLookupUnavailable;
    // Background Holdings Check
    if (result.bibId && !holdingsUnavailable) {
      fetchPolarisSearch(row, 'identifier', '', { bibId: result.bibId }, ctx)
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
            if (ctx) {
              ctx.holdingsLookupUnavailable = true;
            } else {
              holdingsLookupUnavailable = true;
            }
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


export async function openPolarisSearch(row, mode, options = {}, ctx, onRefresh) {
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
      const data = await fetchPolarisSearch(row, currentMode, query, { title: query, author: author }, ctx);
      renderPolarisSearchResults(row, currentMode, data, options, ctx, onRefresh);
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

export function closePolarisSearchDialog() {
  const dialog = document.getElementById('polarisSearchDialog');
  if (dialog) dialog.close();
}

function currentEditPolarisSearchRow(context = 'edit', ctx) {
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
  const existing = ctx.currentSuggestions.find(r => r.id === id) || ctx.allSuggestions.find(r => r.id === id) || {};
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

export function launchEditPolarisSearch(mode, button, context = 'edit', ctx, onRefresh) {
  const input = editPolarisSearchInputForMode(mode, context);
  const row = currentEditPolarisSearchRow(context, ctx);
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
  }, ctx, onRefresh);
}

async function performImmediateStaffAction(id, payload, ctx, onRefresh) {
  await submitTitleRequestAction(id, payload, {
    onRefresh,
    dialogsToClose: ['polarisSearchDialog', 'editModal']
  });
}
