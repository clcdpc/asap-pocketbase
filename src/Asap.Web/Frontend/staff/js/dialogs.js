export function showToast(message, type = 'success', id = '') {
  const container = document.getElementById('toast-container');
  if (!container) return;

  const toast = document.createElement('div');
  toast.className = `asap-toast asap-toast-${type}`;
  if (id) {
    document.getElementById(id)?.remove();
    toast.id = id;
  }
  toast.textContent = message;
  container.appendChild(toast);

  requestAnimationFrame(() => toast.classList.add('show'));

  setTimeout(() => {
    toast.classList.remove('show');
    setTimeout(() => toast.remove(), 300);
  }, 3500);
}

export function showAlert(message) {
  return new Promise(resolve => {
    const dialog = document.getElementById('alert-dialog');
    if (!dialog) return resolve();

    const previousFocus = document.activeElement;
    document.getElementById('alert-dialog-message').textContent = message;
    const okBtn = document.getElementById('alert-dialog-ok');

    let settled = false;

    function cleanup(restoreFocus = true) {
      if (settled) return;
      settled = true;
      if (dialog.open) dialog.close();
      okBtn.removeEventListener('click', onOk);
      dialog.removeEventListener('cancel', onCancel);
      dialog.removeEventListener('close', onClose);
      if (restoreFocus && previousFocus && typeof previousFocus.focus === 'function') {
        previousFocus.focus();
      }
      resolve();
    }

    function onOk() {
      cleanup();
    }

    function onCancel(event) {
      event.preventDefault();
      cleanup();
    }

    function onClose() {
      cleanup(false);
    }

    okBtn.addEventListener('click', onOk);
    dialog.addEventListener('cancel', onCancel);
    dialog.addEventListener('close', onClose);
    dialog.showModal();
    okBtn.focus();
  });
}

export function showConfirm(titleOrMessage, maybeMessage) {
  return new Promise(resolve => {
    const dialog = document.getElementById('confirm-dialog');
    if (!dialog) return resolve(false);

    const previousFocus = document.activeElement;
    const message = maybeMessage || titleOrMessage;
    const title = maybeMessage ? titleOrMessage : 'Confirm action';
    const titleEl = document.getElementById('confirm-dialog-title');
    if (titleEl) titleEl.textContent = title;

    document.getElementById('confirm-dialog-message').textContent = message;
    const okBtn = document.getElementById('confirm-dialog-ok');
    const cancelBtn = document.getElementById('confirm-dialog-cancel');

    let settled = false;

    function cleanup(result) {
      if (settled) return;
      settled = true;
      if (dialog.open) dialog.close();
      okBtn.removeEventListener('click', onOk);
      cancelBtn.removeEventListener('click', onCancel);
      dialog.removeEventListener('cancel', onDialogCancel);
      dialog.removeEventListener('close', onDialogClose);
      if (previousFocus && typeof previousFocus.focus === 'function') {
        previousFocus.focus();
      }
      resolve(result);
    }

    function onOk() {
      cleanup(true);
    }

    function onCancel() {
      cleanup(false);
    }

    function onDialogCancel(event) {
      event.preventDefault();
      cleanup(false);
    }

    function onDialogClose() {
      if (!settled) {
        settled = true;
        okBtn.removeEventListener('click', onOk);
        cancelBtn.removeEventListener('click', onCancel);
        dialog.removeEventListener('cancel', onDialogCancel);
        dialog.removeEventListener('close', onDialogClose);
        resolve(false);
      }
    }

    okBtn.addEventListener('click', onOk);
    cancelBtn.addEventListener('click', onCancel);
    dialog.addEventListener('cancel', onDialogCancel);
    dialog.addEventListener('close', onDialogClose);
    dialog.showModal();
    cancelBtn.focus();
  });
}

export function closeOpenDialogs() {
  document.querySelectorAll('dialog[open]').forEach(dialog => {
    try {
      dialog.close();
    } catch (err) {}
  });
}
