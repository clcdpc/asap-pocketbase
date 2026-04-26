const loginForm = document.getElementById('login-form');
const suggestionForm = document.getElementById('suggestion-form');
const stepLogin = document.getElementById('step-login');
const stepForm = document.getElementById('step-form');
const stepSuccess = document.getElementById('step-success');
const stepConflict = document.getElementById('step-conflict');

const formatSelect = document.getElementById('format');
const physicalFields = document.getElementById('physical-fields');
const econtentFields = document.getElementById('econtent-fields');
const authorInput = document.getElementById('author');
const titleInput = document.getElementById('title');
const agegroupInput = document.getElementById('agegroup');
const publicationInput = document.getElementById('publication');
const defaultPublicationOptions = ['Already published', 'Coming soon', 'Published a while back'];
const formatKeys = ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];
const fieldKeys = ['title', 'author', 'identifier', 'agegroup', 'publication'];
const defaultFormatRules = {
  book: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'ISBN' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  audiobook_cd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'ISBN' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  dvd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Director/Actors/Producer' },
      identifier: { mode: 'hidden', label: 'UPC' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  music_cd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Artist' },
      identifier: { mode: 'hidden', label: 'UPC' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  ebook: {
    messageBehavior: 'ebookMessage',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'ISBN' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  eaudiobook: {
    messageBehavior: 'eaudiobookMessage',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'ISBN' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  }
};
const defaultUiText = {
  successTitle: 'Suggestion Submitted',
  successMessage: 'You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>',
  alreadySubmittedMessage: 'This suggestion has already been submitted. We only accept one suggestion per title. Check the catalog to see if the material was acquired and place a hold.<div>Thank you for using this library\'s suggestion service.</div>',
  pageTitle: 'Material Suggestion',
  publicationOptions: defaultPublicationOptions,
  formatRules: defaultFormatRules
};
let publicationOptions = defaultPublicationOptions.slice();
let uiConfig = { ...defaultUiText };
let formatRules = normalizeFormatRules(defaultFormatRules);

let authToken = '';

function getApiUrl(path) {
  return window.location.origin + path;
}

async function request(path, options = {}) {
  const headers = { 'Content-Type': 'application/json' };
  if (authToken) headers['Authorization'] = authToken;
  const response = await fetch(getApiUrl(path), {
    ...options,
    headers: { ...headers, ...options.headers }
  });
  
  if (!response.ok) {
    let message = response.statusText;
    try {
      const data = await response.json();
      if (data && data.message) message = data.message;
    } catch (e) {}
    const err = new Error(message);
    err.status = response.status;
    throw err;
  }
  return response.json();
}

function getConfigUrl() {
  return '/api/asap/config?t=' + Date.now();
}

function showStep(stepElement) {
  [stepLogin, stepForm, stepSuccess, stepConflict].forEach(el => el.classList.add('hidden'));
  stepElement.classList.remove('hidden');
  
  const announcer = document.getElementById('status-announcer');
  let title = "";
  if (stepElement === stepLogin) title = "Login Step";
  if (stepElement === stepForm) title = "Suggestion Details Step";
  if (stepElement === stepSuccess) title = "Success";
  if (stepElement === stepConflict) title = "Conflict - Already Submitted";
  
  announcer.textContent = "Navigated to " + title;
  
  // Set focus to the first meaningful element in the new step
  const firstInput = stepElement.querySelector('input, select, textarea, button, h2');
  if (firstInput) {
    firstInput.focus();
  }
}

function resetForm() {
  suggestionForm.reset();
  document.getElementById('submit-error').classList.add('hidden');
  updateFormatUI();
  showStep(stepForm);
}

function logout() {
  authToken = '';
  loginForm.reset();
  suggestionForm.reset();
  document.getElementById('login-error').classList.add('hidden');
  document.getElementById('submit-error').classList.add('hidden');
  updateFormatUI();
  showStep(stepLogin);
}

document.querySelectorAll('.btn-cancel').forEach(btn => {
  btn.addEventListener('click', resetForm);
});

document.querySelectorAll('.btn-submit-another').forEach(btn => {
  btn.addEventListener('click', (e) => {
    e.preventDefault();
    resetForm();
  });
});

document.querySelectorAll('.btn-logout').forEach(btn => {
  btn.addEventListener('click', (e) => {
    e.preventDefault();
    logout();
  });
});

loginForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const btn = document.getElementById('login-btn');
  const errorDiv = document.getElementById('login-error');
  btn.disabled = true;
  errorDiv.classList.add('hidden');

  try {
    const fd = new FormData(loginForm);
    const data = Object.fromEntries(fd.entries());
    const result = await request('/api/asap/patron/login', {
      method: 'POST',
      body: JSON.stringify(data)
    });
    
    authToken = result.token;
    
    document.getElementById('display-barcode').textContent = data.username;
    const email = result.email || (result.record && result.record.email);
    if (email) {
      document.getElementById('display-email').textContent = email;
      document.getElementById('no-email-msg').classList.add('hidden');
    } else {
      document.getElementById('display-email').textContent = '';
      const noEmailMsg = document.getElementById('no-email-msg');
      if (uiConfig.noEmailMessage) {
        noEmailMsg.innerHTML = uiConfig.noEmailMessage;
      } else {
        noEmailMsg.innerHTML = 'No email is specified on your library account, which means we won\'t be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.';
      }
      noEmailMsg.classList.remove('hidden');
    }

    if (result.preferredPickupBranchName) {
      document.getElementById('display-pickup-branch').textContent = result.preferredPickupBranchName;
      document.getElementById('pickup-branch-container').classList.remove('hidden');
    } else {
      document.getElementById('pickup-branch-container').classList.add('hidden');
    }

    showStep(stepForm);
  } catch (err) {
    errorDiv.textContent = 'Incorrect Login - Please try again';
    errorDiv.classList.remove('hidden');
  } finally {
    btn.disabled = false;
  }
});

suggestionForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const btn = document.getElementById('submit-btn');
  const errorDiv = document.getElementById('submit-error');
  btn.disabled = true;
  errorDiv.classList.add('hidden');

  try {
    const fd = new FormData(suggestionForm);
    const data = Object.fromEntries(fd.entries());
    const result = await request('/api/asap/patron/suggestions', {
      method: 'POST',
      body: JSON.stringify(data)
    });
    applySuccessConfig(result);
    showStep(stepSuccess);
  } catch (err) {
    if (err.status === 409) {
      showStep(stepConflict);
    } else if (err.status === 406) {
      errorDiv.textContent = err.message || 'You have reached your weekly suggestion limit.';
      errorDiv.classList.remove('hidden');
    } else {
      errorDiv.textContent = err.message || 'Error. Please try again';
      errorDiv.classList.remove('hidden');
    }
  } finally {
    btn.disabled = false;
  }
});

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function normalizeMode(value, fallback) {
  return ['required', 'optional', 'hidden'].includes(value) ? value : fallback || 'optional';
}

function normalizeMessageBehavior(value, fallback) {
  return ['none', 'ebookMessage', 'eaudiobookMessage'].includes(value) ? value : fallback || 'none';
}

function normalizeFormatRules(rules) {
  const normalized = clone(defaultFormatRules);
  const incoming = rules && typeof rules === 'object' ? rules : {};

  formatKeys.forEach(format => {
    const incomingFormat = incoming[format] || {};
    normalized[format].messageBehavior = normalizeMessageBehavior(incomingFormat.messageBehavior, normalized[format].messageBehavior);
    const incomingFields = incomingFormat.fields || {};
    fieldKeys.forEach(field => {
      const incomingField = incomingFields[field] || {};
      const defaultField = normalized[format].fields[field];
      let mode = normalizeMode(incomingField.mode, defaultField.mode);
      if (field === 'title') mode = 'required';
      normalized[format].fields[field] = {
        mode,
        label: String(incomingField.label || defaultField.label || field).trim() || defaultField.label || field
      };
    });
  });

  return normalized;
}

function setLabel(labelEl, label, required) {
  if (!labelEl) return;
  labelEl.textContent = '';
  labelEl.appendChild(document.createTextNode(label + (required ? ' ' : '')));
  if (required) {
    const marker = document.createElement('span');
    marker.setAttribute('aria-hidden', 'true');
    marker.textContent = '*';
    labelEl.appendChild(marker);
  }
}

function fieldElements(field) {
  const ids = {
    title: { row: 'field-title', input: 'title', label: 'lbl-title' },
    author: { row: 'field-author', input: 'author', label: 'lbl-creator' },
    identifier: { row: 'field-identifier', input: 'isbn', label: 'lbl-identifier' },
    agegroup: { row: 'field-agegroup', input: 'agegroup', label: 'lbl-agegroup' },
    publication: { row: 'field-publication', input: 'publication', label: 'lbl-publication' }
  }[field];

  return {
    row: document.getElementById(ids.row),
    input: document.getElementById(ids.input),
    label: document.getElementById(ids.label)
  };
}

function messageHtmlForBehavior(behavior) {
  if (behavior === 'ebookMessage') {
    return uiConfig.ebookMessage || '<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href="https://help.libbyapp.com/en-us/6260.htm" target="_blank" rel="noreferrer">Learn how to suggest a purchase using Libby here.</a></p>';
  }
  if (behavior === 'eaudiobookMessage') {
    return uiConfig.eaudiobookMessage || '<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href="https://help.libbyapp.com/en-us/6260.htm" target="_blank" rel="noreferrer">Learn how to suggest a purchase using Libby here.</a></p>';
  }
  return '';
}

function updateFormatUI() {
  const format = formatKeys.includes(formatSelect.value) ? formatSelect.value : 'book';
  const rule = formatRules[format] || formatRules.book;
  const messageBehavior = rule.messageBehavior || 'none';

  if (messageBehavior !== 'none') {
    physicalFields.classList.add('hidden');
    econtentFields.classList.remove('hidden');
    document.getElementById('submit-error').classList.add('hidden');

    const msgContainer = document.getElementById('econtent-msg-container');
    msgContainer.innerHTML = messageHtmlForBehavior(messageBehavior);

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

  physicalFields.classList.remove('hidden');
  econtentFields.classList.add('hidden');

  fieldKeys.forEach(field => {
    const fieldRule = rule.fields[field] || defaultFormatRules.book.fields[field];
    const mode = field === 'title' ? 'required' : fieldRule.mode;
    const required = mode === 'required';
    const hidden = mode === 'hidden';
    const els = fieldElements(field);

    if (els.row) els.row.classList.toggle('hidden', hidden);
    if (els.input) {
      els.input.disabled = hidden;
      els.input.required = required && !hidden;
      els.input.setAttribute('aria-required', required && !hidden ? 'true' : 'false');
    }
    setLabel(els.label, fieldRule.label, required && !hidden);
  });
}

async function loadConfig() {
  try {
    const config = normalizeUiConfig(await request(getConfigUrl()));
    uiConfig = { ...uiConfig, ...config };
    applyUiConfig();
  } catch (err) {
    console.error('Failed to load config', err);
    renderSuccessMessage();
    renderConflictMessage();
  }
}

function applySuccessConfig(config) {
  const nextConfig = normalizeUiConfig(config);
  if (nextConfig.successTitle || nextConfig.successMessage) {
    uiConfig = { ...uiConfig, ...nextConfig };
    renderSuccessMessage();
    return;
  }

  renderSuccessMessage();
}

function normalizeUiConfig(config) {
  if (!config) return {};
  if (config.ui_text && typeof config.ui_text === 'object') {
    return { ...config.ui_text, logoUrl: config.logoUrl || config.ui_text.logoUrl };
  }
  return config;
}

function applyUiConfig() {
  const noteText = document.getElementById('ui-note-text');
  const loginNote = document.getElementById('ui-login-note-container');
  const loginPrompt = document.getElementById('ui-login-prompt');
  const navLogo = document.getElementById('nav-logo');
  const appIcon = document.getElementById('app-icon');
  const mainTitle = document.getElementById('main-title');

  if (uiConfig.pageTitle && mainTitle) {
    mainTitle.textContent = uiConfig.pageTitle;
    document.title = uiConfig.pageTitle;
  }

  if (uiConfig.suggestionFormNote && noteText) noteText.innerHTML = uiConfig.suggestionFormNote;
  if (uiConfig.loginNote && loginNote) loginNote.innerHTML = uiConfig.loginNote;
  if (uiConfig.loginPrompt && loginPrompt) loginPrompt.innerHTML = uiConfig.loginPrompt;
  if (uiConfig.noEmailMessage) {
    const noEmailEl = document.getElementById('no-email-msg');
    if (noEmailEl) noEmailEl.innerHTML = uiConfig.noEmailMessage;
  }
  if (uiConfig.logoUrl) {
    if (navLogo) {
      navLogo.src = uiConfig.logoUrl;
      navLogo.classList.remove('hidden');
    }
    if (appIcon) appIcon.href = uiConfig.logoUrl;
  }
  if (uiConfig.logoAlt && navLogo) navLogo.alt = uiConfig.logoAlt;

  renderSuccessMessage();
  renderConflictMessage();
  setPublicationOptions(uiConfig.publicationOptions);
  formatRules = normalizeFormatRules(uiConfig.formatRules);
  updateFormatUI();
  updateFormatLabels();
}

function updateFormatLabels() {
  const labels = uiConfig.formatLabels || {};
  const available = uiConfig.availableFormats;
  const select = document.getElementById('format');
  if (!select) return;

  if (available && available.length > 0) {
    // Rebuild the dropdown with only the enabled formats
    select.innerHTML = available.map(key => {
      const label = labels[key] || key;
      return `<option value="${key}">${label}</option>`;
    }).join('');
  } else {
    // No availableFormats config — just rename existing options
    Array.from(select.options).forEach(option => {
      if (labels[option.value]) {
        option.textContent = labels[option.value];
      }
    });
  }

  // Re-trigger format UI for the now-selected first option
  select.dispatchEvent(new Event('change'));
}

function renderSuccessMessage() {
  const title = document.getElementById('success-title');
  const body = document.getElementById('success-body');
  if (title) title.textContent = uiConfig.successTitle || defaultUiText.successTitle;
  if (body) body.innerHTML = uiConfig.successMessage || defaultUiText.successMessage;
}

function renderConflictMessage() {
  const body = document.getElementById('conflict-body');
  if (body) body.innerHTML = uiConfig.alreadySubmittedMessage || defaultUiText.alreadySubmittedMessage;
}

function normalizePublicationOptions(options) {
  const raw = Array.isArray(options) ? options : String(options || '').split(/\r?\n/);
  const cleaned = raw.map(option => String(option || '').trim()).filter(Boolean);
  return cleaned.length ? Array.from(new Set(cleaned)) : defaultPublicationOptions.slice();
}

function setPublicationOptions(options) {
  publicationOptions = normalizePublicationOptions(options);
  const selected = publicationInput.value || publicationOptions[0];
  publicationInput.innerHTML = '';
  publicationOptions.forEach(option => {
    const item = document.createElement('option');
    item.value = option;
    item.textContent = option;
    publicationInput.appendChild(item);
  });
  publicationInput.value = publicationOptions.includes(selected) ? selected : publicationOptions[0];
}

formatSelect.addEventListener('change', updateFormatUI);
updateFormatUI();
loadConfig();
