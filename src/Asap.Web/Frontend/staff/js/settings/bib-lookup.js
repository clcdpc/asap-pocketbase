import { currentSuggestions, setVerifiedBibId } from '../state.js';
import { authorizedJson } from '../http.js';

export async function lookupEditBibById(options = {}) {
  const bibInput = document.getElementById('edit-bibid');
  const bibId = String(options.bibId !== undefined ? options.bibId : bibInput.value).trim();
  if (options.bibId !== undefined) {
    bibInput.value = bibId;
    bibInput.dispatchEvent(new Event('input', { bubbles: true }));
  }
  const btn = Object.prototype.hasOwnProperty.call(options, 'button') ? options.button : document.getElementById('btn-bib-lookup');
  const display = document.getElementById('bib-info-display');
  const text = document.getElementById('bib-info-text');
  const originalButtonText = btn ? btn.textContent : '';

  if (!bibId) {
    display.classList.remove('hidden', 'alert-info');
    display.classList.add('alert-danger');
    text.textContent = 'Please enter a BIB ID first.';
    return null;
  }

  if (btn) {
    btn.disabled = true;
    btn.textContent = options.pendingText || '...';
  }
  display.classList.add('hidden');

  try {
    const row = currentSuggestions.find(r => r.id === document.getElementById('edit-id').value);
    const barcode = row ? row.barcode : '';

    const data = await authorizedJson('/api/asap/staff/bib-lookup', {
      method: 'POST',
      body: { bibId, barcode }
    });

    display.classList.remove('hidden', 'alert-danger', 'alert-warning');
    display.classList.add('alert-info');

    let infoText = (data.title || 'No title') + (data.author ? ' by ' + data.author : '');

    // Update title and author fields if they don't match the bib data
    const titleInput = document.getElementById('edit-title');
    const authorInput = document.getElementById('edit-author');

    if (data.title) {
      const oldTitle = titleInput.value.trim();
      const pTitle = data.title.trim();
      if (pTitle && oldTitle !== pTitle && oldTitle.indexOf(pTitle + " (") !== 0) {
        titleInput.value = pTitle + " (" + oldTitle + ")";
      }
    }

    if (data.author) {
      const oldAuthor = authorInput.value.trim();
      const pAuthor = data.author.trim();
      if (pAuthor && oldAuthor !== pAuthor && oldAuthor.indexOf(pAuthor + " (") !== 0) {
        authorInput.value = pAuthor + " (" + oldAuthor + ")";
      }
    }

    // Check for duplicate hold in Polaris
    if (data.patronHoldCheck && data.patronHoldCheck.statusValue === 29) {
      display.classList.remove('alert-info');
      display.classList.add('alert-warning');
      infoText = "DUPLICATE: Patron already has a hold on this item in Polaris. " + infoText;
    }

    text.textContent = infoText;
    setVerifiedBibId(bibId);
    window.dispatchEvent(new CustomEvent('asap-bib-verified', { detail: { bibId, rowId: document.getElementById('edit-id').value } }));
    return data;
  } catch (err) {
    display.classList.remove('hidden', 'alert-info');
    display.classList.add('alert-danger');
    text.textContent = 'Error: ' + err.message;
    setVerifiedBibId('');
    return null;
  } finally {
    if (btn) {
      btn.disabled = false;
      btn.textContent = options.doneText || originalButtonText || 'Lookup BIB';
    }
  }
}

function mergeCatalogValue(catalogValue, oldValue) {
  const catalog = String(catalogValue || '').trim();
  const old = String(oldValue || '').trim();

  if (!catalog) return old;
  if (!old || old === catalog) return catalog;
  if (old.indexOf(catalog + ' (') === 0) return old;

  const oldBase = old.replace(/\s+\([^()]*\)\s*$/, '').trim();
  if (oldBase && (oldBase === catalog || oldBase.indexOf(catalog) === 0 || catalog.indexOf(oldBase) === 0)) {
    return old;
  }

  return `${catalog} (${old})`;
}

function setHiddenEditValue(id, value) {
  let input = document.getElementById(id);
  if (!input) {
    const editForm = document.getElementById('edit-form') || document.getElementById('editForm');
    if (!editForm) return;
    input = document.createElement('input');
    input.type = 'hidden';
    input.id = id;
    input.name = id;
    editForm.appendChild(input);
  }
  input.value = String(value || '').trim();
}

export function applySelectedPolarisResultToEditForm(result = {}, context = 'edit') {
  const bibId = String(result.bibId || '').trim();
  const title = String(result.title || '').trim();
  const author = String(result.author || '').trim();
  const identifier = String(result.identifier || '').trim();
  const publication = String(result.publication || '').trim();
  const format = String(result.format || '').trim();

  const bibInput = document.getElementById(`${context}-bibid`);
  const titleInput = document.getElementById(`${context}-title`);
  const authorInput = document.getElementById(`${context}-author`);
  const display = document.getElementById(`${context}-bib-info-display`) || document.getElementById('bib-info-display');
  const text = document.getElementById(`${context}-bib-info-text`) || document.getElementById('bib-info-text');

  if (bibInput) {
    bibInput.value = bibId;
    bibInput.dispatchEvent(new Event('input', { bubbles: true }));
  }

  if (titleInput && title) {
    titleInput.value = mergeCatalogValue(title, titleInput.value);
  }

  if (authorInput && author) {
    authorInput.value = mergeCatalogValue(author, authorInput.value);
  }

  // Update identifier if empty or if it's a new form
  const idInput = document.getElementById(`${context}-identifier`);
  if (idInput && (context === 'new' || !idInput.value.trim())) {
    idInput.value = identifier;
  }

  setHiddenEditValue('selectedPolarisBibId', bibId);
  setHiddenEditValue('selectedPolarisTitle', title);
  setHiddenEditValue('selectedPolarisAuthor', author);
  setHiddenEditValue('selectedPolarisIdentifier', identifier);
  setHiddenEditValue('selectedPolarisPublication', publication);
  setHiddenEditValue('selectedPolarisFormat', format);

  if (display && text) {
    display.classList.remove('hidden', 'alert-danger', 'alert-warning');
    display.classList.add('alert-info');

    const parts = [];
    if (title) parts.push(title);
    if (author) parts.push('by ' + author);

    text.textContent = parts.length
      ? parts.join(' ')
      : (bibId ? 'BIB ' + bibId + ' selected from Polaris search.' : 'Polaris result selected.');
  }

  if (bibId) {
    setVerifiedBibId(bibId);
    const rowId = document.getElementById('edit-id')?.value || '';
    window.dispatchEvent(new CustomEvent('asap-bib-verified', { detail: { bibId, rowId } }));
  }
}

const btnBibLookup = document.getElementById('btn-bib-lookup');
if (btnBibLookup) {
  btnBibLookup.addEventListener('click', async () => {
    await lookupEditBibById();
  });
}
