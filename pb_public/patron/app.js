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
const defaultAgeGroups = ['Adult', 'Young Adult / Teen', 'Children'];
const formatKeys = ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];
const fieldKeys = ['title', 'author', 'identifier', 'agegroup', 'publication'];
const defaultFormatRules = {
  book: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'Identifier number' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  audiobook_cd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'Identifier number' },
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
      identifier: { mode: 'optional', label: 'Identifier number' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  eaudiobook: {
    messageBehavior: 'eaudiobookMessage',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'Identifier number' },
      agegroup: { mode: 'required', label: 'Age Group' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  }
};
const defaultUiText = {
  successTitle: 'Suggestion Submitted',
  successMessage: 'You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>',
  alreadySubmittedMessage: 'This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library\'s suggestion service.</div>',
  pageTitle: 'Material Suggestion',
  barcodeLabel: 'Library Card',
  pinLabel: 'Pin',
  publicationOptions: defaultPublicationOptions,
  ageGroups: defaultAgeGroups,
  formatRules: defaultFormatRules
};
let publicationOptions = defaultPublicationOptions.slice();
let ageGroups = defaultAgeGroups.slice();
let uiConfig = { ...defaultUiText };
let formatRules = normalizeFormatRules(defaultFormatRules);

let authToken = '';

function getApiUrl(path) {
  return window.location.origin + path;
}

function escapeHtml(str) {
  return String(str || "")
    .replace(/&/g, "&amp;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;");
}

function sanitizeHtml(html) {
  if (!html) return "";
  try {
    const doc = new DOMParser().parseFromString(html, "text/html");
    const safeTags = ["P", "BR", "B", "I", "STRONG", "EM", "DIV", "SPAN", "A", "UL", "OL", "LI", "H1", "H2", "H3", "H4", "H5", "H6", "BLOCKQUOTE", "TABLE", "THEAD", "TBODY", "TR", "TH", "TD", "U", "S", "HR"];
    const safeAttrs = ["href", "target", "rel", "title", "class", "id", "aria-label", "aria-hidden"];
    
    // Recursive walker to remove unsafe tags and attributes
    function walk(parent) {
      const children = Array.from(parent.childNodes);
      children.forEach(node => {
        if (node.nodeType === 1) { // Element
          if (!safeTags.includes(node.tagName)) {
            // Unsafe tag: replace with its text content
            const text = document.createTextNode(node.textContent);
            parent.replaceChild(text, node);
          } else {
            // Safe tag: check attributes
            const attrs = node.attributes;
            for (let i = attrs.length - 1; i >= 0; i--) {
              const name = attrs[i].name.toLowerCase();
              const value = attrs[i].value.trim().toLowerCase();
              
              if (!safeAttrs.includes(name) || (name === "href" && value.startsWith("javascript:"))) {
                node.removeAttribute(name);
              }
            }
            walk(node);
          }
        }
      });
    }
    
    walk(doc.body);
    return doc.body.innerHTML;
  } catch (err) {
    return escapeHtml(html);
  }
}

async function request(path, options = {}) {
  const headers = { 'Content-Type': 'application/json' };
  if (authToken) headers['Authorization'] = authToken;
  const response = await fetch(getApiUrl(path), {
    ...options,
    headers: { ...headers, ...options.headers }
  });
  
  if (!response.ok) {
    if (response.status === 401 && !path.endsWith('/login')) {
      authToken = '';
      const errorDiv = document.getElementById('login-error');
      errorDiv.textContent = 'Your session has expired. Please log in again.';
      errorDiv.classList.remove('hidden');
      showStep(stepLogin);
      const err = new Error('Session expired');
      err.status = 401;
      throw err;
    }
    let message = response.statusText;
    let data = null;
    try {
      data = await response.json();
      if (data && data.message) message = data.message;
    } catch (e) {}
    const err = new Error(message);
    err.status = response.status;
    err.response = data;
    throw err;
  }
  return response.json();
}

function getConfigUrl() {
  const params = new URLSearchParams(window.location.search);
  let orgId = params.get('libraryOrgId');
  if (orgId) {
    localStorage.setItem('asap_patron_library_org_id', orgId);
  } else {
    orgId = localStorage.getItem('asap_patron_library_org_id') || '';
  }
  
  let url = '/api/asap/config?t=' + Date.now();
  if (orgId) {
    url += '&libraryOrgId=' + encodeURIComponent(orgId);
  }
  return url;
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
  
  // Reset login button state
  const btn = document.getElementById('login-btn');
  if (btn) {
    btn.disabled = false;
    btn.textContent = 'Next';
  }
  
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
  btn.textContent = 'Logging in...';
  errorDiv.classList.add('hidden');

  try {
    const fd = new FormData(loginForm);
    const data = Object.fromEntries(fd.entries());
    const result = await request('/api/asap/patron/login', {
      method: 'POST',
      body: JSON.stringify(data)
    });
    
    authToken = result.token;
    
    if (result.record && result.record.libraryOrgId) {
      localStorage.setItem('asap_patron_library_org_id', result.record.libraryOrgId);
    }
    
    if (result.ui_text) {
      Object.assign(uiConfig, result.ui_text);
      if (uiConfig.formatRules) {
        uiConfig.formatRules = normalizeFormatRules(uiConfig.formatRules);
      }
      applyUiConfig();
    }
    
    document.getElementById('display-barcode').textContent = data.username;
    const email = result.email || (result.record && result.record.email);
    if (email) {
      document.getElementById('display-email').textContent = email;
      document.getElementById('no-email-msg').classList.add('hidden');
    } else {
      document.getElementById('display-email').textContent = '';
      const noEmailMsg = document.getElementById('no-email-msg');
      if (uiConfig.noEmailMessage) {
        noEmailMsg.textContent = uiConfig.noEmailMessage;
      } else {
        noEmailMsg.textContent = 'No email is specified on your library account, which means we won\'t be able to send you updates regarding your suggestion. Please contact the library to add an email address to your account if you would like to receive status updates.';
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
    btn.disabled = false;
    btn.textContent = 'Next';
    errorDiv.textContent = err.message || 'Incorrect Login - Please try again';
    errorDiv.classList.remove('hidden');
    errorDiv.focus();
  }
});

suggestionForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  const btn = document.getElementById('submit-btn');
  const errorDiv = document.getElementById('submit-error');
  btn.disabled = true;
  btn.textContent = 'Submitting...';
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
    if (err.status === 401) {
      // Session expired — already handled by request()
    } else if (err.status === 409) {
      const data = err.response || {};
      const conflictTitle = document.getElementById('conflict-title');
      const conflictBody = document.getElementById('conflict-body');
      if (conflictTitle) conflictTitle.textContent = data.conflictTitle || 'Already Submitted';
      if (conflictBody) conflictBody.innerHTML = sanitizeHtml(data.conflictMessage || (err.message ? escapeHtml(err.message) : (uiConfig.alreadySubmittedMessage || defaultUiText.alreadySubmittedMessage)));
      showStep(stepConflict);
    } else {
      if (err.status === 406) {
        errorDiv.textContent = err.message || 'You have reached your weekly suggestion limit.';
      } else {
        errorDiv.textContent = err.message || 'Error. Please try again';
      }
      errorDiv.classList.remove('hidden');
      errorDiv.focus();
    }
  } finally {
    btn.disabled = false;
    btn.textContent = 'Submit';
  }
});

function normalizeMode(value, fallback) {
  return ['required', 'optional', 'hidden'].includes(value) ? value : fallback || 'optional';
}

function normalizeMessageBehavior(value, fallback) {
  return ['none', 'ebookMessage', 'eaudiobookMessage'].includes(value) ? value : fallback || 'none';
}

function normalizeFormatRules(rules) {
  const normalized = structuredClone(defaultFormatRules);
  const incoming = rules && typeof rules === 'object' ? rules : {};

  // Process all format keys: defaults + any custom formats from the backend
  const allKeys = new Set([...formatKeys, ...Object.keys(incoming)]);
  allKeys.forEach(format => {
    const incomingFormat = incoming[format] || {};
    // Create a base entry for custom formats not in defaults
    if (!normalized[format]) {
      normalized[format] = {
        messageBehavior: 'none',
        fields: {}
      };
      fieldKeys.forEach(field => {
        normalized[format].fields[field] = {
          mode: field === 'title' ? 'required' : 'optional',
          label: defaultFormatRules.book.fields[field]?.label || field
        };
      });
    }
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
  const format = formatSelect.value || 'book';
  const rule = formatRules[format] || formatRules.book;
  const messageBehavior = rule.messageBehavior || 'none';

  if (messageBehavior !== 'none') {
    physicalFields.classList.add('hidden');
    econtentFields.classList.remove('hidden');
    document.getElementById('submit-error').classList.add('hidden');

    const msgContainer = document.getElementById('econtent-msg-container');
    msgContainer.innerHTML = sanitizeHtml(messageHtmlForBehavior(messageBehavior));

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
  const barcodeLoginLabel = document.getElementById('lbl-barcode-login');
  const pinLoginLabel = document.getElementById('lbl-pin-login');
  const barcodeDisplayLabel = document.getElementById('lbl-barcode-display');

  if (uiConfig.pageTitle && mainTitle) {
    mainTitle.textContent = uiConfig.pageTitle;
    document.title = uiConfig.pageTitle;
  }

  if (uiConfig.barcodeLabel) {
    if (barcodeLoginLabel) barcodeLoginLabel.textContent = uiConfig.barcodeLabel;
    if (barcodeDisplayLabel) barcodeDisplayLabel.textContent = uiConfig.barcodeLabel;
  }
  if (uiConfig.pinLabel && pinLoginLabel) {
    pinLoginLabel.textContent = uiConfig.pinLabel;
  }

  if (uiConfig.suggestionFormNote && noteText) noteText.textContent = uiConfig.suggestionFormNote;
  if (uiConfig.loginNote && loginNote) loginNote.textContent = uiConfig.loginNote;
  if (uiConfig.loginPrompt && loginPrompt) loginPrompt.textContent = uiConfig.loginPrompt;
  if (uiConfig.noEmailMessage) {
    const noEmailEl = document.getElementById('no-email-msg');
    if (noEmailEl) noEmailEl.textContent = uiConfig.noEmailMessage;
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
  applyCommonAuthors();
}

function applyCommonAuthors() {
  const container = document.getElementById('common-author-container');
  const select = document.getElementById('common-author');
  if (!container || !select) return;

  if (!uiConfig.commonAuthorsEnabled) {
    container.classList.add('hidden');
    document.getElementById('common-author-msg-container').classList.add('hidden');
    physicalFields.classList.remove('hidden');
    document.getElementById('submit-btn').classList.remove('hidden');
    return;
  }

  const authors = (uiConfig.commonAuthorsList || '').split('\n').map(s => s.trim()).filter(Boolean);
  if (authors.length === 0) {
    container.classList.add('hidden');
    return;
  }

  const currentValue = select.value;
  select.innerHTML = '<option value="">-- Select an Author --</option>';
  authors.forEach(author => {
    const opt = document.createElement('option');
    opt.value = author;
    opt.textContent = author;
    select.appendChild(opt);
  });
  select.value = authors.includes(currentValue) ? currentValue : '';

  container.classList.remove('hidden');
  handleCommonAuthorSelection();
}

function handleCommonAuthorSelection() {
  const select = document.getElementById('common-author');
  const msgContainer = document.getElementById('common-author-msg-container');
  const msgText = document.getElementById('common-author-msg');
  const submitBtn = document.getElementById('submit-btn');

  if (select.value) {
    msgText.textContent = uiConfig.commonAuthorsMessage || "We automatically purchase all upcoming titles by this author. Please check the catalog to place a hold on 'On Order' items.";
    msgContainer.classList.remove('hidden');
    physicalFields.classList.add('hidden');
    submitBtn.classList.add('hidden');
  } else {
    msgContainer.classList.add('hidden');
    updateFormatUI(); // This will show physical-fields if it's not econtent
    submitBtn.classList.remove('hidden');
  }
}

document.getElementById('common-author').addEventListener('change', handleCommonAuthorSelection);

function updateFormatLabels() {
  const labels = uiConfig.formatLabels || {};
  const available = uiConfig.availableFormats;
  const select = document.getElementById('format');
  if (!select) return;

  if (available && available.length > 0) {
    // Rebuild the dropdown with only the enabled formats
    select.innerHTML = available.map(key => {
      const label = labels[key] || key;
      return `<option value="${escapeHtml(key)}">${escapeHtml(label)}</option>`;
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
  if (body) body.innerHTML = sanitizeHtml(uiConfig.successMessage || defaultUiText.successMessage);
}

function renderConflictMessage() {
  const body = document.getElementById('conflict-body');
  if (body) body.innerHTML = sanitizeHtml(uiConfig.alreadySubmittedMessage || defaultUiText.alreadySubmittedMessage);
}

function normalizePublicationOptions(options) {
  const raw = Array.isArray(options) ? options : String(options || '').split(/\r?\n/);
  const cleaned = raw
    .filter(option => !(option && typeof option === 'object') || option.enabled !== false)
    .map(option => String(option && typeof option === 'object' ? option.label : option || '').trim())
    .filter(Boolean);
  return cleaned.length ? Array.from(new Set(cleaned)) : defaultPublicationOptions.slice();
}

function setPublicationOptions(options) {
  publicationOptions = normalizePublicationOptions(options);
  let ageGroups = defaultAgeGroups;
  if (uiConfig.ageGroups && Array.isArray(uiConfig.ageGroups)) {
    ageGroups = uiConfig.ageGroups
      .filter(option => !(option && typeof option === 'object') || option.enabled !== false)
      .map(option => String(option && typeof option === 'object' ? option.label : option || '').trim())
      .filter(Boolean);
  }
  
  const pubSelect = document.getElementById('publication');
  pubSelect.innerHTML = '';
  publicationOptions.forEach(opt => {
    const el = document.createElement('option');
    el.value = opt;
    el.textContent = opt;
    pubSelect.appendChild(el);
  });
  
  const ageSelect = document.getElementById('agegroup');
  ageSelect.innerHTML = '';
  ageGroups.forEach(opt => {
    const el = document.createElement('option');
    el.value = opt;
    el.textContent = opt;
    ageSelect.appendChild(el);
  });

  const selected = publicationInput.value;
  publicationInput.value = publicationOptions.includes(selected) ? selected : publicationOptions[0];
}

formatSelect.addEventListener('change', updateFormatUI);
updateFormatUI();
loadConfig();
