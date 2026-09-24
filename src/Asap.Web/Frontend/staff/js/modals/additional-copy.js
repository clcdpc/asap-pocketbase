import { staffSession, staffAccessGeneration } from '../state.js';

export function confirmAdditionalCopyAction(result, options = {}) {
  return new Promise(resolve => {
    const previousFocus = document.activeElement;
    const accessGeneration = staffAccessGeneration;
    const dialog = document.createElement('dialog');
    dialog.className = 'asap-dialog asap-dialog-small';

    const body = document.createElement('div');
    body.className = 'asap-dialog-small-body';

    const title = document.createElement('h2');
    title.className = 'h5 mb-3';
    title.textContent = 'Buy another copy + Queue Now';

    const message = document.createElement('p');
    message.className = 'dialog-message';
    const bibId = result && result.bibId ? String(result.bibId) : '';
    if (options.message) {
      message.textContent = options.message;
    } else {
      message.textContent = bibId
        ? `Create an additional-copy task for BIB ${bibId} and queue the patron hold on this same BIB?`
        : 'Create an additional-copy task and queue the patron hold on this same BIB?';
    }

    const checkboxGroup = document.createElement('div');
    checkboxGroup.className = 'custom-control custom-checkbox mb-4';

    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.id = 'confirm-additional-copy-reminder';
    checkbox.className = 'custom-control-input';
    checkbox.checked = Object.prototype.hasOwnProperty.call(options, 'emailPurchaseReminderDefault')
      ? !!options.emailPurchaseReminderDefault
      : !!staffSession.staff?.additional_copy_reminder_default;

    const label = document.createElement('label');
    label.className = 'custom-control-label font-weight-bold';
    label.setAttribute('for', 'confirm-additional-copy-reminder');
    label.textContent = 'Email me a purchase reminder';

    checkboxGroup.append(checkbox, label);

    const actions = document.createElement('div');
    actions.className = 'asap-dialog-actions';

    const cancelBtn = document.createElement('button');
    cancelBtn.type = 'button';
    cancelBtn.className = 'btn btn-sm btn-secondary';
    cancelBtn.textContent = 'Cancel';

    const okBtn = document.createElement('button');
    okBtn.type = 'button';
    okBtn.className = 'btn btn-sm btn-success';
    okBtn.textContent = 'Confirm';

    actions.append(cancelBtn, okBtn);

    body.append(title, message, checkboxGroup, actions);
    dialog.append(body);
    document.body.appendChild(dialog);

    let settled = false;
    function cleanup(resultValue) {
      if (settled) return;
      settled = true;
      if (dialog.open) dialog.close();
      dialog.remove();
      if (staffAccessGeneration === accessGeneration && staffSession.authenticated && staffSession.accessAllowed &&
          previousFocus && typeof previousFocus.focus === 'function') previousFocus.focus();
      resolve(resultValue);
    }
    cancelBtn.addEventListener('click', () => cleanup({ confirmed: false, emailPurchaseReminder: false }));
    okBtn.addEventListener('click', () => cleanup({ confirmed: true, emailPurchaseReminder: checkbox.checked }));
    dialog.addEventListener('cancel', event => {
      event.preventDefault();
      cleanup({ confirmed: false, emailPurchaseReminder: false });
    });
    dialog.addEventListener('close', () => cleanup({ confirmed: false, emailPurchaseReminder: false }));
    dialog.showModal();
    cancelBtn.focus();
  });
}
