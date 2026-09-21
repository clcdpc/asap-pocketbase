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
