export const loginForm = document.getElementById('login-form');
export const suggestionForm = document.getElementById('suggestion-form');

export const stepLogin = document.getElementById('step-login');
export const stepForm = document.getElementById('step-form');
export const stepSuccess = document.getElementById('step-success');
export const stepConflict = document.getElementById('step-conflict');

export const formatSelect = document.getElementById('format');
export const physicalFields = document.getElementById('physical-fields');
export const econtentFields = document.getElementById('econtent-fields');

export const authorInput = document.getElementById('author');
export const titleInput = document.getElementById('title');
export const publicationInput = document.getElementById('publication');

export const defaultPublicationOptions = ['Already published', 'Coming soon', 'Published a while back'];
export const formatKeys = ['book', 'audiobook_cd', 'dvd', 'music_cd', 'ebook', 'eaudiobook'];
export const fieldKeys = ['title', 'author', 'identifier', 'publication'];

export let additionalFieldDefinitions = [];
export function setAdditionalFieldDefinitions(definitions) {
  additionalFieldDefinitions = Array.isArray(definitions) ? definitions : [];
}

function readStoredToken() {
  try {
    return sessionStorage.getItem('asap_patron_token') || '';
  } catch {
    return '';
  }
}

// Read persistence once; the live token owns this page even if later writes fail.
export let authToken = readStoredToken();
export function setAuthToken(token) {
  authToken = token || '';
  try {
    if (authToken) {
      sessionStorage.setItem('asap_patron_token', authToken);
    } else {
      sessionStorage.removeItem('asap_patron_token');
    }
  } catch {
    // Persistence is optional; current-page authentication has already changed.
  }
}

let rememberedLibraryOrgId;
export function getRememberedLibraryOrgId() {
  if (rememberedLibraryOrgId === undefined) {
    try {
      rememberedLibraryOrgId = localStorage.getItem('asap_patron_library_org_id') || '';
    } catch {
      rememberedLibraryOrgId = '';
    }
  }
  return rememberedLibraryOrgId;
}

export function rememberLibraryOrgId(orgId) {
  const clean = String(orgId || '').trim();
  if (!clean) return;
  rememberedLibraryOrgId = clean;
  try {
    localStorage.setItem('asap_patron_library_org_id', clean);
  } catch {
    // Keep the accepted selection in memory when preferences cannot be saved.
  }
}

export let submitOutcomeUnknown = false;
export function setSubmitOutcomeUnknown(value) {
  submitOutcomeUnknown = Boolean(value);
}

export let submitInProgress = false;
export function setSubmitInProgress(value) {
  submitInProgress = Boolean(value);
}

export let lastSelectedFormat = formatSelect ? formatSelect.value : 'book';
export function setLastSelectedFormat(format) {
  lastSelectedFormat = format || 'book';
}
