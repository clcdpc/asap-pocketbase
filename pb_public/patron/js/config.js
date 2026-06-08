import { defaultPublicationOptions, fieldKeys, formatKeys, setAdditionalFieldDefinitions } from './state.js';
import { loadPatronConfig } from './api.js';
import { normalizeFormatRules } from './form-rules.js';

export const defaultFormatRules = {
  book: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'Identifier number' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  audiobook_cd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'Identifier number' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  dvd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Director/Actors/Producer' },
      identifier: { mode: 'hidden', label: 'UPC' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  music_cd: {
    messageBehavior: 'none',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Artist' },
      identifier: { mode: 'hidden', label: 'UPC' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  ebook: {
    messageBehavior: 'message',
    message: '<p>This is an eBook suggestion, please use Libby to notify us of your interest.</p><p><a href="https://help.libbyapp.com/en-us/6260.htm" target="_blank" rel="noreferrer">Learn how to suggest a purchase using Libby here.</a></p>',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'Identifier number' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  },
  eaudiobook: {
    messageBehavior: 'message',
    message: '<p>This is an eAudiobook suggestion, please use Libby to notify us of your interest.</p><p><a href="https://help.libbyapp.com/en-us/6260.htm" target="_blank" rel="noreferrer">Learn how to suggest a purchase using Libby here.</a></p>',
    fields: {
      title: { mode: 'required', label: 'Title' },
      author: { mode: 'required', label: 'Author' },
      identifier: { mode: 'optional', label: 'Identifier number' },
      publication: { mode: 'required', label: 'Publication Timing' }
    }
  }
};

export const defaultUiText = {
  successTitle: 'Suggestion Submitted',
  successMessage: 'You have successfully submitted your material suggestion! Check your email inbox for status updates.<div>Thank you for using our suggestion service.</div>',
  alreadySubmittedMessage: 'This suggestion has already been submitted from your account. Your previous request was submitted on {{duplicate_date}} and is currently {{duplicate_status}}.<div>Thank you for using this library\'s suggestion service.</div>',
  pageTitle: 'Material Suggestion',
  barcodeLabel: 'Library Card',
  pinLabel: 'Pin',
  publicationOptions: defaultPublicationOptions,
  formatRules: defaultFormatRules
};

export let publicationOptions = defaultPublicationOptions.slice();
export let uiConfig = { ...defaultUiText };
export let formatRules = normalizeFormatRules(defaultFormatRules, defaultFormatRules, formatKeys, fieldKeys);

export function getConfigUrl() {
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

export function normalizeUiConfig(config) {
  if (!config) return {};
  if (config.ui_text && typeof config.ui_text === 'object') {
    return { ...config.ui_text, logoUrl: config.logoUrl || config.ui_text.logoUrl };
  }
  return config;
}

export function normalizePublicationOptions(options) {
  const raw = Array.isArray(options) ? options : String(options || '').split(/\r?\n/);
  const cleaned = raw
    .filter(option => !(option && typeof option === 'object') || option.enabled !== false)
    .map(option => String(option && typeof option === 'object' ? option.label : option || '').trim())
    .filter(Boolean);
  return cleaned.length ? Array.from(new Set(cleaned)) : defaultPublicationOptions.slice();
}

export function setPublicationOptions(options) {
  const next = normalizePublicationOptions(options);
  publicationOptions.splice(0, publicationOptions.length, ...next);
}

export function applyLoadedUiText(config) {
  const nextConfig = normalizeUiConfig(config);
  Object.assign(uiConfig, nextConfig);
  if (uiConfig.formatRules) {
    const nextRules = normalizeFormatRules(uiConfig.formatRules, defaultFormatRules, formatKeys, fieldKeys);
    // Clear existing keys to ensure a clean merge
    Object.keys(formatRules).forEach(key => delete formatRules[key]);
    Object.assign(formatRules, nextRules);
    uiConfig.formatRules = formatRules;
  }
  setAdditionalFieldDefinitions(Array.isArray(uiConfig.additionalFieldDefinitions) ? uiConfig.additionalFieldDefinitions : []);
  setPublicationOptions(uiConfig.publicationOptions);
  return nextConfig;
}

export function applySuccessConfig(config) {
  const nextConfig = normalizeUiConfig(config);
  if (nextConfig.successTitle || nextConfig.successMessage) {
    applyLoadedUiText(nextConfig);
  }
}

export async function loadInitialConfig() {
  try {
    applyLoadedUiText(await loadPatronConfig(getConfigUrl()));
  } catch (err) {
    console.error('Failed to load config', err);
  }
}
