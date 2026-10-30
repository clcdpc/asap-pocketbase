import { element } from './ui.js';

const controllers = new WeakMap();

// Also detect programmatic value changes that did not emit input/change.
export function controlSnapshot(root) {
  return JSON.stringify([...root.querySelectorAll('input, select, textarea')]
    .map(control => [control.value, control.checked]));
}

// One top-layer decision per document. Owners supply a live predicate covering
// their session, route, version, draft and command state; consent is never a lock.
export function createConfirmationController() {
  const root = element('dialog', { id: 'confirmation-dialog', 'aria-labelledby': 'confirmation-title',
    'aria-describedby': 'confirmation-message' });
  const title = element('h2', { id: 'confirmation-title' });
  const message = element('p', { id: 'confirmation-message' });
  const actions = element('div', { className: 'form-actions' });
  root.append(element('div', { className: 'dialog-shell compact-dialog-shell' }, [
    element('header', { className: 'dialog-header' }, [title]),
    element('div', { className: 'dialog-body' }, [message, actions])
  ]));
  document.body.append(root);
  const events = new window.AbortController();
  let pending = null, disposed = false, cancel = null, approve = null;

  function finish(accepted = false) {
    const decision = pending;
    if (!decision) return;
    const current = decision.isCurrent();
    pending = null;
    decision.controls.abort();
    window.clearInterval(decision.timer);
    decision.signal?.removeEventListener('abort', decision.abort);
    if (root.open) root.close();
    actions.replaceChildren(); title.textContent = ''; message.textContent = '';
    // Never focus an opener in a retired view, disconnected form or closed modal.
    const canFocus = target => target?.isConnected && !target.disabled && !target.closest('[hidden], [inert]') &&
      (!target.closest('dialog') || target.closest('dialog').open);
    const opener = decision.opener?.closest('details:not([open])')?.querySelector('summary') || decision.opener;
    const target = canFocus(opener) ? opener : decision.fallback;
    if (current && canFocus(target)) target.focus();
    decision.resolve(accepted && current);
  }
  root.addEventListener('cancel', event => { event.preventDefault(); finish(); }, { signal: events.signal });
  root.addEventListener('close', () => { if (!root.open) finish(); }, { signal: events.signal });
  root.addEventListener('keydown', event => {
    if (event.key === 'Tab') {
      if (event.shiftKey && document.activeElement === cancel) { event.preventDefault(); approve.focus(); }
      else if (!event.shiftKey && document.activeElement === approve) { event.preventDefault(); cancel.focus(); }
    }
    // Enter elsewhere must not turn an opening keystroke into affirmative consent.
    if (event.key === 'Enter' && event.target !== approve) { event.preventDefault(); finish(); }
  }, { signal: events.signal });

  return {
    ask({ title: heading, message: description, confirmLabel, cancelLabel = 'Cancel', tone = 'normal',
      opener = document.activeElement, fallback = opener?.closest('dialog')?.querySelector('button'), isCurrent, signal }) {
      if (pending && !pending.isCurrent()) finish();
      if (disposed || pending || signal?.aborted || !isCurrent()) return Promise.resolve(false);
      title.textContent = heading;
      message.textContent = description;
      // Retire the controls as well as the promise. A queued click on an old
      // decision must never approve its replacement, even in the same owner.
      cancel = element('button', { type: 'button', className: 'secondary-button', text: cancelLabel });
      approve = element('button', { type: 'button', className: tone === 'danger' ? 'danger-button' : 'primary-button', text: confirmLabel });
      actions.replaceChildren(cancel, approve);
      return new Promise(resolve => {
        const controls = new window.AbortController();
        const decision = { resolve, opener, fallback, signal, isCurrent, controls, abort: () => finish() };
        pending = decision;
        cancel.addEventListener('click', () => { if (pending === decision) finish(); }, { signal: controls.signal });
        approve.addEventListener('click', () => { if (pending === decision) finish(true); }, { signal: controls.signal });
        signal?.addEventListener('abort', decision.abort, { once: true });
        decision.timer = window.setInterval(() => {
          if (!root.isConnected || !root.open || !isCurrent()) finish();
        }, 50);
        try { root.showModal(); }
        catch { finish(); return; }
        cancel.focus();
      });
    },
    cancel: () => finish(),
    dispose() { if (disposed) return; finish(); disposed = true; events.abort(); root.remove(); }
  };
}

export function confirmAction(options) {
  let controller = controllers.get(document);
  if (!controller) { controller = createConfirmationController(); controllers.set(document, controller); }
  return controller.ask(options);
}

export function cancelConfirmations() { controllers.get(document)?.cancel(); }
export function disposeConfirmations() { controllers.get(document)?.dispose(); controllers.delete(document); }
