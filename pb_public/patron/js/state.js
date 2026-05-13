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

export let authToken = '';
export function setAuthToken(token) {
  authToken = token || '';
}

export let lastSelectedFormat = formatSelect ? formatSelect.value : 'book';
export function setLastSelectedFormat(format) {
  lastSelectedFormat = format || 'book';
}
