import { pb, formatMap, patronFormatKeys, patronFormatFields, defaultPatronFormatRules, currentSuggestions, verifiedBibId, publicationOptions, setPublicationOptions, defaultPublicationOptions, defaultAgeGroups, setVerifiedBibId } from './state.js';
import { isValidSmtpHost, validateSmtpHostField, showToast, markSettingsDirty } from './api.js';
import { escapeAttr } from './grid.js';

export function normalizePublicationOptions(options) {
  const raw = Array.isArray(options) ? options : String(options || '').split(/\r?\n/);
  const cleaned = raw.map(option => String(option && typeof option === 'object' ? option.label : option || '').trim()).filter(Boolean);
  return cleaned.length ? Array.from(new Set(cleaned)) : defaultPublicationOptions.slice();
}

export function normalizeAgeGroups(options) {
  const raw = Array.isArray(options) ? options : String(options || '').split(/\r?\n/);
  const cleaned = raw.map(option => String(option && typeof option === 'object' ? option.label : option || '').trim()).filter(Boolean);
  return cleaned.length ? Array.from(new Set(cleaned)) : defaultAgeGroups.slice();
}

export function optionIdFromLabel(label, fallback = 'option') {
  const id = String(label || '').trim().toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '');
  return id || fallback;
}

export function normalizeOptionList(options, fallbackLabels) {
  const fallback = (fallbackLabels || []).map((label, index) => ({ id: optionIdFromLabel(label, `option_${index + 1}`), label, enabled: true, sortOrder: (index + 1) * 10 }));
  let raw = [];
  if (Array.isArray(options)) raw = options;
  else raw = String(options || '').split(/\r?\n/).map(label => ({ label }));
  const seenLabels = new Set();
  const seenIds = new Set();
  const normalized = [];
  raw.forEach((item, index) => {
    const obj = item && typeof item === 'object' ? item : { label: item };
    const label = String(obj.label || obj.value || obj.name || '').trim();
    if (!label || seenLabels.has(label.toLowerCase())) return;
    seenLabels.add(label.toLowerCase());
    let id = String(obj.id || '').trim() || optionIdFromLabel(label, `option_${index + 1}`);
    const baseId = id;
    let suffix = 2;
    while (seenIds.has(id)) id = `${baseId}_${suffix++}`;
    seenIds.add(id);
    normalized.push({ id, label, enabled: obj.enabled !== false, sortOrder: Number(obj.sortOrder || ((index + 1) * 10)) });
  });
  normalized.sort((a, b) => (a.sortOrder || 0) - (b.sortOrder || 0));
  return normalized.length ? normalized : fallback;
}

export function enabledOptionLabels(options, fallbackLabels) {
  return normalizeOptionList(options, fallbackLabels).filter(option => option.enabled !== false).map(option => option.label);
}

export function updatePublicationOptionsUi(options) {
  const normalized = enabledOptionLabels(options, defaultPublicationOptions);
  document.querySelectorAll('.publication-options-select').forEach(select => {
    const val = select.value || normalized[0];
    select.innerHTML = '';
    normalized.forEach(opt => {
      const el = document.createElement('option');
      el.value = opt;
      el.textContent = opt;
      select.appendChild(el);
    });
    select.value = normalized.includes(val) ? val : normalized[0];
  });
}


export function setAgeGroups(options) {
  const normalized = enabledOptionLabels(options, defaultAgeGroups);
  ['edit-age', 'new-age'].forEach(id => {
    const select = document.getElementById(id);
    if (!select) return;
    const val = select.value || normalized[0];
    select.innerHTML = '';
    normalized.forEach(opt => {
      const el = document.createElement('option');
      el.value = opt;
      el.textContent = opt;
      select.appendChild(el);
    });
    if (val && !normalized.includes(val)) {
      const el = document.createElement('option');
      el.value = val;
      el.textContent = val;
      select.appendChild(el);
    }
    select.value = val || normalized[0] || '';
  });
}

export function normalizePatronFormatRules(rules) {
  const normalized = structuredClone(defaultPatronFormatRules);
  const incoming = rules && typeof rules === 'object' ? rules : {};

  // Build the full list of format keys: defaults + any custom formats in formatMap + any in incoming rules
  const allKeys = new Set([...patronFormatKeys, ...Object.keys(formatMap), ...Object.keys(incoming)]);

  allKeys.forEach(format => {
    // Ensure a base entry exists for custom formats
    if (!normalized[format]) {
      normalized[format] = {
        messageBehavior: 'none',
        fields: {}
      };
      patronFormatFields.forEach(fieldInfo => {
        normalized[format].fields[fieldInfo.key] = {
          mode: fieldInfo.key === 'title' ? 'required' : (fieldInfo.key === 'identifier' ? 'optional' : 'required'),
          label: fieldInfo.label
        };
      });
    }

    const incomingFormat = incoming[format] || {};
    const behavior = String(incomingFormat.messageBehavior || '').trim();
    if (['none', 'ebookMessage', 'eaudiobookMessage'].includes(behavior)) {
      normalized[format].messageBehavior = behavior;
    }

    const incomingFields = incomingFormat.fields || {};
    patronFormatFields.forEach(fieldInfo => {
      const field = fieldInfo.key;
      const incomingField = incomingFields[field] || {};
      const defaultField = normalized[format].fields[field];
      let mode = String(incomingField.mode || defaultField.mode || 'optional').trim();
      if (!['required', 'optional', 'hidden'].includes(mode)) mode = defaultField.mode || 'optional';
      if (field === 'title') mode = 'required';
      normalized[format].fields[field] = {
        mode,
        label: String(incomingField.label || defaultField.label || fieldInfo.label).trim() || defaultField.label || fieldInfo.label
      };
    });
  });

  return normalized;
}

export function renderPatronFormatRulesEditor(rules) {
  const editor = document.getElementById('format-rules-editor');
  if (!editor) return;

  const normalized = normalizePatronFormatRules(rules);
  // Show rules for all formats currently in formatMap
  const formatKeys = Object.keys(formatMap);
  editor.innerHTML = formatKeys.map(format => {
    const rule = normalized[format] || { messageBehavior: 'none', fields: {} };
    const rows = patronFormatFields.map(fieldInfo => {
      const field = (rule.fields || {})[fieldInfo.key] || { mode: fieldInfo.key === 'title' ? 'required' : 'optional', label: fieldInfo.label };
      const titleLocked = fieldInfo.key === 'title';
      return `
        <tr>
          <td>
            <strong>${escapeAttr(fieldInfo.label)}</strong>
            <div class="small text-muted">Saves to <code>${escapeAttr(fieldInfo.storage)}</code></div>
          </td>
          <td class="format-rule-mode-cell">
            <select class="form-control form-control-sm format-rule-mode" data-format="${escapeAttr(format)}" data-field="${escapeAttr(fieldInfo.key)}"${titleLocked ? ' disabled' : ''}>
              <option value="required"${field.mode === 'required' ? ' selected' : ''}>Required</option>
              <option value="optional"${field.mode === 'optional' ? ' selected' : ''}>Optional</option>
              <option value="hidden"${field.mode === 'hidden' ? ' selected' : ''}>Hidden</option>
            </select>
            ${titleLocked ? '<div class="small text-muted">Title is always required.</div>' : ''}
          </td>
          <td>
            <input type="text" class="form-control form-control-sm format-rule-label" data-format="${escapeAttr(format)}" data-field="${escapeAttr(fieldInfo.key)}" value="${escapeAttr(field.label)}">
          </td>
        </tr>
      `;
    }).join('');

    return `
      <div class="card mb-3">
        <div class="card-header d-flex flex-wrap justify-content-between align-items-center">
          <strong>${escapeAttr(formatMap[format] || format)}</strong>
          <div class="form-inline mt-2 mt-md-0">
            <label class="small text-muted mr-2" for="format-rule-message-${escapeAttr(format)}">Message behavior</label>
            <select id="format-rule-message-${escapeAttr(format)}" class="form-control form-control-sm format-rule-message" data-format="${escapeAttr(format)}">
              <option value="none"${rule.messageBehavior === 'none' ? ' selected' : ''}>Show fields and allow submission</option>
              <option value="ebookMessage"${rule.messageBehavior === 'ebookMessage' ? ' selected' : ''}>Show eBook message only</option>
              <option value="eaudiobookMessage"${rule.messageBehavior === 'eaudiobookMessage' ? ' selected' : ''}>Show eAudiobook message only</option>
            </select>
          </div>
        </div>
        <div class="table-responsive">
          <table class="table table-sm mb-0">
            <thead>
              <tr>
                <th>Canonical Field</th>
                <th>Mode</th>
                <th>Patron Label</th>
              </tr>
            </thead>
            <tbody>${rows}</tbody>
          </table>
        </div>
      </div>
    `;
  }).join('');
}

export function collectPatronFormatRules() {
  const rules = normalizePatronFormatRules(defaultPatronFormatRules);
  const editor = document.getElementById('format-rules-editor');
  if (!editor) return rules;

  editor.querySelectorAll('.format-rule-message').forEach(select => {
    const format = select.getAttribute('data-format');
    if (!rules[format]) {
      rules[format] = { messageBehavior: 'none', fields: {} };
      patronFormatFields.forEach(f => { rules[format].fields[f.key] = { mode: f.key === 'title' ? 'required' : 'optional', label: f.label }; });
    }
    rules[format].messageBehavior = select.value;
  });

  editor.querySelectorAll('.format-rule-mode').forEach(select => {
    const format = select.getAttribute('data-format');
    const field = select.getAttribute('data-field');
    if (rules[format] && rules[format].fields[field]) {
      rules[format].fields[field].mode = field === 'title' ? 'required' : select.value;
    }
  });

  editor.querySelectorAll('.format-rule-label').forEach(input => {
    const format = input.getAttribute('data-format');
    const field = input.getAttribute('data-field');
    if (rules[format] && rules[format].fields[field]) {
      rules[format].fields[field].label = input.value.trim() || rules[format].fields[field].label;
    }
  });

  return rules;
}

export function populatePublicationSelect(select, selectedValue) {
  if (!select) return;
  const selected = selectedValue || select.value || publicationOptions[0];
  select.innerHTML = '';
  publicationOptions.forEach(option => {
    const item = document.createElement('option');
    item.value = option;
    item.textContent = option;
    select.appendChild(item);
  });
  setSelectValue(select, selected);
}

export function setSelectValue(select, value) {
  if (!select) return;
  value = String(value || '').trim();
  if (value && !publicationOptions.includes(value)) {
    const item = document.createElement('option');
    item.value = value;
    item.textContent = value;
    select.appendChild(item);
  }
  select.value = value || publicationOptions[0] || '';
}

export function renderOptionListEditor(editorId, options, fallbackLabels) {
  const editor = document.getElementById(editorId);
  if (!editor) return;
  const list = normalizeOptionList(options, fallbackLabels);
  editor.innerHTML = list.map((option, index) => `
    <div class="option-list-row" data-option-id="${escapeAttr(option.id)}" draggable="true">
      <div class="text-muted option-drag-handle" aria-label="Drag to reorder" tabindex="0" title="Drag to reorder">&#8597;</div>
      <input type="text" class="form-control form-control-sm option-list-label" value="${escapeAttr(option.label)}" aria-label="Option label">
      <div class="form-check mb-0">
        <input type="checkbox" class="form-check-input option-list-enabled" id="${escapeAttr(editorId)}-${escapeAttr(option.id)}"${option.enabled !== false ? ' checked' : ''}>
        <label class="form-check-label small" for="${escapeAttr(editorId)}-${escapeAttr(option.id)}">Enabled</label>
      </div>
      <button type="button" class="btn btn-sm btn-outline-danger option-list-delete" aria-label="Delete option">Delete</button>
    </div>
  `).join('');
}

export function collectOptionList(editorId, fallbackLabels) {
  const editor = document.getElementById(editorId);
  if (!editor) return normalizeOptionList([], fallbackLabels);
  const seen = new Set();
  const rows = Array.from(editor.querySelectorAll('.option-list-row'));
  const options = rows.map((row, index) => {
    const input = row.querySelector('.option-list-label');
    const label = input ? input.value.trim() : '';
    if (!label) return null;
    const key = label.toLowerCase();
    if (seen.has(key)) throw new Error('Option labels must be unique within each list.');
    seen.add(key);
    const existingId = row.getAttribute('data-option-id') || '';
    return {
      id: existingId || optionIdFromLabel(label, `option_${index + 1}`),
      label,
      enabled: !!row.querySelector('.option-list-enabled')?.checked,
      sortOrder: (index + 1) * 10
    };
  }).filter(Boolean);
  if (!options.length) throw new Error('Each option list must include at least one label.');
  return options;
}

export function addOptionListRow(editorId, fallbackLabels) {
  const current = collectOptionList(editorId, fallbackLabels);
  let base = 'New option';
  let label = base;
  let i = 2;
  const labels = new Set(current.map(option => option.label.toLowerCase()));
  while (labels.has(label.toLowerCase())) label = `${base} ${i++}`;
  current.push({ id: optionIdFromLabel(label, `option_${current.length + 1}`), label, enabled: true, sortOrder: (current.length + 1) * 10 });
  renderOptionListEditor(editorId, current, fallbackLabels);
  markSettingsDirty();
}

export function handleOptionListClick(event) {
  const button = event.target.closest('button');
  if (!button) return;
  const editor = event.target.closest('.option-list-editor');
  if (!editor) return;
  const row = button.closest('.option-list-row');
  if (!row) return;
  const fallback = editor.id === 'ui-age-groups-editor' ? defaultAgeGroups : defaultPublicationOptions;
  const list = collectOptionList(editor.id, fallback);
  const index = Array.from(editor.querySelectorAll('.option-list-row')).indexOf(row);
  if (button.classList.contains('option-list-delete')) {
    if (list.length <= 1) {
      showToast('Each option list must include at least one label.', 'error');
      return;
    }
    list.splice(index, 1);
  } else {
    return;
  }
  list.forEach((option, nextIndex) => option.sortOrder = (nextIndex + 1) * 10);
  renderOptionListEditor(editor.id, list, fallback);
  markSettingsDirty();
}

let optionDraggingRow = null;

document.addEventListener('dragstart', (e) => {
  const row = e.target.closest('.option-list-row');
  if (row && e.target.closest('.option-list-editor')) {
    optionDraggingRow = row;
    row.classList.add('option-row-dragging');
    e.dataTransfer.effectAllowed = 'move';
  }
});

document.addEventListener('dragend', (e) => {
  if (optionDraggingRow) {
    optionDraggingRow.classList.remove('option-row-dragging');
    const editor = optionDraggingRow.closest('.option-list-editor');
    if (editor) {
      editor.querySelectorAll('.option-list-row').forEach(r => r.classList.remove('option-row-drop-target'));
    }
    optionDraggingRow = null;
  }
});

document.addEventListener('dragover', (e) => {
  const editor = e.target.closest('.option-list-editor');
  if (editor && optionDraggingRow && editor.contains(optionDraggingRow)) {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    const target = e.target.closest('.option-list-row');
    if (target && target !== optionDraggingRow) {
      editor.querySelectorAll('.option-list-row').forEach(r => r.classList.remove('option-row-drop-target'));
      target.classList.add('option-row-drop-target');
    }
  }
});

document.addEventListener('drop', (e) => {
  const editor = e.target.closest('.option-list-editor');
  if (editor && optionDraggingRow && editor.contains(optionDraggingRow)) {
    e.preventDefault();
    const target = e.target.closest('.option-list-row');
    editor.querySelectorAll('.option-list-row').forEach(r => r.classList.remove('option-row-drop-target'));
    if (target && target !== optionDraggingRow) {
      editor.insertBefore(optionDraggingRow, target);
      const fallback = editor.id === 'ui-age-groups-editor' ? defaultAgeGroups : defaultPublicationOptions;
      const list = collectOptionList(editor.id, fallback);
      list.forEach((option, nextIndex) => option.sortOrder = (nextIndex + 1) * 10);
      renderOptionListEditor(editor.id, list, fallback);
      markSettingsDirty();
    }
  }
});

document.getElementById('btn-bib-lookup').addEventListener('click', async () => {
  const bibId = document.getElementById('edit-bibid').value.trim();
  const btn = document.getElementById('btn-bib-lookup');
  const display = document.getElementById('bib-info-display');
  const text = document.getElementById('bib-info-text');

  if (!bibId) {
    display.classList.remove('hidden', 'alert-info');
    display.classList.add('alert-danger');
    text.textContent = 'Please enter a BIB ID first.';
    return;
  }

  btn.disabled = true;
  btn.textContent = '...';
  display.classList.add('hidden');

  try {
    const row = currentSuggestions.find(r => r.id === document.getElementById('edit-id').value);
    const barcode = row ? row.barcode : '';

    const res = await fetch('/api/asap/staff/bib-lookup', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': pb.authStore.token
      },
      body: JSON.stringify({ bibId, barcode })
    });
    const data = await res.json();
    if (!res.ok) throw new Error(data.message || 'Lookup failed');

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
  } catch (err) {
    display.classList.remove('hidden', 'alert-info');
    display.classList.add('alert-danger');
    text.textContent = 'Error: ' + err.message;
    setVerifiedBibId('');
  } finally {
    btn.disabled = false;
    btn.textContent = 'Lookup BIB';
  }
});

export function dateOnly(value) {
  value = String(value || '').trim();
  return value ? value.split(' ')[0].split('T')[0] : '';
}

export function syncInputPair(idA, idB) {
  const elA = document.getElementById(idA);
  const elB = document.getElementById(idB);
  if (elA && elB) {
    elA.addEventListener('input', (e) => elB.value = e.target.value);
    elB.addEventListener('input', (e) => elA.value = e.target.value);
  }
}
syncInputPair('email-from-address', 'smtp-from');
syncInputPair('email-from-name', 'smtp-from-name');
const smtpHostInput = document.getElementById('smtp-host');
if (smtpHostInput) {
  smtpHostInput.addEventListener('blur', () => validateSmtpHostField(true));
  smtpHostInput.addEventListener('input', () => {
    if (isValidSmtpHost(smtpHostInput.value) || !smtpHostInput.value.trim()) {
      const resultEl = document.getElementById('smtp-test-result');
      if (resultEl && resultEl.className.includes('text-danger') && resultEl.textContent.includes('SMTP host')) {
        resultEl.textContent = '';
        resultEl.className = 'd-block mt-2';
      }
    }
  });
}
