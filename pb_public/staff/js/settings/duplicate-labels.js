import { duplicateStatusLabelDefaults, duplicateStatusLabelFields, currentLibraryContextOrgId } from '../state.js';
import { escapeAttr } from '../grid.js';

export function normalizeDuplicateStatusLabels(labels = {}) {
  return { ...duplicateStatusLabelDefaults, ...(labels && typeof labels === 'object' ? labels : {}) };
}

export function renderDuplicateStatusLabelSettings(labels = {}, source = '', inherited = false) {
  const container = document.getElementById('duplicate-status-labels-container');
  if (!container) return;
  const normalized = normalizeDuplicateStatusLabels(labels);
  const scopeEl = document.getElementById('duplicate-status-labels-scope');
  if (scopeEl) {
    if (currentLibraryContextOrgId === 'system') {
      scopeEl.textContent = 'Editing global default labels.';
      scopeEl.className = 'small mb-3 text-muted';
    } else if (inherited || source === 'global' || source === 'default') {
      scopeEl.textContent = 'Showing inherited global labels. Saving will create labels for the selected library only.';
      scopeEl.className = 'small mb-3 text-warning';
    } else {
      scopeEl.textContent = 'Editing custom labels for the selected library.';
      scopeEl.className = 'small mb-3 text-info';
    }
  }
  container.replaceChildren(...duplicateStatusLabelFields.map(([key, label]) => {
    const div = document.createElement('div');
    div.className = 'form-group col-md-6';
    const lbl = document.createElement('label');
    lbl.setAttribute('for', `duplicate-status-${escapeAttr(key)}`);
    lbl.className = 'small font-weight-bold';
    lbl.textContent = label;
    const input = document.createElement('input');
    input.type = 'text';
    input.id = `duplicate-status-${escapeAttr(key)}`;
    input.className = 'form-control form-control-sm duplicate-status-label-input';
    input.dataset.key = key;
    input.value = normalized[key] || '';
    div.appendChild(lbl);
    div.appendChild(input);
    return div;
  }));
}

export function collectDuplicateStatusLabels() {
  const labels = {};
  duplicateStatusLabelFields.forEach(([key]) => {
    const el = document.getElementById(`duplicate-status-${key}`);
    const value = el ? el.value.trim() : '';
    labels[key] = value || duplicateStatusLabelDefaults[key] || '';
  });
  labels['Silently Closed'] = labels.silent || duplicateStatusLabelDefaults.silent;
  return labels;
}
