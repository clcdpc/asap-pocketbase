// One mounted owner owns this scope. Handles carry no editable authority.
export function createDraftScope() {
  const drafts = new Map();
  let disposed = false;
  let revision = 0;
  let stamp = Symbol('draft revision');

  function changed() { revision += 1; stamp = Symbol('draft revision'); }

  function prune() {
    for (const [handle, draft] of drafts) {
      if (!draft.root.isConnected) { drafts.delete(handle); changed(); }
    }
  }

  return {
    register({ root, isDirty, kind = 'inline' }) {
      if (disposed || !root || typeof isDirty !== 'function') {
        throw new Error('A live draft owner and dirty predicate are required.');
      }
      const handle = Symbol('draft');
      drafts.set(handle, { root, isDirty, kind });
      changed();
      return handle;
    },
    release(handle) { if (drafts.delete(handle)) changed(); },
    touch() { if (!disposed) changed(); },
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
      for (const [handle, draft] of drafts) {
        if (handle !== declaration.consumes && draft.isDirty()) {
          return { allowed: false, reason: 'competing_draft', kind: draft.kind };
        }
      }
      return { allowed: true };
    },
    dispose() { disposed = true; drafts.clear(); }
  };
}
