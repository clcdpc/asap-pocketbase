import { formatMap, availableFormats, patronFormatKeys, patronFormatFields, defaultPatronFormatRules, additionalFieldDefinitions } from '../state.js';

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

    const incomingCustomFields = incomingFormat.customFields || {};
    normalized[format].customFields = {};
    additionalFieldDefinitions.forEach(def => {
      if (!def || !def.key) return;
      const incomingRule = incomingCustomFields[def.key] || {};
      let mode = String(incomingRule.mode || 'hidden').trim();
      if (!['required', 'optional', 'hidden'].includes(mode)) mode = 'hidden';
      if (def.enabled === false) mode = 'hidden';
      normalized[format].customFields[def.key] = { mode };
    });
  });

  return normalized;
}

export function getPatronFormatRuleSummary(rule) {
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

    const isHidden = !availableFormats.includes(format);
    const item = document.createElement('div');
    item.className = 'asap-accordion-item' + (isHidden ? ' asap-accordion-item-hidden' : '');
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
    summary.textContent = isHidden ? summaryText + ' (Hidden from patrons)' : summaryText;

    const chevron = document.createElement('i');
    chevron.className = 'fa fa-chevron-down asap-accordion-chevron';

    header.append(title, summary, chevron);

    const panel = document.createElement('div');
    panel.id = `panel-format-${format}`;
    panel.className = 'asap-accordion-panel';
    panel.setAttribute('role', 'region');

    if (isHidden) {
      const warning = document.createElement('div');
      warning.className = 'alert alert-warning format-hidden-warning';
      warning.innerHTML = '<i class="fa fa-exclamation-triangle mr-2"></i> This format is currently hidden from patrons. Changes made here will take effect once the format is enabled in the <strong>Format Display Names</strong> section above.';
      panel.appendChild(warning);
    }

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
      const storageLabel = document.createTextNode('Saves to ');
      const code = document.createElement('code');
      code.textContent = fieldInfo.storage;
      storage.append(storageLabel, code);
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

    const customFieldRules = rule.customFields || {};
    additionalFieldDefinitions.forEach(def => {
      if (!def || !def.key) return;
      const tr = document.createElement('tr');

      const nameTd = document.createElement('td');
      const strong = document.createElement('strong');
      strong.textContent = def.label || def.key;
      const storage = document.createElement('div');
      storage.className = 'small text-muted';
      storage.textContent = `Additional ${def.type || 'text'} field`;
      nameTd.append(strong, storage);

      const modeTd = document.createElement('td');
      const select = document.createElement('select');
      select.className = 'form-control form-control-sm format-rule-custom-field-mode';
      select.setAttribute('data-format', format);
      select.setAttribute('data-field', def.key);
      ['required', 'optional', 'hidden'].forEach(mode => select.appendChild(new Option(mode.charAt(0).toUpperCase() + mode.slice(1), mode)));
      select.value = (customFieldRules[def.key] && customFieldRules[def.key].mode) || 'hidden';
      modeTd.appendChild(select);

      const labelTd = document.createElement('td');
      const labelInput = document.createElement('input');
      labelInput.type = 'text';
      labelInput.className = 'form-control form-control-sm format-rule-custom-field-label';
      labelInput.setAttribute('data-format', format);
      labelInput.setAttribute('data-field', def.key);
      labelInput.value = (customFieldRules[def.key] && customFieldRules[def.key].label) || def.label || '';
      labelTd.appendChild(labelInput);
      tr.append(nameTd, modeTd, labelTd);
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

  editor.querySelectorAll('.format-rule-custom-field-mode').forEach(select => {
    const format = select.getAttribute('data-format');
    const field = select.getAttribute('data-field');
    if (!rules[format]) return;
    if (!rules[format].customFields) rules[format].customFields = {};
    const labelInput = editor.querySelector(`.format-rule-custom-field-label[data-format="${format}"][data-field="${field}"]`);
    const label = labelInput ? labelInput.value.trim() : '';
    rules[format].customFields[field] = { mode: select.value || 'hidden' };
    if (label) {
      rules[format].customFields[field].label = label;
    }
  });

  return rules;
}

// Update accordion summary when rules change
document.addEventListener('input', (e) => {
  const target = e.target;
  if (target.classList.contains('format-rule-mode') || target.classList.contains('format-rule-label') || target.classList.contains('format-rule-custom-message') || target.classList.contains('format-rule-custom-field-mode') || target.classList.contains('format-rule-custom-field-label')) {
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

    const modes = item.querySelectorAll(`.format-rule-mode[data-format="${format}"], .format-rule-custom-field-mode[data-format="${format}"]`);
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
