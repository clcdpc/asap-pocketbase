const choices = new Set(['all', 'mine_unclaimed', 'mine', 'unclaimed']);
const storagePrefix = 'asap.staff.claims.';

// Only an explicit choice is retained. Profile remains the default until then.
// Disabled browser storage still permits a shared choice for this page's lifetime.
export function createClaimsFilter({ sessionIdentity, storage = () => window.sessionStorage }) {
  const retained = new Map(), controls = new Set();

  function value() {
    const actor = sessionIdentity.actor(), staff = sessionIdentity.preferences();
    if (!actor || !sessionIdentity.isCurrent(staff)) return 'all';
    // Authority changes establish a new session epoch, but the retained choice
    // belongs to the same trusted principal, independent of role/library access.
    const key = JSON.stringify([actor.tenantId, actor.id, actor.authenticationEmail]);
    if (!retained.has(key)) {
      let selected = null;
      try {
        const saved = JSON.parse(storage()?.getItem(storagePrefix + encodeURIComponent(key)) ?? 'null');
        if (saved?.staff === key && choices.has(saved.value)) selected = saved.value;
      } catch { /* Storage may be disabled or contain an invalid record. */ }
      retained.set(key, selected);
    }
    return retained.get(key) ?? (staff.defaultMineUnclaimedFilter ? 'mine_unclaimed' : 'all');
  }

  function select(selected, owner) {
    if (!sessionIdentity.isCurrent(owner) || !choices.has(selected)) return false;
    const actor = sessionIdentity.actor();
    const key = JSON.stringify([actor.tenantId, actor.id, actor.authenticationEmail]);
    retained.set(key, selected);
    try {
      storage()?.setItem(storagePrefix + encodeURIComponent(key), JSON.stringify({ staff: key, value: selected }));
    } catch { /* The current page keeps its in-memory explicit choice. */ }
    for (const control of controls) control.changed();
    return true;
  }

  function bind(input, onChanged) {
    let owner = null, disposed = false, bound = false;
    const sync = () => { if (!disposed) input.value = sessionIdentity.isCurrent(owner) ? value() : 'all'; };
    const change = () => {
      if (disposed || !input.isConnected || !sessionIdentity.isCurrent(owner)) return;
      if (!select(input.value, owner)) sync();
    };
    input.addEventListener('change', change);
    const binding = {
      changed() {
        if (disposed || !input.isConnected || !sessionIdentity.isCurrent(owner)) return;
        sync(); onChanged();
      },
      setStaff(staff) {
        if (disposed) return;
        if (bound && owner !== staff && !sessionIdentity.sameSession(owner, staff)) {
          // Retire the actual control as well as its actor stamp. References to
          // an old account's DOM cannot become controls for the replacement.
          input.removeEventListener('change', change);
          const replacement = input.cloneNode(true);
          input.replaceWith(replacement); input = replacement;
          input.addEventListener('change', change);
        }
        owner = sessionIdentity.isCurrent(staff) ? staff : null;
        bound = true;
        sync();
      },
      sync,
      dispose() {
        if (disposed) return;
        disposed = true; input.removeEventListener('change', change); controls.delete(binding);
      }
    };
    controls.add(binding);
    return binding;
  }

  return { value, select, bind };
}
