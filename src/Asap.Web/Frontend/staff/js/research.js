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
  controls.bib.value = String(selected.bibId);
  controls.title.value = mergeCatalogValue(selected.title, controls.title.value);
  controls.author.value = mergeCatalogValue(selected.author, controls.author.value);
  if (!controls.identifier.disabled && !String(controls.identifier.value ?? '').trim()) {
    const catalogIdentifier = String(selected.identifier ?? '').trim();
    if (catalogIdentifier) controls.identifier.value = catalogIdentifier;
  }
}

export function researchUrl(template, values, requiredToken = '') {
  const pattern = String(template || '').trim();
  if (!pattern || (requiredToken && !pattern.includes(`{{${requiredToken}}}`))) return '';
  let invalid = false;
  const result = pattern.replace(/\{\{([^{}]+)\}\}/g, (_, token) => {
    if (!supportedTokens.has(token) || !values[token]) {
      invalid = true;
      return '';
    }
    return encodeURIComponent(String(values[token]).trim());
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

export function renderResearchLinks(container, request, research, draft = {}) {
  const links = [];
  const values = {
    title: draft.title ?? request.title,
    identifier: draft.identifier ?? request.identifier,
    bibid: draft.bibId ?? request.bibid,
    'patron-id': research?.patronId,
    patronId: research?.patronId
  };
  const add = (label, url) => {
    if (url) links.push(node('a', { href: url, target: '_blank', rel: 'noopener noreferrer', text: label }));
  };
  if (/^[1-9][0-9]*$/.test(String(values.bibid || '').trim())) {
    add('Open BIB in LEAP', researchUrl(research?.leapBibUrlPattern, values, 'bibid'));
  }
  const patronPattern = research?.leapPatronUrlPattern;
  if (patronPattern?.includes('{{patron-id}}') || patronPattern?.includes('{{patronId}}')) {
    add('Open patron in LEAP', researchUrl(patronPattern, values));
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
    container.append(node('h3', { text: 'Catalog and research links' }), node('div', { className: 'research-links' }, links));
  }
}

export function createPolarisLookup({ authorizedJson, isAbortError, announce }) {
  const dialog = document.querySelector('#polaris-dialog');
  const mode = dialog.querySelector('#polaris-mode');
  const query = dialog.querySelector('#polaris-query');
  const title = dialog.querySelector('#polaris-title');
  const author = dialog.querySelector('#polaris-author');
  const queryField = dialog.querySelector('#polaris-query-field');
  const pairFields = dialog.querySelector('#polaris-pair-fields');
  const status = dialog.querySelector('#polaris-status');
  const results = dialog.querySelector('#polaris-results');
  const searchForm = dialog.querySelector('#polaris-form');
  const closeButton = dialog.querySelector('#close-polaris');
  let context = null;
  let controller = null;
  let generation = 0;

  function active(current, token) {
    return context === current && current?.isCurrent() && generation === token && dialog.open;
  }

  function abort() {
    generation += 1;
    controller?.abort();
    controller = null;
  }

  function close() {
    abort();
    const old = context;
    context = null;
    if (dialog.open) dialog.close();
    if (old?.isCurrent()) {
      const target = old.returnFocus?.isConnected ? old.returnFocus : old.editorFocus;
      target?.focus();
    }
  }

  function modeChanged() {
    const paired = mode.value === 'title_author';
    pairFields.hidden = !paired;
    queryField.hidden = paired;
    query.required = !paired;
    title.required = paired;
    author.required = paired;
    dialog.querySelector('#polaris-query-label').textContent = mode.value === 'bib'
      ? 'Exact Polaris BIB ID' : mode.value === 'identifier' ? 'Catalog identifier'
        : mode.value === 'author' ? 'Catalog author' : 'Catalog title';
    abort();
    results.replaceChildren();
    status.textContent = '';
  }

  function contextText(detail) {
    const parts = [];
    if (detail.holdingsSummary) {
      const h = detail.holdingsSummary;
      parts.push(`${h.myLibraryCount} item${h.myLibraryCount === 1 ? '' : 's'} at this library; ` +
        `${h.otherLibraryCount} elsewhere; ${h.isHoldable ? 'holdable' : 'not holdable'}.`);
    } else if (detail.holdingsUnavailable) {
      parts.push('Holdings are temporarily unavailable.');
    }
    if (detail.patronHasHold === true) parts.push('This patron already has a hold for this BIB.');
    if (detail.patronHasHold === false) parts.push('No existing patron hold was found for this BIB.');
    return parts.join(' ');
  }

  function showResults(current, found, exactDetail = null) {
    const rows = exactDetail ? [exactDetail] : Array.isArray(found.results) ? found.results : [];
    results.replaceChildren();
    status.textContent = rows.length
      ? `${rows.length} catalog result${rows.length === 1 ? '' : 's'} shown${found.totalMatches > rows.length ? ` of ${found.totalMatches}` : ''}.`
      : 'No Polaris matches found.';
    rows.forEach(row => {
      const item = node('li', { className: 'polaris-result' });
      item.append(node('strong', { text: row.title || '(No title returned)' }));
      const metadata = [row.author && `Author: ${row.author}`, row.publication && `Publication: ${row.publication}`,
        row.publisher && `Publisher: ${row.publisher}`, row.format && `Format: ${row.format}`,
        row.identifier && `Identifier: ${row.identifier}`,
        `BIB: ${row.bibId}`].filter(Boolean);
      item.append(node('p', { text: metadata.join(' · ') }));
      if (exactDetail) item.append(node('p', { text: contextText(exactDetail) }));
      const select = node('button', { type: 'button', className: 'primary-button', text: `Use BIB ${row.bibId}` });
      select.disabled = !(typeof current.canApply === 'function' ? current.canApply(row) : current.canApply);
      select.addEventListener('click', () => selectResult(current, row, exactDetail));
      item.append(select);
      results.append(item);
    });
  }

  async function selectResult(current, row, existingDetail) {
    abort();
    const token = generation;
    controller = new AbortController();
    status.textContent = `Verifying BIB ${row.bibId} and loading holdings...`;
    try {
      const detail = existingDetail || await authorizedJson('/api/asap/staff/bib-lookup', {
        method: 'POST', body: { requestId: current.requestId, libraryOrgId: current.libraryOrgId,
          mode: 'bib', bibId: row.bibId }, signal: controller.signal
      });
      if (!active(current, token) || String(detail.bibId) !== String(row.bibId)) return;
      const selected = {
        ...row, ...detail,
        title: detail.title || row.title,
        author: detail.author || row.author,
        publication: detail.publication || row.publication,
        format: detail.format || row.format,
        identifier: detail.identifier || row.identifier
      };
      // Keep catalog publication/format in verified detail; the editor fields store workflow timing and ASAP format codes.
      current.apply(selected, detail);
      announce(`Verified Polaris BIB ${detail.bibId}. Save changes before changing the request status.`, 'success');
      close();
      current.editorFocus?.focus();
    } catch (error) {
      if (!active(current, token) || isAbortError(error)) return;
      status.textContent = error.message || 'BIB verification failed.';
      announce(status.textContent, 'error');
    }
  }

  async function search(event) {
    event.preventDefault();
    const current = context;
    if (!current?.isCurrent()) return;
    abort();
    const token = generation;
    controller = new AbortController();
    results.replaceChildren();
    status.textContent = 'Searching Polaris...';
    const body = { requestId: current.requestId, libraryOrgId: current.libraryOrgId, mode: mode.value };
    if (mode.value === 'bib') body.bibId = query.value.trim();
    else if (mode.value === 'title_author') {
      body.title = title.value.trim();
      body.author = author.value.trim();
    } else body.query = query.value.trim();
    try {
      const found = await authorizedJson('/api/asap/staff/bib-lookup', {
        method: 'POST', body, signal: controller.signal
      });
      if (!active(current, token)) return;
      showResults(current, found, mode.value === 'bib' ? found : null);
    } catch (error) {
      if (!active(current, token) || isAbortError(error)) return;
      status.textContent = error.message || 'Polaris search failed.';
      announce(status.textContent, 'error');
    }
  }

  searchForm.addEventListener('submit', search);
  mode.addEventListener('change', modeChanged);
  for (const input of [query, title, author]) input.addEventListener('input', () => {
    abort();
    results.replaceChildren();
    status.textContent = '';
  });
  closeButton.addEventListener('click', close);
  dialog.addEventListener('cancel', event => { event.preventDefault(); close(); });

  return {
    close,
    invalidate: abort,
    open(next) {
      if (dialog.open) close();
      context = next;
      mode.value = next.mode || 'title';
      query.value = next.query || '';
      title.value = next.title || '';
      author.value = next.author || '';
      modeChanged();
      dialog.showModal();
      (mode.value === 'title_author' ? title : query).focus();
      announce('Polaris search opened.');
    }
  };
}
