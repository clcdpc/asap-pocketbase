import { pb, currentRejectionTemplates, setCurrentRejectionTemplates, currentLibraryContextOrgId, setCurrentLibraryContextOrgId, emailTemplateDefaults, templateFieldIds } from './state.js';
import { markSettingsDirty, updateAutoRejectEmailControls, getFieldChecked, getFieldValue } from './api.js';
import { showAlert, showConfirm } from './dialogs.js';
import { escapeAttr } from './grid.js';

let lastActiveTemplateField = null;
let placeholderHelperResetTimer = null;

function isTemplatePlaceholderField(el) {
  if (!el || (el.tagName !== 'INPUT' && el.tagName !== 'TEXTAREA')) return false;

  if (templateFieldIds.includes(el.id)) return true;

  const field = el.getAttribute('data-field');
  return el.closest('#rejection-templates-accordion-container') && (field === 'subject' || field === 'body');
}

function trackActiveTemplateField(el) {
  if (isTemplatePlaceholderField(el)) {
    lastActiveTemplateField = el;
  }
}

function validLastActiveTemplateField() {
  if (!lastActiveTemplateField || !document.contains(lastActiveTemplateField) || !isTemplatePlaceholderField(lastActiveTemplateField)) {
    lastActiveTemplateField = null;
    return null;
  }
  return lastActiveTemplateField;
}

function flashPlaceholderHelper(message) {
  const helper = document.querySelector('.placeholder-helper-compact .small.mt-2');
  if (!helper) return;

  const originalMessage = helper.getAttribute('data-original-message') || helper.textContent;
  helper.setAttribute('data-original-message', originalMessage);
  helper.textContent = message;
  clearTimeout(placeholderHelperResetTimer);
  placeholderHelperResetTimer = setTimeout(() => {
    helper.textContent = helper.getAttribute('data-original-message') || originalMessage;
  }, 2200);
}

function insertPlaceholderIntoField(field, placeholder) {
  const start = typeof field.selectionStart === 'number' ? field.selectionStart : field.value.length;
  const end = typeof field.selectionEnd === 'number' ? field.selectionEnd : start;
  const text = field.value;
  const caret = start + placeholder.length;

  field.value = text.substring(0, start) + placeholder + text.substring(end);
  field.focus();
  field.selectionStart = field.selectionEnd = caret;
  field.dispatchEvent(new Event('input', { bubbles: true }));
  field.dispatchEvent(new Event('change', { bubbles: true }));
}

export function populateEmailTemplateForms(emails) {
  emails = emails || {};

  if (document.getElementById('email-from-address')) document.getElementById('email-from-address').value = emails.fromAddress || '';
  if (document.getElementById('email-from-name')) document.getElementById('email-from-name').value = emails.fromName || '';

  // Sync to SMTP tab if we are modifying the system context
  if (currentLibraryContextOrgId === 'system') {
    if (document.getElementById('smtp-from')) document.getElementById('smtp-from').value = emails.fromAddress || '';
    if (document.getElementById('smtp-from-name')) document.getElementById('smtp-from-name').value = emails.fromName || '';
  }

  const emailSubmit = emails.suggestion_submitted || {};
  if (document.getElementById('email-submit-subject')) document.getElementById('email-submit-subject').value = emailSubmit.subject || emailTemplateDefaults.suggestion_submitted.subject;
  if (document.getElementById('email-submit-body')) document.getElementById('email-submit-body').value = emailSubmit.body || emailTemplateDefaults.suggestion_submitted.body;

  const emailOwned = emails.already_owned || {};
  if (document.getElementById('email-owned-subject')) document.getElementById('email-owned-subject').value = emailOwned.subject || emailTemplateDefaults.already_owned.subject;
  if (document.getElementById('email-owned-body')) document.getElementById('email-owned-body').value = emailOwned.body || emailTemplateDefaults.already_owned.body;

  const emailRejected = emails.rejected || {};
  if (document.getElementById('email-rejected-subject')) document.getElementById('email-rejected-subject').value = emailRejected.subject || emailTemplateDefaults.rejected.subject;
  if (document.getElementById('email-rejected-body')) document.getElementById('email-rejected-body').value = emailRejected.body || emailTemplateDefaults.rejected.body;

  setCurrentRejectionTemplates(Array.isArray(emails.rejection_templates) ? JSON.parse(JSON.stringify(emails.rejection_templates)) : []);
  renderRejectionTemplates();

  const emailHold = emails.hold_placed || {};
  if (document.getElementById('email-hold-subject')) document.getElementById('email-hold-subject').value = emailHold.subject || emailTemplateDefaults.hold_placed.subject;
  if (document.getElementById('email-hold-body')) document.getElementById('email-hold-body').value = emailHold.body || emailTemplateDefaults.hold_placed.body;

  // Initialize summaries
  updateAllSummaries();
}

export function updateAllSummaries() {
  // Sender Details Summary
  const fromAddr = document.getElementById('email-from-address')?.value || '';
  const fromName = document.getElementById('email-from-name')?.value || '';
  const senderSummary = [fromName, fromAddr].filter(Boolean).join(' <') + (fromAddr ? '>' : '');
  if (document.getElementById('summary-sender-details')) {
    document.getElementById('summary-sender-details').textContent = senderSummary || 'System Defaults';
  }

  // Template Summaries
  const sections = [
    { id: 'submit', field: 'email-submit-subject' },
    { id: 'owned', field: 'email-owned-subject' },
    { id: 'rejected', field: 'email-rejected-subject' },
    { id: 'hold', field: 'email-hold-subject' }
  ];

  sections.forEach(s => {
    const el = document.getElementById(s.field);
    const summaryEl = document.getElementById(`summary-${s.id}`);
    if (el && summaryEl) {
      summaryEl.textContent = el.value || 'No subject set';
    }
  });

  // Rejection Templates Summaries (handled in renderRejectionTemplates)
}

export function renderRejectionTemplates() {
  const container = document.getElementById('rejection-templates-accordion-container');
  if (!container) return;

  container.innerHTML = '';
  currentRejectionTemplates.forEach((template, index) => {
    const item = document.createElement('div');
    item.className = 'asap-accordion-item';
    item.id = `accordion-rejection-${index}`;

    const summaryText = template.subject || 'No subject set';

    item.innerHTML = `
      <div class="asap-accordion-header-row">
        <button type="button" class="asap-accordion-header" aria-expanded="false" aria-controls="panel-rejection-${index}">
          <span class="asap-accordion-title">Rejected: ${escapeAttr(template.name || 'New Reason')}</span>
          <span class="asap-accordion-summary">${escapeAttr(summaryText)}</span>
          <i class="fa fa-chevron-down asap-accordion-chevron"></i>
        </button>
        <button type="button" class="btn btn-link text-danger asap-accordion-delete js-remove-rejection-template" data-index="${index}" aria-label="Delete template ${escapeAttr(template.name || summaryText || 'Rejection template')}">
          <i class="fa fa-trash mr-1" aria-hidden="true"></i>Delete template
        </button>
      </div>
      <div id="panel-rejection-${index}" class="asap-accordion-panel" role="region">
        <div class="form-group">
          <label class="template-editor-label">Template name</label>
          <input type="text" class="form-control form-control-sm js-update-rejection-template" data-field="name" data-index="${index}" value="${escapeAttr(template.name || '')}">
        </div>
        <div class="form-group">
          <label class="template-editor-label">Subject</label>
          <input type="text" class="form-control form-control-sm js-update-rejection-template" data-field="subject" data-index="${index}" value="${escapeAttr(template.subject || '')}">
        </div>
        <div class="form-group mb-0">
          <label class="template-editor-label">Body</label>
          <textarea class="form-control template-editor-textarea" data-field="body" data-index="${index}" rows="5">${escapeAttr(template.body || '')}</textarea>
        </div>
      </div>
    `;
    container.appendChild(item);
  });
}

function accordionHeaderForItem(item) {
  if (!item) return null;
  for (const child of item.children) {
    if (child.classList && child.classList.contains('asap-accordion-header')) {
      return child;
    }
    if (child.classList && child.classList.contains('asap-accordion-header-row')) {
      const header = child.querySelector('.asap-accordion-header');
      if (header) return header;
    }
  }
  return null;
}

export function toggleAccordion(item) {
  const header = accordionHeaderForItem(item);
  if (!header) return;

  const accordion = item.closest('.asap-accordion');
  const allowMultiple = accordion && accordion.getAttribute('data-accordion-multiple') === 'true';
  const isExpanded = header.getAttribute('aria-expanded') === 'true';

  if (!allowMultiple && accordion) {
    Array.from(accordion.children).forEach(i => {
      if (!i.classList || !i.classList.contains('asap-accordion-item') || i === item) return;
      const siblingHeader = accordionHeaderForItem(i);
      i.classList.remove('active');
      if (siblingHeader) siblingHeader.setAttribute('aria-expanded', 'false');
    });
  }

  item.classList.toggle('active', !isExpanded);
  header.setAttribute('aria-expanded', isExpanded ? 'false' : 'true');

  if (!isExpanded) {
    setTimeout(() => {
      const firstInput = item.querySelector('.asap-accordion-panel input, .asap-accordion-panel textarea');
      if (firstInput) firstInput.focus();
    }, 100);
  }
}

export function updateRejectionTemplate(index, field, value) {
  if (currentRejectionTemplates[index]) {
    currentRejectionTemplates[index][field] = value;
    if (field === 'name') {
      updateAutoRejectEmailControls();
      // Update title in header if name changed
      const item = document.getElementById(`accordion-rejection-${index}`);
      if (item) {
        item.querySelector('.asap-accordion-title').textContent = `Rejected: ${value}`;
      }
    }
    if (field === 'subject') {
      // Update summary in header if subject changed
      const item = document.getElementById(`accordion-rejection-${index}`);
      if (item) {
        item.querySelector('.asap-accordion-summary').textContent = value || 'No subject set';
      }
    }
    markSettingsDirty();
  }
}

export async function removeRejectionTemplate(index) {
  const template = currentRejectionTemplates[index];
  if (!template) return;
  const templateName = template.name || template.subject || 'this rejection template';

  const autoRejectEnabled = getFieldChecked('outstanding-timeout-enabled');
  const sendEmail = getFieldChecked('outstanding-timeout-send-email');
  const selectedTemplateId = getFieldValue('outstanding-timeout-rejection-template-id');

  if (autoRejectEnabled && sendEmail && selectedTemplateId === template.id) {
    await showAlert('This template can’t be removed because it’s currently selected for the auto-rejection workflow. Please choose a different template in the Workflow settings first.');
    return;
  }

  const confirmed = await showConfirm('Delete template?', `Delete "${templateName}"? This cannot be undone after you save these settings.`);
  if (!confirmed) return;

  currentRejectionTemplates.splice(index, 1);
  renderRejectionTemplates();
  updateAutoRejectEmailControls();
  markSettingsDirty();
}

// Event Listeners
const emailTemplatesAccordion = document.getElementById('email-templates-accordion');
if (emailTemplatesAccordion) {
  emailTemplatesAccordion.addEventListener('focusin', (e) => {
    trackActiveTemplateField(e.target);
  });

  emailTemplatesAccordion.addEventListener('keyup', (e) => {
    trackActiveTemplateField(e.target);
  });

  emailTemplatesAccordion.addEventListener('click', (e) => {
    trackActiveTemplateField(e.target);
  });
}

const settingsTemplatesPanel = document.getElementById('settings-templates');
if (settingsTemplatesPanel) {
  settingsTemplatesPanel.addEventListener('click', (e) => {
    const chip = e.target.closest('.placeholder-chip');
    if (!chip) return;

    const placeholder = chip.textContent;
    const field = validLastActiveTemplateField();
    if (field) {
      insertPlaceholderIntoField(field, placeholder);
    } else {
      flashPlaceholderHelper('Focus a template Subject or Body field first, then click a placeholder.');
    }
  });
}

document.addEventListener('click', (e) => {
  // Accordion Header Click
  const header = e.target.closest('.asap-accordion-header');
  if (header) {
    const item = header.closest('.asap-accordion-item');
    if (item) toggleAccordion(item);
    return;
  }

  const removeBtn = e.target.closest('.js-remove-rejection-template');
  if (removeBtn) {
    const index = parseInt(removeBtn.getAttribute('data-index'), 10);
    if (!isNaN(index)) {
      removeRejectionTemplate(index);
    }
  }
});

document.addEventListener('input', (e) => {
  const target = e.target;
  if (target.id === 'email-from-address' || target.id === 'email-from-name' || 
      target.id === 'email-submit-subject' || target.id === 'email-owned-subject' || 
      target.id === 'email-rejected-subject' || target.id === 'email-hold-subject') {
    updateAllSummaries();
    markSettingsDirty();
  }

  const updateInput = target.closest('.js-update-rejection-template');
  if (updateInput) {
    const index = parseInt(updateInput.getAttribute('data-index'), 10);
    const field = updateInput.getAttribute('data-field');
    if (!isNaN(index) && field) {
      updateRejectionTemplate(index, field, updateInput.value);
    }
  }
});

const btnAddRejectionTemplate = document.getElementById('btn-add-rejection-template');
if (btnAddRejectionTemplate) {
  btnAddRejectionTemplate.addEventListener('click', () => {
    currentRejectionTemplates.push({
      id: pb.authStore.model ? pb.authStore.model.id + '_' + Date.now() : 'tpl_' + Date.now(),
      name: 'New Rejection Reason',
      subject: emailTemplateDefaults.rejected.subject,
      body: emailTemplateDefaults.rejected.body
    });
    renderRejectionTemplates();
    updateAutoRejectEmailControls();
    markSettingsDirty();
    
    // Open the newly added template
    const newIndex = currentRejectionTemplates.length - 1;
    setTimeout(() => {
      const newItem = document.getElementById(`accordion-rejection-${newIndex}`);
      if (newItem) toggleAccordion(newItem);
    }, 50);
  });
}
