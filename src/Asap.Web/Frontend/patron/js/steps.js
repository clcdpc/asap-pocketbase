import { stepConflict, stepForm, stepLogin, stepSuccess } from './state.js';
import { byId } from './dom.js';

const stepTitles = new Map([
  [stepLogin, 'Login Step'],
  [stepForm, 'Suggestion Details Step'],
  [stepSuccess, 'Success'],
  [stepConflict, 'Conflict - Already Submitted']
]);

export function showStep(stepElement) {
  [stepLogin, stepForm, stepSuccess, stepConflict].forEach(el => {
    if (el) el.classList.add('hidden');
  });
  if (stepElement) stepElement.classList.remove('hidden');

  const announcer = byId('status-announcer');
  if (announcer) {
    announcer.textContent = 'Navigated to ' + (stepTitles.get(stepElement) || '');
  }

  const firstInput = stepElement ? stepElement.querySelector('input, select, textarea, button, h2') : null;
  if (firstInput) firstInput.focus();
}

export function showLoginStep() {
  showStep(stepLogin);
}

export function showSuggestionStep() {
  showStep(stepForm);
}

export function showSuccessStep() {
  showStep(stepSuccess);
}

export function showConflictStep() {
  showStep(stepConflict);
}
