// Sort lookup choices by the label their owner actually renders. Callers keep
// prompts, historical selections and configured/workflow ordering separately.
export function byDisplayLabel(items, label = item => item.name, identity = item => item.id) {
  const compare = (left, right) => String(left ?? '').localeCompare(String(right ?? ''), undefined,
    { sensitivity: 'base', numeric: true });
  return [...(items || [])].sort((left, right) => {
    const order = compare(label(left), label(right));
    if (order) return order;
    const a = String(identity(left) ?? ''), b = String(identity(right) ?? '');
    if (/^\d+$/.test(a) && /^\d+$/.test(b)) {
      return a.length - b.length || a.localeCompare(b, 'en');
    }
    return compare(a, b) || a.localeCompare(b, 'en');
  });
}
