import { leapPatronUrl } from '../api.js';

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
  const patronUrl = leapPatronUrl(row.polarisPatronId || '');

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

  if (patronUrl && /^https?:\/\//i.test(patronUrl)) {
    const linkRow = document.createElement('div');
    const link = document.createElement('a');
    link.href = patronUrl;
    link.target = '_blank';
    link.rel = 'noopener noreferrer';
    link.textContent = 'Open Patron in Leap';
    linkRow.appendChild(link);
    detailRows.appendChild(linkRow);
  }

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
