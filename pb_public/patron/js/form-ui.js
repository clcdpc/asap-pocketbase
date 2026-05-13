import {
  fieldKeys,
  formatSelect,
  lastSelectedFormat,
  physicalFields,
  econtentFields,
  setLastSelectedFormat,
  suggestionForm
} from './state.js';
import {
  defaultFormatRules,
  defaultUiText,
  formatRules,
  publicationOptions,
  setPublicationOptions,
  uiConfig
} from './config.js';
import { getFieldRule } from './form-rules.js';
import { byId, optionNode, replaceChildren, setLabel, setText, setVisible } from './dom.js';
import { sanitizeHtml, applyPatronTextPlaceholders } from './html.js';
import { showSuggestionStep } from './steps.js';

export function fieldElements(field) {
  const ids = {
    title: { row: 'field-title', input: 'title', label: 'lbl-title' },
    author: { row: 'field-author', input: 'author', label: 'lbl-creator' },
    identifier: { row: 'field-identifier', input: 'isbn', label: 'lbl-identifier' },
    publication: { row: 'field-publication', input: 'publication', label: 'lbl-publication' }
  }[field];

  return {
    row: byId(ids.row),
    input: byId(ids.input),
    label: byId(ids.label)
  };
}

export function messageHtmlForBehavior(behavior, formatKey) {
  const rule = formatRules[formatKey] || {};
  if (behavior === 'message') return applyPatronTextPlaceholders(rule.message || '', uiConfig);
  if (behavior === 'ebookMessage') {
    return applyPatronTextPlaceholders(uiConfig.ebookMessage || rule.message || defaultFormatRules.ebook.message, uiConfig);
  }
  if (behavior === 'eaudiobookMessage') {
    return applyPatronTextPlaceholders(uiConfig.eaudiobookMessage || rule.message || defaultFormatRules.eaudiobook.message, uiConfig);
  }
  return '';
}

export function updateFieldRuleUi(fieldKey, rule) {
  const mode = fieldKey === 'title' ? 'required' : rule.mode;
  const required = mode === 'required';
  const hidden = mode === 'hidden';
  const els = fieldElements(fieldKey);

  if (els.row) els.row.classList.toggle('hidden', hidden);
  if (els.input) {
    els.input.disabled = hidden;
    els.input.required = required && !hidden;
    els.input.setAttribute('aria-required', required && !hidden ? 'true' : 'false');
  }
  setLabel(els.label, rule.label, required && !hidden);
}

export function updateFormatUI() {
  const format = (formatSelect && formatSelect.value) || 'book';
  const rule = formatRules[format] || formatRules.book;
  const messageBehavior = rule.messageBehavior || 'none';

  if (messageBehavior !== 'none') {
    if (physicalFields) physicalFields.classList.add('hidden');
    if (econtentFields) econtentFields.classList.remove('hidden');
    setVisible('submit-error', false);

    const msgContainer = byId('econtent-msg-container');
    if (msgContainer) {
      // Configured message HTML is sanitized before rendering.
      msgContainer.innerHTML = sanitizeHtml(messageHtmlForBehavior(messageBehavior, format));
    }

    fieldKeys.forEach(field => {
      const els = fieldElements(field);
      if (els.input) {
        els.input.required = false;
        els.input.setAttribute('aria-required', 'false');
        els.input.disabled = true;
      }
    });
    return;
  }

  if (physicalFields) physicalFields.classList.remove('hidden');
  if (econtentFields) econtentFields.classList.add('hidden');

  fieldKeys.forEach(field => {
    updateFieldRuleUi(field, getFieldRule(formatRules, format, field, defaultFormatRules));
  });
}

export function populatePublicationOptions(options) {
  setPublicationOptions(options);
  const select = byId('publication');
  if (!select) return;
  const currentValue = select.value;
  const optionNodes = publicationOptions.map(option => optionNode(option, option));
  replaceChildren(select, ...optionNodes);
  if (publicationOptions.includes(currentValue)) {
    select.value = currentValue;
  }
}

export function renderSuccessMessage() {
  setText('success-title', applyPatronTextPlaceholders(uiConfig.successTitle || defaultUiText.successTitle, uiConfig));
  const body = byId('success-body');
  if (body) {
    // Configured message HTML is sanitized before rendering.
    body.innerHTML = sanitizeHtml(applyPatronTextPlaceholders(uiConfig.successMessage || defaultUiText.successMessage, uiConfig));
  }
}

export function renderConflictMessage(message) {
  const body = byId('conflict-body');
  if (body) {
    // Configured message HTML is sanitized before rendering.
    body.innerHTML = sanitizeHtml(applyPatronTextPlaceholders(message || uiConfig.alreadySubmittedMessage || defaultUiText.alreadySubmittedMessage, uiConfig));
  }
}

export function applyCommonAuthors() {
  const container = byId('common-author-container');
  const select = byId('common-author');
  if (!container || !select) return;

  if (!uiConfig.commonAuthorsEnabled) {
    container.classList.add('hidden');
    setVisible('common-author-msg-container', false);
    if (physicalFields) physicalFields.classList.remove('hidden');
    setVisible('submit-btn', true);
    return;
  }

  const authors = (uiConfig.commonAuthorsList || '').split('\n').map(s => s.trim()).filter(Boolean);
  if (authors.length === 0) {
    container.classList.add('hidden');
    return;
  }

  const label = document.querySelector('label[for="common-author"]');
  if (label) label.textContent = applyPatronTextPlaceholders(uiConfig.commonAuthorsLabel || 'Popular Creators', uiConfig);
  setText('common-authors-help', applyPatronTextPlaceholders(uiConfig.commonAuthorsHelp || 'See if this is a creator we already collect.', uiConfig));

  const currentValue = select.value;
  replaceChildren(select, optionNode('', '-- Select a Creator --'), ...authors.map(author => optionNode(author, author)));
  select.value = authors.includes(currentValue) ? currentValue : '';

  container.classList.remove('hidden');
  handleCommonAuthorSelection();
}

export function handleCommonAuthorSelection() {
  const select = byId('common-author');
  const msgContainer = byId('common-author-msg-container');
  const submitBtn = byId('submit-btn');
  if (!select || !msgContainer || !submitBtn) return;

  if (select.value) {
    setText('common-author-msg', applyPatronTextPlaceholders(uiConfig.commonAuthorsMessage || "We automatically purchase all upcoming titles by this creator. Please check the catalog to place a hold on 'On Order' items.", uiConfig));
    msgContainer.classList.remove('hidden');
    if (physicalFields) physicalFields.classList.add('hidden');
    submitBtn.classList.add('hidden');
  } else {
    msgContainer.classList.add('hidden');
    updateFormatUI();
    submitBtn.classList.remove('hidden');
  }
}

export function resetCommonAuthorSelection() {
  const select = byId('common-author');
  if (!select) return;
  select.value = '';
  setVisible('common-author-msg-container', false);
}

export function updateFormatLabels() {
  const labels = uiConfig.formatLabels || {};
  const available = uiConfig.availableFormats;
  const select = formatSelect;
  if (!select) return;

  if (available && available.length > 0) {
    replaceChildren(select, ...available.map(key => optionNode(key, labels[key] || key)));
  } else {
    Array.from(select.options).forEach(option => {
      if (labels[option.value]) option.textContent = labels[option.value];
    });
  }

  select.dispatchEvent(new Event('change'));
}

export function applyUiConfig() {
  const navLogo = byId('nav-logo');
  const appIcon = byId('app-icon');

  if (uiConfig.pageTitle) {
    const pageTitle = applyPatronTextPlaceholders(uiConfig.pageTitle, uiConfig);
    setText('main-title', pageTitle);
    document.title = pageTitle;
  }

  if (uiConfig.barcodeLabel) {
    setText('lbl-barcode-login', uiConfig.barcodeLabel);
    setText('lbl-barcode-display', uiConfig.barcodeLabel);
  }
  if (uiConfig.pinLabel) setText('lbl-pin-login', uiConfig.pinLabel);

  if (uiConfig.suggestionFormNote) {
    const noteEl = byId('ui-note-text');
    if (noteEl) noteEl.innerHTML = sanitizeHtml(applyPatronTextPlaceholders(uiConfig.suggestionFormNote, uiConfig));
  }
  if (uiConfig.loginNote) {
    const noteEl = byId('ui-login-note-container');
    if (noteEl) noteEl.innerHTML = sanitizeHtml(applyPatronTextPlaceholders(uiConfig.loginNote, uiConfig));
  }
  if (uiConfig.loginPrompt) {
    const promptEl = byId('ui-login-prompt');
    if (promptEl) promptEl.innerHTML = sanitizeHtml(applyPatronTextPlaceholders(uiConfig.loginPrompt, uiConfig));
  }
  if (uiConfig.noEmailMessage) {
    const msgEl = byId('no-email-msg');
    if (msgEl) msgEl.innerHTML = sanitizeHtml(applyPatronTextPlaceholders(uiConfig.noEmailMessage, uiConfig));
  }

  if (uiConfig.logoUrl) {
    if (navLogo) {
      navLogo.src = uiConfig.logoUrl;
      navLogo.classList.remove('hidden');
    }
    if (appIcon) appIcon.href = uiConfig.logoUrl;
  }
  if (uiConfig.logoAlt && navLogo) navLogo.alt = uiConfig.logoAlt;

  if (uiConfig.systemNotEnabled) {
    const errorDiv = byId('login-error');
    if (errorDiv) {
      // Configured message HTML is sanitized before rendering.
      errorDiv.innerHTML = sanitizeHtml(applyPatronTextPlaceholders(uiConfig.systemNotEnabledMessage || 'Your library does not currently participate in this suggestion service.', uiConfig));
      errorDiv.classList.remove('hidden');
    }
    const btn = byId('login-btn');
    if (btn) btn.disabled = true;
  }

  renderSuccessMessage();
  renderConflictMessage();
  populatePublicationOptions(uiConfig.publicationOptions);
  updateFormatUI();
  updateFormatLabels();
  applyCommonAuthors();

  const autoholdField = byId('field-autohold');
  if (autoholdField) {
    autoholdField.classList.toggle('hidden', !uiConfig.allowPatronAutoholdOptOut);
  }
}

export function resetSuggestionFormUi() {
  if (suggestionForm) suggestionForm.reset();
  setVisible('submit-error', false);
  updateFormatUI();
  showSuggestionStep();
}

export function bindFormEvents() {
  document.querySelectorAll('.btn-cancel').forEach(btn => {
    btn.addEventListener('click', resetSuggestionFormUi);
  });

  document.querySelectorAll('.btn-submit-another').forEach(btn => {
    btn.addEventListener('click', event => {
      event.preventDefault();
      resetSuggestionFormUi();
    });
  });

  const commonAuthor = byId('common-author');
  if (commonAuthor) commonAuthor.addEventListener('change', handleCommonAuthorSelection);

  if (formatSelect) {
    formatSelect.addEventListener('change', () => {
      const nextFormat = formatSelect.value;
      if (nextFormat !== lastSelectedFormat) {
        resetCommonAuthorSelection();
        setLastSelectedFormat(nextFormat);
      }
      updateFormatUI();
    });
  }
}
