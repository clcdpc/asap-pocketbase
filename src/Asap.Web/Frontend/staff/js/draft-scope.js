// One mounted owner owns this scope. Handles carry no editable authority.
export function createDraftScope() {
  const drafts = new Map();
  const controlValues = new WeakMap();
  const observers = new Map();
  let disposed = false;
  let revision = 0;
  let stamp = Symbol('draft revision');

  function changed() { revision += 1; stamp = Symbol('draft revision'); }
  const controlValue = control => JSON.stringify([control.value, control.checked]);

  function prune() {
    for (const [handle, draft] of drafts) {
      if (!draft.root.isConnected) { drafts.delete(handle); changed(); }
    }
  }

  return {
    register({ root, isDirty, kind = 'inline', metadata = null }) {
      if (disposed || !root || typeof isDirty !== 'function') {
        throw new Error('A live draft owner and dirty predicate are required.');
      }
      const handle = Symbol('draft');
      drafts.set(handle, { root, isDirty, kind, metadata });
      const document = root.ownerDocument, Observer = document?.defaultView?.MutationObserver;
      if (Observer && !observers.has(document)) {
        // A later reinsertion must not revive an owner that was disconnected.
        // Mounted reorders within the same turn retain their existing owner.
        const observer = new Observer(prune);
        observer.observe(document, { childList: true, subtree: true });
        observers.set(document, observer);
      }
      for (const control of root.querySelectorAll?.('input, select, textarea') || []) {
        controlValues.set(control, controlValue(control));
      }
      changed();
      return handle;
    },
    release(handle) { if (drafts.delete(handle)) changed(); },
    owns(handle) {
      const draft = drafts.get(handle);
      if (draft && !draft.root.isConnected) { drafts.delete(handle); changed(); }
      return !disposed && drafts.has(handle);
    },
    touch(control = null) {
      if (disposed) return;
      // Moving focus into a dialog can emit change after input for the same
      // value. That blur is not a new draft and must not revoke its consent.
      if (control && 'value' in control) {
        const value = controlValue(control);
        if (controlValues.get(control) === value) return;
        controlValues.set(control, value);
      }
      changed();
    },
    stamp: () => stamp,
    revision: () => revision,
    isDirty() {
      prune();
      return !disposed && [...drafts.values()].some(draft => draft.isDirty());
    },
    admit(declaration) {
      if (!declaration || !Object.hasOwn(declaration, 'consumes')) {
        return { allowed: false, reason: 'missing_consumption' };
      }
      prune();
      if (disposed || (declaration.consumes !== null && !drafts.has(declaration.consumes))) {
        return { allowed: false, reason: 'invalid_owner' };
      }
      const preserves = declaration.preserves ?? [];
      if (!Array.isArray(preserves) || (preserves.length > 0 && declaration.consumes === null) ||
          preserves.some(handle => !drafts.has(handle))) {
        return { allowed: false, reason: 'invalid_preservation' };
      }
      const blockers = [];
      for (const [handle, draft] of drafts) {
        if (handle !== declaration.consumes && !preserves.includes(handle) && draft.isDirty()) {
          // Controllers keep handles and navigation callbacks internal. Only
          // their deliberately public descriptions belong in messages/DOM.
          blockers.push({ handle, kind: draft.kind, metadata: draft.metadata });
        }
      }
      if (blockers.length > 0) {
        return { allowed: false, reason: 'competing_draft', kind: blockers[0].kind, blockers };
      }
      return { allowed: true };
    },
    dispose() { disposed = true; drafts.clear(); for (const observer of observers.values()) observer.disconnect(); observers.clear(); }
  };
}
