import { createCatalogQueue } from './catalog-queue.js';

export function positivePolarisId(value) {
  if (typeof value === 'string') {
    const text = value.trim();
    if (!/^\d+$/.test(text)) return null;
    value = Number(text);
  }
  return Number.isInteger(value) && value > 0 && value <= 2147483647 ? value : null;
}

const supportedTokens = new Set(['title', 'identifier', 'bibid', 'patron-id', 'patronId']);

export function mergeCatalogValue(catalogValue, existingValue) {
  const catalog = String(catalogValue ?? '').trim();
  const existing = String(existingValue ?? '').trim();
  if (!catalog) return existing;
  if (!existing || existing === catalog) return catalog;
  if (existing.startsWith(`${catalog} (`)) return existing;

  const existingBase = existing.replace(/\s+\([^()]*\)\s*$/, '').trim();
  if (existingBase && (existingBase === catalog || existingBase.startsWith(catalog) || catalog.startsWith(existingBase))) {
    return existing;
  }
  return `${catalog} (${existing})`;
}

export function applyPolarisResultToControls(selected, controls) {
  // Selection verifies the BIB association and reconciles Title only. Author,
  // identifier, publication and ASAP format remain the staff member's draft.
  controls.bib.value = String(selected.bibId);
  controls.title.value = mergeCatalogValue(selected.title, controls.title.value);
}

export function selectedStaffBibId(selection, requestId, bibId) {
  const selectedId = positivePolarisId(selection?.bibId);
  if (!selectedId || selection?.requestId !== String(requestId) ||
      selectedId !== positivePolarisId(bibId)) return null;
  return selectedId;
}

export function researchUrl(template, values, requiredToken = '') {
  const pattern = String(template || '').trim();
  if (!/^https?:\/\/[^/\\\s?#]/i.test(pattern) || pattern.includes('\\') || /[\u0000-\u001f\u007f-\u009f]/.test(pattern) ||
      (requiredToken && !pattern.includes(`{{${requiredToken}}}`))) return '';
  let invalid = false;
  const result = pattern.replace(/\{\{([^{}]+)\}\}/g, (_, token) => {
    const value = String(values[token] ?? '').trim();
    if (!supportedTokens.has(token) || !value) {
      invalid = true;
      return '';
    }
    return encodeURIComponent(value);
  });
  if (invalid || /\{\{|\}\}/.test(result)) return '';
  try {
    const url = new URL(result);
    return ['http:', 'https:'].includes(url.protocol) && !url.username && !url.password ? url.href : '';
  } catch {
    return '';
  }
}

function node(tag, attributes = {}, children = []) {
  const result = document.createElement(tag);
  for (const [key, value] of Object.entries(attributes)) {
    if (value === null || value === undefined) continue;
    if (key === 'text') result.textContent = String(value);
    else if (key === 'className') result.className = value;
    else result.setAttribute(key, String(value));
  }
  for (const child of children) result.append(child);
  return result;
}

export function researchRecordUrl(research, kind, bibId = null) {
  if (kind === 'bib') {
    const id = positivePolarisId(bibId);
    return id ? researchUrl(research?.leapBibUrlPattern, { bibid: id }, 'bibid') : '';
  }
  const id = positivePolarisId(research?.patronId), pattern = research?.leapPatronUrlPattern;
  if (!id || typeof pattern !== 'string' ||
      !(pattern.includes('{{patron-id}}') || pattern.includes('{{patronId}}'))) return '';
  return researchUrl(pattern, { 'patron-id': id, patronId: id });
}

export function researchRecordValue(value, research, kind, bibId = null) {
  const label = String(value ?? '').trim() || 'Not recorded';
  const url = researchRecordUrl(research, kind, bibId);
  return url ? node('a', { href: url, target: '_blank', rel: 'noopener noreferrer', text: label })
    : node('span', { text: label });
}

export function renderResearchLinks(container, request, research, draft = {}) {
  const links = [];
  const values = {
    title: draft.title ?? request.title,
    identifier: draft.identifier ?? request.identifier,
    bibid: positivePolarisId(draft.bibId ?? request.bibid)
  };
  const add = (label, url) => {
    if (url) links.push(node('a', { href: url, target: '_blank', rel: 'noopener noreferrer', text: label }));
  };
  if (values.bibid !== null) {
    add('Open BIB in LEAP', researchUrl(research?.leapBibUrlPattern, values, 'bibid'));
  }
  const providers = Array.isArray(research?.externalSearchProviders)
    ? [...research.externalSearchProviders].sort((a, b) => a.sortOrder - b.sortOrder)
    : [];
  for (const provider of providers) {
    if (provider.isEnabled === false || !provider.label) continue;
    const template = String(provider.urlTemplate || '');
    if (/\{\{(?!title\}\}|identifier\}\})/.test(template)) continue;
    add(provider.label, researchUrl(template, values));
  }
  container.replaceChildren();
  container.hidden = links.length === 0;
  if (links.length) {
    container.append(node('div', { className: 'research-links' }, links));
  }
}

export function safeMaterialIconUrl(value) {
  const text = String(value || '').trim();
  if (/[\\\u0000-\u0020\u007f-\u009f]/.test(text)) return '';
  if (text.startsWith('/')) return text.startsWith('//') ? '' : text;
  if (!/^https?:\/\//i.test(text)) return '';
  try {
    const url = new URL(text);
    return ['http:', 'https:'].includes(url.protocol) && !url.username && !url.password ? url.href : '';
  } catch { return ''; }
}

export function catalogActionReason(reason) {
  return ({ holdings_unavailable: 'Holdings unavailable. Search again to check eligibility.',
    bib_not_holdable: 'This BIB is not holdable.', bib_not_owned: 'No consortium item records were found.',
    duplicate_open_request: 'This patron already has an open request for this BIB.',
    additional_copy_exists: 'This request already has an open additional-copy task.',
    stale_version: 'The request changed. Search again to review the current version.',
    hold_operation_incomplete: 'A hold operation needs reconciliation before this request can change.',
    pickup_reconciliation_required: 'A pickup operation needs reconciliation before this request can change.',
    request_context_required: 'Catalog research only.',
    request_claimed_by_other: 'Another staff member has claimed this request.' })[reason] ||
    'The current request permissions, claim or workflow state do not allow this action.';
}

export function createPolarisLookup({ authorizedJson, isAbortError, announce, sessionIdentity, onReceipt, clearReceipt,
  storage }) {
  const dialog = document.querySelector('#polaris-dialog');
  const mode = dialog.querySelector('#polaris-mode');
  const query = dialog.querySelector('#polaris-query');
  const title = dialog.querySelector('#polaris-title');
  const author = dialog.querySelector('#polaris-author');
  const queryField = dialog.querySelector('#polaris-query-field');
  const pairFields = dialog.querySelector('#polaris-pair-fields');
  const status = dialog.querySelector('#polaris-status');
  const ownershipStatus = dialog.querySelector('#polaris-ownership-status');
  const results = dialog.querySelector('#polaris-results');
  const searchForm = dialog.querySelector('#polaris-form');
  const closeButton = dialog.querySelector('#close-polaris');
  const addAuthor = dialog.querySelector('#polaris-add-author');
  const confirmation = dialog.querySelector('#polaris-confirmation');
  let context = null;
  let controller = null;
  let generation = 0;
  let disposed = false;
  let escapeHeld = false;
  const events = new window.AbortController();
  const queue = createCatalogQueue({ dialog, panel: confirmation, searchForm, results,
    sessionIdentity, authorizedJson, isAbortError, announce, onReceipt, clearReceipt, storage,
    isCurrent: current => !disposed && context === current && current?.isCurrent() && dialog.open,
    onCommitted: async (current, result) => {
      close(current);
      return current.onCommitted?.(result);
    },
    onReturnToSearch: current => {
      if (context !== current || !current.isCurrent()) return;
      abort();
      results.replaceChildren();
      status.textContent = 'Search Polaris again to review current results and available actions.';
      searchForm.querySelector('button[type="submit"]')?.focus();
    }
  });

  function active(current, token) {
    return !disposed && context === current && current?.isCurrent() && generation === token && dialog.open;
  }

  function abort() {
    generation += 1;
    controller?.abort();
    controller = null;
    ownershipStatus.textContent = '';
  }

  function close(owner = null, force = false) {
    if (disposed || !context || owner !== null && owner !== context) return false;
    if (!queue.reset(force)) return false;
    abort();
    const old = context;
    context = null;
    escapeHeld = false;
    if (dialog.open) dialog.close();
    if (old?.isCurrent()) {
      const target = old.returnFocus?.isConnected ? old.returnFocus : old.editorFocus || old.fallbackFocus;
      target?.focus();
    }
    return true;
  }

  function modeChanged() {
    if (queue.isLocked()) return;
    const paired = mode.value === 'title_author';
    pairFields.hidden = !paired;
    queryField.hidden = paired;
    query.required = !paired;
    title.required = paired;
    author.required = paired;
    dialog.querySelector('#polaris-query-label').textContent = mode.value === 'bib'
      ? 'Exact Polaris BIB ID' : mode.value === 'identifier' ? 'Catalog identifier'
        : mode.value === 'author' ? 'Author keywords' : 'Title keywords';
    addAuthor.hidden = !String(context?.sourceAuthor || context?.author || '').trim() || !['title', 'author'].includes(mode.value);
    abort();
    results.replaceChildren();
    status.textContent = '';
  }

  function holdingsReady(detail) {
    const h = detail?.holdingsSummary;
    return !detail?.holdingsUnavailable && h &&
      [h.myLibraryCount, h.otherLibraryCount, h.consortiumCount].every(value => Number.isInteger(value) && value >= 0) &&
      h.myLibraryCount + h.otherLibraryCount === h.consortiumCount && typeof h.isHoldable === 'boolean';
  }

  function showOwnership(container, detail) {
    container.replaceChildren();
    if (!holdingsReady(detail)) {
      container.append(node('span', { className: 'polaris-badge warning', text: detail?.holdingsErrorCode === 'holdings_timeout'
        ? 'Holdings unavailable (timed out)' : 'Holdings unavailable' }));
      return;
    }
    const h = detail.holdingsSummary;
    const badge = text => container.append(node('span', { className: 'polaris-badge', text }));
    if (h.myLibraryCount > 0) badge(`Owned by you (${h.myLibraryCount.toLocaleString()})`);
    if (h.otherLibraryCount > 0) badge(`Owned by consortium (${h.consortiumCount.toLocaleString()})`);
    if (h.consortiumCount === 0) badge('No item records found');
    else if (!h.isHoldable) badge('Not holdable');
    if (detail.patronHasHold === true) badge('This patron already has a hold for this BIB');
  }

  function exactLookup(current, bibId, signal, includePatronHolds = false) {
    return authorizedJson('/api/asap/staff/bib-lookup', {
      method: 'POST', body: { requestId: current.requestId, libraryOrgId: current.libraryOrgId,
        mode: 'bib', bibId, includePatronHolds }, signal
    });
  }

  function matchingDetail(current, row, detail) {
    const requestId = current.requestId === null || current.requestId === undefined ? null : String(current.requestId);
    return detail?.bibId === row.bibId &&
      (detail.requestId === undefined || detail.requestId === requestId) &&
      (detail.libraryOrgId === undefined || detail.libraryOrgId === current.libraryOrgId);
  }

  function renderDirectActions(current, row, detail, actions) {
    actions.replaceChildren();
    if (current.directActions !== true) return;
    const preview = detail?.queueActions;
    const scoped = matchingDetail(current, row, detail) && preview?.requestId === String(current.requestId) &&
      preview?.libraryOrgId === current.libraryOrgId && preview?.bibId === row.bibId && Boolean(preview.version);
    for (const [key, label, choice] of [
      ['catalogFound', 'Use BIB & Queue Now', preview?.useBib],
      ['additionalCopy', 'Buy another copy + Queue Now', preview?.additionalCopy]
    ]) {
      // Additional copies require positively confirmed owned, holdable items.
      // A badge alone never grants a workflow action: server capabilities do.
      const allowed = scoped && holdingsReady(detail) && choice?.allowed === true &&
        detail.holdingsSummary.isHoldable && (key !== 'additionalCopy' || detail.holdingsSummary.consortiumCount > 0);
      const button = node('button', { type: 'button', className: key === 'additionalCopy' ? 'polaris-copy-button' : 'polaris-queue-button',
        text: label, 'aria-label': `${label}: ${row.title || 'Catalog title'}, BIB ${row.bibId}` });
      button.disabled = !allowed;
      if (!allowed) button.title = detail ? catalogActionReason(choice?.blockingReason || 'holdings_unavailable') : 'Checking ownership and current action eligibility…';
      button.addEventListener('click', () => queue.confirm(current, row, detail, key, button));
      actions.append(button);
    }
    if (detail && (!scoped || !preview.useBib.allowed || !preview.additionalCopy.allowed)) {
      const reasons = new Set([preview?.useBib, preview?.additionalCopy].filter(choice => !choice?.allowed)
        .map(choice => catalogActionReason(choice?.blockingReason)));
      if (!scoped) reasons.add('The request context could not be verified. Search again.');
      actions.append(node('p', { className: 'polaris-action-help', text: [...reasons].join(' ') }));
    }
  }

  function showResults(current, found, token, exactDetail = null) {
    const rows = (exactDetail ? [exactDetail] : Array.isArray(found.results) ? found.results : []).slice(0, 10);
    results.replaceChildren();
    const total = Number.isInteger(found.totalMatches) && found.totalMatches >= 0 ? found.totalMatches : null;
    status.textContent = rows.length
      ? `${rows.length} catalog result${rows.length === 1 ? '' : 's'} shown${total !== null ? ` of ${total.toLocaleString()}` : ''}.`
      : 'No Polaris matches found.';
    const cards = rows.map(row => {
      const item = node('li', { className: 'polaris-result' });
      const heading = node('div', { className: 'polaris-result-heading' });
      const iconUrl = safeMaterialIconUrl(row.formatIconUrl);
      if (iconUrl) {
        const image = node('img', { src: iconUrl, alt: '', 'aria-hidden': 'true', width: '24', height: '24' });
        image.addEventListener('error', () => image.remove(), { once: true });
        heading.append(image);
      }
      heading.append(node('strong', { text: row.title || '(No title returned)' })); item.append(heading);
      const material = String(row.format || '').trim();
      const metadata = [row.author && `Author: ${row.author}`, row.publication && `Publication: ${row.publication}`,
        row.publisher && `Publisher: ${row.publisher}`, `Format: ${material && !/^\d+$/.test(material) ? material : 'Material type unavailable'}`,
        row.identifier && `Identifier: ${row.identifier}`,
        `BIB: ${row.bibId}`].filter(Boolean);
      item.append(node('p', { className: 'polaris-metadata', text: metadata.join(' · ') }));
      const ownership = node('div', { className: 'polaris-ownership' }, [node('span', { text: 'Checking ownership…' })]);
      item.append(ownership);
      const actions = node('div', { className: 'polaris-result-actions' });
      if (current.readOnly !== true) {
        const select = node('button', { type: 'button', className: 'primary-button', text: `Use BIB ${row.bibId} in form` });
        select.disabled = !positivePolarisId(row.bibId) || !(typeof current.canApply === 'function' ? current.canApply(row) : current.canApply);
        select.addEventListener('click', () => selectResult(current, row, exactDetail));
        actions.append(select);
      } else renderDirectActions(current, row, null, actions);
      item.append(actions);
      results.append(item);
      return { row, ownership, actions };
    });
    // Exact lookup already carries holdings. Search enrichment is at most three
    // concurrent calls, deduplicated per BIB in this search/request/library only.
    const cache = new Map();
    let index = 0;
    async function worker() {
      while (active(current, token) && index < cards.length) {
        const card = cards[index++], id = positivePolarisId(card.row.bibId);
        try {
          if (!id) throw new Error('Invalid catalog BIB');
          if (!cache.has(id)) cache.set(id, exactDetail ? Promise.resolve(exactDetail) : exactLookup(current, id, controller.signal));
          const detail = await cache.get(id);
          if (!active(current, token)) return;
          if (!matchingDetail(current, card.row, detail)) throw new Error('The catalog result context changed.');
          showOwnership(card.ownership, detail);
          card.unavailable = !holdingsReady(detail);
          if (current.readOnly === true) renderDirectActions(current, card.row, detail, card.actions);
        } catch (error) {
          if (!active(current, token)) return;
          showOwnership(card.ownership, null);
          card.unavailable = true;
          if (current.directActions === true) renderDirectActions(current, card.row,
            { holdingsUnavailable: true }, card.actions);
        }
      }
    }
    void Promise.all(Array.from({ length: Math.min(3, cards.length) }, worker)).then(() => {
      if (cards.length && active(current, token)) ownershipStatus.textContent = cards.some(card => card.unavailable)
        ? 'Ownership checks complete. Some holdings are unavailable; affected workflow actions are disabled.'
        : 'Ownership checks complete. Review each result for holdings and available actions.';
    });
  }

  async function selectResult(current, row, existingDetail) {
    if (queue.isLocked() || !current.isCurrent() || current.readOnly === true ||
        !(typeof current.canApply === 'function' ? current.canApply(row) : current.canApply)) return;
    abort();
    const token = generation;
    controller = new AbortController();
    status.textContent = `Verifying BIB ${row.bibId} and loading holdings...`;
    try {
      const detail = existingDetail || await exactLookup(current, row.bibId, controller.signal, true);
      if (!active(current, token) || !matchingDetail(current, row, detail)) return;
      const selected = {
        ...row, ...detail,
        title: detail.title || row.title,
        author: detail.author || row.author,
        publication: detail.publication || row.publication,
        format: detail.format || row.format,
        identifier: detail.identifier || row.identifier
      };
      // Keep catalog publication/format in verified detail; the editor fields store workflow timing and ASAP format codes.
      current.apply(selected, selected);
      announce(`Verified Polaris BIB ${detail.bibId}. Save changes before changing the request status.`, 'success');
      close();
      current.editorFocus?.focus();
    } catch (error) {
      if (!active(current, token)) return;
      status.textContent = error.message || 'BIB verification failed.';
      current.onError?.(status.textContent);
      announce(status.textContent, 'error');
    }
  }

  async function search(event) {
    event.preventDefault();
    const current = context;
    if (!current?.isCurrent() || queue.isLocked()) return;
    abort();
    const token = generation;
    controller = new AbortController();
    results.replaceChildren();
    status.textContent = 'Searching Polaris...';
    const body = { requestId: current.requestId, libraryOrgId: current.libraryOrgId, mode: mode.value,
      includePatronHolds: mode.value === 'bib' && current.readOnly !== true };
    if (mode.value === 'bib') {
      body.bibId = positivePolarisId(query.value);
      if (body.bibId === null) {
        status.textContent = 'Enter a positive Polaris BIB ID up to 2147483647.';
        current.onError?.(status.textContent);
        announce(status.textContent, 'error');
        return;
      }
    }
    else if (mode.value === 'title_author') {
      body.title = title.value.trim();
      body.author = author.value.trim();
    } else body.query = query.value.trim();
    try {
      const found = await authorizedJson('/api/asap/staff/bib-lookup', {
        method: 'POST', body, signal: controller.signal
      });
      if (!active(current, token)) return;
      showResults(current, found, token, mode.value === 'bib' ? found : null);
    } catch (error) {
      if (!active(current, token)) return;
      status.textContent = error.message || 'Polaris search failed.';
      current.onError?.(status.textContent);
      announce(status.textContent, 'error');
    }
  }

  searchForm.addEventListener('submit', search, { signal: events.signal });
  mode.addEventListener('change', modeChanged, { signal: events.signal });
  addAuthor.addEventListener('click', () => {
    if (!context?.isCurrent() || queue.isLocked() || addAuthor.hidden) return;
    title.value = mode.value === 'title' ? query.value : context.title || '';
    author.value = context.sourceAuthor || context.author;
    mode.value = 'title_author'; modeChanged(); author.focus();
    announce('Author added. Review the title and author, then search Polaris.');
  }, { signal: events.signal });
  for (const input of [query, title, author]) input.addEventListener('input', () => {
    if (queue.isLocked()) return;
    abort();
    results.replaceChildren();
    status.textContent = '';
  }, { signal: events.signal });
  closeButton.addEventListener('click', () => close(), { signal: events.signal });
  dialog.addEventListener('keydown', event => {
    if (event.key !== 'Escape' || !queue.isLocked() && !escapeHeld) return;
    // An inline decision owns this keystroke. Prevent the browser's dialog
    // close action as well as repeated keydowns after focus returns to a result.
    event.preventDefault(); event.stopPropagation();
    if (!escapeHeld) queue.dismiss();
    escapeHeld = true;
  }, { signal: events.signal });
  dialog.addEventListener('keyup', event => {
    if (event.key !== 'Escape' || !escapeHeld) return;
    event.preventDefault(); event.stopPropagation(); escapeHeld = false;
  }, { signal: events.signal });
  dialog.addEventListener('cancel', event => {
    event.preventDefault();
    if (queue.isLocked()) queue.dismiss(); else close();
  }, { signal: events.signal });

  return {
    close,
    signedOut() { close(null, true); },
    dispose() { if (disposed) return; close(null, true); disposed = true; abort(); events.abort(); },
    inspectDeparture: queue.inspectDeparture,
    invalidate(owner = null) { if (owner === null || owner === context) abort(); },
    open(next) {
      if (disposed || !next.isCurrent()) return;
      if (dialog.open && !close()) return;
      context = next;
      mode.value = next.mode || 'title';
      query.value = next.query || '';
      title.value = next.title || '';
      author.value = next.author || '';
      modeChanged();
      if (next.readOnly === true) status.textContent = next.directActions === true
        ? 'Search to check ownership and available workflow actions.' : 'Catalog research only.';
      dialog.showModal();
      (mode.value === 'title_author' ? title : query).focus();
      queue.open(next);
      announce('Polaris search opened.');
    }
  };
}
