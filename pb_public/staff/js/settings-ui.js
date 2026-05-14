import { pb, formatMap, patronFormatKeys, patronFormatFields, defaultPatronFormatRules, currentSuggestions, publicationOptions, setPublicationOptions, defaultPublicationOptions, setVerifiedBibId } from './state.js';
import { isValidSmtpHost, validateSmtpHostField, markSettingsDirty } from './api.js';
import { showToast } from './dialogs.js';
import { escapeAttr } from './grid.js';

export function optionIdFromLabel(label, fallback = 'option') {
  const id = String(label || '').trim().toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '');
  return id || fallback;
}

function isByteArray(value) {
  return Array.isArray(value) && value.length > 0 && value.every(item => Number.isInteger(item) && item >= 0 && item <= 255);
}

function decodeByteArray(value) {
  if (!isByteArray(value)) return null;
  for (let i = 0; i < value.length; i += 1) {
    if ([9, 10, 13, 32].includes(value[i])) continue;
    if (value[i] !== 91 && value[i] !== 123) return null;
    break;
  }
  if (typeof TextDecoder !== 'undefined') {
    try {
      return new TextDecoder('utf-8').decode(new Uint8Array(value));
    } catch (err) {
      // Fall through to the small decoder below for older embedded browsers.
    }
  }
  let output = '';
  for (let i = 0; i < value.length;) {
    const b1 = value[i++];
    if (b1 < 0x80) {
      output += String.fromCharCode(b1);
    } else if (b1 >= 0xC2 && b1 < 0xE0 && i < value.length) {
      const b2 = value[i++];
      output += String.fromCharCode(((b1 & 0x1F) << 6) | (b2 & 0x3F));
    } else if (b1 >= 0xE0 && b1 < 0xF0 && i + 1 < value.length) {
      const b2 = value[i++];
      const b3 = value[i++];
      output += String.fromCharCode(((b1 & 0x0F) << 12) | ((b2 & 0x3F) << 6) | (b3 & 0x3F));
    } else if (b1 >= 0xF0 && b1 < 0xF5 && i + 2 < value.length) {
      const b2 = value[i++];
      const b3 = value[i++];
      const b4 = value[i++];
      let codePoint = ((b1 & 0x07) << 18) | ((b2 & 0x3F) << 12) | ((b3 & 0x3F) << 6) | (b4 & 0x3F);
      codePoint -= 0x10000;
      output += String.fromCharCode(0xD800 + (codePoint >> 10), 0xDC00 + (codePoint & 0x3FF));
    } else {
      output += '\uFFFD';
    }
  }
  return output;
}

export function normalizeOptionList(options, fallbackLabels) {
  const fallback = (fallbackLabels || []).map((label, index) => ({ id: optionIdFromLabel(label, `option_${index + 1}`), label, enabled: true, sortOrder: (index + 1) * 10 }));
  let raw = [];
  const decoded = decodeByteArray(options);
  if (decoded !== null) options = decoded;
  if (Array.isArray(options)) {
    raw = options;
  } else {
    const text = String(options || '').trim();
    if (text.charAt(0) === '[') {
      try {
        const parsed = JSON.parse(text);
        raw = Array.isArray(parsed) ? parsed : [];
      } catch (err) {
        raw = [];
      }
    } else {
      raw = String(options || '').split(/\r?\n/).map(label => ({ label }));
    }
  }
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
    if (['none', 'message', 'ebookMessage', 'eaudiobookMessage'].includes(behavior)) {
      normalized[format].messageBehavior = behavior;
    }
    normalized[format].message = String(incomingFormat.message || normalized[format].message || '').trim();

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

function getPatronFormatRuleSummary(rule) {
  if (rule.messageBehavior === 'message') return 'Shows custom message';
  if (rule.messageBehavior === 'ebookMessage') return 'Shows eBook message';
  if (rule.messageBehavior === 'eaudiobookMessage') return 'Shows eAudiobook message';

  const fields = Object.values(rule.fields || {});
  const shown = fields.filter(f => f.mode !== 'hidden').length;
  const hidden = fields.filter(f => f.mode === 'hidden').length;
  const required = fields.filter(f => f.mode === 'required').length;

  const parts = [];
  if (shown > 0) parts.push(`${shown} shown`);
  if (hidden > 0) parts.push(`${hidden} hidden`);
  if (required > 0) parts.push(`${required} required`);

  return parts.join(', ') || 'No fields configured';
}

export function renderPatronFormatRulesEditor(rules) {
  const editor = document.getElementById('format-rules-editor');
  if (!editor) return;

  const normalized = normalizePatronFormatRules(rules);
  // Show rules for all formats currently in formatMap
  const formatKeys = Object.keys(formatMap);

  editor.className = 'asap-accordion';
  editor.replaceChildren();

  formatKeys.forEach(format => {
    const rule = normalized[format] || { messageBehavior: 'none', fields: {} };
    const summaryText = getPatronFormatRuleSummary(rule);

    const item = document.createElement('div');
    item.className = 'asap-accordion-item';
    item.setAttribute('data-format', format);
    item.setAttribute('data-behavior', rule.messageBehavior);

    const header = document.createElement('button');
    header.type = 'button';
    header.className = 'asap-accordion-header';
    header.setAttribute('aria-expanded', 'false');
    header.setAttribute('aria-controls', `panel-format-${format}`);

    const title = document.createElement('span');
    title.className = 'asap-accordion-title';
    title.textContent = formatMap[format] || format;

    const summary = document.createElement('span');
    summary.className = 'asap-accordion-summary';
    summary.textContent = summaryText;

    const chevron = document.createElement('i');
    chevron.className = 'fa fa-chevron-down asap-accordion-chevron';

    header.append(title, summary, chevron);

    const panel = document.createElement('div');
    panel.id = `panel-format-${format}`;
    panel.className = 'asap-accordion-panel';
    panel.setAttribute('role', 'region');

    const isEcontent = format === 'ebook' || format === 'eaudiobook';

    // Behavior select group
    const behaviorGroup = document.createElement('div');
    behaviorGroup.className = 'p-3 border-bottom bg-light';
    const behaviorForm = document.createElement('div');
    behaviorForm.className = 'form-inline';
    const behaviorLabel = document.createElement('label');
    behaviorLabel.className = 'small text-muted mr-3';
    behaviorLabel.setAttribute('for', `format-rule-message-${format}`);
    behaviorLabel.textContent = 'Message behavior';

    if (isEcontent) {
      const behaviorText = document.createElement('span');
      behaviorText.className = 'badge badge-info py-2 px-3';
      behaviorText.textContent = rule.messageBehavior === 'ebookMessage' ? 'Show eBook message' : 
                                rule.messageBehavior === 'eaudiobookMessage' ? 'Show eAudiobook message' : 'Show custom message';
      
      const lockedNote = document.createElement('div');
      lockedNote.className = 'small text-muted ml-2 d-inline-block';
      lockedNote.textContent = '(Required for this format)';
      behaviorForm.append(behaviorLabel, behaviorText, lockedNote);
    } else {
      const behaviorSelect = document.createElement('select');
      behaviorSelect.id = `format-rule-message-${format}`;
      behaviorSelect.className = 'form-control form-control-sm format-rule-message';
      behaviorSelect.setAttribute('data-format', format);
      
      const behaviors = [
        ['none', 'Show fields and allow submission'],
        ['message', 'Show custom message only']
      ];
      
      // Add legacy options if they are already selected
      if (rule.messageBehavior === 'ebookMessage') behaviors.push(['ebookMessage', 'Show eBook message']);
      if (rule.messageBehavior === 'eaudiobookMessage') behaviors.push(['eaudiobookMessage', 'Show eAudiobook message']);

      behaviors.forEach(([val, label]) => {
        const opt = new Option(label, val);
        opt.selected = rule.messageBehavior === val;
        behaviorSelect.add(opt);
      });

      behaviorForm.append(behaviorLabel, behaviorSelect);
    }
    behaviorGroup.appendChild(behaviorForm);

    // Message Editor
    const messageWrap = document.createElement('div');
    messageWrap.className = 'p-3 border-bottom' + (rule.messageBehavior === 'none' ? ' hidden' : '');
    messageWrap.id = `format-message-wrap-${format}`;
    
    const messageLabel = document.createElement('label');
    messageLabel.className = 'small text-muted d-block mb-2';
    messageLabel.textContent = 'Custom Message (Supports HTML)';
    
    const messageArea = document.createElement('textarea');
    messageArea.className = 'form-control form-control-sm format-rule-custom-message';
    messageArea.setAttribute('data-format', format);
    messageArea.rows = 4;
    messageArea.value = rule.message || '';
    
    const messageHint = document.createElement('small');
    messageHint.className = 'form-text text-muted mt-2';
    messageHint.innerHTML = 'Available placeholders: <code>{{barcode_label}}</code>, <code>{{pin_label}}</code>.';

    messageWrap.append(messageLabel, messageArea, messageHint);

    // Table
    const tableWrap = document.createElement('div');
    tableWrap.className = 'table-responsive';
    const table = document.createElement('table');
    table.className = 'table table-sm mb-0';

    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    ['Canonical Field', 'Mode', 'Patron Label'].forEach(txt => {
      const th = document.createElement('th');
      th.textContent = txt;
      headRow.appendChild(th);
    });
    thead.appendChild(headRow);

    const tbody = document.createElement('tbody');
    patronFormatFields.forEach(fieldInfo => {
      const field = (rule.fields || {})[fieldInfo.key] || { mode: fieldInfo.key === 'title' ? 'required' : 'optional', label: fieldInfo.label };
      const titleLocked = fieldInfo.key === 'title';

      const tr = document.createElement('tr');

      const col1 = document.createElement('td');
      const strong = document.createElement('strong');
      strong.textContent = fieldInfo.label;
      const storage = document.createElement('div');
      storage.className = 'small text-muted';
      storage.innerHTML = `Saves to <code>${escapeAttr(fieldInfo.storage)}</code>`;
      col1.append(strong, storage);

      const col2 = document.createElement('td');
      col2.className = 'format-rule-mode-cell';
      const modeSelect = document.createElement('select');
      modeSelect.className = 'form-control form-control-sm format-rule-mode';
      modeSelect.setAttribute('data-format', format);
      modeSelect.setAttribute('data-field', fieldInfo.key);
      if (titleLocked) modeSelect.disabled = true;

      ['required', 'optional', 'hidden'].forEach(m => {
        const opt = new Option(m.charAt(0).toUpperCase() + m.slice(1), m);
        opt.selected = field.mode === m;
        modeSelect.add(opt);
      });
      col2.appendChild(modeSelect);
      if (titleLocked) {
        const lockedNote = document.createElement('div');
        lockedNote.className = 'small text-muted';
        lockedNote.textContent = 'Title is always required.';
        col2.appendChild(lockedNote);
      }

      const col3 = document.createElement('td');
      const labelInput = document.createElement('input');
      labelInput.type = 'text';
      labelInput.className = 'form-control form-control-sm format-rule-label';
      labelInput.setAttribute('data-format', format);
      labelInput.setAttribute('data-field', fieldInfo.key);
      labelInput.value = field.label;
      col3.appendChild(labelInput);

      tr.append(col1, col2, col3);
      tbody.appendChild(tr);
    });

    table.append(thead, tbody);
    tableWrap.appendChild(table);
    if (rule.messageBehavior !== 'none') tableWrap.classList.add('hidden');
    tableWrap.id = `format-table-wrap-${format}`;

    panel.append(behaviorGroup, messageWrap, tableWrap);
    item.append(header, panel);
    editor.appendChild(item);
  });
}

export function collectPatronFormatRules() {
  const rules = normalizePatronFormatRules(defaultPatronFormatRules);
  const editor = document.getElementById('format-rules-editor');
  if (!editor) return rules;

  editor.querySelectorAll('.asap-accordion-item').forEach(item => {
    const format = item.getAttribute('data-format');
    const behavior = item.getAttribute('data-behavior') || 'none';
    if (!rules[format]) {
      rules[format] = { messageBehavior: behavior, message: '', fields: {} };
      patronFormatFields.forEach(f => { rules[format].fields[f.key] = { mode: f.key === 'title' ? 'required' : 'optional', label: f.label }; });
    } else {
      rules[format].messageBehavior = behavior;
    }
  });

  editor.querySelectorAll('.format-rule-custom-message').forEach(textarea => {
    const format = textarea.getAttribute('data-format');
    if (rules[format]) {
      rules[format].message = textarea.value.trim();
    }
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

// Update accordion summary when rules change
document.addEventListener('input', (e) => {
  const target = e.target;
  if (target.classList.contains('format-rule-mode') || target.classList.contains('format-rule-label') || target.classList.contains('format-rule-custom-message')) {
    const format = target.getAttribute('data-format');
    if (!format) return;
    
    const item = document.querySelector(`.asap-accordion-item[data-format="${format}"]`);
    if (!item) return;

    const summary = item.querySelector('.asap-accordion-summary');
    if (!summary) return;

    // Collect current state for this format to compute summary
    const rule = { messageBehavior: item.getAttribute('data-behavior') || 'none', message: '', fields: {} };
    
    const messageArea = item.querySelector('.format-rule-custom-message');
    if (messageArea) rule.message = messageArea.value;

    const modes = item.querySelectorAll(`.format-rule-mode[data-format="${format}"]`);
    modes.forEach(sel => {
      const field = sel.getAttribute('data-field');
      rule.fields[field] = { mode: sel.value };
    });

    summary.textContent = getPatronFormatRuleSummary(rule);
  }
});

document.addEventListener('change', (e) => {
  const target = e.target;
  if (target.classList.contains('format-rule-message')) {
    const format = target.getAttribute('data-format');
    if (!format) return;
    
    const item = document.querySelector(`.asap-accordion-item[data-format="${format}"]`);
    if (!item) return;

    const summary = item.querySelector('.asap-accordion-summary');
    if (!summary) return;

    item.setAttribute('data-behavior', target.value);
    const rule = { messageBehavior: target.value, message: '', fields: {} };
    const messageArea = item.querySelector('.format-rule-custom-message');
    if (messageArea) rule.message = messageArea.value;

    summary.textContent = getPatronFormatRuleSummary(rule);

    // Toggle visibility
    const messageWrap = document.getElementById(`format-message-wrap-${format}`);
    const tableWrap = document.getElementById(`format-table-wrap-${format}`);
    if (messageWrap) messageWrap.classList.toggle('hidden', target.value === 'none');
    if (tableWrap) tableWrap.classList.toggle('hidden', target.value !== 'none');
  }
});

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
  const fallback = defaultPublicationOptions;
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
      const fallback = defaultPublicationOptions;
      const list = collectOptionList(editor.id, fallback);
      list.forEach((option, nextIndex) => option.sortOrder = (nextIndex + 1) * 10);
      renderOptionListEditor(editor.id, list, fallback);
      markSettingsDirty();
    }
  }
});

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

document.getElementById('btn-bib-lookup').addEventListener('click', async () => {
  await lookupEditBibById();
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
