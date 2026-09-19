import { isValidSmtpHost, validateSmtpHostField } from '../api.js';

export function dateOnly(value) {
  value = String(value || '').trim();
  return value ? value.split(' ')[0].split('T')[0] : '';
}

export function syncInputPair(idA, idB) {
  const elA = document.getElementById(idA);
  const elB = document.getElementById(idB);
  if (elA && elB) {
    elA.addEventListener('input', (e) => elB.value = e.target.value);
    elB.addEventListener('input', (e) => elA.value = e.target.value);
  }
}

syncInputPair('email-from-address', 'smtp-from');
syncInputPair('email-from-name', 'smtp-from-name');
const smtpHostInput = document.getElementById('smtp-host');
if (smtpHostInput) {
  smtpHostInput.addEventListener('blur', () => validateSmtpHostField(true));
  smtpHostInput.addEventListener('input', () => {
    if (isValidSmtpHost(smtpHostInput.value) || !smtpHostInput.value.trim()) {
      const resultEl = document.getElementById('smtp-test-result');
      if (resultEl && resultEl.className.includes('text-danger') && resultEl.textContent.includes('SMTP host')) {
        resultEl.textContent = '';
        resultEl.className = 'd-block mt-2';
      }
    }
  });
}
