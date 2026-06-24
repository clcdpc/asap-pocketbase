export function toggleTimeoutGroup() {
  const group = document.getElementById('timeout-config-group');
  const enabled = document.getElementById('outstanding-timeout-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}

export function toggleHoldPickupTimeoutGroup() {
  const group = document.getElementById('hold-pickup-timeout-group');
  const enabled = document.getElementById('hold-pickup-timeout-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}

export function togglePendingHoldTimeoutGroup() {
  const group = document.getElementById('pending-hold-timeout-group');
  const enabled = document.getElementById('pending-hold-timeout-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}

export function toggleAdditionalCopyTimeoutGroup() {
  const group = document.getElementById('additional-copy-timeout-group');
  const enabled = document.getElementById('additional-copy-timeout-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}

export function toggleCommonAuthorsGroup() {
  const group = document.getElementById('common-authors-config-group');
  const enabled = document.getElementById('wf-common-authors-enabled').checked;
  if (enabled) {
    group.classList.remove('hidden');
  } else {
    group.classList.add('hidden');
  }
}
