import { pb, verifiedNewSuggestionBarcode, setVerifiedNewSuggestionBarcode } from './state.js';
import { setFieldChecked, getFieldChecked } from './api.js';
import { loadTab, escapeAttr } from './grid.js';
import { renderPatronContext } from './modals.js';

document.getElementById('btn-new-suggestion').addEventListener('click', () => {
  document.getElementById('new-suggestion-form').reset();
  setFieldChecked('new-autohold', true);
  setFieldChecked('staff-new-suggestion-email-patron', false);
  document.getElementById('new-exact-publication-date').value = '';
  clearNewSuggestionError();
  resetStaffPatronLookup();
  document.getElementById('newSuggestionModal').showModal();
  document.getElementById('close-new-modal-btn').focus();
  document.getElementById('new-barcode').focus();
});

document.getElementById('close-new-modal-x').addEventListener('click', () => {
  document.getElementById('newSuggestionModal').close();
});
document.getElementById('close-new-modal-btn').addEventListener('click', () => {
  document.getElementById('newSuggestionModal').close();
});
document.getElementById('close-patron-search-x').addEventListener('click', () => {
  document.getElementById('patronSearchDialog').close();
});
document.getElementById('close-patron-search-btn').addEventListener('click', () => {
  document.getElementById('patronSearchDialog').close();
});

document.getElementById('new-barcode').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') {
    e.preventDefault();
    document.getElementById('btn-lookup-patron').click();
  }
});

document.getElementById('new-barcode').addEventListener('input', () => {
  const patronQuery = document.getElementById('new-barcode').value.trim();
  if (verifiedNewSuggestionBarcode && patronQuery !== verifiedNewSuggestionBarcode) {
    resetStaffPatronLookup();
    showLookupResult('Patron lookup changed. Look up the patron again before entering suggestion details.', 'warning');
  }
});

document.getElementById('btn-lookup-patron').addEventListener('click', async () => {
  const patronQuery = document.getElementById('new-barcode').value.trim();
  const btn = document.getElementById('btn-lookup-patron');
  clearNewSuggestionError();
  resetStaffPatronLookup();

  if (!patronQuery) {
    showLookupResult('Enter a patron barcode or name before lookup.', 'danger');
    document.getElementById('new-barcode').focus();
    return;
  }

  btn.disabled = true;
  btn.textContent = 'Looking up...';
  try {
    const res = await fetch('/api/asap/staff/patron-lookup', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify({ query: patronQuery })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      throw new Error(data.message || 'No patron found. Try barcode, or first name then last name.');
    }

    if (data.status === 'multiple' && Array.isArray(data.results)) {
      openPatronSearchDialog(patronQuery, data.results);
      return;
    }

    applySelectedPatron(data);
  } catch (err) {
    showLookupResult(err.message || 'No patron found. Try barcode, or first name then last name.', 'danger');
    document.getElementById('new-barcode').focus();
  } finally {
    btn.disabled = false;
    btn.textContent = 'Lookup Patron';
  }
});

document.getElementById('new-suggestion-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const barcode = document.getElementById('new-barcode').value.trim();
  if (!verifiedNewSuggestionBarcode || barcode !== verifiedNewSuggestionBarcode) {
    showNewSuggestionError('Look up and verify the patron before submitting a suggestion.');
    document.getElementById('new-barcode').focus();
    return;
  }

  const payload = {
    barcode: barcode,
    title: document.getElementById('new-title').value,
    author: document.getElementById('new-author').value,
    identifier: document.getElementById('new-identifier').value,
    format: document.getElementById('new-format').value,
    publication: document.getElementById('new-publication').value,
    exactPublicationDate: document.getElementById('new-exact-publication-date').value,
    notes: document.getElementById('new-notes').value,
    autohold: getFieldChecked('new-autohold'),
    emailPatronConfirmation: getFieldChecked('staff-new-suggestion-email-patron')
  };

  clearNewSuggestionError();
  const btn = document.getElementById('btn-submit-new');
  btn.disabled = true;
  btn.textContent = 'Submitting...';

  try {
    const res = await fetch('/api/asap/staff/suggestions', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify(payload)
    });

    if (!res.ok) {
      const data = await res.json();
      throw new Error(data.message || 'Failed to create suggestion');
    }

    document.getElementById('newSuggestionModal').close();
    loadTab('suggestion');
  } catch (err) {
    showNewSuggestionError(err.message || 'Failed to create suggestion');
  } finally {
    btn.disabled = false;
    btn.textContent = 'Submit';
  }
});

function applySelectedPatron(data) {
  const barcode = String(data.barcode || '').trim();
  if (!barcode) {
    showLookupResult('Could not verify selected patron.', 'danger');
    document.getElementById('new-barcode').focus();
    return;
  }

  setVerifiedNewSuggestionBarcode(barcode);
  document.getElementById('new-barcode').value = barcode;
  setNewSuggestionDetailsEnabled(true);

  // Match the layout of the edit modal, but expanded by default
  renderPatronContext(data, {
    containerSelector: '#newSuggestionModal .asap-dialog-edit-body',
    blockId: 'new-patron-context',
    expanded: true,
    anchorSelector: '#new-suggestion-details',
    insertAfter: false
  });

  // Hide the old simple lookup result
  const oldResult = document.getElementById('new-lookup-result');
  if (oldResult) {
    oldResult.classList.add('hidden');
    oldResult.textContent = '';
  }

  document.getElementById('new-title').focus();
}

function patronSearchElements() {
  return {
    dialog: document.getElementById('patronSearchDialog'),
    summary: document.getElementById('patron-search-summary'),
    status: document.getElementById('patron-search-status'),
    results: document.getElementById('patron-search-results')
  };
}

function openPatronSearchDialog(query, results) {
  const els = patronSearchElements();
  if (!els.dialog) return;

  els.summary.textContent = `Multiple patrons matched "${query}". Choose the correct patron.`;
  els.status.className = 'alert alert-light border py-2 px-3 small';
  els.status.textContent = `${results.length} result${results.length === 1 ? '' : 's'} shown.`;

  els.results.innerHTML = results.map((result, index) => {
    const name = patronLookupName(result) || result.name || 'Patron';
    const barcode = result.barcode || '';
    const library = result.libraryOrgName || 'Library not returned';
    return `
      <div class="polaris-search-result">
        <div class="polaris-search-result-title">${escapeAttr(name)}</div>
        <div class="polaris-search-result-meta">Barcode: ${escapeAttr(barcode)} | Library: ${escapeAttr(library)}</div>
        <div class="polaris-search-result-actions">
          <button type="button" class="btn btn-sm btn-primary patron-search-select" data-result-index="${escapeAttr(String(index))}">
            Use this patron
          </button>
        </div>
      </div>
    `;
  }).join('');

  els.results.querySelectorAll('.patron-search-select').forEach(button => {
    button.addEventListener('click', () => {
      const index = parseInt(button.getAttribute('data-result-index') || '-1', 10);
      const result = results[index];
      if (!result || !result.barcode) return;
      els.dialog.close();
      applySelectedPatron(result);
    });
  });

  if (!els.dialog.open) {
    els.dialog.showModal();
  }
}

export function resetStaffPatronLookup() {
  setVerifiedNewSuggestionBarcode('');
  clearNewSuggestionError();
  clearNewSuggestionDetails();
  setNewSuggestionDetailsEnabled(false);
  document.getElementById('new-lookup-result').className = 'mt-2 hidden';
  document.getElementById('new-lookup-result').textContent = '';
  const ctx = document.getElementById('new-patron-context');
  if (ctx) ctx.remove();
}

export function clearNewSuggestionDetails() {
  document.getElementById('new-title').value = '';
  document.getElementById('new-author').value = '';
  document.getElementById('new-identifier').value = '';
  document.getElementById('new-format').selectedIndex = 0;
  document.getElementById('new-publication').selectedIndex = 0;
  document.getElementById('new-exact-publication-date').value = '';
  document.getElementById('new-notes').value = '';
  setFieldChecked('new-autohold', true);
}

export function setNewSuggestionDetailsEnabled(enabled) {
  document.getElementById('new-suggestion-details').classList.toggle('hidden', !enabled);
  document.querySelectorAll('.new-detail-field').forEach(field => {
    field.disabled = !enabled;
  });
  document.getElementById('btn-submit-new').disabled = !enabled;
}

export function showLookupResult(message, type) {
  const result = document.getElementById('new-lookup-result');
  result.className = 'mt-2 alert alert-' + type + ' py-2';
  result.textContent = message;
}

export function clearNewSuggestionError() {
  const el = document.getElementById('new-error-summary');
  if (!el) return;
  el.textContent = '';
  el.classList.add('hidden');
}

export function showNewSuggestionError(message) {
  const text = String(message || 'Failed to create suggestion');
  const el = document.getElementById('new-error-summary');
  if (!el) return;
  el.textContent = text;
  el.classList.remove('hidden');
}

export function patronLookupName(data) {
  const name = [data.nameFirst, data.nameLast].filter(Boolean).join(' ').trim();
  return name || 'barcode found';
}

export async function openNewSuggestionForPatron(barcode) {
  if (!barcode) return;
  document.getElementById('new-suggestion-form').reset();
  setFieldChecked('new-autohold', true);
  setFieldChecked('staff-new-suggestion-email-patron', false);
  document.getElementById('new-exact-publication-date').value = '';
  clearNewSuggestionError();
  resetStaffPatronLookup();
  
  document.getElementById('new-barcode').value = barcode;
  document.getElementById('newSuggestionModal').showModal();
  document.getElementById('close-new-modal-btn').focus();
  
  // Trigger lookup automatically
  document.getElementById('btn-lookup-patron').click();
}
