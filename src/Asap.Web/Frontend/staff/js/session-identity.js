function actorKey(staff) {
  return staff ? [staff.tenantId, staff.id, staff.authenticationEmail, staff.role, staff.organizationId]
    .map(value => String(value ?? '')).join('|') : null;
}

export function createSessionIdentity() {
  let actor = null;
  let preferences = null;
  let epoch = 0;
  let revision = 0;
  const snapshots = new WeakMap();

  function accept(staff) {
    const key = actorKey(staff);
    if (key !== actor?.key) {
      epoch += 1;
      actor = staff ? Object.freeze({ key, epoch,
        tenantId: staff.tenantId, id: String(staff.id),
        authenticationEmail: staff.authenticationEmail,
        role: staff.role, organizationId: staff.organizationId }) : null;
    }
    preferences = staff ? Object.freeze({ ...staff }) : null;
    revision += 1;
    if (preferences) snapshots.set(preferences, epoch);
    return preferences;
  }

  function isCurrent(snapshot) {
    return Boolean(actor && snapshot && snapshots.get(snapshot) === epoch);
  }

  return {
    accept,
    isCurrent,
    sameSession(left, right) { return Boolean(left && right && snapshots.has(left) && snapshots.get(left) === snapshots.get(right)); },
    actor: () => actor,
    preferences: () => preferences,
    revision: () => revision,
    updatePreferences(staff, owner) {
      if (!isCurrent(owner) || actorKey(staff) !== actor.key) return null;
      return accept(staff);
    },
    clear() { accept(null); }
  };
}
