// Drive the actual app dialog, including its asynchronous settlement. Tests can
// return null to leave a decision open and exercise changes during the wait.
exports.answerConfirmations = (window, response = () => true) => {
  if (!window.HTMLDialogElement.prototype.showModal) {
    window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    window.HTMLDialogElement.prototype.close = function () { this.open = false; };
  }
  if (window.__confirmationAnswers) {
    window.__confirmationAnswers.response = response;
    return;
  }
  const state = { response, seen: false };
  const document = window.document;
  window.__confirmationAnswers = state;
  const observer = new window.MutationObserver(() => {
    const dialog = document.querySelector('#confirmation-dialog');
    if (!dialog?.open) { state.seen = false; return; }
    if (state.seen) return;
    state.seen = true;
    const result = state.response(dialog.querySelector('#confirmation-message').textContent, dialog);
    if (typeof result === 'boolean') dialog.querySelector(result ? '.form-actions button:last-child' : '.form-actions button:first-child').click();
  });
  observer.observe(window.document.body, { subtree: true, attributes: true, childList: true });
  window.addEventListener('unload', () => observer.disconnect(), { once: true });
};
